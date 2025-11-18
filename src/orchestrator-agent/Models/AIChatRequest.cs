namespace OrchestratorAgent.Models;

public class AIChatRequest
{
    public List<AIChatMessage> Messages { get; set; } = new();
    public string? SessionState { get; set; }
}

public class AIChatMessage
{
    public required string Role { get; set; }
    public required string Content { get; set; }
}
