using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstanceAndFactoryOverloadsPersistWithoutGlobalRegistration(bool useFactory)
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = new CosmosAgentSessionStore(sdk.Repository,
            NullLogger<CosmosAgentSessionStore>.Instance, ttl: 86400);
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddAIAgent("agent", (_, name) => Agent(name));
        var returned = useFactory
            ? builder.WithCosmosSessionStore((_, _) => store)
            : builder.WithCosmosSessionStore(store);
        using var provider = services.BuildServiceProvider();
        var agent = provider.GetRequiredKeyedService<AIAgent>("agent");
        var hostedStore = provider.GetRequiredKeyedService<AgentSessionStore>("agent");
        var key = new AgentSessionStoreKey("same-context");
        var session = await hostedStore.GetOrCreateSessionAsync(agent, key);

        await hostedStore.SaveSessionAsync(agent, key, session);
        var restored = await hostedStore.GetSessionAsync(agent, key);

        Assert.Same(builder, returned);
        Assert.Null(provider.GetService<CosmosAgentSessionStore>());
        Assert.IsType<IsolationKeyScopedAgentSessionStore>(hostedStore);
        Assert.NotNull(restored);
        Assert.NotSame(session, restored);
        Assert.Equal(SessionPersistenceState.GetRequired(session), SessionPersistenceState.GetRequired(restored));
        Assert.Equal(86400, JsonSerializer.Deserialize<SessionDocument>(
            Assert.Single(sdk.Requests, request => request.Operation == "create").Body)!.Ttl);
    }

    [Fact]
    public async Task AgentFactoriesAreLazySingletonsAndKeepIndependentContainersAndRetention()
    {
        var first = new SessionCosmosSdkFixture();
        var second = new SessionCosmosSdkFixture();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<Container>("first", first.Container.Object);
        services.AddKeyedSingleton<Container>("second", second.Container.Object);
        var calls = new List<string>();
        foreach (var name in new[] { "first", "second" })
        {
            services.AddAIAgent(name, (_, agentName) => Agent(agentName))
                .WithCosmosSessionStore((sp, agentName) =>
                {
                    calls.Add(agentName);
                    return new CosmosAgentSessionStore(sp.GetRequiredKeyedService<Container>(agentName),
                        sp.GetRequiredService<ILogger<CosmosAgentSessionStore>>(),
                        ttl: agentName == "first" ? 86400 : 604800);
                });
        }
        using var provider = services.BuildServiceProvider();
        Assert.Empty(calls);

        foreach (var name in new[] { "first", "second" })
        {
            var agent = provider.GetRequiredKeyedService<AIAgent>(name);
            var store = provider.GetRequiredKeyedService<AgentSessionStore>(name);
            Assert.Same(store, provider.GetRequiredKeyedService<AgentSessionStore>(name));
            var key = new AgentSessionStoreKey("same-context");
            var session = await store.GetOrCreateSessionAsync(agent, key);
            await store.SaveSessionAsync(agent, key, session);
        }

        Assert.Equal(new[] { "first", "second" }, calls);
        var firstDocument = JsonSerializer.Deserialize<SessionDocument>(
            Assert.Single(first.Requests, request => request.Operation == "create").Body)!;
        var secondDocument = JsonSerializer.Deserialize<SessionDocument>(
            Assert.Single(second.Requests, request => request.Operation == "create").Body)!;
        Assert.Equal(("first", 86400, "second", 604800),
            (firstDocument.AgentId, firstDocument.Ttl, secondDocument.AgentId, secondDocument.Ttl));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewOverloadsPreserveStrictIsolationAndTrustedScope(bool useFactory)
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = new CosmosAgentSessionStore(sdk.Repository, NullLogger<CosmosAgentSessionStore>.Instance);
        var isolation = new Mock<AgentIsolationKeyProvider>();
        isolation.Setup(value => value.GetIsolationKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(isolation.Object);
        services.Configure<IsolationKeyScopedAgentSessionStoreOptions>(options => options.Strict = false);
        var builder = services.AddAIAgent("agent", (_, name) => Agent(name));
        if (useFactory)
            builder.WithCosmosSessionStore((_, _) => store);
        else
            builder.WithCosmosSessionStore(store);
        using var provider = services.BuildServiceProvider();
        var agent = provider.GetRequiredKeyedService<AIAgent>("agent");
        var hostedStore = provider.GetRequiredKeyedService<AgentSessionStore>("agent");
        var key = new AgentSessionStoreKey("lookup");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hostedStore.GetOrCreateSessionAsync(agent, key).AsTask());
        isolation.Setup(value => value.GetIsolationKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("trusted-owner");
        var session = await hostedStore.GetOrCreateSessionAsync(agent, key);

        Assert.Equal(StorageScope.Create("lookup", new Dictionary<string, string> { ["isolation"] = "trusted-owner" }),
            SessionPersistenceState.GetRequired(session).ActiveHistory.ScopeKey);
    }

    [Fact]
    public void NewSessionOverloadsRejectNullArgumentsAndNullFactoryResults()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddAIAgent("agent", (_, name) => Agent(name));
        Assert.Throws<ArgumentNullException>(() => builder.WithCosmosSessionStore((CosmosAgentSessionStore)null!));
        Assert.Throws<ArgumentNullException>(() =>
            builder.WithCosmosSessionStore((Func<IServiceProvider, string, CosmosAgentSessionStore>)null!));
        builder.WithCosmosSessionStore((_, _) => null!);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<ArgumentNullException>(() => provider.GetRequiredKeyedService<AgentSessionStore>("agent"));
    }

    private static ChatClientAgent Agent(string name) =>
        new(new Mock<IChatClient>().Object, new ChatClientAgentOptions { Id = name, Name = name });
}
