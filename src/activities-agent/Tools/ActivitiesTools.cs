using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ActivitiesAgent.Services;

namespace ActivitiesAgent.Tools;

public class ActivitiesTools
{
    private readonly ActivitiesService _activitiesService;
    private readonly ILogger<ActivitiesTools> _logger;

    public ActivitiesTools(
        ActivitiesService activitiesService,
        ILogger<ActivitiesTools> logger)
    {
        _activitiesService = activitiesService;
        _logger = logger;
    }

    [Description("Search for activities based on various criteria including category, location, and keywords")]
    public async Task<string> SearchActivitiesAsync(
        [Description("Category to filter by: museums, theaters, cultural_events, attractions")] string? category = null,
        [Description("Latitude coordinate for proximity search")] double? latitude = null,
        [Description("Longitude coordinate for proximity search")] double? longitude = null,
        [Description("Maximum distance from the coordinates in kilometers (default: 1 km)")] double? maxDistanceKm = 1.0,
        [Description("Keywords to search in activity name or description")] string? keywords = null)
    {
        try
        {
            var activities = _activitiesService.SearchActivities(
                category: category,
                latitude: latitude,
                longitude: longitude,
                maxDistanceKm: maxDistanceKm,
                keywords: keywords);

            if (activities.Count == 0)
            {
                return JsonSerializer.Serialize(new { message = "No activities found matching the criteria." });
            }

            return JsonSerializer.Serialize(new
            {
                message = $"Found {activities.Count} activity(ies) matching your criteria.",
                activities
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching activities");
            return JsonSerializer.Serialize(new { error = "An error occurred while searching for activities." });
        }
    }

    [Description("Get all available activities without any filters")]
    public string GetAllActivities()
    {
        try
        {
            var activities = _activitiesService.GetAllActivities();
            return JsonSerializer.Serialize(new
            {
                message = $"Found {activities.Count} total activity(ies).",
                activities
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all activities");
            return JsonSerializer.Serialize(new { error = "An error occurred while retrieving activities." });
        }
    }

    [Description("Get activities by category. Supported categories: museums, theaters, cultural_events, attractions")]
    public string GetActivitiesByCategory(
        [Description("The category to filter activities by (e.g., 'museums', 'theaters', 'cultural_events', 'attractions')")] string category)
    {
        try
        {
            var activities = _activitiesService.GetActivitiesByCategory(category);
            return JsonSerializer.Serialize(new
            {
                message = $"Found {activities.Count} {category} activity(ies).",
                activities
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting activities by category");
            return JsonSerializer.Serialize(new { error = "An error occurred while retrieving activities." });
        }
    }

    public IEnumerable<AIFunction> GetFunctions()
    {
        yield return AIFunctionFactory.Create(SearchActivitiesAsync);
        yield return AIFunctionFactory.Create(GetAllActivities);
        yield return AIFunctionFactory.Create(GetActivitiesByCategory);
    }
}
