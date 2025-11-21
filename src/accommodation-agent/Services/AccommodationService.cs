using AccommodationAgent.Models;

namespace AccommodationAgent.Services;

public class AccommodationService : IAccommodationService
{
    private readonly List<Accommodation> _accommodations;

    public AccommodationService()
    {
        _accommodations = new List<Accommodation>
        {
            // Hotels in Rome near Colosseum
            new Accommodation
            {
                Id = "1",
                Name = "Grand Hotel Colosseo",
                Type = AccommodationType.Hotel,
                Rating = 4.8,
                Amenities = ["parking", "room-service", "breakfast", "wifi", "gym", "restaurant"],
                Position = new Location { Latitude = 41.8902, Longitude = 12.4922 },
                Address = new Address
                {
                    Street = "Via Labicana 125",
                    City = "Rome",
                    State = "Lazio",
                    ZipCode = "00184",
                    Country = "Italy"
                },
                PricePerNight = 180.00m,
                Description = "Luxury hotel strategically located just 200 meters from the iconic Colosseum, offering breathtaking ancient monument views from select rooms. Features elegantly appointed suites with marble bathrooms, a rooftop terrace restaurant serving authentic Italian cuisine, and a modern fitness center. Perfect for history enthusiasts and travelers seeking upscale comfort in the heart of ancient Rome. The property combines classical Roman architecture with contemporary amenities, including secure underground parking and 24/7 concierge service."
            },
            new Accommodation
            {
                Id = "2",
                Name = "Hotel Forum",
                Type = AccommodationType.Hotel,
                Rating = 4.6,
                Amenities = ["breakfast", "wifi", "restaurant", "bar"],
                Position = new Location { Latitude = 41.8925, Longitude = 12.4853 },
                Address = new Address
                {
                    Street = "Via Tor de' Conti 25",
                    City = "Rome",
                    State = "Lazio",
                    ZipCode = "00184",
                    Country = "Italy"
                },
                PricePerNight = 150.00m,
                Description = "Charming boutique hotel perched on a hilltop overlooking the majestic Roman Forum and Imperial Forums. The panoramic rooftop terrace offers stunning sunset views of ancient Rome's archaeological wonders. Rooms feature classic Italian decor with modern comforts, complimentary high-speed WiFi, and quality linens. The on-site restaurant specializes in traditional Roman cuisine with locally sourced ingredients. Ideal for couples and culture lovers seeking an intimate, authentic Roman experience in a historic setting."
            },
            new Accommodation
            {
                Id = "3",
                Name = "Palazzo Manfredi",
                Type = AccommodationType.Hotel,
                Rating = 4.9,
                Amenities = ["parking", "room-service", "breakfast", "wifi", "gym", "spa", "restaurant", "pool"],
                Position = new Location { Latitude = 41.8897, Longitude = 12.4964 },
                Address = new Address
                {
                    Street = "Via Labicana 125",
                    City = "Rome",
                    State = "Lazio",
                    ZipCode = "00184",
                    Country = "Italy"
                },
                PricePerNight = 450.00m,
                Description = "Five-star luxury boutique hotel offering unparalleled exclusive views of the Colosseum from every room and the Michelin-starred rooftop restaurant Aroma. This intimate palace features only 18 elegantly designed suites with bespoke furnishings, Italian marble bathrooms with chromotherapy showers, and state-of-the-art technology. Guests enjoy personalized butler service, a tranquil spa with Roman-inspired treatments, an outdoor pool, and valet parking. The hotel seamlessly blends Renaissance elegance with contemporary luxury, catering to discerning travelers seeking the ultimate Roman experience."
            },
            
            // B&Bs in Rome
            new Accommodation
            {
                Id = "4",
                Name = "Colosseum B&B",
                Type = AccommodationType.BedAndBreakfast,
                Rating = 4.5,
                Amenities = ["breakfast", "wifi", "parking"],
                Position = new Location { Latitude = 41.8905, Longitude = 12.4930 },
                Address = new Address
                {
                    Street = "Via Capo d'Africa 21",
                    City = "Rome",
                    State = "Lazio",
                    ZipCode = "00184",
                    Country = "Italy"
                },
                PricePerNight = 75.00m,
                Description = "Cozy family-run bed and breakfast located just 150 steps from the Colosseum, offering warm Italian hospitality in a residential neighborhood. Each morning, guests enjoy a generous homemade breakfast featuring fresh pastries, local cheeses, seasonal fruits, and Italian espresso. The bright, comfortable rooms are decorated with traditional Italian furnishings and equipped with air conditioning and free WiFi. Street parking is available nearby. Perfect for budget-conscious travelers who want proximity to major attractions while experiencing authentic Roman living. The friendly owners provide personalized recommendations for local restaurants and hidden gems."
            },
            new Accommodation
            {
                Id = "5",
                Name = "Trastevere Hideaway",
                Type = AccommodationType.BedAndBreakfast,
                Rating = 4.7,
                Amenities = ["breakfast", "wifi", "air-conditioning"],
                Position = new Location { Latitude = 41.8899, Longitude = 12.4707 },
                Address = new Address
                {
                    Street = "Via della Paglia 15",
                    City = "Rome",
                    State = "Lazio",
                    ZipCode = "00153",
                    Country = "Italy"
                },
                PricePerNight = 65.00m,
                Description = "Charming bed and breakfast nestled in the vibrant, bohemian Trastevere neighborhood, known for its cobblestone streets, artisan shops, and lively trattorias. This restored 17th-century building offers uniquely decorated rooms with original frescoed ceilings, terracotta floors, and antique furnishings that capture Rome's artistic soul. Guests savor authentic Roman breakfast with homemade jams and fresh bread on a sunny courtyard terrace. The area comes alive at night with street musicians and local wine bars. Ideal for travelers seeking an immersive cultural experience in Rome's most authentic neighborhood, away from tourist crowds."
            },
            
            // Hotels in Latina
            new Accommodation
            {
                Id = "6",
                Name = "Hotel Latina",
                Type = AccommodationType.Hotel,
                Rating = 4.2,
                Amenities = ["breakfast", "wifi", "parking", "restaurant", "bar"],
                Position = new Location { Latitude = 41.4677, Longitude = 12.9037 },
                Address = new Address
                {
                    Street = "Viale Kennedy 50",
                    City = "Latina",
                    State = "Lazio",
                    ZipCode = "04100",
                    Country = "Italy"
                },
                PricePerNight = 85.00m,
                Description = "Modern business-oriented hotel in the heart of Latina's city center, ideal for both corporate travelers and tourists exploring the region. Features contemporary, functional rooms with work desks, complimentary WiFi, and soundproofing for a peaceful stay. The on-site restaurant serves Mediterranean cuisine with a focus on regional Lazio specialties, while the bar offers a relaxed atmosphere for evening drinks. Ample free parking available. Conveniently located within walking distance of Latina's main shopping district, government offices, and the train station for day trips to Rome or the nearby coastal towns."
            },
            new Accommodation
            {
                Id = "7",
                Name = "Park Hotel",
                Type = AccommodationType.Hotel,
                Rating = 4.4,
                Amenities = ["breakfast", "wifi", "parking", "gym", "restaurant", "pool"],
                Position = new Location { Latitude = 41.4701, Longitude = 12.9049 },
                Address = new Address
                {
                    Street = "Via Isonzo 45",
                    City = "Latina",
                    State = "Lazio",
                    ZipCode = "04100",
                    Country = "Italy"
                },
                PricePerNight = 110.00m,
                Description = "Elegant four-star hotel set in a tranquil location with beautifully landscaped gardens and mature trees. The property features a seasonal outdoor swimming pool surrounded by sun loungers, a modern wellness center with sauna and spa treatments, and a well-equipped fitness room. Spacious rooms blend classic Italian style with modern comfort, featuring balconies overlooking the gardens. The gourmet restaurant emphasizes farm-to-table cuisine using ingredients from local producers. Perfect for families and wellness seekers looking for a relaxing retreat while maintaining easy access to both Latina city center and the pristine beaches of the Tyrrhenian coast."
            },
            
            // Budget-friendly options
            new Accommodation
            {
                Id = "8",
                Name = "Hostel Roma",
                Type = AccommodationType.Hostel,
                Rating = 4.0,
                Amenities = ["wifi", "breakfast", "shared-kitchen"],
                Position = new Location { Latitude = 41.9028, Longitude = 12.4964 },
                Address = new Address
                {
                    Street = "Via Castro Pretorio 25",
                    City = "Rome",
                    State = "Lazio",
                    ZipCode = "00185",
                    Country = "Italy"
                },
                PricePerNight = 30.00m,
                Description = "Budget-friendly hostel strategically located near Termini Central Station, Rome's main transportation hub, making it ideal for backpackers and budget travelers. Offers both dormitory beds and private rooms, all maintained to high cleanliness standards. Features include a fully equipped communal kitchen where guests can prepare meals, free WiFi throughout, lockers for secure storage, and a common area perfect for meeting fellow travelers. Continental breakfast included. The multilingual staff organizes regular social events and walking tours. Located in a safe neighborhood with easy access to metro lines connecting to all major attractions, supermarkets, and affordable eateries."
            },
            new Accommodation
            {
                Id = "9",
                Name = "Budget Inn Rome",
                Type = AccommodationType.Hotel,
                Rating = 3.8,
                Amenities = ["wifi", "breakfast"],
                Position = new Location { Latitude = 41.8980, Longitude = 12.4872 },
                Address = new Address
                {
                    Street = "Via Nazionale 230",
                    City = "Rome",
                    State = "Lazio",
                    ZipCode = "00184",
                    Country = "Italy"
                },
                PricePerNight = 45.00m,
                Description = "Affordable no-frills hotel on Via Nazionale, one of Rome's main shopping streets connecting Termini Station to Piazza Venezia. Provides clean, basic accommodations perfect for travelers prioritizing location over luxury. Rooms are compact but functional, equipped with essential amenities including air conditioning, free WiFi, and private bathrooms. Continental breakfast served in a simple dining room. The exceptional central location allows guests to walk to the Colosseum, Trevi Fountain, and Spanish Steps within 15-20 minutes. Surrounded by restaurants, cafes, and shops offering various price points. Excellent value for money for independent travelers and those spending most time sightseeing."
            },
            
            // More variety
            new Accommodation
            {
                Id = "10",
                Name = "Vatican View B&B",
                Type = AccommodationType.BedAndBreakfast,
                Rating = 4.6,
                Amenities = ["breakfast", "wifi", "parking", "air-conditioning"],
                Position = new Location { Latitude = 41.9029, Longitude = 12.4534 },
                Address = new Address
                {
                    Street = "Via Germanico 198",
                    City = "Rome",
                    State = "Lazio",
                    ZipCode = "00192",
                    Country = "Italy"
                },
                PricePerNight = 80.00m,
                Description = "Lovely bed and breakfast in the prestigious Prati district, offering stunning views of St. Peter's Basilica dome from the terrace breakfast area. This elegant guesthouse occupies the top floor of a classic Roman building with an elevator, featuring beautifully decorated rooms that combine antique furniture with modern comforts. The abundant breakfast spread includes fresh Italian pastries, premium coffee, homemade cakes, and organic products. Located in a quiet residential area just 500 meters from the Vatican Museums, surrounded by authentic local markets, artisan gelaterias, and family-owned restaurants. Secure parking garage available. Perfect for pilgrims, art enthusiasts, and visitors seeking a peaceful base near Vatican City."
            },
            new Accommodation
            {
                Id = "11",
                Name = "Pantheon Suites",
                Type = AccommodationType.Boutique,
                Rating = 4.8,
                Amenities = ["wifi", "room-service", "breakfast", "bar", "concierge"],
                Position = new Location { Latitude = 41.8986, Longitude = 12.4768 },
                Address = new Address
                {
                    Street = "Piazza della Rotonda 73",
                    City = "Rome",
                    State = "Lazio",
                    ZipCode = "00186",
                    Country = "Italy"
                },
                PricePerNight = 280.00m,
                Description = "Exclusive boutique hotel occupying a prime position directly on the iconic Piazza della Rotonda, with front-facing rooms offering spectacular views of the ancient Pantheon, one of Rome's most perfectly preserved monuments. This luxurious property features individually designed suites with high frescoed ceilings, designer furnishings, rainfall showers, and Nespresso machines. The attentive concierge team arranges private tours, restaurant reservations at Rome's finest establishments, and exclusive experiences. Gourmet breakfast served in-room or on the panoramic terrace. The location is unbeatable—steps from Piazza Navona, Trevi Fountain, and surrounded by high-end boutiques, historic cafes, and artisan workshops. Ideal for romantic getaways and luxury travelers."
            },
            new Accommodation
            {
                Id = "12",
                Name = "Termini Budget Hotel",
                Type = AccommodationType.Hotel,
                Rating = 3.9,
                Amenities = ["wifi", "breakfast", "24-hour-reception"],
                Position = new Location { Latitude = 41.9008, Longitude = 12.5015 },
                Address = new Address
                {
                    Street = "Via Marsala 80",
                    City = "Rome",
                    State = "Lazio",
                    ZipCode = "00185",
                    Country = "Italy"
                },
                PricePerNight = 55.00m,
                Description = "Simple, practical hotel located directly across from Termini Railway Station, Rome's central transportation hub for trains, buses, and metro lines. Ideal for travelers with early departures, late arrivals, or those planning day trips to Naples, Florence, or other Italian cities. Rooms are basic but clean, equipped with essential amenities including air conditioning, private bathroom, and free WiFi. The 24-hour reception provides flexibility for any arrival time and luggage storage. While lacking luxury touches, the hotel offers unbeatable convenience for exploring Rome via public transport, with direct metro access to all major sites. Surrounded by numerous budget restaurants, grocery stores, and travel services."
            }
        };
    }

    public List<Accommodation> GetAllAccommodations()
    {
        return _accommodations;
    }

    public List<Accommodation> SearchAccommodations(
        double? minRating = null,
        string? city = null,
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

        // Filter by city
        if (!string.IsNullOrWhiteSpace(city))
        {
            results = results.Where(a => a.Address.City.Equals(city, StringComparison.OrdinalIgnoreCase));
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
