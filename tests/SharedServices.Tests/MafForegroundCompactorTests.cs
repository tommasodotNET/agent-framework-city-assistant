#pragma warning disable MAAI001 // Exercise the real installed MAF compaction strategies.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SharedServices;

namespace SharedServices.Tests;

public class MafForegroundCompactorTests
{
    [Fact]
    public void SupportsOnlyForeground()
    {
        var compactor = new MafForegroundHistoryCompactor(new TruncationCompactionStrategy(CompactionTriggers.Never));

        Assert.Equal(new[] { HistoryCompactionMode.Foreground }, compactor.SupportedModes);
    }

    [Fact]
    public void NullStrategyIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MafForegroundHistoryCompactor(null!));
    }

    [Fact]
    public async Task NullRequestIsRejected()
    {
        var compactor = new MafForegroundHistoryCompactor(new TruncationCompactionStrategy(CompactionTriggers.Never));

        await Assert.ThrowsAsync<ArgumentNullException>(() => compactor.CompactAsync(null!));
    }

    [Fact]
    public async Task BackgroundIsExplicitlyRejectedByTheForegroundImplementation()
    {
        var request = new HistoryCompactionRequest("agent", "source", History(),
            new HistoryCompactionOptions
            {
                Mode = HistoryCompactionMode.Background,
                CompactorKey = "foreground-test"
            });
        var compactor = new MafForegroundHistoryCompactor(new TruncationCompactionStrategy(CompactionTriggers.Never));
        await Assert.ThrowsAsync<NotSupportedException>(() => compactor.CompactAsync(request));
    }

    [Fact]
    public async Task TriggerBelowThresholdReturnsUnchanged()
    {
        var compactor = new MafForegroundHistoryCompactor(
            new TruncationCompactionStrategy(CompactionTriggers.MessagesExceed(10), minimumPreservedGroups: 2));

        var result = await compactor.CompactAsync(Request(History()));

        Assert.Equal(HistoryCompactionStatus.Unchanged, result.Status);
    }

    [Fact]
    public async Task TruncationPreservesSystemAndMostRecentGroups()
    {
        var history = History();
        var compactor = new MafForegroundHistoryCompactor(
            new TruncationCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2));

        var result = await compactor.CompactAsync(Request(history));

        Assert.Equal(new[] { "instructions", "recent question", "recent answer" },
            result.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task SuccessfulCompactionDoesNotMutateOriginal()
    {
        var history = History();
        var before = JsonSerializer.Serialize(history);
        var compactor = new MafForegroundHistoryCompactor(
            new TruncationCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2));

        await compactor.CompactAsync(Request(history));

        Assert.Equal(before, JsonSerializer.Serialize(history));
    }

    [Fact]
    public async Task UnchangedResultContainsDetachedMessagesAndContents()
    {
        var history = History();
        var compactor = new MafForegroundHistoryCompactor(new TruncationCompactionStrategy(CompactionTriggers.Never));
        var result = await compactor.CompactAsync(Request(history));

        ((TextContent)result.Messages[0].Contents[0]).Text = "changed by caller";

        Assert.Equal("instructions", history[0].Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameCountShorterContentsAreAcceptedRegardlessOfStrategyBoolean(bool reportedChange)
    {
        var history = History();
        var strategy = new TestStrategy((index, _) =>
        {
            ((TextContent)index.Groups[1].Messages[0].Contents[0]).Text = "short";
            return ValueTask.FromResult(reportedChange);
        });

        var result = await new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(history));

        Assert.Equal((HistoryCompactionStatus.Completed, history.Count, "short"),
            (result.Status, result.Messages.Count, result.Messages[1].Text));
    }

    [Fact]
    public async Task StrategyTrueWithoutGenuineChangeReturnsUnchanged()
    {
        var strategy = new TestStrategy((_, _) => ValueTask.FromResult(true));

        var result = await new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(History()));

        Assert.Equal(HistoryCompactionStatus.Unchanged, result.Status);
    }

    [Fact]
    public async Task MutatingFailedStrategyLeavesNestedToolArgumentsUnchanged()
    {
        var history = ToolHistory();
        var original = JsonSerializer.Serialize(history);
        var strategy = new TestStrategy((index, _) =>
        {
            var call = index.Groups.SelectMany(group => group.Messages)
                .SelectMany(message => message.Contents).OfType<FunctionCallContent>().Single();
            call.Arguments!.Clear();
            ((TextContent)index.Groups[1].Messages[0].Contents[0]).Text = "mutated";
            throw new InvalidOperationException("deterministic plugin failure");
        });

        await Record.ExceptionAsync(() => new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(history)));

        Assert.Equal(original, JsonSerializer.Serialize(history));
    }

    [Fact]
    public async Task StrategyFailureIsPropagated()
    {
        var strategy = new TestStrategy((_, _) => throw new InvalidOperationException("deterministic failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(History())));
    }

    [Fact]
    public async Task ReductionReportsTheExactCandidateSize()
    {
        var history = History();
        var expected = new[] { history[0], history[3], history[4] };
        var size = HistoryCompactionValidation.Measure(expected);
        var compactor = new MafForegroundHistoryCompactor(
            new TruncationCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2));

        var result = await compactor.CompactAsync(Request(history));

        Assert.Equal((HistoryCompactionStatus.Completed, size), (result.Status, result.AfterUtf8Bytes));
    }

    [Fact]
    public async Task UnchangedResultReportsEqualMeasuredSizes()
    {
        var history = History();
        var size = HistoryCompactionValidation.Measure(history);
        var compactor = new MafForegroundHistoryCompactor(new TruncationCompactionStrategy(CompactionTriggers.Never));

        var result = await compactor.CompactAsync(Request(history));

        Assert.Equal((size, size), (result.BeforeUtf8Bytes, result.AfterUtf8Bytes));
    }

    [Fact]
    public async Task MinimumPreservedGroupsCanPreventReachingTheStrategyTarget()
    {
        var history = History();
        var compactor = new MafForegroundHistoryCompactor(
            new TruncationCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2,
                target: CompactionTriggers.Never));

        var result = await compactor.CompactAsync(Request(history));

        Assert.Equal(HistoryCompactionStatus.Completed, result.Status);
        Assert.Equal(new[] { "instructions", "recent question", "recent answer" },
            result.Messages.Select(message => message.Text));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdapterDiagnosticsMeasureCompleteBeforeAndAfterHistory(bool measureBefore)
    {
        var history = History();
        history[3].AdditionalProperties = new() { ["metadata"] = new string('m', 800) };
        var compactor = new MafForegroundHistoryCompactor(
            new TruncationCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2));

        var result = await compactor.CompactAsync(Request(history));

        Assert.Equal(HistoryCompactionValidation.Measure(measureBefore ? history : result.Messages),
            measureBefore ? result.BeforeUtf8Bytes : result.AfterUtf8Bytes);
    }

    [Fact]
    public async Task ByteDiagnosticsIncludeRetainedMetadataNotOnlyMafContentMetrics()
    {
        var history = History();
        history[4].AdditionalProperties = new() { ["retained-metadata"] = new string('m', 10_000) };
        var compactor = new MafForegroundHistoryCompactor(
            new TruncationCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2));

        var result = await compactor.CompactAsync(Request(history));

        Assert.True(result.AfterUtf8Bytes > 10_000);
        Assert.Equal(HistoryCompactionValidation.Measure(result.Messages), result.AfterUtf8Bytes);
    }

    [Fact]
    public async Task NonreducingChangedCandidateIsRejected()
    {
        var strategy = new TestStrategy((index, _) =>
        {
            index.AddGroup(CompactionGroupKind.AssistantText, [new(ChatRole.Assistant, new string('x', 200))]);
            return ValueTask.FromResult(true);
        });

        var error = await Assert.ThrowsAsync<HistoryCompactionValidationException>(() =>
            new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(History())));

        Assert.Equal(HistoryCompactionFailureReason.NotReduced, error.Reason);
    }

    [Fact]
    public async Task EmptyCandidateIsRejected()
    {
        var strategy = new TestStrategy((index, _) =>
        {
            index.Groups.Clear();
            return ValueTask.FromResult(true);
        });

        var error = await Assert.ThrowsAsync<HistoryCompactionValidationException>(() =>
            new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(History())));

        Assert.Equal(HistoryCompactionFailureReason.EmptyCandidate, error.Reason);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("mutate")]
    [InlineData("promote")]
    public async Task MaliciousSystemChangesAreRejected(string attack)
    {
        var strategy = new TestStrategy((index, _) =>
        {
            ((TextContent)index.Groups[1].Messages[0].Contents[0]).Text = "short";
            ApplyInstructionAttack(index, attack);
            return ValueTask.FromResult(true);
        });

        var error = await Assert.ThrowsAsync<HistoryCompactionValidationException>(() =>
            new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(History())));

        Assert.Equal(HistoryCompactionFailureReason.ProtectedMessagesChanged, error.Reason);
    }

    [Fact]
    public async Task DeveloperInstructionsAreProtected()
    {
        var history = History();
        history.Insert(1, new(new ChatRole("developer"), "developer instructions"));
        var strategy = new TestStrategy((index, _) =>
        {
            index.Groups[1].IsExcluded = true;
            return ValueTask.FromResult(true);
        });

        var error = await Assert.ThrowsAsync<HistoryCompactionValidationException>(() =>
            new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(history)));

        Assert.Equal(HistoryCompactionFailureReason.ProtectedMessagesChanged, error.Reason);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("developer")]
    public async Task InterleavedInstructionsAreRejectedBeforeStrategyExecution(string role)
    {
        var history = History();
        history.Insert(2, new(new ChatRole(role), "interleaved instruction"));
        var calls = 0;
        var strategy = new TestStrategy((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(false);
        });
        var original = JsonSerializer.Serialize(history);

        var error = await Assert.ThrowsAsync<HistoryCompactionValidationException>(() =>
            new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(history)));

        Assert.Equal(HistoryCompactionFailureReason.ProtectedMessagesChanged, error.Reason);
        Assert.Equal(0, calls);
        Assert.Equal(original, JsonSerializer.Serialize(history));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemPrefixRemainsCompactableWithBuiltInStrategies(bool summarize)
    {
        var history = History();
        using var client = new SummaryClient(_ => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Old conversation summary."))));
        CompactionStrategy strategy = summarize
            ? new SummarizationCompactionStrategy(client, CompactionTriggers.Always, minimumPreservedGroups: 2)
            : new TruncationCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2);

        var result = await new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(history));

        Assert.Equal(HistoryCompactionStatus.Completed, result.Status);
        Assert.True(HistoryCompactionValidation.Equivalent(history.Take(1).ToArray(), result.Messages.Take(1).ToArray()));
    }

    [Fact]
    public async Task BuiltInToolResultStrategyCollapsesWholeCompletedExchange()
    {
        var compactor = new MafForegroundHistoryCompactor(
            new ToolResultCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2));

        var result = await compactor.CompactAsync(Request(ToolHistory()));

        Assert.Equal(new[] { "instructions", new string('q', 500), "[Tool Calls]\nlookup:\n  - answer",
            "recent question", "recent answer" }, result.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task TruncationRemovesCallsAndResultsTogether()
    {
        var compactor = new MafForegroundHistoryCompactor(
            new TruncationCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2));

        var result = await compactor.CompactAsync(Request(ToolHistory()));

        Assert.DoesNotContain(result.Messages.SelectMany(message => message.Contents),
            content => content is ToolCallContent or ToolResultContent);
    }

    [Fact]
    public async Task RecentToolGroupPreservesContentTypesMetadataRolesAndArguments()
    {
        var history = ToolHistory();
        history.RemoveRange(history.Count - 2, 2);
        var compactor = new MafForegroundHistoryCompactor(
            new TruncationCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 1));

        var result = await compactor.CompactAsync(Request(history));

        Assert.Equal(JsonSerializer.Serialize(new[] { history[2], history[3] }),
            JsonSerializer.Serialize(result.Messages.Skip(1).ToArray()));
    }

    [Theory]
    [InlineData("drop-result")]
    [InlineData("drop-call")]
    [InlineData("change-arguments")]
    [InlineData("approve-call")]
    public async Task MaliciousToolGroupChangesAreRejected(string attack)
    {
        var strategy = new TestStrategy((index, _) =>
        {
            ((TextContent)index.Groups[1].Messages[0].Contents[0]).Text = "short";
            ApplyToolAttack(index, attack);
            return ValueTask.FromResult(true);
        });

        var error = await Assert.ThrowsAsync<HistoryCompactionValidationException>(() =>
            new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(ToolHistory())));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
    }

    [Fact]
    public async Task ParallelCallsCannotLoseOneCompletedPair()
    {
        var history = ToolHistory();
        history[2].Contents.Add(new FunctionCallContent("second", "lookup", new Dictionary<string, object?>()));
        history[3].Contents.Add(new FunctionResultContent("second", "second answer"));
        var strategy = new TestStrategy((index, _) =>
        {
            index.Groups[1].IsExcluded = true;
            var messages = index.Groups[2].Messages;
            messages[0].Contents.RemoveAt(1);
            messages[1].Contents.RemoveAt(1);
            return ValueTask.FromResult(true);
        });

        var error = await Assert.ThrowsAsync<HistoryCompactionValidationException>(() =>
            new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(history)));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
    }

    [Theory]
    [InlineData("pending-call")]
    [InlineData("pending-approval")]
    [InlineData("pending-response")]
    [InlineData("orphan-result")]
    public async Task UnsafeSourceIsExplicitlyDeferred(string condition)
    {
        var compactor = new MafForegroundHistoryCompactor(
            new TruncationCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2));

        var error = await Assert.ThrowsAsync<HistoryCompactionValidationException>(() =>
            compactor.CompactAsync(Request(UnsafeHistory(condition))));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
    }

    [Fact]
    public async Task PendingApprovalNeverReachesArbitraryStrategy()
    {
        var executions = 0;
        var strategy = new TestStrategy((_, _) =>
        {
            Interlocked.Increment(ref executions);
            return ValueTask.FromResult(true);
        });

        await Record.ExceptionAsync(() =>
            new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(UnsafeHistory("pending-approval"))));

        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task InformationalFlagWithoutCompletedExchangeDoesNotProveApprovalConsumed()
    {
        var history = History();
        var call = new FunctionCallContent("consumed", "lookup", null) { InformationalOnly = true };
        history.Insert(2, new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", call)]));
        var strategy = new TestStrategy((index, _) =>
        {
            index.Groups[2].IsExcluded = true;
            return ValueTask.FromResult(true);
        });

        var error = await Assert.ThrowsAsync<HistoryCompactionValidationException>(() =>
            new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(history)));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
    }

    [Fact]
    public async Task DeterministicSummaryUsesAssistantRoleAndPreservesRecentHistory()
    {
        using var client = new SummaryClient(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The favorite city is Rome."))));
        var compactor = new MafForegroundHistoryCompactor(
            new SummarizationCompactionStrategy(client, CompactionTriggers.Always, minimumPreservedGroups: 2));

        var result = await compactor.CompactAsync(Request(History()));

        Assert.Equal(new[] { (ChatRole.System, "instructions"),
            (ChatRole.Assistant, "[Summary]\nThe favorite city is Rome."),
            (ChatRole.User, "recent question"), (ChatRole.Assistant, "recent answer") },
            result.Messages.Select(message => (message.Role, message.Text)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\r\n")]
    [InlineData("[Summary unavailable]")]
    public async Task BlankOrUnavailableBuiltInSummaryIsNotPersistable(string summary)
    {
        using var client = new SummaryClient(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, summary))));
        var compactor = new MafForegroundHistoryCompactor(
            new SummarizationCompactionStrategy(client, CompactionTriggers.Always, minimumPreservedGroups: 2));

        var error = await Assert.ThrowsAsync<HistoryCompactionValidationException>(() => compactor.CompactAsync(Request(History())));

        Assert.Equal(HistoryCompactionFailureReason.InvalidSummary, error.Reason);
    }

    [Fact]
    public async Task FailedBuiltInSummaryReturnsUnchangedRatherThanNotNeeded()
    {
        using var client = FailingSummaryClient();
        var compactor = new MafForegroundHistoryCompactor(
            new SummarizationCompactionStrategy(client, CompactionTriggers.Always, minimumPreservedGroups: 2));

        var result = await compactor.CompactAsync(Request(History()));

        Assert.Equal(HistoryCompactionStatus.Unchanged, result.Status);
    }

    [Theory]
    [InlineData("[Summary]")]
    [InlineData("[Summary unavailable]")]
    [InlineData("[Summary]\nExplanation quoting [Summary unavailable]")]
    public async Task NativeNoOpDoesNotTreatOrdinaryReplyAsGeneratedSummary(string text)
    {
        var history = History();
        history[^1] = new(ChatRole.Assistant, text);
        var compactor = new MafForegroundHistoryCompactor(
            new TruncationCompactionStrategy(CompactionTriggers.Never));

        var result = await compactor.CompactAsync(Request(history));

        Assert.Equal(HistoryCompactionStatus.Unchanged, result.Status);
        Assert.Equal(text, result.Messages[^1].Text);
    }

    [Fact]
    public async Task FailedBuiltInSummaryPreservesWarningEvidence()
    {
        using var client = FailingSummaryClient();
        var logger = new RecordingLogger();
        var compactor = new MafForegroundHistoryCompactor(
            new SummarizationCompactionStrategy(client, CompactionTriggers.Always, minimumPreservedGroups: 2), logger: logger);

        await compactor.CompactAsync(Request(History()));

        Assert.Contains(logger.Entries, entry => entry.Level >= LogLevel.Warning && entry.EventId.Id != 0);
    }

    [Fact]
    public async Task FailedBuiltInSummaryDoesNotLogModelErrorTextOrSourceBinding()
    {
        using var client = FailingSummaryClient();
        var logger = new RecordingLogger();
        var compactor = new MafForegroundHistoryCompactor(
            new SummarizationCompactionStrategy(client, CompactionTriggers.Always, minimumPreservedGroups: 2), logger: logger);

        await compactor.CompactAsync(Request(History()));

        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains("private-model-error", StringComparison.Ordinal)
            || entry.Message.Contains("opaque-source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuiltInPipelineComposesToolCollapseSummaryAndTruncation()
    {
        using var client = new SummaryClient(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Lookup completed."))));
        var pipeline = new PipelineCompactionStrategy(
        [
            new ToolResultCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2),
            new SummarizationCompactionStrategy(client, CompactionTriggers.Always, minimumPreservedGroups: 2),
            new TruncationCompactionStrategy(CompactionTriggers.MessagesExceed(4), minimumPreservedGroups: 3)
        ]);

        var result = await new MafForegroundHistoryCompactor(pipeline).CompactAsync(Request(ToolHistory()));

        Assert.Equal(new[] { "instructions", "[Summary]\nLookup completed.", "recent question", "recent answer" },
            result.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task NoOpBuiltInPipelineReturnsUnchanged()
    {
        using var client = FailingSummaryClient();
        var pipeline = new PipelineCompactionStrategy(
        [
            new ToolResultCompactionStrategy(CompactionTriggers.Never),
            new SummarizationCompactionStrategy(client, CompactionTriggers.Never),
            new TruncationCompactionStrategy(CompactionTriggers.Never)
        ]);

        var result = await new MafForegroundHistoryCompactor(pipeline).CompactAsync(Request(History()));

        Assert.Equal(HistoryCompactionStatus.Unchanged, result.Status);
    }

    [Fact]
    public async Task ConcurrentRequestsDoNotShareIndexesOrContents()
    {
        const int count = 8;
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var strategy = new TestStrategy(async (index, cancellationToken) =>
        {
            var message = index.Groups[1].Messages[0];
            var tag = message.MessageId;
            Interlocked.Increment(ref entered);
            if (Volatile.Read(ref entered) == count)
            {
                allEntered.TrySetResult();
            }
            await allEntered.Task.WaitAsync(cancellationToken);
            ((TextContent)message.Contents[0]).Text = tag;
            return true;
        });
        var compactor = new MafForegroundHistoryCompactor(strategy);
        var requests = Enumerable.Range(0, count).Select(number =>
        {
            var history = History();
            history[1].MessageId = $"call-{number}";
            return Request(history, timeout: TimeSpan.FromSeconds(10), binding: $"binding-{number}");
        }).ToArray();

        var results = await Task.WhenAll(requests.Select(request => compactor.CompactAsync(request)));

        Assert.Equal(Enumerable.Range(0, count).Select(number => ($"binding-{number}", $"call-{number}", new string('q', 500))),
            results.Select((result, number) => (result.SourceBinding, result.Messages[1].Text, requests[number].Messages[1].Text)));
    }

    [Fact]
    public async Task AlreadyCancelledRequestIsPropagatedEvenWhenTriggerWouldSkip()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var compactor = new MafForegroundHistoryCompactor(new TruncationCompactionStrategy(CompactionTriggers.Never));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compactor.CompactAsync(Request(History()), cancellation.Token));
    }

    [Fact]
    public async Task TimeoutCancelsRealSummaryClient()
    {
        using var client = new SummaryClient(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreachable"));
        });
        var compactor = new MafForegroundHistoryCompactor(
            new SummarizationCompactionStrategy(client, CompactionTriggers.Always, minimumPreservedGroups: 2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            compactor.CompactAsync(Request(History(), timeout: TimeSpan.FromMilliseconds(50))));
    }

    [Fact]
    public async Task CallerCancellationReachesRealSummaryClient()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new SummaryClient(async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreachable"));
        });
        var compactor = new MafForegroundHistoryCompactor(
            new SummarizationCompactionStrategy(client, CompactionTriggers.Always, minimumPreservedGroups: 2));
        var operation = compactor.CompactAsync(Request(History()), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task TimeoutWaitsForActualCooperativeStrategyCleanup()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var strategy = new TestStrategy(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return false;
            }
            finally
            {
                cancelled.SetResult();
                await cleanup.Task;
            }
        });
        var operation = new MafForegroundHistoryCompactor(strategy)
            .CompactAsync(Request(History(), timeout: TimeSpan.FromMilliseconds(50)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var returnedBeforeCleanup = operation.IsCompleted;
        cleanup.SetResult();

        await Record.ExceptionAsync(() => operation);

        Assert.False(returnedBeforeCleanup);
    }

    [Fact]
    public async Task StrategyReturningAfterCancellationCannotReturnSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var strategy = new TestStrategy((_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(false);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(History()), cancellation.Token));
    }

    [Fact]
    public async Task MultimodalContentsSurviveDetachment()
    {
        var history = History();
        history[4].Contents.Add(new DataContent(new byte[] { 1, 2, 3 }, "image/png"));
        history[4].Contents.Add(new UriContent(new Uri("https://example.invalid/image.png"), "image/png"));
        history[4].Contents.Add(new TextReasoningContent("reasoning") { ProtectedData = "opaque reasoning" });
        history[4].AuthorName = "assistant";
        history[4].MessageId = "multimodal";
        history[4].CreatedAt = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
        history[4].AdditionalProperties = new() { ["details"] = new Dictionary<string, object?> { ["flag"] = true } };

        var result = await new MafForegroundHistoryCompactor(new TruncationCompactionStrategy(CompactionTriggers.Never))
            .CompactAsync(Request(history));

        Assert.Equal(JsonSerializer.Serialize(history), JsonSerializer.Serialize(result.Messages));
    }

    [Fact]
    public async Task OutOfOrderContentDiscriminatorAndSummaryFlagAreRecognized()
    {
        var history = JsonSerializer.Deserialize<List<ChatMessage>>(
            """
            [
              {"Role":"system","Contents":[{"Text":"instructions","$type":"text"}]},
              {"Role":"assistant","Contents":[{"Text":"[Summary]\nold summary","$type":"text"}],"AdditionalProperties":{"_is_summary":true}},
              {"Role":"user","Contents":[{"Text":"recent question","$type":"text"}]},
              {"Role":"assistant","Contents":[{"Text":"recent answer","$type":"text"}]}
            ]
            """, new JsonSerializerOptions { AllowOutOfOrderMetadataProperties = true })!;
        var seenSummaryKind = false;
        var strategy = new TestStrategy((index, _) =>
        {
            seenSummaryKind = index.Groups[1].Kind == CompactionGroupKind.Summary;
            return ValueTask.FromResult(false);
        });

        await new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(history));

        Assert.True(seenSummaryKind);
    }

    [Fact]
    public async Task PublicHelperUsesDocumentedPerGroupByteEstimate()
    {
        var actual = Array.Empty<int>();
        var expected = Array.Empty<int>();
        var strategy = new TestStrategy((index, _) =>
        {
            actual = index.Groups.Select(group => group.TokenCount).ToArray();
            expected = index.Groups.Select(group => group.ByteCount / 4).ToArray();
            return ValueTask.FromResult(false);
        });

        await new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(History()));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task PublicHelperUsesMafDefaultTokenizerConfiguration()
    {
        var indexUsedDefaultTokenizer = false;
        var strategy = new TestStrategy((index, _) =>
        {
            indexUsedDefaultTokenizer = index.Tokenizer is null;
            return ValueTask.FromResult(false);
        });

        await new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(History()));

        Assert.True(indexUsedDefaultTokenizer);
    }

    private static void ApplyInstructionAttack(CompactionMessageIndex index, string attack)
    {
        switch (attack)
        {
            case "remove":
                index.Groups[0].IsExcluded = true;
                break;
            case "mutate":
                ((TextContent)index.Groups[0].Messages[0].Contents[0]).Text = "replacement";
                break;
            case "promote":
                index.Groups[2].Messages[0].Role = ChatRole.System;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(attack));
        }
    }

    private static void ApplyToolAttack(CompactionMessageIndex index, string attack)
    {
        var group = index.Groups[2];
        switch (attack)
        {
            case "drop-result":
            case "drop-call":
                group.IsExcluded = true;
                var retained = attack == "drop-result" ? group.Messages[0] : group.Messages[1];
                index.InsertGroup(2, CompactionGroupKind.ToolCall, [retained]);
                break;
            case "change-arguments":
                ((FunctionCallContent)group.Messages[0].Contents[0]).Arguments!.Clear();
                break;
            case "approve-call":
                ((FunctionCallContent)group.Messages[0].Contents[0]).InformationalOnly = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(attack));
        }
    }

    private static List<ChatMessage> ToolHistory() =>
    [
        new(ChatRole.System, "instructions"),
        new(ChatRole.User, new string('q', 500)),
        new(ChatRole.Assistant, [new FunctionCallContent("lookup-1", "lookup",
            new Dictionary<string, object?> { ["city"] = new Dictionary<string, object?> { ["name"] = "Rome" } })])
        {
            MessageId = "tool-call",
            AdditionalProperties = new() { ["provider-metadata"] = "must survive" }
        },
        new(ChatRole.Tool, [new FunctionResultContent("lookup-1", "answer")]),
        new(ChatRole.User, "recent question"),
        new(ChatRole.Assistant, "recent answer")
    ];

    private static List<ChatMessage> UnsafeHistory(string condition)
    {
        var history = History();
        var call = new FunctionCallContent("pending", "lookup", null);
        history.Add(condition switch
        {
            "pending-call" => new(ChatRole.Assistant, [call]),
            "pending-approval" => new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", call)]),
            "pending-response" => new(ChatRole.User, [new ToolApprovalResponseContent("approval", true, call)]),
            "orphan-result" => new(ChatRole.Tool, [new FunctionResultContent("unknown", "result")]),
            _ => throw new ArgumentOutOfRangeException(nameof(condition))
        });
        return history;
    }

    private static SummaryClient FailingSummaryClient() =>
        new(_ => Task.FromException<ChatResponse>(new InvalidOperationException("private-model-error")));

    private static List<ChatMessage> History() =>
    [
        new(ChatRole.System, "instructions"),
        new(ChatRole.User, new string('q', 500)),
        new(ChatRole.Assistant, new string('a', 500)),
        new(ChatRole.User, "recent question"),
        new(ChatRole.Assistant, "recent answer")
    ];

    private static HistoryCompactionRequest Request(
        IReadOnlyList<ChatMessage> history, TimeSpan? timeout = null,
        string binding = "opaque-source") =>
        new("agent", binding, history, new HistoryCompactionOptions
        {
            CompactorKey = "foreground-test",
            Timeout = timeout
        });

    private sealed class TestStrategy(
        Func<CompactionMessageIndex, CancellationToken, ValueTask<bool>> compact) : CompactionStrategy(CompactionTriggers.Always)
    {
        protected override ValueTask<bool> CompactCoreAsync(
            CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken) =>
            compact(index, cancellationToken);
    }

    private sealed class SummaryClient(Func<CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            respond(cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class RecordingLogger : ILogger<MafForegroundHistoryCompactor>
    {
        internal ConcurrentQueue<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, eventId, formatter(state, exception)));
    }
}
