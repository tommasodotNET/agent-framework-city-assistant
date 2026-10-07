using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SharedServices;

/// <summary>The sole serialized representation of the active history and its expected revision.</summary>
public sealed record HistoryReference
{
    /// <summary>Restores a complete history reference; revision zero represents a new history.</summary>
    [JsonConstructor]
    public HistoryReference(string scopeKey, string conversationId, long revision)
    {
        var address = new HistoryStorageAddress(scopeKey, conversationId);
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), StorageErrors.Get("InvalidRevision"));
        }
        ScopeKey = address.ScopeKey;
        ConversationId = address.ConversationId;
        Revision = revision;
    }

    /// <summary>The immutable history anchor scope, not the current session document scope.</summary>
    [JsonPropertyName("scopeKey")]
    public string ScopeKey { get; }
    /// <summary>The server-generated history id.</summary>
    [JsonPropertyName("conversationId")]
    public string ConversationId { get; }
    /// <summary>The expected linear history revision; not a Cosmos ETag.</summary>
    [JsonPropertyName("revision")]
    public long Revision { get; }

    /// <summary>Returns the complete address for all history operations.</summary>
    public HistoryStorageAddress ToAddress() => new(ScopeKey, ConversationId);

    /// <summary>Advances this reference after a successful conditional history operation.</summary>
    public HistoryReference WithRevision(long revision)
    {
        if (revision < Revision)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), StorageErrors.Get("InvalidRevision"));
        }
        return new(ScopeKey, ConversationId, revision);
    }
}

/// <summary>
/// Serialized per-session ownership and history state. It contains neither current lookup aliases
/// nor ETags. The same type is suitable for the voice application snapshot.
/// </summary>
public sealed record SessionPersistenceContext
{
    /// <summary>Restores ownership and the single active history reference.</summary>
    [JsonConstructor]
    public SessionPersistenceContext(string agentId, HistoryReference activeHistory, PendingHistoryCompaction? pendingCompaction = null)
    {
        StorageSchema.ValidateKey(agentId, nameof(agentId));
        ArgumentNullException.ThrowIfNull(activeHistory);
        if (pendingCompaction is { } pending
            && (pending.Source.ScopeKey != activeHistory.ScopeKey
                || pending.Source.ConversationId != activeHistory.ConversationId
                || pending.Source.Revision > activeHistory.Revision))
            throw new InvalidOperationException(StorageErrors.Get("IncompatibleContext"));
        AgentId = agentId;
        ActiveHistory = activeHistory;
        PendingCompaction = pendingCompaction;
    }

    /// <summary>The stable agent owning this session and its history.</summary>
    [JsonPropertyName("agentId")]
    public string AgentId { get; }
    /// <summary>The single source of truth for the active history, including its anchor scope.</summary>
    [JsonPropertyName("activeHistory")]
    public HistoryReference ActiveHistory { get; }

