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
        Assert.Single(scenario.Backend.Cancellations);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundMergeRestoresOnlyProvenApprovalsWithoutChangingCallerObjects(bool validExchange)
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.Ready = true;
        var approvalCall = new FunctionCallContent("current-call", "lookup", null);
        List<ChatMessage> response =
        [
            new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", approvalCall)]),
            new(ChatRole.User, [new ToolApprovalResponseContent("approval", true,
                new FunctionCallContent("current-call", "lookup", null))])
        ];
        if (validExchange)
            response.Add(new(ChatRole.Assistant, [new FunctionCallContent("current-call", "lookup", null)]));
        response.Add(new(ChatRole.Tool, [new FunctionResultContent("current-call", "result")]));
        response.Add(new(ChatRole.Assistant, "done"));

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session,
            [new(ChatRole.User, "current")], response));

        Assert.False(approvalCall.InformationalOnly);
        var stored = await ReadAsync(scenario);
        var approval = Assert.Single(stored.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Equal(validExchange, Assert.IsType<FunctionCallContent>(approval.ToolCall).InformationalOnly);
        Assert.Equal(validExchange, State(scenario.Session).ActiveHistory.ConversationId != scenario.Source.ConversationId);
        if (!validExchange)
        {
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("Approval consumption proof rejected", StringComparison.Ordinal));
            var view = await LoadAsync(scenario.Provider, scenario.Session);
            var pending = Assert.Single(view.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
            Assert.False(Assert.IsType<FunctionCallContent>(pending.ToolCall).InformationalOnly);
        }
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
        Assert.Single(scenario.Backend.Cancellations);
        Assert.Null(State(scenario.Session).PendingCompaction);
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
        Assert.Equal(first, Assert.Single(scenario.Backend.Cancellations));
    }

    [Fact]
    public async Task DisabledProfileCancelsJobThroughItsOriginalCompactor()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        using var disabled = new CosmosChatHistoryProvider(scenario.Fixture.CreateRepository());

        await SaveAsync(disabled, scenario.Session, "current", "answer");

        Assert.Single(scenario.Backend.Cancellations);
        Assert.Equal(0, scenario.Backend.Polls);
        Assert.Equal(4, (await ReadAsync(scenario)).Count);
    }

    [Fact]
    public async Task ClearCancelsAbandonedJob()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);

        await scenario.Provider.ClearMessagesAsync(scenario.Session);

        Assert.Single(scenario.Backend.Cancellations);
        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Empty(await ReadAsync(scenario));
    }

    [Fact]
    public async Task FailedCancellationDoesNotPreventSavingTurn()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.OnCancel = () => throw new HttpRequestException("sensitive");

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Equal(4, (await ReadAsync(scenario)).Count);
        Assert.Contains(scenario.Logger.Messages, message => message.Contains("Cancellation/", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Logger.Messages, message => message.Contains("sensitive", StringComparison.Ordinal));
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
    public async Task LocalCancellationSignalsWorkerAndIsIdempotent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreground = Foreground(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new UnreachableException();
        });
        var compactor = new LocalBackgroundHistoryCompactor(foreground);
        var pending = await compactor.CompactAsync(new("agent", "binding", Original, Options()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await compactor.CancelAsync(pending.Ticket!).WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await compactor.CancelAsync(pending.Ticket!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => compactor.GetResultAsync(pending.Ticket!));
    }

    [Fact]
    public async Task SaveDeadlineCancelsActualLocalWorkerAndPersistsTurn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compactor = new LocalBackgroundHistoryCompactor(Foreground(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new UnreachableException();
        }));
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository(),
            compactor: compactor, compactionOptions: Options() with
            {
                BackgroundSaveWaitTimeout = TimeSpan.FromMilliseconds(50)
            });
        var session = NewSession();
        await provider.InvokedAsync(new(Agent(), session, Original, []));
        await LoadAsync(provider, session);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await SaveAsync(provider, session, "current", "answer");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, (await fixture.CreateRepository().ReadAsync(State(session).ActiveHistory)).Messages.Count);
        Assert.Null(State(session).PendingCompaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrSelfCancelledLocalWorkerStillSavesTurn(bool selfCancelled)
    {
        var compactor = new LocalBackgroundHistoryCompactor(Foreground((_, _) =>
            selfCancelled
                ? Task.FromCanceled<HistoryCompactionResult>(new CancellationToken(true))
                : Task.FromException<HistoryCompactionResult>(new ArgumentException("sensitive"))));
        var fixture = new HistoryCosmosFixture();
        var logger = new TestLogger();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository(),
            compactor: compactor, compactionOptions: Options() with
            {
                BackgroundSaveWaitTimeout = TimeSpan.FromSeconds(2)
            }, logger: logger);
        var session = NewSession();
        await provider.InvokedAsync(new(Agent(), session, Original, []));
        var source = State(session).ActiveHistory;
        await LoadAsync(provider, session);

        await SaveAsync(provider, session, "current", "answer");

        Assert.Equal(source.ConversationId, State(session).ActiveHistory.ConversationId);
        Assert.Equal(4, (await fixture.CreateRepository().ReadAsync(State(session).ActiveHistory)).Messages.Count);
        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("sensitive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalCancellationDoesNotWaitForWorkerIgnoringToken()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compactor = new LocalBackgroundHistoryCompactor(Foreground(async (request, _) =>
        {
            started.SetResult();
            await completion.Task;
            finished.SetResult();
            return Completed(request);
        }));
        var pending = await compactor.CompactAsync(new("agent", "binding", Original, Options()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await compactor.CancelAsync(pending.Ticket!).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(finished.Task.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => compactor.GetResultAsync(pending.Ticket!));
        }
        finally
        {
            completion.SetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task WrongTicketBindingCannotCancelAnotherJob()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compactor = new LocalBackgroundHistoryCompactor(Foreground(async (request, _) =>
        {
            await completion.Task;
            return Completed(request);
        }));
        var pending = await compactor.CompactAsync(new("agent", "binding", Original, Options()));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                compactor.CancelAsync(new(pending.Ticket!.JobId, "wrong-binding")));
            Assert.Equal(HistoryCompactionStatus.Pending,
                (await compactor.GetResultAsync(pending.Ticket!)).Status);
        }
        finally
        {
            await compactor.CancelAsync(pending.Ticket!);
            completion.SetResult();
        }
    }

    [Fact]
    public async Task RetentionTimerStartsOnlyAfterWorkerTerminates()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new RetentionClock();
        var compactor = new LocalBackgroundHistoryCompactor(Foreground(async (request, _) =>
        {
            started.SetResult();
            await completion.Task;
            return Completed(request);
        }), null, clock);
        var pending = await compactor.CompactAsync(new("agent", "binding", Original,
            Options() with { BackgroundSaveWaitTimeout = TimeSpan.FromMinutes(6) }));

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(clock.Created.Task.IsCompleted);
            Assert.Equal(HistoryCompactionStatus.Pending,
                (await compactor.GetResultAsync(pending.Ticket!)).Status);
            completion.SetResult();

            var timer = await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromMinutes(5), timer.DueTime);
            Assert.Equal(HistoryCompactionStatus.Completed,
                (await compactor.GetResultAsync(pending.Ticket!)).Status);
            timer.Fire();
        }
        finally
        {
            completion.TrySetResult();
            await compactor.CancelAsync(pending.Ticket!);
            if (clock.Created.Task.IsCompletedSuccessfully)
                (await clock.Created.Task).Fire();
        }
    }

    private sealed class RetentionClock : TimeProvider
    {
        internal TaskCompletionSource<RetentionTimer> Created { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new RetentionTimer(callback, state, dueTime);
            Created.TrySetResult(timer);
            return timer;
        }
    }

    private sealed class RetentionTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        internal TimeSpan DueTime { get; } = dueTime;
        internal void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IHistoryCompactor Foreground(
        Func<HistoryCompactionRequest, CancellationToken, Task<HistoryCompactionResult>> execute)
    {
        var foreground = new Mock<IHistoryCompactor>();
        foreground.SetupGet(value => value.SupportedModes)
            .Returns(new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Foreground });
        foreground.Setup(value => value.CompactAsync(
            It.IsAny<HistoryCompactionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(execute);
        return foreground.Object;
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
        public List<HistoryCompactionTicket> Cancellations { get; } = [];
        public Func<Task>? OnCancel { get; set; }
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

        public Task CancelAsync(HistoryCompactionTicket ticket, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            backend.Cancellations.Add(ticket);
            return backend.OnCancel?.Invoke() ?? Task.CompletedTask;
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
