using AccommodationAgent.Models;
using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Collections.Concurrent;

namespace AccommodationAgent.Services;

public class RerankingService : IRerankingService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<RerankingService> _logger;

    public RerankingService(IChatClient chatClient, ILogger<RerankingService> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// Rerank accommodations using LLM-based pointwise scoring.
    /// Only returns accommodations with a score greater than 6.
    /// </summary>
    public async Task<List<Accommodation>> RerankAccommodationsAsync(
        List<Accommodation> accommodations,
        string userQuery,
        int maxDegreeOfParallelism = 3)
    {
        if (accommodations.Count == 0)
        {
            return accommodations;
        }

        var scoredAccommodations = new ConcurrentBag<(Accommodation accommodation, int score)>();

        // Process accommodations in parallel with configurable MAXDOP
        var options = new ParallelOptions 
        { 
            MaxDegreeOfParallelism = maxDegreeOfParallelism 
        };

        await Parallel.ForEachAsync(accommodations, options, async (accommodation, cancellationToken) =>
        {
            try
            {
                var score = await ScoreAccommodationAsync(accommodation, userQuery);
                if (score > 6)
                {
                    scoredAccommodations.Add((accommodation, score));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to score accommodation {AccommodationName}. Skipping.", accommodation.Name);
            }
        });

        // Sort by score descending
        return scoredAccommodations
            .OrderByDescending(x => x.score)
            .Select(x => x.accommodation)
            .ToList();
    }

    private async Task<int> ScoreAccommodationAsync(Accommodation accommodation, string userQuery)
    {
        var prompt = $@"Rate the following accommodation's relevance to the user query on a scale from 1 to 10.
Only respond with a single number between 1 and 10.

User Query: {userQuery}

Accommodation:
Name: {accommodation.Name}
Type: {accommodation.Type}
Rating: {accommodation.Rating}/5
Price per night: €{accommodation.PricePerNight}
Location: {accommodation.Address}
Amenities: {string.Join(", ", accommodation.Amenities)}
Description: {accommodation.Description}

Relevance Score (1-10):";

        var systemPrompt = @"You are an expert accommodation evaluator that rates accommodations based on how well they match user queries.

EVALUATION PROCESS:
1. Analyze the user's query to identify key requirements (location, price, amenities, rating, type)
2. Compare each requirement against the accommodation's attributes
3. Calculate a relevance score based on the grading criteria below

GRADING CRITERIA (1-10 scale):
- 9-10: Excellent match - meets all or almost all key requirements perfectly
- 7-8: Good match - meets most key requirements with minor compromises
- 5-6: Moderate match - meets some requirements but missing important aspects
- 3-4: Poor match - meets few requirements or has significant mismatches
- 1-2: Very poor match - fails to meet most requirements

SCORING FACTORS:
- Location match: Does it match the requested city/landmark/area?
- Price match: Is it within the stated budget (if specified)?
- Amenities match: Does it have the requested facilities/services?
- Rating match: Does it meet quality expectations (if specified)?
- Type match: Is it the right accommodation type (hotel, B&B, hostel)?

IMPORTANT: Only respond with a single number from 1 to 10. Do not include any explanation or additional text.";

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, prompt)
        };

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: default);
        var scoreText = response.Text?.Trim() ?? "1";

        // Try to parse the score
        if (int.TryParse(scoreText, out var score) && score >= 1 && score <= 10)
        {
            return score;
        }

        _logger.LogWarning("Invalid score received: {ScoreText}. Defaulting to 1.", scoreText);
        return 1;
    }
}
