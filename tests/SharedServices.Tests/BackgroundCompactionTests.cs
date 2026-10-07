#pragma warning disable MAAI001 // Exercise the actual provider lifecycle and hosted session snapshots.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace SharedServices.Tests;

public class BackgroundCompactionTests
{
    [Fact]
    public async Task LoadReturnsOriginalAndRecordsTicketWithoutWritingHistory()
    {
        using var scenario = await SetupAsync();
        var batches = scenario.Fixture.Batches.Count;

        var result = await LoadAsync(scenario.Provider, scenario.Session);

        Assert.Equal(Original.Select(message => message.Text), result.Select(message => message.Text));
        Assert.NotNull(State(scenario.Session).PendingCompaction);
        Assert.Equal((scenario.Source, batches, 1, 0),
            (State(scenario.Session).ActiveHistory, scenario.Fixture.Batches.Count, scenario.Backend.Starts, scenario.Backend.Polls));
        await LoadAsync(scenario.Provider, scenario.Session);
        Assert.Equal(1, scenario.Backend.Starts);
    }

    [Fact]
    public async Task PendingAcrossTurnsThenCompletedMergesEverySuffixAndNewMessageExactlyOnce()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        await SaveAsync(scenario.Provider, scenario.Session, "first", "first answer");
        var pending = State(scenario.Session).PendingCompaction;
        Assert.NotNull(pending);
        Assert.Equal(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);
        scenario.Backend.Ready = true;
        await LoadAsync(scenario.Provider, scenario.Session);

        await SaveAsync(scenario.Provider, scenario.Session, "second", "second answer");

