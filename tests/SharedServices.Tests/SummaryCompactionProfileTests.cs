using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharedServices.Tests;

public class SummaryCompactionProfileTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("200", 4)]
    [InlineData("1", 4)]
    public async Task NativeSummaryTargetControlsExcludedGroupsWithoutCrossingPreservedFloor(string? target, int excludedGroups)
    {
        var settings = Settings();
        settings["TriggerTokens"] = "500";
        settings["TargetTokens"] = target;
        var services = new ServiceCollection();
        var options = services.AddHistoryCompactionProfile(Section(settings), (_, _) => new SummaryClient());
        using var provider = services.BuildServiceProvider();
        ChatMessage[] messages =
        [
            new(ChatRole.User, new string('a', 400)),
            new(ChatRole.Assistant, new string('b', 400)),
            new(ChatRole.User, new string('c', 400)),
            new(ChatRole.Assistant, new string('d', 400)),
            new(ChatRole.User, new string('e', 400)),
            new(ChatRole.Assistant, new string('f', 400))
        ];

        var result = await provider.GetHistoryCompactor(options)!.CompactAsync(new("agent", "source", messages, options!));

        Assert.Equal(new[] { "[Summary]\nTrip: Agentburg, vegetarian, budget 200." }
            .Concat(messages.Skip(excludedGroups).Select(message => message.Text)),
            result.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task SummaryProfileSummarizesOlderToolHistoryAndPreservesRecentGroups()
    {
        var client = new SummaryClient();
        var services = new ServiceCollection();
        var options = services.AddHistoryCompactionProfile(Section(Settings()), (_, _) => client);
        using var provider = services.BuildServiceProvider();
        var compactor = Assert.IsType<MafForegroundHistoryCompactor>(provider.GetHistoryCompactor(options));

        var result = await compactor.CompactAsync(Request(options!));

        Assert.Equal(new[] { "instructions", "[Summary]\nTrip: Agentburg, vegetarian, budget 200.",
            "recent question", "recent answer" }, result.Messages.Select(message => message.Text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void SummaryProfileRequiresExplicitModelBeforeCreatingAClient(string? model)
    {
        var settings = Settings();
        settings["Model"] = model;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            settings.Select(entry => new KeyValuePair<string, string?>($"HistoryCompaction:{entry.Key}", entry.Value))
                .Append(new("AI:ChatModel", "must-not-be-reused"))).Build();

        var exception = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddHistoryCompactionProfile(
            configuration.GetSection("HistoryCompaction"), (_, _) => throw new InvalidOperationException()));

        Assert.Equal("Model", exception.ParamName);
    }

    [Theory]
    [InlineData("TriggerTokens", null)]
    [InlineData("TriggerTokens", "0")]
    [InlineData("TriggerTokens", "-1")]
    [InlineData("MinimumPreservedGroups", null)]
    [InlineData("MinimumPreservedGroups", "0")]
    [InlineData("MinimumPreservedGroups", "-1")]
    public void SummaryProfileRequiresPositiveTriggerAndGroupsBeforeCreatingAClient(string setting, string? value)
    {
        var settings = Settings();
        settings[setting] = value;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddHistoryCompactionProfile(
            Section(settings), (_, _) => throw new InvalidOperationException()));

        Assert.Equal(setting, exception.ParamName);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("100")]
    [InlineData("101")]
    public void SummaryTargetMustBePositiveAndBelowTriggerBeforeCreatingAClient(string target)
    {
        var settings = Settings();
        settings["TargetTokens"] = target;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddHistoryCompactionProfile(
            Section(settings), (_, _) => throw new InvalidOperationException()));

        Assert.Equal("TargetTokens", exception.ParamName);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("99")]
    public void ValidSummaryTargetDoesNotAllocateAClientAtRegistration(string target)
    {
        var settings = Settings();
        settings["TargetTokens"] = target;

        var options = new ServiceCollection().AddHistoryCompactionProfile(
            Section(settings), (_, _) => throw new InvalidOperationException());

        Assert.NotNull(options);
    }

    [Theory]
    [InlineData("TriggerTokens", "not-a-number")]
    [InlineData("MinimumPreservedGroups", "2147483648")]
    [InlineData("TargetTokens", "not-a-number")]
    [InlineData("TargetTokens", "2147483648")]
    public void SummaryProfileRejectsInvalidIntegerConfiguration(string setting, string value)
    {
        var settings = Settings();
        settings[setting] = value;

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHistoryCompactionProfile(
            Section(settings), (_, _) => throw new NotSupportedException()));
    }

    [Fact]
    public void SummaryProfileRejectsBackgroundBeforeCreatingAClient()
    {
        var settings = Settings();
        settings["Mode"] = "Background";

        Assert.Throws<NotSupportedException>(() => new ServiceCollection().AddHistoryCompactionProfile(
            Section(settings), (_, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public void SummaryProfileRequiresDedicatedFactoryInsteadOfUsingRegisteredAgentClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(_ => throw new InvalidOperationException());

        Assert.Throws<ArgumentNullException>(() => services.AddHistoryCompactionProfile(Section(Settings())));
    }

    [Fact]
    public void MissingProfileDoesNotRegisterOrCreateSummaryClient()
    {
        var services = new ServiceCollection();

        var options = services.AddHistoryCompactionProfile(Section([]), (_, _) => throw new InvalidOperationException());

        Assert.Equal((null, 0), (options, services.Count));
    }

    [Fact]
    public void DisabledProfileIgnoresInvalidSummarySettingsAndRegistersNothing()
    {
        var services = new ServiceCollection();
        var settings = Settings();
        settings["Enabled"] = "false";
        settings["Model"] = null;
        settings["TriggerTokens"] = "-1";
        settings["TargetTokens"] = "-1";

        var options = services.AddHistoryCompactionProfile(Section(settings), (_, _) => throw new InvalidOperationException());

        Assert.Equal((null, 0), (options, services.Count));
    }

    [Fact]
    public void SummaryClientIsNotAllocatedUntilCompactorIsResolved()
    {
        var services = new ServiceCollection();
        var options = services.AddHistoryCompactionProfile(Section(Settings()), (_, _) => throw new InvalidOperationException());
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(options);
    }

    [Fact]
    public void SummaryProfileCreatesOneClientWithTheExplicitModel()
    {
        var models = new List<string>();
        var services = new ServiceCollection();
        var options = services.AddHistoryCompactionProfile(Section(Settings()), (_, model) =>
        {
            models.Add(model);
            return new SummaryClient();
        });
        using var provider = services.BuildServiceProvider();

        _ = provider.GetHistoryCompactor(options);
        _ = provider.GetHistoryCompactor(options);

        Assert.Equal(new[] { "gpt-5.4-mini" }, models);
    }

    [Fact]
    public void SummaryProfileDefaultsToForegroundWithoutByteCapOrTimeout()
    {
        var options = new ServiceCollection().AddHistoryCompactionProfile(Section(Settings()), (_, _) => new SummaryClient());

        Assert.Equal(new HistoryCompactionOptions { CompactorKey = "summary" }, options);
    }

    [Fact]
    public void SummaryProfileRetainsExplicitByteCapAndTimeout()
    {
        var settings = Settings();
        settings["MaxHistoryUtf8Bytes"] = "50000";
        settings["Timeout"] = "00:00:30";

        var options = new ServiceCollection().AddHistoryCompactionProfile(Section(settings), (_, _) => new SummaryClient());

        Assert.Equal(new HistoryCompactionOptions
        {
            CompactorKey = "summary", MaxHistoryUtf8Bytes = 50000, Timeout = TimeSpan.FromSeconds(30)
        }, options);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("50")]
    public async Task SummaryDoesNotInvokeTheAgentsFunctionInvokingClient(string? target)
    {
        var main = new SummaryClient();
        var summary = new SummaryClient();
        using var mainPipeline = new FunctionInvokingChatClient(main);
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(mainPipeline);
        var settings = Settings();
        settings["TargetTokens"] = target;
        var options = services.AddHistoryCompactionProfile(Section(settings), (_, _) => summary);
        using var provider = services.BuildServiceProvider();
        var compactor = provider.GetHistoryCompactor(options)!;

        await compactor.CompactAsync(Request(options!));

        Assert.Equal((0, 1), (main.Calls, summary.Calls));
    }

    [Fact]
    public async Task SummaryCanRunWithoutAnyAgentChatClientRegistered()
    {
        var summary = new SummaryClient();
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(_ => throw new InvalidOperationException("Agent client must not be resolved."));
        var options = services.AddHistoryCompactionProfile(Section(Settings()), (_, _) => summary);
        using var provider = services.BuildServiceProvider();

        await provider.GetHistoryCompactor(options)!.CompactAsync(Request(options!));

        Assert.Equal(1, summary.Calls);
    }

    [Fact]
    public async Task SummaryRequestHasNoTools()
    {
        var summary = new SummaryClient();
        var services = new ServiceCollection();
        var options = services.AddHistoryCompactionProfile(Section(Settings()), (_, _) => summary);
        using var provider = services.BuildServiceProvider();

        await provider.GetHistoryCompactor(options)!.CompactAsync(Request(options!));

        Assert.Null(summary.Options?.Tools);
    }

    [Fact]
    public async Task SummaryKeepsNativeSystemPromptAndAddsOnlyAPrivateFinalInstruction()
    {
        var summary = new SummaryClient();
        var services = new ServiceCollection();
        var options = services.AddHistoryCompactionProfile(Section(Settings()), (_, _) => summary);
        using var provider = services.BuildServiceProvider();
        var request = Request(options!);
        var before = System.Text.Json.JsonSerializer.Serialize(request.Messages);

        var result = await provider.GetHistoryCompactor(options)!.CompactAsync(request);

        Assert.Equal(ChatRole.System, summary.Messages[0].Role);
        Assert.StartsWith("You are a conversation summarizer.", summary.Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(ChatRole.User, summary.Messages[^1].Role);
        Assert.Contains("Do not continue the conversation", summary.Messages[^1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Messages, message => message.Text.Contains("Do not continue the conversation", StringComparison.Ordinal));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(request.Messages));
    }

    [Fact]
    public async Task SummaryReceivesOldToolResultsAsContent()
    {
        var summary = new SummaryClient();
        var services = new ServiceCollection();
        var options = services.AddHistoryCompactionProfile(Section(Settings()), (_, _) => summary);
        using var provider = services.BuildServiceProvider();

        await provider.GetHistoryCompactor(options)!.CompactAsync(Request(options!));

        Assert.Contains("Hotel and restaurant details.",
            System.Text.Json.JsonSerializer.Serialize(summary.Messages), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TriggerTokens", "100000", null)]
    [InlineData("TriggerTokens", "100000", "50")]
    [InlineData("MinimumPreservedGroups", "100", null)]
    [InlineData("MinimumPreservedGroups", "100", "50")]
    public async Task SummaryRemainsUnchangedWithoutInferenceWhenTriggerOrPreservedFloorPreventsReduction(
        string setting, string value, string? target)
    {
        var summary = new SummaryClient();
        var services = new ServiceCollection();
        var settings = Settings();
        settings[setting] = value;
        settings["TargetTokens"] = target;
        var options = services.AddHistoryCompactionProfile(Section(settings), (_, _) => summary);
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetHistoryCompactor(options)!.CompactAsync(Request(options!));

        Assert.Equal((HistoryCompactionStatus.Unchanged, 0), (result.Status, summary.Calls));
    }

    [Fact]
    public void DedicatedSummaryClientIsDisposedByItsServiceProvider()
    {
        var summary = new SummaryClient();
        var services = new ServiceCollection();
        var options = services.AddHistoryCompactionProfile(Section(Settings()), (_, _) => summary);
        using (var provider = services.BuildServiceProvider())
        {
            _ = provider.GetHistoryCompactor(options);
        }

        Assert.True(summary.Disposed);
    }

    [Fact]
    public async Task TriggerDiagnosticsLabelHistoryTokensAsEstimatedWithoutLoggingHistory()
    {
        var logger = new TestLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<MafForegroundHistoryCompactor>>(logger);
        var options = services.AddHistoryCompactionProfile(Section(Settings()), (_, _) => new SummaryClient());
        using var provider = services.BuildServiceProvider();

        await provider.GetHistoryCompactor(options)!.CompactAsync(Request(options!));

        Assert.Contains(logger.Messages, message => message.Contains(
            "estimated history tokens; configured threshold 100. This estimate is not a full-prompt or model-window budget.",
            StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("Hotel and restaurant details.", StringComparison.Ordinal));
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        ["CompactorKey"] = "summary",
        ["Model"] = "gpt-5.4-mini",
        ["TriggerTokens"] = "100",
        ["MinimumPreservedGroups"] = "2"
    };

    private static IConfigurationSection Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(entry =>
            new KeyValuePair<string, string?>($"HistoryCompaction:{entry.Key}", entry.Value)))
            .Build().GetSection("HistoryCompaction");

    private static HistoryCompactionRequest Request(HistoryCompactionOptions options) =>
        new("agent", "source",
        [
            new(ChatRole.System, "instructions"),
            new(ChatRole.User, "Old trip request. " + new string('u', 500)),
            new(ChatRole.Assistant, [new FunctionCallContent("lookup-1", "lookup", new Dictionary<string, object?> { ["city"] = "Agentburg" })]),
            new(ChatRole.Tool, [new FunctionResultContent("lookup-1", "Hotel and restaurant details. " + new string('t', 1500))]),
            new(ChatRole.Assistant, "Old detailed response. " + new string('a', 500)),
            new(ChatRole.User, "recent question"),
            new(ChatRole.Assistant, "recent answer")
        ], options);

    private sealed class SummaryClient : IChatClient
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        internal ChatMessage[] Messages { get; private set; } = [];
        internal ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Messages = messages.ToArray();
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "Trip: Agentburg, vegetarian, budget 200.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }

    private sealed class TestLogger : ILogger<MafForegroundHistoryCompactor>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
