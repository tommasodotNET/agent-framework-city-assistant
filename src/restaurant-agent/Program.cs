using A2A;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using RestaurantAgent.Services;
using RestaurantAgent.Tools;
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
builder.Services.AddSingleton<RestaurantService>();
builder.Services.AddSingleton<RestaurantTools>();

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

// Register OpenAI endpoints
builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();

// Register Cosmos containers for session and conversation storage
builder.AddKeyedAzureCosmosContainer("sessions",
    configureClientOptions: (option) => option.Serializer = new CosmosSystemTextJsonSerializer());
builder.AddKeyedAzureCosmosContainer("conversations",
    configureClientOptions: (option) => option.Serializer = new CosmosSystemTextJsonSerializer());

// Register session store and chat history provider
builder.Services.AddCosmosAgentSessionStore("sessions");
builder.Services.AddCosmosChatHistoryProvider("conversations");

// Register the restaurant agent
builder.AddAIAgent("restaurant-agent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var restaurantTools = sp.GetRequiredService<RestaurantTools>().GetFunctions();

    var agentOptions = new ChatClientAgentOptions()
    {
        Name = key,
        Description = "A friendly restaurant assistant that helps find restaurants in Agentburg",
        ChatOptions = new ChatOptions()
        {
            Instructions = @"You are a helpful restaurant assistant for the city of Agentburg. You help users find restaurants based on their preferences.

AVAILABLE TOOLS:
1. geocode_location (MCP) - Convert addresses, city names, or landmark names to coordinates (latitude, longitude). Location must be in English.
2. SearchRestaurantsByLocation - Search for restaurants near a location using coordinates and optional filters
3. GetRestaurantsByCategory - Get restaurants by category
4. SearchRestaurants - Search restaurants by keywords
5. GetAllRestaurants - Get all available restaurants

SEARCH WORKFLOW FOR LOCATION-BASED QUERIES:
When users mention ANY location (landmark, neighborhood, or attraction), you MUST:
1. Use geocode_location to convert the location to coordinates (pass English location names like 'Old Town Square', 'Castle Hill', 'Central Park')
2. Parse the JSON response to extract latitude and longitude
3. Then use SearchRestaurantsByLocation with those coordinates

Supported categories: vegetarian, pizza, japanese, seafood, french, indian, steakhouse

All restaurants are located in Agentburg. Always be friendly and provide detailed information including name, address, phone, description, rating, and price range.",
            Tools = [.. restaurantTools, .. mcpTools.Cast<AITool>()]
        }
    }.WithCosmosChatHistoryProvider(sp);

    return chatClient.AsAIAgent(agentOptions, services: sp);
}).WithCosmosSessionStore();

var app = builder.Build();

// Map A2A endpoint
app.MapA2A("restaurant-agent", "/agenta2a", new AgentCard
{
    Name = "restaurant-agent",
    Url = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] + "/agenta2a" ?? "http://localhost:5196/agenta2a",
    Description = "A restaurant assistant that helps find and recommend restaurants in Agentburg based on user preferences and location",
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
            Name = "Restaurant Search",
            Description = "Find restaurants by category, keywords, or location in Agentburg",
            Examples = [
                "Find me a vegetarian restaurant",
                "What pizza places do you recommend?",
                "Show me all restaurants",
                "Find a vegetarian restaurant near the Old Town Square",
                "Japanese food near the Cultural Center"
            ]
        }
    ]
});

// Map OpenAI-compatible endpoints
app.MapOpenAIResponses();
app.MapOpenAIConversations();

app.MapDefaultEndpoints();
app.Run();
