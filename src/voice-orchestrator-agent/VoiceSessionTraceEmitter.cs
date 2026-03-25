using System.Diagnostics;
using System.Text.Json;

namespace VoiceOrchestratorAgent;

/// <summary>
/// Emits OpenTelemetry gen_ai traces post-hoc from collected conversation data.
/// Follows the OTel gen_ai semantic conventions for inference and tool execution spans.
/// </summary>
public sealed class VoiceSessionTraceEmitter
{
    public const string ActivitySourceName = "VoiceOrchestratorAgent.GenAI";
    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    private readonly ILogger _logger;

    public VoiceSessionTraceEmitter(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Emits a root "chat" span with child "execute_tool" spans from the collected session data.
    /// </summary>
    public void Emit(
        string endpoint,
        string model,
        string instructions,
        IReadOnlyList<ConversationMessage> messages,
        IReadOnlyList<ToolExecution> toolExecutions,
        IReadOnlyList<ToolDefinitionInfo> toolDefinitions,
        DateTimeOffset sessionStart,
        DateTimeOffset sessionEnd,
        string? errorType)
    {
        if (messages.Count == 0)
        {
            _logger.LogDebug("No conversation messages to emit traces for");
            return;
        }

        var serverHost = new Uri(endpoint).Host;
        var serverPort = new Uri(endpoint).Port;

        var rootActivity = s_activitySource.StartActivity(
            name: $"chat {model}",
            kind: ActivityKind.Client,
            tags: new ActivityTagsCollection
            {
                { "gen_ai.operation.name", "chat" },
                { "gen_ai.provider.name", "azure.ai.openai" },
                { "gen_ai.request.model", model },
            },
            startTime: sessionStart);

        if (rootActivity is null)
        {
            _logger.LogDebug("ActivitySource not sampled, skipping trace emission");
            return;
        }

        rootActivity.SetTag("gen_ai.output.type", "speech");
        rootActivity.SetTag("gen_ai.response.model", model);
        rootActivity.SetTag("server.address", serverHost);
        rootActivity.SetTag("server.port", serverPort == -1 ? 443 : serverPort);

        rootActivity.SetTag("gen_ai.system_instructions",
            JsonSerializer.Serialize(new[] { new { type = "text", content = instructions } }));

        rootActivity.SetTag("gen_ai.tool.definitions",
            JsonSerializer.Serialize(toolDefinitions.Select(t => new
            {
                type = "function",
                name = t.Name,
                description = t.Description,
                parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersJson)
            })));

        var inputMessages = messages
            .Where(m => m.Role == "user" || (m.Role == "assistant" && m.Type == "tool_call") || m.Role == "tool")
            .Select(BuildMessageObject)
            .ToList();

        if (inputMessages.Count > 0)
            rootActivity.SetTag("gen_ai.input.messages", JsonSerializer.Serialize(inputMessages));

        var outputMessages = messages
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

        if (errorType is not null)
        {
            rootActivity.SetTag("error.type", errorType);
            rootActivity.SetStatus(ActivityStatusCode.Error);
        }

        foreach (var tool in toolExecutions)
        {
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

        rootActivity.SetEndTime(sessionEnd.UtcDateTime);
        rootActivity.Dispose();

        _logger.LogInformation(
            "Emitted gen_ai traces: 1 chat span + {ToolCount} tool spans, {MessageCount} messages",
            toolExecutions.Count, messages.Count);
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
}
