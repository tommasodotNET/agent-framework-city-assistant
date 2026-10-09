using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

#pragma warning disable MAAI001

namespace SharedServices;

/// <summary>Per-host snapshot retention configuration, independent of history metadata retention.</summary>
public sealed class CosmosAgentSessionStoreOptions
{
    /// <summary>Session TTL in seconds; -1 preserves the default of no expiration.</summary>
    public int TtlSeconds { get; set; } = -1;
}

/// <summary>
/// MAF adapter for schema-v2 snapshots. The container requires [/scopeKey, /sessionId] and TTL -1.
/// Agents must provide stable ids; the full owning agent serialization is preserved.
/// </summary>
public sealed class CosmosAgentSessionStore : AgentSessionStore
{
    private readonly CosmosSessionRepository _repository;
    private readonly JsonSerializerOptions? _serializationOptions;
    private readonly int _ttl;
    private readonly ConditionalWeakTable<AgentSession, SessionWriteTracker> _trackers = new();

    /// <summary>Preserves direct construction with an existing SDK container and optional MAF serializer options.</summary>
    public CosmosAgentSessionStore(Container container, ILogger<CosmosAgentSessionStore> logger,
        int ttl = -1, JsonSerializerOptions? jsonSerializerOptions = null)
        : this(new CosmosSessionRepository(container, logger),
            logger, ttl, jsonSerializerOptions) { }

    /// <summary>Uses the shared repository also available to voice adapters through DI.</summary>
    public CosmosAgentSessionStore(CosmosSessionRepository repository, ILogger<CosmosAgentSessionStore> logger,
        int ttl = -1, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        if (ttl != -1 && ttl <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), StorageErrors.Get("InvalidTtl"));
        }
        _repository = repository;
        _ttl = ttl;
        _serializationOptions = jsonSerializerOptions;
    }

    /// <inheritdoc />
    public override async ValueTask<AgentSession?> GetSessionAsync(
        AIAgent agent, AgentSessionStoreKey sessionKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(sessionKey);
        var address = SessionStorageAddress.Create(agent.Id, sessionKey);
        var result = await _repository.ReadAsync(address, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }
        var session = await agent.DeserializeSessionAsync(result.Document.SerializedSession,
            _serializationOptions, cancellationToken).ConfigureAwait(false);
        SessionPersistenceState.GetRequired(session).ValidateFor(address);
        _trackers.GetOrCreateValue(session).Record(result.Version);
        return session;
    }

    /// <inheritdoc />
    public override async ValueTask<AgentSession> GetOrCreateSessionAsync(
        AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        var existing = await GetSessionAsync(agent, key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create(agent.Id, key));
        return session;
    }

    /// <inheritdoc />
    public override async ValueTask SaveSessionAsync(
        AIAgent agent, AgentSessionStoreKey sessionKey, AgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentNullException.ThrowIfNull(session);
        var address = SessionStorageAddress.Create(agent.Id, sessionKey);
        SessionPersistenceState.GetRequired(session).ValidateFor(address);
        var snapshot = await agent.SerializeSessionAsync(session, _serializationOptions, cancellationToken).ConfigureAwait(false);
        var document = SessionDocument.Create(address, snapshot, DateTimeOffset.UtcNow, _ttl);
        var tracker = _trackers.GetOrCreateValue(session);
        var version = await _repository.WriteAsync(document, tracker.ForAddress(address), cancellationToken).ConfigureAwait(false);
        tracker.Record(version);
    }
}
