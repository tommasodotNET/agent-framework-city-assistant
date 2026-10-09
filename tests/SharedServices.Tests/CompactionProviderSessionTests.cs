#pragma warning disable MAAI001 // Exercise normal MAF session serialization and foreground runs.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SharedServices.Tests;

public class CompactionProviderSessionTests
{
    [Fact]
    public async Task HostedSavePreservesCompactedHistoryAndAllOtherSessionState()
    {
        var history = new HistoryCosmosFixture();
        var sessions = new SessionCosmosSdkFixture();
        using var provider = new CosmosChatHistoryProvider(history.CreateRepository(),
            compactor: new OnceCompactor(), compactionOptions: Options());
        var agent = Agent(provider);
        var store = new CosmosAgentSessionStore(sessions.Repository, NullLogger<CosmosAgentSessionStore>.Instance);
        var key = new AgentSessionStoreKey("session");
        var session = await store.GetOrCreateSessionAsync(agent, key);
        session.StateBag.SetValue("skill-state", new Dictionary<string, string> { ["loaded"] = "restaurant" });
        await agent.RunAsync("The original history contains deliberately lengthy details for reduction.", session);
        await store.SaveSessionAsync(agent, key, session);
        await agent.RunAsync("current turn", session);
        var target = SessionPersistenceState.GetRequired(session).ActiveHistory;
        await store.SaveSessionAsync(agent, key, session);

        var restored = await store.GetSessionAsync(agent, key);
        Assert.NotNull(restored);

        Assert.Equal((target, "restaurant"), (SessionPersistenceState.GetRequired(restored).ActiveHistory,
            restored.StateBag.GetValue<Dictionary<string, string>>("skill-state")?["loaded"]));
    }

