using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharedServices;

/// <summary>Runs a foreground compactor as best-effort in-process work.</summary>
public sealed class LocalBackgroundHistoryCompactor : IHistoryCompactor
{
    private static readonly FrozenSet<HistoryCompactionMode> s_supportedModes =
        new[] { HistoryCompactionMode.Background }.ToFrozenSet();
    private static readonly TimeSpan s_resultRetention = TimeSpan.FromMinutes(5);
    private readonly IHistoryCompactor _foreground;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly ILogger<LocalBackgroundHistoryCompactor> _logger;

    /// <summary>Creates a local background adapter over a foreground implementation.</summary>
    public LocalBackgroundHistoryCompactor(
        IHistoryCompactor foreground,
        ILogger<LocalBackgroundHistoryCompactor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(foreground);
        if (!foreground.SupportedModes.Contains(HistoryCompactionMode.Foreground))
            throw new NotSupportedException(CompactionErrors.Get("UnsupportedMode"));
        _foreground = foreground;
        _logger = logger ?? NullLogger<LocalBackgroundHistoryCompactor>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlySet<HistoryCompactionMode> SupportedModes => s_supportedModes;

    /// <inheritdoc />
    public Task<HistoryCompactionResult> CompactAsync(
        HistoryCompactionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options.Mode != HistoryCompactionMode.Background)
            throw new NotSupportedException(CompactionErrors.Get("UnsupportedMode"));
        cancellationToken.ThrowIfCancellationRequested();

        var ticket = new HistoryCompactionTicket(Guid.NewGuid().ToString("N"), request.SourceBinding);
        var detachedMessages = HistoryCompactionValidation.Detach(
            HistoryCompactionValidation.Serialize(request.Messages));
        var foregroundRequest = new HistoryCompactionRequest(
            request.AgentId,
            request.SourceBinding,
            detachedMessages,
            request.Options with { Mode = HistoryCompactionMode.Foreground });
        var job = new Job(ticket.SourceBinding,
            Task.Run(() => _foreground.CompactAsync(foregroundRequest, CancellationToken.None)));
        if (!_jobs.TryAdd(ticket.JobId, job))
            throw new InvalidOperationException(CompactionErrors.Get("InvalidLifecycle"));

        _ = ObserveFailureAsync(job.Task);
        _ = ExpireAsync(ticket.JobId);
        return Task.FromResult(HistoryCompactionResult.Pending(ticket));
    }

    /// <inheritdoc />
    public async Task<HistoryCompactionResult> GetResultAsync(
        HistoryCompactionTicket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_jobs.TryGetValue(ticket.JobId, out var job)
            || !string.Equals(job.SourceBinding, ticket.SourceBinding, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(CompactionErrors.Get("InvalidLifecycle"));
        }

        if (!job.Task.IsCompleted)
            return HistoryCompactionResult.Pending(ticket);

        try
        {
            return await job.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _jobs.TryRemove(ticket.JobId, out _);
        }
    }

    private async Task ExpireAsync(string jobId)
    {
        await Task.Delay(s_resultRetention).ConfigureAwait(false);
        _jobs.TryRemove(jobId, out _);
    }

    private async Task ObserveFailureAsync(Task job)
    {
        try
        {
            await job.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(CompactionErrors.Get("BackgroundLog"), exception.GetType().Name);
        }
    }

    private sealed record Job(string SourceBinding, Task<HistoryCompactionResult> Task);
}
