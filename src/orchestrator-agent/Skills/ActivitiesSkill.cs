using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using OrchestratorAgent.Services;

namespace OrchestratorAgent.Skills;

public sealed class ActivitiesSkill : AgentClassSkill<ActivitiesSkill>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly ActivitiesService _activitiesService;
    private readonly ILogger<ActivitiesSkill> _logger;

    public ActivitiesSkill(ActivitiesService activitiesService, ILogger<ActivitiesSkill> logger)
    {
        _activitiesService = activitiesService;
        _logger = logger;
    }

    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        "activities-search",
        "Find Agentburg museums, theaters, cultural events, and attractions by category, keywords, or proximity to a landmark.");

    protected override string Instructions => """
        Use this skill whenever the user asks about activities, things to do, museums, theaters, cultural events, or attractions in Agentburg.

        Supported categories are museums, theaters, cultural_events, and attractions.

        For a location-based request:
        1. Run geocode-location with the English landmark or neighborhood name.
        2. Extract latitude and longitude from its JSON response.
        3. Run search-activities with those coordinates and any requested category or keywords.

        For other requests, run get-all-activities, get-activities-by-category, or search-activities.
        Return friendly, detailed recommendations including hours, dates, pricing, restrictions, accessibility, ratings, and notable details.
        """;

    protected override JsonSerializerOptions? SerializerOptions => JsonOptions;

    [AgentSkillResource("agentburg-locations")]
    [Description("Known Agentburg landmarks and neighborhoods accepted by geocode-location.")]
    public string AgentburgLocations => AgentburgLocationCatalog.SerializeKnownLocations(JsonOptions);

    [AgentSkillScript("geocode-location")]
    [Description("Converts an Agentburg landmark or neighborhood name to latitude and longitude as JSON.")]
    private static string GeocodeLocation(
        [Description("English location name, such as Old Town Square, Castle Hill, or Museum Mile.")] string location)
        => AgentburgLocationCatalog.GeocodeLocation(location, JsonOptions);

    [AgentSkillScript("get-all-activities")]
    [Description("Gets every available Agentburg activity as JSON.")]
    private async Task<string> GetAllActivitiesAsync()
    {
        try
        {
            var activities = await _activitiesService.GetAllActivities();
            return JsonSerializer.Serialize(new
            {
                message = $"Found {activities.Count} total activity(ies).",
                activities
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all activities");
            return JsonSerializer.Serialize(new { error = "An error occurred while retrieving activities." }, JsonOptions);
        }
    }

    [AgentSkillScript("get-activities-by-category")]
    [Description("Gets Agentburg activities by category as JSON.")]
    private async Task<string> GetActivitiesByCategoryAsync(
        [Description("Category: museums, theaters, cultural_events, or attractions.")] string category)
    {
        try
        {
            var activities = await _activitiesService.GetActivitiesByCategory(category);
            return JsonSerializer.Serialize(new
            {
                message = $"Found {activities.Count} {category} activity(ies).",
                activities
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting activities by category");
            return JsonSerializer.Serialize(new { error = "An error occurred while retrieving activities." }, JsonOptions);
        }
    }

    [AgentSkillScript("search-activities")]
    [Description("Searches Agentburg activities by category, coordinates, or keywords and returns JSON.")]
    private async Task<string> SearchActivitiesAsync(
        [Description("Optional category: museums, theaters, cultural_events, or attractions.")] string? category = null,
        [Description("Optional latitude coordinate for proximity search.")] double? latitude = null,
        [Description("Optional longitude coordinate for proximity search.")] double? longitude = null,
        [Description("Maximum search radius in kilometers when coordinates are supplied.")] double? maxDistanceKm = 1.0,
        [Description("Optional keywords to match in activity names, descriptions, or locations.")] string? keywords = null)
    {
        try
        {
            var activities = await _activitiesService.SearchActivities(
                category: category,
                latitude: latitude,
                longitude: longitude,
                maxDistanceKm: maxDistanceKm,
                keywords: keywords);

            if (activities.Count == 0)
            {
                return JsonSerializer.Serialize(new { message = "No activities found matching the criteria." }, JsonOptions);
            }

            return JsonSerializer.Serialize(new
            {
                message = $"Found {activities.Count} activity(ies) matching your criteria.",
                activities
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching activities");
            return JsonSerializer.Serialize(new { error = "An error occurred while searching for activities." }, JsonOptions);
        }
    }
}
