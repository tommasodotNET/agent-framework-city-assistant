using System.Text.Json;
using Microsoft.Extensions.AI;
using SharedServices;

namespace VoiceOrchestratorAgent;

/// <summary>Adapts voice working copies to the same snapshot/history repositories as text.</summary>
public sealed class VoiceConversationStore
{
    /// <summary>Stable discriminator; client continuation ids are not modified.</summary>
    public const string AgentId = "voice-orchestrator-agent";
    /// <summary>Voice snapshots and messages retain the existing seven-day policy.</summary>
    public const int RetentionSeconds = 7 * 86400;

    private readonly CosmosSessionRepository _sessions;
    private readonly CosmosChatMessageRepository _history;

    /// <summary>Uses shared repositories without keeping any caller state on this service.</summary>
    public VoiceConversationStore(CosmosSessionRepository sessions, CosmosChatMessageRepository history)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(history);
        _sessions = sessions;
        _history = history;
    }

    /// <summary>Loads and validates the complete resume history before a connection can be ready.</summary>
    public async Task<VoiceConversationSession> LoadAsync(
        SessionStorageAddress? address, CancellationToken cancellationToken = default)
    {
        if (address is null) return new(null, null, []);
        if (address.AgentId != AgentId) throw new ArgumentException(VoiceErrors.Get("WrongAgent"), nameof(address));

        var stored = await _sessions.ReadAsync(address, cancellationToken).ConfigureAwait(false);
        var context = stored is null
            ? SessionPersistenceContext.Create(address)
            : stored.Document.SerializedSession.Deserialize<SessionPersistenceContext>()
                ?? throw new InvalidOperationException(VoiceErrors.Get("InvalidSnapshot"));
        context.ValidateFor(address);
        var history = await _history.ReadAsync(context.ActiveHistory, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var workingCopy = new VoiceConversationSession(address, context, history.Messages);
        if (stored is not null) workingCopy.WriteTracker.Record(stored.Version);
        return workingCopy;
    }

    /// <summary>
    /// Appends only the current connection's messages, then conditionally writes its snapshot.
    /// A failed/partial save is not retried: history and snapshots are not one transaction.
    /// </summary>
    public async Task SaveAsync(VoiceConversationSession session,
        IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messages);
        if (session.Address is null) return;
        if (session.SaveAttempted) throw new InvalidOperationException(VoiceErrors.Get("SaveAlreadyAttempted"));
        session.SaveAttempted = true;
        var context = session.Context ?? throw new InvalidOperationException(VoiceErrors.Get("InvalidSnapshot"));
        context.ValidateFor(session.Address);
        await _history.AppendAsync(context.ActiveHistory,
            messages.Select(VoiceMessageAdapter.ToChatMessage).ToArray(), RetentionSeconds,
            reference =>
            {
                context = context.WithHistory(reference);
                session.Context = context;
            },
            cancellationToken).ConfigureAwait(false);
        var document = SessionDocument.Create(session.Address, JsonSerializer.SerializeToElement(session.Context),
            DateTimeOffset.UtcNow, RetentionSeconds);
        var version = await _sessions.WriteAsync(document, session.WriteTracker.ForAddress(session.Address),
            cancellationToken).ConfigureAwait(false);
        session.WriteTracker.Record(version);
    }
}

/// <summary>One connection's captured address, replay messages and nonserialized write conditions.</summary>
public sealed class VoiceConversationSession
{
    internal VoiceConversationSession(SessionStorageAddress? address, SessionPersistenceContext? context,
        IReadOnlyList<ChatMessage> messages)
    {
        Address = address;
        Context = context;
        Messages = messages;
    }

    /// <summary>The captured full address, or null for an intentionally nonresumable connection.</summary>
    public SessionStorageAddress? Address { get; }
    /// <summary>The application snapshot; never contains the live SDK connection.</summary>
    public SessionPersistenceContext? Context { get; internal set; }
    /// <summary>Validated structured history to replay as native VoiceLive conversation items.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; }
    internal SessionWriteTracker WriteTracker { get; } = new();
    internal bool SaveAttempted { get; set; }
}
