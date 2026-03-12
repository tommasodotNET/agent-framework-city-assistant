using AccommodationAgent.Models;

namespace AccommodationAgent.Services;

public class AccommodationService : IAccommodationService
{
    private readonly List<Accommodation> _accommodations;

    public AccommodationService()
    {
        _accommodations = new List<Accommodation>
        {
            // Hotels
            new Accommodation
            {
                Id = "1",
                Name = "Grand Hotel Agentburg",
                Type = AccommodationType.Hotel,
                Rating = 4.8,
                Amenities = ["parking", "room-service", "breakfast", "wifi", "gym", "restaurant"],
                Position = new Location { Latitude = 48.1005, Longitude = 11.0992 },
                Address = new Address
                {
                    Street = "3 Old Town Square",
                    City = "Agentburg",
                    State = "Agentburg State",
                    ZipCode = "AG1001",
                    Country = "Agentland"
                },
                PricePerNight = 180.00m,
                Description = "Luxury hotel overlooking Old Town Square, just steps from the Historic Fountain. Features elegant rooms with views of cobblestone streets, a rooftop restaurant, and a wellness spa."
            },
            new Accommodation
            {
                Id = "2",
                Name = "Castle View Hotel",
                Type = AccommodationType.Hotel,
                Rating = 4.6,
                Amenities = ["breakfast", "wifi", "restaurant", "bar", "parking"],
                Position = new Location { Latitude = 48.1062, Longitude = 11.0933 },
                Address = new Address
                {
                    Street = "5 Fortress Road",
                    City = "Agentburg",
                    State = "Agentburg State",
                    ZipCode = "AG1060",
                    Country = "Agentland"
                },
                PricePerNight = 150.00m,
                Description = "Four-star hotel at the foot of Castle Hill with stunning fortress views from every room. Perfect for guests who want to explore the castle by day and dine at Agentburg's finest restaurants by night."
            },
            new Accommodation
            {
                Id = "3",
                Name = "Harbor Inn",
                Type = AccommodationType.Hotel,
                Rating = 4.5,
                Amenities = ["breakfast", "wifi", "parking", "restaurant", "bar"],
                Position = new Location { Latitude = 48.0951, Longitude = 11.1099 },
                Address = new Address
                {
                    Street = "8 Quayside Walk",
                    City = "Agentburg",
                    State = "Agentburg State",
                    ZipCode = "AG0950",
                    Country = "Agentland"
                },
                PricePerNight = 120.00m,
                Description = "Charming waterfront hotel in the vibrant Harbor District, with panoramic views of the marina. Home to the award-winning Harbor Fish House restaurant next door. Great for seafood lovers."
            },

            // Bed & Breakfasts
            new Accommodation
            {
                Id = "4",
                Name = "Old Town B&B",
                Type = AccommodationType.BedAndBreakfast,
                Rating = 4.7,
                Amenities = ["breakfast", "wifi", "air-conditioning"],
                Position = new Location { Latitude = 48.1007, Longitude = 11.0988 },
                Address = new Address
                {
                    Street = "12 Cobblestone Lane",
                    City = "Agentburg",
                    State = "Agentburg State",
                    ZipCode = "AG1005",
                    Country = "Agentland"
                },
                PricePerNight = 75.00m,
                Description = "Cozy family-run bed and breakfast in Agentburg's historic Old Town, just around the corner from the Old Town Square. Homemade breakfasts with local products, and the owners are a goldmine of local tips."
            },
            new Accommodation
            {
                Id = "5",
                Name = "University Guesthouse",
                Type = AccommodationType.BedAndBreakfast,
                Rating = 4.4,
                Amenities = ["breakfast", "wifi", "shared-kitchen", "parking"],
                Position = new Location { Latitude = 48.1079, Longitude = 11.0949 },
                Address = new Address
                {
                    Street = "33 Campus Road",
                    City = "Agentburg",
                    State = "Agentburg State",
                    ZipCode = "AG1080",
                    Country = "Agentland"
                },
                PricePerNight = 60.00m,
                Description = "Welcoming guesthouse in the heart of the University Quarter, popular with academics and budget-conscious travellers. Communal kitchen, leafy courtyard, and close to Spice Route restaurant."
            },

            // Hostel
            new Accommodation
            {
                Id = "6",
                Name = "Central Park Hostel",
                Type = AccommodationType.Hostel,
                Rating = 4.1,
                Amenities = ["wifi", "breakfast", "shared-kitchen"],
                Position = new Location { Latitude = 48.1019, Longitude = 11.1029 },
                Address = new Address
                {
                    Street = "20 Park Lane",
                    City = "Agentburg",
                    State = "Agentburg State",
                    ZipCode = "AG1020",
                    Country = "Agentland"
                },
                PricePerNight = 28.00m,
                Description = "Budget-friendly hostel facing Agentburg's Central Park and Botanical Garden. Offers dorm beds and private rooms. A great base for young travellers wanting to explore the city on foot."
            },

            // Boutique
            new Accommodation
            {
                Id = "7",
                Name = "Museum Mile Boutique Hotel",
                Type = AccommodationType.Boutique,
                Rating = 4.9,
                Amenities = ["wifi", "room-service", "breakfast", "bar", "concierge", "spa"],
                Position = new Location { Latitude = 48.1052, Longitude = 11.1012 },
                Address = new Address
                {
                    Street = "6 Museum Avenue",
                    City = "Agentburg",
                    State = "Agentburg State",
                    ZipCode = "AG1050",
                    Country = "Agentland"
                },
                PricePerNight = 320.00m,
                Description = "Exquisite boutique hotel on the prestigious Museum Mile, minutes from the Agentburg History Museum and the Modern Art Gallery. Each suite is individually designed with curated artworks, blending cultural immersion with luxury comfort."
            },

            // Budget hotel
            new Accommodation
            {
                Id = "8",
                Name = "Main Station Budget Hotel",
                Type = AccommodationType.Hotel,
                Rating = 3.9,
                Amenities = ["wifi", "breakfast", "24-hour-reception"],
                Position = new Location { Latitude = 48.0991, Longitude = 11.1021 },
                Address = new Address
                {
                    Street = "1 Station Road",
                    City = "Agentburg",
                    State = "Agentburg State",
                    ZipCode = "AG0990",
                    Country = "Agentland"
                },
                PricePerNight = 55.00m,
                Description = "Convenient budget hotel directly opposite Agentburg Main Station. Ideal for transit passengers and early-morning travellers. Basic but clean rooms, 24-hour reception, and easy walking distance to the city center."
            }
        };
    }

