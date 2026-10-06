using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using A2A;
using Azure;
using Azure.AI.VoiceLive;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using SharedServices;

namespace VoiceOrchestratorAgent;

/// <summary>
/// Handles a single voice session, bridging a browser WebSocket to a Voice Live session.
/// Delegates conversation persistence to <see cref="VoiceConversationStore"/> and
/// telemetry emission to <see cref="VoiceSessionTraceEmitter"/>.
/// </summary>
public sealed class VoiceWebSocketHandler
{
    private readonly WebSocket _clientSocket;
    private readonly VoiceLiveClient _voiceLiveClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _instructions;
    private readonly Dictionary<string, AIAgent> _a2aAgents;
    private readonly ILogger<VoiceWebSocketHandler> _logger;
    private readonly SessionStorageAddress? _address;
    private readonly VoiceConversationStore _conversationStore;
    private readonly VoiceSessionTraceEmitter _traceEmitter;
    private readonly SemaphoreSlim _clientSendGate = new(1, 1);

    // Conversation tracking for post-hoc telemetry and persistence
    private readonly List<ConversationMessage> _messages = new();
    private readonly List<ToolExecution> _toolExecutions = new();
    private readonly List<ToolDefinitionInfo> _toolDefinitions = new();
    private DateTimeOffset _sessionStartTime;
    private DateTimeOffset _sessionEndTime;
    private string? _errorType;

    /// <summary>Creates one connection handler with an already captured identity/address.</summary>
    public VoiceWebSocketHandler(
        WebSocket clientSocket,
        VoiceLiveClient voiceLiveClient,
        string endpoint,
        string model,
        string voice,
        string instructions,
        Dictionary<string, AIAgent> a2aAgents,
        ILogger<VoiceWebSocketHandler> logger,
        SessionStorageAddress? address,
        VoiceConversationStore conversationStore)
    {
        _clientSocket = clientSocket;
        ArgumentNullException.ThrowIfNull(voiceLiveClient);
        _voiceLiveClient = voiceLiveClient;
        _endpoint = endpoint;
        _model = model;
        _voice = voice;
        _instructions = instructions;
        _a2aAgents = a2aAgents;
        _logger = logger;
        ArgumentNullException.ThrowIfNull(conversationStore);
        _address = address;
        _conversationStore = conversationStore;
        _traceEmitter = new VoiceSessionTraceEmitter(logger);
    }

    /// <summary>Loads, replays, bridges and shuts down one voice connection within bounded save/drain budgets.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Voice Live session with endpoint {Endpoint}, model {Model}", _endpoint, _model);
        _sessionStartTime = DateTimeOffset.UtcNow;
        VoiceConversationSession? workingCopy = null;
        var processingStopped = true;
        var conversationStarted = false;

