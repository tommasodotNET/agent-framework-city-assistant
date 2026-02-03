using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace SharedServices;

/// <summary>
/// Configuration options for <see cref="CosmosAgentSessionStore"/>.
/// </summary>
public sealed class CosmosAgentSessionStoreOptions
{
    /// <summary>
    /// Time-To-Live in seconds for session documents. 
    /// Default is -1 (never expire). Set to a positive value to enable automatic expiration.
    /// </summary>
    public int TtlSeconds { get; set; } = -1;
}

/// <summary>
/// Cosmos DB implementation of <see cref="AgentSessionStore"/> for persisting agent sessions.
/// </summary>
/// <remarks>
/// <para>
/// This store persists serialized agent sessions in Azure Cosmos DB, enabling conversation
/// continuity across requests and server restarts.
/// </para>
/// 
/// <para><b>Container Requirements:</b></para>
/// <list type="bullet">
///   <item><description>Partition key: /conversationId</description></item>
///   <item><description>TTL enabled on container if using document expiration</description></item>
/// </list>
/// </remarks>
public sealed class CosmosAgentSessionStore : AgentSessionStore
{
    private readonly Container _container;
    private readonly ILogger<CosmosAgentSessionStore> _logger;
    private readonly JsonSerializerOptions? _serializationOptions;
    private readonly int _ttl;

    private static readonly ItemRequestOptions s_noContentResponse = new() { EnableContentResponseOnWrite = false };

    /// <summary>
    /// Initializes a new instance with a Cosmos DB container.
    /// </summary>
    /// <param name="container">The Cosmos DB container.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ttl">TTL in seconds. Use -1 for no expiration (default).</param>
    /// <param name="jsonSerializerOptions">Optional JSON serialization options.</param>
    public CosmosAgentSessionStore(
        Container container,
        ILogger<CosmosAgentSessionStore> logger,
        int ttl = -1,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(logger);

        _container = container;
        _logger = logger;
        _ttl = ttl;
        _serializationOptions = jsonSerializerOptions;
    }

    /// <inheritdoc />
    public override async ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var key = GetKey(conversationId, agent.Id);

        _logger.LogDebug("Retrieving session for conversation {ConversationId}, agent {AgentId}", conversationId, agent.Id);

        try
        {
            var response = await _container
                .ReadItemAsync<CosmosSessionItem>(key, new PartitionKey(key), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var jsonElement = JsonSerializer.Deserialize<JsonElement>(response.Resource.SerializedSession, _serializationOptions);

            _logger.LogDebug("Retrieved session {Key}, RU: {RequestCharge}", key, response.RequestCharge);

            return await agent
                .DeserializeSessionAsync(jsonElement, jsonSerializerOptions: _serializationOptions, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("No existing session found, creating new session for {ConversationId}", conversationId);
            return await agent.GetNewSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async ValueTask SaveSessionAsync(
        AIAgent agent,
        string conversationId,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(session);

        var key = GetKey(conversationId, agent.Id);

        _logger.LogDebug("Saving session for conversation {ConversationId}, agent {AgentId}", conversationId, agent.Id);

        var serializedSession = session.Serialize(_serializationOptions);

        var sessionItem = new CosmosSessionItem
        {
            Id = key,
            AgentName = agent.Name??string.Empty,
            ConversationId = key,
            SerializedSession = JsonSerializer.Serialize(serializedSession, _serializationOptions),
            LastUpdated = DateTime.UtcNow,
            Ttl = _ttl
        };

        var response = await _container
            .UpsertItemAsync(sessionItem, new PartitionKey(key), requestOptions: s_noContentResponse, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug("Saved session {Key}, RU: {RequestCharge}", key, response.RequestCharge);
    }

    private static string GetKey(string conversationId, string agentId) => $"{agentId}:{conversationId}";

    private sealed class CosmosSessionItem
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("agent")]
        public required string AgentName { get; init; }

        [JsonPropertyName("conversationId")]
        public required string ConversationId { get; init; }

        [JsonPropertyName("serializedSession")]
        public required string SerializedSession { get; init; }

        [JsonPropertyName("lastUpdated")]
        public DateTime LastUpdated { get; init; } = DateTime.UtcNow;

        [JsonPropertyName("ttl")]
        public int Ttl { get; init; } = -1;
    }
}