    [Fact]
    public async Task FreshReplicaUsesHostedTargetSnapshotForTheNextTurn()
    {
        var history = new HistoryCosmosFixture();
        var sessions = new SessionCosmosSdkFixture();
        using var provider = new CosmosChatHistoryProvider(history.CreateRepository(),
            compactor: new OnceCompactor(), compactionOptions: Options());
        var agent = Agent(provider);
        var store = new CosmosAgentSessionStore(sessions.Repository, NullLogger<CosmosAgentSessionStore>.Instance);
        var key = new AgentSessionStoreKey("session");
        var session = await store.GetOrCreateSessionAsync(agent, key);
        await agent.RunAsync("The original history contains deliberately lengthy details for reduction.", session);
        await store.SaveSessionAsync(agent, key, session);
        await agent.RunAsync("current", session);
        await store.SaveSessionAsync(agent, key, session);
        using var freshProvider = new CosmosChatHistoryProvider(history.CreateRepository());
        var freshAgent = Agent(freshProvider);
        var freshStore = new CosmosAgentSessionStore(sessions.Repository, NullLogger<CosmosAgentSessionStore>.Instance);
        var restored = await freshStore.GetSessionAsync(freshAgent, key);
        Assert.NotNull(restored);
        await freshAgent.RunAsync("next", restored);

        var messages = await history.CreateRepository().ReadAsync(SessionPersistenceState.GetRequired(restored).ActiveHistory);

        Assert.Equal(new[] { "summary", "current", "reply", "next", "reply" }, messages.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task CompactionDoesNotInstallAnAutomaticProviderIndexInSerializedSession()
    {
        var history = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(history.CreateRepository(),
            compactor: new OnceCompactor(), compactionOptions: Options());
        var agent = Agent(provider);
        var session = await agent.CreateSessionAsync();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create(agent.Id, "session"));
        await agent.RunAsync("The original history contains deliberately lengthy details for reduction.", session);
        await agent.RunAsync("current", session);

        var serialized = await agent.SerializeSessionAsync(session);

        Assert.DoesNotContain("CompactionProvider", serialized.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderDoesNotSaveTheHostedSnapshotDuringForegroundLoad()
    {
        var history = new HistoryCosmosFixture();
        var sessions = new SessionCosmosSdkFixture();
        using var provider = new CosmosChatHistoryProvider(history.CreateRepository(),
            compactor: new OnceCompactor(), compactionOptions: Options());
        var agent = Agent(provider);
        var store = new CosmosAgentSessionStore(sessions.Repository, NullLogger<CosmosAgentSessionStore>.Instance);
        var session = await store.GetOrCreateSessionAsync(agent, new("session"));
        await agent.RunAsync("The original history contains deliberately lengthy details for reduction.", session);
        await agent.RunAsync("current", session);

        Assert.Equal(0, sessions.CompletedWrites);
    }

    [Fact]
    public async Task HostedSnapshotDuringPreparationExcludesCandidateButOriginalLiveSessionStillPublishesIt()
    {
        var history = new HistoryCosmosFixture();
        var sessions = new SessionCosmosSdkFixture();
        using var provider = new CosmosChatHistoryProvider(history.CreateRepository(),
            compactor: new OnceCompactor(), compactionOptions: Options());
        var agent = Agent(provider);
        var store = new CosmosAgentSessionStore(sessions.Repository, NullLogger<CosmosAgentSessionStore>.Instance);
        var key = new AgentSessionStoreKey("session");
        var session = await store.GetOrCreateSessionAsync(agent, key);
        await provider.InvokedAsync(new(agent, session, [new(ChatRole.User, new string('x', 1000))], []));
        var source = SessionPersistenceState.GetRequired(session).ActiveHistory;
        await provider.InvokingAsync(new(agent, session, []));

        await store.SaveSessionAsync(agent, key, session);
        var restored = await store.GetSessionAsync(agent, key);
        Assert.NotNull(restored);
        await provider.InvokedAsync(new(agent, session, [new(ChatRole.User, "current")], []));

        Assert.Equal(source, SessionPersistenceState.GetRequired(restored).ActiveHistory);
        Assert.DoesNotContain("summary", JsonSerializer.Serialize(sessions.Requests.Select(request => request.Body)), StringComparison.Ordinal);
        Assert.Equal(new[] { "summary", "current" }, (await history.CreateRepository()
            .ReadAsync(SessionPersistenceState.GetRequired(session).ActiveHistory)).Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task FrameworkSnapshotDoesNotRestorePreparedCandidateAndRestartRecalculatesIt()
    {
        var history = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(history.CreateRepository(),
            compactor: new OnceCompactor(), compactionOptions: Options());
        var agent = Agent(provider);
        var session = await agent.CreateSessionAsync();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create(agent.Id, "session"));
        await provider.InvokedAsync(new(agent, session, [new(ChatRole.User, new string('x', 1000))], []));
        await provider.InvokingAsync(new(agent, session, []));
        var serialized = await agent.SerializeSessionAsync(session);
        using var freshProvider = new CosmosChatHistoryProvider(history.CreateRepository(),
            compactor: new OnceCompactor(), compactionOptions: Options());
        var freshAgent = Agent(freshProvider);
        var restored = await freshAgent.DeserializeSessionAsync(serialized);
        var noPreparedState = new SessionPersistenceContext(agent.Id, SessionPersistenceState.GetRequired(session).ActiveHistory);

        Assert.Equal(noPreparedState, SessionPersistenceState.GetRequired(restored));
        var loaded = await freshProvider.InvokingAsync(new(freshAgent, restored, []));
        Assert.Equal("summary", Assert.Single(loaded).Text);
    }

    [Fact]
    public async Task PreparedAndPendingCompactionsRemainLiveOnly()
    {
        var history = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(history.CreateRepository(),
            compactor: new OnceCompactor(), compactionOptions: Options());
        var agent = Agent(provider);
        var session = await agent.CreateSessionAsync();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create(agent.Id, "session"));
        await provider.InvokedAsync(new(agent, session, [new(ChatRole.User, new string('x', 1000))], []));
        var source = SessionPersistenceState.GetRequired(session).ActiveHistory;
        await provider.InvokingAsync(new(agent, session, []));
        var pending = new PendingHistoryCompaction(source,
            new HistoryCompactionRequest(agent.Id, "binding", [new ChatMessage(ChatRole.User, "message")],
                new HistoryCompactionOptions { CompactorKey = "background", Mode = HistoryCompactionMode.Background }),
            new HistoryCompactionTicket("job", "binding"), Guid.NewGuid().ToString("N"));
        SessionPersistenceState.SetPendingCompaction(session, pending);
        SessionPersistenceState.SetHistory(session, source);

        var restored = new TestAgentSession(AgentSessionStateBag.Deserialize(session.StateBag.Serialize()));
        await provider.InvokedAsync(new(agent, session, [], []));

        Assert.Equal(new SessionPersistenceContext(agent.Id, source), SessionPersistenceState.GetRequired(restored));
        Assert.Equal("summary", Assert.Single((await history.CreateRepository()
            .ReadAsync(SessionPersistenceState.GetRequired(session).ActiveHistory)).Messages).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualModelFailureLeavesSourceIntactAndNextInvocationDoesNotReusePreparation(bool streaming)
    {
        var history = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(history.CreateRepository(),
            compactor: new OnceCompactor(), compactionOptions: Options());
        using var model = new FailureClient { Failure = new HttpRequestException("model unavailable") };
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Id = "failure-test", ChatHistoryProvider = provider
        });
        var session = await agent.CreateSessionAsync();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create(agent.Id, "session"));
        var original = new string('x', 1000);
        await provider.InvokedAsync(new(agent, session, [new(ChatRole.User, original)], []));
        var source = SessionPersistenceState.GetRequired(session).ActiveHistory;

        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(agent, session, streaming));

        Assert.Equal((source, 1), (SessionPersistenceState.GetRequired(session).ActiveHistory, history.Batches.Count));
        model.Failure = null;
        await RunAsync(agent, session, streaming);
        Assert.Equal(new[] { original, "current", "reply" }, (await history.CreateRepository()
            .ReadAsync(SessionPersistenceState.GetRequired(session).ActiveHistory)).Messages.Select(message => message.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualModelCancellationLeavesSourceIntactIncludingPartialStreamingOutput(bool streaming)
    {
        var history = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(history.CreateRepository(),
            compactor: new OnceCompactor(), compactionOptions: Options());
        using var cancellation = new CancellationTokenSource();
        using var model = new FailureClient { BeforeResponse = cancellation.Cancel };
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Id = "cancellation-test", ChatHistoryProvider = provider
        });
        var session = await agent.CreateSessionAsync();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create(agent.Id, "session"));
        await provider.InvokedAsync(new(agent, session, [new(ChatRole.User, new string('x', 1000))], []));
        var source = SessionPersistenceState.GetRequired(session).ActiveHistory;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(agent, session, streaming, cancellation.Token));

        Assert.Equal((source, 1), (SessionPersistenceState.GetRequired(session).ActiveHistory, history.Batches.Count));
        model.BeforeResponse = null;
        await RunAsync(agent, session, streaming);
        Assert.Equal(source.WithRevision(2), SessionPersistenceState.GetRequired(session).ActiveHistory);
    }

