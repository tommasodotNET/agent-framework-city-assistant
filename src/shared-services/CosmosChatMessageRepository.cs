using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Serialization;

namespace SharedServices;

/// <summary>
/// Internal repository for Cosmos DB chat message CRUD operations.
/// This class handles all direct interactions with Cosmos DB, including batch operations,
/// emulator compatibility, and RU tracking.
/// </summary>
[RequiresUnreferencedCode("The CosmosChatMessageRepository uses JSON serialization which is incompatible with trimming.")]
[RequiresDynamicCode("The CosmosChatMessageRepository uses JSON serialization which is incompatible with NativeAOT.")]
internal sealed class CosmosChatMessageRepository
{
    private readonly Container _container;
    private readonly ILogger _logger;
    private readonly bool _isEmulator;

    /// <summary>
    /// Gets or sets the maximum number of messages to return in a single query batch.
    /// Default is 100 for optimal performance.
    /// </summary>
    public int MaxItemCount { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of items per transactional batch operation.
    /// Default is 100, maximum allowed by Cosmos DB is 100.
    /// Note: This setting is ignored when connected to the emulator.
    /// </summary>
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatMessageRepository"/> class.
    /// </summary>
    /// <param name="container">The Cosmos DB container.</param>
    /// <param name="isEmulator">Whether connected to the Cosmos DB emulator.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public CosmosChatMessageRepository(Container container, bool isEmulator, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
        _isEmulator = isEmulator;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Retrieves chat message documents from Cosmos DB.
    /// </summary>
    /// <param name="conversationId">The conversation ID to query.</param>
    /// <param name="partitionKey">The partition key for the query.</param>
    /// <param name="maxMessages">Optional maximum number of messages to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of message documents and the total RU consumed.</returns>
    public async Task<(List<CosmosMessageDocument> Documents, double RequestCharge)> GetMessagesAsync(
        string conversationId,
        PartitionKey partitionKey,
        int? maxMessages = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Querying messages for conversation {ConversationId}", conversationId);

        var orderDirection = maxMessages.HasValue ? "DESC" : "ASC";
        var query = new QueryDefinition($"SELECT * FROM c WHERE c.conversationId = @conversationId AND c.type = @type ORDER BY c.timestamp {orderDirection}")
            .WithParameter("@conversationId", conversationId)
            .WithParameter("@type", "ChatMessage");

        var iterator = _container.GetItemQueryIterator<CosmosMessageDocument>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = partitionKey,
            MaxItemCount = MaxItemCount
        });

        var documents = new List<CosmosMessageDocument>();
        var totalRu = 0.0;

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            totalRu += response.RequestCharge;

            foreach (var document in response)
            {
                if (maxMessages.HasValue && documents.Count >= maxMessages.Value)
                    break;

                documents.Add(document);
            }

