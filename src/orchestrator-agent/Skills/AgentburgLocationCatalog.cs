using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OrchestratorAgent.Skills;

internal static class AgentburgLocationCatalog
{
    public const double AgentburgCityCenterLatitude = 48.1000;
    public const double AgentburgCityCenterLongitude = 11.1000;

    private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static readonly IReadOnlyDictionary<string, GeoPosition> KnownLocations =
        new Dictionary<string, GeoPosition>(StringComparer.OrdinalIgnoreCase)
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

    public static string SerializeKnownLocations(JsonSerializerOptions? serializerOptions = null)
        => JsonSerializer.Serialize(KnownLocations, serializerOptions ?? DefaultJsonOptions);

    public static string GeocodeLocation(string location, JsonSerializerOptions? serializerOptions = null)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Location parameter is required",
                location
            }, serializerOptions ?? DefaultJsonOptions);
        }

        var normalizedLocation = location.Trim();
        var isFallback = !KnownLocations.TryGetValue(normalizedLocation, out var position);
        position ??= new GeoPosition(AgentburgCityCenterLatitude, AgentburgCityCenterLongitude);

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
        }, serializerOptions ?? DefaultJsonOptions);
    }

    public static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
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

    private sealed record GeoPosition(double Latitude, double Longitude);
}
