namespace VoiceOrchestratorAgent;

/// <summary>Stops both producers before any caller enumerates their mutable conversation state.</summary>
public static class VoiceProcessingShutdown
{
    /// <summary>
    /// Cancels and awaits every producer within one budget. Faults remain observable; on timeout
    /// the caller must not enumerate or persist collections still owned by those producers.
    /// </summary>
    public static async Task CancelAndWaitAsync(CancellationTokenSource cancellation,
        IReadOnlyList<Task> producers, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentNullException.ThrowIfNull(producers);
        using var budget = new CancellationTokenSource(timeout);
        // Include cancellation callbacks in the shutdown budget as well as the async loops.
        await cancellation.CancelAsync().WaitAsync(budget.Token).ConfigureAwait(false);
        var completed = Task.WhenAll(producers);
        try
        {
            await completed.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (completed.IsCompleted && cancellation.IsCancellationRequested)
        {
            // Expected cooperative cancellation only after every producer has actually stopped.
        }
    }
}
