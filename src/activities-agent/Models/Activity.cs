namespace ActivitiesAgent.Models;

public class Activity
{
    public required string Name { get; set; }
    public required string Category { get; set; }
    public required string Description { get; set; }
    public required string Location { get; set; }
    public required Position Position { get; set; }
    public required string Address { get; set; }
    public required string Hours { get; set; }
    public required string AvailableDates { get; set; }
    public required List<PricingTier> PricingTiers { get; set; }
    public required List<string> Restrictions { get; set; }
    public required AccessibilityInfo Accessibility { get; set; }
    public required double Rating { get; set; }
    public required List<UserReview> Reviews { get; set; }
}

public class Position
{
    public required double Latitude { get; set; }
    public required double Longitude { get; set; }
}

public class PricingTier
{
    public required string Type { get; set; }
    public required decimal Price { get; set; }
    public required string Currency { get; set; }
}

public class AccessibilityInfo
{
    public required bool WheelchairAccessible { get; set; }
    public required bool AudioGuideAvailable { get; set; }
    public required bool SignLanguageSupport { get; set; }
    public required string AdditionalInfo { get; set; }
}

public class UserReview
{
    public required string Username { get; set; }
    public required int Stars { get; set; }
    public required string Comment { get; set; }
    public required string Date { get; set; }
}
