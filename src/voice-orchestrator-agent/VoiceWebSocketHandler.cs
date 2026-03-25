using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Azure.AI.VoiceLive;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace VoiceOrchestratorAgent;

/// <summary>
/// Handles a single voice session, bridging a browser WebSocket to a Voice Live session.
/// Collects conversation events during the session and emits gen_ai OTel traces post-hoc.
/// </summary>
public sealed class VoiceWebSocketHandler
{
    public const string ActivitySourceName = "VoiceOrchestratorAgent.GenAI";
    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    private readonly WebSocket _clientSocket;
    private readonly TokenCredential _credential;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _instructions;
    private readonly Dictionary<string, AIAgent> _a2aAgents;
    private readonly ILogger<VoiceWebSocketHandler> _logger;

    // Conversation tracking for post-hoc telemetry
    private readonly List<ConversationMessage> _messages = new();
    private readonly List<ToolExecution> _toolExecutions = new();
    private readonly List<ToolDefinitionInfo> _toolDefinitions = new();
    private DateTimeOffset _sessionStartTime;
    private DateTimeOffset _sessionEndTime;
    private string? _errorType;

    public VoiceWebSocketHandler(
        WebSocket clientSocket,
        TokenCredential credential,
        string endpoint,
        string model,
        string voice,
        string instructions,
        Dictionary<string, AIAgent> a2aAgents,
        ILogger<VoiceWebSocketHandler> logger)
    {
        _clientSocket = clientSocket;
        _credential = credential;
        _endpoint = endpoint;
        _model = model;
        _voice = voice;
        _instructions = instructions;
        _a2aAgents = a2aAgents;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Voice Live session with endpoint {Endpoint}, model {Model}", _endpoint, _model);
        _sessionStartTime = DateTimeOffset.UtcNow;

        try
        {
            var client = new VoiceLiveClient(new Uri(_endpoint), _credential);
            await using var session = await client.StartSessionAsync(_model, cancellationToken);

            await ConfigureSessionAsync(session);
            await SendToClient(new { type = "ready" }, cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var clientToVoiceLive = Task.Run(() => ProcessClientMessages(session, cts.Token), cts.Token);
            var voiceLiveToClient = Task.Run(() => ProcessVoiceLiveEvents(session, cts.Token), cts.Token);

            await Task.WhenAny(clientToVoiceLive, voiceLiveToClient);
            await cts.CancelAsync();

            _logger.LogInformation("Voice Live session ended");
        }
        catch (Exception ex)
        {
            _errorType = ex.GetType().FullName;
            _logger.LogError(ex, "Voice Live session failed");
        }
        finally
        {
            _sessionEndTime = DateTimeOffset.UtcNow;
            EmitTraces();
        }
    }

    private async Task ConfigureSessionAsync(VoiceLiveSession session)
    {
        var functionTools = new List<VoiceLiveFunctionDefinition>
        {
            new("restaurant_agent")
            {
                Description = "Search for restaurants in Agentburg by category, keywords, or location. " +
                              "Use this when the user asks about restaurants, food, dining, or places to eat.",
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            description = "The user's restaurant-related question or search query"
                        }
                    },
                    required = new[] { "query" }
                })
            },
            new("activities_agent")
            {
                Description = "Discover museums, theaters, cultural events, attractions, and activities in Agentburg. " +
                              "Use this when the user asks about things to do, sightseeing, entertainment, or culture.",
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            description = "The user's activities-related question or search query"
                        }
                    },
                    required = new[] { "query" }
                })
            },
            new("accommodation_agent")
            {
                Description = "Find hotels, B&Bs, hostels, and accommodations in Agentburg. " +
                              "Use this when the user asks about places to stay, lodging, or accommodation.",
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            description = "The user's accommodation-related question or search query"
                        }
                    },
                    required = new[] { "query" }
                })
            },
            new("get_weather")
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
            }
        };

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
            Instructions = _instructions,
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

        await session.ConfigureSessionAsync(options);
        _logger.LogInformation("Voice Live session configured with {ToolCount} tools", functionTools.Count);
    }

    /// <summary>
    /// Reads messages from the browser WebSocket and forwards audio to Voice Live.
    /// </summary>
    private async Task ProcessClientMessages(VoiceLiveSession session, CancellationToken cancellationToken)
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
                    await HandleClientMessage(session, message, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogInformation("Client WebSocket closed prematurely");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing client messages");
        }
    }

    private async Task HandleClientMessage(VoiceLiveSession session, string message, CancellationToken cancellationToken)
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
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client message");
        }
    }

    /// <summary>
    /// Processes events from Voice Live and forwards audio/transcripts to the browser.
    /// </summary>
    private async Task ProcessVoiceLiveEvents(VoiceLiveSession session, CancellationToken cancellationToken)
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
                        await SendToClient(new { type = "status", status = "ready" }, cancellationToken);
                        break;

                    case SessionUpdateInputAudioBufferSpeechStarted:
                        _logger.LogDebug("User started speaking (barge-in)");
                        await SendToClient(new { type = "clear_audio" }, cancellationToken);
                        await SendToClient(new { type = "status", status = "listening" }, cancellationToken);
                        break;

                    case SessionUpdateInputAudioBufferSpeechStopped:
                        _logger.LogDebug("User stopped speaking");
                        await SendToClient(new { type = "status", status = "processing" }, cancellationToken);
                        break;

                    case SessionUpdateResponseCreated:
                        _logger.LogDebug("Response created");
                        break;

                    case SessionUpdateResponseAudioDelta audioDelta:
                        if (audioDelta.Delta is { Length: > 0 })
                        {
                            var base64Audio = Convert.ToBase64String(audioDelta.Delta.ToArray());
                            await SendToClient(new { type = "audio", data = base64Audio }, cancellationToken);
                        }
                        break;

                    case SessionUpdateResponseAudioTranscriptDelta transcriptDelta:
                        if (!string.IsNullOrEmpty(transcriptDelta.Delta))
                        {
                            await SendToClient(new
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
                            // Record assistant message for telemetry
                            _messages.Add(new ConversationMessage(
                                DateTimeOffset.UtcNow, "assistant", "text", transcriptDone.Transcript));

                            await SendToClient(new
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
                            // Record user message for telemetry
                            _messages.Add(new ConversationMessage(
                                DateTimeOffset.UtcNow, "user", "text", inputTranscript.Transcript));

                            await SendToClient(new
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

                        // Record tool_call message for telemetry
                        _messages.Add(new ConversationMessage(
                            DateTimeOffset.UtcNow, "assistant", "tool_call",
                            Content: null,
                            ToolCallId: functionCallFinished.CallId,
                            ToolName: functionCallFinished.Name,
                            ToolArguments: functionCallFinished.Arguments));

                        _logger.LogInformation("Function call: {FunctionName}", functionCallFinished.Name);
                        await SendToClient(new
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
                            await ExecuteFunctionCall(session, pendingFunctionCall, cancellationToken);
                            pendingFunctionCall = null;
                        }
                        await SendToClient(new { type = "status", status = "ready" }, cancellationToken);
                        break;

                    case SessionUpdateError errorUpdate:
                        _errorType = "voice_live_error";
                        _logger.LogError("Voice Live error: {Error}", errorUpdate.Error.Message);
                        await SendToClient(new { type = "error", message = errorUpdate.Error.Message }, cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _errorType ??= ex.GetType().FullName;
            _logger.LogError(ex, "Error processing Voice Live events");
            await SendToClient(new { type = "error", message = ex.Message }, cancellationToken);
        }
    }

    private async Task ExecuteFunctionCall(VoiceLiveSession session, Dictionary<string, object> callInfo, CancellationToken cancellationToken)
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

                var messages = new List<ChatMessage>
                {
                    new(ChatRole.User, query)
                };
                var agentResponse = await agent.RunAsync(messages);

                // Extract the full response text; fall back to joining all assistant message contents
                var responseText = agentResponse.Text;
                if (string.IsNullOrEmpty(responseText) && agentResponse.Messages is { Count: > 0 })
                {
                    responseText = string.Join("\n", agentResponse.Messages
                        .Where(m => m.Role == ChatRole.Assistant)
                        .SelectMany(m => m.Contents.OfType<TextContent>())
                        .Select(tc => tc.Text));
                }

                resultJson = responseText ?? "No response from agent";
                _logger.LogDebug("A2A agent {FunctionName} returned {Length} chars", functionName, resultJson.Length);
            }
            else
            {
                _logger.LogWarning("Unknown function: {FunctionName}", functionName);
                resultJson = JsonSerializer.Serialize(new { error = $"Unknown function: {functionName}" });
            }

            // Record tool response message for telemetry
            _messages.Add(new ConversationMessage(
                DateTimeOffset.UtcNow, "tool", "tool_call_response",
                Content: null,
                ToolCallId: callId,
                ToolName: functionName,
                ToolResult: resultJson));

            toolExecution = toolExecution with { Result = resultJson, EndTime = DateTimeOffset.UtcNow };
            _toolExecutions.Add(toolExecution);

            await session.AddItemAsync(new FunctionCallOutputItem(callId, resultJson), cancellationToken);
            _logger.LogInformation("Function {FunctionName} result sent", functionName);

            await session.StartResponseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing function {FunctionName}", functionName);
            var errorResult = JsonSerializer.Serialize(new { error = ex.Message });

            _messages.Add(new ConversationMessage(
                DateTimeOffset.UtcNow, "tool", "tool_call_response",
                Content: null,
                ToolCallId: callId,
                ToolName: functionName,
                ToolResult: errorResult));

            toolExecution = toolExecution with { Result = errorResult, EndTime = DateTimeOffset.UtcNow, ErrorType = ex.GetType().FullName };
            _toolExecutions.Add(toolExecution);

            await session.AddItemAsync(new FunctionCallOutputItem(callId, errorResult), cancellationToken);
            await session.StartResponseAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Emits gen_ai OTel traces post-hoc from the collected conversation data.
    /// </summary>
    private void EmitTraces()
    {
        if (_messages.Count == 0)
        {
            _logger.LogDebug("No conversation messages to emit traces for");
            return;
        }

        var serverHost = new Uri(_endpoint).Host;
        var serverPort = new Uri(_endpoint).Port;

        // Root span: chat inference
        var rootActivity = s_activitySource.StartActivity(
            name: $"chat {_model}",
            kind: ActivityKind.Client,
            tags: new ActivityTagsCollection
            {
                { "gen_ai.operation.name", "chat" },
                { "gen_ai.provider.name", "azure.ai.openai" },
                { "gen_ai.request.model", _model },
            },
            startTime: _sessionStartTime);

        if (rootActivity is null)
        {
            _logger.LogDebug("ActivitySource not sampled, skipping trace emission");
            return;
        }

        rootActivity.SetTag("gen_ai.output.type", "speech");
        rootActivity.SetTag("gen_ai.response.model", _model);
        rootActivity.SetTag("server.address", serverHost);
        rootActivity.SetTag("server.port", serverPort == -1 ? 443 : serverPort);

        // System instructions
        rootActivity.SetTag("gen_ai.system_instructions",
            JsonSerializer.Serialize(new[] { new { type = "text", content = _instructions } }));

        // Tool definitions
        rootActivity.SetTag("gen_ai.tool.definitions",
            JsonSerializer.Serialize(_toolDefinitions.Select(t => new
            {
                type = "function",
                name = t.Name,
                description = t.Description,
                parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersJson)
            })));

        // Input messages (user utterances, tool calls by assistant, tool responses)
        var toolResponseMessages = _messages.Where(m => m.Role == "tool").ToList();
        foreach (var trm in toolResponseMessages)
        {
            _logger.LogInformation(
                "Tool response message: callId={CallId}, toolName={ToolName}, result_length={Len}, result_preview={Preview}",
                trm.ToolCallId, trm.ToolName, trm.ToolResult?.Length ?? -1,
                trm.ToolResult?[..Math.Min(100, trm.ToolResult?.Length ?? 0)]);
        }

        var inputMessages = _messages
            .Where(m => m.Role == "user" || (m.Role == "assistant" && m.Type == "tool_call") || m.Role == "tool")
            .Select(BuildMessageObject)
            .ToList();

        if (inputMessages.Count > 0)
            rootActivity.SetTag("gen_ai.input.messages", JsonSerializer.Serialize(inputMessages));

        // Output messages (assistant text responses)
        var outputMessages = _messages
            .Where(m => m.Role == "assistant" && m.Type == "text")
            .Select(m => new
            {
                role = "assistant",
                parts = new[] { new { type = "text", content = m.Content } },
                finish_reason = "stop"
            })
            .ToList();

        if (outputMessages.Count > 0)
            rootActivity.SetTag("gen_ai.output.messages", JsonSerializer.Serialize(outputMessages));

        rootActivity.SetTag("gen_ai.response.finish_reasons", JsonSerializer.Serialize(new[] { "stop" }));

        if (_errorType is not null)
        {
            rootActivity.SetTag("error.type", _errorType);
            rootActivity.SetStatus(ActivityStatusCode.Error);
        }

        // Child spans: execute_tool for each tool invocation
        foreach (var tool in _toolExecutions)
        {
            _logger.LogInformation(
                "Emitting execute_tool span: name={Name}, callId={CallId}, args={Args}, result_length={ResultLen}",
                tool.Name, tool.CallId,
                tool.Arguments?[..Math.Min(100, tool.Arguments?.Length ?? 0)],
                tool.Result?.Length ?? -1);

            using var toolActivity = s_activitySource.StartActivity(
                $"execute_tool {tool.Name}",
                ActivityKind.Internal,
                rootActivity.Context,
                tags: null,
                links: null,
                startTime: tool.StartTime);

            if (toolActivity is null) continue;

            toolActivity.SetTag("gen_ai.operation.name", "execute_tool");
            toolActivity.SetTag("gen_ai.tool.name", tool.Name);
            toolActivity.SetTag("gen_ai.tool.call.id", tool.CallId);
            toolActivity.SetTag("gen_ai.tool.type", "function");
            toolActivity.SetTag("gen_ai.tool.call.arguments", tool.Arguments);
            toolActivity.SetTag("gen_ai.tool.call.result", tool.Result ?? "");

            if (tool.ErrorType is not null)
            {
                toolActivity.SetTag("error.type", tool.ErrorType);
                toolActivity.SetStatus(ActivityStatusCode.Error);
            }

            if (tool.EndTime.HasValue)
                toolActivity.SetEndTime(tool.EndTime.Value.UtcDateTime);
        }

        rootActivity.SetEndTime(_sessionEndTime.UtcDateTime);
        rootActivity.Dispose();

        _logger.LogInformation(
            "Emitted gen_ai traces: 1 chat span + {ToolCount} tool spans, {MessageCount} messages",
            _toolExecutions.Count, _messages.Count);
    }

    private static object BuildMessageObject(ConversationMessage msg) => msg.Type switch
    {
        "text" => new
        {
            role = msg.Role,
            parts = new object[] { new { type = "text", content = msg.Content } }
        },
        "tool_call" => new
        {
            role = msg.Role,
            parts = new object[]
            {
                new
                {
                    type = "tool_call",
                    id = msg.ToolCallId,
                    name = msg.ToolName,
                    arguments = TryParseJson(msg.ToolArguments)
                }
            }
        },
        "tool_call_response" => new
        {
            role = msg.Role,
            parts = new object[]
            {
                new
                {
                    type = "tool_call_response",
                    id = msg.ToolCallId,
                    response = TryParseJson(msg.ToolResult)
                }
            }
        },
        _ => new { role = msg.Role, parts = Array.Empty<object>() } as object
    };

    private static object TryParseJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new { };
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return json; }
    }

    private async Task SendToClient(object message, CancellationToken cancellationToken)
    {
        if (_clientSocket.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);

        try
        {
            await _clientSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending to client WebSocket");
        }
    }

    // Telemetry data models
    private record ConversationMessage(
        DateTimeOffset Timestamp,
        string Role,
        string Type,
        string? Content = null,
        string? ToolCallId = null,
        string? ToolName = null,
        string? ToolArguments = null,
        string? ToolResult = null);

    private record ToolExecution(
        string Name,
        string CallId,
        string Arguments,
        DateTimeOffset StartTime,
        string? Result = null,
        DateTimeOffset? EndTime = null,
        string? ErrorType = null);

    private record ToolDefinitionInfo(string Name, string Description, string ParametersJson);
}
