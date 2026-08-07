using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;

namespace OrchestratorAgent.Skills;

/// <summary>
/// In-process restaurant capability exposed through Agent Framework skills.
/// </summary>
public sealed class RestaurantSkill : AgentClassSkill<RestaurantSkill>
{
    private const double AgentburgCityCenterLatitude = 48.1000;
    private const double AgentburgCityCenterLongitude = 11.1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static readonly IReadOnlyList<Restaurant> Restaurants =
    [
        new("The Green Sprout", "vegetarian", "3 Old Town Square, Old Town, Agentburg", "+49-800-0101",
            "A cozy vegetarian restaurant just steps from the Old Town Square fountain, serving creative plant-based dishes made from locally sourced produce.",
            4.7, "$$", new(48.1006, 11.0991)),
        new("Herb & Garden Bistro", "vegetarian", "12 Park Lane, Central Park, Agentburg", "+49-800-0102",
            "Modern vegetarian bistro overlooking Agentburg's Central Park, with a seasonal menu celebrating the freshest local vegetables and herbs.",
            4.5, "$$$", new(48.1021, 11.1028)),
        new("Roots & Leaves", "vegetarian", "8 Museum Avenue, Museum Mile, Agentburg", "+49-800-0103",
            "Upscale vegetarian dining on Museum Mile, beloved by culture seekers after a visit to the History Museum. Known for its tasting menus and natural wines.",
            4.6, "$$$", new(48.1048, 11.1012)),
        new("Casa Agentburg", "pizza", "5 Market Square, Agentburg", "+49-800-0201",
            "Wood-fired Neapolitan-style pizza at the heart of Market Square. A local favourite for quick lunches and evening dinners.",
            4.8, "$$", new(48.1012, 11.0962)),
        new("Old Town Pizza", "pizza", "17 Cobblestone Lane, Old Town, Agentburg", "+49-800-0202",
            "Charming Old Town pizzeria tucked in a historic alley, serving crispy thin-crust pizzas with artisanal toppings since 1987.",
            4.6, "$", new(48.1004, 11.0988)),
        new("Harbor Fish House", "seafood", "1 Quayside Walk, Harbor District, Agentburg", "+49-800-0301",
            "Fresh seafood restaurant right on the harbor waterfront, with daily catches from the region's lakes and rivers.",
            4.7, "$$$", new(48.0952, 11.1098)),
        new("Sakura Garden", "japanese", "22 Cultural Plaza, Cultural Center, Agentburg", "+49-800-0302",
            "Authentic Japanese cuisine near the Cultural Center, featuring sushi, ramen, and seasonal omakase menus.",
            4.7, "$$$", new(48.1016, 11.0975)),
        new("Spice Route", "indian", "44 University Road, University Quarter, Agentburg", "+49-800-0303",
            "Vibrant Indian restaurant beloved by students and faculty from the nearby university, with an extensive menu of curries, tandoori, and vegetarian options.",
            4.5, "$$", new(48.1077, 11.0950)),
        new("Castle Bistro", "french", "2 Fortress Road, Castle Hill, Agentburg", "+49-800-0304",
            "Elegant French bistro at the foot of Castle Hill, offering classic Gallic cuisine with panoramic views of the old fortress.",
            4.8, "$$$$", new(48.1058, 11.0932)),
        new("Prime Steaks Agentburg", "steakhouse", "9 Downtown Boulevard, City Center, Agentburg", "+49-800-0305",
            "Premium steakhouse in the heart of Agentburg's city center, serving dry-aged cuts and an extensive cellar of regional wines.",
            4.9, "$$$$", new(48.1001, 11.1003))
    ];

