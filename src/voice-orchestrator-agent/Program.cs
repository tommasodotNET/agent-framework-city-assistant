using A2A;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Azure.Cosmos;
using OpenTelemetry.Trace;
using SharedServices;
using VoiceOrchestratorAgent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddKeyedAzureCosmosContainer("conversations",
    configureClientOptions: (option) =>
    {
        option.Serializer = new CosmosSystemTextJsonSerializer();
    });

builder.Services.AddSingleton(sp => sp.GetRequiredKeyedService<Container>("conversations"));

// Register custom ActivitySource for gen_ai tracing
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(VoiceWebSocketHandler.ActivitySourceName));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Parse the Voice Live endpoint from the Foundry connection string
// Voice Live needs the cognitiveservices.azure.com endpoint (the "Endpoint" key)
var endpoint = ParseVoiceLiveEndpoint(builder.Configuration.GetConnectionString("foundry") ?? "");

var model = builder.Configuration["VoiceLive:Model"] ?? "gpt-realtime";
var voice = builder.Configuration["VoiceLive:Voice"] ?? "en-US-Ava:DragonHDLatestNeural";

// Connect to downstream agents via A2A (same pattern as orchestrator-agent)
var agents = new Dictionary<string, AIAgent>();

var agentConfigs = new Dictionary<string, string>
{
    ["restaurant_agent"] = "services__restaurantagent__https__0",
    ["activities_agent"] = "services__activitiesagent__https__0",
    ["accommodation_agent"] = "services__accommodationagent__https__0",
};

foreach (var (agentName, envVar) in agentConfigs)
{
    var url = Environment.GetEnvironmentVariable(envVar)
        ?? Environment.GetEnvironmentVariable(envVar.Replace("https", "http"));

    if (!string.IsNullOrEmpty(url))
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromSeconds(60)
        };
        var cardResolver = new A2ACardResolver(
            httpClient.BaseAddress!,
            httpClient,
            agentCardPath: "/agenta2a/v1/card");

        agents[agentName] = cardResolver.GetAIAgentAsync().Result;
    }
}

builder.Services.AddSingleton(agents);
builder.Services.AddSingleton(new DefaultAzureCredential());

var systemPrompt = File.ReadAllText(
    Path.Combine(builder.Environment.ContentRootPath, "Prompts", "system-prompt.txt"));

var app = builder.Build();

app.UseCors();
app.UseWebSockets();

app.MapGet("/health", () => Results.Ok("healthy"));

app.Map("/ws/voice", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connection expected");
        return;
    }

    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger<VoiceWebSocketHandler>();

    var credential = context.RequestServices.GetRequiredService<DefaultAzureCredential>();
    var a2aAgents = context.RequestServices.GetRequiredService<Dictionary<string, AIAgent>>();
    var cosmosContainer = context.RequestServices.GetRequiredService<Container>();

    var conversationId = context.Request.Query["conversationId"].FirstOrDefault();
    if (!string.IsNullOrEmpty(conversationId))
        conversationId = conversationId + "-voice";

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

    var handler = new VoiceWebSocketHandler(
        webSocket,
        credential,
        endpoint,
        model,
        voice,
        systemPrompt,
        a2aAgents,
        logger,
        conversationId,
        cosmosContainer);

    await handler.RunAsync(context.RequestAborted);
});

app.MapDefaultEndpoints();
app.Run();

static string ParseVoiceLiveEndpoint(string connectionString)
{
    // Voice Live requires the cognitiveservices.azure.com endpoint (the "Endpoint" key)
    string? endpoint = null;

    foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = part.Split('=', 2);
        if (kv.Length != 2) continue;

        var key = kv[0].Trim();
        var value = kv[1].Trim();

        if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = value.TrimEnd('/');
        }
    }

    if (endpoint is not null)
        return endpoint;

    // If no key=value format, treat the whole string as a URL
    if (connectionString.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        return connectionString.TrimEnd('/');

    return "https://localhost";
}
