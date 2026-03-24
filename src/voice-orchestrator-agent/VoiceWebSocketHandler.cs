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
/// </summary>
public sealed class VoiceWebSocketHandler
{
    private readonly WebSocket _clientSocket;
    private readonly TokenCredential _credential;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _instructions;
    private readonly Dictionary<string, AIAgent> _a2aAgents;
    private readonly ILogger<VoiceWebSocketHandler> _logger;

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

        var client = new VoiceLiveClient(new Uri(_endpoint), _credential);
        await using var session = await client.StartSessionAsync(_model, cancellationToken);

        await ConfigureSessionAsync(session);
        await SendToClient(new { type = "ready" }, cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var clientToVoiceLive = Task.Run(() => ProcessClientMessages(session, cts.Token), cts.Token);
        var voiceLiveToClient = Task.Run(() => ProcessVoiceLiveEvents(session, cts.Token), cts.Token);

        // Wait for either direction to finish (client disconnect or error)
        await Task.WhenAny(clientToVoiceLive, voiceLiveToClient);
        await cts.CancelAsync();

        _logger.LogInformation("Voice Live session ended");
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
                        // Send initial greeting
                        await session.StartResponseAsync(cancellationToken);
                        await SendToClient(new { type = "status", status = "ready" }, cancellationToken);
                        break;

                    case SessionUpdateInputAudioBufferSpeechStarted:
                        _logger.LogDebug("User started speaking (barge-in)");
                        // Tell the frontend to stop playing any buffered agent audio
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
                        _logger.LogError("Voice Live error: {Error}", errorUpdate.Error.Message);
                        await SendToClient(new { type = "error", message = errorUpdate.Error.Message }, cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
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

                // Invoke the A2A agent using MAF's RunAsync
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.User, query)
                };
                var agentResponse = await agent.RunAsync(messages);
                resultJson = JsonSerializer.Serialize(new { response = agentResponse.Text });
            }
            else
            {
                _logger.LogWarning("Unknown function: {FunctionName}", functionName);
                resultJson = JsonSerializer.Serialize(new { error = $"Unknown function: {functionName}" });
            }

            await session.AddItemAsync(new FunctionCallOutputItem(callId, resultJson), cancellationToken);
            _logger.LogInformation("Function {FunctionName} result sent", functionName);

            await session.StartResponseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing function {FunctionName}", functionName);
            var errorResult = JsonSerializer.Serialize(new { error = ex.Message });
            await session.AddItemAsync(new FunctionCallOutputItem(callId, errorResult), cancellationToken);
            await session.StartResponseAsync(cancellationToken);
        }
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
}
