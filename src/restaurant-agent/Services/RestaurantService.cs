using RestaurantAgent.Models;

namespace RestaurantAgent.Services;

public class RestaurantService
{
    private readonly List<Restaurant> _restaurants;

    public RestaurantService()
    {
        _restaurants = new List<Restaurant>
        {
            // Vegetarian Restaurants
            new Restaurant
            {
                Name = "Green Garden",
                Category = "vegetarian",
                Address = "123 Veggie Lane, Downtown",
                Phone = "+1-555-0101",
                Description = "A cozy vegetarian restaurant with fresh organic ingredients and creative plant-based dishes.",
                Rating = 4.7,
                PriceRange = "$$"
            },
            new Restaurant
            {
                Name = "The Herbivore",
                Category = "vegetarian",
                Address = "456 Plant Street, Midtown",
                Phone = "+1-555-0102",
                Description = "Modern vegetarian cuisine with a focus on local and seasonal produce.",
                Rating = 4.5,
                PriceRange = "$$$"
            },
            new Restaurant
            {
                Name = "Veggie Delight",
                Category = "vegetarian",
                Address = "789 Green Avenue, Uptown",
                Phone = "+1-555-0103",
                Description = "Family-friendly vegetarian restaurant offering comfort food classics made plant-based.",
                Rating = 4.3,
                PriceRange = "$"
            },

            // Pizza Restaurants
            new Restaurant
            {
                Name = "Napoli Pizza",
                Category = "pizza",
                Address = "321 Italian Way, Little Italy",
                Phone = "+1-555-0201",
                Description = "Authentic Neapolitan pizza made with imported Italian ingredients in a traditional wood-fired oven.",
                Rating = 4.8,
                PriceRange = "$$"
            },
            new Restaurant
            {
                Name = "Pizza Palace",
                Category = "pizza",
                Address = "654 Cheese Boulevard, Downtown",
                Phone = "+1-555-0202",
                Description = "New York-style pizza with generous toppings and crispy thin crust.",
                Rating = 4.6,
                PriceRange = "$"
            },
            new Restaurant
            {
                Name = "Artisan Pizza Co",
                Category = "pizza",
                Address = "987 Gourmet Street, Arts District",
                Phone = "+1-555-0203",
                Description = "Creative gourmet pizzas with unique toppings and artisanal ingredients.",
                Rating = 4.9,
                PriceRange = "$$$"
            },

            // Other Categories
            new Restaurant
            {
                Name = "Sakura Sushi",
                Category = "japanese",
                Address = "111 Cherry Blossom Way, Japantown",
                Phone = "+1-555-0301",
                Description = "Traditional Japanese sushi restaurant with fresh fish and authentic preparation.",
                Rating = 4.7,
                PriceRange = "$$$"
            },
            new Restaurant
            {
                Name = "Taco Fiesta",
                Category = "mexican",
                Address = "222 Salsa Street, Mission District",
                Phone = "+1-555-0302",
                Description = "Vibrant Mexican restaurant serving authentic tacos, burritos, and margaritas.",
                Rating = 4.4,
                PriceRange = "$"
            },
            new Restaurant
            {
                Name = "The French Table",
                Category = "french",
                Address = "333 Champagne Avenue, Financial District",
                Phone = "+1-555-0303",
                Description = "Elegant French bistro with classic dishes and an extensive wine selection.",
                Rating = 4.8,
                PriceRange = "$$$$"
            },
            new Restaurant
            {
                Name = "Curry House",
                Category = "indian",
                Address = "444 Spice Road, Little India",
                Phone = "+1-555-0304",
                Description = "Aromatic Indian cuisine with a wide variety of curries, tandoori, and vegetarian options.",
                Rating = 4.6,
                PriceRange = "$$"
            },
            new Restaurant
            {
                Name = "The Steakhouse",
                Category = "steakhouse",
                Address = "555 Prime Cut Lane, Financial District",
                Phone = "+1-555-0305",
                Description = "Premium steakhouse featuring aged beef, seafood, and an extensive wine cellar.",
                Rating = 4.9,
                PriceRange = "$$$$"
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
}
