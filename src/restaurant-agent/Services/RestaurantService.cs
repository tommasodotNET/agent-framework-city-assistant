using RestaurantAgent.Models;

namespace RestaurantAgent.Services;

public class RestaurantService
{
    private readonly List<Restaurant> _restaurants;

    public RestaurantService()
    {
        _restaurants = new List<Restaurant>
        {
            // Vegetarian Restaurants (near public attractions in Agentburg)
            new Restaurant
            {
                Name = "The Green Sprout",
                Category = "vegetarian",
                Address = "3 Old Town Square, Old Town, Agentburg",
                Phone = "+49-800-0101",
                Description = "A cozy vegetarian restaurant just steps from the Old Town Square fountain, serving creative plant-based dishes made from locally sourced produce.",
                Rating = 4.7,
                PriceRange = "$$",
                Position = new Position { Latitude = 48.1006, Longitude = 11.0991 }
            },
            new Restaurant
            {
                Name = "Herb & Garden Bistro",
                Category = "vegetarian",
                Address = "12 Park Lane, Central Park, Agentburg",
                Phone = "+49-800-0102",
                Description = "Modern vegetarian bistro overlooking Agentburg's Central Park, with a seasonal menu celebrating the freshest local vegetables and herbs.",
                Rating = 4.5,
                PriceRange = "$$$",
                Position = new Position { Latitude = 48.1021, Longitude = 11.1028 }
            },
            new Restaurant
            {
                Name = "Roots & Leaves",
                Category = "vegetarian",
                Address = "8 Museum Avenue, Museum Mile, Agentburg",
                Phone = "+49-800-0103",
                Description = "Upscale vegetarian dining on Museum Mile, beloved by culture seekers after a visit to the History Museum. Known for its tasting menus and natural wines.",
                Rating = 4.6,
                PriceRange = "$$$",
                Position = new Position { Latitude = 48.1048, Longitude = 11.1012 }
            },

            // Pizza Restaurants
            new Restaurant
            {
                Name = "Casa Agentburg",
                Category = "pizza",
                Address = "5 Market Square, Agentburg",
                Phone = "+49-800-0201",
                Description = "Wood-fired Neapolitan-style pizza at the heart of Market Square. A local favourite for quick lunches and evening dinners.",
                Rating = 4.8,
                PriceRange = "$$",
                Position = new Position { Latitude = 48.1012, Longitude = 11.0962 }
            },
            new Restaurant
            {
                Name = "Old Town Pizza",
                Category = "pizza",
                Address = "17 Cobblestone Lane, Old Town, Agentburg",
                Phone = "+49-800-0202",
                Description = "Charming Old Town pizzeria tucked in a historic alley, serving crispy thin-crust pizzas with artisanal toppings since 1987.",
                Rating = 4.6,
                PriceRange = "$",
                Position = new Position { Latitude = 48.1004, Longitude = 11.0988 }
            },

            // Other Categories
            new Restaurant
            {
                Name = "Harbor Fish House",
                Category = "seafood",
                Address = "1 Quayside Walk, Harbor District, Agentburg",
                Phone = "+49-800-0301",
                Description = "Fresh seafood restaurant right on the harbor waterfront, with daily catches from the region's lakes and rivers.",
                Rating = 4.7,
                PriceRange = "$$$",
                Position = new Position { Latitude = 48.0952, Longitude = 11.1098 }
            },
            new Restaurant
            {
                Name = "Sakura Garden",
                Category = "japanese",
                Address = "22 Cultural Plaza, Cultural Center, Agentburg",
                Phone = "+49-800-0302",
                Description = "Authentic Japanese cuisine near the Cultural Center, featuring sushi, ramen, and seasonal omakase menus.",
                Rating = 4.7,
                PriceRange = "$$$",
                Position = new Position { Latitude = 48.1016, Longitude = 11.0975 }
            },
            new Restaurant
            {
                Name = "Spice Route",
                Category = "indian",
                Address = "44 University Road, University Quarter, Agentburg",
                Phone = "+49-800-0303",
                Description = "Vibrant Indian restaurant beloved by students and faculty from the nearby university, with an extensive menu of curries, tandoori, and vegetarian options.",
                Rating = 4.5,
                PriceRange = "$$",
                Position = new Position { Latitude = 48.1077, Longitude = 11.0950 }
            },
            new Restaurant
            {
                Name = "Castle Bistro",
                Category = "french",
                Address = "2 Fortress Road, Castle Hill, Agentburg",
                Phone = "+49-800-0304",
                Description = "Elegant French bistro at the foot of Castle Hill, offering classic Gallic cuisine with panoramic views of the old fortress.",
                Rating = 4.8,
                PriceRange = "$$$$",
                Position = new Position { Latitude = 48.1058, Longitude = 11.0932 }
            },
            new Restaurant
            {
                Name = "Prime Steaks Agentburg",
                Category = "steakhouse",
                Address = "9 Downtown Boulevard, City Center, Agentburg",
                Phone = "+49-800-0305",
                Description = "Premium steakhouse in the heart of Agentburg's city center, serving dry-aged cuts and an extensive cellar of regional wines.",
                Rating = 4.9,
                PriceRange = "$$$$",
                Position = new Position { Latitude = 48.1001, Longitude = 11.1003 }
            }
        };
    }

    public List<Restaurant> GetAllRestaurants()
    {
        return _restaurants;
    }

    public List<Restaurant> GetRestaurantsByCategory(string category)
    {
        var normalizedCategory = category.ToLowerInvariant();
        return _restaurants.Where(r => r.Category.ToLowerInvariant() == normalizedCategory).ToList();
    }

    public List<Restaurant> SearchRestaurants(string query)
    {
        var normalizedQuery = query.ToLowerInvariant();
        return _restaurants.Where(r =>
            r.Name.ToLowerInvariant().Contains(normalizedQuery) ||
            r.Category.ToLowerInvariant().Contains(normalizedQuery) ||
            r.Description.ToLowerInvariant().Contains(normalizedQuery)
        ).ToList();
    }

    public List<Restaurant> SearchRestaurantsByLocation(
        double latitude,
        double longitude,
        double maxDistanceKm = 1.0,
        string? category = null,
        string? keywords = null)
    {
        var query = _restaurants.AsQueryable();

        // Filter by distance
        query = query.Where(r =>
            CalculateDistance(latitude, longitude, r.Position.Latitude, r.Position.Longitude) <= maxDistanceKm);

        // Filter by category
        if (!string.IsNullOrWhiteSpace(category))
        {
            var normalizedCategory = category.ToLowerInvariant();
            query = query.Where(r => r.Category.ToLowerInvariant() == normalizedCategory);
        }

        // Filter by keywords
        if (!string.IsNullOrWhiteSpace(keywords))
        {
            var normalizedKeywords = keywords.ToLowerInvariant();
            query = query.Where(r =>
                r.Name.ToLowerInvariant().Contains(normalizedKeywords) ||
                r.Description.ToLowerInvariant().Contains(normalizedKeywords));
        }

        return query.ToList();
    }

    private static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusKm * c;
    }
}
