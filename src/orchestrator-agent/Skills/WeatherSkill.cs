using System.ComponentModel;
using Microsoft.Agents.AI;

namespace OrchestratorAgent.Skills;

public sealed class WeatherSkill : AgentClassSkill<WeatherSkill>
{
    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        "weather-lookup",
        "Get the current mock weather for an Agentburg location.");

    protected override string Instructions => """
        Use this skill whenever the user asks about the weather or current conditions in Agentburg.
        Run get-weather with the requested location and relay the result clearly.
        """;

    [AgentSkillScript("get-weather")]
    [Description("Gets the current weather for a given location.")]
    private static string GetWeather(
        [Description("The location to get the weather for.")] string location)
        => $"The weather in {location} is cloudy with a high of 15°C.";
}
