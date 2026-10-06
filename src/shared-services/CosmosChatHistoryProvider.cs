using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace SharedServices;

/// <summary>
/// Cosmos history adapter using only the required shared session context. Standalone callers must
/// initialize SessionPersistenceState explicitly before invoking the provider.
/// </summary>
public sealed class CosmosChatHistoryProvider : ChatHistoryProvider, IDisposable
{
    private readonly CosmosChatMessageRepository _repository;
    private readonly CosmosClient? _ownedClient;
    private bool _disposed;
    private int? _maxMessagesToRetrieve;

    /// <summary>Uses the shared repository; no ambient identity or fallback history state is consulted.</summary>
    public CosmosChatHistoryProvider(
        CosmosChatMessageRepository repository,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null)
        : base(provideOutputMessageFilter, storeInputRequestMessageFilter: storeInputMessageFilter, storeInputResponseMessageFilter: null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    /// <summary>Uses an existing client, optionally owning it; session context remains required.</summary>
    public CosmosChatHistoryProvider(
        CosmosClient cosmosClient, string databaseId, string containerId, bool ownsClient = false,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(new CosmosChatMessageRepository(cosmosClient, databaseId, containerId, logger), provideOutputMessageFilter, storeInputMessageFilter)
    {
        DatabaseId = databaseId;
        ContainerId = containerId;
        _ownedClient = ownsClient ? cosmosClient : null;
    }

    /// <summary>Creates and owns a client from a connection string; no persistence state is created implicitly.</summary>
    public CosmosChatHistoryProvider(
        string connectionString, string databaseId, string containerId,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(CreateClient(connectionString), databaseId, containerId, true, provideOutputMessageFilter, storeInputMessageFilter, logger) { }

    /// <summary>Creates and owns a client using token credentials; session context is still required.</summary>
    public CosmosChatHistoryProvider(
        string accountEndpoint, TokenCredential tokenCredential, string databaseId, string containerId,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(CreateClient(accountEndpoint, tokenCredential), databaseId, containerId, true, provideOutputMessageFilter, storeInputMessageFilter, logger) { }

    /// <summary>Query page size.</summary>
    public int MaxItemCount { get => _repository.MaxItemCount; set => _repository.MaxItemCount = value; }
    /// <summary>Total batch operations including the mandatory head update (2..100).</summary>
    public int MaxBatchSize { get => _repository.MaxBatchSize; set => _repository.MaxBatchSize = value; }
    /// <summary>Optional latest-message limit; when specified the legacy reducer is bypassed.</summary>
    public int? MaxMessagesToRetrieve
    {
        get => _maxMessagesToRetrieve;
        set => _maxMessagesToRetrieve = value is <= 0
            ? throw new ArgumentOutOfRangeException(nameof(value), HistoryErrors.Get("PositiveLimit")) : value;
    }
    /// <summary>Message retention, default 24 hours; null/-1 means permanent.</summary>
    public int? MessageTtlSeconds { get; set; } = 86400;
    /// <summary>The database id when constructed from a client.</summary>
    public string? DatabaseId { get; }
    /// <summary>The container id when constructed from a client.</summary>
    public string? ContainerId { get; }
    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => [SessionPersistenceState.StateKey];

#pragma warning disable MEAI001
    /// <summary>Legacy opt-in reducer; off by default. No automatic compaction or history rotation is added.</summary>
    public IChatReducer? ChatReducer { get; init; }
#pragma warning restore MEAI001
    /// <summary>Legacy reduction storage policy, applied only when an explicitly configured reducer reduces history.</summary>
    public ReductionStoragePolicy ReductionStoragePolicy { get; init; } = ReductionStoragePolicy.Clear;

    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var session = context.Session;
        ArgumentNullException.ThrowIfNull(session);
        var reference = GetReference(session);
        var result = await _repository.ReadAsync(reference, MaxMessagesToRetrieve, cancellationToken).ConfigureAwait(false);
        var messages = result.Messages;
        RestoreProcessedApprovals(messages);
        if (MaxMessagesToRetrieve is null && ChatReducer is not null)
        {
            var reduced = (await ChatReducer.ReduceAsync(messages, cancellationToken).ConfigureAwait(false)).ToArray();
            if (reduced.Length < messages.Count)
            {
                if (ReductionStoragePolicy == ReductionStoragePolicy.Archive)
                {
                    await _repository.ArchiveAsync(reference,
                        new(reference.ScopeKey, $"{reference.ConversationId}_archived_{Guid.NewGuid():N}"),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                var cleared = await _repository.ClearAsync(reference,
                    cursor => SessionPersistenceState.SetHistory(session, cursor), cancellationToken).ConfigureAwait(false);
                await _repository.AppendAsync(cleared.Reference, reduced, MessageTtlSeconds,
                    cursor => SessionPersistenceState.SetHistory(session, cursor), cancellationToken).ConfigureAwait(false);
                messages = reduced;
            }
        }
        return messages;
    }

    private static void RestoreProcessedApprovals(IReadOnlyList<ChatMessage> messages)
    {
        // The framework mutates old approval objects after execution. Append-only storage retains
        // their earlier state; a persisted result proves the call is no longer pending.
        var completedCalls = messages.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(result => result.CallId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var content in messages.SelectMany(message => message.Contents))
        {
            var call = content switch
            {
                ToolApprovalRequestContent request => request.ToolCall as FunctionCallContent,
                ToolApprovalResponseContent response => response.ToolCall as FunctionCallContent,
                _ => null
            };
            if (call is not null && completedCalls.Contains(call.CallId))
                call.InformationalOnly = true;
        }
    }

    /// <inheritdoc />
    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        var session = context.Session;
        ArgumentNullException.ThrowIfNull(session);
        var reference = GetReference(session);
        var messages = context.RequestMessages.Concat(context.ResponseMessages ?? []).ToArray();
        await _repository.AppendAsync(reference, messages, MessageTtlSeconds,
            cursor => SessionPersistenceState.SetHistory(session, cursor), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts live messages using the required shared history cursor.</summary>
    public Task<int> GetMessageCountAsync(AgentSession session, CancellationToken cancellationToken = default) =>
        _repository.CountAsync(GetReference(session), cancellationToken);

    /// <summary>Clears live messages, advancing shared state once per committed conditional batch.</summary>
    public async Task<int> ClearMessagesAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        var result = await _repository.ClearAsync(GetReference(session),
            cursor => SessionPersistenceState.SetHistory(session, cursor), cancellationToken).ConfigureAwait(false);
        return result.MessageCount;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _ownedClient?.Dispose();
        _disposed = true;
    }

    private HistoryReference GetReference(AgentSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SessionPersistenceState.GetRequired(session).ActiveHistory;
    }

    private static CosmosClient CreateClient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new(connectionString);
    }

    private static CosmosClient CreateClient(string endpoint, TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(credential);
        return new(endpoint, credential);
    }
}
