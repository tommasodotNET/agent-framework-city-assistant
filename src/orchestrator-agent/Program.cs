using A2A;
using A2A.AspNetCore;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using OrchestratorAgent.Skills;
using SharedServices;
using System.ComponentModel;

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
    .AddChatClient("gpt-4.1").ConfigureOptions(options => options.AllowMultipleToolCalls = true);

// Register Cosmos containers for session storage with a custom serializer to handle complex types
builder.AddKeyedAzureCosmosContainer("sessions",
    configureClientOptions: (option) => 
    {
        option.Serializer = new CosmosSystemTextJsonSerializer();
    });

// Register Cosmos containers for conversation storage with a custom serializer to handle complex types
builder.AddKeyedAzureCosmosContainer("conversations", 
    configureClientOptions: (option) =>
    {
        option.Serializer = new CosmosSystemTextJsonSerializer();
    });


// Register session and chat history providers using keyed containers
builder.Services.AddCosmosAgentSessionStore("sessions", opt => { opt.TtlSeconds = 86400 * 7; });

//Register the reducer for chat history
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

builder.Services.AddSingleton<IChatReducer, Microsoft.Extensions.AI.SummarizingChatReducer>(sp => new SummarizingChatReducer(sp.GetRequiredService<IChatClient>(), 5, 3));
builder.Services.AddCosmosChatHistoryProvider("conversations", (sp, opt) =>
{
    opt.MessageTtlSeconds = 86400 * 7;
    //opt.ChatReducer = sp.GetRequiredService<IChatReducer>();
    //opt.ReductionStoragePolicy = ReductionStoragePolicy.Archive;
});

#pragma warning restore MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Register the in-process restaurant skill and its progressive-disclosure provider.
builder.Services.AddSingleton<RestaurantSkill>();
builder.Services.AddSingleton(sp =>
    new AgentSkillsProvider(sp.GetRequiredService<RestaurantSkill>()));

// Connect to remote agents through their current A2A agent cards.
var activitiesAgent = await ResolveA2AAgentAsync("activitiesagent");
var accommodationAgent = await ResolveA2AAgentAsync("accommodationagent");

[Description("Get the weather for a given location.")]
static string GetWeather([Description("The location to get the weather for.")] string location)
 => $"The weather in {location} is cloudy with a high of 15°C.";

var systemPrompt = File.ReadAllText(Path.Combine(builder.Environment.ContentRootPath, "Prompts", "system-prompt.txt"));

// Register the orchestrator agent
builder.AddAIAgent("orchestrator-agent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var restaurantSkillsProvider = sp.GetRequiredService<AgentSkillsProvider>();

    var agentOptions = new ChatClientAgentOptions()
    {
        Name = key,
        Description = "A city assistant that orchestrates multiple specialized agents",
        ChatOptions = new ChatOptions()
        {
            Instructions = systemPrompt,
            Tools = [
                activitiesAgent.AsAIFunction(),
                accommodationAgent.AsAIFunction(),
                AIFunctionFactory.Create(GetWeather)
            ]
        },
        AIContextProviders = [restaurantSkillsProvider]
    }.WithCosmosChatHistoryProvider(sp);

    #pragma warning disable MAAI001
    var agent = chatClient
        .AsAIAgent(agentOptions, services: sp)
        .AsBuilder()
        .UseToolApproval(new ToolApprovalAgentOptions
        {
            AutoApprovalRules = [AgentSkillsProvider.AllToolsAutoApprovalRule]
        })
        .Build();
    #pragma warning restore MAAI001

    var ficc = agent.GetService<FunctionInvokingChatClient>();
    ficc?.AllowConcurrentInvocation = true;

    return agent;
}).WithCosmosSessionStore()
  .AddA2AServer();



var app = builder.Build();

// Enable CORS
app.UseCors();

// Map A2A endpoint for orchestrator agent
var orchestratorAgentUrl = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] + "/agenta2a" ?? "http://localhost:5197/agenta2a";
app.MapWellKnownAgentCard(new AgentCard
{
    Name = "orchestrator-agent",
    SupportedInterfaces = [
        new AgentInterface
        {
            Url = orchestratorAgentUrl,
            ProtocolBinding = "HTTP+JSON",
            ProtocolVersion = "1.0"
        }
    ],
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
        new A2A.AgentSkill
        {
            Name = "City Assistant",
            Description = "Help users with city-related tasks including restaurant recommendations, activity planning, and accommodation recommendations",
            Examples = [
                "Find me a good restaurant",
                "What's the best pizza place in Agentburg?",
                "Recommend a vegetarian restaurant near the Old Town Square",
                "What museums can I visit?",
                "Show me theaters in Agentburg",
                "What cultural events are happening?",
                "What attractions do you recommend?",
                "Find me a hotel near the Castle Hill",
                "Show me B&Bs with parking for less than 80€ per night",
                "Where can I stay in Agentburg?"
            ]
        }
    ]
});
app.MapA2AHttpJson("orchestrator-agent", "/agenta2a");

app.MapDefaultEndpoints();
app.Run();

static async Task<AIAgent> ResolveA2AAgentAsync(string serviceName)
{
    var url = Environment.GetEnvironmentVariable($"services__{serviceName}__https__0")
        ?? Environment.GetEnvironmentVariable($"services__{serviceName}__http__0")
        ?? throw new InvalidOperationException($"No endpoint is configured for A2A service '{serviceName}'.");

    var httpClient = new HttpClient
    {
        BaseAddress = new Uri(url),
        Timeout = TimeSpan.FromSeconds(60)
    };
    var resolver = new A2ACardResolver(
        httpClient.BaseAddress,
        httpClient,
        agentCardPath: "/.well-known/agent-card.json");
    var agentCard = await resolver.GetAgentCardAsync();

    return agentCard.AsAIAgent(httpClient);
}
