using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SharedServices;

namespace SharedServices.Tests;

public class SessionPersistenceContextTests
{
    [Fact]
    public void AnonymousAliasesKeepOriginalHistoryAnchor()
    {
        var initial = SessionStorageAddress.Create("agent", "resp_1");
        var context = SessionPersistenceContext.Create(initial);
        var alias = SessionStorageAddress.Create("agent", "resp_2");

        context.ValidateFor(alias);

        Assert.Equal(initial.ScopeKey, context.ActiveHistory.ScopeKey);
    }

    [Fact]
    public void AuthenticatedAliasWithSameAllPartitionsIsCompatible()
    {
        var partitions = new Dictionary<string, string> { ["isolation"] = "owner", ["region"] = "it" };
        var context = SessionPersistenceContext.Create(SessionStorageAddress.Create("agent", "resp_1", partitions));

        var error = Record.Exception(() => context.ValidateFor(SessionStorageAddress.Create("agent", "resp_2", partitions)));

        Assert.Null(error);
    }

    [Theory]
    [InlineData("isolation", "other")]
    [InlineData("region", "de")]
    public void AnyChangedTrustedPartitionIsIncompatible(string name, string value)
    {
        var partitions = new Dictionary<string, string> { ["isolation"] = "owner", ["region"] = "it" };
        var context = SessionPersistenceContext.Create(SessionStorageAddress.Create("agent", "id", partitions));
        partitions[name] = value;

        Assert.Throws<InvalidOperationException>(() => context.ValidateFor(SessionStorageAddress.Create("agent", "alias", partitions)));
    }

    [Fact]
    public void DifferentAgentCannotReuseContext()
    {
        var context = SessionPersistenceContext.Create(SessionStorageAddress.Create("text", "id"));

        Assert.Throws<InvalidOperationException>(() => context.ValidateFor(SessionStorageAddress.Create("voice", "id")));
    }

    [Fact]
    public void AnonymousContextCannotBecomeAuthenticated()
    {
        var context = SessionPersistenceContext.Create(SessionStorageAddress.Create("agent", "id"));

        Assert.Throws<InvalidOperationException>(() => context.ValidateFor(SessionStorageAddress.Create("agent", "id",
            new Dictionary<string, string> { ["isolation"] = "owner" })));
    }

    [Fact]
    public void AuthenticatedContextCannotFallBackToAnonymous()
    {
        var context = SessionPersistenceContext.Create(SessionStorageAddress.Create("agent", "id",
            new Dictionary<string, string> { ["isolation"] = "owner" }));

        Assert.Throws<InvalidOperationException>(() => context.ValidateFor(SessionStorageAddress.Create("agent", "id")));
    }

    [Fact]
    public void HistoryScopeCannotBeReanchoredToAnAnonymousAlias()
    {
        var context = SessionPersistenceContext.Create(SessionStorageAddress.Create("agent", "old"));
        var history = new HistoryReference(StorageScope.Create("new"), context.ActiveHistory.ConversationId, 1);

        Assert.Throws<InvalidOperationException>(() => context.WithHistory(history));
    }

    [Fact]
    public void RevisionCannotMoveBackwards()
    {
        var history = new HistoryReference(StorageScope.Create("id"), "history", 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => history.WithRevision(1));
    }

