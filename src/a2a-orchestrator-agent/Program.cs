using A2A;
using A2A.AspNetCore;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SharedServices;

const string TelemetrySourceName = "CityAssistant.A2AOrchestrator";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(TelemetrySourceName))
    .WithTracing(tracing => tracing.AddSource(TelemetrySourceName));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

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

builder.AddKeyedAzureCosmosContainer("sessions",
    configureClientOptions: option =>
    {
        option.Serializer = new CosmosSystemTextJsonSerializer();
    });

builder.AddKeyedAzureCosmosContainer("conversations",
    configureClientOptions: option =>
    {
        option.Serializer = new CosmosSystemTextJsonSerializer();
    });

builder.Services.AddCosmosAgentSessionStore("sessions", opt => { opt.TtlSeconds = 86400 * 7; });

#pragma warning disable MEAI001
builder.Services.AddSingleton<IChatReducer, Microsoft.Extensions.AI.SummarizingChatReducer>(sp => new SummarizingChatReducer(sp.GetRequiredService<IChatClient>(), 5, 3));
builder.Services.AddCosmosChatHistoryProvider("conversations", (sp, opt) =>
{
    opt.MessageTtlSeconds = 86400 * 7;
});
#pragma warning restore MEAI001

var restaurantAgent = await ResolveA2AAgentAsync("restaurantagent");
var activitiesAgent = await ResolveA2AAgentAsync("activitiesagent");
var accommodationAgent = await ResolveA2AAgentAsync("accommodationagent");

var systemPrompt = File.ReadAllText(Path.Combine(builder.Environment.ContentRootPath, "Prompts", "system-prompt.txt"));

builder.AddAIAgent("a2a-orchestrator-agent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();

    var agentOptions = new ChatClientAgentOptions()
    {
        Id = key,
        Name = key,
        Description = "A city assistant that orchestrates remote specialist agents through A2A agent-as-tool integrations",
        ChatOptions = new ChatOptions()
        {
            Instructions = systemPrompt,
            Tools =
            [
                restaurantAgent.AsAIFunction(),
                activitiesAgent.AsAIFunction(),
                accommodationAgent.AsAIFunction()
            ]
        }
    }.WithCosmosChatHistoryProvider(sp);

    var agent = chatClient
        .AsAIAgent(agentOptions, services: sp)
        .AsBuilder()
        .UseOpenTelemetry(
            sourceName: TelemetrySourceName,
            configure: telemetry => telemetry.EnableSensitiveData = true)
        .Build();
    var ficc = agent.GetService<FunctionInvokingChatClient>();
    ficc?.AllowConcurrentInvocation = true;

    return agent;
}).WithCosmosSessionStore()
  .AddA2AServer();

var app = builder.Build();

app.UseCors();

var orchestratorAgentBaseUrl = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] ?? "http://localhost:5201";
var orchestratorAgentUrl = $"{orchestratorAgentBaseUrl}/agenta2a";
app.MapWellKnownAgentCard(new AgentCard
{
    Name = "a2a-orchestrator-agent",
    SupportedInterfaces =
    [
        new AgentInterface
        {
            Url = orchestratorAgentUrl,
            ProtocolBinding = "HTTP+JSON",
            ProtocolVersion = "1.0"
        }
    ],
    Description = "A city assistant that composes the restaurant, activities, and accommodation agents strictly through A2A agent-as-tool integrations",
    Version = "1.0",
    DefaultInputModes = ["text"],
    DefaultOutputModes = ["text"],
    Capabilities = new AgentCapabilities
    {
        Streaming = true,
        PushNotifications = false
    },
    Skills =
    [
        new A2A.AgentSkill
        {
            Name = "A2A City Assistant",
            Description = "Help users with city-related tasks by routing requests to the restaurant, activities, and accommodation agents over A2A",
            Examples =
            [
                "Find me a good restaurant",
                "Recommend a vegetarian restaurant near the Old Town Square",
                "What museums can I visit?",
                "Show me theaters in Agentburg",
                "Find me a hotel near Castle Hill",
                "Show me B&Bs with parking for less than 80€ per night"
            ]
        }
    ]
});
app.MapA2AHttpJson("a2a-orchestrator-agent", "/agenta2a");

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