        try
        {
            // A failed snapshot/history read must not be advertised as a successful empty resume.
            workingCopy = await _conversationStore.LoadAsync(_address, cancellationToken);
            await using var session = await _voiceLiveClient.StartSessionAsync(_model, cancellationToken);

            await ConfigureSessionAsync(session, _instructions, cancellationToken);
            await InjectConversationHistoryAsync(session, workingCopy, cancellationToken);
            await SendToClientAsync(new { type = "ready" }, cancellationToken);
            conversationStarted = true;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var clientToVoiceLive = ProcessClientMessagesAsync(session, cts.Token);
            var voiceLiveToClient = ProcessVoiceLiveEventsAsync(session, cts.Token);
            processingStopped = false;
            try
            {
                await Task.WhenAny(clientToVoiceLive, voiceLiveToClient);
            }
            finally
            {
                try
                {
                    await VoiceProcessingShutdown.CancelAndWaitAsync(cts,
                        [clientToVoiceLive, voiceLiveToClient], TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException)
                {
                    // A shutdown timeout must not hide the useful producer failure that triggered it.
                    foreach (var producer in new[] { clientToVoiceLive, voiceLiveToClient })
                    {
                        if (producer.Exception is { } failure)
                        {
                            _errorType ??= failure.InnerException?.GetType().FullName;
                            _logger.LogError(failure, "Voice producer failed before shutdown completed");
                        }
                    }
                    throw;
                }
                finally
                {
                    processingStopped = clientToVoiceLive.IsCompleted && voiceLiveToClient.IsCompleted;
                }
            }

            _logger.LogInformation("Voice Live session ended");
        }
        catch (OperationCanceledException ex) { await ReportSessionFailureAsync(ex); }
        catch (TimeoutException ex) { await ReportSessionFailureAsync(ex); }
        catch (InvalidOperationException ex) { await ReportSessionFailureAsync(ex); }
        catch (CosmosException ex) { await ReportSessionFailureAsync(ex); }
        catch (RequestFailedException ex) { await ReportSessionFailureAsync(ex); }
        catch (WebSocketException ex) { await ReportSessionFailureAsync(ex); }
        catch (JsonException ex) { await ReportSessionFailureAsync(ex); }
        catch (ArgumentException ex) { await ReportSessionFailureAsync(ex); }
        catch (HttpRequestException ex) { await ReportSessionFailureAsync(ex); }
        catch (A2AException ex) { await ReportSessionFailureAsync(ex); }
        catch (IOException ex) { await ReportSessionFailureAsync(ex); }
        finally
        {
            _sessionEndTime = DateTimeOffset.UtcNow;

            if (processingStopped)
            {
                _traceEmitter.Emit(_endpoint, _model, _instructions,
                    _messages, _toolExecutions, _toolDefinitions,
                    _sessionStartTime, _sessionEndTime, _errorType);
                if (conversationStarted && workingCopy?.Address is not null)
                    await PersistAsync(workingCopy);
            }
            else
            {
                _logger.LogError(VoiceErrors.Get("ShutdownFailed"));
                using var notification = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendToClientAsync(new { type = "persistence_error", message = VoiceErrors.Get("ShutdownFailed") },
                    notification.Token);
            }
        }
    }

    private async Task ReportSessionFailureAsync(Exception exception)
    {
        _errorType ??= exception.GetType().FullName;
        if (exception is OperationCanceledException)
            _logger.LogInformation(exception, "Voice session canceled");
        else
            _logger.LogError(exception, "Voice session failed");
        using var notification = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await SendToClientAsync(new { type = "error", message = VoiceErrors.Get("SessionFailed") }, notification.Token);
    }