    private static async Task RunAsync(AIAgent agent, AgentSession session, bool streaming, CancellationToken cancellationToken = default)
    {
        if (streaming)
        {
            await foreach (var _ in agent.RunStreamingAsync("current", session, cancellationToken: cancellationToken)) { }
        }
        else
        {
            await agent.RunAsync("current", session, cancellationToken: cancellationToken);
        }
    }

    private static HistoryCompactionOptions Options() => new() { CompactorKey = "test", MaxHistoryUtf8Bytes = 10000 };

    private static ChatClientAgent Agent(CosmosChatHistoryProvider provider)
    {
        var client = new Mock<IChatClient>();
        client.Setup(value => value.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")));
        return new(client.Object, new ChatClientAgentOptions
        {
            Id = "compaction-agent", Name = "compaction-agent", ChatHistoryProvider = provider
        });
    }

    private sealed class OnceCompactor : IHistoryCompactor
    {
        private bool _completed;
        public IReadOnlySet<HistoryCompactionMode> SupportedModes { get; } =
            new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Foreground };

        public Task<HistoryCompactionResult> CompactAsync(HistoryCompactionRequest request, CancellationToken cancellationToken = default)
        {
            var sourceSize = HistoryCompactionValidation.Measure(request.Messages);
            if (_completed || request.Messages.Count == 0)
                return Task.FromResult(new HistoryCompactionResult(HistoryCompactionStatus.Unchanged,
                    request.SourceBinding, request.Messages, sourceSize, sourceSize));
            _completed = true;
            ChatMessage[] candidate = [new(ChatRole.User, "summary")];
            return Task.FromResult(new HistoryCompactionResult(HistoryCompactionStatus.Completed,
                request.SourceBinding, candidate, sourceSize, HistoryCompactionValidation.Measure(candidate)));
        }

    }

    private sealed class FailureClient : IChatClient
        {
            internal HttpRequestException? Failure { get; set; }
            internal Action? BeforeResponse { get; set; }

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                BeforeResponse?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (Failure is { } failure) throw failure;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")));
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                // A model can fail after emitting an update. None of that partial response is history.
                if (Failure is not null || BeforeResponse is not null)
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
                var response = await GetResponseAsync(messages, options, cancellationToken);
                foreach (var update in response.ToChatResponseUpdates())
                    yield return update;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
    }
}
