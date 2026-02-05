using A2A;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using RestaurantAgent.Services;
using RestaurantAgent.Tools;
using SharedServices;

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
        Description = "A friendly restaurant assistant that helps find restaurants",
        ChatOptions = new ChatOptions()
        {
            Instructions = @"You are a helpful restaurant assistant. You help users find restaurants based on their preferences.
You can search for restaurants by category (vegetarian, pizza, japanese, mexican, french, indian, steakhouse) or by keywords.
Always be friendly and provide detailed information about the restaurants including their name, address, phone, description, rating, and price range.
When users ask about restaurants, use the available tools to retrieve the information.",
            Tools = [.. restaurantTools]
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
    Description = "A restaurant assistant that helps find and recommend restaurants based on user preferences",
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
            Description = "Find restaurants by category or keywords",
            Examples = [
                "Find me a vegetarian restaurant",
                "What pizza places do you recommend?",
                "Show me all restaurants"
            ]
        }
    ]
});

// Map OpenAI-compatible endpoints
app.MapOpenAIResponses();
app.MapOpenAIConversations();

app.MapDefaultEndpoints();
app.Run();
