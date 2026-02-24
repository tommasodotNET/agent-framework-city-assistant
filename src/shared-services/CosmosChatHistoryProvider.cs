// Copyright (c) Microsoft. All rights reserved.

using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace SharedServices;

/// <summary>
/// Provides a Cosmos DB implementation of the <see cref="ChatHistoryProvider"/> abstract class.
/// </summary>
[RequiresUnreferencedCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with trimming.")]
[RequiresDynamicCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with NativeAOT.")]
public sealed class CosmosChatHistoryProvider : ChatHistoryProvider, IDisposable
{
    private readonly ProviderSessionState<State> _sessionState;
    private readonly CosmosClient _cosmosClient;
    private readonly Container _container;
    private readonly bool _ownsClient;
    private readonly ILogger<CosmosChatHistoryProvider>? _logger;
    private bool _disposed;


    private CosmosChatMessageRepository _messageRepository;

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
    public int MaxItemCount
    {
        get => _messageRepository.MaxItemCount;
        set => _messageRepository.MaxItemCount = value;
    }

    /// <summary>
    /// Gets or sets the maximum number of items per transactional batch operation.
    /// Default is 100, maximum allowed by Cosmos DB is 100.
    /// </summary>
    public int MaxBatchSize
    {
        get => _messageRepository.MaxBatchSize;
        set => _messageRepository.MaxBatchSize = value;
    }

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
    /// Gets the database ID associated with this provider.
    /// </summary>
    public string DatabaseId { get; init; }

    /// <summary>
    /// Gets the container ID associated with this provider.
    /// </summary>
    public string ContainerId { get; init; }

    /// <inheritdoc />
    public override string StateKey => this._sessionState.StateKey;

    
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

    /// <summary>
    /// Gets the chat reducer used to process or reduce chat messages. If null, no reduction logic will be applied.
    /// </summary>
    public IChatReducer? ChatReducer { get; init; } = null;

    /// <summary>
    /// Gets the storage policy to apply when chat history reduction occurs.
    /// Default is <see cref="ReductionStoragePolicy.Clear"/> which deletes old messages.
    /// Use <see cref="ReductionStoragePolicy.Archive"/> to preserve original messages with an archived suffix.
    /// </summary>
    public ReductionStoragePolicy ReductionStoragePolicy { get; init; } = ReductionStoragePolicy.Clear;

#pragma warning restore MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.



    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class.
    /// </summary>
    /// <param name="cosmosClient">The <see cref="CosmosClient"/> instance to use for Cosmos DB operations.</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="stateInitializer">A delegate that initializes the provider state on the first invocation, providing the conversation routing info (conversationId, tenantId, userId).</param>
    /// <param name="ownsClient">Whether this instance owns the CosmosClient and should dispose it.</param>
    /// <param name="stateKey">An optional key to use for storing the state in the <see cref="AgentSession.StateBag"/>.</param>
    /// <param name="provideOutputMessageFilter">An optional filter function to apply to messages when retrieving them from the chat history.</param>
    /// <param name="storeInputMessageFilter">An optional filter function to apply to messages before storing them in the chat history. If not set, defaults to excluding messages with source type <see cref="AgentRequestMessageSourceType.ChatHistory"/>.</param>
    /// <param name="logger">An optional logger for diagnostics and telemetry.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cosmosClient"/> or <paramref name="stateInitializer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(
        CosmosClient cosmosClient,
        string databaseId,
        string containerId,
        Func<AgentSession?, State> stateInitializer,
        bool ownsClient = false,
        string? stateKey = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        ILogger<CosmosChatHistoryProvider>? logger = null)
        : base(provideOutputMessageFilter, storeInputMessageFilter)
    {

        ArgumentNullException.ThrowIfNull(cosmosClient);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(stateInitializer);

        this._sessionState = new ProviderSessionState<State>( stateInitializer, stateKey ?? this.GetType().Name);
        this._cosmosClient = cosmosClient;
        this.DatabaseId = databaseId;
        this.ContainerId = containerId;
        this._container = this._cosmosClient.GetContainer(databaseId, containerId);
        this._ownsClient = ownsClient;
        this._logger = logger;

        _messageRepository = new CosmosChatMessageRepository(cosmosClient, databaseId, containerId);
    }

    

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class using a connection string.
    /// </summary>
    /// <param name="connectionString">The Cosmos DB connection string.</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="stateInitializer">A delegate that initializes the provider state on the first invocation.</param>
    /// <param name="stateKey">An optional key to use for storing the state in the <see cref="AgentSession.StateBag"/>.</param>
    /// <param name="provideOutputMessageFilter">An optional filter function to apply to messages when retrieving them from the chat history.</param>
    /// <param name="storeInputMessageFilter">An optional filter function to apply to messages before storing them in the chat history. If not set, defaults to excluding messages with source type <see cref="AgentRequestMessageSourceType.ChatHistory"/>.</param>
    /// <param name="logger">An optional logger for diagnostics and telemetry.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(
        string connectionString,
        string databaseId,
        string containerId,
        Func<AgentSession?, State> stateInitializer,
        string? stateKey = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(CreateCosmosClient(connectionString), databaseId, containerId, stateInitializer, ownsClient: true, stateKey, provideOutputMessageFilter, storeInputMessageFilter, logger)
    {
    }

    /// <summary>
    /// Creates a CosmosClient after validating the connection string.
    /// </summary>
    private static CosmosClient CreateCosmosClient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new CosmosClient(connectionString);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosChatHistoryProvider"/> class using TokenCredential for authentication.
    /// </summary>
    /// <param name="accountEndpoint">The Cosmos DB account endpoint URI.</param>
    /// <param name="tokenCredential">The TokenCredential to use for authentication (e.g., DefaultAzureCredential, ManagedIdentityCredential).</param>
    /// <param name="databaseId">The identifier of the Cosmos DB database.</param>
    /// <param name="containerId">The identifier of the Cosmos DB container.</param>
    /// <param name="stateInitializer">A delegate that initializes the provider state on the first invocation.</param>
    /// <param name="stateKey">An optional key to use for storing the state in the <see cref="AgentSession.StateBag"/>.</param>
    /// <param name="provideOutputMessageFilter">An optional filter function to apply to messages when retrieving them from the chat history.</param>
    /// <param name="storeInputMessageFilter">An optional filter function to apply to messages before storing them in the chat history. If not set, defaults to excluding messages with source type <see cref="AgentRequestMessageSourceType.ChatHistory"/>.</param>
    /// <param name="logger">An optional logger for diagnostics and telemetry.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null or whitespace.</exception>
    public CosmosChatHistoryProvider(
        string accountEndpoint,
        TokenCredential tokenCredential,
        string databaseId,
        string containerId,
        Func<AgentSession?, State> stateInitializer,
        string? stateKey = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(CreateCosmosClient(accountEndpoint, tokenCredential), databaseId, containerId, stateInitializer, ownsClient: true, stateKey, provideOutputMessageFilter, storeInputMessageFilter, logger)
    {
    }

    /// <summary>
    /// Creates a CosmosClient after validating endpoint and credential.
    /// </summary>
    private static CosmosClient CreateCosmosClient(string accountEndpoint, TokenCredential tokenCredential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountEndpoint);
        ArgumentNullException.ThrowIfNull(tokenCredential);
        return new CosmosClient(accountEndpoint, tokenCredential);
    }

    /// <summary>
    /// Determines whether hierarchical partitioning should be used based on the state.
    /// </summary>
    private static bool UseHierarchicalPartitioning(State state) =>
        state.TenantId is not null && state.UserId is not null;

    /// <summary>
    /// Builds the partition key from the state.
    /// </summary>
    private static PartitionKey BuildPartitionKey(State state)
    {
        if (UseHierarchicalPartitioning(state))
        {
            return new PartitionKeyBuilder()
                .Add(state.TenantId)
                .Add(state.UserId)
                .Add(state.ConversationId)
                .Build();
        }

        return new PartitionKey(state.ConversationId);
    }

    /// <summary>
    /// Builds the partition key for archiving from the state.
    /// </summary>
    private static PartitionKey BuildArchivePartitionKey(State state, string newConversationId)
    {
        if (UseHierarchicalPartitioning(state))
        {
            return new PartitionKeyBuilder()
                .Add(state.TenantId)
                .Add(state.UserId)
                .Add(newConversationId)
                .Build();
        }

        return new PartitionKey(newConversationId);
    }


    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
#pragma warning disable CA1513 // Use ObjectDisposedException.ThrowIf - not available on all target frameworks
        if (this._disposed)
        {
            throw new ObjectDisposedException(this.GetType().FullName);
        }
#pragma warning restore CA1513

        var state = this._sessionState.GetOrInitializeState(context.Session);
        var partitionKey = BuildPartitionKey(state);

        var documents = await this._messageRepository.GetMessageDocumentAsync(state.ConversationId, partitionKey, this.MaxMessagesToRetrieve, cancellationToken).ConfigureAwait(false);

        // Convert documents to ChatMessages
        var messages = new List<ChatMessage>();
        foreach (var document in documents)
        {
            if (string.IsNullOrEmpty(document.Message)) continue;

            if (JsonSerializer.Deserialize<ChatMessage>(document.Message, s_defaultJsonOptions) is { } message)
            {
                messages.Add(message);
            }
        }

        if (!this.MaxMessagesToRetrieve.HasValue && this.ChatReducer is not null)
        {
            var initialCount = messages.Count;
            this._logger?.LogDebug("Evaluating reduction for conversation {ConversationId} with {MessageCount} messages.", state.ConversationId, initialCount);

            messages = [..await ChatReducer.ReduceAsync(messages, cancellationToken).ConfigureAwait(false)];

            // If reducer actually reduced messages, apply the configured reduction strategy
            if (messages.Count < initialCount)
            {
                this._logger?.LogInformation(
                    "Reducer reduced messages for conversation {ConversationId} from {InitialCount} to {FinalCount}.",
                    state.ConversationId,
                    initialCount,
                    messages.Count);

                await ApplyReductionStrategyAsync(state, documents, messages, cancellationToken).ConfigureAwait(false);
                
            }
        }

        return messages;

    }

    /// <inheritdoc />
    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
#pragma warning disable CA1513 // Use ObjectDisposedException.ThrowIf - not available on all target frameworks
        if (this._disposed)
        {
            throw new ObjectDisposedException(this.GetType().FullName);
        }
#pragma warning restore CA1513

        var state = this._sessionState.GetOrInitializeState(context.Session);
        var messages = context.RequestMessages.Concat(context.ResponseMessages ?? []).ToList();
        if (messages.Count == 0)
        {
            return;
        }

        var partitionKey = BuildPartitionKey(state);

        // Create all documents upfront for validation and batch operation
        var documents = new List<CosmosMessageDocument>(messages.Count);
        var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var message in messages)
        {
            documents.Add(this.CreateMessageDocument(state, message, currentTimestamp));
        }

        await _messageRepository.StoreDocumentsAsync(documents, partitionKey, cancellationToken).ConfigureAwait(false);

    }


    /// <summary>
    /// Gets the count of messages in this conversation.
    /// This is an additional utility method beyond the base contract.
    /// </summary>
    /// <param name="session">The agent session to get state from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of messages in the conversation.</returns>
    public async Task<int> GetMessageCountAsync(AgentSession? session, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var state = this._sessionState.GetOrInitializeState(session);
        var partitionKey = BuildPartitionKey(state);

        return await _messageRepository.GetDocumentCountAsync(state.ConversationId, partitionKey, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// Deletes all messages in this conversation.
    /// This is an additional utility method beyond the base contract.
    /// </summary>
    /// <param name="session">The agent session to get state from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of messages deleted.</returns>
    public async Task<int> ClearMessagesAsync(AgentSession? session, CancellationToken cancellationToken = default)
    {
        var state = this._sessionState.GetOrInitializeState(session);
        var partitionKey = BuildPartitionKey(state);

        return await _messageRepository.DeleteDocumentsAsync(state.ConversationId, partitionKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!this._disposed)
        {
            if (this._ownsClient)
            {
                this._cosmosClient?.Dispose();
            }

            this._logger?.LogDebug("Disposed CosmosChatHistoryProvider for {DatabaseId}/{ContainerId}.", this.DatabaseId, this.ContainerId);
            this._disposed = true;
        }
    }

    /// <summary>
    /// Creates a message document with enhanced metadata.
    /// </summary>
    private CosmosMessageDocument CreateMessageDocument(State state, ChatMessage message, long timestamp)
    {
        var useHierarchical = UseHierarchicalPartitioning(state);

        return new CosmosMessageDocument
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = state.ConversationId,
            Timestamp = timestamp,
            MessageId = message.MessageId,
            Role = message.Role.Value,
            Message = JsonSerializer.Serialize(message, s_defaultJsonOptions),
            Type = "ChatMessage", // Type discriminator
            Ttl = this.MessageTtlSeconds, // Configurable TTL
            // Include hierarchical metadata when using hierarchical partitioning
            TenantId = useHierarchical ? state.TenantId : null,
            UserId = useHierarchical ? state.UserId : null,
            SessionId = useHierarchical ? state.ConversationId : null
        };
    }

    /// <summary>
    /// Applies the configured storage policy to replace conversation history with reduced messages.
    /// For <see cref="ReductionStoragePolicy.Clear"/>: deletes old messages permanently.
    /// For <see cref="ReductionStoragePolicy.Archive"/>: copies old messages with a timestamp suffix, then deletes originals.
    /// </summary>
    /// <param name="reducedMessages">The reduced set of messages to store.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ApplyReductionStrategyAsync(State state, List<CosmosMessageDocument> originalDocuments, List<ChatMessage> compressedMessages, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        this._logger?.LogInformation(
            "Applying reduction policy {Policy} for conversation {ConversationId} with {MessageCount} reduced messages.",
            this.ReductionStoragePolicy,
            state.ConversationId,
            compressedMessages.Count);

        string actualConversationId = state.ConversationId;

        // Step 1: Archive messages if policy requires it (copy only)
        if (ReductionStoragePolicy == ReductionStoragePolicy.Archive)
        {
            var archiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string archiveConversationId = $"{actualConversationId}_archived_{archiveTimestamp}";
            var archivedPartitionKey = BuildArchivePartitionKey(state, archiveConversationId);

            await _messageRepository.CopyDocumentsAsync(originalDocuments, archiveConversationId, archivedPartitionKey, cancellationToken).ConfigureAwait(false);

        }

        // Step 2: Clear original messages (always - single point of responsibility)
        await _messageRepository.DeleteDocumentsAsync(conversationId: actualConversationId, partitionKey: BuildPartitionKey(state), cancellationToken).ConfigureAwait(false);

        // Step 3: Store the reduced messages
        if (compressedMessages.Count > 0)
        {
            var partitionKey = BuildPartitionKey(state);

            // Create all documents upfront for validation and batch operation
            var documents = new List<CosmosMessageDocument>(compressedMessages.Count);
            var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var message in compressedMessages)
            {
                documents.Add(this.CreateMessageDocument(state, message, currentTimestamp));
            }

            await _messageRepository.StoreDocumentsAsync(documents, partitionKey, cancellationToken).ConfigureAwait(false);
        }

    }


    /// <summary>
    /// Represents the per-session state of a <see cref="CosmosChatHistoryProvider"/> stored in the <see cref="AgentSession.StateBag"/>.
    /// </summary>
    public sealed class State
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="State"/> class.
        /// </summary>
        /// <param name="conversationId">The unique identifier for this conversation thread.</param>
        /// <param name="tenantId">Optional tenant identifier for hierarchical partitioning.</param>
        /// <param name="userId">Optional user identifier for hierarchical partitioning.</param>
        public State(string conversationId, string? tenantId = null, string? userId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
            this.ConversationId = conversationId;
            this.TenantId = tenantId;
            this.UserId = userId;
        }

        /// <summary>
        /// Gets the conversation ID associated with this state.
        /// </summary>
        public string ConversationId { get; }

        /// <summary>
        /// Gets the tenant identifier for hierarchical partitioning, if any.
        /// </summary>
        public string? TenantId { get; }

        /// <summary>
        /// Gets the user identifier for hierarchical partitioning, if any.
        /// </summary>
        public string? UserId { get; }
    }

    
}