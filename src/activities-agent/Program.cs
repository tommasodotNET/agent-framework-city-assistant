using A2A;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using ActivitiesAgent.Services;
using ActivitiesAgent.Tools;
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
builder.Services.AddSingleton<ActivitiesService>();

// Configure MCP Client for geocoding server
var geocodingMcpUrl = builder.Configuration["services__geocodingmcpserver__https__0"] 
    ?? builder.Configuration["services__geocodingmcpserver__http__0"]
    ?? "https://localhost:7299";

// Append the MCP endpoint path
var mcpEndpoint = new Uri(new Uri(geocodingMcpUrl), "/mcp");

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = mcpEndpoint
});

var mcpClient = await McpClient.CreateAsync(transport);

// Retrieve the list of tools available on the MCP geocoding server
var mcpTools = await mcpClient.ListToolsAsync();

// Register MCP client as a singleton
builder.Services.AddSingleton(mcpClient);

builder.Services.AddSingleton<ActivitiesTools>();

// Register OpenAI endpoints
builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();

// Register Cosmos for conversation storage
builder.AddKeyedAzureCosmosContainer("conversations",
    configureClientOptions: (option) => option.Serializer = new CosmosSystemTextJsonSerializer());
builder.Services.AddSingleton<ICosmosThreadRepository, CosmosThreadRepository>();
builder.Services.AddSingleton<CosmosAgentSessionStore>();

// Register the activities agent
builder.AddAIAgent("activities-agent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var activitiesTools = sp.GetRequiredService<ActivitiesTools>().GetFunctions();

    var agent = chatClient.AsAIAgent(
        instructions: @"You are a helpful activities assistant. You help users discover and plan activities during their trip.

AVAILABLE TOOLS:
1. geocode_location (MCP) - Convert addresses, city names, or landmark names to coordinates (latitude, longitude). Location must be in English.
2. SearchActivitiesAsync - Search for activities using coordinates and other filters
3. GetAllActivities - Get all available activities
4. GetActivitiesByCategory - Get activities by category (museums, theaters, cultural_events, attractions)

SEARCH WORKFLOW:
ALWAYS geocode locations first! When users mention ANY location (city, landmark, or address), you MUST:
1. Use geocode_location to convert the location to coordinates (pass English location names)
2. Parse the JSON response to extract latitude and longitude
3. Then use those coordinates with SearchActivitiesAsync

You can search for activities by:
- Category (museums, theaters, cultural_events, attractions)
- Location using coordinates:
  * ALWAYS use geocode_location first for ANY location (cities like 'Rome' or 'Latina', landmarks like 'Colosseum' or 'Vatican', addresses)
  * Parse the returned JSON to get latitude and longitude values
  * Then pass the latitude/longitude to SearchActivitiesAsync
  * The default search radius is 1 km from the coordinates
- Keywords in name or description

Multiple criteria can be combined (e.g., 'find me museums near the Colosseum').

Each activity includes detailed information about hours, dates, pricing, restrictions, accessibility, location, and user reviews.
Always be friendly and provide comprehensive information to help users plan their visit.",
        description: "A friendly activities assistant that helps discover museums, theaters, cultural events, and attractions",
        name: key,
        tools: [.. activitiesTools, .. mcpTools.Cast<AITool>()]
    );

    return agent;
}).WithSessionStore((sp, key) => sp.GetRequiredService<CosmosAgentSessionStore>());

var app = builder.Build();

// Map A2A endpoint
app.MapA2A("activities-agent", "/agenta2a", new AgentCard
{
    Name = "activities-agent",
    Url = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] + "/agenta2a" ?? "http://localhost:5198/agenta2a",
    Description = "An activities assistant that helps users discover and plan activities including museums, theaters, cultural events, and attractions",
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
            Name = "Activity Search",
            Description = "Find museums, theaters, cultural events, and attractions",
            Examples = [
                "Find me museums to visit",
                "What theaters are available?",
                "Show me cultural events",
                "What attractions do you recommend?"
            ]
        }
    ]
});

// Map OpenAI-compatible endpoints
app.MapOpenAIResponses();
app.MapOpenAIConversations();

app.MapDefaultEndpoints();
app.Run();
