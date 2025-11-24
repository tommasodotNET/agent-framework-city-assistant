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
    
    // Fallback coordinates for unknown locations (Rome city center)
    private const double RomeCityCenterLatitude = 41.9028;
    private const double RomeCityCenterLongitude = 12.4964;
    
    // Mock data for known landmarks and cities
    // TODO: Consider externalizing to configuration file or database for easier maintenance
    private static readonly Dictionary<string, (double Latitude, double Longitude)> KnownLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        // Rome landmarks
        { "colosseum", (41.8902, 12.4922) },
        { "coliseum", (41.8902, 12.4922) },
        { "roman forum", (41.8925, 12.4853) },
        { "vatican", (41.9029, 12.4534) },
        { "vatican city", (41.9029, 12.4534) },
        { "pantheon", (41.8986, 12.4768) },
        { "trevi fountain", (41.9009, 12.4833) },
        { "spanish steps", (41.9058, 12.4823) },
        { "trastevere", (41.8899, 12.4707) },
        
        // Cities
        { "rome", (41.9028, 12.4964) },
        { "roma", (41.9028, 12.4964) },
        { "latina", (41.4677, 12.9037) },
        
        // Rome areas/neighborhoods
        { "downtown rome", (41.9028, 12.4964) },
        { "rome city center", (41.9028, 12.4964) },
        { "termini station", (41.9008, 12.5015) }
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
        [Description("Address or landmark name to geocode in English (e.g., 'Colosseum', 'Vatican', 'Rome'). Must be in English.")] string location)
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

        // Fallback: return Rome city center coordinates for unknown locations
        _logger.LogWarning("Location '{Location}' not found in mock data. Returning Rome city center coordinates as fallback.", location);
        
        var fallbackResult = new
        {
            success = true,
            location = location.Trim(),
            latitude = RomeCityCenterLatitude,
            longitude = RomeCityCenterLongitude,
            message = $"Location '{location}' not found in database. Returning Rome city center coordinates as fallback.",
            isFallback = true
        };
        
        return Task.FromResult(JsonSerializer.Serialize(fallbackResult));
    }
}
