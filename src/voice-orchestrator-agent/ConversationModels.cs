namespace VoiceOrchestratorAgent;

/// <summary>
/// Data models used for conversation tracking, telemetry, and persistence.
/// </summary>
public record ConversationMessage(
    DateTimeOffset Timestamp,
    string Role,
    string Type,
    string? Content = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ToolArguments = null,
    string? ToolResult = null);

public record ToolExecution(
    string Name,
    string CallId,
    string Arguments,
    DateTimeOffset StartTime,
    string? Result = null,
    DateTimeOffset? EndTime = null,
    string? ErrorType = null);

public record ToolDefinitionInfo(string Name, string Description, string ParametersJson);
