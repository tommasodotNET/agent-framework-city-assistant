using System.Text.Json;
using System.Text;

namespace AccommodationAgent.Services;

/// <summary>
/// Geocoding service that calls the MCP geocoding server
/// </summary>
public class McpGeocodingService : IGeocodingService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<McpGeocodingService> _logger;
    private readonly string _mcpServerUrl;

    public McpGeocodingService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<McpGeocodingService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
        
        // Get MCP server URL from Aspire service reference
        _mcpServerUrl = configuration["services__geocodingmcpserver__https__0"] 
            ?? configuration["services__geocodingmcpserver__http__0"]
            ?? "https://localhost:5199"; // Fallback for local development
    }

    public async Task<(double Latitude, double Longitude)?> GeocodeAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        try
        {
            // Call MCP server's geocode_location tool
            var request = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "geocode_location",
                    arguments = new { location = query }
                }
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PostAsync($"{_mcpServerUrl}/mcp", jsonContent);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);
            
            // Parse the MCP response
            if (doc.RootElement.TryGetProperty("result", out var result))
            {
                if (result.TryGetProperty("content", out var content) && content.GetArrayLength() > 0)
                {
                    var firstContent = content[0];
                    if (firstContent.TryGetProperty("text", out var text))
                    {
                        // The tool returns JSON string, parse it
                        using var resultDoc = JsonDocument.Parse(text.GetString() ?? "{}");
                        if (resultDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean())
                        {
                            var latitude = resultDoc.RootElement.GetProperty("latitude").GetDouble();
                            var longitude = resultDoc.RootElement.GetProperty("longitude").GetDouble();
                            
                            _logger.LogInformation("Geocoded '{Query}' via MCP server to: {Lat}, {Lon}", query, latitude, longitude);
                            return (latitude, longitude);
                        }
                    }
                }
            }

            _logger.LogWarning("Failed to geocode '{Query}' via MCP server", query);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling MCP geocoding server for '{Query}'", query);
            return null;
        }
    }
}
