using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RestaurantAgent.Services;

namespace RestaurantAgent.Tools;

public class RestaurantTools
{
    private readonly RestaurantService _restaurantService;

    public RestaurantTools(RestaurantService restaurantService)
    {
        _restaurantService = restaurantService;
    }

    [Description("Get a list of all available restaurants")]
    public string GetAllRestaurants()
    {
        var restaurants = _restaurantService.GetAllRestaurants();
        return JsonSerializer.Serialize(restaurants);
    }

    [Description("Search for restaurants by category. Supported categories: vegetarian, pizza, japanese, seafood, french, indian, steakhouse")]
    public string GetRestaurantsByCategory(
        [Description("The category to filter restaurants by (e.g., 'vegetarian', 'pizza', 'japanese')")] string category)
    {
        var restaurants = _restaurantService.GetRestaurantsByCategory(category);
        return JsonSerializer.Serialize(restaurants);
    }

    [Description("Search for restaurants by name or description using keywords")]
    public string SearchRestaurants(
        [Description("Search query or keywords to find restaurants")] string query)
    {
        var restaurants = _restaurantService.SearchRestaurants(query);
        return JsonSerializer.Serialize(restaurants);
    }

    [Description("Search for restaurants near a specific location using coordinates. Use geocode_location first to obtain latitude and longitude for any named place.")]
    public string SearchRestaurantsByLocation(
        [Description("Latitude of the reference location")] double latitude,
        [Description("Longitude of the reference location")] double longitude,
        [Description("Maximum search radius in kilometers (default: 1.0)")] double maxDistanceKm = 1.0,
        [Description("Optional category filter (e.g., 'vegetarian', 'pizza')")] string? category = null,
        [Description("Optional keywords to narrow down results")] string? keywords = null)
    {
        var restaurants = _restaurantService.SearchRestaurantsByLocation(latitude, longitude, maxDistanceKm, category, keywords);
        return JsonSerializer.Serialize(restaurants);
    }

    public IEnumerable<AIFunction> GetFunctions()
    {
        yield return AIFunctionFactory.Create(GetAllRestaurants);
        yield return AIFunctionFactory.Create(GetRestaurantsByCategory);
        yield return AIFunctionFactory.Create(SearchRestaurants);
        yield return AIFunctionFactory.Create(SearchRestaurantsByLocation);
    }
}
