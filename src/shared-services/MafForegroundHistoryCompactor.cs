#pragma warning disable MAAI001 // The installed MAF compaction API is experimental.

using System.Collections.Frozen;
using System.Diagnostics;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharedServices;

/// <summary>Executes an injected MAF strategy on detached, invocation-local foreground history.</summary>
/// <remarks>
/// No persistence or session state is accessed. The injected strategy/client must be concurrency-safe
/// when registered as a singleton. The public ad-hoc MAF helper creates an invocation-local index
/// using MAF's default content-token estimate (bytes / 4 per group). It has no tokenizer parameter.
/// Neither those estimates nor MAF's content-byte metrics replace the complete JSON safety budget.
/// No stateful CompactionProvider instance is installed in an agent's context pipeline.
/// </remarks>
public sealed class MafForegroundHistoryCompactor : IHistoryCompactor
{
    private static readonly FrozenSet<HistoryCompactionMode> s_supportedModes =
        new[] { HistoryCompactionMode.Foreground }.ToFrozenSet();
    private readonly CompactionStrategy _strategy;
    private readonly ILogger<MafForegroundHistoryCompactor> _logger;

    /// <summary>Creates an adapter for a real MAF strategy or pipeline and optional logger.</summary>
    public MafForegroundHistoryCompactor(
        CompactionStrategy strategy,
        ILogger<MafForegroundHistoryCompactor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategy = strategy;
        _logger = logger ?? NullLogger<MafForegroundHistoryCompactor>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlySet<HistoryCompactionMode> SupportedModes => s_supportedModes;

    /// <inheritdoc />
    public async Task<HistoryCompactionResult> CompactAsync(
        HistoryCompactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SupportedModes.Contains(request.Options.Mode))
        {
            throw new NotSupportedException(CompactionErrors.Get("UnsupportedMode"));
        }
        request.Options.Validate();

        using var timeout = request.Options.Timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        timeout?.CancelAfter(request.Options.Timeout.GetValueOrDefault());
        var operationToken = timeout?.Token ?? cancellationToken;
        var stopwatch = Stopwatch.StartNew();
        var finished = false;
        try
        {
            operationToken.ThrowIfCancellationRequested();
            var sourceJson = HistoryCompactionValidation.Serialize(request.Messages);
            // Two independent round-trips isolate both the validation baseline and the strategy's
            // mutable contents, nested tool arguments/results, annotations and additional metadata.
            var source = HistoryCompactionValidation.Detach(sourceJson);
            var working = HistoryCompactionValidation.Detach(sourceJson);
            HistoryCompactionValidation.ValidateSourceForCompaction(source);
            operationToken.ThrowIfCancellationRequested();

            var strategyLogger = new StrategyLogger(_logger);
            var compactedMessages = await CompactionProvider.CompactAsync(
                _strategy, working, strategyLogger, operationToken).ConfigureAwait(false);
            // Await the actual cooperative completion, even during cancellation cleanup. Do not use
            // WaitAsync/WhenAny: those could abandon a strategy still mutating its working history.
            operationToken.ThrowIfCancellationRequested();
            var candidate = HistoryCompactionValidation.Detach(HistoryCompactionValidation.Serialize(compactedMessages.ToArray()));
            var unchanged = HistoryCompactionValidation.Equivalent(source, candidate);
            HistoryCompactionResult result;
            if (unchanged)
            {
                result = new(HistoryCompactionStatus.Unchanged, request.SourceBinding, source, sourceJson.LongLength, sourceJson.LongLength);
            }
            else
            {
                if (candidate.Count == 0)
                {
                    throw new HistoryCompactionValidationException(HistoryCompactionFailureReason.EmptyCandidate);
                }
                result = new(HistoryCompactionStatus.Completed, request.SourceBinding, candidate,
                    sourceJson.LongLength, HistoryCompactionValidation.Measure(candidate));
            }

            HistoryCompactionValidation.ValidateResult(request, result);
            operationToken.ThrowIfCancellationRequested();
            _logger.LogInformation(CompactionErrors.Get("OutcomeLog"), result.Status,
                result.BeforeUtf8Bytes, result.AfterUtf8Bytes, stopwatch.Elapsed.TotalMilliseconds,
                !unchanged, strategyLogger.HasWarning);
            finished = true;
            return result;
        }
        catch (HistoryCompactionValidationException exception)
        {
            _logger.LogWarning(CompactionErrors.Get("ValidationLog"), exception.Reason);
            throw;
        }
        finally
        {
            // Also record failures from arbitrary plugins without swallowing exceptions or logging
            // their possibly sensitive messages, source binding, agent identity or history contents.
            if (!finished)
            {
                _logger.LogWarning(CompactionErrors.Get("FailureLog"), operationToken.IsCancellationRequested,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
    }

    private sealed class StrategyLogger(ILogger logger) : ILogger
    {
        private int _hasWarning;
        internal bool HasWarning => Volatile.Read(ref _hasWarning) != 0;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Capture warnings even when the configured logger filters them out: a swallowed MAF model
        // failure must not be described as "not needed" in the final operation diagnostics.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning || logger.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Interlocked.Exchange(ref _hasWarning, 1);
            }

            // MAF's summarization-failed event embeds the model exception's message. Preserve the
            // event id/severity, not that text (which may contain a prompt, credentials or user data).
            logger.Log(logLevel, eventId, CompactionErrors.Get("StrategyLog"), eventId.Id);
        }
    }
}
