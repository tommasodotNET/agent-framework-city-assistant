namespace RestaurantAgent.Models;

public class Restaurant
{
    public required string Name { get; set; }
    public required string Category { get; set; }
    public required string Address { get; set; }
    public required string Phone { get; set; }
    public required string Description { get; set; }
    public required double Rating { get; set; }
    public required string PriceRange { get; set; }
}
