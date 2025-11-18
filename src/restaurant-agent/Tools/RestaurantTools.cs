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

    [Description("Search for restaurants by category. Supported categories: vegetarian, pizza, japanese, mexican, french, indian, steakhouse")]
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

    public IEnumerable<AIFunction> GetFunctions()
    {
        yield return AIFunctionFactory.Create(GetAllRestaurants);
        yield return AIFunctionFactory.Create(GetRestaurantsByCategory);
        yield return AIFunctionFactory.Create(SearchRestaurants);
    }
}
