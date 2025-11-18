using System.Text.Json;
using A2A;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using OrchestratorAgent.Models;
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

// Register Cosmos for conversation storage
builder.AddKeyedAzureCosmosContainer("conversations",
    configureClientOptions: (option) => option.Serializer = new CosmosSystemTextJsonSerializer());
builder.Services.AddSingleton<ICosmosThreadRepository, CosmosThreadRepository>();
builder.Services.AddSingleton<CosmosAgentThreadStore>();

// Connect to restaurant agent via A2A
var restaurantAgentUrl = Environment.GetEnvironmentVariable("services__restaurant-agent__https__0") ?? "http://localhost:5196";
var httpClient = new HttpClient()
{
    BaseAddress = new Uri(restaurantAgentUrl),
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

// Custom API endpoint for frontend
app.MapPost("/agent/chat/stream", async (
    [FromKeyedServices("orchestrator-agent")] AIAgent agent,
    [FromKeyedServices("orchestrator-agent")] AgentThreadStore threadStore,
    [FromBody] AIChatRequest request,
    [FromServices] ILogger<Program> logger,
    HttpResponse response) =>
{
    var conversationId = request.SessionState ?? Guid.NewGuid().ToString();

    // Set response headers for streaming
    response.ContentType = "text/plain; charset=utf-8";
    response.Headers["Transfer-Encoding"] = "chunked";

    if (request.Messages.Count == 0)
    {
        // Initial greeting
        AIChatCompletionDelta delta = new(new AIChatMessageDelta()
        {
            Content = $"Hi! I'm your city assistant. I can help you find great restaurants in the city. Just ask me about restaurants, food, or dining options!"
        })
        {
            SessionState = conversationId
        };

        await response.WriteAsync($"{JsonSerializer.Serialize(delta)}\r\n");
        await response.Body.FlushAsync();
    }
    else
    {
        var message = request.Messages.LastOrDefault();
        if (message == null)
        {
            return Results.BadRequest("No message provided");
        }

        var thread = await threadStore.GetThreadAsync(agent, conversationId);
        var chatMessage = new ChatMessage(ChatRole.User, message.Content);

        try
        {
            // Stream responses
            await foreach (var update in agent.RunStreamingAsync(chatMessage, thread))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    var delta = new AIChatCompletionDelta(new AIChatMessageDelta()
                    {
                        Content = update.Text
                    })
                    {
                        SessionState = conversationId
                    };

                    await response.WriteAsync($"{JsonSerializer.Serialize(delta)}\r\n");
                    await response.Body.FlushAsync();
                }
            }

            await threadStore.SaveThreadAsync(agent, conversationId, thread);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing chat request");
            return Results.Problem("An error occurred processing your request");
        }
    }

    return Results.Ok();
});

app.MapDefaultEndpoints();
app.Run();
