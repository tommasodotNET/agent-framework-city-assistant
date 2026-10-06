using System.Text.Json;
using Azure.AI.VoiceLive;
using Microsoft.Extensions.AI;

namespace VoiceOrchestratorAgent;

/// <summary>Lossless text/tool conversion between voice tracking and common structured history.</summary>
public static class VoiceMessageAdapter
{
    /// <summary>Preserves Unicode text, function identity, typed arguments and tool output.</summary>
    public static ChatMessage ToChatMessage(ConversationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var result = new ChatMessage(new ChatRole(message.Role), new List<AIContent>());
        result.AdditionalProperties = new() { ["voiceTimestamp"] = message.Timestamp };
        switch (message.Type)
        {
            case "text":
                result.Contents.Add(new TextContent(message.Content ?? ""));
                break;
            case "tool_call":
                ArgumentException.ThrowIfNullOrWhiteSpace(message.ToolCallId);
                ArgumentException.ThrowIfNullOrWhiteSpace(message.ToolName);
                var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(message.ToolArguments ?? "{}")
                    ?? throw new JsonException(VoiceErrors.Get("InvalidArguments"));
                result.Contents.Add(new FunctionCallContent(message.ToolCallId, message.ToolName, arguments));
                break;
            case "tool_call_response":
                ArgumentException.ThrowIfNullOrWhiteSpace(message.ToolCallId);
                result.Contents.Add(new FunctionResultContent(message.ToolCallId, message.ToolResult ?? ""));
                result.AdditionalProperties["voiceToolName"] = message.ToolName;
                break;
            default:
                throw new ArgumentException(VoiceErrors.Get("UnsupportedMessage"), nameof(message));
        }
        return result;
    }

    /// <summary>Replays history using native user/assistant/function items, never a system prompt.</summary>
    public static IEnumerable<ConversationRequestItem> ToVoiceLiveItems(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (var content in message.Contents)
        {
            yield return content switch
            {
                TextContent text when message.Role == ChatRole.User => new UserMessageItem(text.Text),
                TextContent text when message.Role == ChatRole.Assistant => new AssistantMessageItem(text.Text),
                FunctionCallContent call => new FunctionCallItem(call.Name, call.CallId,
                    JsonSerializer.Serialize(call.Arguments)),
                FunctionResultContent result => new FunctionCallOutputItem(result.CallId, ResultText(result.Result)),
                _ => throw new InvalidOperationException(VoiceErrors.Get("UnsupportedMessage"))
            };
        }
    }

    private static string ResultText(object? result) => result switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? "",
        JsonElement json => json.GetRawText(),
        null => "",
        _ => JsonSerializer.Serialize(result)
    };
}
