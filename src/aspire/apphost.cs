#:sdk Aspire.AppHost.Sdk@13.2.0-pr.14149.g805bf3d2
#:package Aspire.Hosting.Foundry@13.2.0-pr.14149.g805bf3d2
#:package Aspire.Hosting.Azure.CosmosDB@13.2.0-pr.14149.g805bf3d2
#:package Aspire.Hosting.JavaScript@13.2.0-pr.14149.g805bf3d2
#:package Aspire.Hosting.Yarp@13.2.0-pr.14149.g805bf3d2

#:project ../restaurant-agent/RestaurantAgent.csproj
#:project ../activities-agent/ActivitiesAgent.csproj
#:project ../accommodation-agent/AccommodationAgent.csproj

#:project ../orchestrator-agent/OrchestratorAgent.csproj
#:project ../geocoding-mcp-server/GeocodingMcpServer.csproj

using Aspire.Hosting.Foundry;
using Aspire.Hosting.Yarp.Transforms;

var builder = DistributedApplication.CreateBuilder(args);

var tenantId = builder.AddParameterFromConfiguration("tenant", "Azure:TenantId");
var existingFoundryName = builder.AddParameter("existingFoundryName")
    .WithDescription("The name of the existing Azure Foundry resource.");
var existingFoundryResourceGroup = builder.AddParameter("existingFoundryResourceGroup")
    .WithDescription("The resource group of the existing Azure Foundry resource.");

var foundry = builder.AddFoundry("foundry")
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
var sessions = db.AddContainer("sessions", "/conversationId");
var conversations = db.AddContainer("conversations", "/conversationId");

var geocodingMcpServer = builder.AddProject("geocodingmcpserver", "../geocoding-mcp-server/GeocodingMcpServer.csproj")
    .WithHttpHealthCheck("/health");

var restaurantAgent = builder.AddProject("restaurantagent", "../restaurant-agent/RestaurantAgent.csproj")
    .WithHttpHealthCheck("/health")
    .WithReference(foundry).WaitFor(foundry)
    .WithReference(sessions).WaitFor(sessions)
    .WithReference(conversations).WaitFor(conversations)
    .WithReference(geocodingMcpServer).WaitFor(geocodingMcpServer)
    .WithEnvironment("AZURE_TENANT_ID", tenantId)
    .WithUrls((e) =>
    {
        e.Urls.Add(new() { Url = "/agenta2a/v1/card", DisplayText = "🤖Restaurant Agent A2A Card", Endpoint = e.GetEndpoint("https") });
    });

var activitiesAgent = builder.AddProject("activitiesagent", "../activities-agent/ActivitiesAgent.csproj")
    .WithHttpHealthCheck("/health")
    .WithReference(foundry).WaitFor(foundry)
    .WithReference(sessions).WaitFor(sessions)
    .WithReference(conversations).WaitFor(conversations)
    .WithReference(geocodingMcpServer).WaitFor(geocodingMcpServer)
    .WithEnvironment("AZURE_TENANT_ID", tenantId)
    .WithUrls((e) =>
    {
        e.Urls.Add(new() { Url = "/agenta2a/v1/card", DisplayText = "🎭Activities Agent A2A Card", Endpoint = e.GetEndpoint("https") });
    });

var accommodationAgent = builder.AddProject("accommodationagent", "../accommodation-agent/AccommodationAgent.csproj")
    .WithHttpHealthCheck("/health")
    .WithReference(foundry).WaitFor(foundry)
    .WithReference(sessions).WaitFor(sessions)
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
    .WithReference(sessions).WaitFor(sessions)
    .WithReference(conversations).WaitFor(conversations)
    .WithReference(restaurantAgent).WaitFor(restaurantAgent)
    .WithReference(activitiesAgent).WaitFor(activitiesAgent)
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
