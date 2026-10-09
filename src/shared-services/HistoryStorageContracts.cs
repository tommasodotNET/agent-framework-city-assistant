using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace SharedServices;

/// <summary>A structured message envelope shared by text and voice; sequence is authoritative ordering.</summary>
public sealed record HistoryMessageDocument
{
    /// <summary>The persisted schema version.</summary>
    [JsonPropertyName("schemaVersion"), JsonRequired] public int SchemaVersion { get; init; } = StorageSchema.Version;
    /// <summary>A server-generated storage id, distinct from the model's message id.</summary>
    [JsonPropertyName("id"), JsonRequired] public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>The immutable history scope.</summary>
    [JsonPropertyName("scopeKey")] public required string ScopeKey { get; init; }
    /// <summary>The internal history id.</summary>
    [JsonPropertyName("conversationId")] public required string ConversationId { get; init; }
    /// <summary>The discriminator, excluding metadata from message queries.</summary>
    [JsonPropertyName("type"), JsonRequired] public string Type { get; init; } = "ChatMessage";
    /// <summary>A monotonically allocated per-history sequence, unaffected by TTL or clear.</summary>
    [JsonPropertyName("sequence"), JsonRequired] public long Sequence { get; init; }
    /// <summary>Diagnostic creation time; not used for ordering.</summary>
    [JsonPropertyName("timestamp"), JsonRequired] public DateTimeOffset Timestamp { get; init; }
    /// <summary>The entire ChatMessage JSON object, never a string containing JSON.</summary>
    [JsonPropertyName("message"), JsonRequired] public JsonElement Message { get; init; }
    /// <summary>Message retention; -1 means no expiration.</summary>
    [JsonPropertyName("ttl"), JsonRequired] public int Ttl { get; init; }

    /// <summary>Restores typed content, including tool calls/results and message identifiers.</summary>
    public ChatMessage ToChatMessage() =>
        Message.Deserialize<ChatMessage>(HistoryJson.Options)
        ?? throw new InvalidOperationException(StorageErrors.Get("InvalidSchema"));

    internal void ValidateFor(HistoryStorageAddress address)
    {
        if (SchemaVersion != StorageSchema.Version || Type != "ChatMessage"
            || ScopeKey != address.ScopeKey || ConversationId != address.ConversationId
            || Sequence < 0 || Message.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(Id) || (Ttl != -1 && Ttl <= 0))
        {
            throw new InvalidOperationException(StorageErrors.Get("InvalidSchema"));
        }
    }
}

/// <summary>A consistent history read at the requested revision.</summary>
public sealed record HistoryReadResult(HistoryReference Reference, IReadOnlyList<ChatMessage> Messages);

/// <summary>The cursor and number of messages affected by a completed operation.</summary>
public sealed record HistoryWriteResult(HistoryReference Reference, int MessageCount);

/// <summary>A stale snapshot or competing conditional write, never automatically retried.</summary>
public sealed class HistoryConcurrencyException : InvalidOperationException
{
    /// <summary>Creates an explicit linear-continuation conflict.</summary>
    public HistoryConcurrencyException() : base(HistoryErrors.Get("Conflict")) { }
}

/// <summary>A multi-batch operation failed after one or more commits; no cross-batch rollback is implied.</summary>
public sealed class HistoryPartialWriteException : InvalidOperationException
{
    internal HistoryPartialWriteException(HistoryReference reference, int count, Exception cause)
        : base(HistoryErrors.Get("Partial"), cause)
    {
        LastCommittedReference = reference;
        CommittedMessageCount = count;
    }
    /// <summary>The last successful cursor; earlier chunks remain persisted.</summary>
    public HistoryReference LastCommittedReference { get; }
    /// <summary>The number of messages committed before failure.</summary>
    public int CommittedMessageCount { get; }
}

/// <summary>Cancellation after partial commits, preserving cancellation semantics and the committed cursor.</summary>
public sealed class HistoryWriteCanceledException : OperationCanceledException
{
    internal HistoryWriteCanceledException(HistoryReference reference, int count, OperationCanceledException cause)
        : base(HistoryErrors.Get("Partial"), cause, cause.CancellationToken)
    {
        LastCommittedReference = reference;
        CommittedMessageCount = count;
    }
    /// <summary>The last successful cursor.</summary>
    public HistoryReference LastCommittedReference { get; }
    /// <summary>The committed message count.</summary>
    public int CommittedMessageCount { get; }
}

internal sealed record HistoryHeadDocument
{
    internal const string DocumentId = "history-head";
    [JsonPropertyName("schemaVersion"), JsonRequired] public int SchemaVersion { get; init; } = StorageSchema.Version;
    [JsonPropertyName("id"), JsonRequired] public string Id { get; init; } = DocumentId;
    [JsonPropertyName("scopeKey")] public required string ScopeKey { get; init; }
    [JsonPropertyName("conversationId")] public required string ConversationId { get; init; }
    [JsonPropertyName("type"), JsonRequired] public string Type { get; init; } = "HistoryHead";
    [JsonPropertyName("revision"), JsonRequired] public long Revision { get; init; }
    [JsonPropertyName("nextSequence"), JsonRequired] public long NextSequence { get; init; }
    [JsonPropertyName("ttl"), JsonRequired] public int Ttl { get; init; } = -1;
    // Absent fields preserve schema-2 heads' original active-history semantics.
    [JsonPropertyName("rotationState"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RotationState { get; init; }
    [JsonPropertyName("rotationCandidate"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryRotationBinding? RotationCandidate { get; init; }
    [JsonPropertyName("rotationTransition"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryRotationBinding? RotationTransition { get; init; }
}

// The same immutable binding on both heads proves publication without a cross-partition transaction.
internal sealed record HistoryRotationBinding
{
    [JsonPropertyName("operationId"), JsonRequired] public required string OperationId { get; init; }
    [JsonPropertyName("source"), JsonRequired] public required HistoryReference Source { get; init; }
    [JsonPropertyName("target"), JsonRequired] public required HistoryReference Target { get; init; }
    [JsonPropertyName("snapshotHash"), JsonRequired] public required string SnapshotHash { get; init; }
    [JsonPropertyName("messageCount"), JsonRequired] public int MessageCount { get; init; }
    [JsonPropertyName("messageTtl"), JsonRequired] public int MessageTtl { get; init; }
}

internal static class HistoryErrors
{
    private static readonly ResourceManager s_resources = new("SharedServices.HistoryResources", typeof(HistoryErrors).Assembly);
    internal static string Get(string name) => s_resources.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException(name);
}