    /// <summary>Optional background job; lives only in the serialized session, not the snapshot envelope.</summary>
    [JsonPropertyName("pendingCompaction"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PendingHistoryCompaction? PendingCompaction { get; }

    // Live invocation state only. Serializing a session must not turn a prepared model view
    // into a durable job or lose it from the original session while inference is in progress.
    [JsonIgnore]
    internal PreparedHistoryCompaction? PreparedCompaction { get; init; }

    /// <summary>Updates the pending job without changing ownership or active history.</summary>
    public SessionPersistenceContext WithPendingCompaction(PendingHistoryCompaction? pending) =>
        new(AgentId, ActiveHistory, pending) { PreparedCompaction = PreparedCompaction };

    /// <summary>Initializes new state before history is used; the history id is generated server-side.</summary>
    public static SessionPersistenceContext Create(SessionStorageAddress initialAddress)
    {
        ArgumentNullException.ThrowIfNull(initialAddress);
        return new(initialAddress.AgentId,
            new HistoryReference(initialAddress.ScopeKey, Guid.NewGuid().ToString("N"), 0));
    }

    /// <summary>
    /// Validates agent and authenticated ownership. Anonymous aliases intentionally need not have
    /// equal lookup scopes; both must remain anonymous. This does not authenticate anonymous clients.
    /// </summary>
    public void ValidateFor(SessionStorageAddress currentAddress)
    {
        ArgumentNullException.ThrowIfNull(currentAddress);
        var historyIsAnonymous = StorageScope.IsAnonymous(ActiveHistory.ScopeKey);
        var currentIsAnonymous = StorageScope.IsAnonymous(currentAddress.ScopeKey);
        if (!string.Equals(AgentId, currentAddress.AgentId, StringComparison.Ordinal)
            || historyIsAnonymous != currentIsAnonymous
            || (!historyIsAnonymous
                && !string.Equals(ActiveHistory.ScopeKey, currentAddress.ScopeKey, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(StorageErrors.Get("IncompatibleContext"));
        }
    }

    /// <summary>Replaces the history reference without changing its immutable anchor scope.</summary>
    public SessionPersistenceContext WithHistory(HistoryReference history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (!string.Equals(ActiveHistory.ScopeKey, history.ScopeKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(StorageErrors.Get("IncompatibleContext"));
        }
        if (ActiveHistory.ConversationId == history.ConversationId && history.Revision < ActiveHistory.Revision)
        {
            throw new ArgumentOutOfRangeException(nameof(history), StorageErrors.Get("InvalidRevision"));
        }
        var sameConversation = history.ConversationId == ActiveHistory.ConversationId;
        return new(AgentId, history, sameConversation ? PendingCompaction : null)
        {
            PreparedCompaction = sameConversation ? PreparedCompaction : null
        };
    }
}

internal sealed record PreparedHistoryCompaction(
    HistoryReference Source, IReadOnlyList<ChatMessage> Messages, string OperationId, HistoryCompactionOptions Options);

/// <summary>
/// Explicit StateBag entry point shared by the session store and history provider. The provider
/// uses this same ProviderSessionState entry, not a second active-history state.
/// </summary>
public static class SessionPersistenceState
{
    /// <summary>The stable serialized StateBag key; include it in the history provider's StateKeys.</summary>
    public const string StateKey = "SharedServices.SessionPersistence.v2";

    private static readonly ProviderSessionState<SessionPersistenceContext> s_state = new(
        _ => throw new InvalidOperationException(StorageErrors.Get("MissingContext")), StateKey);

    /// <summary>Stamps a newly created or standalone session exactly once, before provider execution.</summary>
    public static SessionPersistenceContext Initialize(AgentSession session, SessionStorageAddress address)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(address);
        if (session.StateBag.GetValue<SessionPersistenceContext>(StateKey) is not null)
        {
            throw new InvalidOperationException(StorageErrors.Get("AlreadyInitialized"));
        }
        var context = SessionPersistenceContext.Create(address);
        s_state.SaveState(session, context);
        return context;
    }

    /// <summary>Gets required state; missing or malformed state is never silently initialized.</summary>
    public static SessionPersistenceContext GetRequired(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return s_state.GetOrInitializeState(session);
    }

    /// <summary>Updates the sole active-history reference after successful history persistence.</summary>
    public static void SetHistory(AgentSession session, HistoryReference history)
    {
        ArgumentNullException.ThrowIfNull(session);
        var context = GetRequired(session);
        s_state.SaveState(session, context.WithHistory(history));
    }

    /// <summary>Records or clears a durable job; normal hosting remains responsible for the session checkpoint.</summary>
    public static void SetPendingCompaction(AgentSession session, PendingHistoryCompaction? pending) =>
        s_state.SaveState(session, GetRequired(session).WithPendingCompaction(pending));

    internal static void SetPreparedCompaction(AgentSession session, PreparedHistoryCompaction? prepared) =>
        s_state.SaveState(session, GetRequired(session) with { PreparedCompaction = prepared });
}
