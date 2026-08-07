using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using OrchestratorAgent.Models.Accommodation;
using OrchestratorAgent.Services;

namespace OrchestratorAgent.Skills;

public sealed class AccommodationSkill : AgentClassSkill<AccommodationSkill>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static readonly string[] SupportedAmenities =
    [
        "parking",
        "room-service",
        "breakfast",
        "wifi",
        "gym",
        "restaurant",
        "spa",
        "pool",
        "bar",
        "air-conditioning",
        "24-hour-reception",
        "concierge",
        "shared-kitchen"
    ];

    private readonly IAccommodationService _accommodationService;
    private readonly IRerankingService _rerankingService;
    private readonly ILogger<AccommodationSkill> _logger;

    public AccommodationSkill(
        IAccommodationService accommodationService,
        IRerankingService rerankingService,
        ILogger<AccommodationSkill> logger)
    {
        _accommodationService = accommodationService;
        _rerankingService = rerankingService;
        _logger = logger;
    }

    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        "accommodation-search",
        "Find and recommend Agentburg accommodations by rating, price, amenities, type, or proximity to a landmark.");

    protected override string Instructions => """
        Use this skill whenever the user asks about accommodations, hotels, B&Bs, hostels, or places to stay in Agentburg.

        Supported accommodation types are hotel, bed-and-breakfast, hostel, apartment, resort, guesthouse, motel, villa, and boutique.

        For a location-based request:
        1. Run geocode-location with the English landmark or neighborhood name.
        2. Extract latitude and longitude from its JSON response.
        3. Run search-accommodations with those coordinates and any requested rating, amenities, budget, or type filters.

        For other requests, run get-all-accommodations or search-accommodations.
        The search results are automatically reranked with AI using the original user query.
        Return friendly, detailed recommendations including accommodation type, rating, amenities, address, price per night, and description.
        """;

    protected override JsonSerializerOptions? SerializerOptions => JsonOptions;

    [AgentSkillResource("agentburg-locations")]
    [Description("Known Agentburg landmarks and neighborhoods accepted by geocode-location.")]
    public string AgentburgLocations => AgentburgLocationCatalog.SerializeKnownLocations(JsonOptions);

    [AgentSkillResource("supported-accommodation-types")]
    [Description("Supported accommodation type values accepted by search-accommodations.")]
    public string SupportedAccommodationTypes
        => JsonSerializer.Serialize(Enum.GetNames<AccommodationType>(), JsonOptions);

    [AgentSkillResource("supported-amenities")]
    [Description("Supported amenity values accepted by search-accommodations.")]
    public string Amenities => JsonSerializer.Serialize(SupportedAmenities, JsonOptions);

    [AgentSkillScript("geocode-location")]
    [Description("Converts an Agentburg landmark or neighborhood name to latitude and longitude as JSON.")]
    private static string GeocodeLocation(
        [Description("English location name, such as Old Town Square, Castle Hill, or Main Station.")] string location)
        => AgentburgLocationCatalog.GeocodeLocation(location, JsonOptions);

    [AgentSkillScript("get-all-accommodations")]
    [Description("Gets every available Agentburg accommodation as JSON.")]
    private async Task<string> GetAllAccommodationsAsync()
    {
        try
        {
            var accommodations = await _accommodationService.GetAllAccommodations();
            return JsonSerializer.Serialize(new
            {
                message = $"Found {accommodations.Count} total accommodation(s).",
                accommodations
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all accommodations");
            return JsonSerializer.Serialize(new { error = "An error occurred while retrieving accommodations." }, JsonOptions);
        }
    }

    [AgentSkillScript("search-accommodations")]
    [Description("Searches Agentburg accommodations by rating, coordinates, amenities, price, or type and returns reranked JSON results.")]
    private async Task<string> SearchAccommodationsAsync(
        [Description("The user's original search query to use for reranking the filtered results.")] string userQuery,
        [Description("Optional minimum user rating from 1 to 5.")] double? minRating = null,
        [Description("Optional latitude coordinate for proximity search.")] double? latitude = null,
        [Description("Optional longitude coordinate for proximity search.")] double? longitude = null,
        [Description("Maximum search radius in kilometers when coordinates are supplied.")] double? maxDistanceKm = 1.0,
        [Description("Optional list of required amenities. All provided amenities must be present.")] List<string>? amenities = null,
        [Description("Optional maximum price per night in euros.")] decimal? maxPricePerNight = null,
        [Description("Optional accommodation type such as hotel, bed-and-breakfast, hostel, or boutique.")] string? type = null)
    {
        try
        {
            var parsedType = ParseAccommodationType(type);
            if (!string.IsNullOrWhiteSpace(type) && parsedType is null)
            {
                return JsonSerializer.Serialize(new
                {
                    error = $"Unsupported accommodation type '{type}'.",
                    supportedTypes = Enum.GetNames<AccommodationType>()
                }, JsonOptions);
            }

            var accommodations = await _accommodationService.SearchAccommodations(
                minRating: minRating,
                latitude: latitude,
                longitude: longitude,
                maxDistanceKm: maxDistanceKm,
                amenities: amenities,
                maxPricePerNight: maxPricePerNight,
                type: parsedType);

            if (accommodations.Count == 0)
            {
                return JsonSerializer.Serialize(new { message = "No accommodations found matching the criteria." }, JsonOptions);
            }

            var rerankedAccommodations = await _rerankingService.RerankAccommodationsAsync(accommodations, userQuery);

            if (rerankedAccommodations.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    message = "Found some accommodations but none were highly relevant to your query.",
                    matchedCount = accommodations.Count
                }, JsonOptions);
            }

            return JsonSerializer.Serialize(new
            {
                message = $"Found {rerankedAccommodations.Count} highly relevant accommodation(s) out of {accommodations.Count} matching your criteria.",
                accommodations = rerankedAccommodations
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching accommodations");
            return JsonSerializer.Serialize(new { error = "An error occurred while searching for accommodations." }, JsonOptions);
        }
    }

    private static AccommodationType? ParseAccommodationType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var normalizedType = type.Trim().ToLowerInvariant();

        return normalizedType switch
        {
            "hotel" => AccommodationType.Hotel,
            "bed and breakfast" or "bed-and-breakfast" or "bed&breakfast" or "b&b" or "bnb" => AccommodationType.BedAndBreakfast,
            "hostel" => AccommodationType.Hostel,
            "apartment" => AccommodationType.Apartment,
            "resort" => AccommodationType.Resort,
            "guesthouse" or "guest house" => AccommodationType.Guesthouse,
            "motel" => AccommodationType.Motel,
            "villa" => AccommodationType.Villa,
            "boutique" or "boutique hotel" => AccommodationType.Boutique,
            _ => null
        };
    }
}