    private async Task PersistAsync(VoiceConversationSession workingCopy)
    {
        // RequestAborted is usually canceled here; persistence gets its own bounded shutdown budget.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _conversationStore.SaveAsync(workingCopy, _messages, timeout.Token);
            _logger.LogInformation("Voice snapshot and {MessageCount} new messages persisted", _messages.Count);
            await SendToClientAsync(new { type = "persisted" }, timeout.Token);
        }
        catch (OperationCanceledException ex) { await ReportPersistenceFailureAsync(ex); }
        catch (InvalidOperationException ex) { await ReportPersistenceFailureAsync(ex); }
        catch (CosmosException ex) { await ReportPersistenceFailureAsync(ex); }
        catch (JsonException ex) { await ReportPersistenceFailureAsync(ex); }
        catch (ArgumentException ex) { await ReportPersistenceFailureAsync(ex); }
        catch (HttpRequestException ex) { await ReportPersistenceFailureAsync(ex); }
        catch (IOException ex) { await ReportPersistenceFailureAsync(ex); }
    }

    private async Task ReportPersistenceFailureAsync(Exception exception)
    {
        _logger.LogError(exception, "Voice persistence failed; no completed save is reported");
        using var notification = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await SendToClientAsync(new { type = "persistence_error", message = VoiceErrors.Get("SaveFailed") }, notification.Token);
    }

    private async Task ConfigureSessionAsync(VoiceLiveSession session, string instructions, CancellationToken cancellationToken)
    {
        // Convert A2A agents to Voice Live tool definitions (analogous to agent.AsAIFunction() in MAF)
        var functionTools = _a2aAgents.Values
            .Select(agent => agent.AsVoiceLiveTool())
            .ToList();

        // Add non-agent tools manually
        functionTools.Add(new VoiceLiveFunctionDefinition("get_weather")
        {
            Description = "Get the weather for a given location.",
            Parameters = BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    location = new
                    {
                        type = "string",
                        description = "The location to get the weather for"
                    }
                },
                required = new[] { "location" }
            })
        });

        // Collect tool definitions for telemetry
        foreach (var tool in functionTools)
        {
            _toolDefinitions.Add(new ToolDefinitionInfo(
                tool.Name,
                tool.Description ?? "",
                tool.Parameters?.ToString() ?? "{}"));
        }

        var options = new VoiceLiveSessionOptions
        {
            Model = _model,
            Instructions = instructions,
            Voice = new AzureStandardVoice(_voice),
            InputAudioFormat = InputAudioFormat.Pcm16,
            OutputAudioFormat = OutputAudioFormat.Pcm16,
            TurnDetection = new AzureSemanticVadTurnDetection
            {
                Threshold = 0.5f,
                PrefixPadding = TimeSpan.FromMilliseconds(300),
                SilenceDuration = TimeSpan.FromMilliseconds(500),
            },
            InputAudioEchoCancellation = new AudioEchoCancellation(),
            InputAudioNoiseReduction = new AudioNoiseReduction(AudioNoiseReductionType.AzureDeepNoiseSuppression),
            ToolChoice = ToolChoiceLiteral.Auto,
            InputAudioTranscription = new AudioInputTranscriptionOptions(AudioInputTranscriptionOptionsModel.Whisper1)
        };

        options.Modalities.Clear();
        options.Modalities.Add(InteractionModality.Text);
        options.Modalities.Add(InteractionModality.Audio);

        foreach (var tool in functionTools)
            options.Tools.Add(tool);

        await session.ConfigureSessionAsync(options, cancellationToken);
        _logger.LogInformation("Voice Live session configured with {ToolCount} tools", functionTools.Count);
    }

    /// <summary>
    /// Injects previous conversation history as native conversation items.
    /// </summary>
    private static async Task InjectConversationHistoryAsync(VoiceLiveSession session,
        VoiceConversationSession workingCopy, CancellationToken cancellationToken)
    {
        foreach (var message in workingCopy.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in VoiceMessageAdapter.ToVoiceLiveItems(message))
            {
                await session.AddItemAsync(item, cancellationToken);
            }
        }
    }

    private async Task ProcessClientMessagesAsync(VoiceLiveSession session, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 64];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _clientSocket.State == WebSocketState.Open)
            {
                var result = await _clientSocket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Client WebSocket closed");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (await HandleClientMessageAsync(session, message, cancellationToken)) break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Client audio processing canceled");
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogInformation("Client WebSocket closed prematurely");
        }
    }

    private async Task<bool> HandleClientMessageAsync(VoiceLiveSession session, string message, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type == "audio" && doc.RootElement.TryGetProperty("data", out var dataElement))
            {
                var base64Audio = dataElement.GetString();
                if (!string.IsNullOrEmpty(base64Audio))
                {
                    var audioBytes = Convert.FromBase64String(base64Audio);
                    await session.SendInputAudioAsync(audioBytes, cancellationToken);
                }
            }
            else if (type == "stop")
            {
                _logger.LogInformation("Client requested stop");
                return true;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid client message JSON");
        }
        catch (FormatException ex) { _logger.LogWarning(ex, "Invalid client audio encoding"); }
        return false;
    }

    private async Task ProcessVoiceLiveEventsAsync(VoiceLiveSession session, CancellationToken cancellationToken)
    {
        Dictionary<string, object>? pendingFunctionCall = null;

        try
        {
            await foreach (var update in session.GetUpdatesAsync(cancellationToken))
            {
                switch (update)
                {
                    case SessionUpdateSessionUpdated:
                        _logger.LogInformation("Voice Live session updated and ready");
                        await session.StartResponseAsync(cancellationToken);
                        await SendToClientAsync(new { type = "status", status = "ready" }, cancellationToken);
                        break;

                    case SessionUpdateInputAudioBufferSpeechStarted:
                        _logger.LogDebug("User started speaking (barge-in)");
                        await SendToClientAsync(new { type = "clear_audio" }, cancellationToken);
                        await SendToClientAsync(new { type = "status", status = "listening" }, cancellationToken);
                        break;

                    case SessionUpdateInputAudioBufferSpeechStopped:
                        _logger.LogDebug("User stopped speaking");
                        await SendToClientAsync(new { type = "status", status = "processing" }, cancellationToken);
                        break;

                    case SessionUpdateResponseCreated:
                        _logger.LogDebug("Response created");
                        break;

                    case SessionUpdateResponseAudioDelta audioDelta:
                        if (audioDelta.Delta is { Length: > 0 })
                        {
                            var base64Audio = Convert.ToBase64String(audioDelta.Delta.ToArray());
                            await SendToClientAsync(new { type = "audio", data = base64Audio }, cancellationToken);
                        }
                        break;

                    case SessionUpdateResponseAudioTranscriptDelta transcriptDelta:
                        if (!string.IsNullOrEmpty(transcriptDelta.Delta))
                        {
                            await SendToClientAsync(new
                            {
                                type = "transcript",
                                role = "assistant",
                                text = transcriptDelta.Delta,
                                final_ = false
                            }, cancellationToken);
                        }
                        break;

                    case SessionUpdateResponseAudioTranscriptDone transcriptDone:
                        if (!string.IsNullOrEmpty(transcriptDone.Transcript))
                        {
                            _messages.Add(new ConversationMessage(
                                DateTimeOffset.UtcNow, "assistant", "text", transcriptDone.Transcript));

                            await SendToClientAsync(new
                            {
                                type = "transcript",
                                role = "assistant",
                                text = transcriptDone.Transcript,
                                final_ = true
                            }, cancellationToken);
                        }
                        break;

                    case SessionUpdateConversationItemInputAudioTranscriptionCompleted inputTranscript:
                        if (!string.IsNullOrEmpty(inputTranscript.Transcript))
                        {
                            _messages.Add(new ConversationMessage(
                                DateTimeOffset.UtcNow, "user", "text", inputTranscript.Transcript));

                            await SendToClientAsync(new
                            {
                                type = "transcript",
                                role = "user",
                                text = inputTranscript.Transcript,
                                final_ = true
                            }, cancellationToken);
                        }
                        break;

                    case SessionUpdateResponseFunctionCallArgumentsDone functionCallFinished:
                        pendingFunctionCall = new Dictionary<string, object>
                        {
                            ["name"] = functionCallFinished.Name,
                            ["call_id"] = functionCallFinished.CallId,
                            ["item_id"] = functionCallFinished.ItemId,
                            ["arguments"] = functionCallFinished.Arguments
                        };

                        _messages.Add(new ConversationMessage(
                            DateTimeOffset.UtcNow, "assistant", "tool_call",
                            Content: null,
                            ToolCallId: functionCallFinished.CallId,
                            ToolName: functionCallFinished.Name,
                            ToolArguments: functionCallFinished.Arguments));

                        _logger.LogInformation("Function call: {FunctionName}", functionCallFinished.Name);
                        await SendToClientAsync(new
                        {
                            type = "status",
                            status = "function_calling",
                            function_name = functionCallFinished.Name
                        }, cancellationToken);
                        break;

                    case SessionUpdateResponseDone:
                        _logger.LogDebug("Response done");
                        if (pendingFunctionCall != null)
                        {
                            await ExecuteFunctionCallAsync(session, pendingFunctionCall, cancellationToken);
                            pendingFunctionCall = null;
                        }
                        await SendToClientAsync(new { type = "status", status = "ready" }, cancellationToken);
                        break;

                    case SessionUpdateError errorUpdate:
                        _errorType = "voice_live_error";
                        _logger.LogError("Voice Live error: {Error}", errorUpdate.Error.Message);
                        await SendToClientAsync(new { type = "error", message = errorUpdate.Error.Message }, cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Voice model processing canceled");
        }
    }

    private async Task ExecuteFunctionCallAsync(VoiceLiveSession session, Dictionary<string, object> callInfo, CancellationToken cancellationToken)
    {
        var functionName = (string)callInfo["name"];
        var callId = (string)callInfo["call_id"];
        var arguments = (string)callInfo["arguments"];

        _logger.LogInformation("Executing function {FunctionName} with args: {Args}", functionName, arguments);

        var toolExecution = new ToolExecution(functionName, callId, arguments, StartTime: DateTimeOffset.UtcNow);

        string resultJson;
        try
        {
            if (functionName == "get_weather")
            {
                var args = JsonDocument.Parse(arguments).RootElement;
                var location = args.TryGetProperty("location", out var loc) ? loc.GetString() ?? "Unknown" : "Unknown";
                resultJson = JsonSerializer.Serialize(new
                {
                    location,
                    weather = $"The weather in {location} is cloudy with a high of 15°C."
                });
            }
            else if (_a2aAgents.TryGetValue(functionName, out var agent))
            {
                var args = JsonDocument.Parse(arguments).RootElement;
                var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";

                var messages = new List<ChatMessage> { new(ChatRole.User, query) };
                var agentResponse = await agent.RunAsync(messages, cancellationToken: cancellationToken);

                var responseText = agentResponse.Text;
                if (string.IsNullOrEmpty(responseText) && agentResponse.Messages is { Count: > 0 })
                {
                    responseText = string.Join("\n", agentResponse.Messages
                        .Where(m => m.Role == ChatRole.Assistant)
                        .SelectMany(m => m.Contents.OfType<TextContent>())
                        .Select(tc => tc.Text));
                }

                resultJson = responseText ?? "No response from agent";
            }
            else
            {
                _logger.LogWarning("Unknown function: {FunctionName}", functionName);
                resultJson = JsonSerializer.Serialize(new { error = $"Unknown function: {functionName}" });
            }

        }
        catch (OperationCanceledException) { throw; }
        catch (A2AException ex)
        {
            resultJson = await RecordFailureAsync(ex);
        }
        catch (HttpRequestException ex)
        {
            resultJson = await RecordFailureAsync(ex);
        }
        catch (RequestFailedException ex)
        {
            resultJson = await RecordFailureAsync(ex);
        }
        catch (JsonException ex)
        {
            resultJson = await RecordFailureAsync(ex);
        }
        catch (TimeoutException ex)
        {
            resultJson = await RecordFailureAsync(ex);
        }

        // Recording is outside the transport operations: an AddItem/StartResponse failure must
        // not record a second result for the same call or replace its useful tool failure.
        _messages.Add(new ConversationMessage(
            DateTimeOffset.UtcNow, "tool", "tool_call_response",
            Content: null, ToolCallId: callId, ToolName: functionName, ToolResult: resultJson));
        toolExecution = toolExecution with { Result = resultJson, EndTime = DateTimeOffset.UtcNow };
        _toolExecutions.Add(toolExecution);

        await session.AddItemAsync(new FunctionCallOutputItem(callId, resultJson), cancellationToken);
        _logger.LogInformation("Function {FunctionName} result sent", functionName);
        await session.StartResponseAsync(cancellationToken);

        async Task<string> RecordFailureAsync(Exception exception)
        {
            _logger.LogError(exception, "Error executing function {FunctionName}", functionName);
            toolExecution = toolExecution with { ErrorType = exception.GetType().FullName };
            _errorType ??= exception.GetType().FullName;
            await SendToClientAsync(new
            {
                type = "error",
                message = VoiceErrors.Get("ToolFailed"),
                function_name = functionName
            }, cancellationToken);
            return JsonSerializer.Serialize(new { error = VoiceErrors.Get("ToolFailed") });
        }
    }

    private async Task SendToClientAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);

        try
        {
            // A timeout/error notification can race a producer that is still shutting down.
            await _clientSendGate.WaitAsync(cancellationToken);
            try
            {
                if (_clientSocket.State != WebSocketState.Open)
                {
                    _logger.LogDebug("Client notification unavailable because the socket is {SocketState}", _clientSocket.State);
                    return;
                }
                await _clientSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _clientSendGate.Release();
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "Error sending to client WebSocket");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Client notification canceled");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Client socket is no longer usable for notifications");
        }
    }
}
