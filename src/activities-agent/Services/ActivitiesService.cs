using ActivitiesAgent.Models;

namespace ActivitiesAgent.Services;

public class ActivitiesService
{
    private readonly List<Activity> _activities;

    public ActivitiesService()
    {
        _activities = new List<Activity>
        {
            // MUSEUMS (5 activities)
            new Activity
            {
                Name = "Agentburg History Museum",
                Category = "museums",
                Description = "Explore the rich and layered past of Agentburg through permanent galleries spanning prehistoric settlements, medieval trade routes, and the city's rise as a modern regional hub. Highlights include the original city charter, medieval armour collections, and immersive dioramas.",
                Location = "Museum Mile",
                Position = new Position { Latitude = 48.1050, Longitude = 11.1010 },
                Address = "1 Museum Avenue, Museum Mile, Agentburg",
                Hours = "Tuesday-Sunday: 9:00 AM - 6:00 PM, Closed Mondays",
                AvailableDates = "Year-round; special temporary exhibitions change seasonally",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 14.00m, Currency = "EUR" },
                    new PricingTier { Type = "Senior (65+)", Price = 10.00m, Currency = "EUR" },
                    new PricingTier { Type = "Student", Price = 8.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 12)", Price = 0.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "No flash photography", "No food or drinks in galleries", "Large bags must be stored in lockers" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Wheelchairs available free at the entrance; lifts connect all three floors; tactile exhibits in the medieval gallery"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "HistoryNerd_AG", Stars = 5, Comment = "Absolutely outstanding permanent collection. The medieval trade-route room alone is worth the ticket price.", Date = "2024-03-10" },
                    new UserReview { Username = "WeekendExplorer", Stars = 5, Comment = "Well laid-out and informative. The audio guide is excellent and free with admission.", Date = "2024-04-02" }
                }
            },
            new Activity
            {
                Name = "Agentburg Modern Art Gallery",
                Category = "museums",
                Description = "A vibrant showcase of contemporary and modern art set within a beautifully restored 19th-century merchant's house in Old Town. The rotating collection features paintings, sculpture, and digital installations by both local Agentburg artists and internationally recognised names.",
                Location = "Old Town",
                Position = new Position { Latitude = 48.1008, Longitude = 11.0985 },
                Address = "15 Gallery Street, Old Town, Agentburg",
                Hours = "Wednesday-Monday: 10:00 AM - 7:00 PM, Closed Tuesdays",
                AvailableDates = "Year-round; rotating exhibitions updated every eight weeks",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 12.00m, Currency = "EUR" },
                    new PricingTier { Type = "Senior (65+)", Price = 9.00m, Currency = "EUR" },
                    new PricingTier { Type = "Student", Price = 7.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 12)", Price = 0.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "No photography of digital installations", "No touching sculptures", "Quiet atmosphere requested" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Ramp access at side entrance on Merchant Lane; lift to upper gallery; large-print catalogue available on request"
                },
                Rating = 4.6,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "ArtWalker", Stars = 5, Comment = "Loved the digital installation on the top floor. The building itself is a piece of art.", Date = "2024-02-18" },
                    new UserReview { Username = "CasualCritic", Stars = 4, Comment = "Rotating exhibitions keep it fresh. Went back twice this season.", Date = "2024-03-25" }
                }
            },
            new Activity
            {
                Name = "Natural History & Science Museum",
                Category = "museums",
                Description = "From towering dinosaur skeletons to live insect habitats and a planetarium dome, this expansive museum in Science Park offers a full day of discovery for families and enthusiasts alike. Interactive labs let visitors conduct simple experiments alongside trained educators.",
                Location = "Science Park",
                Position = new Position { Latitude = 48.1035, Longitude = 11.1060 },
                Address = "200 Science Boulevard, Science Park, Agentburg",
                Hours = "Monday-Sunday: 9:00 AM - 5:30 PM",
                AvailableDates = "Open daily except 24-26 December and New Year's Day",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 16.00m, Currency = "EUR" },
                    new PricingTier { Type = "Senior (60+)", Price = 12.00m, Currency = "EUR" },
                    new PricingTier { Type = "Student", Price = 10.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 6)", Price = 0.00m, Currency = "EUR" },
                    new PricingTier { Type = "Family (2 adults + 2 children)", Price = 42.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "Children under 8 must be accompanied by an adult", "No touching live exhibits without staff supervision", "No outside food in the lab zones" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Full step-free access throughout; accessible restrooms on every floor; sensory-friendly morning sessions every Saturday 9:00-10:30 AM"
                },
                Rating = 4.9,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "ScienceDad", Stars = 5, Comment = "Our kids didn't want to leave. The planetarium show was spectacular and the interactive labs are genuinely hands-on.", Date = "2024-04-12" },
                    new UserReview { Username = "NatureGuide_M", Stars = 5, Comment = "Best natural history collection in the region. The new marine life wing is a must-see.", Date = "2024-04-20" }
                }
            },
            new Activity
            {
                Name = "Maritime & Trade Museum",
                Category = "museums",
                Description = "Housed in a converted 18th-century warehouse on the Agentburg waterfront, this museum charts the city's centuries-old trading legacy. Authentic cargo vessels, navigational instruments, and merchant ledgers tell the story of how Agentburg became a vital inland port.",
                Location = "Harbor District",
                Position = new Position { Latitude = 48.0952, Longitude = 11.1098 },
                Address = "3 Quayside Road, Harbor District, Agentburg",
                Hours = "Tuesday-Sunday: 10:00 AM - 5:00 PM, Closed Mondays",
                AvailableDates = "Year-round; boat tour add-on available May-September",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 11.00m, Currency = "EUR" },
                    new PricingTier { Type = "Senior (65+)", Price = 8.00m, Currency = "EUR" },
                    new PricingTier { Type = "Student", Price = 7.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 12)", Price = 0.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "No flash photography near historic documents", "No climbing on vessel exhibits", "Guided boat tours require advance booking" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Ground floor fully accessible; upper gallery reached by lift; some historic vessel interiors have limited access due to structure"
                },
                Rating = 4.5,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "HarborHistorian", Stars = 5, Comment = "Fascinating look at how trade shaped this city. The replica merchant brig below decks is immersive.", Date = "2024-03-05" },
                    new UserReview { Username = "WaterfrontWalker", Stars = 4, Comment = "Great location on the quay. Worth adding the harbour boat tour if visiting in summer.", Date = "2024-05-18" }
                }
            },
            new Activity
            {
                Name = "Technology & Innovation Museum",
                Category = "museums",
                Description = "An interactive journey from the Industrial Revolution to the age of artificial intelligence, located at the heart of Agentburg's Tech Hub. Visitors can program robots, explore virtual reality environments, and trace the evolution of computing through working vintage machines.",
                Location = "Tech Hub",
                Position = new Position { Latitude = 48.1028, Longitude = 11.1048 },
                Address = "88 Innovation Drive, Tech Hub, Agentburg",
                Hours = "Monday-Saturday: 10:00 AM - 7:00 PM, Sunday: 11:00 AM - 5:00 PM",
                AvailableDates = "Year-round; school holiday programmes run February, Easter, and summer",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 15.00m, Currency = "EUR" },
                    new PricingTier { Type = "Senior (65+)", Price = 11.00m, Currency = "EUR" },
                    new PricingTier { Type = "Student", Price = 9.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 12)", Price = 0.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "VR sessions limited to ages 10 and above", "Robot workshop requires advance registration", "Photography permitted except in VR zones" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "All interactive stations designed for seated and standing use; hearing loop fitted in main auditorium; accessible parking bays at main entrance"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "TechEnthusiast99", Stars = 5, Comment = "The AI lab section is genuinely mind-blowing. Spent three hours and still felt I missed things.", Date = "2024-04-08" },
                    new UserReview { Username = "RetroComputer_Fan", Stars = 5, Comment = "Working ENIAC replica and rows of vintage computers - a dream for anyone who loves tech history.", Date = "2024-05-01" }
                }
            },

            // THEATERS (3 activities)
            new Activity
            {
                Name = "Agentburg Grand Opera",
                Category = "theaters",
                Description = "Agentburg's flagship performing arts venue, the Grand Opera presents world-class opera, ballet, and orchestral performances in a stunning neo-baroque hall. With a capacity of 1,400 and near-perfect acoustics, it draws leading companies from across Europe.",
                Location = "Cultural Center",
                Position = new Position { Latitude = 48.1014, Longitude = 11.0979 },
                Address = "10 Cultural Plaza, Cultural Center, Agentburg",
                Hours = "Box office: Tuesday-Sunday 11:00 AM - 7:30 PM; performances typically at 7:00 PM",
                AvailableDates = "September-June season; summer gala concerts in July",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Stalls (adult)", Price = 55.00m, Currency = "EUR" },
                    new PricingTier { Type = "Circle (adult)", Price = 40.00m, Currency = "EUR" },
                    new PricingTier { Type = "Gallery (adult)", Price = 22.00m, Currency = "EUR" },
                    new PricingTier { Type = "Student (gallery)", Price = 12.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "Smart casual dress code enforced", "Latecomers admitted only at suitable breaks", "No photography or recording during performances", "Mobile phones must be silenced" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Dedicated wheelchair spaces in stalls and circle; hearing loop throughout the auditorium; sign-language-interpreted performances on selected dates (see programme)"
                },
                Rating = 4.9,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "OperaAficionado", Stars = 5, Comment = "Saw La Traviata here last autumn. The acoustics are flawless and the production design was breathtaking.", Date = "2024-01-20" },
                    new UserReview { Username = "FirstTimeOperaGoer", Stars = 5, Comment = "Was nervous about my first opera but the atmosphere made it magical. Booking the circle for perfect sightlines.", Date = "2024-03-15" }
                }
            },
            new Activity
            {
                Name = "Old Town Playhouse",
                Category = "theaters",
                Description = "A beloved 300-seat repertory theatre tucked into a cobbled Old Town alley, the Playhouse has nurtured local acting talent for over a century. Its intimate stage hosts drama, comedy, and new writing throughout the year, with a dedicated youth theatre programme.",
                Location = "Old Town",
                Position = new Position { Latitude = 48.1003, Longitude = 11.0992 },
                Address = "7 Theater Lane, Old Town, Agentburg",
                Hours = "Box office: Monday-Saturday 10:00 AM - 6:00 PM; evening performances at 7:30 PM, matinees at 2:30 PM",
                AvailableDates = "Year-round except two-week dark period in January for maintenance",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 25.00m, Currency = "EUR" },
                    new PricingTier { Type = "Concession (senior/student)", Price = 18.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 16)", Price = 12.00m, Currency = "EUR" },
                    new PricingTier { Type = "Group (10+)", Price = 20.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "No photography or recording during performances", "Children under 5 not admitted to evening shows", "Mobile phones must be switched off" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = true,
                    AdditionalInfo = "Step-free access via Cobbler's Yard entrance; hearing loop in all seating areas; audio-described performances on the second Sunday of each month"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "TheatreLover_AG", Stars = 5, Comment = "Intimate, atmospheric, and the acting is consistently superb. One of Agentburg's hidden gems.", Date = "2024-02-28" },
                    new UserReview { Username = "LocalMum", Stars = 4, Comment = "Took the kids to the Christmas pantomime - they laughed all night. Great youth programme.", Date = "2023-12-22" }
                }
            },
            new Activity
            {
                Name = "Central Park Open Air Theater",
                Category = "theaters",
                Description = "A magical amphitheatre nestled within the tree-lined heart of Central Park, staging open-air performances from late spring through early autumn. The programme ranges from Shakespeare to jazz-infused musicals, with picnic seating on the lawn available.",
                Location = "Central Park",
                Position = new Position { Latitude = 48.1021, Longitude = 11.1034 },
                Address = "Central Park South, Agentburg",
                Hours = "Performances: Thursday-Sunday evenings at 8:00 PM; Saturday matinees at 3:00 PM (May-September only)",
                AvailableDates = "May through September; free community performances on Bank Holiday Sundays",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Reserved seating (adult)", Price = 20.00m, Currency = "EUR" },
                    new PricingTier { Type = "Reserved seating (concession)", Price = 14.00m, Currency = "EUR" },
                    new PricingTier { Type = "Lawn (general admission)", Price = 10.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 12, lawn)", Price = 0.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "Performances may be cancelled in severe weather; refunds issued", "Glass bottles not permitted on the lawn", "Dogs welcome on a lead in lawn area only" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Designated wheelchair and companion spaces at the front of the reserved seating tier; accessible portable facilities on site; hearing loop covers reserved seating rows 1-10"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "SummerNights_Fan", Stars = 5, Comment = "Midsummer Night's Dream under real stars with a picnic blanket - simply perfect. Do not miss this.", Date = "2023-07-22" },
                    new UserReview { Username = "ParkPerformer", Stars = 5, Comment = "The acoustics in the amphitheatre are surprisingly good. Jazz musical last Saturday was a sell-out for good reason.", Date = "2024-06-08" }
                }
            },

            // CULTURAL EVENTS (4 activities)
            new Activity
            {
                Name = "Agentburg Summer Music Festival",
                Category = "cultural_events",
                Description = "The city's flagship outdoor music festival takes over Market Square every July, featuring three days of live performances across four stages. Genres span classical and folk through to electronic and world music, with food stalls, craft markets, and evening fireworks.",
                Location = "Market Square",
                Position = new Position { Latitude = 48.1010, Longitude = 11.0960 },
                Address = "Market Square, Agentburg",
                Hours = "Friday 4:00 PM - 11:00 PM, Saturday & Sunday 12:00 PM - 11:00 PM",
                AvailableDates = "Second weekend of July annually",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Day ticket (adult)", Price = 22.00m, Currency = "EUR" },
                    new PricingTier { Type = "Weekend pass (adult)", Price = 50.00m, Currency = "EUR" },
                    new PricingTier { Type = "Day ticket (under 16)", Price = 10.00m, Currency = "EUR" },
                    new PricingTier { Type = "Weekend pass (under 16)", Price = 22.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "No outside alcohol permitted", "No professional camera equipment without press accreditation", "Last entry 9:00 PM" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Accessible viewing platforms at all four stages; accessible toilets throughout the site; a quiet rest zone is available near the eastern entrance"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "FestivalFrequenter", Stars = 5, Comment = "Three days of non-stop great music in a stunning square. The fireworks finale on Sunday is unmissable.", Date = "2023-07-16" },
                    new UserReview { Username = "WorldMusicFan", Stars = 5, Comment = "Discovered so many new artists here. The world music stage is a real highlight.", Date = "2023-07-17" }
                }
            },
            new Activity
            {
                Name = "Harvest Food Fair",
                Category = "cultural_events",
                Description = "Held each October along the picturesque Harbor Promenade, the Harvest Food Fair brings together over 80 local and regional producers selling artisan cheeses, smoked meats, fresh-baked bread, preserves, and seasonal produce. Evening tastings and chef demonstrations are included in the ticket price.",
                Location = "Harbor Waterfront",
                Position = new Position { Latitude = 48.0948, Longitude = 11.1098 },
                Address = "Harbor Promenade, Harbor District, Agentburg",
                Hours = "Saturday & Sunday: 10:00 AM - 7:00 PM",
                AvailableDates = "First full weekend of October annually",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult (entry + tasting tokens)", Price = 12.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 12)", Price = 0.00m, Currency = "EUR" },
                    new PricingTier { Type = "Evening tasting session (adult)", Price = 18.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "Well-behaved dogs on leads welcome", "Stroller-friendly paths throughout", "Producer stalls close at 6:00 PM; evening tastings require separate ticket" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Fully paved promenade with no steps; accessible parking adjacent to the Harbor Gate entrance; accessible portable toilets on site"
                },
                Rating = 4.6,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "FoodieFamily", Stars = 5, Comment = "The smoked cheese stall alone is worth the trip. An excellent community event with a fantastic atmosphere.", Date = "2023-10-08" },
                    new UserReview { Username = "LocalFarmer_Hans", Stars = 4, Comment = "Proud to exhibit here every year. Crowd is enthusiastic and respectful of the producers.", Date = "2023-10-09" }
                }
            },
            new Activity
            {
                Name = "Castle Hill Night Market",
                Category = "cultural_events",
                Description = "As dusk falls over Castle Hill every summer Friday, artisan vendors, street-food chefs, and live musicians transform the Esplanade into a glowing night market. Lanterns line the ancient walls while visitors browse handmade crafts, vintage finds, and eclectic street food from a dozen cuisines.",
                Location = "Castle Hill",
                Position = new Position { Latitude = 48.1059, Longitude = 11.0932 },
                Address = "Castle Hill Esplanade, Agentburg",
                Hours = "Fridays: 6:00 PM - 11:00 PM (June-August)",
                AvailableDates = "Every Friday, June through August",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Entry (adult)", Price = 5.00m, Currency = "EUR" },
                    new PricingTier { Type = "Entry (under 16)", Price = 0.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "No outside alcohol", "Last entry 9:30 PM", "Esplanade path may be uneven; flat shoes recommended" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Accessible route via the Castle Hill lift from Lower Esplanade; accessible portable toilets on site; some cobbled sections are avoidable via the tarmac perimeter path"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "NightMarketRegular", Stars = 5, Comment = "My favourite Friday evening activity in Agentburg. The view from the Esplanade at night is incredible.", Date = "2023-08-11" },
                    new UserReview { Username = "CraftHunter_Eva", Stars = 5, Comment = "Found a beautiful hand-thrown ceramic lamp here. Wonderful atmosphere and the food choices are superb.", Date = "2023-07-28" }
                }
            },
            new Activity
            {
                Name = "University Innovation Expo",
                Category = "cultural_events",
                Description = "Held each spring across the University Quarter's open campus, the Innovation Expo showcases research breakthroughs, start-up pitches, robotics competitions, and interactive science demonstrations by Agentburg University students and faculty. Free public talks run throughout both days.",
                Location = "University Quarter",
                Position = new Position { Latitude = 48.1080, Longitude = 11.0950 },
                Address = "University Campus, University Quarter, Agentburg",
                Hours = "Saturday & Sunday: 9:00 AM - 6:00 PM",
                AvailableDates = "Last weekend of April annually",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "General admission", Price = 0.00m, Currency = "EUR" },
                    new PricingTier { Type = "Workshop session (adult)", Price = 8.00m, Currency = "EUR" },
                    new PricingTier { Type = "Workshop session (student)", Price = 4.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "Workshop places limited; book online in advance", "Children under 14 must be accompanied by an adult in robotics lab", "No cycling on campus during the event" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = false,
                    SignLanguageSupport = true,
                    AdditionalInfo = "All campus buildings involved are step-free; accessible parking on North Campus; sign-language interpreters present at main stage talks"
                },
                Rating = 4.5,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "CuriousEngineer", Stars = 5, Comment = "The robotics competition is thrilling. Stayed for the afternoon AI panel and it was genuinely thought-provoking.", Date = "2024-04-28" },
                    new UserReview { Username = "FamilyDayOut_AG", Stars = 4, Comment = "Great free event. Kids loved the chemistry demos and the drone display. A bit crowded on Sunday afternoon.", Date = "2024-04-29" }
                }
            },

            // ATTRACTIONS (5 activities)
            new Activity
            {
                Name = "Castle Hill Fortress & Museum",
                Category = "attractions",
                Description = "Dominating the Agentburg skyline from its rocky summit, the medieval Castle Hill Fortress dates to the 12th century and offers panoramic views across the entire city and surrounding valley. The on-site museum details the fortress's evolution from military stronghold to civic landmark, with reconstructed battlements and a working drawbridge.",
                Location = "Castle Hill",
                Position = new Position { Latitude = 48.1060, Longitude = 11.0930 },
                Address = "Castle Hill Summit, Agentburg",
                Hours = "Daily: 9:00 AM - 7:00 PM (April-October); 10:00 AM - 4:00 PM (November-March)",
                AvailableDates = "Year-round; extended hours during summer solstice and Agentburg heritage days",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 13.00m, Currency = "EUR" },
                    new PricingTier { Type = "Senior (65+)", Price = 10.00m, Currency = "EUR" },
                    new PricingTier { Type = "Student", Price = 8.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 12)", Price = 0.00m, Currency = "EUR" },
                    new PricingTier { Type = "Family (2 adults + up to 3 children)", Price = 32.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "Steep paths on the north side; sturdy footwear recommended", "Battlements closed in high winds", "No drones without prior written permission" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Castle Hill lift provides step-free access from Lower Esplanade to the main gate; the inner courtyard and museum are fully accessible; some outer battlements involve steps"
                },
                Rating = 4.9,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "CastleChaser", Stars = 5, Comment = "Jaw-dropping views and a genuinely fascinating history. Allow at least three hours to do it justice.", Date = "2024-03-30" },
                    new UserReview { Username = "HistoryTeacher_Klara", Stars = 5, Comment = "Brought a school group here and the museum exceeded all expectations. Staff were excellent with the children.", Date = "2024-05-10" }
                }
            },
            new Activity
            {
                Name = "Central Park Botanical Garden",
                Category = "attractions",
                Description = "Spread across twelve hectares in the eastern quarter of Central Park, the Botanical Garden shelters over 5,000 plant species across themed zones: a Japanese zen garden, a medicinal herb labyrinth, tropical glasshouses, and a native wildflower meadow that blooms spectacularly in May and June.",
                Location = "Central Park",
                Position = new Position { Latitude = 48.1022, Longitude = 11.1031 },
                Address = "Central Park East, Agentburg",
                Hours = "Daily: 8:00 AM - 8:00 PM (April-September); 9:00 AM - 5:00 PM (October-March)",
                AvailableDates = "Year-round; guided tours Saturday mornings at 10:00 AM (April-October)",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 8.00m, Currency = "EUR" },
                    new PricingTier { Type = "Senior (65+)", Price = 6.00m, Currency = "EUR" },
                    new PricingTier { Type = "Student", Price = 5.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 12)", Price = 0.00m, Currency = "EUR" },
                    new PricingTier { Type = "Guided tour supplement", Price = 4.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "No picking of plants or flowers", "Dogs not permitted inside the Botanical Garden boundaries", "Bicycles must be left at the entrance racks" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "All main paths are paved and fully accessible; glasshouses are step-free; accessible toilets at the main entrance and near the zen garden; tactile plant guide available at reception"
                },
                Rating = 4.7,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "GardenEnthusiast", Stars = 5, Comment = "The wildflower meadow in June is simply stunning. One of the most peaceful spots in the city.", Date = "2024-06-02" },
                    new UserReview { Username = "SaturdayStroller", Stars = 5, Comment = "Took the Saturday guided tour and learned so much about native plants. Highly recommend.", Date = "2024-04-20" }
                }
            },
            new Activity
            {
                Name = "Old Town Square & Historic Fountain",
                Category = "attractions",
                Description = "The beating heart of Agentburg's medieval quarter, Old Town Square is framed by guild houses, baroque facades, and the city's celebrated 17th-century Merchants' Fountain. The square hosts a daily changing-of-the-guard ceremony, weekly farmers' markets, and serves as the hub for all major civic festivities.",
                Location = "Old Town",
                Position = new Position { Latitude = 48.1005, Longitude = 11.0990 },
                Address = "Old Town Square, Agentburg",
                Hours = "Always accessible (outdoor attraction); visitor information kiosk open daily 9:00 AM - 6:00 PM",
                AvailableDates = "Year-round; Christmas market December 1-24; Easter market Good Friday through Easter Monday",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Free entry", Price = 0.00m, Currency = "EUR" },
                    new PricingTier { Type = "Guided walking tour (adult)", Price = 10.00m, Currency = "EUR" },
                    new PricingTier { Type = "Guided walking tour (child under 12)", Price = 0.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "No cycling through the pedestrianised square", "Fountain wading strictly prohibited", "Guided tours depart from the kiosk at 10:00 AM and 2:00 PM daily" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Fully paved square with level access throughout; accessible toilets at the visitor kiosk; audio guide app available free via the Agentburg city app"
                },
                Rating = 4.8,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "TouristFromVienna", Stars = 5, Comment = "Arrived at dusk and the lighting on the guild houses is magical. The fountain is gorgeous and the atmosphere is lively.", Date = "2024-05-25" },
                    new UserReview { Username = "LocalHistory_Georg", Stars = 5, Comment = "The guided tour is excellent value. Our guide knew every stone and story. Ended at the best café in the square.", Date = "2024-04-14" }
                }
            },
            new Activity
            {
                Name = "Agentburg Observation Tower",
                Category = "attractions",
                Description = "Rising 95 metres above Downtown Agentburg, the Observation Tower offers a 360-degree panoramic viewing deck at the top alongside an indoor sky lounge with floor-to-ceiling glass. On clear days visitors can see beyond the city limits to the surrounding hills. The tower includes a permanent exhibition on the city's architectural evolution.",
                Location = "Downtown",
                Position = new Position { Latitude = 48.1001, Longitude = 11.1001 },
                Address = "1 Skyline Plaza, Downtown, Agentburg",
                Hours = "Daily: 10:00 AM - 10:00 PM (last entry 9:30 PM)",
                AvailableDates = "Year-round except two scheduled maintenance days per year (announced on website)",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Adult", Price = 18.00m, Currency = "EUR" },
                    new PricingTier { Type = "Senior (65+)", Price = 14.00m, Currency = "EUR" },
                    new PricingTier { Type = "Student", Price = 12.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 12)", Price = 8.00m, Currency = "EUR" },
                    new PricingTier { Type = "Child (under 4)", Price = 0.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "Outdoor deck closed during thunderstorms and extreme winds", "No climbing on safety barriers", "Sky lounge reservations recommended at weekends" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = true,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "High-speed accessible lift to all levels; fully accessible outdoor deck; accessible restrooms on ground and observation floors; tactile city map at the top"
                },
                Rating = 4.6,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "SkylineSeeker", Stars = 5, Comment = "Worth every euro. Sunset from the top deck is extraordinary - book the sky lounge table for the full experience.", Date = "2024-05-30" },
                    new UserReview { Username = "CityPhotographer_Lena", Stars = 4, Comment = "Best photography spot in Agentburg. Golden hour light across the old town rooftops is spectacular.", Date = "2024-04-17" }
                }
            },
            new Activity
            {
                Name = "Harbor Lighthouse & Maritime Walk",
                Category = "attractions",
                Description = "Agentburg's iconic 19th-century lighthouse stands at the tip of the harbor breakwater, open to visitors who can climb its 112 spiral steps for sweeping views across the waterway and cityscape. The adjacent Maritime Walk is a 2 km waterfront promenade lined with historic plaques, moored tall ships, and artisan kiosks.",
                Location = "Harbor District",
                Position = new Position { Latitude = 48.0946, Longitude = 11.1105 },
                Address = "1 Lighthouse Lane, Harbor District, Agentburg",
                Hours = "Lighthouse: Daily 10:00 AM - 6:00 PM (May-October), weekends only 10:00 AM - 4:00 PM (November-April); Maritime Walk always open",
                AvailableDates = "Maritime Walk year-round; lighthouse interior May-October daily, November-April weekends only",
                PricingTiers = new List<PricingTier>
                {
                    new PricingTier { Type = "Lighthouse adult", Price = 6.00m, Currency = "EUR" },
                    new PricingTier { Type = "Lighthouse child (under 12)", Price = 3.00m, Currency = "EUR" },
                    new PricingTier { Type = "Maritime Walk", Price = 0.00m, Currency = "EUR" }
                },
                Restrictions = new List<string> { "Lighthouse tower not suitable for visitors with vertigo or mobility issues (112 steps, no lift)", "Children under 6 not permitted inside the lighthouse tower", "No cycling on the Maritime Walk promenade" },
                Accessibility = new AccessibilityInfo
                {
                    WheelchairAccessible = false,
                    AudioGuideAvailable = true,
                    SignLanguageSupport = false,
                    AdditionalInfo = "Maritime Walk is fully accessible and wheelchair friendly; lighthouse interior has 112 narrow steps with no lift and is not accessible; accessible seating areas along the promenade"
                },
                Rating = 4.3,
                Reviews = new List<UserReview>
                {
                    new UserReview { Username = "HarborWalker_Fritz", Stars = 4, Comment = "The promenade is lovely in the evening with all the tall ships lit up. Lighthouse climb is steep but rewarding.", Date = "2024-05-19" },
                    new UserReview { Username = "MaritimeHistory_Fan", Stars = 5, Comment = "The historic plaques along the walk tell a brilliant story. The view from the lighthouse top is worth the climb.", Date = "2024-06-01" }
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