    private static readonly IReadOnlyDictionary<string, Position> KnownLocations =
        new Dictionary<string, Position>(StringComparer.OrdinalIgnoreCase)
        {
            ["old town square"] = new(48.1005, 11.0990),
            ["old town"] = new(48.1005, 11.0990),
            ["castle hill"] = new(48.1060, 11.0930),
            ["castle hill fortress"] = new(48.1060, 11.0930),
            ["agentburg history museum"] = new(48.1050, 11.1010),
            ["history museum"] = new(48.1050, 11.1010),
            ["museum mile"] = new(48.1050, 11.1010),
            ["central park"] = new(48.1020, 11.1030),
            ["botanical garden"] = new(48.1022, 11.1031),
            ["harbor waterfront"] = new(48.0950, 11.1100),
            ["harbor district"] = new(48.0950, 11.1100),
            ["market square"] = new(48.1010, 11.0960),
            ["cultural center"] = new(48.1015, 11.0980),
            ["grand opera house"] = new(48.1014, 11.0979),
            ["agentburg grand opera"] = new(48.1014, 11.0979),
            ["tech hub"] = new(48.1030, 11.1050),
            ["innovation district"] = new(48.1030, 11.1050),
            ["observation tower"] = new(48.1001, 11.1001),
            ["harbor lighthouse"] = new(48.0946, 11.1105),
            ["agentburg"] = new(AgentburgCityCenterLatitude, AgentburgCityCenterLongitude),
            ["agentburg city center"] = new(AgentburgCityCenterLatitude, AgentburgCityCenterLongitude),
            ["downtown agentburg"] = new(AgentburgCityCenterLatitude, AgentburgCityCenterLongitude),
            ["university quarter"] = new(48.1080, 11.0950),
            ["university district"] = new(48.1080, 11.0950),
            ["main station"] = new(48.0990, 11.1020),
            ["science park"] = new(48.1035, 11.1060)
        };

    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        "restaurant-search",
        "Find and recommend Agentburg restaurants by category, keywords, or proximity to a landmark.");

    protected override string Instructions => """
        Use this skill whenever the user asks about restaurants, food, or dining in Agentburg.

        Supported categories are vegetarian, pizza, japanese, seafood, french, indian, and steakhouse.

        For a location-based request:
        1. Run geocode-location with the English landmark or neighborhood name.
        2. Extract latitude and longitude from its JSON response.
        3. Run search-restaurants-by-location with those coordinates and any requested category or keywords.

        For other requests, run get-all-restaurants, get-restaurants-by-category, or search-restaurants.
        Return friendly, detailed recommendations including name, address, phone, description, rating, and price range.
        """;

    protected override JsonSerializerOptions? SerializerOptions => JsonOptions;

    [AgentSkillResource("agentburg-locations")]
    [Description("Known Agentburg landmarks and neighborhoods accepted by geocode-location.")]
    public string AgentburgLocations => JsonSerializer.Serialize(KnownLocations, JsonOptions);

    [AgentSkillScript("geocode-location")]
    [Description("Converts an Agentburg landmark or neighborhood name to latitude and longitude as JSON.")]
    private static string GeocodeLocation(
        [Description("English location name, such as Old Town Square, Castle Hill, or Agentburg.")] string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Location parameter is required",
                location
            }, JsonOptions);
        }

        var normalizedLocation = location.Trim();
        var isFallback = !KnownLocations.TryGetValue(normalizedLocation, out var position);
        position ??= new Position(AgentburgCityCenterLatitude, AgentburgCityCenterLongitude);

        return JsonSerializer.Serialize(new
        {
            success = true,
            location = normalizedLocation,
            latitude = position.Latitude,
            longitude = position.Longitude,
            message = isFallback
                ? $"Location '{normalizedLocation}' not found in database. Returning Agentburg city center coordinates as fallback."
                : $"Successfully geocoded '{normalizedLocation}' to coordinates",
            isFallback
        }, JsonOptions);
    }

    [AgentSkillScript("get-all-restaurants")]
    [Description("Gets every available Agentburg restaurant as JSON.")]
    private static string GetAllRestaurants() => JsonSerializer.Serialize(Restaurants, JsonOptions);

    [AgentSkillScript("get-restaurants-by-category")]
    [Description("Gets Agentburg restaurants in a supported category as JSON.")]
    private static string GetRestaurantsByCategory(
        [Description("Category: vegetarian, pizza, japanese, seafood, french, indian, or steakhouse.")] string category)
    {
        var results = Restaurants
            .Where(restaurant => string.Equals(restaurant.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return JsonSerializer.Serialize(results, JsonOptions);
    }

    [AgentSkillScript("search-restaurants")]
    [Description("Searches restaurant names, categories, and descriptions using keywords and returns JSON.")]
    private static string SearchRestaurants(
        [Description("Keywords to find in a restaurant name, category, or description.")] string query)
    {
        var results = Restaurants
            .Where(restaurant =>
                restaurant.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                restaurant.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                restaurant.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return JsonSerializer.Serialize(results, JsonOptions);
    }

    [AgentSkillScript("search-restaurants-by-location")]
    [Description("Finds restaurants within a radius of coordinates, with optional category and keyword filters, and returns JSON.")]
    private static string SearchRestaurantsByLocation(
        [Description("Latitude of the reference location.")] double latitude,
        [Description("Longitude of the reference location.")] double longitude,
        [Description("Maximum search radius in kilometers.")] double maxDistanceKm = 1.0,
        [Description("Optional supported restaurant category.")] string? category = null,
        [Description("Optional keywords to match in restaurant names or descriptions.")] string? keywords = null)
    {
        var results = Restaurants.Where(restaurant =>
            CalculateDistance(latitude, longitude, restaurant.Position.Latitude, restaurant.Position.Longitude) <= maxDistanceKm);

        if (!string.IsNullOrWhiteSpace(category))
        {
            results = results.Where(restaurant =>
                string.Equals(restaurant.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(keywords))
        {
            results = results.Where(restaurant =>
                restaurant.Name.Contains(keywords, StringComparison.OrdinalIgnoreCase) ||
                restaurant.Description.Contains(keywords, StringComparison.OrdinalIgnoreCase));
        }

        return JsonSerializer.Serialize(results.ToList(), JsonOptions);
    }

    private static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusKm * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private sealed record Restaurant(
        string Name,
        string Category,
        string Address,
        string Phone,
        string Description,
        double Rating,
        string PriceRange,
        Position Position);

    private sealed record Position(double Latitude, double Longitude);
}
