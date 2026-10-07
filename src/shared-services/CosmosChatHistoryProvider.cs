using Azure.Core;
using System.Globalization;
using System.Resources;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharedServices;

/// <summary>
/// Cosmos history adapter using only the required shared session context. Standalone callers must
/// initialize SessionPersistenceState explicitly before invoking the provider.
/// </summary>
/// <remarks>
/// Compaction is limited to one rotation per provider invocation, including exact recovery. The
/// provider has no hosted snapshot checkpoint notification; a later invocation on the same working
/// session can rotate again before that checkpoint. Recovery does not follow a chain of rotations.
/// </remarks>
public sealed class CosmosChatHistoryProvider : ChatHistoryProvider, IDisposable
{
    private readonly CosmosChatMessageRepository _repository;
    private readonly CosmosClient? _ownedClient;
    private readonly IHistoryCompactor? _compactor;
    private readonly HistoryCompactionOptions? _compactionOptions;
    private readonly ILogger<CosmosChatHistoryProvider> _logger;
    private bool _disposed;
    private int? _maxMessagesToRetrieve;

    /// <summary>Uses the shared repository; no ambient identity or fallback history state is consulted.</summary>
    public CosmosChatHistoryProvider(
        CosmosChatMessageRepository repository,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        IHistoryCompactor? compactor = null,
        HistoryCompactionOptions? compactionOptions = null,
        ILogger<CosmosChatHistoryProvider>? logger = null)
        : base(provideOutputMessageFilter, storeInputRequestMessageFilter: storeInputMessageFilter, storeInputResponseMessageFilter: null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if ((compactor is null) != (compactionOptions is null))
            throw new ArgumentException(HistoryProviderErrors.Get("CompactorOptionsPair"), nameof(compactor));
        compactionOptions?.Validate();
        if (compactionOptions is not null)
            HistoryCompactionExtensions.ValidateCapabilities(compactor!, compactionOptions);
        _repository = repository;
        _compactor = compactor;
        _compactionOptions = compactionOptions is null ? null : compactionOptions with { };
        _logger = logger ?? NullLogger<CosmosChatHistoryProvider>.Instance;
    }

