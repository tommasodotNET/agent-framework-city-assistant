using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
namespace SharedServices;

/// <summary>
/// Specifies the storage policy to apply when chat history reduction occurs.
/// </summary>
public enum ReductionStoragePolicy
{
    /// <summary>
    /// Clears the existing messages and replaces them with the reduced set.
    /// This is the most storage-efficient option but loses the original history.
    /// </summary>
    Clear,

    /// <summary>
    /// Archives the existing messages by renaming their conversationId with an "_archived_{timestamp}" suffix,
    /// then stores the reduced messages with the original conversationId.
    /// This preserves the original history for audit/recovery purposes.
    /// </summary>
    Archive
}

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
    private readonly CosmosChatMessageRepository _repository;
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
    /// Gets the chat reducer used to process or reduce chat messages. If null, no reduction logic will be applied.
    /// </summary>
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    public IChatReducer? ChatReducer { get; init; } = null;
#pragma warning restore MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

    /// <summary>
    /// Gets the storage policy to apply when chat history reduction occurs.
    /// Default is <see cref="ReductionStoragePolicy.Clear"/> which deletes old messages.
    /// Use <see cref="ReductionStoragePolicy.Archive"/> to preserve original messages with an archived suffix.
    /// </summary>
    public ReductionStoragePolicy ReductionStoragePolicy { get; init; } = ReductionStoragePolicy.Clear;

    /// <summary>
    /// Determines if the given CosmosClient is connected to the local emulator.
    /// </summary>
    /// <param name="cosmosClient">The CosmosClient to check.</param>
    /// <returns>True if connected to the emulator, false otherwise.</returns>
    private static bool DetectEmulator(CosmosClient cosmosClient) =>
        cosmosClient.Endpoint?.Host is { } host &&
        (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         host.StartsWith("127.", StringComparison.OrdinalIgnoreCase) ||
         host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase));

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
        _ownsClient = ownsClient;
        _isEmulator = DetectEmulator(cosmosClient);
        _tenantId = tenantId;
        _userId = userId;
        _useHierarchicalPartitioning = tenantId is not null && userId is not null;
        _logger = logger ?? NullLogger<CosmosChatHistoryProvider>.Instance;

        // Initialize repository
        var container = cosmosClient.GetContainer(databaseId, containerId);
        _repository = new CosmosChatMessageRepository(container, _isEmulator, _logger);

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
    /// This factory method is used to restore a provider instance from state that was previously saved via <see cref="Serialize"/>.
    /// </summary>
    /// <param name="cosmosClient">The <see cref="CosmosClient"/> instance to use for Cosmos DB operations.</param>
    /// <param name="serializedState">A <see cref="JsonElement"/> representing the serialized state of the provider, 
    /// typically obtained from a previous call to <see cref="Serialize"/>.</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="jsonSerializerOptions">Optional settings for customizing the JSON deserialization process.</param>
    /// <param name="reducer">Optional chat reducer to process or reduce chat messages before retrieval. 
    /// If null, no reduction logic will be applied.</param>
    /// <param name="reductionStoragePolicy">The storage policy to apply when chat history reduction occurs. 
    /// Default is <see cref="ReductionStoragePolicy.Clear"/> which deletes old messages. 
    /// Use <see cref="ReductionStoragePolicy.Archive"/> to preserve original messages with an archived suffix.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>A new instance of <see cref="CosmosChatHistoryProvider"/> initialized from the serialized state.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cosmosClient"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="databaseId"/> or <paramref name="containerId"/> is null or whitespace,
    /// or when the serialized state is invalid or cannot be deserialized.</exception>
    public static CosmosChatHistoryProvider CreateFromSerializedState(CosmosClient cosmosClient, JsonElement serializedState, string databaseId, string containerId, JsonSerializerOptions? jsonSerializerOptions = null,
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        IChatReducer? reducer = null,
#pragma warning restore MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        ReductionStoragePolicy reductionStoragePolicy = ReductionStoragePolicy.Clear,
        ILogger<CosmosChatHistoryProvider>? logger = null)
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
              { ChatReducer = reducer, ReductionStoragePolicy = reductionStoragePolicy }
            : new CosmosChatHistoryProvider(cosmosClient, databaseId, containerId, conversationId, ownsClient: false, logger) 
              { ChatReducer = reducer, ReductionStoragePolicy = reductionStoragePolicy };
    }

    /// <inheritdoc />
    public override async ValueTask<IEnumerable<ChatMessage>> InvokingAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogDebug("Retrieving chat history for conversation {ConversationId}", ConversationId);

        // Configure repository settings
        _repository.MaxItemCount = MaxItemCount;
        _repository.MaxBatchSize = MaxBatchSize;

        // Fetch messages via repository
        var (documents, totalRu) = await _repository.GetMessagesAsync(
            ConversationId, 
            _partitionKey, 
            MaxMessagesToRetrieve, 
            cancellationToken).ConfigureAwait(false);

        // Convert documents to ChatMessages
        var messages = new List<ChatMessage>();
        foreach (var document in documents)
        {
            if (string.IsNullOrEmpty(document.Message)) continue;

            if (JsonSerializer.Deserialize<ChatMessage>(document.Message, s_defaultJsonOptions) is { } message)
                messages.Add(message);
        }

        _logger.LogDebug("Retrieved {MessageCount} messages for conversation {ConversationId}, RU: {RequestCharge:F2}", 
            messages.Count, ConversationId, totalRu);

        if (!MaxMessagesToRetrieve.HasValue && ChatReducer is not null)
        {
            var initialCount = messages.Count;
            _logger.LogDebug("Evaluating reduce for conversation {ConversationId}", ConversationId);
            messages = (await ChatReducer.ReduceAsync(messages, cancellationToken).ConfigureAwait(false)).ToList();

            // If reducer actually reduced messages, apply the configured reduction strategy
            if (messages.Count < initialCount)
            {
                _logger.LogDebug("Reducer reduced messages for conversation {ConversationId} from {InitialCount} to {FinalCount}", 
                    ConversationId, initialCount, messages.Count);
                
                await ApplyReductionStrategyAsync(messages, cancellationToken).ConfigureAwait(false);
            }
        }

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

        await StoreMessagesAsync(messageList, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the configured storage policy to replace conversation history with reduced messages.
    /// For <see cref="ReductionStoragePolicy.Clear"/>: deletes old messages permanently.
    /// For <see cref="ReductionStoragePolicy.Archive"/>: copies old messages with a timestamp suffix, then deletes originals.
    /// </summary>
    /// <param name="reducedMessages">The reduced set of messages to store.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ApplyReductionStrategyAsync(IList<ChatMessage> reducedMessages, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogInformation("[{Policy} Policy] Applying reduction for {ConversationId} with {MessageCount} reduced messages",
            ReductionStoragePolicy.ToString(), ConversationId, reducedMessages.Count);

        string? archivedConversationId = null;
        var archivedCount = 0;

        // Step 1: Archive messages if policy requires it (copy only)
        if (ReductionStoragePolicy == ReductionStoragePolicy.Archive)
        {
            var archiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            archivedConversationId = $"{ConversationId}_archived_{archiveTimestamp}";
            archivedCount = await CopyMessagesToArchiveAsync(archivedConversationId, cancellationToken).ConfigureAwait(false);
        }

        // Step 2: Clear original messages (always - single point of responsibility)
        var clearedCount = await ClearMessagesAsync(cancellationToken).ConfigureAwait(false);

        // Step 3: Store the reduced messages
        if (reducedMessages.Count > 0)
        {
            await StoreMessagesAsync(reducedMessages, cancellationToken).ConfigureAwait(false);
        }

        // Log completion
        if (archivedConversationId is not null)
        {
            _logger.LogInformation("Reduction complete for {ConversationId}: {ArchivedCount} copied to {ArchivedId}, {ClearedCount} cleared, {NewCount} stored", 
                ConversationId, archivedCount, archivedConversationId, clearedCount, reducedMessages.Count);
        }
        else
        {
            _logger.LogInformation("Reduction complete for {ConversationId}: {ClearedCount} cleared, {NewCount} stored", 
                ConversationId, clearedCount, reducedMessages.Count);
        }
    }

    /// <summary>
    /// Copies messages to a new archived conversationId.
    /// This is necessary because Cosmos DB doesn't support updating partition key values directly.
    /// Note: This method only copies messages; deletion is handled separately by the caller.
    /// </summary>
    /// <param name="archivedConversationId">The target conversationId for archived messages.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of messages copied.</returns>
    private async Task<int> CopyMessagesToArchiveAsync(string archivedConversationId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Copying messages from {ConversationId} to {ArchivedConversationId}", ConversationId, archivedConversationId);

        // Get all existing documents
        var (documentsToArchive, _) = await _repository.GetMessagesAsync(ConversationId, _partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (documentsToArchive.Count == 0)
        {
            _logger.LogDebug("No messages to copy for conversation {ConversationId}", ConversationId);
            return 0;
        }

        // Build the archived partition key
        var archivedPartitionKey = _useHierarchicalPartitioning
            ? new PartitionKeyBuilder().Add(_tenantId!).Add(_userId!).Add(archivedConversationId).Build()
            : new PartitionKey(archivedConversationId);

        // Copy documents to archived conversation
        await _repository.CopyDocumentsToConversationAsync(
            documentsToArchive, 
            archivedConversationId, 
            archivedPartitionKey, 
            _tenantId, 
            _userId, 
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Copied {Count} messages to {ArchivedConversationId}", documentsToArchive.Count, archivedConversationId);

        return documentsToArchive.Count;
    }

    /// <summary>
    /// Stores a list of chat messages to Cosmos DB.
    /// </summary>
    /// <param name="messages">The list of messages to store.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task StoreMessagesAsync(IList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (messages.Count == 0)
        {
            _logger.LogDebug("No messages to store for conversation {ConversationId}", ConversationId);
            return;
        }

        _logger.LogDebug("Storing {MessageCount} messages for conversation {ConversationId}", messages.Count, ConversationId);

        // Convert ChatMessages to documents
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var documents = messages.Select(m => CreateMessageDocument(m, timestamp)).ToList();

        // Store via repository
        await _repository.StoreDocumentsAsync(documents, _partitionKey, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Successfully stored {MessageCount} messages for conversation {ConversationId}", messages.Count, ConversationId);
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

        var (count, _) = await _repository.GetMessageCountAsync(ConversationId, _partitionKey, cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>Deletes all messages in this conversation.</summary>
    public async Task<int> ClearMessagesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogDebug("Clearing all messages for conversation {ConversationId}", ConversationId);

        var (deletedCount, totalRu) = await _repository.DeleteMessagesAsync(ConversationId, _partitionKey, cancellationToken).ConfigureAwait(false);

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
}
