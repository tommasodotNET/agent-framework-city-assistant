namespace AccommodationAgent.Services;

/// <summary>
/// Mock geocoding service that returns coordinates for known locations.
/// This will be replaced with a real geocoding API (e.g., Azure Maps API) in the future.
/// </summary>
public class GeocodingService : IGeocodingService
{
    private readonly ILogger<GeocodingService> _logger;
    private readonly IAccommodationService _accommodationService;
    
    // Mock data for known landmarks and cities
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

    public GeocodingService(ILogger<GeocodingService> logger, IAccommodationService accommodationService)
    {
        _logger = logger;
        _accommodationService = accommodationService;
    }

    public Task<(double Latitude, double Longitude)?> GeocodeAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<(double Latitude, double Longitude)?>(null);
        }

        if (KnownLocations.TryGetValue(query.Trim(), out var coordinates))
        {
            _logger.LogInformation("Geocoded '{Query}' to coordinates: {Lat}, {Lon}", query, coordinates.Latitude, coordinates.Longitude);
            return Task.FromResult<(double Latitude, double Longitude)?>(coordinates);
        }

        // When location not found, return coordinates near a random mocked accommodation
        var accommodations = _accommodationService.GetAllAccommodations();
        if (accommodations.Count > 0)
        {
            // Use query hash to select an accommodation (deterministic but varies based on query)
            var random = new Random(query.GetHashCode());
            var selectedAccommodation = accommodations[random.Next(accommodations.Count)];
            
            // Generate coordinates near the selected accommodation (within ~500 meters)
            // Roughly 0.005 degrees latitude/longitude is about 500 meters
            var latOffset = (random.NextDouble() - 0.5) * 0.01; // +/- 0.005 degrees (~500m)
            var lonOffset = (random.NextDouble() - 0.5) * 0.01; // +/- 0.005 degrees (~500m)
            
            var nearbyLat = selectedAccommodation.Position.Latitude + latOffset;
            var nearbyLon = selectedAccommodation.Position.Longitude + lonOffset;
            
            _logger.LogWarning("Location '{Query}' not found in mock data. Returning coordinates near '{AccommodationName}': {Lat}, {Lon}", 
                query, selectedAccommodation.Name, nearbyLat, nearbyLon);
            
            return Task.FromResult<(double Latitude, double Longitude)?>((nearbyLat, nearbyLon));
        }
        
        // Fallback if no accommodations exist (shouldn't happen)
        _logger.LogError("No accommodations available for fallback geocoding. Returning null.");
        return Task.FromResult<(double Latitude, double Longitude)?>(null);
    }
}
