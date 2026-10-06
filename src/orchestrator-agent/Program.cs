using A2A;
using A2A.AspNetCore;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OrchestratorAgent.Services;
using OrchestratorAgent.Skills;
using SharedServices;

const string TelemetrySourceName = "CityAssistant.ClassSkillsOrchestrator";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(TelemetrySourceName))
    .WithTracing(tracing => tracing.AddSource(TelemetrySourceName));

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
    .AddChatClient("gpt-4.1")
    .UseStreamingUsage()
    .UseOpenTelemetry(
        sourceName: TelemetrySourceName,
        configure: telemetry => telemetry.EnableSensitiveData = true)
    .ConfigureOptions(options => options.AllowMultipleToolCalls = true);

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

// Register the in-process class-based skills and their progressive-disclosure provider.
builder.Services.AddSingleton<ActivitiesService>();
builder.Services.AddSingleton<IAccommodationService, AccommodationService>();
builder.Services.AddSingleton<IRerankingService, RerankingService>();
builder.Services.AddSingleton<RestaurantSkill>();
builder.Services.AddSingleton<ActivitiesSkill>();
builder.Services.AddSingleton<AccommodationSkill>();
builder.Services.AddSingleton<WeatherSkill>();
builder.Services.AddSingleton(sp =>
    new AgentSkillsProvider(
    [
        sp.GetRequiredService<RestaurantSkill>(),
        sp.GetRequiredService<ActivitiesSkill>(),
        sp.GetRequiredService<AccommodationSkill>(),
        sp.GetRequiredService<WeatherSkill>()
    ]));

var systemPrompt = File.ReadAllText(Path.Combine(builder.Environment.ContentRootPath, "Prompts", "system-prompt.txt"));

// Register the orchestrator agent
builder.AddAIAgent("class-skills-orchestrator-agent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var restaurantSkillsProvider = sp.GetRequiredService<AgentSkillsProvider>();

    var agentOptions = new ChatClientAgentOptions()
    {
        Id = key,
        Name = key,
        Description = "A city assistant that orchestrates multiple specialized agents",
        ChatOptions = new ChatOptions()
        {
            Instructions = systemPrompt,
            Tools = []
        },
        AIContextProviders = [restaurantSkillsProvider]
    }.WithCosmosChatHistoryProvider(sp);

    #pragma warning disable MAAI001
    var agent = chatClient
        .AsAIAgent(agentOptions, services: sp)
        .AsBuilder()
        .UseOpenTelemetry(
            sourceName: TelemetrySourceName,
            configure: telemetry => telemetry.EnableSensitiveData = true)
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
var orchestratorAgentBaseUrl = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] ?? "http://localhost:5197";
var orchestratorAgentUrl = $"{orchestratorAgentBaseUrl}/agenta2a";
app.MapWellKnownAgentCard(new AgentCard
{
    Name = "class-skills-orchestrator-agent",
    SupportedInterfaces = [
        new AgentInterface
        {
            Url = orchestratorAgentUrl,
            ProtocolBinding = "HTTP+JSON",
            ProtocolVersion = "1.0"
        }
    ],
    Description = "A city assistant that uses only in-process class-based skills for restaurants, activities, accommodations, and weather in Agentburg",
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
            Description = "Help users with city-related tasks using in-process class-based skills for restaurant recommendations, activity planning, accommodation recommendations, and weather",
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
                "Where can I stay in Agentburg?",
                "What's the weather like in Agentburg today?"
            ]
        }
    ]
});
app.MapA2AHttpJson("class-skills-orchestrator-agent", "/agenta2a");

app.MapDefaultEndpoints();
app.Run();
