using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace SharedServices;

/// <summary>The requested execution mode, independent of the compaction algorithm.</summary>
public enum HistoryCompactionMode
{
    /// <summary>Await compaction before providing history to the model.</summary>
    Foreground,
    /// <summary>Start work during Load and retrieve its result during Save.</summary>
    Background
}

/// <summary>The outcome of an operation that has not persisted any history.</summary>
public enum HistoryCompactionStatus
{
    /// <summary>No applicable change; the provider keeps the original history.</summary>
    Unchanged,
    /// <summary>A candidate replacement; only the provider may persist and activate it.</summary>
    Completed,
    /// <summary>Accepted work is outstanding; no replacement history is available yet.</summary>
    Pending
}

/// <summary>A storage-independent compaction engine whose strategy owns triggers and algorithms.</summary>
/// <remarks>
/// Implementations must reject unsupported modes, propagate failures and cancellation, and never write
/// history or agent sessions. They must deep-copy mutable messages and contents before running a strategy.
/// Per-invocation indexes and message state must not be shared between calls.
/// SupportedModes alone declares capabilities; background implementations must override GetResultAsync.
/// Background jobs may be local best-effort or durable remote work. Implementations own execution
/// and short-lived result retention, not the history provider.
/// </remarks>
public interface IHistoryCompactor
{
    /// <summary>The execution modes this implementation supports.</summary>
    IReadOnlySet<HistoryCompactionMode> SupportedModes { get; }

    /// <summary>Produces a detached result in foreground, or acknowledges accepted work in background; never writes history.</summary>
    Task<HistoryCompactionResult> CompactAsync(
        HistoryCompactionRequest request,
        CancellationToken cancellationToken = default);
    /// <summary>Retrieves one job outcome without waiting for unfinished compaction.</summary>
    /// <remarks>
    /// In Background mode CompactAsync returns Unchanged or Pending, never inline Completed.
    /// Return Pending only for a known accepted job that is still running, or the final
    /// Unchanged/Completed result bound to the original source. Report missing, expired or failed
    /// jobs with InvalidOperationException, never fake Pending or empty history.
    /// Request cancellation cancels enqueue/retrieval, not an already accepted job.
    /// Foreground-only implementations can use this default, which explicitly rejects retrieval.
    /// </remarks>
    /// <exception cref="NotSupportedException">The compactor does not implement background retrieval.</exception>
    Task<HistoryCompactionResult> GetResultAsync(
        HistoryCompactionTicket ticket, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(CompactionErrors.Get("RetrievalNotSupported"));
}

/// <summary>Opaque accepted job identity bound to the original provider request.</summary>
public sealed record HistoryCompactionTicket
{
    /// <summary>Creates a ticket; neither value should contain credentials or be logged.</summary>
    public HistoryCompactionTicket(string jobId, string sourceBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBinding);
        JobId = jobId;
        SourceBinding = sourceBinding;
    }

    /// <summary>The backend's opaque correlation id.</summary>
    [JsonPropertyName("jobId")]
    public string JobId { get; }
    /// <summary>The exact opaque source binding received when the job was started.</summary>
    [JsonPropertyName("sourceBinding")]
    public string SourceBinding { get; }
}

/// <summary>The logical source binding and complete history for one compaction operation.</summary>
/// <remarks>
/// SourceBinding is an opaque, provider-generated binding to the exact source version. The compactor
/// only echoes it; it must not parse it or log it. Messages snapshots the collection, not the mutable
/// message/content objects: implementations must detach those before applying a strategy.
/// </remarks>
public sealed record HistoryCompactionRequest
{
    /// <summary>Creates a request without exposing a mutable agent session or storage address.</summary>
    public HistoryCompactionRequest(
        string agentId,
        string sourceBinding,
        IReadOnlyList<ChatMessage> messages,
        HistoryCompactionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBinding);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var snapshot = messages.ToArray();
        if (snapshot.Any(message => message is null))
        {
            throw new ArgumentException("History cannot contain null messages.", nameof(messages));
        }

