using ActivitiesAgent.Models;

namespace ActivitiesAgent.Services;

public class ActivitiesService
{
    private readonly List<Activity> _activities;

    public ActivitiesService()
    {
        _activities = new List<Activity>
        {
            // MUSEUMS (10 activities)
            new Activity
            {
                Name = "City Art Museum",
                Category = "museums",
                Description = "World-renowned art museum featuring masterpieces from the Renaissance to contemporary art, including works by Monet, Van Gogh, and Picasso.",
                Location = "Downtown Cultural District",
                Position = new Position { Latitude = 41.9028, Longitude = 12.4964 },
                Address = "100 Museum Boulevard, Downtown",
                Hours = "Tuesday-Sunday: 10:00 AM - 6:00 PM, Closed Mondays",
                AvailableDates = "Year-round, check website for special exhibitions",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 25.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior (65+)", Price = 20.00m, Currency = "USD" },
                    new PricingTier { Type = "Student", Price = 15.00m, Currency = "USD" },
                    new PricingTier { Type = "Child (under 12)", Price = 0.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No photography with flash", "No large bags", "No food or drinks" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Wheelchairs available at entrance, service animals welcome"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "ArtLover123", Stars = 5, Comment = "Incredible collection! Spent the whole day here.", Date = "2024-01-15" },
                    new UserReview { Username = "TravelBug", Stars = 4, Comment = "Great museum but can get crowded on weekends.", Date = "2024-01-20" }
                }
            },
            new Activity
            {
                Name = "Natural History Museum",
                Category = "museums",
                Description = "Explore millions of years of natural history with dinosaur fossils, gemstones, and interactive exhibits about the evolution of life on Earth.",
                Location = "Science Park",
                Position = new Position { Latitude = 41.92, Longitude = 12.51 },
                Address = "250 Science Avenue, North Side",
                Hours = "Monday-Sunday: 9:00 AM - 5:00 PM",
                AvailableDates = "Open daily except Thanksgiving and Christmas",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 22.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior (60+)", Price = 18.00m, Currency = "USD" },
                    new PricingTier { Type = "Student", Price = 12.00m, Currency = "USD" },
                    new PricingTier { Type = "Child (under 5)", Price = 0.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No touching exhibits", "Children must be supervised" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Accessible restrooms on all floors, elevator access"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "DinoFan", Stars = 5, Comment = "Kids loved the T-Rex exhibit!", Date = "2024-02-01" },
                    new UserReview { Username = "MuseumHopper", Stars = 5, Comment = "Educational and fun for all ages.", Date = "2024-02-05" }
                }
            },
            new Activity
            {
                Name = "Modern Art Gallery",
                Category = "museums",
                Description = "Contemporary art space showcasing cutting-edge installations, digital art, and works by emerging artists from around the world.",
                Location = "Arts District",
                Position = new Position { Latitude = 41.898, Longitude = 12.476 },
                Address = "45 Contemporary Lane, Arts District",
                Hours = "Wednesday-Monday: 11:00 AM - 7:00 PM, Closed Tuesdays",
                AvailableDates = "Year-round with rotating exhibitions every 3 months",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "General Admission", Price = 18.00m, Currency = "USD" },
                    new PricingTier { Type = "Student/Senior", Price = 12.00m, Currency = "USD" },
                    new PricingTier { Type = "Free Thursday Evenings", Price = 0.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No photography allowed", "Quiet zone - minimal noise" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Sensory-friendly hours first Sunday of each month"
                },
                Rating = 4.5,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "ModernArtFan", Stars = 5, Comment = "Thought-provoking exhibits!", Date = "2024-01-10" },
                    new UserReview { Username = "CriticEye", Stars = 4, Comment = "Interesting pieces but small venue.", Date = "2024-01-25" }
                }
            },
            new Activity
            {
                Name = "Historical Society Museum",
                Category = "museums",
                Description = "Journey through local history from indigenous peoples to modern times with artifacts, photographs, and interactive displays.",
                Location = "Old Town",
                Position = new Position { Latitude = 41.896, Longitude = 12.485 },
                Address = "789 Heritage Street, Old Town",
                Hours = "Tuesday-Saturday: 10:00 AM - 4:00 PM",
                AvailableDates = "Open year-round, closed major holidays",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 12.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior", Price = 10.00m, Currency = "USD" },
                    new PricingTier { Type = "Child (6-17)", Price = 8.00m, Currency = "USD" },
                    new PricingTier { Type = "Family Pass (4)", Price = 30.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No flash photography", "Supervised children only" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Large print materials available"
                },
                Rating = 4.3,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "HistoryBuff", Stars = 5, Comment = "Fascinating local history!", Date = "2024-02-10" },
                    new UserReview { Username = "TeacherSue", Stars = 4, Comment = "Great for school field trips.", Date = "2024-02-15" }
                }
            },
            new Activity
            {
                Name = "Science & Technology Museum",
                Category = "museums",
                Description = "Interactive science museum with hands-on exhibits covering robotics, space exploration, physics, and biology. Perfect for curious minds.",
                Location = "Innovation Quarter",
                Position = new Position { Latitude = 41.91, Longitude = 12.52 },
                Address = "300 Discovery Drive, Innovation Quarter",
                Hours = "Daily: 9:00 AM - 6:00 PM",
                AvailableDates = "Open every day of the year",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 28.00m, Currency = "USD" },
                    new PricingTier { Type = "Youth (3-17)", Price = 20.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior (65+)", Price = 24.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Active supervision required for children", "Some exhibits have height requirements" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Quiet room available, ASL interpreters on request"
                },
                Rating = 4.9,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "ScienceGeek", Stars = 5, Comment = "Amazing interactive exhibits!", Date = "2024-01-18" },
                    new UserReview { Username = "FamilyTraveler", Stars = 5, Comment = "Kids didn't want to leave!", Date = "2024-01-30" }
                }
            },
            new Activity
            {
                Name = "Maritime Museum",
                Category = "museums",
                Description = "Discover the city's nautical heritage with historic ships, navigation instruments, and tales of maritime adventure.",
                Location = "Harbor District",
                Position = new Position { Latitude = 41.75, Longitude = 12.28 },
                Address = "1 Wharf Street, Harbor District",
                Hours = "Monday-Friday: 10:00 AM - 5:00 PM, Saturday-Sunday: 9:00 AM - 6:00 PM",
                AvailableDates = "Open year-round",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 16.00m, Currency = "USD" },
                    new PricingTier { Type = "Child (under 16)", Price = 10.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior", Price = 14.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Weather may affect outdoor exhibits", "Sturdy footwear recommended for ship tours" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = false,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Historic ships have limited accessibility, main building is accessible"
                },
                Rating = 4.4,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "SailorJoe", Stars = 5, Comment = "Loved the ship tours!", Date = "2024-02-08" },
                    new UserReview { Username = "CoastalVisitor", Stars = 4, Comment = "Interesting history, beautiful harbor views.", Date = "2024-02-12" }
                }
            },
            new Activity
            {
                Name = "Children's Discovery Museum",
                Category = "museums",
                Description = "Playful learning environment designed for children ages 0-10 with creative play areas, art studios, and STEM activities.",
                Location = "Family Park",
                Position = new Position { Latitude = 41.915, Longitude = 12.49 },
                Address = "500 Playtime Boulevard, West End",
                Hours = "Monday-Saturday: 9:00 AM - 5:00 PM, Sunday: 12:00 PM - 5:00 PM",
                AvailableDates = "Open daily except major holidays",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Per Person (1+)", Price = 14.00m, Currency = "USD" },
                    new PricingTier { Type = "Infant (under 1)", Price = 0.00m, Currency = "USD" },
                    new PricingTier { Type = "Annual Membership", Price = 150.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Adults must accompany children", "Socks required in play areas" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Sensory-friendly mornings every Tuesday"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "MomOfThree", Stars = 5, Comment = "Perfect for toddlers and preschoolers!", Date = "2024-01-22" },
                    new UserReview { Username = "DadLife", Stars = 5, Comment = "Kids had a blast, very clean and safe.", Date = "2024-02-03" }
                }
            },
            new Activity
            {
                Name = "Photography Museum",
                Category = "museums",
                Description = "Dedicated to the art of photography from daguerreotypes to digital, featuring works by legendary photographers and emerging talents.",
                Location = "Cultural Center",
                Position = new Position { Latitude = 41.9, Longitude = 12.49 },
                Address = "88 Lens Avenue, Cultural Center",
                Hours = "Tuesday-Sunday: 11:00 AM - 6:00 PM",
                AvailableDates = "Rotating exhibitions every 2 months",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 15.00m, Currency = "USD" },
                    new PricingTier { Type = "Student/Senior", Price = 10.00m, Currency = "USD" },
                    new PricingTier { Type = "Member", Price = 0.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No flash photography", "Tripods not allowed" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Descriptive tours available by appointment"
                },
                Rating = 4.6,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "ShutterBug", Stars = 5, Comment = "Inspiring collection!", Date = "2024-01-12" },
                    new UserReview { Username = "VisualArtist", Stars = 4, Comment = "Beautiful space, great curation.", Date = "2024-01-28" }
                }
            },
            new Activity
            {
                Name = "Aviation & Space Museum",
                Category = "museums",
                Description = "Experience the thrill of flight with vintage aircraft, spacecraft, flight simulators, and astronaut artifacts.",
                Location = "Airport District",
                Position = new Position { Latitude = 41.8003, Longitude = 12.2389 },
                Address = "1000 Runway Road, Airport District",
                Hours = "Daily: 10:00 AM - 5:00 PM",
                AvailableDates = "Open year-round",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 24.00m, Currency = "USD" },
                    new PricingTier { Type = "Youth (5-17)", Price = 16.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior/Military", Price = 20.00m, Currency = "USD" },
                    new PricingTier { Type = "Under 5", Price = 0.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Flight simulator has height/weight restrictions", "Some exhibits require parental supervision" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Accessible viewing platforms for all aircraft"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "Pilot123", Stars = 5, Comment = "Amazing collection of aircraft!", Date = "2024-02-05" },
                    new UserReview { Username = "SpaceEnthusiast", Stars = 5, Comment = "The Apollo exhibit is incredible!", Date = "2024-02-14" }
                }
            },
            new Activity
            {
                Name = "Folk Art Museum",
                Category = "museums",
                Description = "Celebrating traditional crafts and folk art from diverse cultures, featuring textiles, pottery, woodwork, and traditional costumes.",
                Location = "Heritage Quarter",
                Position = new Position { Latitude = 41.895, Longitude = 12.478 },
                Address = "222 Tradition Lane, Heritage Quarter",
                Hours = "Wednesday-Sunday: 10:00 AM - 5:00 PM",
                AvailableDates = "Open most of the year, check for special workshops",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 10.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior/Student", Price = 7.00m, Currency = "USD" },
                    new PricingTier { Type = "Child (under 12)", Price = 5.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No touching artifacts", "Photography allowed without flash" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Braille descriptions available for select exhibits"
                },
                Rating = 4.2,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "CraftLover", Stars = 4, Comment = "Beautiful traditional crafts!", Date = "2024-01-20" },
                    new UserReview { Username = "CulturalExplorer", Stars = 4, Comment = "Great variety of cultural exhibits.", Date = "2024-02-02" }
                }
            },

            // THEATERS (10 activities)
            new Activity
            {
                Name = "Grand Opera House",
                Category = "theaters",
                Description = "Historic opera house hosting world-class opera, ballet, and classical music performances in an ornate 19th-century venue.",
                Location = "Theater District",
                Position = new Position { Latitude = 41.901, Longitude = 12.495 },
                Address = "1 Opera Plaza, Theater District",
                Hours = "Performance times vary, Box Office: Monday-Saturday 10:00 AM - 6:00 PM",
                AvailableDates = "Season runs September through June",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Orchestra", Price = 150.00m, Currency = "USD" },
                    new PricingTier { Type = "Mezzanine", Price = 100.00m, Currency = "USD" },
                    new PricingTier { Type = "Balcony", Price = 60.00m, Currency = "USD" },
                    new PricingTier { Type = "Standing Room", Price = 25.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Formal attire recommended", "No children under 5", "Late seating restrictions apply" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Assistive listening devices available, accessible seating in all levels"
                },
                Rating = 4.9,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "OperaFan", Stars = 5, Comment = "Stunning acoustics and beautiful venue!", Date = "2024-01-15" },
                    new UserReview { Username = "ClassicalLover", Stars = 5, Comment = "World-class performances!", Date = "2024-01-29" }
                }
            },
            new Activity
            {
                Name = "Shakespeare Theater",
                Category = "theaters",
                Description = "Dedicated to producing Shakespeare's plays and contemporary works, featuring both traditional and innovative productions.",
                Location = "Cultural District",
                Position = new Position { Latitude = 41.9028, Longitude = 12.4964 },
                Address = "42 Bard Street, Cultural District",
                Hours = "Shows: Tuesday-Sunday evenings, Matinees Saturday-Sunday",
                AvailableDates = "Year-round programming",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Premium", Price = 85.00m, Currency = "USD" },
                    new PricingTier { Type = "Standard", Price = 55.00m, Currency = "USD" },
                    new PricingTier { Type = "Student/Senior", Price = 35.00m, Currency = "USD" },
                    new PricingTier { Type = "Rush Tickets", Price = 20.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Recommended for ages 10+", "No late seating", "No photography" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Audio description available for select performances"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "TheaterGoer", Stars = 5, Comment = "Brilliant production of Hamlet!", Date = "2024-02-01" },
                    new UserReview { Username = "DramaTeacher", Stars = 4, Comment = "Excellent acting, creative staging.", Date = "2024-02-10" }
                }
            },
            new Activity
            {
                Name = "Comedy Club Central",
                Category = "theaters",
                Description = "Intimate comedy venue featuring stand-up comedians from local talent to internationally recognized headliners.",
                Location = "Entertainment District",
                Position = new Position { Latitude = 41.899, Longitude = 12.48 },
                Address = "123 Laugh Lane, Entertainment District",
                Hours = "Shows: Wednesday-Sunday, Doors open 7:00 PM",
                AvailableDates = "Shows every week, check schedule online",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "General Admission", Price = 30.00m, Currency = "USD" },
                    new PricingTier { Type = "VIP Seating", Price = 50.00m, Currency = "USD" },
                    new PricingTier { Type = "Open Mic Night", Price = 10.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Ages 18+ only", "2-drink minimum", "No heckling policy" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Accessible seating near stage, service animals welcome"
                },
                Rating = 4.5,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "LaughOutLoud", Stars = 5, Comment = "Hilarious shows every time!", Date = "2024-01-25" },
                    new UserReview { Username = "ComedyFan", Stars = 4, Comment = "Great atmosphere, talented comedians.", Date = "2024-02-05" }
                }
            },
            new Activity
            {
                Name = "Broadway Theater",
                Category = "theaters",
                Description = "Premier venue for touring Broadway musicals and original productions, featuring elaborate sets and professional casts.",
                Location = "Theater District",
                Position = new Position { Latitude = 41.901, Longitude = 12.495 },
                Address = "5 Show Street, Theater District",
                Hours = "Varying show times, Box Office: Daily 10:00 AM - 8:00 PM",
                AvailableDates = "Rotating shows throughout the year",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Premium Orchestra", Price = 175.00m, Currency = "USD" },
                    new PricingTier { Type = "Orchestra", Price = 125.00m, Currency = "USD" },
                    new PricingTier { Type = "Mezzanine", Price = 95.00m, Currency = "USD" },
                    new PricingTier { Type = "Balcony", Price = 55.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No children under 4", "No cameras or recording devices" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Hearing loops available, companion seats for wheelchair users"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "MusicalLover", Stars = 5, Comment = "Hamilton was phenomenal!", Date = "2024-01-18" },
                    new UserReview { Username = "ShowTunes", Stars = 5, Comment = "Incredible production quality!", Date = "2024-02-02" }
                }
            },
            new Activity
            {
                Name = "Experimental Theater Collective",
                Category = "theaters",
                Description = "Avant-garde theater space showcasing experimental works, immersive performances, and new playwrights.",
                Location = "Arts District",
                Position = new Position { Latitude = 41.898, Longitude = 12.476 },
                Address = "77 Edge Avenue, Arts District",
                Hours = "Shows: Thursday-Sunday, Times vary by production",
                AvailableDates = "Multiple productions running simultaneously",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "General Admission", Price = 40.00m, Currency = "USD" },
                    new PricingTier { Type = "Student", Price = 25.00m, Currency = "USD" },
                    new PricingTier { Type = "Pay What You Can", Price = 0.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Content may not be suitable for all audiences", "Interactive elements - audience participation may be required" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Sensory warnings provided for each show"
                },
                Rating = 4.3,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "ArtAdventurer", Stars = 5, Comment = "Truly unique theatrical experiences!", Date = "2024-01-22" },
                    new UserReview { Username = "TheaterInnovator", Stars = 4, Comment = "Challenging and thought-provoking.", Date = "2024-02-08" }
                }
            },
            new Activity
            {
                Name = "Children's Playhouse",
                Category = "theaters",
                Description = "Theater dedicated to children's productions with interactive shows, fairy tales, and educational performances.",
                Location = "Family Entertainment Zone",
                Position = new Position { Latitude = 41.912, Longitude = 12.485 },
                Address = "200 Storytime Circle, West End",
                Hours = "Weekend matinees, Summer daily shows",
                AvailableDates = "Year-round with seasonal specials",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Child", Price = 18.00m, Currency = "USD" },
                    new PricingTier { Type = "Adult", Price = 20.00m, Currency = "USD" },
                    new PricingTier { Type = "Family Pack (4)", Price = 65.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Recommended for ages 3-12", "Adults must accompany children" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Sensory-friendly performances offered monthly"
                },
                Rating = 4.6,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "ParentOfTwo", Stars = 5, Comment = "Kids were mesmerized the entire show!", Date = "2024-01-30" },
                    new UserReview { Username = "GrandmaJoy", Stars = 5, Comment = "Perfect for young children!", Date = "2024-02-12" }
                }
            },
            new Activity
            {
                Name = "Improv Comedy Theater",
                Category = "theaters",
                Description = "Spontaneous comedy theater where every show is different, featuring improvisation games and audience suggestions.",
                Location = "Entertainment District",
                Position = new Position { Latitude = 41.899, Longitude = 12.48 },
                Address = "456 Spontaneous Street, Entertainment District",
                Hours = "Shows: Friday-Saturday 8:00 PM & 10:30 PM",
                AvailableDates = "Weekly shows year-round",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "General Admission", Price = 25.00m, Currency = "USD" },
                    new PricingTier { Type = "Student", Price = 18.00m, Currency = "USD" },
                    new PricingTier { Type = "Group (6+)", Price = 20.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Ages 16+", "Audience participation expected" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Accessible seating available, advance notice appreciated"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "ImprovFan", Stars = 5, Comment = "Never seen the same show twice!", Date = "2024-02-03" },
                    new UserReview { Username = "DateNightPro", Stars = 5, Comment = "So much fun, great for groups!", Date = "2024-02-15" }
                }
            },
            new Activity
            {
                Name = "Historic Bijou Theater",
                Category = "theaters",
                Description = "Beautifully restored 1920s theater showing classic films, hosting live music, and special cinema events.",
                Location = "Downtown Historic District",
                Position = new Position { Latitude = 41.9028, Longitude = 12.4964 },
                Address = "33 Vintage Boulevard, Downtown",
                Hours = "Showtimes vary, generally 7:00 PM & 9:30 PM",
                AvailableDates = "Open year-round with themed film festivals",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "General Admission", Price = 12.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior/Student", Price = 9.00m, Currency = "USD" },
                    new PricingTier { Type = "Member", Price = 8.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No outside food/drinks", "Film ratings apply" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Assistive listening devices available"
                },
                Rating = 4.4,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "ClassicCinemaFan", Stars = 5, Comment = "Love the vintage atmosphere!", Date = "2024-01-20" },
                    new UserReview { Username = "MovieBuff", Stars = 4, Comment = "Great classic film selection.", Date = "2024-02-01" }
                }
            },
            new Activity
            {
                Name = "Black Box Studio Theater",
                Category = "theaters",
                Description = "Intimate 99-seat theater specializing in contemporary plays, solo performances, and workshops.",
                Location = "Arts Quarter",
                Position = new Position { Latitude = 41.897, Longitude = 12.475 },
                Address = "11 Performance Place, Arts Quarter",
                Hours = "Shows: Thursday-Sunday, 8:00 PM",
                AvailableDates = "Programming changes monthly",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "General", Price = 35.00m, Currency = "USD" },
                    new PricingTier { Type = "Student/Artist", Price = 20.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Ages 13+", "No late seating", "Intimate setting - be prepared for close proximity" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Small venue with flexible seating arrangements"
                },
                Rating = 4.5,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "TheaterPro", Stars = 5, Comment = "Powerful intimate performances!", Date = "2024-01-28" },
                    new UserReview { Username = "DramaStudent", Stars = 4, Comment = "Great venue for emerging artists.", Date = "2024-02-10" }
                }
            },
            new Activity
            {
                Name = "Outdoor Amphitheater",
                Category = "theaters",
                Description = "Open-air venue for concerts, Shakespeare under the stars, and summer movie nights with stunning city views.",
                Location = "Hillside Park",
                Position = new Position { Latitude = 41.918, Longitude = 12.482 },
                Address = "1 Scenic Vista Drive, Hillside Park",
                Hours = "Seasonal: May-September, Times vary by event",
                AvailableDates = "Summer season only",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Reserved Seating", Price = 45.00m, Currency = "USD" },
                    new PricingTier { Type = "Lawn Seating", Price = 25.00m, Currency = "USD" },
                    new PricingTier { Type = "Family Lawn Pass", Price = 75.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Weather dependent", "No outside alcohol", "Blankets and low chairs allowed on lawn" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Accessible seating area, shuttle service from parking"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "SummerNights", Stars = 5, Comment = "Magical evening under the stars!", Date = "2024-06-15" },
                    new UserReview { Username = "ConcertGoer", Stars = 5, Comment = "Beautiful venue, great acoustics!", Date = "2024-07-20" }
                }
            },

            // CULTURAL EVENTS (10 activities)
            new Activity
            {
                Name = "Annual Food & Wine Festival",
                Category = "cultural_events",
                Description = "Three-day celebration featuring local chefs, international cuisine, wine tastings, and cooking demonstrations.",
                Location = "Waterfront Park",
                Position = new Position { Latitude = 41.76, Longitude = 12.29 },
                Address = "100 Harbor View, Waterfront",
                Hours = "Friday-Sunday: 11:00 AM - 10:00 PM",
                AvailableDates = "First weekend of October",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Single Day Pass", Price = 75.00m, Currency = "USD" },
                    new PricingTier { Type = "Weekend Pass", Price = 180.00m, Currency = "USD" },
                    new PricingTier { Type = "VIP Experience", Price = 350.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Ages 21+ for wine tastings", "Sampling tickets sold separately", "No outside food/beverages" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Paved pathways, accessible restrooms, quiet area available"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "Foodie123", Stars = 5, Comment = "Amazing variety of cuisines!", Date = "2023-10-08" },
                    new UserReview { Username = "WineLover", Stars = 4, Comment = "Great wine selection, but crowded.", Date = "2023-10-07" }
                }
            },
            new Activity
            {
                Name = "International Film Festival",
                Category = "cultural_events",
                Description = "Ten-day festival showcasing independent films, documentaries, and world cinema with Q&As and panel discussions.",
                Location = "Multiple Theaters Downtown",
                Position = new Position { Latitude = 41.902, Longitude = 12.496 },
                Address = "Various Locations, Festival Hub at 50 Cinema Square",
                Hours = "Screenings throughout the day, 10:00 AM - 11:00 PM",
                AvailableDates = "Mid-November, 10 days",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Single Screening", Price = 15.00m, Currency = "USD" },
                    new PricingTier { Type = "10-Film Pass", Price = 120.00m, Currency = "USD" },
                    new PricingTier { Type = "All-Access Pass", Price = 350.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Film ratings apply", "Reserved seating", "No late admission" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Closed captioning available for select screenings"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "CinephilePro", Stars = 5, Comment = "Incredible selection of films!", Date = "2023-11-20" },
                    new UserReview { Username = "DocuFan", Stars = 5, Comment = "Loved the documentary lineup!", Date = "2023-11-18" }
                }
            },
            new Activity
            {
                Name = "Street Art Festival",
                Category = "cultural_events",
                Description = "Annual celebration of urban art with live mural painting, graffiti workshops, street performances, and art markets.",
                Location = "Arts District",
                Position = new Position { Latitude = 41.898, Longitude = 12.476 },
                Address = "Entire Arts District neighborhood",
                Hours = "Saturday-Sunday: 10:00 AM - 8:00 PM",
                AvailableDates = "Last weekend of May",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Free Entry", Price = 0.00m, Currency = "USD" },
                    new PricingTier { Type = "Workshop Registration", Price = 35.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Some workshops require pre-registration", "Street closures in effect" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Outdoor event, accessible routes marked"
                },
                Rating = 4.6,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "UrbanArtist", Stars = 5, Comment = "Inspiring and vibrant!", Date = "2024-05-27" },
                    new UserReview { Username = "FamilyFun", Stars = 4, Comment = "Kids loved watching the artists!", Date = "2024-05-26" }
                }
            },
            new Activity
            {
                Name = "Jazz & Blues Festival",
                Category = "cultural_events",
                Description = "Four-day music festival featuring legendary jazz musicians, emerging blues artists, and late-night jam sessions.",
                Location = "Riverside Park",
                Position = new Position { Latitude = 41.905, Longitude = 12.47 },
                Address = "75 River Road, Riverside Park",
                Hours = "Thursday-Sunday: 2:00 PM - 11:00 PM",
                AvailableDates = "Third weekend in July",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Single Day", Price = 65.00m, Currency = "USD" },
                    new PricingTier { Type = "4-Day Pass", Price = 200.00m, Currency = "USD" },
                    new PricingTier { Type = "VIP with Seating", Price = 450.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No outside chairs in general admission", "Clear bag policy", "No pets except service animals" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "ADA viewing area, accessible parking with permit"
                },
                Rating = 4.9,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "JazzEnthusiast", Stars = 5, Comment = "Best lineup in years!", Date = "2024-07-21" },
                    new UserReview { Username = "BluesFan", Stars = 5, Comment = "Amazing atmosphere!", Date = "2024-07-20" }
                }
            },
            new Activity
            {
                Name = "Cultural Heritage Parade",
                Category = "cultural_events",
                Description = "Colorful parade celebrating the city's diverse communities with traditional costumes, music, dance, and cultural displays.",
                Location = "Main Street to City Hall",
                Position = new Position { Latitude = 41.9028, Longitude = 12.4964 },
                Address = "Parade route starts at 1st & Main Street",
                Hours = "Saturday: 10:00 AM - 3:00 PM",
                AvailableDates = "Second Saturday in June",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Free Viewing", Price = 0.00m, Currency = "USD" },
                    new PricingTier { Type = "VIP Bleacher Seating", Price = 40.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Street closures from 8:00 AM - 4:00 PM", "No alcohol on parade route" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Designated accessible viewing areas along route"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "CultureExplorer", Stars = 5, Comment = "Beautiful celebration of diversity!", Date = "2024-06-08" },
                    new UserReview { Username = "FamilyFirst", Stars = 5, Comment = "Kids had so much fun!", Date = "2024-06-08" }
                }
            },
            new Activity
            {
                Name = "Winter Lights Festival",
                Category = "cultural_events",
                Description = "Magical light installations and projections throughout the city during the winter season, featuring local and international artists.",
                Location = "City-wide",
                Position = new Position { Latitude = 41.9028, Longitude = 12.4964 },
                Address = "Multiple locations, maps available at Visitor Center",
                Hours = "Daily: 5:00 PM - 11:00 PM",
                AvailableDates = "December 1 - January 15",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Free to View", Price = 0.00m, Currency = "USD" },
                    new PricingTier { Type = "Guided Tour", Price = 30.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Weather dependent", "Some installations in pedestrian-only zones" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Most installations at ground level, accessible routes provided"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "WinterMagic", Stars = 5, Comment = "Absolutely stunning displays!", Date = "2023-12-20" },
                    new UserReview { Username = "PhotoHunter", Stars = 5, Comment = "Perfect for photography!", Date = "2023-12-15" }
                }
            },
            new Activity
            {
                Name = "Book Festival & Literary Fair",
                Category = "cultural_events",
                Description = "Three-day festival celebrating literature with author readings, book signings, writing workshops, and independent publishers.",
                Location = "Convention Center",
                Position = new Position { Latitude = 41.9, Longitude = 12.498 },
                Address = "500 Convention Plaza, Downtown",
                Hours = "Friday: 4:00 PM - 9:00 PM, Saturday-Sunday: 10:00 AM - 6:00 PM",
                AvailableDates = "First weekend in April",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "General Admission", Price = 15.00m, Currency = "USD" },
                    new PricingTier { Type = "3-Day Pass", Price = 35.00m, Currency = "USD" },
                    new PricingTier { Type = "Premium (includes workshops)", Price = 75.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Some author events require separate tickets", "Workshop capacity limited" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Large print programs available, quiet reading areas"
                },
                Rating = 4.6,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "BookWorm", Stars = 5, Comment = "Met my favorite author!", Date = "2024-04-06" },
                    new UserReview { Username = "WriterWannabe", Stars = 4, Comment = "Great workshops for aspiring writers.", Date = "2024-04-07" }
                }
            },
            new Activity
            {
                Name = "Outdoor Sculpture Exhibition",
                Category = "cultural_events",
                Description = "Summer-long outdoor exhibition featuring large-scale sculptures by renowned artists placed throughout city parks.",
                Location = "Multiple Parks",
                Position = new Position { Latitude = 41.908, Longitude = 12.488 },
                Address = "See exhibition map at Tourism Office",
                Hours = "Dawn to dusk, self-guided",
                AvailableDates = "June 1 - September 30",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Free to View", Price = 0.00m, Currency = "USD" },
                    new PricingTier { Type = "Audio Guide App", Price = 5.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Do not climb on sculptures", "Photography encouraged" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "All sculptures accessible via paved paths"
                },
                Rating = 4.5,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "SculptureLover", Stars = 5, Comment = "Beautiful art in beautiful settings!", Date = "2024-07-10" },
                    new UserReview { Username = "MorningWalker", Stars = 4, Comment = "Great addition to my morning walks.", Date = "2024-06-15" }
                }
            },
            new Activity
            {
                Name = "Folk Music & Dance Festival",
                Category = "cultural_events",
                Description = "Weekend celebration of traditional music and dance from around the world with performances, workshops, and participatory dancing.",
                Location = "Heritage Village",
                Position = new Position { Latitude = 41.894, Longitude = 12.477 },
                Address = "100 Tradition Way, Heritage Village",
                Hours = "Saturday-Sunday: 11:00 AM - 10:00 PM",
                AvailableDates = "First weekend in September",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Day Pass", Price = 25.00m, Currency = "USD" },
                    new PricingTier { Type = "Weekend Pass", Price = 40.00m, Currency = "USD" },
                    new PricingTier { Type = "Workshop Bundle", Price = 60.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Comfortable shoes recommended for dance workshops", "Some workshops require pre-registration" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Accessible seating areas, some workshops adaptable"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "FolkDancer", Stars = 5, Comment = "Learned so many new dances!", Date = "2024-09-07" },
                    new UserReview { Username = "MusicTradition", Stars = 5, Comment = "Authentic and joyful!", Date = "2024-09-08" }
                }
            },
            new Activity
            {
                Name = "Science & Technology Expo",
                Category = "cultural_events",
                Description = "Interactive expo showcasing latest innovations, robotics competitions, VR experiences, and talks by tech leaders.",
                Location = "Convention Center",
                Position = new Position { Latitude = 41.9, Longitude = 12.498 },
                Address = "500 Convention Plaza, Downtown",
                Hours = "Friday-Sunday: 9:00 AM - 7:00 PM",
                AvailableDates = "Last weekend in March",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 30.00m, Currency = "USD" },
                    new PricingTier { Type = "Student", Price = 20.00m, Currency = "USD" },
                    new PricingTier { Type = "Family (4)", Price = 85.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Some VR experiences have age restrictions", "Photography allowed except in demo areas" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Accessible tech demos, sensory-friendly hours available"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "TechEnthusiast", Stars = 5, Comment = "Mind-blowing innovations!", Date = "2024-03-30" },
                    new UserReview { Username = "FutureScientist", Stars = 5, Comment = "Inspired my career path!", Date = "2024-03-29" }
                }
            },

            // ATTRACTIONS (10 activities)
            new Activity
            {
                Name = "City Observation Tower",
                Category = "attractions",
                Description = "360-degree views from the tallest building in the city. Features glass floor viewing platform, interactive exhibits, and sunset viewing lounge.",
                Location = "Downtown Financial District",
                Position = new Position { Latitude = 41.904, Longitude = 12.497 },
                Address = "1 Skyline Plaza, Floor 75, Downtown",
                Hours = "Daily: 9:00 AM - 11:00 PM (last entry 10:30 PM)",
                AvailableDates = "Open year-round",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 35.00m, Currency = "USD" },
                    new PricingTier { Type = "Child (3-12)", Price = 25.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior (65+)", Price = 30.00m, Currency = "USD" },
                    new PricingTier { Type = "Sunset Experience", Price = 45.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Height restrictions for glass floor", "No tripods", "Weather may affect visibility" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Elevator access, tactile models of cityscape"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "SkyHigh", Stars = 5, Comment = "Breathtaking views!", Date = "2024-02-10" },
                    new UserReview { Username = "TouristPro", Stars = 4, Comment = "A must-visit, but buy tickets online to skip the line.", Date = "2024-02-15" }
                }
            },
            new Activity
            {
                Name = "Botanical Gardens",
                Category = "attractions",
                Description = "50-acre garden featuring themed sections including Japanese garden, rose garden, tropical conservatory, and butterfly pavilion.",
                Location = "Garden District",
                Position = new Position { Latitude = 41.915, Longitude = 12.485 },
                Address = "1000 Garden Way, Garden District",
                Hours = "Daily: 9:00 AM - 5:00 PM (until 8:00 PM in summer)",
                AvailableDates = "Open year-round, special seasonal displays",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 18.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior/Student", Price = 15.00m, Currency = "USD" },
                    new PricingTier { Type = "Child (under 12)", Price = 10.00m, Currency = "USD" },
                    new PricingTier { Type = "Family Pass", Price = 45.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No picking flowers", "Stay on designated paths", "No pets except service animals" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Paved pathways throughout, wheelchairs available for loan"
                },
                Rating = 4.9,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "NatureLover", Stars = 5, Comment = "Peaceful and beautiful!", Date = "2024-01-25" },
                    new UserReview { Username = "Photographer", Stars = 5, Comment = "Perfect for photography, especially in spring!", Date = "2024-02-01" }
                }
            },
            new Activity
            {
                Name = "Historic Downtown Walking Tour",
                Category = "attractions",
                Description = "Guided 2-hour walking tour through historic downtown, covering architecture, local legends, and important historical sites.",
                Location = "Starts at City Hall",
                Position = new Position { Latitude = 41.9028, Longitude = 12.4964 },
                Address = "City Hall Plaza, 1 Civic Center",
                Hours = "Tours daily at 10:00 AM, 2:00 PM, and 6:00 PM",
                AvailableDates = "Year-round, weather permitting",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 25.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior/Student", Price = 20.00m, Currency = "USD" },
                    new PricingTier { Type = "Child (6-16)", Price = 15.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Comfortable walking shoes required", "Approximately 1.5 miles of walking", "Reservations recommended" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Accessible route available, advance notice appreciated"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "HistoryFan", Stars = 5, Comment = "Excellent guide, learned so much!", Date = "2024-01-30" },
                    new UserReview { Username = "FirstVisitor", Stars = 5, Comment = "Perfect introduction to the city!", Date = "2024-02-05" }
                }
            },
            new Activity
            {
                Name = "City Zoo & Aquarium",
                Category = "attractions",
                Description = "World-class zoo and aquarium featuring over 3,000 animals, interactive exhibits, feeding experiences, and conservation programs.",
                Location = "North Park",
                Position = new Position { Latitude = 41.925, Longitude = 12.505 },
                Address = "2000 Wildlife Drive, North Park",
                Hours = "Daily: 9:00 AM - 6:00 PM (until 8:00 PM in summer)",
                AvailableDates = "Open year-round except Christmas Day",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 32.00m, Currency = "USD" },
                    new PricingTier { Type = "Child (3-12)", Price = 24.00m, Currency = "USD" },
                    new PricingTier { Type = "Senior", Price = 28.00m, Currency = "USD" },
                    new PricingTier { Type = "Annual Pass", Price = 150.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No outside food allowed", "Stroller-friendly", "Some animal encounters require additional tickets" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Wheelchair and stroller rentals available, sensory map provided"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "AnimalLover", Stars = 5, Comment = "Amazing exhibits, well cared for animals!", Date = "2024-01-20" },
                    new UserReview { Username = "FamilyDay", Stars = 5, Comment = "Kids loved the penguin feeding!", Date = "2024-02-08" }
                }
            },
            new Activity
            {
                Name = "Harbor Cruise & Ferry Tours",
                Category = "attractions",
                Description = "Scenic harbor cruises offering city skyline views, historical commentary, and sunset dinner cruises available.",
                Location = "Ferry Terminal",
                Position = new Position { Latitude = 41.755, Longitude = 12.285 },
                Address = "10 Pier Street, Harbor District",
                Hours = "Departures every hour, 10:00 AM - 8:00 PM",
                AvailableDates = "April through October, weather permitting",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Standard Cruise (1 hour)", Price = 28.00m, Currency = "USD" },
                    new PricingTier { Type = "Extended Tour (2 hours)", Price = 45.00m, Currency = "USD" },
                    new PricingTier { Type = "Sunset Dinner Cruise", Price = 95.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Weather dependent", "Advance booking recommended for dinner cruises", "Life jackets required for children under 7" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Accessible boarding ramp, wheelchair spaces on deck"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "BoatLover", Stars = 5, Comment = "Beautiful views of the city!", Date = "2024-05-15" },
                    new UserReview { Username = "SunsetChaser", Stars = 4, Comment = "Dinner cruise was romantic but pricey.", Date = "2024-06-20" }
                }
            },
            new Activity
            {
                Name = "Adventure Theme Park",
                Category = "attractions",
                Description = "Thrilling amusement park with roller coasters, family rides, water attractions, and live entertainment shows.",
                Location = "Entertainment Zone",
                Position = new Position { Latitude = 41.91, Longitude = 12.515 },
                Address = "3000 Thrill Road, East Side",
                Hours = "Daily: 10:00 AM - 10:00 PM (seasonal hours vary)",
                AvailableDates = "March through October, weekends in November",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Single Day", Price = 65.00m, Currency = "USD" },
                    new PricingTier { Type = "Two-Day Pass", Price = 110.00m, Currency = "USD" },
                    new PricingTier { Type = "Fast Pass Add-on", Price = 50.00m, Currency = "USD" },
                    new PricingTier { Type = "Under 48 inches", Price = 45.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Height requirements on many rides", "No outside food/drinks", "Locker rental required for some rides" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Accessibility guide available, companion restrooms, ride access program"
                },
                Rating = 4.6,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "ThrillSeeker", Stars = 5, Comment = "Best roller coasters in the region!", Date = "2024-06-10" },
                    new UserReview { Username = "FamilyFunTimes", Stars = 4, Comment = "Great for all ages, but gets crowded.", Date = "2024-07-04" }
                }
            },
            new Activity
            {
                Name = "Historic Market & Food Hall",
                Category = "attractions",
                Description = "Vibrant public market operating since 1895, featuring local produce, artisan foods, restaurants, and cultural performances.",
                Location = "Market District",
                Position = new Position { Latitude = 41.897, Longitude = 12.482 },
                Address = "400 Market Street, Market District",
                Hours = "Monday-Saturday: 7:00 AM - 9:00 PM, Sunday: 9:00 AM - 6:00 PM",
                AvailableDates = "Open year-round",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Free Entry", Price = 0.00m, Currency = "USD" },
                    new PricingTier { Type = "Guided Food Tour", Price = 55.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Individual vendors set their own hours", "Cash preferred at some stalls" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Wide aisles, accessible restrooms, seating areas throughout"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "Foodie2024", Stars = 5, Comment = "Amazing variety of local foods!", Date = "2024-01-28" },
                    new UserReview { Username = "LocalFan", Stars = 5, Comment = "A city treasure, love the atmosphere!", Date = "2024-02-12" }
                }
            },
            new Activity
            {
                Name = "City Beach & Boardwalk",
                Category = "attractions",
                Description = "Sandy beach with 2-mile boardwalk, beach volleyball, bike rentals, arcades, and oceanfront dining.",
                Location = "Coastal District",
                Position = new Position { Latitude = 41.73, Longitude = 12.25 },
                Address = "1 Beachfront Boulevard, Coastal District",
                Hours = "Beach: 6:00 AM - 10:00 PM, Boardwalk shops vary",
                AvailableDates = "Open year-round, lifeguards on duty Memorial Day - Labor Day",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Beach Access", Price = 0.00m, Currency = "USD" },
                    new PricingTier { Type = "Parking", Price = 15.00m, Currency = "USD" },
                    new PricingTier { Type = "Bike Rental (per hour)", Price = 12.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No glass containers on beach", "Alcohol prohibited", "Dogs allowed October-April only" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Beach wheelchairs available for loan, accessible boardwalk ramps"
                },
                Rating = 4.6,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "BeachBum", Stars = 5, Comment = "Clean beach, fun boardwalk!", Date = "2024-07-01" },
                    new UserReview { Username = "SummerFun", Stars = 4, Comment = "Great for families, parking can be challenging.", Date = "2024-08-05" }
                }
            },
            new Activity
            {
                Name = "Scenic Cable Car Ride",
                Category = "attractions",
                Description = "Historic cable car system offering scenic transportation through the city's hills with stunning views and historic landmarks.",
                Location = "Multiple Routes",
                Position = new Position { Latitude = 41.902, Longitude = 12.495 },
                Address = "Stations throughout downtown, main hub at Union Square",
                Hours = "Daily: 6:00 AM - 11:00 PM",
                AvailableDates = "Year-round service",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Single Ride", Price = 8.00m, Currency = "USD" },
                    new PricingTier { Type = "Day Pass", Price = 23.00m, Currency = "USD" },
                    new PricingTier { Type = "3-Day Visitor Pass", Price = 40.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "Standing passengers hold handrails", "Subject to delays during peak hours", "Can get crowded at tourist stops" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = false,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Historic vehicles not wheelchair accessible, accessible bus alternative available"
                },
                Rating = 4.5,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "TouristFirst", Stars = 5, Comment = "Classic city experience!", Date = "2024-01-15" },
                    new UserReview { Username = "LocalRider", Stars = 4, Comment = "Charming but often crowded with tourists.", Date = "2024-02-20" }
                }
            },
            new Activity
            {
                Name = "City Planetarium & Observatory",
                Category = "attractions",
                Description = "State-of-the-art planetarium with immersive shows about space, astronomy workshops, and telescope viewing on clear nights.",
                Location = "Science Park",
                Position = new Position { Latitude = 41.92, Longitude = 12.51 },
                Address = "800 Star Drive, Science Park",
                Hours = "Tuesday-Sunday: 10:00 AM - 9:00 PM, Night viewing: 8:00 PM - 11:00 PM",
                AvailableDates = "Open year-round, telescope viewing weather dependent",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult Show", Price = 18.00m, Currency = "USD" },
                    new PricingTier { Type = "Child/Senior", Price = 14.00m, Currency = "USD" },
                    new PricingTier { Type = "Telescope Viewing", Price = 10.00m, Currency = "USD" },
                    new PricingTier { Type = "Combo Ticket", Price = 25.00m, Currency = "USD" }
                },
                Restrictions = new List<string> { "No flash photography during shows", "Quiet environment required", "Age recommendations for different shows" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Assistive listening devices, accessible seating in theater"
                },
                Rating = 4.9,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "StarGazer", Stars = 5, Comment = "Mind-blowing planetarium shows!", Date = "2024-01-22" },
                    new UserReview { Username = "ScienceKid", Stars = 5, Comment = "Saw Saturn through the telescope - amazing!", Date = "2024-02-18" }
                }
            }
        };
    }

    public List<Activity> GetAllActivities()
    {
        return _activities;
    }

    public List<Activity> GetActivitiesByCategory(string category)
    {
        var normalizedCategory = category.ToLowerInvariant();
        return _activities.Where(a => a.Category.ToLowerInvariant() == normalizedCategory).ToList();
    }

    public List<Activity> SearchActivities(
        string? category = null,
        double? latitude = null,
        double? longitude = null,
        double? maxDistanceKm = 1.0,
        string? keywords = null)
    {
        var query = _activities.AsEnumerable();

        // Filter by category if provided
        if (!string.IsNullOrEmpty(category))
        {
            var normalizedCategory = category.ToLowerInvariant();
            query = query.Where(a => a.Category.ToLowerInvariant() == normalizedCategory);
        }

        // Filter by location proximity if coordinates provided
        if (latitude.HasValue && longitude.HasValue)
        {
            query = query.Where(a =>
            {
                var distance = CalculateDistance(
                    latitude.Value,
                    longitude.Value,
                    a.Position.Latitude,
                    a.Position.Longitude);
                return distance <= (maxDistanceKm ?? 1.0);
            });
        }

        // Filter by keywords if provided
        if (!string.IsNullOrEmpty(keywords))
        {
            var normalizedKeywords = keywords.ToLowerInvariant();
            query = query.Where(a =>
                a.Name.ToLowerInvariant().Contains(normalizedKeywords) ||
                a.Description.ToLowerInvariant().Contains(normalizedKeywords) ||
                a.Location.ToLowerInvariant().Contains(normalizedKeywords));
        }

        return query.ToList();
    }

    private static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        // Haversine formula to calculate distance between two points on Earth
        const double R = 6371; // Earth's radius in kilometers

        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }
}