            if (maxMessages.HasValue && documents.Count >= maxMessages.Value)
                break;
        }

        // If we fetched in descending order (most recent first), reverse to ascending order
        if (maxMessages.HasValue)
            documents.Reverse();

        _logger.LogDebug("Retrieved {DocumentCount} documents for conversation {ConversationId}, RU: {RequestCharge:F2}",
            documents.Count, conversationId, totalRu);

        return (documents, totalRu);
    }

    /// <summary>
    /// Stores chat message documents to Cosmos DB.
    /// Uses transactional batch for Azure Cosmos DB or sequential operations for the emulator.
    /// </summary>
    /// <param name="documents">The documents to store.</param>
    /// <param name="partitionKey">The partition key for the documents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total RU consumed.</returns>
    public async Task<double> StoreDocumentsAsync(
        IList<CosmosMessageDocument> documents,
        PartitionKey partitionKey,
        CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0)
        {
            _logger.LogDebug("No documents to store");
            return 0.0;
        }

        _logger.LogDebug("Storing {DocumentCount} documents", documents.Count);

        double totalRu;
        if (_isEmulator)
        {
            totalRu = await AddDocumentsSequentiallyAsync(documents, partitionKey, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            totalRu = await AddDocumentsWithBatchAsync(documents, partitionKey, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("Successfully stored {DocumentCount} documents, RU: {RequestCharge:F2}", documents.Count, totalRu);
        return totalRu;
    }

    /// <summary>
    /// Deletes all chat messages for a conversation.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="partitionKey">The partition key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted messages and total RU consumed.</returns>
    public async Task<(int DeletedCount, double RequestCharge)> DeleteMessagesAsync(
        string conversationId,
        PartitionKey partitionKey,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting all messages for conversation {ConversationId}", conversationId);

        var query = new QueryDefinition("SELECT VALUE c.id FROM c WHERE c.conversationId = @conversationId AND c.type = @type")
            .WithParameter("@conversationId", conversationId)
            .WithParameter("@type", "ChatMessage");

        var iterator = _container.GetItemQueryIterator<string>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = partitionKey,
            MaxItemCount = MaxItemCount
        });

        var deletedCount = 0;
        var totalRu = 0.0;

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            totalRu += response.RequestCharge;
            var itemIds = response.Where(id => !string.IsNullOrEmpty(id)).ToList();

            if (_isEmulator)
            {
                foreach (var itemId in itemIds)
                {
                    var deleteResponse = await _container.DeleteItemAsync<object>(itemId, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                    totalRu += deleteResponse.RequestCharge;
                    deletedCount++;
                }
            }
            else if (itemIds.Count > 0)
            {
                var batch = _container.CreateTransactionalBatch(partitionKey);
                foreach (var itemId in itemIds)
                    batch.DeleteItem(itemId);

                var batchResponse = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                totalRu += batchResponse.RequestCharge;
                deletedCount += itemIds.Count;
            }
        }

        _logger.LogDebug("Deleted {DeletedCount} messages for conversation {ConversationId}, RU: {RequestCharge:F2}",
            deletedCount, conversationId, totalRu);

        return (deletedCount, totalRu);
    }

    /// <summary>
    /// Gets the count of messages in a conversation.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="partitionKey">The partition key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The message count and RU consumed.</returns>
    public async Task<(int Count, double RequestCharge)> GetMessageCountAsync(
        string conversationId,
        PartitionKey partitionKey,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.conversationId = @conversationId AND c.type = @type")
            .WithParameter("@conversationId", conversationId)
            .WithParameter("@type", "ChatMessage");

        var iterator = _container.GetItemQueryIterator<int>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = partitionKey
        });

        var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        var count = response.FirstOrDefault();

        _logger.LogDebug("Message count for conversation {ConversationId}: {Count}, RU: {RequestCharge:F2}",
            conversationId, count, response.RequestCharge);

        return (count, response.RequestCharge);
    }

    /// <summary>
    /// Copies documents to a new conversation ID (for archiving).
    /// </summary>
    /// <param name="documents">The documents to copy.</param>
    /// <param name="targetConversationId">The target conversation ID.</param>
    /// <param name="targetPartitionKey">The target partition key.</param>
    /// <param name="tenantId">Optional tenant ID for hierarchical partitioning.</param>
    /// <param name="userId">Optional user ID for hierarchical partitioning.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total RU consumed.</returns>
    public async Task<double> CopyDocumentsToConversationAsync(
        IList<CosmosMessageDocument> documents,
        string targetConversationId,
        PartitionKey targetPartitionKey,
        string? tenantId = null,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0)
        {
            _logger.LogDebug("No documents to copy");
            return 0.0;
        }

        _logger.LogDebug("Copying {DocumentCount} documents to conversation {TargetConversationId}",
            documents.Count, targetConversationId);

        var totalRu = 0.0;
        var useHierarchicalPartitioning = tenantId is not null && userId is not null;

        // Note: We can't use transactional batch across different partition keys
        foreach (var doc in documents)
        {
            var archivedDoc = new CosmosMessageDocument
            {
                Id = Guid.NewGuid().ToString(),
                ConversationId = targetConversationId,
                Timestamp = doc.Timestamp,
                MessageId = doc.MessageId,
                Role = doc.Role,
                Message = doc.Message,
                Type = doc.Type,
                Ttl = null, // Archived messages don't expire
                TenantId = useHierarchicalPartitioning ? tenantId : null,
                UserId = useHierarchicalPartitioning ? userId : null,
                SessionId = useHierarchicalPartitioning ? targetConversationId : null
            };

            var createResponse = await _container.CreateItemAsync(archivedDoc, targetPartitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            totalRu += createResponse.RequestCharge;
        }

        _logger.LogDebug("Copied {DocumentCount} documents to {TargetConversationId}, RU: {RequestCharge:F2}",
            documents.Count, targetConversationId, totalRu);

        return totalRu;
    }

    #region Private Helpers

    private async Task<double> AddDocumentsSequentiallyAsync(
        IList<CosmosMessageDocument> documents,
        PartitionKey partitionKey,
        CancellationToken cancellationToken)
    {
        var totalRu = 0.0;

        foreach (var document in documents)
        {
            try
            {
                var response = await _container.CreateItemAsync(document, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                totalRu += response.RequestCharge;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                _logger.LogError(ex, "Document exceeds 2MB limit, DocumentId: {DocumentId}", document.Id);
                throw new InvalidOperationException(
                    $"Document exceeds Cosmos DB's maximum item size limit of 2MB. Document ID: {document.Id}", ex);
            }
        }

        _logger.LogDebug("Added {DocumentCount} documents sequentially, RU: {RequestCharge:F2}",
            documents.Count, totalRu);

        return totalRu;
    }

    private async Task<double> AddDocumentsWithBatchAsync(
        IList<CosmosMessageDocument> documents,
        PartitionKey partitionKey,
        CancellationToken cancellationToken,
        int startIndex = 0,
        int? count = null)
    {
        var totalRu = 0.0;
        var itemsToProcess = count ?? documents.Count - startIndex;
        if (itemsToProcess <= 0) return totalRu;

        for (var i = startIndex; i < startIndex + itemsToProcess; i += MaxBatchSize)
        {
            var chunkSize = Math.Min(MaxBatchSize, startIndex + itemsToProcess - i);
            var chunk = documents.Skip(i).Take(chunkSize).ToList();

            var batch = _container.CreateTransactionalBatch(partitionKey);
            foreach (var doc in chunk)
                batch.CreateItem(doc);

            try
            {
                var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Batch operation failed: {StatusCode} - {ErrorMessage}",
                        response.StatusCode, response.ErrorMessage);
                    throw new InvalidOperationException($"Batch operation failed: {response.StatusCode} - {response.ErrorMessage}");
                }

                totalRu += response.RequestCharge;
                _logger.LogDebug("Batch added {DocumentCount} documents, RU: {RequestCharge:F2}",
                    chunk.Count, response.RequestCharge);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                _logger.LogWarning("Batch too large, splitting: {DocumentCount} documents", chunk.Count);

                if (chunk.Count == 1)
                {
                    totalRu += await AddDocumentsSequentiallyAsync(chunk, partitionKey, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var mid = chunk.Count / 2;
                totalRu += await AddDocumentsWithBatchAsync(documents, partitionKey, cancellationToken, i, mid).ConfigureAwait(false);
                totalRu += await AddDocumentsWithBatchAsync(documents, partitionKey, cancellationToken, i + mid, chunk.Count - mid).ConfigureAwait(false);

                i += chunkSize - MaxBatchSize;
            }
        }

        return totalRu;
    }

    #endregion
}

/// <summary>
/// Represents a chat message document stored in Cosmos DB.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Deserialized by Cosmos DB")]
internal sealed class CosmosMessageDocument
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("conversationId")] public string ConversationId { get; set; } = string.Empty;
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    [JsonPropertyName("messageId")] public string? MessageId { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("ttl")] public int? Ttl { get; set; }
    [JsonPropertyName("tenantId")] public string? TenantId { get; set; }
    [JsonPropertyName("userId")] public string? UserId { get; set; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
}
