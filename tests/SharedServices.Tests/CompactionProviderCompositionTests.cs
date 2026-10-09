#pragma warning disable MAAI001 // Exercise real public agent/provider composition.

using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Moq.Protected;
using System.Reflection;

namespace SharedServices.Tests;

public class CompactionProviderCompositionTests
{
    [Theory]
    [InlineData("profile", true)]
    [InlineData("profile", false)]
    [InlineData("capability", true)]
    [InlineData("capability", false)]
    [InlineData("pair", true)]
    [InlineData("pair", false)]
    [InlineData("partial-window", true)]
    [InlineData("partial-window", false)]
    [InlineData("provider-setting", true)]
    [InlineData("provider-setting", false)]
    [InlineData("callback", true)]
    [InlineData("callback", false)]
    public void FactoryFailureDisposesOnlyOwnedClientAndPreservesOriginalError(string failure, bool ownsClient)
    {
        var fixture = new HistoryCosmosFixture();
        var client = Client(fixture);
        var options = new CosmosChatHistoryProviderOptions();
        IHistoryCompactor? compactor = null;
        var expected = new InvalidOperationException("configuration failed");
        switch (failure)
        {
            case "profile": options.Compaction = new(); break;
            case "capability":
                options.Compaction = Options() with { Mode = HistoryCompactionMode.Background };
                compactor = new TrackingCompactor();
                break;
            case "pair": options.Compaction = Options(); break;
            case "partial-window":
                options.Compaction = Options();
                options.MaxMessagesToRetrieve = 1;
                compactor = new TrackingCompactor();
                break;
            case "provider-setting": options.MaxItemCount = 0; break;
            case "callback": options.ConfigureProvider = _ => throw expected; break;
        }
        var factory = typeof(CosmosChatHistoryProviderExtensions)
            .GetMethod("BuildProvider", BindingFlags.NonPublic | BindingFlags.Static)!;

        var error = Assert.Throws<TargetInvocationException>(() => factory.Invoke(null,
            [client.Object, "database", "conversations", options, ownsClient, null, compactor]));

        if (failure == "callback")
            Assert.Same(expected, error.InnerException);
        else
        {
            var expectedType = failure switch
            {
                "profile" or "pair" or "partial-window" => typeof(ArgumentException),
                "capability" => typeof(NotSupportedException),
                _ => typeof(ArgumentOutOfRangeException)
            };
            Assert.IsType(expectedType, error.InnerException);
        }
        client.Protected().Verify("Dispose", ownsClient ? Times.Once() : Times.Never(), ItExpr.IsAny<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulFactoryTransfersOnlyOwnedClientToProvider(bool ownsClient)
    {
        var fixture = new HistoryCosmosFixture();
        var client = Client(fixture);
        var factory = typeof(CosmosChatHistoryProviderExtensions)
            .GetMethod("BuildProvider", BindingFlags.NonPublic | BindingFlags.Static)!;
        var provider = Assert.IsType<CosmosChatHistoryProvider>(factory.Invoke(null,
            [client.Object, "database", "conversations", new CosmosChatHistoryProviderOptions(), ownsClient, null, null]));
        client.Protected().Verify("Dispose", Times.Never(), ItExpr.IsAny<bool>());

        provider.Dispose();

        client.Protected().Verify("Dispose", ownsClient ? Times.Once() : Times.Never(), ItExpr.IsAny<bool>());
    }

    [Fact]
    public async Task DisabledProviderDoesNotResolveARegisteredCompactor()
    {
        var services = Services();
        services.AddHistoryCompactor<TrackingCompactor>("test", _ => throw new InvalidOperationException());
        services.AddCosmosChatHistoryProvider("history");
        using var serviceProvider = services.BuildServiceProvider();
        using var provider = Compose(serviceProvider);

        var error = await Record.ExceptionAsync(() => LoadAsync(provider));

        Assert.Null(error);
    }

    [Fact]
    public async Task FactoryIsResolvedOnlyOnceAtProviderConstruction()
    {
        var services = Services();
        var resolutions = 0;
        services.AddKeyedTransient<IHistoryCompactor>("test", (_, _) =>
        {
            resolutions++;
            return new TrackingCompactor();
        });
        services.AddCosmosChatHistoryProvider("history", options => options.Compaction = Options());
        using var serviceProvider = services.BuildServiceProvider();
        using var provider = Compose(serviceProvider);
        await LoadAsync(provider);
        await LoadAsync(provider);

        Assert.Equal(1, resolutions);
    }

    [Fact]
    public void MissingFactoryFailsDuringComposition()
    {
        var services = Services();
        services.AddCosmosChatHistoryProvider("history", options => options.Compaction = Options());
        using var serviceProvider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => Compose(serviceProvider));
    }

    [Fact]
    public async Task RegistrationSnapshotsRetentionAndImmutableCompactionOptions()
    {
        var services = Services();
        CosmosChatHistoryProviderOptions? retained = null;
        var compactor = new TrackingCompactor();
        var configured = Options() with { Timeout = TimeSpan.FromSeconds(5) };
        services.AddHistoryCompactor("test", _ => compactor);
        services.AddCosmosChatHistoryProvider("history", options =>
        {
            options.Compaction = configured;
            options.MessageTtlSeconds = 604800;
            retained = options;
        });
        Assert.NotNull(retained);
        retained.Compaction = null;
        retained.MessageTtlSeconds = 1;
        using var serviceProvider = services.BuildServiceProvider();
        using var provider = Compose(serviceProvider);

        await LoadAsync(provider);

        Assert.Equal((604800, configured), (provider.MessageTtlSeconds, compactor.LastOptions));
    }

    [Fact]
    public async Task PerAgentOverrideDoesNotModifyRegisteredBaseProfileOrRetention()
    {
        var services = Services();
        var first = new TrackingCompactor();
        var second = new TrackingCompactor();
        services.AddHistoryCompactor("first", _ => first);
        services.AddHistoryCompactor("second", _ => second);
        services.AddCosmosChatHistoryProvider("history", options =>
        {
            options.Compaction = Options() with { CompactorKey = "first" };
            options.MessageTtlSeconds = 604800;
        });
        using var serviceProvider = services.BuildServiceProvider();
        using var overridden = Assert.IsType<CosmosChatHistoryProvider>(
            new ChatClientAgentOptions().WithCosmosChatHistoryProvider(serviceProvider, options =>
            {
                options.Compaction = Options() with { CompactorKey = "second" };
                options.MessageTtlSeconds = 86400;
            }).ChatHistoryProvider);
        using var original = Compose(serviceProvider);
        await LoadAsync(overridden);
        await LoadAsync(original);

        Assert.Equal(("first", "second", 604800, 86400),
            (first.LastOptions?.CompactorKey, second.LastOptions?.CompactorKey,
                original.MessageTtlSeconds, overridden.MessageTtlSeconds));
    }

    [Fact]
    public void EscapeHatchCannotEnablePartialWindowWithCompaction()
    {
        var services = Services();
        services.AddHistoryCompactor("test", _ => new TrackingCompactor());
        services.AddCosmosChatHistoryProvider("history", options =>
        {
            options.Compaction = Options();
            options.ConfigureProvider = provider => provider.MaxMessagesToRetrieve = 2;
        });
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<ArgumentException>(() => Compose(serviceProvider));

        Assert.StartsWith("Compaction and MaxMessagesToRetrieve cannot be enabled together. Set MaxMessagesToRetrieve to null so compaction reads the complete history, or disable Compaction to use a partial-history window.",
            exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredMessageLimitIsSnapshottedAndCanBeOverriddenPerProvider()
    {
        var services = Services();
        CosmosChatHistoryProviderOptions? retained = null;
        services.AddCosmosChatHistoryProvider("history", options =>
        {
            options.MaxMessagesToRetrieve = 2;
            retained = options;
        });
        Assert.NotNull(retained);
        retained.MaxMessagesToRetrieve = 100;
        using var serviceProvider = services.BuildServiceProvider();
        using var original = Compose(serviceProvider);
        using var overridden = Assert.IsType<CosmosChatHistoryProvider>(
            new ChatClientAgentOptions().WithCosmosChatHistoryProvider(serviceProvider,
                options => options.MaxMessagesToRetrieve = null).ChatHistoryProvider);

        Assert.Equal(2, original.MaxMessagesToRetrieve);
        Assert.Null(overridden.MaxMessagesToRetrieve);
    }

    [Fact]
    public void FactoryRejectsPartialWindowAndCompaction()
    {
        var services = Services();
        services.AddHistoryCompactor("test", _ => new TrackingCompactor());
        services.AddCosmosChatHistoryProvider("history", options =>
        {
            options.Compaction = Options();
            options.MaxMessagesToRetrieve = 2;
        });
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<ArgumentException>(() => Compose(serviceProvider));

        Assert.StartsWith("Compaction and MaxMessagesToRetrieve cannot be enabled together. Set MaxMessagesToRetrieve to null so compaction reads the complete history, or disable Compaction to use a partial-history window.",
            exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandaloneExtensionUsesAnExplicitInjectedCompactor()
    {
        var fixture = new HistoryCosmosFixture();
        var client = Client(fixture);
        var plugin = new TrackingCompactor();
        using var provider = Assert.IsType<CosmosChatHistoryProvider>(
            new ChatClientAgentOptions().WithCosmosDBChatHistoryProvider(client.Object, "database", "conversations",
                options => options.Compaction = Options(), compactor: plugin).ChatHistoryProvider);

        await LoadAsync(provider);

        Assert.Equal(Options(), plugin.LastOptions);
    }

    [Fact]
    public async Task DirectContainerCompositionResolvesCompactorAndSnapshotsAgentOptions()
    {
        var services = Services();
        var plugin = new TrackingCompactor();
        services.AddHistoryCompactor("test", _ => plugin);
        using var serviceProvider = services.BuildServiceProvider();
        var container = serviceProvider.GetRequiredKeyedService<Container>("history");
        CosmosChatHistoryProviderOptions? retained = null;
        var agentOptions = new ChatClientAgentOptions();
        var returned = agentOptions.WithCosmosChatHistoryProvider(container, serviceProvider, options =>
        {
            options.Compaction = Options();
            options.MessageTtlSeconds = 604800;
            options.MaxItemCount = 7;
            options.MaxBatchSize = 5;
            retained = options;
        });
        using var provider = Assert.IsType<CosmosChatHistoryProvider>(agentOptions.ChatHistoryProvider);
        Assert.NotNull(retained);
        retained.Compaction = null;
        retained.MessageTtlSeconds = 1;

        await LoadAsync(provider);

        Assert.Same(agentOptions, returned);
        Assert.Null(serviceProvider.GetService<CosmosChatHistoryProviderRegistration>());
        Assert.Equal((604800, 7, 5, Options()),
            (provider.MessageTtlSeconds, provider.MaxItemCount, provider.MaxBatchSize, plugin.LastOptions));
    }

    [Fact]
    public async Task ContainerFactoryIsEvaluatedOnceDuringCompositionNotOnEachLoad()
    {
        var services = Services();
        services.AddHistoryCompactor<TrackingCompactor>("unused", _ => throw new InvalidOperationException());
        using var serviceProvider = services.BuildServiceProvider();
        var calls = 0;
        var agentOptions = new ChatClientAgentOptions().WithCosmosChatHistoryProvider(sp =>
        {
            Assert.Same(serviceProvider, sp);
            calls++;
            return sp.GetRequiredKeyedService<Container>("history");
        }, serviceProvider);
        using var provider = Assert.IsType<CosmosChatHistoryProvider>(agentOptions.ChatHistoryProvider);

        await LoadAsync(provider);
        await LoadAsync(provider);

        Assert.Equal(1, calls);
        Assert.Equal(86400, provider.MessageTtlSeconds);
        Assert.Null(provider.MaxMessagesToRetrieve);
    }

    [Fact]
    public void DirectCompositionDoesNotModifyOrInheritAnotherRegistration()
    {
        var services = Services();
        services.AddCosmosChatHistoryProvider("history", options => options.MessageTtlSeconds = 604800);
        using var serviceProvider = services.BuildServiceProvider();
        using var direct = Assert.IsType<CosmosChatHistoryProvider>(new ChatClientAgentOptions()
            .WithCosmosChatHistoryProvider(serviceProvider.GetRequiredKeyedService<Container>("history"),
                serviceProvider, options => options.MaxMessagesToRetrieve = 3).ChatHistoryProvider);
        using var original = Compose(serviceProvider);
        using var legacyNullConfigure = Assert.IsType<CosmosChatHistoryProvider>(new ChatClientAgentOptions()
            .WithCosmosChatHistoryProvider(serviceProvider, null).ChatHistoryProvider);

        Assert.Equal((86400, 3, 604800, 604800),
            (direct.MessageTtlSeconds, direct.MaxMessagesToRetrieve, original.MessageTtlSeconds,
                legacyNullConfigure.MessageTtlSeconds));
    }

    [Fact]
    public async Task PerAgentContainerCompositionKeepsMessagesAndRetentionSeparate()
    {
        var first = new HistoryCosmosFixture();
        var second = new HistoryCosmosFixture();
        var services = Services(first);
        var other = ConfigureContainer(second);
        services.AddSingleton(other);
        using var serviceProvider = services.BuildServiceProvider();
        using var firstProvider = Assert.IsType<CosmosChatHistoryProvider>(new ChatClientAgentOptions()
            .WithCosmosChatHistoryProvider(serviceProvider.GetRequiredKeyedService<Container>("history"),
                serviceProvider, options => options.MessageTtlSeconds = 86400).ChatHistoryProvider);
        using var secondProvider = Assert.IsType<CosmosChatHistoryProvider>(new ChatClientAgentOptions()
            .WithCosmosChatHistoryProvider(sp => sp.GetRequiredService<Container>(),
                serviceProvider, options => options.MessageTtlSeconds = 604800).ChatHistoryProvider);
        var firstSession = new TestAgentSession();
        var secondSession = new TestAgentSession();
        SessionPersistenceState.Initialize(firstSession, SessionStorageAddress.Create("first", "same-context"));
        SessionPersistenceState.Initialize(secondSession, SessionStorageAddress.Create("second", "same-context"));
        var agent = new Mock<AIAgent>().Object;

        await firstProvider.InvokedAsync(new(agent, firstSession, [new(ChatRole.User, "first message")], []));
        await secondProvider.InvokedAsync(new(agent, secondSession, [new(ChatRole.User, "second message")], []));

        Assert.Equal("first message", Assert.Single((await first.CreateRepository()
            .ReadAsync(SessionPersistenceState.GetRequired(firstSession).ActiveHistory)).Messages).Text);
        Assert.Equal("second message", Assert.Single((await second.CreateRepository()
            .ReadAsync(SessionPersistenceState.GetRequired(secondSession).ActiveHistory)).Messages).Text);
        Assert.Equal(86400, Assert.Single(first.Documents, document =>
            document.GetProperty("type").GetString() == "ChatMessage").GetProperty("ttl").GetInt32());
        Assert.Equal(604800, Assert.Single(second.Documents, document =>
            document.GetProperty("type").GetString() == "ChatMessage").GetProperty("ttl").GetInt32());
    }

    [Fact]
    public void DirectContainerProviderDoesNotDisposeTheSharedCosmosClient()
    {
        var fixture = new HistoryCosmosFixture();
        var client = Client(fixture);
        var database = new Mock<Database>();
        database.SetupGet(value => value.Id).Returns("database");
        database.SetupGet(value => value.Client).Returns(client.Object);
        fixture.Container.SetupGet(value => value.Database).Returns(database.Object);
        fixture.Container.SetupGet(value => value.Id).Returns("conversations");
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var provider = Assert.IsType<CosmosChatHistoryProvider>(new ChatClientAgentOptions()
            .WithCosmosChatHistoryProvider(fixture.Container.Object, serviceProvider).ChatHistoryProvider);

        provider.Dispose();

        client.Protected().Verify("Dispose", Times.Never(), ItExpr.IsAny<bool>());
        client.Object.Dispose();
        client.Protected().Verify("Dispose", Times.Once(), ItExpr.IsAny<bool>());
    }

    [Fact]
    public void NewHistoryOverloadsRejectNullArgumentsAndNullFactoryResults()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        Assert.Throws<ArgumentNullException>(() =>
            new ChatClientAgentOptions().WithCosmosChatHistoryProvider((Container)null!, serviceProvider));
        Assert.Throws<ArgumentNullException>(() => new ChatClientAgentOptions()
            .WithCosmosChatHistoryProvider((Func<IServiceProvider, Container>)null!, serviceProvider));
        Assert.Throws<ArgumentNullException>(() => new ChatClientAgentOptions()
            .WithCosmosChatHistoryProvider(_ => null!, serviceProvider));
        Assert.Throws<ArgumentNullException>(() => new ChatClientAgentOptions()
            .WithCosmosChatHistoryProvider(_ => throw new InvalidOperationException(), null!));
    }

    [Fact]
    public void DirectCompositionKeepsPartialHistoryAndCompactionValidation()
    {
        var services = Services();
        services.AddHistoryCompactor("test", _ => new TrackingCompactor());
        using var serviceProvider = services.BuildServiceProvider();

        Assert.Throws<ArgumentException>(() => new ChatClientAgentOptions()
            .WithCosmosChatHistoryProvider(serviceProvider.GetRequiredKeyedService<Container>("history"),
                serviceProvider, options =>
                {
                    options.Compaction = Options();
                    options.MaxMessagesToRetrieve = 2;
                }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void EmptyOrExplicitlyDisabledProfileRegistersNothing(string? enabled)
    {
        var services = new ServiceCollection();
        var section = Section(new Dictionary<string, string?> { ["Enabled"] = enabled });

        var options = services.AddHistoryCompactionProfile(section);

        Assert.Equal((null, 0), (options, services.Count));
    }

    [Fact]
    public void ExplicitSlidingWindowProfileRequiresMaxTurns()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddHistoryCompactionProfile(Section(
            new() { ["CompactorKey"] = "test-sliding-window" })));
    }

    [Theory]
    [InlineData("CompactorKey")]
    public void ExplicitEnabledProfileRequiresItsSafetyConfiguration(string omitted)
    {
        var values = new Dictionary<string, string?>
        {
            ["CompactorKey"] = "test-sliding-window", ["MaxTurns"] = "2"
        };
        values.Remove(omitted);

        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddHistoryCompactionProfile(Section(values)));
    }

    [Fact]
    public async Task EnabledProfileCompactsUsingTheConfiguredStrategy()
    {
        var services = new ServiceCollection();
        var options = services.AddHistoryCompactionProfile(Section(new()
        {
            ["Enabled"] = "true", ["CompactorKey"] = "test-sliding-window", ["MaxTurns"] = "1"
        }));
        Assert.NotNull(options);
        using var serviceProvider = services.BuildServiceProvider();
        var compactor = serviceProvider.GetHistoryCompactor(options);
        Assert.NotNull(compactor);

        var result = await compactor.CompactAsync(new("agent", "source",
            [new(ChatRole.User, "old"), new(ChatRole.Assistant, "old answer"),
             new(ChatRole.User, "recent"), new(ChatRole.Assistant, "recent answer")], options));

        Assert.Equal(HistoryCompactionStatus.Completed, result.Status);
        Assert.Equal(new[] { "recent", "recent answer" }, result.Messages.Select(message => message.Text));
    }

    [Fact]
    public void BuiltInSlidingWindowProfileSupportsLocalBackgroundMode()
    {
        var services = new ServiceCollection();
        var options = services.AddHistoryCompactionProfile(Section(
            new() { ["CompactorKey"] = "test-sliding-window", ["Mode"] = "Background", ["MaxTurns"] = "2" }));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<LocalBackgroundHistoryCompactor>(provider.GetHistoryCompactor(options));
    }

    [Fact]
    public void ProfileReadsBackgroundSaveWaitTimeout()
    {
        var options = new ServiceCollection().AddHistoryCompactionProfile(Section(new()
        {
            ["CompactorKey"] = "test-sliding-window",
            ["Mode"] = "Background",
            ["MaxTurns"] = "2",
            ["BackgroundSaveWaitTimeout"] = "00:00:00.250"
        }));

        Assert.Equal(TimeSpan.FromMilliseconds(250), options!.BackgroundSaveWaitTimeout);
    }

    [Fact]
    public async Task SlidingWindowTestProfileBuildsARealModelFreeMafCompactor()
    {
        var services = new ServiceCollection();
        var profile = services.AddHistoryCompactionProfile(Section(new()
        {
            ["CompactorKey"] = "test-sliding-window", ["Mode"] = "Foreground",
            ["Timeout"] = "00:00:05", ["MaxTurns"] = "1"
        }));
        using var serviceProvider = services.BuildServiceProvider();
        var compactor = serviceProvider.GetHistoryCompactor(profile);
        Assert.NotNull(compactor);
        Assert.NotNull(profile);
        var request = new HistoryCompactionRequest("agent", "binding",
            [new(ChatRole.User, "old"), new(ChatRole.Assistant, "old reply"),
                new(ChatRole.User, "recent"), new(ChatRole.Assistant, "recent reply")], profile);

        var result = await compactor.CompactAsync(request);

        Assert.Equal(new[] { "recent", "recent reply" }, result.Messages.Select(message => message.Text));
    }

    [Fact]
    public void CustomProfileUsesNormalKeyedRegistrationWithoutBuiltInStrategyRegistry()
    {
        var services = new ServiceCollection();
        var custom = new TrackingCompactor();
        services.AddHistoryCompactor("custom-summarizer", _ => custom);
        var options = services.AddHistoryCompactionProfile(Section(new()
        {
            ["CompactorKey"] = "custom-summarizer"
        }));
        using var serviceProvider = services.BuildServiceProvider();

        Assert.Same(custom, serviceProvider.GetHistoryCompactor(options));
    }

    private static IConfigurationSection Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Where(entry => entry.Value is not null)
            .Select(entry => new KeyValuePair<string, string?>($"HistoryCompaction:{entry.Key}", entry.Value)))
            .Build().GetSection("HistoryCompaction");

    private static HistoryCompactionOptions Options() => new() { CompactorKey = "test" };

    private static CosmosChatHistoryProvider Compose(IServiceProvider services) =>
        Assert.IsType<CosmosChatHistoryProvider>(new ChatClientAgentOptions().WithCosmosChatHistoryProvider(services).ChatHistoryProvider);

    private static IServiceCollection Services(HistoryCosmosFixture? fixture = null)
    {
        fixture ??= new HistoryCosmosFixture();
        return new ServiceCollection().AddKeyedSingleton("history", ConfigureContainer(fixture));
    }

    private static Container ConfigureContainer(HistoryCosmosFixture fixture)
    {
        var client = Client(fixture);
        var database = new Mock<Database>();
        database.SetupGet(value => value.Id).Returns("database");
        database.SetupGet(value => value.Client).Returns(client.Object);
        fixture.Container.SetupGet(value => value.Database).Returns(database.Object);
        fixture.Container.SetupGet(value => value.Id).Returns("conversations");
        return fixture.Container.Object;
    }

    private static Mock<CosmosClient> Client(HistoryCosmosFixture fixture)
    {
        var client = new Mock<CosmosClient>();
        client.Setup(value => value.GetContainer("database", "conversations")).Returns(fixture.Container.Object);
        return client;
    }

    private static async Task LoadAsync(CosmosChatHistoryProvider provider)
    {
        var session = new TestAgentSession();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create("test", "session"));
        await provider.InvokingAsync(new(new Mock<AIAgent>().Object, session, []));
    }

    private sealed class TrackingCompactor : IHistoryCompactor
    {
        internal HistoryCompactionOptions? LastOptions { get; private set; }
        public IReadOnlySet<HistoryCompactionMode> SupportedModes { get; } =
            new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Foreground };
        public Task<HistoryCompactionResult> CompactAsync(HistoryCompactionRequest request, CancellationToken cancellationToken = default)
        {
            LastOptions = request.Options;
            var size = HistoryCompactionValidation.Measure(request.Messages);
            return Task.FromResult(new HistoryCompactionResult(HistoryCompactionStatus.Unchanged,
                request.SourceBinding, request.Messages, size, size));
        }
    }
}
