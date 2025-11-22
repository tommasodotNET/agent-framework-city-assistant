using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using AccommodationAgent.Services;
using AccommodationAgent.Models;

namespace AccommodationAgent.Tools;

public class AccommodationTools
{
    private readonly IAccommodationService _accommodationService;
    private readonly IRerankingService _rerankingService;
    private readonly ILogger<AccommodationTools> _logger;

    public AccommodationTools(
        IAccommodationService accommodationService,
        IRerankingService rerankingService,
        ILogger<AccommodationTools> logger)
    {
        _accommodationService = accommodationService;
        _rerankingService = rerankingService;
        _logger = logger;
    }

    [Description("Search for accommodations based on various criteria including rating, location, amenities, price, and type")]
    public async Task<string> SearchAccommodationsAsync(
        [Description("The user's original search query to use for reranking results")] string userQuery,
        [Description("Minimum user rating from 1 to 5 (e.g., 4 means at least 4 stars)")] double? minRating = null,
        [Description("Latitude coordinate for proximity search")] double? latitude = null,
        [Description("Longitude coordinate for proximity search")] double? longitude = null,
        [Description("Maximum distance from the coordinates in kilometers (default: 1 km)")] double? maxDistanceKm = 1.0,
        [Description("List of required amenities (all must be present). Options include: parking, room-service, breakfast, wifi, gym, spa, restaurant, pool, bar, air-conditioning, 24-hour-reception, concierge, shared-kitchen")] List<string>? amenities = null,
        [Description("Maximum price per night in euros")] decimal? maxPricePerNight = null,
        [Description("Type of accommodation")] AccommodationType? type = null)
    {
        try
        {
            // Search accommodations with filters (all location searches use geocoding + coordinates)
            var accommodations = _accommodationService.SearchAccommodations(
                minRating: minRating,
                latitude: latitude,
                longitude: longitude,
                maxDistanceKm: maxDistanceKm,
                amenities: amenities,
                maxPricePerNight: maxPricePerNight,
                type: type);

            if (accommodations.Count == 0)
            {
                return JsonSerializer.Serialize(new { message = "No accommodations found matching the criteria." });
            }

            // Rerank using LLM to return only the most relevant results
            var rerankedAccommodations = await _rerankingService.RerankAccommodationsAsync(accommodations, userQuery);

            if (rerankedAccommodations.Count == 0)
            {
                return JsonSerializer.Serialize(new { 
                    message = "Found some accommodations but none were highly relevant to your query.",
                    matchedCount = accommodations.Count
                });
            }

            return JsonSerializer.Serialize(new
            {
                message = $"Found {rerankedAccommodations.Count} highly relevant accommodation(s) out of {accommodations.Count} matching your criteria.",
                accommodations = rerankedAccommodations
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching accommodations");
            return JsonSerializer.Serialize(new { error = "An error occurred while searching for accommodations." });
        }
    }

    [Description("Get all available accommodations without any filters")]
    public string GetAllAccommodations()
    {
        try
        {
            var accommodations = _accommodationService.GetAllAccommodations();
            return JsonSerializer.Serialize(new
            {
                message = $"Found {accommodations.Count} total accommodation(s).",
                accommodations
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all accommodations");
            return JsonSerializer.Serialize(new { error = "An error occurred while retrieving accommodations." });
        }
    }

    public IEnumerable<AIFunction> GetFunctions()
    {
        yield return AIFunctionFactory.Create(SearchAccommodationsAsync);
        yield return AIFunctionFactory.Create(GetAllAccommodations);
    }
}
