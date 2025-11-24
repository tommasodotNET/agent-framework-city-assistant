namespace AccommodationAgent.Models;

public class Accommodation
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required AccommodationType Type { get; set; }
    public required double Rating { get; set; }
    public required List<string> Amenities { get; set; }
    public required Location Position { get; set; }
    public required Address Address { get; set; }
    public required decimal PricePerNight { get; set; }
    public required string Description { get; set; }
}

public class Location
{
    public required double Latitude { get; set; }
    public required double Longitude { get; set; }
}

public class Address
{
    public required string Street { get; set; }
    public required string City { get; set; }
    public required string State { get; set; }
    public required string ZipCode { get; set; }
    public required string Country { get; set; }

    public override string ToString()
    {
        return $"{Street}, {City}, {State} {ZipCode}, {Country}";
    }
}
