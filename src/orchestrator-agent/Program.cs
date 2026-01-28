using A2A;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using SharedServices;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Configure Azure chat client
builder.AddAzureChatCompletionsClient(connectionName: "foundry",
    configureSettings: settings =>
    {
        settings.TokenCredential = new DefaultAzureCredential();
        settings.EnableSensitiveTelemetryData = true;
    })
    .AddChatClient("gpt-4.1");

// Register Cosmos for conversation storage
builder.AddKeyedAzureCosmosContainer("conversations",
    configureClientOptions: (option) => option.Serializer = new CosmosSystemTextJsonSerializer());
builder.Services.AddSingleton<ICosmosThreadRepository, CosmosThreadRepository>();
builder.Services.AddSingleton<CosmosAgentSessionStore>();

// Connect to restaurant agent via A2A
var restaurantAgentUrl = Environment.GetEnvironmentVariable("services__restaurantagent__https__0") ?? Environment.GetEnvironmentVariable("services__restaurantagent__http__0");
var restaurantHttpClient = new HttpClient()
{
    BaseAddress = new Uri(restaurantAgentUrl!),
    Timeout = TimeSpan.FromSeconds(60)
};
var restaurantCardResolver = new A2ACardResolver(
    restaurantHttpClient.BaseAddress!,
    restaurantHttpClient,
    agentCardPath: "/agenta2a/v1/card"
);

var restaurantAgent = restaurantCardResolver.GetAIAgentAsync().Result;

// Connect to activities agent via A2A
var activitiesAgentUrl = Environment.GetEnvironmentVariable("services__activitiesagent__https__0") ?? Environment.GetEnvironmentVariable("services__activitiesagent__http__0");
var activitiesHttpClient = new HttpClient()
{
    BaseAddress = new Uri(activitiesAgentUrl!),
    Timeout = TimeSpan.FromSeconds(60)
};
var activitiesCardResolver = new A2ACardResolver(
    activitiesHttpClient.BaseAddress!,
    activitiesHttpClient,
    agentCardPath: "/agenta2a/v1/card"
);

var activitiesAgent = activitiesCardResolver.GetAIAgentAsync().Result;
// Connect to accommodation agent via A2A
var accommodationAgentUrl = Environment.GetEnvironmentVariable("services__accommodationagent__https__0") ?? Environment.GetEnvironmentVariable("services__accommodationagent__http__0");
var accommodationHttpClient = new HttpClient()
{
    BaseAddress = new Uri(accommodationAgentUrl!),
    Timeout = TimeSpan.FromSeconds(60)
};
var accommodationCardResolver = new A2ACardResolver(
    accommodationHttpClient.BaseAddress!,
    accommodationHttpClient,
    agentCardPath: "/agenta2a/v1/card"
);

var accommodationAgent = accommodationCardResolver.GetAIAgentAsync().Result;

// Register the orchestrator agent
builder.AddAIAgent("orchestrator-agent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();

    var agent = chatClient.AsAIAgent(
        instructions: @"You are a helpful city assistant that helps users with various tasks.

AVAILABLE AGENTS:
1. restaurant-agent - Find restaurants by category or search
2. activities-agent - Discover museums, theaters, cultural events, and attractions (has geocoding for location-based search)
3. accommodation-agent - Find hotels, B&Bs, and hostels (has geocoding for location-based search)

ROUTING RULES:
- When users ask about restaurants, food, or dining, use the restaurant-agent
- When users ask about activities, things to do, museums, theaters, cultural events, or attractions, use the activities-agent
- When users ask about accommodations, hotels, lodging, or places to stay, use the accommodation-agent

Both activities-agent and accommodation-agent have geocoding capabilities built-in, so they can handle location-based queries.

Always be friendly, helpful, and provide comprehensive responses based on the information you receive from the tools.",
        description: "A city assistant that orchestrates multiple specialized agents",
        name: key,
        tools: [
            restaurantAgent.AsAIFunction(),
            activitiesAgent.AsAIFunction(),
            accommodationAgent.AsAIFunction()
        ]
    );

    return agent;
}).WithSessionStore((sp, key) => sp.GetRequiredService<CosmosAgentSessionStore>());

var app = builder.Build();

// Enable CORS
app.UseCors();

// Map A2A endpoint for orchestrator agent
app.MapA2A("orchestrator-agent", "/agenta2a", new AgentCard
{
    Name = "orchestrator-agent",
    Url = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] + "/agenta2a" ?? "http://localhost:5197/agenta2a",
    Description = "A city assistant that orchestrates multiple specialized agents to help with restaurants, activities, and accommodations",
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
            Name = "City Assistant",
            Description = "Help users with city-related tasks including restaurant recommendations, activity planning, and accommodation recommendations",
            Examples = [
                "Find me a good restaurant",
                "What's the best pizza place in the city?",
                "Recommend a vegetarian restaurant",
                "What museums can I visit?",
                "Show me theaters in the city",
                "What cultural events are happening?",
                "What attractions do you recommend?",
                "Find me a hotel near the Colosseum",
                "Show me B&Bs with parking for less than 80€ per night",
                "Where can I stay in Rome?"
            ]
        }
    ]
});

app.MapDefaultEndpoints();
app.Run();
