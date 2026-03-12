using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GeocodingMcpServer.Tools;

/// <summary>
/// MCP tool for geocoding addresses and landmarks to coordinates
/// </summary>
[McpServerToolType]
public sealed class GeocodingTool
{
    private readonly ILogger<GeocodingTool> _logger;
    
    // Fallback coordinates for unknown locations (Agentburg city center)
    private const double AgentburgCityCenterLatitude = 48.1000;
    private const double AgentburgCityCenterLongitude = 11.1000;
    
    // Mock data for known landmarks and locations in Agentburg
    private static readonly Dictionary<string, (double Latitude, double Longitude)> KnownLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        // Agentburg landmarks and attractions
        { "old town square", (48.1005, 11.0990) },
        { "castle hill", (48.1060, 11.0930) },
        { "castle hill fortress", (48.1060, 11.0930) },
        { "agentburg history museum", (48.1050, 11.1010) },
        { "history museum", (48.1050, 11.1010) },
        { "central park", (48.1020, 11.1030) },
        { "botanical garden", (48.1022, 11.1031) },
        { "harbor waterfront", (48.0950, 11.1100) },
        { "market square", (48.1010, 11.0960) },
        { "cultural center", (48.1015, 11.0980) },
        { "grand opera house", (48.1014, 11.0979) },
        { "agentburg grand opera", (48.1014, 11.0979) },
        { "tech hub", (48.1030, 11.1050) },
        { "innovation district", (48.1030, 11.1050) },
        { "observation tower", (48.1001, 11.1001) },
        { "harbor lighthouse", (48.0946, 11.1105) },
        
        // Agentburg city
        { "agentburg", (48.1000, 11.1000) },
        { "agentburg city center", (48.1000, 11.1000) },
        { "downtown agentburg", (48.1000, 11.1000) },
        
        // Agentburg neighborhoods
        { "old town", (48.1005, 11.0990) },
        { "harbor district", (48.0950, 11.1100) },
        { "museum mile", (48.1050, 11.1010) },
        { "university quarter", (48.1080, 11.0950) },
        { "university district", (48.1080, 11.0950) },
        { "main station", (48.0990, 11.1020) },
        { "science park", (48.1035, 11.1060) }
    };

    public GeocodingTool(ILogger<GeocodingTool> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Geocode an address or landmark to get its coordinates
    /// </summary>
    [McpServerTool]
    [Description("Geocode an address or landmark to get its coordinates (latitude, longitude). Returns location details with coordinates.")]
    public Task<string> GeocodeLocation(
        [Description("Address or landmark name to geocode in English (e.g., 'Old Town Square', 'Castle Hill', 'Agentburg'). Must be in English.")] string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            var errorResult = new
            {
                success = false,
                error = "Location parameter is required",
                location = location
            };
            return Task.FromResult(JsonSerializer.Serialize(errorResult));
        }

        if (KnownLocations.TryGetValue(location.Trim(), out var coordinates))
        {
            _logger.LogInformation("Geocoded '{Location}' to coordinates: {Lat}, {Lon}", location, coordinates.Latitude, coordinates.Longitude);
            
            var result = new
            {
                success = true,
                location = location.Trim(),
                latitude = coordinates.Latitude,
                longitude = coordinates.Longitude,
                message = $"Successfully geocoded '{location}' to coordinates"
            };
            
            return Task.FromResult(JsonSerializer.Serialize(result));
        }

        // Fallback: return Agentburg city center coordinates for unknown locations
        _logger.LogWarning("Location '{Location}' not found in mock data. Returning Agentburg city center coordinates as fallback.", location);
        
        var fallbackResult = new
        {
            success = true,
            location = location.Trim(),
            latitude = AgentburgCityCenterLatitude,
            longitude = AgentburgCityCenterLongitude,
            message = $"Location '{location}' not found in database. Returning Agentburg city center coordinates as fallback.",
            isFallback = true
        };
        
        return Task.FromResult(JsonSerializer.Serialize(fallbackResult));
    }
}
