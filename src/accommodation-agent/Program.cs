using A2A;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using AccommodationAgent.Services;
using AccommodationAgent.Tools;
using SharedServices;
using ModelContextProtocol.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Configure Azure chat client
builder.AddAzureChatCompletionsClient(connectionName: "foundry",
    configureSettings: settings =>
    {
        settings.TokenCredential = new DefaultAzureCredential();
        settings.EnableSensitiveTelemetryData = true;
    })
    .AddChatClient("gpt-4.1");

// Register services
builder.Services.AddSingleton<IAccommodationService, AccommodationService>();
builder.Services.AddSingleton<IRerankingService, RerankingService>();

// Configure MCP Client for geocoding server
var geocodingMcpUrl = builder.Configuration["services__geocodingmcpserver__https__0"] 
    ?? builder.Configuration["services__geocodingmcpserver__http__0"]
    ?? "https://localhost:7299";

// Append the MCP endpoint path
var mcpEndpoint = new Uri(new Uri(geocodingMcpUrl), "/mcp");

var transport = new SseClientTransport(new SseClientTransportOptions
{
    Endpoint = mcpEndpoint
});

var mcpClient = await McpClientFactory.CreateAsync(transport);

// Retrieve the list of tools available on the MCP geocoding server
var mcpTools = await mcpClient.ListToolsAsync();

// Register MCP client as a singleton
builder.Services.AddSingleton(mcpClient);

builder.Services.AddSingleton<AccommodationTools>();

// Register OpenAI endpoints
builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();

// Register Cosmos for conversation storage
builder.AddKeyedAzureCosmosContainer("conversations",
    configureClientOptions: (option) => option.Serializer = new CosmosSystemTextJsonSerializer());
builder.Services.AddSingleton<ICosmosThreadRepository, CosmosThreadRepository>();
builder.Services.AddSingleton<CosmosAgentThreadStore>();

// Register the accommodation agent
builder.AddAIAgent("accommodation-agent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var accommodationTools = sp.GetRequiredService<AccommodationTools>().GetFunctions();

    var agent = chatClient.CreateAIAgent(
        instructions: @"You are a helpful accommodation assistant. You help users find accommodations (hotels, B&Bs, hostels) based on their preferences.

AVAILABLE TOOLS:
1. geocode_location (MCP) - Convert addresses, city names, or landmark names to coordinates (latitude, longitude). Location must be in English.
2. SearchAccommodationsAsync - Search for accommodations using coordinates and other filters
3. GetAllAccommodations - Get all available accommodations

SEARCH WORKFLOW:
ALWAYS geocode locations first! When users mention ANY location (city, landmark, or address), you MUST:
1. Use geocode_location to convert the location to coordinates (pass English location names)
2. Parse the JSON response to extract latitude and longitude
3. Then use those coordinates with SearchAccommodationsAsync

You can search for accommodations by:
- User rating (e.g., 'find me the best hotels', 'hotels rated more than 4')
- Location using coordinates:
  * ALWAYS use geocode_location first for ANY location (cities like 'Rome' or 'Latina', landmarks like 'Colosseum' or 'Vatican', addresses)
  * Parse the returned JSON to get latitude and longitude values
  * Then pass the latitude/longitude to SearchAccommodationsAsync
  * The default search radius is 1 km from the coordinates
- Amenities/services (e.g., 'with parking', 'with room service', 'with breakfast')
- Price per night (e.g., 'less than 50€ per night', 'under 100 euros')
- Type (e.g., 'hotel', 'bed-and-breakfast', 'hostel')

Multiple criteria can be combined (e.g., 'find me a hotel near the Colosseum with parking for less than 80€ per night').

Always be friendly and provide detailed information about the accommodations including their name, type, rating, address, amenities, price, and description.
The search results are automatically reranked using AI to show only the most relevant options for the user's query.",
        description: "A friendly accommodation assistant that helps find hotels, B&Bs, and other lodging",
        name: key,
        tools: [.. accommodationTools, .. mcpTools.Cast<AITool>()]
    );

    return agent;
}).WithThreadStore((sp, key) => sp.GetRequiredService<CosmosAgentThreadStore>());

var app = builder.Build();

// Map A2A endpoint
app.MapA2A("accommodation-agent", "/agenta2a", new AgentCard
{
    Name = "accommodation-agent",
    Url = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] + "/agenta2a" ?? "http://localhost:5198/agenta2a",
    Description = "An accommodation assistant that helps find and recommend hotels, B&Bs, and other lodging based on user preferences",
    Version = "1.0",
    DefaultInputModes = ["text"],
    DefaultOutputModes = ["text"],
    Capabilities = new AgentCapabilities
    {
        Streaming = true,
        PushNotifications = false
    },
    Skills = [
        new AgentSkill
        {
            Name = "Accommodation Search",
            Description = "Find accommodations by rating, location, amenities, price, and type",
            Examples = [
                "Find me the best hotels",
                "Show me hotels near the Colosseum",
                "Find a B&B with parking for less than 80€ per night",
                "Hotels in Latina rated more than 4 stars"
            ]
        }
    ]
});

// Map OpenAI-compatible endpoints
app.MapOpenAIResponses();
app.MapOpenAIConversations();

app.MapDefaultEndpoints();
app.Run();