        Assert.Equal(new[] { "summary", "first", "first answer", "second", "second answer" },
            (await ReadAsync(scenario)).Select(message => message.Text));
        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Equal((1, 2), (scenario.Backend.Starts, scenario.Backend.Polls));
        Assert.NotEqual(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);
    }

    [Fact]
    public async Task PendingSaveDoesNotQueryHistoryAndReadySaveQueriesItOnlyOnce()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        var queriesBeforePending = scenario.Fixture.Queries.Count;

        await SaveAsync(scenario.Provider, scenario.Session, "first", "first answer");

        Assert.Equal(queriesBeforePending, scenario.Fixture.Queries.Count);
        scenario.Backend.Ready = true;
        await SaveAsync(scenario.Provider, scenario.Session, "second", "second answer");
        Assert.Equal(queriesBeforePending + 1, scenario.Fixture.Queries.Count);
    }

    [Fact]
    public async Task OriginalBoundaryUsesStoredSequenceNotMessagePositionAfterClear()
    {
        using var scenario = await SetupAsync();
        await scenario.Provider.ClearMessagesAsync(scenario.Session);
        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session, Original, []));
        await LoadAsync(scenario.Provider, scenario.Session);
        var pending = State(scenario.Session).PendingCompaction;
        Assert.NotNull(pending);
        Assert.Equal((2, 3L), (pending.SourceMessageCount, pending.SourceLastSequence));
        await SaveAsync(scenario.Provider, scenario.Session, "first", "first answer");
        scenario.Backend.Ready = true;

        await SaveAsync(scenario.Provider, scenario.Session, "second", "second answer");

        Assert.Equal(new[] { "summary", "first", "first answer", "second", "second answer" },
            (await ReadAsync(scenario)).Select(message => message.Text));
    }

    [Fact]
    public async Task ReadyAtFirstSavePublishesNewTurnWithoutAppendingToSource()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.Ready = true;

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Equal(new[] { "summary", "current", "answer" }, (await ReadAsync(scenario)).Select(message => message.Text));
        var sourceMessages = scenario.Fixture.Documents.Where(document =>
            document.GetProperty("type").GetString() == "ChatMessage"
            && document.GetProperty("conversationId").GetString() == scenario.Source.ConversationId).ToArray();
        Assert.Equal(2, sourceMessages.Length);
        Assert.All(sourceMessages, document => Assert.Equal(86400, document.GetProperty("ttl").GetInt32()));
    }

    [Fact]
    public async Task EmptyHistoryDoesNotStartWork()
    {
        using var scenario = await SetupAsync();
        var session = NewSession("empty");

        await LoadAsync(scenario.Provider, session);

        Assert.Equal(0, scenario.Backend.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedAtStartOrRetrievalKeepsOriginalHistory(bool duringRetrieval)
    {
        using var scenario = await SetupAsync();
        if (duringRetrieval)
            scenario.Backend.OnPoll = (_, _) => Task.FromResult(Unchanged(scenario.Backend.Request!));
        else
            scenario.Backend.OnStart = (request, _) => Task.FromResult(Unchanged(request));
        await LoadAsync(scenario.Provider, scenario.Session);

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Equal(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);
        Assert.Equal(4, (await ReadAsync(scenario)).Count);
    }

    [Fact]
    public async Task BackgroundCannotReturnInlineCompletedInsteadOfTicket()
    {
        using var scenario = await SetupAsync();
        scenario.Backend.OnStart = (request, _) => Task.FromResult(Completed(request));

        await LoadAsync(scenario.Provider, scenario.Session);

        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Single(scenario.Fixture.Batches);
        Assert.Contains(scenario.Logger.Messages, message => message.Contains("InvalidLifecycle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForegroundCannotReturnPending()
    {
        using var scenario = await SetupAsync();
        using var provider = new CosmosChatHistoryProvider(scenario.Fixture.CreateRepository(),
            compactor: new TestCompactor(scenario.Backend),
            compactionOptions: Options() with { Mode = HistoryCompactionMode.Foreground });

        await LoadAsync(provider, scenario.Session);

        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Single(scenario.Fixture.Batches);
    }

    [Theory]
    [InlineData("wrong-binding")]
    [InlineData("wrong-ticket")]
    [InlineData("invalid-diagnostics")]
    [InlineData("empty-job")]
    public async Task InvalidOrMissingJobIsDiscardedAndNewTurnIsStillSaved(string failure)
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.OnPoll = (ticket, _) => failure switch
        {
            "wrong-binding" => Task.FromResult(new HistoryCompactionResult(HistoryCompactionStatus.Completed,
                "wrong", Summary, Size(Original), Size(Summary))),
            "wrong-ticket" => Task.FromResult(HistoryCompactionResult.Pending(new("different-job", ticket.SourceBinding))),
            "invalid-diagnostics" => Task.FromResult(new HistoryCompactionResult(HistoryCompactionStatus.Completed,
                ticket.SourceBinding, Summary, 0, 0)),
            _ => throw new InvalidOperationException("job expired, sensitive details")
        };

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Equal(4, (await ReadAsync(scenario)).Count);
        Assert.Equal(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);
        Assert.NotEmpty(scenario.Logger.Messages);
        Assert.DoesNotContain(scenario.Logger.Messages, message => message.Contains("sensitive", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidToolCandidateClearsPersistedTicketAndAllowsAnotherJob(bool completeInventedExchange)
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        var oldTicket = State(scenario.Session).PendingCompaction!.Ticket;
        List<ChatMessage> candidate = [new(ChatRole.Assistant, [new FunctionCallContent("invented", "lookup", null)])];
        if (completeInventedExchange)
            candidate.Add(new(ChatRole.Tool, [new FunctionResultContent("invented", "invented result")]));
        scenario.Backend.OnPoll = (ticket, _) => Task.FromResult(new HistoryCompactionResult(
            HistoryCompactionStatus.Completed, ticket.SourceBinding, candidate, Size(Original), Size(candidate)));

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Equal(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);
        Assert.Equal(4, (await ReadAsync(scenario)).Count);
        Assert.Contains(scenario.Logger.Messages, message => message.Contains("UnsafeToolHistory", StringComparison.Ordinal));

        var restored = new TestAgentSession(AgentSessionStateBag.Deserialize(scenario.Session.StateBag.Serialize()));
        Assert.Null(State(restored).PendingCompaction);
        await LoadAsync(scenario.Provider, restored);

        Assert.Equal(2, scenario.Backend.Starts);
        Assert.Equal(1, scenario.Backend.Polls);
        Assert.NotEqual(oldTicket, State(restored).PendingCompaction!.Ticket);
    }

    [Fact]
    public async Task WrongTicketBindingAtStartIsNeverPersisted()
    {
        using var scenario = await SetupAsync();
        scenario.Backend.OnStart = (_, _) => Task.FromResult(HistoryCompactionResult.Pending(new("job", "wrong-source")));

        await LoadAsync(scenario.Provider, scenario.Session);

        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Single(scenario.Fixture.Batches);
    }

    [Fact]
    public async Task RetrievalFailureKeepsTicketForNextSaveAndDoesNotDropCurrentTurn()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.OnPoll = (_, _) => throw new HttpRequestException("sensitive");

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.NotNull(State(scenario.Session).PendingCompaction);
        Assert.Equal(4, (await ReadAsync(scenario)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CooperativeTimeoutDoesNotLoseOriginalHistoryOrAbandonJob(bool retrieval)
    {
        using var scenario = await SetupAsync(Options() with { Timeout = TimeSpan.FromMilliseconds(50) });
        if (!retrieval)
            scenario.Backend.OnStart = async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new UnreachableException(); };
        await LoadAsync(scenario.Provider, scenario.Session);
        if (retrieval)
            scenario.Backend.OnPoll = async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new UnreachableException(); };

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Equal(retrieval, State(scenario.Session).PendingCompaction is not null);
        Assert.Equal(4, (await ReadAsync(scenario)).Count);
        Assert.Contains(scenario.Logger.Messages, message => message.Contains("TimeoutException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallerCancellationDuringRetrievalDoesNotAppendOrRotate()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        using var cancellation = new CancellationTokenSource();
        scenario.Backend.OnPoll = (_, _) => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); throw new UnreachableException(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SaveAsync(scenario.Provider, scenario.Session, "current", "answer", cancellation.Token));

        Assert.Single(scenario.Fixture.Batches);
        Assert.NotNull(State(scenario.Session).PendingCompaction);
    }

    [Fact]
    public async Task ReadyJobIsDeferredAcrossIncompleteToolExchange()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.Ready = true;
        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session,
            [new(ChatRole.User, "lookup")],
            [new(ChatRole.Assistant, [new FunctionCallContent("call", "lookup", null)])]));
        Assert.NotNull(State(scenario.Session).PendingCompaction);
        Assert.Equal(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session,
            [new(ChatRole.Tool, [new FunctionResultContent("call", "result")])],
            [new(ChatRole.Assistant, "done")]));

        var messages = await ReadAsync(scenario);
        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Equal(5, messages.Count);
        Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
    }

    [Fact]
    public async Task NativeMafAutoApprovalCompletesBeforeBackgroundPublicationAndIsNeverReplayed()
    {
        using var scenario = await SetupAsync();
        scenario.Backend.OnStart = (request, _) => Task.FromResult(scenario.Backend.Starts == 1
            ? HistoryCompactionResult.Pending(new("job", request.SourceBinding)) : Unchanged(request));
        var invocations = 0;
        var tool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() =>
        {
            invocations++;
            return "result";
        }, "lookup"));
        var client = new Mock<IChatClient>();
        client.Setup(value => value.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> input, ChatOptions? _, CancellationToken _) =>
                Task.FromResult(new ChatResponse(input.Last().Text == "use tool"
                    ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", "lookup", new Dictionary<string, object?>())])
                    : new ChatMessage(ChatRole.Assistant, "reply"))));
        var agent = new ChatClientAgent(client.Object, new ChatClientAgentOptions
        {
            Id = "background-approval", Name = "background-approval", ChatHistoryProvider = scenario.Provider,
            ChatOptions = new() { Tools = [tool] }
        }).AsBuilder().UseToolApproval(new ToolApprovalAgentOptions
        {
            AutoApprovalRules = [ToolApprovalAgent.AllToolsAutoApprovalRule]
        }).Build();
        var session = await agent.CreateSessionAsync();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create(agent.Id, "approval-context"));
        await agent.RunAsync(new string('x', 800), session);
        await agent.RunAsync("pending turn", session);
        var source = State(session).ActiveHistory;
        scenario.Backend.Ready = true;

        await agent.RunAsync("use tool", session);
        await agent.RunAsync("recall", session);

        Assert.Equal(1, invocations);
        Assert.Null(State(session).PendingCompaction);
        Assert.NotEqual(source.ConversationId, State(session).ActiveHistory.ConversationId);
        var history = await scenario.Fixture.CreateRepository().ReadAsync(State(session).ActiveHistory);
        Assert.Single(history.Messages, message => message.Text == "use tool");
        var approvals = history.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>().ToArray();
        Assert.True(Assert.IsType<FunctionCallContent>(Assert.Single(approvals).ToolCall).InformationalOnly);
        Assert.Single(history.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
    }

    [Fact]
    public async Task MergeDoesNotMutateTheFrameworksApprovalObjects()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.Ready = true;
        var call = new FunctionCallContent("call", "lookup", null);
        var approval = new ToolApprovalRequestContent("approval", call);
        var decision = new ToolApprovalResponseContent("approval", true, call);
        ChatMessage[] input =
        [
            new(ChatRole.User, "lookup"),
            new(ChatRole.Assistant, [approval]),
            new(ChatRole.User, [decision])
        ];
        ChatMessage[] output =
        [
            new(ChatRole.Assistant, [call]),
            new(ChatRole.Tool, [new FunctionResultContent("call", "result")]),
            new(ChatRole.Assistant, "done")
        ];

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session, input, output));

        Assert.False(call.InformationalOnly);
        Assert.NotEqual(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);
        var storedApproval = Assert.Single((await ReadAsync(scenario)).SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>());
        Assert.True(Assert.IsType<FunctionCallContent>(storedApproval.ToolCall).InformationalOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOrRecreatedIdenticalPrefixCannotBeCompacted(bool recreate)
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.Ready = true;
        var message = scenario.Fixture.Documents.First(document => document.GetProperty("type").GetString() == "ChatMessage");
        scenario.Fixture.ExpireMessage(scenario.Source.ToAddress(), message.GetProperty("id").GetString()!);
        if (recreate)
        {
            var appended = await scenario.Fixture.CreateRepository().AppendAsync(scenario.Source, [Original[0]]);
            SessionPersistenceState.SetHistory(scenario.Session, appended.Reference);
        }

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Equal(1, scenario.Backend.Polls);
        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Equal(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);
        Assert.Equal(recreate ? 4 : 3, (await ReadAsync(scenario)).Count);
        Assert.Contains(scenario.Logger.Messages, message => message.Contains("SourceExpiredOrReplaced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JsonPropertyReorderingDoesNotInvalidatePrefix()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.Ready = true;
        foreach (var document in scenario.Fixture.Documents.Where(document => document.GetProperty("type").GetString() == "ChatMessage"))
        {
            var reordered = document.EnumerateObject().Reverse().ToDictionary(property => property.Name, property => property.Value);
            scenario.Fixture.Seed(scenario.Source.ToAddress(), JsonSerializer.SerializeToElement(reordered));
        }

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Equal("summary", (await ReadAsync(scenario))[0].Text);
    }

    [Fact]
    public async Task ExpiryWhileRetrievingCompletedJobDoesNotRestoreExpiredMessages()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.OnPoll = (_, _) =>
        {
            var message = scenario.Fixture.Documents.First(document => document.GetProperty("type").GetString() == "ChatMessage");
            scenario.Fixture.ExpireMessage(scenario.Source.ToAddress(), message.GetProperty("id").GetString()!);
            return Task.FromResult(Completed(scenario.Backend.Request!));
        };

        await SaveAsync(scenario.Provider, scenario.Session, "current", "answer");

        Assert.Equal(scenario.Source.ConversationId, State(scenario.Session).ActiveHistory.ConversationId);
        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Equal(3, (await ReadAsync(scenario)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrChangedProfileNeverPollsTheOldJob(bool changed)
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        using var provider = new CosmosChatHistoryProvider(scenario.Fixture.CreateRepository(),
            compactor: changed ? new TestCompactor(scenario.Backend) : null,
            compactionOptions: changed ? Options() with { CompactorKey = "other" } : null, logger: scenario.Logger);

        await SaveAsync(provider, scenario.Session, "current", "answer");

        Assert.Equal(0, scenario.Backend.Polls);
        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Equal(4, (await ReadAsync(scenario)).Count);
    }

    [Fact]
    public async Task ExplicitClearCancelsApplicationOfPendingJob()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);

        await scenario.Provider.ClearMessagesAsync(scenario.Session);

        Assert.Null(State(scenario.Session).PendingCompaction);
    }

    [Fact]
    public async Task CompetingWriteDuringRetrievalCannotBeOverwrittenByRotationOrFallback()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.OnPoll = async (_, _) =>
        {
            await scenario.Fixture.CreateRepository().AppendAsync(scenario.Source, [new(ChatRole.User, "competitor")]);
            return Completed(scenario.Backend.Request!);
        };

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            SaveAsync(scenario.Provider, scenario.Session, "current", "answer"));

        Assert.Equal(scenario.Source, State(scenario.Session).ActiveHistory);
        Assert.NotNull(State(scenario.Session).PendingCompaction);
        Assert.DoesNotContain(scenario.Fixture.Documents, document =>
            document.GetProperty("type").GetString() == "ChatMessage"
            && document.GetProperty("message").GetRawText().Contains("current", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StagingFailureDoesNotAppendToOriginalAsFallback()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.Ready = true;
        scenario.Fixture.BatchFailures[2] = HttpStatusCode.BadRequest;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveAsync(scenario.Provider, scenario.Session, "current", "answer"));

        Assert.Equal(scenario.Source, State(scenario.Session).ActiveHistory);
        Assert.Equal(2, (await ReadAsync(scenario)).Count);
    }

    [Fact]
    public async Task LostPublicationResponseRecoversOneTransitionAndClearsTicket()
    {
        using var scenario = await SetupAsync();
        await LoadAsync(scenario.Provider, scenario.Session);
        scenario.Backend.Ready = true;
        scenario.Fixture.AfterCommitAsync = batch =>
        {
            if (batch.Partition == scenario.Source.ToAddress().ToPartitionKey())
                throw new IOException("response lost");
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<IOException>(() => SaveAsync(scenario.Provider, scenario.Session, "current", "answer"));
        scenario.Fixture.AfterCommitAsync = null;

        var recovered = await LoadAsync(scenario.Provider, scenario.Session);

        Assert.Equal(new[] { "summary", "current", "answer" }, recovered.Select(message => message.Text));
        Assert.Null(State(scenario.Session).PendingCompaction);
        Assert.Equal(1, scenario.Backend.Starts);
    }

    [Fact]
    public async Task RealHostedSnapshotResumesPendingJobOnNewProviderAndSessionStore()
    {
        using var scenario = await SetupAsync();
        var sessions = new SessionCosmosSdkFixture();
        var store = new CosmosAgentSessionStore(sessions.Repository, NullLogger<CosmosAgentSessionStore>.Instance);
        var agent = ChatAgent(scenario.Provider);
        var key = new AgentSessionStoreKey("hosted-job");
        var session = await store.GetOrCreateSessionAsync(agent, key);
        await agent.RunAsync(new string('h', 800), session);
        await agent.RunAsync("first", session);
        await store.SaveSessionAsync(agent, key, session);
        Assert.NotNull(State(session).PendingCompaction);
        using var replicaProvider = new CosmosChatHistoryProvider(scenario.Fixture.CreateRepository(),
            compactor: new TestCompactor(scenario.Backend), compactionOptions: Options());
        var replicaAgent = ChatAgent(replicaProvider);
        var replicaStore = new CosmosAgentSessionStore(sessions.Repository, NullLogger<CosmosAgentSessionStore>.Instance);
        var restored = await replicaStore.GetSessionAsync(replicaAgent, key);
        Assert.NotNull(restored);
        Assert.Equal(State(session).PendingCompaction, State(restored).PendingCompaction);
        scenario.Backend.Ready = true;

        await replicaAgent.RunAsync("second", restored);
        await replicaStore.SaveSessionAsync(replicaAgent, key, restored);

        var messages = await scenario.Fixture.CreateRepository().ReadAsync(State(restored).ActiveHistory);
        Assert.Equal(new[] { "summary", "first", "reply", "second", "reply" }, messages.Messages.Select(message => message.Text));
        Assert.Null(State(restored).PendingCompaction);
        Assert.Equal(1, scenario.Backend.Starts);
    }

    [Fact]
    public void BackgroundConfigurationResolvesARealRetrievalCapableRegistration()
    {
        var services = new ServiceCollection();
        var compactor = new TestCompactor(new Backend());
        services.AddHistoryCompactor("background", _ => compactor);
        var section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HistoryCompaction:Mode"] = "Background", ["HistoryCompaction:CompactorKey"] = "background"
        }).Build().GetSection("HistoryCompaction");
        var options = services.AddHistoryCompactionProfile(section);
        using var provider = services.BuildServiceProvider();

        Assert.Same(compactor, provider.GetHistoryCompactor(options));
    }

    private static readonly ChatMessage[] Original =
        [new(ChatRole.User, new string('u', 500)), new(ChatRole.Assistant, new string('a', 500))];
    private static readonly ChatMessage[] Summary = [new(ChatRole.User, "summary")];
    private static HistoryCompactionOptions Options() => new() { CompactorKey = "background", Mode = HistoryCompactionMode.Background };
    private static SessionPersistenceContext State(AgentSession session) => SessionPersistenceState.GetRequired(session);
    private static AIAgent Agent() => new Mock<AIAgent>().Object;
    private static long Size(IReadOnlyList<ChatMessage> messages) => HistoryCompactionValidation.Measure(messages);
    private static HistoryCompactionResult Completed(HistoryCompactionRequest request) =>
        new(HistoryCompactionStatus.Completed, request.SourceBinding, Summary, Size(request.Messages), Size(Summary));
    private static HistoryCompactionResult Unchanged(HistoryCompactionRequest request) =>
        new(HistoryCompactionStatus.Unchanged, request.SourceBinding, request.Messages, Size(request.Messages), Size(request.Messages));
    private static async Task<IReadOnlyList<ChatMessage>> LoadAsync(CosmosChatHistoryProvider provider, AgentSession session) =>
        (await provider.InvokingAsync(new(Agent(), session, []))).ToArray();
    private static Task SaveAsync(CosmosChatHistoryProvider provider, AgentSession session, string user, string answer,
        CancellationToken cancellationToken = default) =>
        provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, user)], [new(ChatRole.Assistant, answer)]), cancellationToken).AsTask();
    private static async Task<IReadOnlyList<ChatMessage>> ReadAsync(Scenario scenario) =>
        (await scenario.Fixture.CreateRepository().ReadAsync(State(scenario.Session).ActiveHistory)).Messages;
    private static TestAgentSession NewSession(string id = "lookup")
    {
        var session = new TestAgentSession();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create("background-agent", id));
        return session;
    }
    private static async Task<Scenario> SetupAsync(HistoryCompactionOptions? options = null)
    {
        var fixture = new HistoryCosmosFixture();
        var backend = new Backend();
        var logger = new TestLogger();
        var provider = new CosmosChatHistoryProvider(fixture.CreateRepository(),
            compactor: new TestCompactor(backend), compactionOptions: options ?? Options(), logger: logger);
        var session = NewSession();
        await provider.InvokedAsync(new(Agent(), session, Original, []));
        return new(fixture, backend, logger, provider, session, State(session).ActiveHistory);
    }
    private static ChatClientAgent ChatAgent(CosmosChatHistoryProvider provider)
    {
        var client = new Mock<IChatClient>();
        client.Setup(value => value.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")));
        return new(client.Object, new ChatClientAgentOptions { Id = "background-agent", Name = "background-agent", ChatHistoryProvider = provider });
    }
    private sealed record Scenario(HistoryCosmosFixture Fixture, Backend Backend, TestLogger Logger,
        CosmosChatHistoryProvider Provider, TestAgentSession Session, HistoryReference Source) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }
    // A test double for an external durable backend, deliberately shared by distinct compactor replicas.
    private sealed class Backend
    {
        public int Starts { get; set; }
        public int Polls { get; set; }
        public bool Ready { get; set; }
        public HistoryCompactionRequest? Request { get; set; }
        public Func<HistoryCompactionRequest, CancellationToken, Task<HistoryCompactionResult>>? OnStart { get; set; }
        public Func<HistoryCompactionTicket, CancellationToken, Task<HistoryCompactionResult>>? OnPoll { get; set; }
    }
    private sealed class TestCompactor(Backend backend) : IBackgroundHistoryCompactor
    {
        public IReadOnlySet<HistoryCompactionMode> SupportedModes { get; } =
            new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Foreground, HistoryCompactionMode.Background };
        public Task<HistoryCompactionResult> CompactAsync(HistoryCompactionRequest request, CancellationToken cancellationToken = default)
        {
            backend.Starts++;
            backend.Request = request;
            return backend.OnStart?.Invoke(request, cancellationToken)
                ?? Task.FromResult(HistoryCompactionResult.Pending(new("job", request.SourceBinding)));
        }
        public Task<HistoryCompactionResult> GetResultAsync(HistoryCompactionTicket ticket, CancellationToken cancellationToken = default)
        {
            backend.Polls++;
            return backend.OnPoll?.Invoke(ticket, cancellationToken)
                ?? Task.FromResult(backend.Ready ? Completed(backend.Request!) : HistoryCompactionResult.Pending(ticket));
        }
    }
    private sealed class TestLogger : ILogger<CosmosChatHistoryProvider>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
