using Azure.AI.VoiceLive;
using Microsoft.Agents.AI;

namespace VoiceOrchestratorAgent;

/// <summary>
/// Extension methods to convert MAF A2A agents into Voice Live function tool definitions.
/// Analogous to <c>agent.AsAIFunction()</c> in Microsoft Agent Framework, but targeting
/// the Voice Live SDK's <see cref="VoiceLiveFunctionDefinition"/> format.
/// </summary>
public static class VoiceLiveAgentExtensions
{
    private static readonly BinaryData s_queryParameters = BinaryData.FromObjectAsJson(new
    {
        type = "object",
        properties = new
        {
            query = new
            {
                type = "string",
                description = "Input query to invoke the agent"
            }
        },
        required = new[] { "query" }
    });

    /// <summary>
    /// Converts an A2A <see cref="AIAgent"/> into a <see cref="VoiceLiveFunctionDefinition"/>
    /// that can be registered as a tool on a Voice Live session.
    /// The agent's name and description are taken from its A2A agent card.
    /// </summary>
    public static VoiceLiveFunctionDefinition AsVoiceLiveTool(this AIAgent agent)
    {
        return new VoiceLiveFunctionDefinition(agent.Name)
        {
            Description = agent.Description ?? $"Invoke the {agent.Name} agent",
            Parameters = s_queryParameters
        };
    }
}
