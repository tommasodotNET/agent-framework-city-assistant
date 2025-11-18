var builder = DistributedApplication.CreateBuilder(args);

// Add Azure AI Inference (Foundry)
var foundry = builder.AddAzureAIInference("foundry");

// Add Cosmos DB
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .AddDatabase("conversations");

// Add Restaurant Agent
var restaurantAgent = builder.AddProject<Projects.RestaurantAgent>("restaurant-agent")
    .WithReference(foundry)
    .WithReference(cosmos);

// Add Orchestrator Agent
var orchestratorAgent = builder.AddProject<Projects.OrchestratorAgent>("orchestrator-agent")
    .WithReference(foundry)
    .WithReference(cosmos)
    .WithReference(restaurantAgent);

// Add Frontend with proxy to orchestrator
builder.AddNpmApp("frontend", "../frontend")
    .WithReference(orchestratorAgent)
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints()
    .PublishAsDockerFile();

builder.Build().Run();
