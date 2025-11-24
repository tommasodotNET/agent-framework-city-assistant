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
builder.Services.AddSingleton<CosmosAgentThreadStore>();

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

    var agent = chatClient.CreateAIAgent(
        instructions: @"You are a helpful city assistant that helps users with various tasks.
You can help users find restaurants using the restaurant-agent tool.
You can help users find accommodations (hotels, B&Bs, hostels) using the accommodation-agent tool.
The accommodation agent has geocoding capabilities built-in, so it can handle location-based queries.
When users ask about restaurants, food, dining, or related topics, use the restaurant-agent to get the information.
When users ask about accommodations, hotels, lodging, places to stay, or related topics, use the accommodation-agent to get the information.
Always be friendly, helpful, and provide comprehensive responses based on the information you receive from the tools.",
        description: "A city assistant that orchestrates multiple specialized agents",
        name: key,
        tools: [
            restaurantAgent.AsAIFunction(),
            accommodationAgent.AsAIFunction()
        ]
    );

    return agent;
}).WithThreadStore((sp, key) => sp.GetRequiredService<CosmosAgentThreadStore>());

var app = builder.Build();

// Enable CORS
app.UseCors();

// Map A2A endpoint for orchestrator agent
app.MapA2A("orchestrator-agent", "/agenta2a", new AgentCard
{
    Name = "orchestrator-agent",
    Url = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] + "/agenta2a" ?? "http://localhost:5197/agenta2a",
    Description = "A city assistant that orchestrates multiple specialized agents to help with various tasks including restaurant and accommodation recommendations",
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
            Description = "Help users with city-related tasks including restaurant and accommodation recommendations",
            Examples = [
                "Find me a good restaurant",
                "What's the best pizza place in the city?",
                "Recommend a vegetarian restaurant",
                "Find me a hotel near the Colosseum",
                "Show me B&Bs with parking for less than 80€ per night",
                "Where can I stay in Rome?"
            ]
        }
    ]
});

app.MapDefaultEndpoints();
app.Run();