        AgentId = agentId;
        SourceBinding = sourceBinding;
        Messages = Array.AsReadOnly(snapshot);
        Options = options;
    }

    /// <summary>The configured logical agent id, not a user or storage identity.</summary>
    public string AgentId { get; }
    /// <summary>The opaque binding to the exact source version, echoed by the result.</summary>
    public string SourceBinding { get; }
    /// <summary>A collection snapshot; the compactor must isolate mutable messages and contents.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; }
    /// <summary>The immutable profile; CompactorKey identifies the DI-configured strategy profile.</summary>
    public HistoryCompactionOptions Options { get; }
}

/// <summary>A candidate outcome with independently verifiable before/after history diagnostics.</summary>
/// <remarks>
/// UTF-8 sizes use the metric documented on HistoryCompactionOptions.MaxHistoryUtf8Bytes.
/// The provider must verify the binding and measure the history it will use before any writes;
/// plugin-supplied diagnostics alone are not proof of safety. Completed does not imply a smaller
/// message count or successful persistence. Failures and cancellation are exceptions, not outcomes.
/// </remarks>
public sealed record HistoryCompactionResult
{
    /// <summary>Creates an outcome. Pending requires only a matching ticket; final outcomes have no ticket.</summary>
    [JsonConstructor]
    public HistoryCompactionResult(
        HistoryCompactionStatus status,
        string sourceBinding,
        IReadOnlyList<ChatMessage> messages,
        long beforeUtf8Bytes,
        long afterUtf8Bytes,
        HistoryCompactionTicket? ticket = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBinding);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfNegative(beforeUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(afterUtf8Bytes);
        var snapshot = messages.ToArray();
        if (snapshot.Any(message => message is null))
        {
            throw new ArgumentException("History cannot contain null messages.", nameof(messages));
        }
        if (status == HistoryCompactionStatus.Pending)
        {
            var matchingTicket = ticket is not null && ticket.SourceBinding == sourceBinding;
            var hasFinalPayload = snapshot.Length != 0 || beforeUtf8Bytes != 0 || afterUtf8Bytes != 0;
            if (!matchingTicket || hasFinalPayload)
            {
                throw new ArgumentException(CompactionErrors.Get("InvalidLifecycle"), nameof(ticket));
            }
        }
        else if (ticket is not null)
        {
            throw new ArgumentException(CompactionErrors.Get("InvalidLifecycle"), nameof(ticket));
        }
        if (status == HistoryCompactionStatus.Completed && snapshot.Length == 0)
        {
            throw new ArgumentException("A completed result requires a usable replacement history.", nameof(messages));
        }
        if (status == HistoryCompactionStatus.Unchanged && beforeUtf8Bytes != afterUtf8Bytes)
        {
            throw new ArgumentException("An unchanged result must report equal history sizes.", nameof(afterUtf8Bytes));
        }

        Status = status;
        SourceBinding = sourceBinding;
        Messages = Array.AsReadOnly(snapshot);
        BeforeUtf8Bytes = beforeUtf8Bytes;
        AfterUtf8Bytes = afterUtf8Bytes;
        Ticket = ticket;
    }

    /// <summary>Creates an outstanding-job outcome. It has no messages or final byte diagnostics.</summary>
    public static HistoryCompactionResult Pending(HistoryCompactionTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return new(HistoryCompactionStatus.Pending, ticket.SourceBinding, [], 0, 0, ticket);
    }

    /// <summary>Present only for Pending; never interpretable as completed or unchanged history.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryCompactionTicket? Ticket { get; }
    /// <summary>Whether to keep the source or validate a replacement candidate.</summary>
    public HistoryCompactionStatus Status { get; }
    /// <summary>The exact opaque source binding received in the request.</summary>
    public string SourceBinding { get; }
    /// <summary>The detached candidate, or unchanged history; collection membership is immutable.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; }
    /// <summary>The original history size, not the full prompt. Unavailable (zero) for Pending.</summary>
    public long BeforeUtf8Bytes { get; }
    /// <summary>The resulting history size, not the full prompt. Unavailable (zero) for Pending.</summary>
    public long AfterUtf8Bytes { get; }
}
