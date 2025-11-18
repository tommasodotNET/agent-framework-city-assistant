namespace OrchestratorAgent.Models;

public class AIChatCompletionDelta
{
    public AIChatMessageDelta Delta { get; set; }
    public string? SessionState { get; set; }

    public AIChatCompletionDelta(AIChatMessageDelta delta)
    {
        Delta = delta;
    }
}

public class AIChatMessageDelta
{
    public string? Role { get; set; }
    public string? Content { get; set; }
}