    /// <summary>Uses an existing client, optionally owning it; session context remains required.</summary>
    public CosmosChatHistoryProvider(
        CosmosClient cosmosClient, string databaseId, string containerId, bool ownsClient = false,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        ILogger<CosmosChatHistoryProvider>? logger = null,
        IHistoryCompactor? compactor = null,
        HistoryCompactionOptions? compactionOptions = null)
        : this(new CosmosChatMessageRepository(cosmosClient, databaseId, containerId, logger),
            provideOutputMessageFilter, storeInputMessageFilter, compactor, compactionOptions, logger)
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
        ILogger<CosmosChatHistoryProvider>? logger = null,
        IHistoryCompactor? compactor = null,
        HistoryCompactionOptions? compactionOptions = null)
        : this(CreateClient(connectionString), databaseId, containerId, true,
            provideOutputMessageFilter, storeInputMessageFilter, logger, compactor, compactionOptions) { }

    /// <summary>Creates and owns a client using token credentials; session context is still required.</summary>
    public CosmosChatHistoryProvider(
        string accountEndpoint, TokenCredential tokenCredential, string databaseId, string containerId,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        ILogger<CosmosChatHistoryProvider>? logger = null,
        IHistoryCompactor? compactor = null,
        HistoryCompactionOptions? compactionOptions = null)
        : this(CreateClient(accountEndpoint, tokenCredential), databaseId, containerId, true,
            provideOutputMessageFilter, storeInputMessageFilter, logger, compactor, compactionOptions) { }

    /// <summary>Query page size.</summary>
    public int MaxItemCount { get => _repository.MaxItemCount; set => _repository.MaxItemCount = value; }
    /// <summary>Total batch operations including the mandatory head update (2..100).</summary>
    public int MaxBatchSize { get => _repository.MaxBatchSize; set => _repository.MaxBatchSize = value; }
    /// <summary>Optional latest-message read limit; does not modify storage and cannot be combined with compaction.</summary>
    public int? MaxMessagesToRetrieve
    {
        get => _maxMessagesToRetrieve;
        set
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), HistoryErrors.Get("PositiveLimit"));
            if (value.HasValue && _compactionOptions is not null)
                throw new ArgumentException(HistoryProviderErrors.Get("PartialHistoryConflict"), nameof(MaxMessagesToRetrieve));
            _maxMessagesToRetrieve = value;
        }
    }
    /// <summary>Message retention, default 24 hours; null/-1 means permanent.</summary>
    public int? MessageTtlSeconds { get; set; } = 86400;
    /// <summary>The database id when constructed from a client.</summary>
    public string? DatabaseId { get; }
    /// <summary>The container id when constructed from a client.</summary>
    public string? ContainerId { get; }
    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => [SessionPersistenceState.StateKey];

    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var session = context.Session;
        ArgumentNullException.ThrowIfNull(session);
        var reference = GetReference(session);
        // The base skips Store on model failure. A fresh invocation (including an opted-out
        // profile) must discard that abandoned view before any read or compactor can fail.
        SessionPersistenceState.SetPreparedCompaction(session, null);
        DiscardIncompatiblePending(session);
        HistoryReadResult result;
        var recoveredRotation = false;
        try
        {
            result = await _repository.ReadAsync(reference, MaxMessagesToRetrieve, cancellationToken).ConfigureAwait(false);
        }
        catch (HistoryConcurrencyException)
        {
            // Old hosted snapshots may point at a retired source, even after opting out. Resolve
            // only that exact transition; never catch up to a later turn or follow a second rotation.
            var target = await _repository.ResolveRotationAsync(reference, cancellationToken).ConfigureAwait(false);
            if (target is null) throw;
            result = await _repository.ReadAsync(target, MaxMessagesToRetrieve, cancellationToken).ConfigureAwait(false);
            reference = target;
            recoveredRotation = true;
        }
        var messages = result.Messages;
        RestoreProcessedApprovals(messages);
        if (recoveredRotation)
        {
            if (_compactionOptions is not null)
                HistoryCompactionValidation.ValidateFallback(messages, _compactionOptions.MaxHistoryUtf8Bytes);
            SessionPersistenceState.SetHistory(session, reference);
            return messages;
        }

        if (_compactionOptions is null)
            return messages;

        return _compactionOptions.Mode switch
        {
            HistoryCompactionMode.Foreground => await CompactForegroundAsync(session, result, cancellationToken).ConfigureAwait(false),
            HistoryCompactionMode.Background => await StartBackgroundAsync(session, result, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException(CompactionErrors.Get("UnsupportedMode"))
        };
    }

    /// <inheritdoc />
    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        var session = context.Session;
        ArgumentNullException.ThrowIfNull(session);
        var reference = GetReference(session);
        var messages = context.RequestMessages.Concat(context.ResponseMessages ?? []).ToArray();
        DiscardIncompatiblePending(session);
        var prepared = SessionPersistenceState.GetRequired(session).PreparedCompaction;
        if (prepared is not null)
        {
            if (_compactionOptions != prepared.Options)
            {
                SessionPersistenceState.SetPreparedCompaction(session, null);
            }
            else
            {
                if (reference != prepared.Source)
                    throw new HistoryConcurrencyException();

                // Only the old, complete prefix was compacted and validated. Append the exact
                // filtered suffix without rewriting it: tool/approval saves can be incomplete.
                // Revalidating it as a reduction would incorrectly reject ordinary tool pauses.
                var merged = prepared.Messages.Concat(CopyMessages(messages)).ToArray();
                HistoryCompactionValidation.ValidateFallback(merged, prepared.Options.MaxHistoryUtf8Bytes);
                await PublishHistoryAsync(session, prepared.Source, merged, prepared.OperationId, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        var pending = SessionPersistenceState.GetRequired(session).PendingCompaction;

        if (pending is not null)
        {
            var applied = await TryApplyBackgroundAsync(session, pending, messages, cancellationToken).ConfigureAwait(false);
            if (applied)
                return;
        }

        await _repository.AppendAsync(reference, messages, MessageTtlSeconds,
            cursor => SessionPersistenceState.SetHistory(session, cursor), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ChatMessage>> CompactForegroundAsync(
        AgentSession session, HistoryReadResult history, CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var request = CreateCompactionRequest(session, history, operationId);
        HistoryCompactionResult result;
        try
        {
            result = DetachResult(await StartCompactionAsync(request, cancellationToken).ConfigureAwait(false));
            HistoryCompactionValidation.ValidateResult(request, result);
        }
        catch (Exception exception) when (IsCompactorFailure(exception))
        {
            return await FallbackAsync(FailureCategory(exception), history.Messages,
                history.Reference, request.Options, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (result.Status == HistoryCompactionStatus.Unchanged)
            return history.Messages;

        SessionPersistenceState.SetPreparedCompaction(session,
            new(history.Reference, result.Messages, operationId, request.Options));
        // The output filter, framework and model may mutate their view. The ready canonical
        // candidate must remain private until Store publishes it with the newly filtered turn.
        return CopyMessages(result.Messages);
    }

    private async Task<IReadOnlyList<ChatMessage>> StartBackgroundAsync(
        AgentSession session, HistoryReadResult history, CancellationToken cancellationToken)
    {
        var options = _compactionOptions ?? throw new InvalidOperationException();
        // This invocation still uses the original history; a future job cannot fix its current budget.
        HistoryCompactionValidation.ValidateFallback(history.Messages, options.MaxHistoryUtf8Bytes);
        if (SessionPersistenceState.GetRequired(session).PendingCompaction is not null || history.Messages.Count == 0)
            return history.Messages;

        var operationId = Guid.NewGuid().ToString("N");
        var request = CreateCompactionRequest(session, history, operationId);
        try
        {
            var result = await StartCompactionAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Status == HistoryCompactionStatus.Unchanged)
            {
                HistoryCompactionValidation.ValidateResult(request, DetachResult(result));
                cancellationToken.ThrowIfCancellationRequested();
                return history.Messages;
            }

            if (result.Status != HistoryCompactionStatus.Pending || result.Ticket is not { } ticket
                || ticket.SourceBinding != request.SourceBinding)
                throw new HistoryCompactionValidationException(HistoryCompactionFailureReason.InvalidLifecycle);

            var pending = new PendingHistoryCompaction(options.CompactorKey, ticket, history.Reference,
                history.Messages.Count, history.LastSequence, operationId);
            cancellationToken.ThrowIfCancellationRequested();
            SessionPersistenceState.SetPendingCompaction(session, pending);
            return history.Messages;
        }
        catch (Exception exception) when (IsCompactorFailure(exception))
        {
            return await FallbackAsync(FailureCategory(exception), history.Messages,
                history.Reference, options, cancellationToken).ConfigureAwait(false);
        }
    }

    private HistoryCompactionRequest CreateCompactionRequest(
        AgentSession session, HistoryReadResult history, string operationId)
    {
        var options = _compactionOptions ?? throw new InvalidOperationException();
        var binding = JsonSerializer.Serialize(new { Source = history.Reference, OperationId = operationId });
        return new(SessionPersistenceState.GetRequired(session).AgentId, binding, history.Messages, options);
    }

    private Task<HistoryCompactionResult> StartCompactionAsync(
        HistoryCompactionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HistoryCompactionValidation.ValidateSourceForCompaction(request.Messages);
        var compactor = _compactor ?? throw new InvalidOperationException();
        // Only the plugin gets a deep copy. The provider's repository read is already private.
        var isolatedRequest = new HistoryCompactionRequest(request.AgentId, request.SourceBinding,
            CopyMessages(request.Messages), request.Options);
        return ExecuteCompactorAsync(token => compactor.CompactAsync(isolatedRequest, token), cancellationToken);
    }

    private async Task<IReadOnlyList<ChatMessage>> FallbackAsync(
        string category, IReadOnlyList<ChatMessage> baseline, HistoryReference reference,
        HistoryCompactionOptions options, CancellationToken cancellationToken)
    {
        _logger.LogWarning(CompactionErrors.Get(options.Mode == HistoryCompactionMode.Foreground ? "ValidationLog" : "BackgroundLog"), category);
        cancellationToken.ThrowIfCancellationRequested();
        HistoryCompactionValidation.ValidateFallback(baseline, options.MaxHistoryUtf8Bytes);
        var reread = await _repository.ReadAsync(reference, cancellationToken: cancellationToken).ConfigureAwait(false);
        RestoreProcessedApprovals(reread.Messages);
        HistoryCompactionValidation.ValidateFallback(reread.Messages, options.MaxHistoryUtf8Bytes);
        if (!HistoryCompactionValidation.Equivalent(baseline, reread.Messages))
            throw new HistoryConcurrencyException();
        return baseline;
    }

    private void DiscardIncompatiblePending(AgentSession session)
    {
        var pending = SessionPersistenceState.GetRequired(session).PendingCompaction;
        if (pending is not null && (_compactionOptions?.Mode != HistoryCompactionMode.Background
            || pending.CompactorKey != _compactionOptions.CompactorKey))
        {
            DiscardPendingCompaction(session, "ProfileChanged");
        }
    }

    private async Task<bool> TryApplyBackgroundAsync(
        AgentSession session, PendingHistoryCompaction pending, IReadOnlyList<ChatMessage> newMessages,
        CancellationToken cancellationToken)
    {
        var options = _compactionOptions ?? throw new InvalidOperationException();
        var reference = GetReference(session);
        var result = await TryGetBackgroundResultAsync(session, pending.Ticket, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return false;

        // Only a ready result needs history. Reading after retrieval also observes TTL expiry
        // during that call; revision checks still reject competing writers.
        var history = await _repository.ReadAsync(reference, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!history.HasOriginalPrefix(pending.SourceLastSequence, pending.SourceMessageCount))
        {
            DiscardPendingCompaction(session, "SourceExpiredOrReplaced");
            return false;
        }
        RestoreProcessedApprovals(history.Messages);

        try
        {
            var original = history.Messages.Take(pending.SourceMessageCount).ToArray();
            var request = new HistoryCompactionRequest(SessionPersistenceState.GetRequired(session).AgentId,
                pending.Ticket.SourceBinding, original, options);
            HistoryCompactionValidation.ValidateResult(request, result);
        }
        catch (HistoryCompactionValidationException exception)
        {
            // An invalid final job result will not become valid by polling it again.
            DiscardPendingCompaction(session, FailureCategory(exception));
            cancellationToken.ThrowIfCancellationRequested();
            HistoryCompactionValidation.ValidateFallback(history.Messages, options.MaxHistoryUtf8Bytes);
            return false;
        }

        if (result.Status == HistoryCompactionStatus.Unchanged)
        {
            SessionPersistenceState.SetPendingCompaction(session, null);
            return false;
        }

        IReadOnlyList<ChatMessage> merged;
        try
        {
            merged = MergeBackgroundResult(history.Messages, result.Messages, pending.SourceMessageCount,
                newMessages, options.MaxHistoryUtf8Bytes);
        }
        catch (HistoryCompactionValidationException exception)
        {
            // Only the later/current turn can still have a tool exchange waiting to finish.
            if (exception.Reason == HistoryCompactionFailureReason.UnsafeToolHistory)
                _logger.LogWarning(CompactionErrors.Get("BackgroundLog"), FailureCategory(exception));
            else
                DiscardPendingCompaction(session, FailureCategory(exception));
            cancellationToken.ThrowIfCancellationRequested();
            HistoryCompactionValidation.ValidateFallback(history.Messages, options.MaxHistoryUtf8Bytes);
            return false;
        }

        await PublishHistoryAsync(session, reference, merged, pending.OperationId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<HistoryCompactionResult?> TryGetBackgroundResultAsync(
        AgentSession session, HistoryCompactionTicket ticket, CancellationToken cancellationToken)
    {
        var compactor = _compactor as IBackgroundHistoryCompactor ?? throw new InvalidOperationException();
        try
        {
            var result = await ExecuteCompactorAsync(
                token => compactor.GetResultAsync(ticket, token), cancellationToken).ConfigureAwait(false);
            if (result.Status != HistoryCompactionStatus.Pending)
                return DetachResult(result);

            if (result.Ticket != ticket)
                throw new HistoryCompactionValidationException(HistoryCompactionFailureReason.InvalidLifecycle);

            return null;
        }
        catch (Exception exception) when (IsCompactorFailure(exception))
        {
            if (exception is HttpRequestException or TimeoutException)
                _logger.LogWarning(CompactionErrors.Get("BackgroundLog"), FailureCategory(exception));
            else
                DiscardPendingCompaction(session, FailureCategory(exception));
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static IReadOnlyList<ChatMessage> MergeBackgroundResult(
        IReadOnlyList<ChatMessage> storedMessages, IReadOnlyList<ChatMessage> compactedPrefix,
        int originalCount, IReadOnlyList<ChatMessage> newMessages, long? maxHistoryUtf8Bytes)
    {
        // Stored messages and the detached result are private. Copy only the framework's new
        // messages before restoring approval flags, preserving the caller's mutable objects.
        var currentTurn = CopyMessages(newMessages);
        var source = storedMessages.Concat(currentTurn).ToArray();
        var suffix = storedMessages.Skip(originalCount);
        var merged = compactedPrefix.Concat(suffix).Concat(currentTurn).ToArray();
        RestoreProcessedApprovals(source);
        RestoreProcessedApprovals(merged);
        HistoryCompactionValidation.ValidateCandidate(source, merged, maxHistoryUtf8Bytes);
        return merged;
    }

    private void DiscardPendingCompaction(AgentSession session, string reason)
    {
        _logger.LogWarning(CompactionErrors.Get("BackgroundLog"), reason);
        SessionPersistenceState.SetPendingCompaction(session, null);
    }

    private async Task PublishHistoryAsync(
        AgentSession session, HistoryReference source, IReadOnlyList<ChatMessage> messages,
        string operationId, CancellationToken cancellationToken)
    {
        // Both modes publish outside their fallback handlers: a storage failure cannot safely
        // become an append to the old history after an uncertain publication.
        cancellationToken.ThrowIfCancellationRequested();
        var target = await _repository.RotateAsync(source, messages, MessageTtlSeconds, operationId, cancellationToken).ConfigureAwait(false);
        SessionPersistenceState.SetHistory(session, target);
    }

    private async Task<HistoryCompactionResult> ExecuteCompactorAsync(
        Func<CancellationToken, Task<HistoryCompactionResult>> operation, CancellationToken cancellationToken)
    {
        var duration = _compactionOptions?.Timeout;
        using var timeout = duration.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        timeout?.CancelAfter(duration.GetValueOrDefault());
        var token = timeout?.Token ?? cancellationToken;
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await operation(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(result);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout?.IsCancellationRequested == true)
        {
            throw new TimeoutException(CompactionErrors.Get("OperationTimeout"));
        }
    }

    private static HistoryCompactionResult DetachResult(HistoryCompactionResult result)
    {
        if (result.Status == HistoryCompactionStatus.Pending)
            throw new HistoryCompactionValidationException(HistoryCompactionFailureReason.InvalidLifecycle);
        return new(result.Status, result.SourceBinding,
            CopyMessages(result.Messages), result.BeforeUtf8Bytes, result.AfterUtf8Bytes);
    }

    private static IReadOnlyList<ChatMessage> CopyMessages(IReadOnlyList<ChatMessage> messages) =>
        HistoryCompactionValidation.Detach(HistoryCompactionValidation.Serialize(messages));

    private static bool IsCompactorFailure(Exception exception) =>
        exception is HttpRequestException or TimeoutException
        || exception is InvalidOperationException and not HistoryConcurrencyException and not HistoryPartialWriteException;

    private static string FailureCategory(Exception exception) =>
        exception is HistoryCompactionValidationException validation
            ? $"{validation.Reason}/{exception.GetType().Name}" : exception.GetType().Name;

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

    /// <summary>Counts live messages using the required shared history cursor.</summary>
    public Task<int> GetMessageCountAsync(AgentSession session, CancellationToken cancellationToken = default) =>
        _repository.CountAsync(GetReference(session), cancellationToken);

    /// <summary>Clears live messages, advancing shared state once per committed conditional batch.</summary>
    public async Task<int> ClearMessagesAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        var reference = GetReference(session);
        SessionPersistenceState.SetPreparedCompaction(session, null);
        var result = await _repository.ClearAsync(reference,
            cursor => SessionPersistenceState.SetHistory(session, cursor), cancellationToken).ConfigureAwait(false);
        SessionPersistenceState.SetPendingCompaction(session, null);
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

internal static class HistoryProviderErrors
{
    private static readonly ResourceManager s_resources = new(
        "SharedServices.CosmosChatHistoryProviderResources", typeof(HistoryProviderErrors).Assembly);

    internal static string Get(string name) => s_resources.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException(name);
}
