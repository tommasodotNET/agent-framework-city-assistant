namespace SharedServices;

/// <summary>Live Load-to-Save state for one best-effort background job.</summary>
internal sealed record PendingHistoryCompaction(
    HistoryReference Source,
    HistoryCompactionRequest Request,
    HistoryCompactionTicket Ticket,
    string OperationId,
    IHistoryCompactor Compactor);
