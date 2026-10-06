using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharedServices;

/// <summary>A version bound to one complete session address. Never put this in serialized state.</summary>
public sealed class SessionWriteCondition
{
    private SessionWriteCondition(SessionStorageAddress address, string? etag)
    {
        Address = address;
        ETag = etag;
    }

    /// <summary>The complete address this conditional write is valid for.</summary>
    public SessionStorageAddress Address { get; }
    /// <summary>The read/write response ETag for replace, or null for create-only.</summary>
    public string? ETag { get; }
    /// <summary>Whether to create rather than replace. A conflict must never trigger an upsert.</summary>
    public bool IsCreateOnly => ETag is null;

    /// <summary>Creates a condition for an address not loaded by this working copy.</summary>
    public static SessionWriteCondition CreateOnly(SessionStorageAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new(address, null);
    }

    /// <summary>Binds a response ETag to the complete address it came from.</summary>
    public static SessionWriteCondition IfMatch(SessionStorageAddress address, string etag)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        return new(address, etag);
    }
}

/// <summary>
/// Nonserialized tokens for ONE independent working copy. A singleton MAF store may associate one
/// instance per AgentSession using ConditionalWeakTable; never share it by lookup id or agent id.
/// </summary>
public sealed class SessionWriteTracker
{
    private readonly Dictionary<SessionStorageAddress, SessionWriteCondition> _conditions = [];
    private readonly object _gate = new();

    /// <summary>Returns create-only until this copy loads or successfully writes this exact address.</summary>
    public SessionWriteCondition ForAddress(SessionStorageAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        lock (_gate)
        {
            return _conditions.GetValueOrDefault(address) ?? SessionWriteCondition.CreateOnly(address);
        }
    }

    /// <summary>Records only a successful read/write response, never a failed conditional operation.</summary>
    public void Record(SessionWriteCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        lock (_gate)
        {
            _conditions[condition.Address] = condition;
        }
    }
}

/// <summary>Validated schema-v2 envelope; active history exists only inside the session snapshot.</summary>
public sealed class SessionDocument
{
    /// <summary>Restores the full envelope, rejecting old schemas and string-valued snapshots.</summary>
    [JsonConstructor]
    public SessionDocument(int schemaVersion, string id, string agentId, string scopeKey, string sessionId,
        JsonElement serializedSession, DateTimeOffset lastUpdated, int ttl)
    {
        var address = new SessionStorageAddress(agentId, scopeKey, sessionId);
        if (schemaVersion != StorageSchema.Version || id != address.DocumentId)
        {
            throw new InvalidOperationException(StorageErrors.Get("InvalidSchema"));
        }
        if (serializedSession.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(StorageErrors.Get("InvalidSnapshot"), nameof(serializedSession));
        }
        if (ttl != -1 && ttl <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), StorageErrors.Get("InvalidTtl"));
        }
        SchemaVersion = schemaVersion;
        Id = id;
        AgentId = agentId;
        ScopeKey = scopeKey;
        SessionId = sessionId;
        SerializedSession = serializedSession.Clone();
        LastUpdated = lastUpdated;
        Ttl = ttl;
    }

    /// <summary>The persisted document schema version.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; }
    /// <summary>The encoded stable agent id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; }
    /// <summary>The original stable agent id.</summary>
    [JsonPropertyName("agentId")]
    public string AgentId { get; }
    /// <summary>The current session lookup scope.</summary>
    [JsonPropertyName("scopeKey")]
    public string ScopeKey { get; }
    /// <summary>The raw continuation key.</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; }
    /// <summary>The entire MAF or voice state as a detached JSON object, without ETags.</summary>
    [JsonPropertyName("serializedSession")]
    public JsonElement SerializedSession { get; }
    /// <summary>The last successful snapshot's timestamp.</summary>
    [JsonPropertyName("lastUpdated")]
    public DateTimeOffset LastUpdated { get; }
    /// <summary>The agent-specific document TTL, preserving existing retention policy.</summary>
    [JsonPropertyName("ttl")]
    public int Ttl { get; }

    /// <summary>Creates a new envelope without losing unknown snapshot fields.</summary>
    public static SessionDocument Create(SessionStorageAddress address, JsonElement serializedSession,
        DateTimeOffset lastUpdated, int ttl = -1)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new(StorageSchema.Version, address.DocumentId, address.AgentId, address.ScopeKey,
            address.SessionId, serializedSession, lastUpdated, ttl);
    }

    /// <summary>Validates the read envelope against the requested full address.</summary>
    public void ValidateFor(SessionStorageAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (new SessionStorageAddress(AgentId, ScopeKey, SessionId) != address)
        {
            throw new InvalidOperationException(StorageErrors.Get("InvalidSchema"));
        }
    }

    /// <summary>
    /// Serializes and checks the FULL envelope. Repositories must write these same bytes/options,
    /// not measure only the snapshot or use a different serializer after validating.
    /// </summary>
    public byte[] SerializeToUtf8Bytes(JsonSerializerOptions? options = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, options);
        StorageSchema.ValidateSessionDocumentSize(bytes);
        return bytes;
    }
}

/// <summary>A successful point read; return null from the repository only for a genuine 404.</summary>
public sealed class SessionReadResult
{
    /// <summary>Binds an independent, validated snapshot to its address-specific response version.</summary>
    public SessionReadResult(SessionDocument document, SessionWriteCondition version)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(version);
        document.ValidateFor(version.Address);
        if (version.IsCreateOnly)
        {
            throw new ArgumentException(StorageErrors.Get("InvalidSchema"), nameof(version));
        }
        Document = document;
        Version = version;
    }

    /// <summary>The complete independently cloned snapshot.</summary>
    public SessionDocument Document { get; }
    /// <summary>The conditional replace token, scoped to this working copy and full address.</summary>
    public SessionWriteCondition Version { get; }
}
