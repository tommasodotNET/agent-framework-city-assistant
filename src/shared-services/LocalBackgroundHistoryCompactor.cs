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
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a local background adapter over a foreground implementation.</summary>
    public LocalBackgroundHistoryCompactor(
        IHistoryCompactor foreground,
        ILogger<LocalBackgroundHistoryCompactor>? logger = null)
        : this(foreground, logger, TimeProvider.System) { }

    internal LocalBackgroundHistoryCompactor(
        IHistoryCompactor foreground, ILogger<LocalBackgroundHistoryCompactor>? logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(foreground);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!foreground.SupportedModes.Contains(HistoryCompactionMode.Foreground))
            throw new NotSupportedException(CompactionErrors.Get("UnsupportedMode"));
        _foreground = foreground;
        _logger = logger ?? NullLogger<LocalBackgroundHistoryCompactor>.Instance;
        _timeProvider = timeProvider;
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
        var job = new Job(ticket.SourceBinding, token => _foreground.CompactAsync(foregroundRequest, token));
        if (!_jobs.TryAdd(ticket.JobId, job))
            throw new InvalidOperationException(CompactionErrors.Get("InvalidLifecycle"));

        _ = RetainResultAsync(ticket.JobId, job);
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
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && (job.Task.IsFaulted || job.Task.IsCanceled))
        {
            throw new InvalidOperationException(CompactionErrors.Get("BackgroundJobFailed"), exception);
        }
        finally
        {
            _jobs.TryRemove(ticket.JobId, out _);
        }
    }

    /// <inheritdoc />
    public Task CancelAsync(HistoryCompactionTicket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_jobs.TryGetValue(ticket.JobId, out var job))
            return Task.CompletedTask;
        if (!string.Equals(job.SourceBinding, ticket.SourceBinding, StringComparison.Ordinal))
            throw new InvalidOperationException(CompactionErrors.Get("InvalidLifecycle"));

        if (!_jobs.TryRemove(new KeyValuePair<string, Job>(ticket.JobId, job)))
            return Task.CompletedTask;
        return job.CancelAsync();
    }

    private async Task RetainResultAsync(string jobId, Job job)
    {
        try
        {
            await job.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
            _logger.LogDebug(CompactionErrors.Get("BackgroundLog"), nameof(OperationCanceledException));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(CompactionErrors.Get("BackgroundLog"), exception.GetType().Name);
        }
        finally
        {
            // Retention covers terminal results, never the execution time of an active worker.
            if (_jobs.ContainsKey(jobId))
                await Task.Delay(s_resultRetention, _timeProvider).ConfigureAwait(false);
            _jobs.TryRemove(jobId, out _);
            try
            {
                await job.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(CompactionErrors.Get("BackgroundLog"), $"Cancellation/{exception.GetType().Name}");
            }
        }
    }

    private sealed class Job
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private Task? _cancellationRequest;
        private bool _disposed;

        internal Job(string sourceBinding, Func<CancellationToken, Task<HistoryCompactionResult>> execute)
        {
            SourceBinding = sourceBinding;
            var token = _cancellation.Token;
            Task = System.Threading.Tasks.Task.Run(() => execute(token));
        }

        internal string SourceBinding { get; }
        internal Task<HistoryCompactionResult> Task { get; }
        internal bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        internal Task CancelAsync()
        {
            lock (_gate)
                return _disposed ? System.Threading.Tasks.Task.CompletedTask
                    : _cancellationRequest ??= _cancellation.CancelAsync();
        }

        internal async Task DisposeAsync()
        {
            Task? request;
            lock (_gate)
            {
                _disposed = true;
                request = _cancellationRequest;
            }
            try
            {
                if (request is not null)
                    await request.ConfigureAwait(false);
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }
}
