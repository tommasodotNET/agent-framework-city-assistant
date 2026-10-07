using System.Text.Json.Serialization;

namespace SharedServices;

/// <summary>Durable provider bookkeeping inside session state; never a copy of the original history.</summary>
public sealed record PendingHistoryCompaction
{
    /// <summary>Restores a job and the exact immutable prefix it may replace.</summary>
    [JsonConstructor]
    public PendingHistoryCompaction(
        string compactorKey, HistoryCompactionTicket ticket, HistoryReference source,
        int sourceMessageCount, long sourceLastSequence, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compactorKey);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceMessageCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceLastSequence, sourceMessageCount - 1L);
        if (!StorageSchema.IsRotationOperationId(operationId))
            throw new ArgumentException(StorageErrors.Get("InvalidSchema"), nameof(operationId));
        CompactorKey = compactorKey;
        Ticket = ticket;
        Source = source;
        SourceMessageCount = sourceMessageCount;
        SourceLastSequence = sourceLastSequence;
        OperationId = operationId;
    }

    /// <summary>The registration owning the job; a different profile must not retrieve it.</summary>
    [JsonPropertyName("compactorKey")]
    public string CompactorKey { get; }
    /// <summary>The job identity and opaque source binding.</summary>
    [JsonPropertyName("ticket")]
    public HistoryCompactionTicket Ticket { get; }
    /// <summary>The history version submitted to the compactor, before any later appended turns.</summary>
    [JsonPropertyName("source")]
    public HistoryReference Source { get; }
    /// <summary>The exact number of original live messages.</summary>
    [JsonPropertyName("sourceMessageCount")]
    public int SourceMessageCount { get; }
    /// <summary>Inclusive boundary of the original messages; later appends always have higher sequences.</summary>
    [JsonPropertyName("sourceLastSequence")]
    public long SourceLastSequence { get; }
    /// <summary>Stable publication identity retained across attempts.</summary>
    [JsonPropertyName("operationId")]
    public string OperationId { get; }
}
