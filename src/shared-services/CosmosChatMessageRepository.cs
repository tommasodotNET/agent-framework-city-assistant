using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharedServices;

/// <summary>
/// Shared text/voice history persistence. Every operation uses a full HPK and an expected revision.
/// Conditional batches are mandatory. Multiple batches and session snapshots are not one transaction.
/// </summary>
public sealed class CosmosChatMessageRepository
{
    // Leave wire-operation overhead below Cosmos's 2 MB batch limit.
    private const int PayloadBudget = 1_800_000;
    private const int OperationOverhead = 1024;
    private const int HeadBudget = 65_536;
    private static readonly ConditionalWeakTable<Container, SchemaGate> s_schemaGates = new();
    private readonly Container _container;
    private readonly ILogger _logger;
    private int _maxItemCount = 100;
    private int _maxBatchSize = 100;

    /// <summary>Uses an existing container without owning its client.</summary>
    public CosmosChatMessageRepository(Container container, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Uses an existing Cosmos client without owning its lifecycle.</summary>
    public CosmosChatMessageRepository(CosmosClient cosmosClient, string databaseId, string containerId, ILogger? logger = null)
        : this(GetContainer(cosmosClient, databaseId, containerId), logger) { }

    /// <summary>Query page size, positive; independent of the total read limit.</summary>
    public int MaxItemCount
    {
        get => _maxItemCount;
        set => _maxItemCount = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), HistoryErrors.Get("PositiveLimit"));
    }

    /// <summary>Total operations per batch, including one reserved metadata operation (2..100).</summary>
    public int MaxBatchSize
    {
        get => _maxBatchSize;
        set => _maxBatchSize = value is >= 2 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(value), HistoryErrors.Get("BatchLimit"));
    }

    /// <summary>Reads ordered, typed messages only after checking the expected history revision.</summary>
    public async Task<HistoryReadResult> ReadAsync(HistoryReference reference, int? maxMessages = null, CancellationToken cancellationToken = default)
    {
        var documents = await ReadDocumentsAsync(reference, maxMessages, cancellationToken).ConfigureAwait(false);
        return new(reference, documents.Select(document => document.ToChatMessage()).ToArray());
    }

    /// <summary>
    /// Appends typed messages and advances the head with create-only/IfMatch in the same batch.
    /// onCommitted is called exactly once per successful chunk so adapters can advance their cursor.
    /// Empty input checks the revision but does not create metadata or advance a cursor.
    /// </summary>
    public async Task<HistoryWriteResult> AppendAsync(
        HistoryReference reference, IReadOnlyList<ChatMessage> messages, int? messageTtlSeconds = 86400,
        Action<HistoryReference>? onCommitted = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(messages);
        var ttl = ValidateTtl(messageTtlSeconds);
        var address = reference.ToAddress();
        var head = await ReadHeadAsync(reference, cancellationToken).ConfigureAwait(false);
        var documents = messages.Select((message, index) =>
        {
            ArgumentNullException.ThrowIfNull(message);
            return new HistoryMessageDocument
            {
                ScopeKey = address.ScopeKey, ConversationId = address.ConversationId,
                Sequence = checked(head.Document.NextSequence + index),
                Timestamp = DateTimeOffset.UtcNow,
                Message = JsonSerializer.SerializeToElement(message, HistoryJson.Options), Ttl = ttl
            };
        }).ToArray();
        // Validate the complete request before the first commit, including oversized single messages.
        var chunks = Chunk(documents);
        return await CommitChunksAsync(reference, chunks, delete: false, onCommitted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts only messages in this full history partition, verifying the revision before and after.</summary>
    public async Task<int> CountAsync(HistoryReference reference, CancellationToken cancellationToken = default)
    {
        await ReadHeadAsync(reference, cancellationToken).ConfigureAwait(false);
        var query = MessageQuery(reference, "SELECT VALUE COUNT(1) FROM c");
        using var iterator = _container.GetItemQueryStreamIterator(query, requestOptions: QueryOptions(reference));
        var count = 0;
        while (iterator.HasMoreResults)
        {
            using var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
            count = checked(count + json.RootElement.GetProperty("Documents").EnumerateArray().Sum(value => value.GetInt32()));
        }
        await ReadHeadAsync(reference, cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>
    /// Deletes existing messages through conditional batches, retaining the monotonic sequence/head.
    /// Partial failure exposes the committed cursor/count rather than reporting a completed clear.
    /// </summary>
    public async Task<HistoryWriteResult> ClearAsync(
        HistoryReference reference, Action<HistoryReference>? onCommitted = null, CancellationToken cancellationToken = default)
    {
        var documents = await ReadDocumentsAsync(reference, null, cancellationToken).ConfigureAwait(false);
        return await CommitChunksAsync(reference, Chunk(documents), delete: true, onCommitted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the source into an explicitly supplied, new history with permanent message/head TTL.
    /// Does not delete the source; this is not an atomic cross-partition move.
    /// </summary>
    public async Task<HistoryWriteResult> ArchiveAsync(
        HistoryReference source, HistoryStorageAddress target, Action<HistoryReference>? onCommitted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.ToAddress() == target || source.ScopeKey != target.ScopeKey)
            throw new ArgumentException(StorageErrors.Get("IncompatibleContext"), nameof(target));
        var messages = await ReadAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await AppendAsync(new(target.ScopeKey, target.ConversationId, 0), messages.Messages, -1, onCommitted, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<HistoryMessageDocument>> ReadDocumentsAsync(
        HistoryReference reference, int? maxMessages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (maxMessages is <= 0) throw new ArgumentOutOfRangeException(nameof(maxMessages), HistoryErrors.Get("PositiveLimit"));
        await ReadHeadAsync(reference, cancellationToken).ConfigureAwait(false);
        var select = maxMessages.HasValue ? "SELECT TOP @limit * FROM c" : "SELECT * FROM c";
        var query = MessageQuery(reference, select, maxMessages.HasValue ? " ORDER BY c.sequence DESC" : " ORDER BY c.sequence ASC");
        if (maxMessages.HasValue) query.WithParameter("@limit", maxMessages.Value);
        using var iterator = _container.GetItemQueryStreamIterator(query, requestOptions: QueryOptions(reference));
        var documents = new List<HistoryMessageDocument>();
        while (iterator.HasMoreResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var item in json.RootElement.GetProperty("Documents").EnumerateArray())
            {
                var document = item.Deserialize<HistoryMessageDocument>(HistoryJson.Options)
                    ?? throw new InvalidOperationException(StorageErrors.Get("InvalidSchema"));
                document.ValidateFor(reference.ToAddress());
                documents.Add(document);
            }
        }
        // This also detects writes racing the query rather than returning mixed revisions.
        await ReadHeadAsync(reference, cancellationToken).ConfigureAwait(false);
        return documents.OrderBy(document => document.Sequence).ThenBy(document => document.Id, StringComparer.Ordinal).ToList();
    }

    private async Task<HistoryWriteResult> CommitChunksAsync(
        HistoryReference reference, IReadOnlyList<IReadOnlyList<HistoryMessageDocument>> chunks,
        bool delete, Action<HistoryReference>? onCommitted, CancellationToken cancellationToken)
    {
        var current = reference;
        var committed = 0;
        // An empty operation must still reject a stale snapshot.
        if (chunks.Count == 0) await ReadHeadAsync(current, cancellationToken).ConfigureAwait(false);
        foreach (var chunk in chunks)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var head = await ReadHeadAsync(current, cancellationToken).ConfigureAwait(false);
                var next = head.Document with
                {
                    Revision = checked(current.Revision + 1),
                    NextSequence = delete ? head.Document.NextSequence : checked(chunk[^1].Sequence + 1),
                    // Snapshot saves can occur arbitrarily later without a history append. A finite
                    // head TTL therefore cannot safely cover all snapshots, even with a grace period.
                    Ttl = -1
                };
                var batch = _container.CreateTransactionalBatch(current.ToAddress().ToPartitionKey());
                var streams = new List<MemoryStream>();
                try
                {
                    foreach (var document in chunk)
                    {
                        if (delete) batch.DeleteItem(document.Id);
                        else
                        {
                            var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document, HistoryJson.Options), writable: false);
                            streams.Add(stream);
                            batch.CreateItemStream(stream);
                        }
                    }
                    var headStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(next, HistoryJson.Options), writable: false);
                    streams.Add(headStream);
                    if (head.ETag is null) batch.CreateItemStream(headStream);
                    else batch.ReplaceItemStream(HistoryHeadDocument.DocumentId, headStream,
                        new TransactionalBatchItemRequestOptions { IfMatchEtag = head.ETag });
                    using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError("Conditional history batch failed with status {StatusCode}", response.StatusCode);
                        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
                            || Enumerable.Range(0, response.Count).Any(index => response[index].StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed))
                            throw new HistoryConcurrencyException();
                        throw new InvalidOperationException(HistoryErrors.Get("BatchFailed"));
                    }
                }
                finally
                {
                    foreach (var stream in streams) stream.Dispose();
                }
                current = current.WithRevision(next.Revision);
                committed += chunk.Count;
                onCommitted?.Invoke(current);
                _logger.LogDebug("Committed history batch with {MessageCount} messages at revision {Revision}", chunk.Count, current.Revision);
            }
            catch (OperationCanceledException exception) when (committed > 0)
            {
                throw new HistoryWriteCanceledException(current, committed, exception);
            }
            catch (CosmosException exception) when (committed > 0)
            {
                throw new HistoryPartialWriteException(current, committed, exception);
            }
            catch (CosmosException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                throw new HistoryConcurrencyException();
            }
            catch (InvalidOperationException exception) when (committed > 0)
            {
                throw new HistoryPartialWriteException(current, committed, exception);
            }
            catch (JsonException exception) when (committed > 0)
            {
                throw new HistoryPartialWriteException(current, committed, exception);
            }
            catch (NotSupportedException exception) when (committed > 0)
            {
                throw new HistoryPartialWriteException(current, committed, exception);
            }
            catch (OverflowException exception) when (committed > 0)
            {
                throw new HistoryPartialWriteException(current, committed, exception);
            }
        }
        return new(current, committed);
    }

    private List<IReadOnlyList<HistoryMessageDocument>> Chunk(IReadOnlyList<HistoryMessageDocument> documents)
    {
        var chunks = new List<IReadOnlyList<HistoryMessageDocument>>();
        var chunk = new List<HistoryMessageDocument>();
        // Reserve the largest possible head and its wire overhead.
        var bytes = HeadBudget;
        foreach (var document in documents)
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(document, HistoryJson.Options).Length + OperationOverhead;
            if (size + HeadBudget > PayloadBudget) throw new InvalidOperationException(HistoryErrors.Get("TooLarge"));
            if (chunk.Count >= MaxBatchSize - 1 || bytes + size > PayloadBudget)
            {
                chunks.Add(chunk);
                chunk = [];
                bytes = HeadBudget;
            }
            chunk.Add(document);
            bytes += size;
        }
        if (chunk.Count > 0) chunks.Add(chunk);
        return chunks;
    }

    private async Task<(HistoryHeadDocument Document, string? ETag)> ReadHeadAsync(HistoryReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await ValidateSchemaAsync(cancellationToken).ConfigureAwait(false);
        var address = reference.ToAddress();
        using var response = await _container.ReadItemStreamAsync(
            HistoryHeadDocument.DocumentId, address.ToPartitionKey(), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (reference.Revision != 0) throw new InvalidOperationException(HistoryErrors.Get("MissingHead"));
            return (new() { ScopeKey = address.ScopeKey, ConversationId = address.ConversationId }, null);
        }
        response.EnsureSuccessStatusCode();
        var head = await JsonSerializer.DeserializeAsync<HistoryHeadDocument>(response.Content, HistoryJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(StorageErrors.Get("InvalidSchema"));
        if (head.SchemaVersion != StorageSchema.Version || head.Id != HistoryHeadDocument.DocumentId || head.Type != "HistoryHead"
            || head.ScopeKey != address.ScopeKey || head.ConversationId != address.ConversationId
            || head.Revision < 0 || head.NextSequence < 0 || (head.Ttl != -1 && head.Ttl <= 0))
            throw new InvalidOperationException(StorageErrors.Get("InvalidSchema"));
        if (head.Revision != reference.Revision) throw new HistoryConcurrencyException();
        if (string.IsNullOrWhiteSpace(response.Headers.ETag)) throw new InvalidOperationException(StorageErrors.Get("InvalidSchema"));
        return (head, response.Headers.ETag);
    }

    private async Task ValidateSchemaAsync(CancellationToken cancellationToken)
    {
        var gate = s_schemaGates.GetValue(_container, _ => new());
        await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (gate.Validated) return;
            var response = await _container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            StorageSchema.ValidateContainer(response.Resource, isSessionContainer: false);
            gate.Validated = true;
        }
        finally { gate.Semaphore.Release(); }
    }

    private static int ValidateTtl(int? ttl) => ttl is null or -1 ? -1
        : ttl > 0 ? ttl.Value : throw new ArgumentOutOfRangeException(nameof(ttl), StorageErrors.Get("InvalidTtl"));

    private QueryRequestOptions QueryOptions(HistoryReference reference) => new()
    {
        PartitionKey = reference.ToAddress().ToPartitionKey(), MaxItemCount = MaxItemCount
    };

    private static QueryDefinition MessageQuery(HistoryReference reference, string select, string ordering = "") =>
        new QueryDefinition(select + " WHERE c.scopeKey = @scopeKey AND c.conversationId = @conversationId AND c.type = @type" + ordering)
            .WithParameter("@scopeKey", reference.ScopeKey).WithParameter("@conversationId", reference.ConversationId)
            .WithParameter("@type", "ChatMessage");

    private static Container GetContainer(CosmosClient client, string databaseId, string containerId)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        return client.GetContainer(databaseId, containerId);
    }

    private sealed class SchemaGate
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);
        internal bool Validated { get; set; }
    }
}
