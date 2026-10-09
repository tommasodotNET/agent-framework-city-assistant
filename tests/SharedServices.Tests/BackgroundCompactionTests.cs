#pragma warning disable MAAI001 // Exercise the actual provider lifecycle.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;

namespace SharedServices.Tests;

public class BackgroundCompactionTests
{
    [Fact]
    public async Task LoadStartsJobWithoutWritingAndTicketDoesNotSurviveSerialization()
    {
        using var scenario = await SetupAsync();
        var batches = scenario.Fixture.Batches.Count;

        var result = await LoadAsync(scenario.Provider, scenario.Session);

        Assert.Equal(Original.Select(message => message.Text), result.Select(message => message.Text));
        Assert.NotNull(State(scenario.Session).PendingCompaction);
        Assert.Equal((batches, 1, 0), (scenario.Fixture.Batches.Count, scenario.Backend.Starts, scenario.Backend.Polls));
        var restored = new TestAgentSession(AgentSessionStateBag.Deserialize(scenario.Session.StateBag.Serialize()));
        Assert.Null(State(restored).PendingCompaction);
    }

    [Fact]
    public async Task ReadyAtSavePublishesCompactedHistoryWithCurrentTurn()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.Ready = true;

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Equal(new[] { "summary", "current", "answer" },
            (await ReadAsync(scenario)).Select(message => message.Text));
        Assert.NotEqual(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);
        Assert.Null(State(scenario.Session).PendingCompaction);
    }

    [Fact]
    public async Task SaveWaitsBrieflyForJobThatIsAlmostReady()
    {
        using var scenario = await SetupAsync(Options() with
        {
            BackgroundSaveWaitTimeout = TimeSpan.FromMilliseconds(500)
        });
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.OnPoll = (ticket, _) => Task.FromResult(
            scenario.Backend.Polls >= 3
                ? Completed(scenario.Backend.Request!)
                : HistoryCompactionResult.Pending(ticket));

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.True(scenario.Backend.Polls >= 3);
        Assert.Equal("summary", (await ReadAsync(scenario))[0].Text);
    }

    [Fact]
    public async Task SaveTimeoutAppendsNormallyAndDiscardsLiveTicket()
    {
        using var scenario = await SetupAsync(Options() with
        {
            BackgroundSaveWaitTimeout = TimeSpan.FromMilliseconds(80)
        });
        await LoadAsync(scenario.Provider, scenario.Session);
        var stopwatch = Stopwatch.StartNew();

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(50));
        Assert.Equal(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);
        Assert.Equal(4, (await ReadAsync(scenario)).Count);
        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Contains(scenario.Logger.Messages,
            message => message.Contains(nameof(HistoryCompactionOptions.BackgroundSaveWaitTimeout), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetrievalFailureStillSavesCurrentTurn(bool timeout)
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.OnPoll = (_, _) => timeout
            ? throw new TimeoutException("sensitive")
            : throw new InvalidOperationException("sensitive");

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Equal(4, (await ReadAsync(scenario)).Count);
        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.DoesNotContain(scenario.Logger.Messages,
            message => message.Contains("sensitive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidCompletedResultStillSavesCurrentTurn()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.OnPoll = (_, _) => Task.FromResult(new HistoryCompactionResult(
            HistoryCompactionStatus.Completed, "wrong-binding", Summary, Size(Original), Size(Summary)));

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Equal(4, (await ReadAsync(scenario)).Count);
        Assert.Equal(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);
    }

    [Fact]
    public async Task CallerCancellationDuringWaitDoesNotSave()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.OnPoll = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new UnreachableException();
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SaveAsync(scenario.Provider, scenario.Session, "current", "answer", cancellation.Token));

        Assert.Single(scenario.Fixture.Batches);
        Assert.Equal(2, (await ReadAsync(scenario)).Count);
    }

    [Fact]
    public async Task NewLoadReplacesAbandonedJob()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        var first = State(scenario.Session).PendingCompaction!.Ticket;

        await LoadAsync(scenario.Provider, scenario.Session);

        Assert.Equal(2, scenario.Backend.Starts);
        Assert.NotEqual(first, State(scenario.Session).PendingCompaction!.Ticket);
    }

    [Fact]
    public async Task BackgroundCannotReturnInlineCompleted()
    {
        using var scenario = await SetupAsync();
        scenario.Backend.OnStart = (request, _) => Task.FromResult(Completed(request));

        await LoadAsync(scenario.Provider, scenario.Session);

        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Contains(scenario.Logger.Messages,
            message => message.Contains("InvalidLifecycle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalAdapterRunsForegroundCompactorInBackground()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreground = new Mock<IHistoryCompactor>();
        foreground.SetupGet(value => value.SupportedModes)
            .Returns(new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Foreground });
        foreground.Setup(value => value.CompactAsync(
                It.IsAny<HistoryCompactionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (HistoryCompactionRequest request, CancellationToken _) =>
            {
                await completion.Task;
                return Completed(request);
            });
        var compactor = new LocalBackgroundHistoryCompactor(foreground.Object);
        var request = new HistoryCompactionRequest(
            "agent", "binding", Original, Options());

        var pending = await compactor.CompactAsync(request);
        Assert.Equal(HistoryCompactionStatus.Pending, pending.Status);
        Assert.Equal(HistoryCompactionStatus.Pending,
            (await compactor.GetResultAsync(pending.Ticket!)).Status);

        completion.SetResult();
        HistoryCompactionResult result;
        do
        {
            await Task.Delay(10);
            result = await compactor.GetResultAsync(pending.Ticket!);
        } while (result.Status == HistoryCompactionStatus.Pending);

        Assert.Equal(HistoryCompactionStatus.Completed, result.Status);
    }

    [Fact]
    public void BackgroundSaveWaitDefaultsToTwoSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), new HistoryCompactionOptions().BackgroundSaveWaitTimeout);
    }

    private static readonly ChatMessage[] Original =
        [new(ChatRole.User, new string('u', 500)), new(ChatRole.Assistant, new string('a', 500))];
    private static readonly ChatMessage[] Summary = [new(ChatRole.User, "summary")];

    private static HistoryCompactionOptions Options() => new()
    {
        CompactorKey = "background",
        Mode = HistoryCompactionMode.Background,
        BackgroundSaveWaitTimeout = TimeSpan.Zero
    };

    private static SessionPersistenceContext State(AgentSession session) =>
        SessionPersistenceState.GetRequired(session);

    private static AIAgent Agent() => new Mock<AIAgent>().Object;

    private static long Size(IReadOnlyList<ChatMessage> messages) =>
        HistoryCompactionValidation.Measure(messages);

    private static HistoryCompactionResult Completed(HistoryCompactionRequest request) =>
        new(HistoryCompactionStatus.Completed, request.SourceBinding, Summary,
            Size(request.Messages), Size(Summary));

    private static async Task<IReadOnlyList<ChatMessage>> LoadAsync(
        CosmosChatHistoryProvider provider, AgentSession session) =>
        (await provider.InvokingAsync(new(Agent(), session, []))).ToArray();

    private static Task SaveAsync(
        CosmosChatHistoryProvider provider, AgentSession session, string user, string answer,
        CancellationToken cancellationToken = default) =>
        provider.InvokedAsync(new(Agent(), session,
            [new(ChatRole.User, user)], [new(ChatRole.Assistant, answer)]),
            cancellationToken).AsTask();

    private static async Task<IReadOnlyList<ChatMessage>> ReadAsync(Scenario scenario) =>
        (await scenario.Fixture.CreateRepository().ReadAsync(State(scenario.Session).ActiveHistory)).Messages;

    private static TestAgentSession NewSession()
    {
        var session = new TestAgentSession();
        SessionPersistenceState.Initialize(session,
            SessionStorageAddress.Create("background-agent", "lookup"));
        return session;
    }

    private static async Task<Scenario> SetupAsync(HistoryCompactionOptions? options = null)
    {
        var fixture = new HistoryCosmosFixture();
        var backend = new Backend();
        var logger = new TestLogger();
        var provider = new CosmosChatHistoryProvider(
            fixture.CreateRepository(),
            compactor: new TestCompactor(backend),
            compactionOptions: options ?? Options(),
            logger: logger);
        var session = NewSession();
        await provider.InvokedAsync(new(Agent(), session, Original, []));
        return new(fixture, backend, logger, provider, session, State(session).ActiveHistory);
    }

    private sealed record Scenario(
        HistoryCosmosFixture Fixture,
        Backend Backend,
        TestLogger Logger,
        CosmosChatHistoryProvider Provider,
        TestAgentSession Session,
        HistoryReference Source) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private sealed class Backend
    {
        public int Starts { get; set; }
        public int Polls { get; set; }
        public bool Ready { get; set; }
        public HistoryCompactionRequest? Request { get; set; }
        public Func<HistoryCompactionRequest, CancellationToken, Task<HistoryCompactionResult>>? OnStart { get; set; }
        public Func<HistoryCompactionTicket, CancellationToken, Task<HistoryCompactionResult>>? OnPoll { get; set; }
    }

    private sealed class TestCompactor(Backend backend) : IHistoryCompactor
    {
        public IReadOnlySet<HistoryCompactionMode> SupportedModes { get; } =
            new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Background };

        public Task<HistoryCompactionResult> CompactAsync(
            HistoryCompactionRequest request, CancellationToken cancellationToken = default)
        {
            backend.Starts++;
            backend.Request = request;
            return backend.OnStart?.Invoke(request, cancellationToken)
                ?? Task.FromResult(HistoryCompactionResult.Pending(
                    new HistoryCompactionTicket(Guid.NewGuid().ToString("N"), request.SourceBinding)));
        }

        public Task<HistoryCompactionResult> GetResultAsync(
            HistoryCompactionTicket ticket, CancellationToken cancellationToken = default)
        {
            backend.Polls++;
            if (backend.OnPoll is not null)
                return backend.OnPoll(ticket, cancellationToken);
            if (backend.Request is not { } request)
                throw new InvalidOperationException();
            return Task.FromResult(backend.Ready
                ? Completed(request)
                : HistoryCompactionResult.Pending(ticket));
        }
    }

    private sealed class TestLogger : ILogger<CosmosChatHistoryProvider>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
