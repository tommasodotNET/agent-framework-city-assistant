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
    public required Position Position { get; set; }
}

public class Position
{
    public required double Latitude { get; set; }
    public required double Longitude { get; set; }
}
