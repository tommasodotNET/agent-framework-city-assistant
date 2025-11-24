using AccommodationAgent.Models;

namespace AccommodationAgent.Services;

public interface IRerankingService
{
    /// <summary>
    /// Rerank accommodations using LLM-based pointwise scoring.
    /// Only returns accommodations with a score greater than 6.
    /// </summary>
    /// <param name="accommodations">The list of accommodations to rerank</param>
    /// <param name="userQuery">The user's original search query</param>
    /// <param name="maxDegreeOfParallelism">Maximum number of parallel evaluations (default: 3)</param>
    /// <returns>List of highly relevant accommodations sorted by score</returns>
    Task<List<Accommodation>> RerankAccommodationsAsync(
        List<Accommodation> accommodations,
        string userQuery,
        int maxDegreeOfParallelism = 3);
}
