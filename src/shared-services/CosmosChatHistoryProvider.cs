using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharedServices;

/// <summary>
/// Provides a Cosmos DB implementation of the <see cref="ChatHistoryProvider"/> abstract class.
/// Automatically detects whether connected to the Cosmos DB Emulator and adjusts behavior accordingly.
/// When connected to the emulator, transactional batch operations are avoided as they are not fully supported.
/// </summary>
[RequiresUnreferencedCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with trimming.")]
[RequiresDynamicCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with NativeAOT.")]
public sealed class CosmosChatHistoryProvider : ChatHistoryProvider, IDisposable
{
    private readonly CosmosClient _cosmosClient;
    private readonly Container _container;
    private readonly bool _ownsClient;
    private readonly ILogger _logger;
    private bool _disposed;

    // Hierarchical partition key support
    private readonly string? _tenantId;
    private readonly string? _userId;
    private readonly PartitionKey _partitionKey;
    private readonly bool _useHierarchicalPartitioning;

    /// <summary>
    /// Indicates whether this provider is connected to the Cosmos DB Emulator.
    /// When true, certain features like transactional batch are disabled.
    /// </summary>
    private readonly bool _isEmulator;

    /// <summary>
    /// Cached JSON serializer options for .NET 9.0 compatibility.
    /// </summary>
    private static readonly JsonSerializerOptions s_defaultJsonOptions = CreateDefaultJsonOptions();

    private static JsonSerializerOptions CreateDefaultJsonOptions()
    {
        var options = new JsonSerializerOptions();
#if NET9_0_OR_GREATER
        // Configure TypeInfoResolver for .NET 9.0 to enable JSON serialization
        options.TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
#endif
        return options;
    }

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
    /// Gets or sets the maximum number of messages to retrieve from the provider.
    /// This helps prevent exceeding LLM context windows in long conversations.
    /// Default is null (no limit). When set, only the most recent messages are returned.
    /// </summary>
    public int? MaxMessagesToRetrieve { get; set; }

    /// <summary>
    /// Gets or sets the Time-To-Live (TTL) in seconds for messages.
    /// Default is 86400 seconds (24 hours). Set to null to disable TTL.
    /// </summary>
    public int? MessageTtlSeconds { get; set; } = 86400;

    /// <summary>
    /// Gets the conversation ID associated with this provider.
    /// </summary>
    public string ConversationId { get; init; }

    /// <summary>
    /// Gets the database ID associated with this provider.
    /// </summary>
    public string DatabaseId { get; init; }

    /// <summary>
    /// Gets the container ID associated with this provider.
    /// </summary>
    public string ContainerId { get; init; }

    /// <summary>
    /// Gets whether this provider is connected to the Cosmos DB Emulator.
    /// </summary>
    public bool IsEmulator => _isEmulator;

    /// <summary>
    /// Determines if the given CosmosClient is connected to the local emulator.
    /// </summary>
    /// <param name="cosmosClient">The CosmosClient to check.</param>
    /// <returns>True if connected to the emulator, false otherwise.</returns>
    private static bool DetectEmulator(CosmosClient cosmosClient)
    {
        var host = cosmosClient.Endpoint?.Host;
        if (string.IsNullOrEmpty(host)) return false;

        // Emulator typically runs on localhost, 127.x.x.x, or host.docker.internal
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("127.", StringComparison.OrdinalIgnoreCase)
            || host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Internal primary constructor used by all public constructors.
    /// </summary>
    /// <param name="cosmosClient">The <see cref="CosmosClient"/> instance to use for Cosmos DB operations.</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="conversationId">The unique identifier for this conversation thread.</param>
    /// <param name="ownsClient">Whether this instance owns the CosmosClient and should dispose it.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="tenantId">Optional tenant identifier for hierarchical partitioning.</param>
    /// <param name="userId">Optional user identifier for hierarchical partitioning.</param>
    internal CosmosChatHistoryProvider(CosmosClient cosmosClient, string databaseId, string containerId, string conversationId, bool ownsClient, ILogger<CosmosChatHistoryProvider>? logger = null, string? tenantId = null, string? userId = null)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(conversationId);

        _cosmosClient = cosmosClient;
        _container = cosmosClient.GetContainer(databaseId, containerId);
        _ownsClient = ownsClient;
        _isEmulator = DetectEmulator(cosmosClient);
        _tenantId = tenantId;
        _userId = userId;
        _useHierarchicalPartitioning = tenantId is not null && userId is not null;
        _logger = logger ?? NullLogger<CosmosChatHistoryProvider>.Instance;

        ConversationId = conversationId;
        DatabaseId = databaseId;
        ContainerId = containerId;

        _partitionKey = _useHierarchicalPartitioning
            ? new PartitionKeyBuilder().Add(tenantId!).Add(userId!).Add(conversationId).Build()
            : new PartitionKey(conversationId);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class using a connection string.
    /// </summary>
    /// <param name="connectionString">The Cosmos DB connection string.</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(string connectionString, string databaseId, string containerId, ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(connectionString, databaseId, containerId, Guid.NewGuid().ToString("N"), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class using a connection string.
    /// </summary>
    /// <param name="connectionString">The Cosmos DB connection string.</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="conversationId">The unique identifier for this conversation thread.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(string connectionString, string databaseId, string containerId, string conversationId, ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(new CosmosClient(connectionString), databaseId, containerId, conversationId, ownsClient: true, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class using TokenCredential for authentication.
    /// </summary>
    /// <param name="accountEndpoint">The Cosmos DB account endpoint URI.</param>
    /// <param name="tokenCredential">The TokenCredential to use for authentication (e.g., DefaultAzureCredential, ManagedIdentityCredential).</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(string accountEndpoint, TokenCredential tokenCredential, string databaseId, string containerId, ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(accountEndpoint, tokenCredential, databaseId, containerId, Guid.NewGuid().ToString("N"), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class using a TokenCredential for authentication.
    /// </summary>
    /// <param name="accountEndpoint">The Cosmos DB account endpoint URI.</param>
    /// <param name="tokenCredential">The TokenCredential to use for authentication (e.g., DefaultAzureCredential, ManagedIdentityCredential).</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="conversationId">The unique identifier for this conversation thread.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(string accountEndpoint, TokenCredential tokenCredential, string databaseId, string containerId, string conversationId, ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(new CosmosClient(accountEndpoint, tokenCredential), databaseId, containerId, conversationId, ownsClient: true, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class using an existing <see cref="CosmosClient"/>.
    /// </summary>
    /// <param name="cosmosClient">The <see cref="CosmosClient"/> instance to use for Cosmos DB operations.</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cosmosClient"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(CosmosClient cosmosClient, string databaseId, string containerId, ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(cosmosClient, databaseId, containerId, Guid.NewGuid().ToString("N"), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class using an existing <see cref="CosmosClient"/>.
    /// </summary>
    /// <param name="cosmosClient">The <see cref="CosmosClient"/> instance to use for Cosmos DB operations.</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="conversationId">The unique identifier for this conversation thread.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cosmosClient"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(CosmosClient cosmosClient, string databaseId, string containerId, string conversationId, ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(cosmosClient, databaseId, containerId, conversationId, ownsClient: false, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class using a connection string with hierarchical partition keys.
    /// </summary>
    /// <param name="connectionString">The Cosmos DB connection string.</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="tenantId">The tenant identifier for hierarchical partitioning.</param>
    /// <param name="userId">The user identifier for hierarchical partitioning.</param>
    /// <param name="sessionId">The session identifier for hierarchical partitioning.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(string connectionString, string databaseId, string containerId, string tenantId, string userId, string sessionId, ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(new CosmosClient(connectionString), databaseId, containerId, sessionId, ownsClient: true, logger, tenantId, userId)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class using a TokenCredential for authentication with hierarchical partition keys.
    /// </summary>
    /// <param name="accountEndpoint">The Cosmos DB account endpoint URI.</param>
    /// <param name="tokenCredential">The TokenCredential to use for authentication (e.g., DefaultAzureCredential, ManagedIdentityCredential).</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="tenantId">The tenant identifier for hierarchical partitioning.</param>
    /// <param name="userId">The user identifier for hierarchical partitioning.</param>
    /// <param name="sessionId">The session identifier for hierarchical partitioning.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(string accountEndpoint, TokenCredential tokenCredential, string databaseId, string containerId, string tenantId, string userId, string sessionId, ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(new CosmosClient(accountEndpoint, tokenCredential), databaseId, containerId, sessionId, ownsClient: true, logger, tenantId, userId)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class using an existing <see cref="CosmosClient"/> with hierarchical partition keys.
    /// </summary>
    /// <param name="cosmosClient">The <see cref="CosmosClient"/> instance to use for Cosmos DB operations.</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="tenantId">The tenant identifier for hierarchical partitioning.</param>
    /// <param name="userId">The user identifier for hierarchical partitioning.</param>
    /// <param name="sessionId">The session identifier for hierarchical partitioning.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cosmosClient"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(CosmosClient cosmosClient, string databaseId, string containerId, string tenantId, string userId, string sessionId, ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(cosmosClient, databaseId, containerId, sessionId, ownsClient: false, logger, tenantId, userId)
    {
    }

    /// <summary>
    /// Creates a new instance of the <see cref="CosmosChatHistoryProvider"/> class from previously serialized state.
    /// </summary>
    /// <param name="cosmosClient">The <see cref="CosmosClient"/> instance to use for Cosmos DB operations.</param>
    /// <param name="serializedState">A <see cref="JsonElement"/> representing the serialized state of the provider.</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="jsonSerializerOptions">Optional settings for customizing the JSON deserialization process.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>A new instance of <see cref="CosmosChatHistoryProvider"/> initialized from the serialized state.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cosmosClient"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the serialized state cannot be deserialized.</exception>
    public static CosmosChatHistoryProvider CreateFromSerializedState(CosmosClient cosmosClient, JsonElement serializedState, string databaseId, string containerId, JsonSerializerOptions? jsonSerializerOptions = null, ILogger<CosmosChatHistoryProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient, nameof(cosmosClient));
        ArgumentNullException.ThrowIfNullOrWhiteSpace(databaseId, nameof(databaseId));
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerId, nameof(containerId));

        if (serializedState.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("Invalid serialized state", nameof(serializedState));
        }

        var state = serializedState.Deserialize<State>(jsonSerializerOptions);
        if (state?.ConversationIdentifier is not { } conversationId)
        {
            throw new ArgumentException("Invalid serialized state", nameof(serializedState));
        }

        // Use the internal constructor with all parameters to ensure partition key logic is centralized
        return state.UseHierarchicalPartitioning && state.TenantId != null && state.UserId != null
            ? new CosmosChatHistoryProvider(cosmosClient, databaseId, containerId, conversationId, ownsClient: false, logger, state.TenantId, state.UserId)
            : new CosmosChatHistoryProvider(cosmosClient, databaseId, containerId, conversationId, ownsClient: false, logger);
    }

    /// <inheritdoc />
    public override async ValueTask<IEnumerable<ChatMessage>> InvokingAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogDebug("Retrieving chat history for conversation {ConversationId}", ConversationId);

        // Fetch most recent messages in descending order when limit is set, then reverse to ascending
        var orderDirection = MaxMessagesToRetrieve.HasValue ? "DESC" : "ASC";
        var query = new QueryDefinition($"SELECT * FROM c WHERE c.conversationId = @conversationId AND c.type = @type ORDER BY c.timestamp {orderDirection}")
            .WithParameter("@conversationId", ConversationId)
            .WithParameter("@type", "ChatMessage");

        var iterator = _container.GetItemQueryIterator<CosmosMessageDocument>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = _partitionKey,
            MaxItemCount = MaxItemCount
        });

        var messages = new List<ChatMessage>();
        var totalRu = 0.0;

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            totalRu += response.RequestCharge;

            foreach (var document in response)
            {
                if (MaxMessagesToRetrieve.HasValue && messages.Count >= MaxMessagesToRetrieve.Value)
                    break;

                if (string.IsNullOrEmpty(document.Message)) continue;

                if (JsonSerializer.Deserialize<ChatMessage>(document.Message, s_defaultJsonOptions) is { } message)
                    messages.Add(message);
            }

            if (MaxMessagesToRetrieve.HasValue && messages.Count >= MaxMessagesToRetrieve.Value)
                break;
        }

        // If we fetched in descending order (most recent first), reverse to ascending order
        if (MaxMessagesToRetrieve.HasValue)
            messages.Reverse();

        _logger.LogDebug("Retrieved {MessageCount} messages for conversation {ConversationId}, RU: {RequestCharge:F2}", 
            messages.Count, ConversationId, totalRu);

        return messages;
    }

    /// <inheritdoc />
    public override async ValueTask InvokedAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Do not store messages if there was an exception during invocation
        if (context.InvokeException is not null)
        {
            _logger.LogDebug("Skipping message storage due to invocation exception for conversation {ConversationId}", ConversationId);
            return;
        }

        var messageList = context.RequestMessages
            .Concat(context.AIContextProviderMessages ?? [])
            .Concat(context.ResponseMessages ?? [])
            .ToList();

        if (messageList.Count == 0)
        {
            _logger.LogDebug("No messages to store for conversation {ConversationId}", ConversationId);
            return;
        }

        _logger.LogDebug("Storing {MessageCount} messages for conversation {ConversationId}", messageList.Count, ConversationId);

        // Emulator: sequential operations (no transactional batch support)
        // Azure: transactional batch for atomicity
        if (_isEmulator)
        {
            await AddMessagesSequentiallyAsync(messageList, cancellationToken).ConfigureAwait(false);
        }
        else if (messageList.Count > 1)
        {
            await AddMessagesInBatchAsync(messageList, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AddSingleMessageAsync(messageList[0], cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("Successfully stored {MessageCount} messages for conversation {ConversationId}", messageList.Count, ConversationId);
    }

    private async Task AddMessagesSequentiallyAsync(List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var totalRu = 0.0;

        foreach (var message in messages)
        {
            var document = CreateMessageDocument(message, timestamp);
            try
            {
                var response = await _container.CreateItemAsync(document, _partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                totalRu += response.RequestCharge;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                _logger.LogError(ex, "Message exceeds 2MB limit for conversation {ConversationId}, MessageId: {MessageId}", 
                    ConversationId, message.MessageId);
                throw new InvalidOperationException(
                    $"Message exceeds Cosmos DB's maximum item size limit of 2MB. Message ID: {message.MessageId}", ex);
            }
        }

        _logger.LogDebug("Added {MessageCount} messages sequentially for conversation {ConversationId}, RU: {RequestCharge:F2}", 
            messages.Count, ConversationId, totalRu);
    }

    private async Task AddMessagesInBatchAsync(List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        for (var i = 0; i < messages.Count; i += MaxBatchSize)
        {
            var batch = messages.Skip(i).Take(MaxBatchSize).ToList();
            await ExecuteBatchOperationAsync(batch, timestamp, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteBatchOperationAsync(List<ChatMessage> messages, long timestamp, CancellationToken cancellationToken)
    {
        var documents = messages.Select(m => CreateMessageDocument(m, timestamp)).ToList();

        ValidatePartitionKeyConsistency(documents);

        var batch = _container.CreateTransactionalBatch(_partitionKey);
        foreach (var doc in documents)
            batch.CreateItem(doc);

        try
        {
            var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Batch operation failed for conversation {ConversationId}: {StatusCode} - {ErrorMessage}", 
                    ConversationId, response.StatusCode, response.ErrorMessage);
                throw new InvalidOperationException($"Batch operation failed: {response.StatusCode} - {response.ErrorMessage}");
            }

            _logger.LogDebug("Batch added {MessageCount} messages for conversation {ConversationId}, RU: {RequestCharge:F2}", 
                messages.Count, ConversationId, response.RequestCharge);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            _logger.LogWarning("Batch too large, splitting for conversation {ConversationId}: {MessageCount} messages", 
                ConversationId, messages.Count);

            if (messages.Count == 1)
            {
                await AddSingleMessageAsync(messages[0], cancellationToken).ConfigureAwait(false);
                return;
            }

            // Split and retry
            var mid = messages.Count / 2;
            await ExecuteBatchOperationAsync(messages.Take(mid).ToList(), timestamp, cancellationToken).ConfigureAwait(false);
            await ExecuteBatchOperationAsync(messages.Skip(mid).ToList(), timestamp, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ValidatePartitionKeyConsistency(List<CosmosMessageDocument> documents)
    {
        if (documents.Count == 0) return;

        var first = documents[0];
        var isValid = _useHierarchicalPartitioning
            ? documents.All(d => d.TenantId == first.TenantId && d.UserId == first.UserId && d.SessionId == first.SessionId)
            : documents.All(d => d.ConversationId == first.ConversationId);

        if (!isValid)
            throw new InvalidOperationException("All messages in a batch must share the same partition key.");
    }

    private async Task AddSingleMessageAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        var document = CreateMessageDocument(message, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        try
        {
            var response = await _container.CreateItemAsync(document, _partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Added single message for conversation {ConversationId}, RU: {RequestCharge:F2}", 
                ConversationId, response.RequestCharge);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            _logger.LogError(ex, "Message exceeds 2MB limit for conversation {ConversationId}, MessageId: {MessageId}", 
                ConversationId, message.MessageId);
            throw new InvalidOperationException(
                $"Message exceeds Cosmos DB's maximum item size limit of 2MB. Message ID: {message.MessageId}", ex);
        }
    }

    private CosmosMessageDocument CreateMessageDocument(ChatMessage message, long timestamp) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ConversationId = ConversationId,
        Timestamp = timestamp,
        MessageId = message.MessageId,
        Role = message.Role.Value,
        Message = JsonSerializer.Serialize(message, s_defaultJsonOptions),
        Type = "ChatMessage",
        Ttl = MessageTtlSeconds,
        TenantId = _useHierarchicalPartitioning ? _tenantId : null,
        UserId = _useHierarchicalPartitioning ? _userId : null,
        SessionId = _useHierarchicalPartitioning ? ConversationId : null
    };

    /// <inheritdoc />
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var state = new State
        {
            ConversationIdentifier = ConversationId,
            TenantId = _tenantId,
            UserId = _userId,
            UseHierarchicalPartitioning = _useHierarchicalPartitioning
        };

        return JsonSerializer.SerializeToElement(state, jsonSerializerOptions ?? s_defaultJsonOptions);
    }

    /// <summary>Gets the count of messages in this conversation.</summary>
    public async Task<int> GetMessageCountAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.conversationId = @conversationId AND c.Type = @type")
            .WithParameter("@conversationId", ConversationId)
            .WithParameter("@type", "ChatMessage");

        var iterator = _container.GetItemQueryIterator<int>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = _partitionKey
        });

        var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        var count = response.FirstOrDefault();

        _logger.LogDebug("Message count for conversation {ConversationId}: {Count}, RU: {RequestCharge:F2}", 
            ConversationId, count, response.RequestCharge);

        return count;
    }

    /// <summary>Deletes all messages in this conversation.</summary>
    public async Task<int> ClearMessagesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogDebug("Clearing all messages for conversation {ConversationId}", ConversationId);

        var query = new QueryDefinition("SELECT VALUE c.id FROM c WHERE c.conversationId = @conversationId AND c.Type = @type")
            .WithParameter("@conversationId", ConversationId)
            .WithParameter("@type", "ChatMessage");

        var iterator = _container.GetItemQueryIterator<string>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = _partitionKey,
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
                    var deleteResponse = await _container.DeleteItemAsync<object>(itemId, _partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                    totalRu += deleteResponse.RequestCharge;
                    deletedCount++;
                }
            }
            else if (itemIds.Count > 0)
            {
                var batch = _container.CreateTransactionalBatch(_partitionKey);
                foreach (var itemId in itemIds)
                    batch.DeleteItem(itemId);

                var batchResponse = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                totalRu += batchResponse.RequestCharge;
                deletedCount += itemIds.Count;
            }
        }

        _logger.LogInformation("Cleared {DeletedCount} messages for conversation {ConversationId}, RU: {RequestCharge:F2}", 
            deletedCount, ConversationId, totalRu);

        return deletedCount;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        if (_ownsClient)
            _cosmosClient.Dispose();

        _disposed = true;
    }

    private sealed class State
    {
        public string ConversationIdentifier { get; set; } = string.Empty;
        public string? TenantId { get; set; }
        public string? UserId { get; set; }
        public bool UseHierarchicalPartitioning { get; set; }
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Deserialized by Cosmos DB")]
    private sealed class CosmosMessageDocument
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
}
