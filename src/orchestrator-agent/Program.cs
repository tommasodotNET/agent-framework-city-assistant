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
var httpClient = new HttpClient()
{
    BaseAddress = new Uri(restaurantAgentUrl!),
    Timeout = TimeSpan.FromSeconds(60)
};
var cardResolver = new A2ACardResolver(
    httpClient.BaseAddress!,
    httpClient,
    agentCardPath: "/agenta2a/v1/card"
);

var restaurantAgent = cardResolver.GetAIAgentAsync().Result;

// Register the orchestrator agent
builder.AddAIAgent("orchestrator-agent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();

    var agent = chatClient.CreateAIAgent(
        instructions: @"You are a helpful city assistant that helps users with various tasks.
You can help users find restaurants using the restaurant-agent tool.
When users ask about restaurants, food, dining, or related topics, use the restaurant-agent to get the information.
Always be friendly, helpful, and provide comprehensive responses based on the information you receive from the tools.",
        description: "A city assistant that orchestrates multiple specialized agents",
        name: key,
        tools: [
            restaurantAgent.AsAIFunction()
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
    Description = "A city assistant that orchestrates multiple specialized agents to help with various tasks",
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
            Description = "Help users with city-related tasks including restaurant recommendations",
            Examples = [
                "Find me a good restaurant",
                "What's the best pizza place in the city?",
                "Recommend a vegetarian restaurant"
            ]
        }
    ]
});

app.MapDefaultEndpoints();
app.Run();
