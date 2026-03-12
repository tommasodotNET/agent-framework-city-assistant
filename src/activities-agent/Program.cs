using A2A;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using ActivitiesAgent.Services;
using ActivitiesAgent.Tools;
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
builder.Services.AddSingleton<ActivitiesService>();

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

builder.Services.AddSingleton<ActivitiesTools>();

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

var systemPrompt = File.ReadAllText(Path.Combine(builder.Environment.ContentRootPath, "Prompts", "system-prompt.txt"));

// Register the activities agent
builder.AddAIAgent("activities-agent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var activitiesTools = sp.GetRequiredService<ActivitiesTools>().GetFunctions();

    var agentOptions = new ChatClientAgentOptions()
    {
        Name = key,
        Description = "A friendly activities assistant that helps discover museums, theaters, cultural events, and attractions",
        ChatOptions = new ChatOptions()
        {
            Instructions = systemPrompt,
            Tools = [.. activitiesTools, .. mcpTools.Cast<AITool>()]
        }
    }.WithCosmosChatHistoryProvider(sp);

    return chatClient.AsAIAgent(agentOptions, services: sp);
}).WithCosmosSessionStore();

var app = builder.Build();

// Map A2A endpoint
app.MapA2A("activities-agent", "/agenta2a", new AgentCard
{
    Name = "activities-agent",
    Url = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] + "/agenta2a" ?? "http://localhost:5198/agenta2a",
    Description = "An activities assistant that helps users discover and plan activities including museums, theaters, cultural events, and attractions",
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
            Name = "Activity Search",
            Description = "Find museums, theaters, cultural events, and attractions",
            Examples = [
                "Find me museums to visit",
                "What theaters are available?",
                "Show me cultural events",
                "What attractions do you recommend?"
            ]
        }
    ]
});

// Map OpenAI-compatible endpoints
app.MapOpenAIResponses();
app.MapOpenAIConversations();

app.MapDefaultEndpoints();
app.Run();