    [Fact]
    public void NegativeHistoryRevisionIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryReference(StorageScope.Create("id"), "history", -1));
    }

    [Fact]
    public void ContextUpdateCannotMoveHistoryRevisionBackwards()
    {
        var context = new SessionPersistenceContext("agent", new HistoryReference(StorageScope.Create("id"), "history", 2));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.WithHistory(new HistoryReference(context.ActiveHistory.ScopeKey, "history", 1)));
    }

    [Fact]
    public void MalformedStateBagContextIsRejected()
    {
        var session = new TestAgentSession();
        session.StateBag.SetValue(SessionPersistenceState.StateKey, "not a persistence context");

        Assert.Throws<InvalidOperationException>(() => SessionPersistenceState.GetRequired(session));
    }

    [Fact]
    public void MissingContextFailsRatherThanInventingAnonymousState()
    {
        Assert.Throws<InvalidOperationException>(() => SessionPersistenceState.GetRequired(new TestAgentSession()));
    }

    [Fact]
    public void ContextCannotBeInitializedTwice()
    {
        var session = new TestAgentSession();
        var address = SessionStorageAddress.Create("agent", "id");
        SessionPersistenceState.Initialize(session, address);

        Assert.Throws<InvalidOperationException>(() => SessionPersistenceState.Initialize(session, address));
    }

    [Fact]
    public void ContextRoundTripsThroughRealFrameworkStateBag()
    {
        var session = new TestAgentSession();
        var context = SessionPersistenceState.Initialize(session, SessionStorageAddress.Create("agent", "id"));
        SessionPersistenceState.SetHistory(session, context.ActiveHistory.WithRevision(7));
        var restored = new TestAgentSession(AgentSessionStateBag.Deserialize(session.StateBag.Serialize()));

        Assert.Equal(context.WithHistory(context.ActiveHistory.WithRevision(7)), SessionPersistenceState.GetRequired(restored));
    }

    [Fact]
    public void StateBagContainsOnlyOneHistoryRepresentation()
    {
        var session = new TestAgentSession();
        var context = SessionPersistenceState.Initialize(session, SessionStorageAddress.Create("agent", "id"));
        SessionPersistenceState.SetHistory(session, context.ActiveHistory.WithRevision(1));

        Assert.Equal(1, session.StateBag.Count);
    }

    [Fact]
    public void AgentsSharingLookupGetIndependentHistoryIds()
    {
        var text = SessionPersistenceContext.Create(SessionStorageAddress.Create("text", "id"));
        var voice = SessionPersistenceContext.Create(SessionStorageAddress.Create("voice", "id"));

        Assert.NotEqual(text.ActiveHistory.ConversationId, voice.ActiveHistory.ConversationId);
    }

    [Fact]
    public void PendingJobIsLiveOnlyAndDoesNotRoundTrip()
    {
        var session = new TestAgentSession();
        var original = SessionPersistenceState.Initialize(session, SessionStorageAddress.Create("agent", "id"));
        var pending = Pending(original.ActiveHistory);
        SessionPersistenceState.SetPendingCompaction(session, pending);
        SessionPersistenceState.SetHistory(session, original.ActiveHistory.WithRevision(1));
        var restored = new TestAgentSession(AgentSessionStateBag.Deserialize(session.StateBag.Serialize()));

        Assert.Equal(pending, SessionPersistenceState.GetRequired(session).PendingCompaction);
        Assert.Null(SessionPersistenceState.GetRequired(restored).PendingCompaction);
        Assert.Equal(1, restored.StateBag.Count);
    }

    [Fact]
    public void OldSnapshotsWithoutPendingFieldRemainValid()
    {
        var json = """{"agentId":"agent","activeHistory":{"scopeKey":"[\"anonymous\",\"id\"]","conversationId":"history","revision":1}}""";
        var state = System.Text.Json.JsonSerializer.Deserialize<SessionPersistenceContext>(json);

        Assert.NotNull(state);
        Assert.Null(state.PendingCompaction);
        Assert.DoesNotContain("pendingCompaction", System.Text.Json.JsonSerializer.Serialize(state), StringComparison.Ordinal);
    }

    private static PendingHistoryCompaction Pending(HistoryReference source) =>
        new(source,
            new HistoryCompactionRequest("agent", "source-binding", [new ChatMessage(ChatRole.User, "message")],
                new HistoryCompactionOptions { CompactorKey = "background", Mode = HistoryCompactionMode.Background }),
            new HistoryCompactionTicket("job", "source-binding"),
            Guid.NewGuid().ToString("N"));
}
