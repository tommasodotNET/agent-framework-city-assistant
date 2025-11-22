#:sdk Aspire.AppHost.Sdk@13.0.0
#:package Aspire.Hosting.AppHost@13.0.0
#:package Aspire.Hosting.Azure.AIFoundry@13.0.0-preview.1.25560.3
#:package Aspire.Hosting.Azure.CosmosDB@13.0.0
#:package Aspire.Hosting.JavaScript@13.0.0
#:package Aspire.Hosting.Yarp@13.0.0

#:project ../restaurant-agent/RestaurantAgent.csproj
#:project ../accommodation-agent/AccommodationAgent.csproj
#:project ../orchestrator-agent/OrchestratorAgent.csproj
#:project ../geocoding-mcp-server/GeocodingMcpServer.csproj

using Aspire.Hosting.Yarp.Transforms;

var builder = DistributedApplication.CreateBuilder(args);

var tenantId = builder.AddParameterFromConfiguration("tenant", "Azure:TenantId");
var existingFoundryName = builder.AddParameter("existingFoundryName")
    .WithDescription("The name of the existing Azure Foundry resource.");
var existingFoundryResourceGroup = builder.AddParameter("existingFoundryResourceGroup")
    .WithDescription("The resource group of the existing Azure Foundry resource.");

var foundry = builder.AddAzureAIFoundry("foundry")
    .AsExisting(existingFoundryName, existingFoundryResourceGroup);

tenantId.WithParentRelationship(foundry);
existingFoundryName.WithParentRelationship(foundry);
existingFoundryResourceGroup.WithParentRelationship(foundry);

#pragma warning disable ASPIRECOSMOSDB001
var cosmos = builder.AddAzureCosmosDB("cosmos-db")
    .RunAsPreviewEmulator(
        emulator =>
        {
            emulator.WithDataExplorer();
            emulator.WithLifetime(ContainerLifetime.Persistent);
        });
var db = cosmos.AddCosmosDatabase("db");
var conversations = db.AddContainer("conversations", "/conversationId");

var restaurantAgent = builder.AddProject("restaurantagent", "../restaurant-agent/RestaurantAgent.csproj")
    .WithHttpHealthCheck("/health")
    .WithReference(foundry).WaitFor(foundry)
    .WithReference(conversations).WaitFor(conversations)
    .WithEnvironment("AZURE_TENANT_ID", tenantId)
    .WithUrls((e) =>
    {
        e.Urls.Add(new() { Url = "/agenta2a/v1/card", DisplayText = "🤖Restaurant Agent A2A Card", Endpoint = e.GetEndpoint("https") });
    });

var geocodingMcpServer = builder.AddProject("geocodingmcpserver", "../geocoding-mcp-server/GeocodingMcpServer.csproj")
    .WithHttpHealthCheck("/health");

var accommodationAgent = builder.AddProject("accommodationagent", "../accommodation-agent/AccommodationAgent.csproj")
    .WithHttpHealthCheck("/health")
    .WithReference(foundry).WaitFor(foundry)
    .WithReference(conversations).WaitFor(conversations)
    .WithReference(geocodingMcpServer).WaitFor(geocodingMcpServer)
    .WithEnvironment("AZURE_TENANT_ID", tenantId)
    .WithUrls((e) =>
    {
        e.Urls.Add(new() { Url = "/agenta2a/v1/card", DisplayText = "🏨Accommodation Agent A2A Card", Endpoint = e.GetEndpoint("https") });
    });

var orchestratorAgent = builder.AddProject("orchestratoragent", "../orchestrator-agent/OrchestratorAgent.csproj")
    .WithHttpHealthCheck("/health")
    .WithReference(foundry).WaitFor(foundry)
    .WithReference(conversations).WaitFor(conversations)
    .WithReference(restaurantAgent).WaitFor(restaurantAgent)
    .WithReference(accommodationAgent).WaitFor(accommodationAgent)
    .WithEnvironment("AZURE_TENANT_ID", tenantId)
    .WithUrls((e) =>
    {
        e.Urls.Add(new() { Url = "/agenta2a/v1/card", DisplayText = "🤖Orchestrator Agent A2A Card", Endpoint = e.GetEndpoint("https") });
    });

var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithReference(orchestratorAgent).WaitFor(orchestratorAgent)
    .WithUrls((e) =>
    {
        e.Urls.Clear();
        e.Urls.Add(new() { Url = "/", DisplayText = "💬City Assistant", Endpoint = e.GetEndpoint("http") });
    });

if (builder.ExecutionContext.IsPublishMode)
{
    builder.AddYarp("yarp")
        .WithExternalHttpEndpoints()
        .WithConfiguration(yarp =>
        {
            yarp.AddRoute("/agent/{**catch-all}", orchestratorAgent)
                .WithTransformPathPrefix("/agent");
        })
        .PublishWithStaticFiles(frontend);
}

builder.Build().Run();
