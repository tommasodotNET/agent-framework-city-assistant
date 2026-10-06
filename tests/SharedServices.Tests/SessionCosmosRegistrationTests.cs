using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;

#pragma warning disable MAAI001

namespace SharedServices.Tests;

public class SessionCosmosRegistrationTests
{
    private static ServiceCollection Services(SessionCosmosSdkFixture sdk)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<Container>("sessions", sdk.Container.Object);
        services.AddCosmosAgentSessionStore("sessions", options => options.TtlSeconds = 604800);
        return services;
    }

    private static void RegisterAgent(ServiceCollection services) =>
        services.AddAIAgent("agent", (_, _) => new ChatClientAgent(new Mock<IChatClient>().Object,
            new ChatClientAgentOptions { Id = "agent", Name = "agent" })).WithCosmosSessionStore();

    [Fact]
    public void SnapshotOptionsAreRegisteredAsOneSingletonForHistoryRetention()
    {
        var services = Services(new());
        using var provider = services.BuildServiceProvider();

        Assert.Equal(604800, provider.GetRequiredService<CosmosAgentSessionStoreOptions>().TtlSeconds);
    }

    [Fact]
    public void SharedRepositoryIsAvailableToVoice()
    {
        var services = Services(new());
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<CosmosSessionRepository>(), provider.GetRequiredService<CosmosSessionRepository>());
    }

    [Fact]
    public void DirectClientRegistrationPreservesOptionsAndRepository()
    {
        var sdk = new SessionCosmosSdkFixture();
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer("database", "sessions")).Returns(sdk.Container.Object);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCosmosAgentSessionStore(client.Object, "database", "sessions");
        using var provider = services.BuildServiceProvider();

        Assert.Equal(-1, provider.GetRequiredService<CosmosAgentSessionStoreOptions>().TtlSeconds);
    }

    [Fact]
    public async Task AnonymousHostAlwaysUsesWrapperAndAllowsMissingProvider()
    {
        var services = Services(new());
        RegisterAgent(services);
        using var provider = services.BuildServiceProvider();
        var agent = provider.GetRequiredKeyedService<AIAgent>("agent");
        var store = provider.GetRequiredKeyedService<AgentSessionStore>("agent");

        var session = await store.GetOrCreateSessionAsync(agent, new("lookup"));

        Assert.Equal((typeof(IsolationKeyScopedAgentSessionStore), "agent"),
            (store.GetType(), SessionPersistenceState.GetRequired(session).AgentId));
    }

    [Fact]
    public async Task RegisteredProviderRejectsMissingKeyEvenWhenNonStrictOptionsWereConfigured()
    {
        var services = Services(new());
        var isolation = new Mock<AgentIsolationKeyProvider>();
        isolation.Setup(p => p.GetIsolationKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        services.AddSingleton(isolation.Object);
        services.Configure<IsolationKeyScopedAgentSessionStoreOptions>(o => o.Strict = false);
        RegisterAgent(services);
        using var provider = services.BuildServiceProvider();
        var agent = provider.GetRequiredKeyedService<AIAgent>("agent");
        var store = provider.GetRequiredKeyedService<AgentSessionStore>("agent");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetOrCreateSessionAsync(agent, new("lookup")).AsTask());
    }

    [Fact]
    public async Task RegisteredProviderScopesSnapshotContext()
    {
        var services = Services(new());
        var isolation = new Mock<AgentIsolationKeyProvider>();
        isolation.Setup(p => p.GetIsolationKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("trusted-owner");
        services.AddSingleton(isolation.Object);
        RegisterAgent(services);
        using var provider = services.BuildServiceProvider();
        var agent = provider.GetRequiredKeyedService<AIAgent>("agent");

        var session = await provider.GetRequiredKeyedService<AgentSessionStore>("agent").GetOrCreateSessionAsync(agent, new("lookup"));

        Assert.Equal(StorageScope.Create("lookup", new Dictionary<string, string> { ["isolation"] = "trusted-owner" }),
            SessionPersistenceState.GetRequired(session).ActiveHistory.ScopeKey);
    }
}