    public List<Accommodation> GetAllAccommodations()
    {
        return _accommodations;
    }

    public List<Accommodation> SearchAccommodations(
        double? minRating = null,
        double? latitude = null,
        double? longitude = null,
        double? maxDistanceKm = 1.0,
        List<string>? amenities = null,
        decimal? maxPricePerNight = null,
        AccommodationType? type = null)
    {
        var results = _accommodations.AsQueryable();

        // Filter by rating
        if (minRating.HasValue)
        {
            results = results.Where(a => a.Rating >= minRating.Value);
        }

        // Filter by distance from a reference point
        if (latitude.HasValue && longitude.HasValue && maxDistanceKm.HasValue)
        {
            results = results.Where(a =>
                CalculateDistance(latitude.Value, longitude.Value, a.Position.Latitude, a.Position.Longitude) <= maxDistanceKm.Value);
        }

        // Filter by amenities (all amenities must be present)
        if (amenities != null && amenities.Count > 0)
        {
            results = results.Where(a =>
                amenities.All(amenity => a.Amenities.Contains(amenity, StringComparer.OrdinalIgnoreCase)));
        }

        // Filter by max price per night
        if (maxPricePerNight.HasValue)
        {
            results = results.Where(a => a.PricePerNight <= maxPricePerNight.Value);
        }

        // Filter by type
        if (type.HasValue)
        {
            results = results.Where(a => a.Type == type.Value);
        }

        return results.ToList();
    }

    /// <summary>
    /// Calculate distance between two coordinates using Haversine formula
    /// </summary>
    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
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

    private double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }
}
