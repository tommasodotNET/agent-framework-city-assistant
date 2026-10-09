#pragma warning disable MAAI001 // Exercise real public agent/provider composition.

using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace SharedServices.Tests;

public class CompactionProviderCompositionTests
{
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
            new() { ["CompactorKey"] = "test-sliding-window", ["MaxHistoryUtf8Bytes"] = "10000" })));
    }

    [Theory]
    [InlineData("CompactorKey")]
    public void ExplicitEnabledProfileRequiresItsSafetyConfiguration(string omitted)
    {
        var values = new Dictionary<string, string?>
        {
            ["CompactorKey"] = "test-sliding-window", ["MaxHistoryUtf8Bytes"] = "10000", ["MaxTurns"] = "2"
        };
        values.Remove(omitted);

        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddHistoryCompactionProfile(Section(values)));
    }

    [Fact]
    public async Task EnabledProfileWithoutByteCapCompactsUsingTheConfiguredStrategy()
    {
        var services = new ServiceCollection();
        var options = services.AddHistoryCompactionProfile(Section(new()
        {
            ["Enabled"] = "true", ["CompactorKey"] = "test-sliding-window", ["MaxTurns"] = "1"
        }));
        Assert.NotNull(options);
        Assert.Null(options.MaxHistoryUtf8Bytes);
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
    public void ExplicitJsonNullDisablesByteCap()
    {
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{"HistoryCompaction":{"Enabled":true,"CompactorKey":"test-sliding-window","MaxTurns":1,"MaxHistoryUtf8Bytes":null}}"""));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = new ServiceCollection().AddHistoryCompactionProfile(configuration.GetSection("HistoryCompaction"));

        Assert.NotNull(options);
        Assert.Null(options.MaxHistoryUtf8Bytes);
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
            ["MaxHistoryUtf8Bytes"] = "10000", ["Timeout"] = "00:00:05", ["MaxTurns"] = "1"
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
            ["CompactorKey"] = "custom-summarizer", ["MaxHistoryUtf8Bytes"] = "10000"
        }));
        using var serviceProvider = services.BuildServiceProvider();

        Assert.Same(custom, serviceProvider.GetHistoryCompactor(options));
    }

    private static IConfigurationSection Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Where(entry => entry.Value is not null)
            .Select(entry => new KeyValuePair<string, string?>($"HistoryCompaction:{entry.Key}", entry.Value)))
            .Build().GetSection("HistoryCompaction");

    private static HistoryCompactionOptions Options() => new() { CompactorKey = "test", MaxHistoryUtf8Bytes = 10000 };

    private static CosmosChatHistoryProvider Compose(IServiceProvider services) =>
        Assert.IsType<CosmosChatHistoryProvider>(new ChatClientAgentOptions().WithCosmosChatHistoryProvider(services).ChatHistoryProvider);

    private static IServiceCollection Services()
    {
        var fixture = new HistoryCosmosFixture();
        var client = Client(fixture);
        var database = new Mock<Database>();
        database.SetupGet(value => value.Id).Returns("database");
        database.SetupGet(value => value.Client).Returns(client.Object);
        fixture.Container.SetupGet(value => value.Database).Returns(database.Object);
        fixture.Container.SetupGet(value => value.Id).Returns("conversations");
        return new ServiceCollection().AddKeyedSingleton("history", fixture.Container.Object);
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
