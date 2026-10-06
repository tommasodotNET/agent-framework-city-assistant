using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;

namespace SharedServices;

/// <summary>A complete point-read address: stable agent id plus both session partition components.</summary>
public sealed record SessionStorageAddress
{
    /// <summary>Validates a stored address; anonymous scope must be derived from this lookup id.</summary>
    [JsonConstructor]
    public SessionStorageAddress(string agentId, string scopeKey, string sessionId)
    {
        StorageSchema.ValidateKey(agentId, nameof(agentId));
        StorageSchema.ValidateKey(sessionId, nameof(sessionId));
        StorageScope.Validate(scopeKey);
        StorageSchema.ValidatePartitionKey(scopeKey, sessionId);
        if (StorageScope.IsAnonymous(scopeKey)
            && !string.Equals(scopeKey, StorageScope.Create(sessionId), StringComparison.Ordinal))
        {
            throw new ArgumentException(StorageErrors.Get("InvalidScope"), nameof(scopeKey));
        }

        var documentId = Uri.EscapeDataString(agentId);
        StorageSchema.ValidateKey(documentId, nameof(agentId), StorageSchema.MaxDocumentIdBytes);
        AgentId = agentId;
        ScopeKey = scopeKey;
        SessionId = sessionId;
    }

    /// <summary>The stable agent discriminator, never inferred from a protocol or lookup prefix.</summary>
    public string AgentId { get; }
    /// <summary>The current session document's canonical lookup scope.</summary>
    public string ScopeKey { get; }
    /// <summary>The unmodified external lookup id.</summary>
    public string SessionId { get; }
    /// <summary>Reversible URI encoding of only the agent id, satisfying Cosmos id restrictions.</summary>
    [JsonIgnore]
    public string DocumentId => Uri.EscapeDataString(AgentId);

    /// <summary>Builds the same address for hosted adapters and explicitly captured voice keys.</summary>
    public static SessionStorageAddress Create(
        string agentId, string lookupId, IReadOnlyDictionary<string, string>? partitions = null) =>
        new(agentId, StorageScope.Create(lookupId, partitions), lookupId);

#pragma warning disable MAAI001 // Only this overload depends on the experimental hosting key.
    /// <summary>Consumes every partition supplied by the framework, without ambient identity lookup.</summary>
    public static SessionStorageAddress Create(string agentId, AgentSessionStoreKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Create(agentId, key.SessionId, key.Partitions);
    }
#pragma warning restore MAAI001

    /// <summary>Builds the full hierarchical key; never use a prefix for point reads or writes.</summary>
    public PartitionKey ToPartitionKey() => new PartitionKeyBuilder().Add(ScopeKey).Add(SessionId).Build();
}

/// <summary>The complete history partition, independent of any current session lookup alias.</summary>
public sealed record HistoryStorageAddress
{
    /// <summary>Validates the immutable history scope and server-generated conversation id.</summary>
    [JsonConstructor]
    public HistoryStorageAddress(string scopeKey, string conversationId)
    {
        StorageScope.Validate(scopeKey);
        StorageSchema.ValidateKey(conversationId, nameof(conversationId));
        StorageSchema.ValidatePartitionKey(scopeKey, conversationId);
        ScopeKey = scopeKey;
        ConversationId = conversationId;
    }

    /// <summary>The scope fixed when the history was created.</summary>
    public string ScopeKey { get; }
    /// <summary>The internal history identity, unrelated to protocol continuation ids.</summary>
    public string ConversationId { get; }

    /// <summary>Builds the full history hierarchical partition key.</summary>
    public PartitionKey ToPartitionKey() => new PartitionKeyBuilder().Add(ScopeKey).Add(ConversationId).Build();
}
