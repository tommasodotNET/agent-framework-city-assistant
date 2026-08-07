using System.ComponentModel;

namespace OrchestratorAgent.Models.Accommodation;

/// <summary>
/// Types of accommodation available
/// </summary>
public enum AccommodationType
{
    [Description("Hotel")]
    Hotel,
    
    [Description("Bed and Breakfast")]
    BedAndBreakfast,
    
    [Description("Hostel")]
    Hostel,
    
    [Description("Apartment")]
    Apartment,
    
    [Description("Resort")]
    Resort,
    
    [Description("Guesthouse")]
    Guesthouse,
    
    [Description("Motel")]
    Motel,
    
    [Description("Villa")]
    Villa,
    
    [Description("Boutique")]
    Boutique
}
