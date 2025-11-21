using AccommodationAgent.Models;

namespace AccommodationAgent.Services;

public interface IAccommodationService
{
    List<Accommodation> GetAllAccommodations();
    List<Accommodation> SearchAccommodations(
        double? minRating = null,
        string? city = null,
        double? latitude = null,
        double? longitude = null,
        double? maxDistanceKm = 1.0,
        List<string>? amenities = null,
        decimal? maxPricePerNight = null,
        AccommodationType? type = null);
}
