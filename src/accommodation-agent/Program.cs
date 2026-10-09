using A2A;
using A2A.AspNetCore;
using Azure.AI.Inference;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using AccommodationAgent.Services;
using AccommodationAgent.Tools;
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
    .AddChatClient(builder.Configuration["AI:ChatModel"] ?? "gpt-4.1")
    .UseProtocolMetadataFilter();

// Register services
builder.Services.AddSingleton<IAccommodationService, AccommodationService>();
builder.Services.AddSingleton<IRerankingService, RerankingService>();

// Configure MCP Client for geocoding server
var geocodingMcpUrl = Environment.GetEnvironmentVariable("services__geocodingmcpserver__https__0")
    ?? Environment.GetEnvironmentVariable("services__geocodingmcpserver__http__0")
    ?? throw new InvalidOperationException(
        "Could not resolve geocoding MCP server URL. Env vars 'services__geocodingmcpserver__https__0' and " +
        "'services__geocodingmcpserver__http__0' are both missing.");

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

builder.Services.AddSingleton<AccommodationTools>();

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
var historyCompaction = builder.Services.AddHistoryCompactionProfile(
    builder.Configuration.GetSection("HistoryCompaction"),
    static (sp, model) => sp.GetRequiredService<ChatCompletionsClient>().AsIChatClient(model));
builder.Services.AddCosmosChatHistoryProvider("conversations", options => options.Compaction = historyCompaction);

var systemPrompt = File.ReadAllText(Path.Combine(builder.Environment.ContentRootPath, "Prompts", "system-prompt.txt"));

// Register the accommodation agent
builder.AddAIAgent("accommodation-agent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var accommodationTools = sp.GetRequiredService<AccommodationTools>().GetFunctions();

    var agentOptions = new ChatClientAgentOptions()
    {
        Id = key,
        Name = key,
        Description = "A friendly accommodation assistant that helps find hotels, B&Bs, and other lodging in Agentburg",
        ChatOptions = new ChatOptions()
        {
            Instructions = systemPrompt,
            Tools = [.. accommodationTools, .. mcpTools.Cast<AITool>()]
        }
    }.WithCosmosChatHistoryProvider(sp);

    return chatClient.AsAIAgent(agentOptions, services: sp);
}).WithCosmosSessionStore()
  .AddA2AServer();

var app = builder.Build();

// Map A2A endpoint
var accommodationAgentUrl = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] + "/agenta2a" ?? "http://localhost:5198/agenta2a";
app.MapWellKnownAgentCard(new AgentCard
{
    Name = "accommodation-agent",
    SupportedInterfaces = [
        new AgentInterface
        {
            Url = accommodationAgentUrl,
            ProtocolBinding = "HTTP+JSON",
            ProtocolVersion = "1.0"
        }
    ],
    Description = "An accommodation assistant that helps find and recommend hotels, B&Bs, and other lodging in Agentburg based on user preferences",
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
            Name = "Accommodation Search",
            Description = "Find accommodations by rating, location, amenities, price, and type in Agentburg",
            Examples = [
                "Find me the best hotels",
                "Show me hotels near the Old Town Square",
                "Find a B&B with parking for less than 80€ per night",
                "Hotels near Museum Mile rated more than 4 stars",
                "Budget accommodation near the main station"
            ]
        }
    ]
});
app.MapA2AHttpJson("accommodation-agent", "/agenta2a");

// Map OpenAI-compatible endpoints
app.MapOpenAIResponses();
app.MapOpenAIConversations();

app.MapDefaultEndpoints();
app.Run();
