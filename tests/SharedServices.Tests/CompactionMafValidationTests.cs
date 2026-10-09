#pragma warning disable MAAI001 // Test validation of installed MAF summary metadata.

using System.Text.Json;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using SharedServices;

namespace SharedServices.Tests;

public class CompactionMafValidationTests
{
    [Fact]
    public void MeasureUsesExactContractSerializationIncludingMetadata()
    {
        IReadOnlyList<ChatMessage> messages =
        [
            new(ChatRole.User, "quoted \" text")
            {
                AuthorName = "author",
                MessageId = "id",
                CreatedAt = DateTimeOffset.Parse("2026-10-07T00:00:00Z"),
                AdditionalProperties = new() { ["metadata"] = new string('m', 500) }
            }
        ];

        var bytes = JsonSerializer.SerializeToUtf8Bytes<IReadOnlyList<ChatMessage>>(messages, JsonSerializerOptions.Default);

        Assert.Equal(bytes.LongLength, HistoryCompactionValidation.Measure(messages));
    }

    [Fact]
    public void MeasureRejectsNullList()
    {
        Assert.Throws<ArgumentNullException>(() => HistoryCompactionValidation.Measure(null!));
    }

    [Fact]
    public void MeasureRejectsNullMessage()
    {
        Assert.Throws<ArgumentException>(() => HistoryCompactionValidation.Measure(new ChatMessage[] { null! }));
    }

    [Fact]
    public void MeasureRejectsNullContent()
    {
        var source = Source();
        source[1].Contents.Add(null!);

        Assert.Throws<ArgumentException>(() => HistoryCompactionValidation.Measure(source));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("developer")]
    [InlineData("user")]
    public void OrdinaryInstructionOrUserSummaryTextIsNotFrameworkSummary(string role)
    {
        IReadOnlyList<ChatMessage> source = [new(new ChatRole(role), "[Summary]\n[Summary unavailable]")];

        var error = Record.Exception(() => HistoryCompactionValidation.ValidateFallback(source));

        Assert.Null(error);
    }

    [Fact]
    public void FallbackRejectsNullMessageAndContent()
    {
        Assert.Throws<ArgumentException>(() =>
            HistoryCompactionValidation.ValidateFallback(new ChatMessage[] { null! }));
        var source = Source();
        source[1].Contents.Add(null!);
        Assert.Throws<ArgumentException>(() => HistoryCompactionValidation.ValidateFallback(source));
    }

    [Fact]
    public void CandidateRequiresARealReduction()
    {
        var source = Source();
        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(source, source));

        Assert.Equal(HistoryCompactionFailureReason.NotReduced, error.Reason);
    }

    [Fact]
    public void ShorterJsonSpellingOfTheSameValueIsNotCompaction()
    {
        IReadOnlyList<ChatMessage> source =
        [
            new(ChatRole.User, "unchanged") { AdditionalProperties = new() { ["number"] = JsonSerializer.Deserialize<JsonElement>("1.0000") } }
        ];
        IReadOnlyList<ChatMessage> candidate =
        [
            new(ChatRole.User, "unchanged") { AdditionalProperties = new() { ["number"] = JsonSerializer.Deserialize<JsonElement>("1") } }
        ];
        Assert.True(HistoryCompactionValidation.Measure(candidate) < HistoryCompactionValidation.Measure(source));

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Equal(HistoryCompactionFailureReason.NotReduced, error.Reason);
    }

    [Fact]
    public void SafeOriginalPendingApprovalMayFallbackWithoutBeingApproved()
    {
        var call = new FunctionCallContent("pending", "lookup", null);
        IReadOnlyList<ChatMessage> messages =
        [
            new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", call)])
        ];

        HistoryCompactionValidation.ValidateFallback(messages);

        Assert.False(call.InformationalOnly);
    }

    [Fact]
    public void BindingFromDifferentSourceVersionIsRejected()
    {
        var request = Request();
        var result = Completed(request, Candidate(), binding: "wrong-source");

        var error = Assert.Throws<HistoryCompactionValidationException>(() => HistoryCompactionValidation.ValidateResult(request, result));

        Assert.Equal(HistoryCompactionFailureReason.SourceBindingMismatch, error.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForgedPluginByteDiagnosticsAreRejected(bool forgeBefore)
    {
        var request = Request();
        var candidate = Candidate();
        var before = HistoryCompactionValidation.Measure(request.Messages);
        var after = HistoryCompactionValidation.Measure(candidate);
        var result = new HistoryCompactionResult(HistoryCompactionStatus.Completed, request.SourceBinding, candidate,
            forgeBefore ? before - 1 : before, forgeBefore ? after : after - 1);

        var error = Assert.Throws<HistoryCompactionValidationException>(() => HistoryCompactionValidation.ValidateResult(request, result));

        Assert.Equal(HistoryCompactionFailureReason.InvalidDiagnostics, error.Reason);
    }

    [Fact]
    public void ChangedHistoryCannotBeLabeledUnchangedEvenAtEqualSize()
    {
        var request = Request();
        var modified = Source();
        ((TextContent)modified[1].Contents[0]).Text = new string('x', 500);
        var size = HistoryCompactionValidation.Measure(modified);
        var result = new HistoryCompactionResult(HistoryCompactionStatus.Unchanged, request.SourceBinding, modified, size, size);

        var error = Assert.Throws<HistoryCompactionValidationException>(() => HistoryCompactionValidation.ValidateResult(request, result));

        Assert.Equal(HistoryCompactionFailureReason.InvalidUnchangedResult, error.Reason);
    }

    [Fact]
    public void CompletedPluginResultCannotBypassProviderSystemProtection()
    {
        var request = Request();
        var candidate = Candidate();
        candidate.RemoveAt(0);
        var result = Completed(request, candidate);

        var error = Assert.Throws<HistoryCompactionValidationException>(() => HistoryCompactionValidation.ValidateResult(request, result));

        Assert.Equal(HistoryCompactionFailureReason.ProtectedMessagesChanged, error.Reason);
    }

    [Theory]
    [InlineData("system", 1)]
    [InlineData("system", 2)]
    [InlineData("developer", 1)]
    [InlineData("developer", 2)]
    public void ProtectedInstructionCannotMoveAfterConversationContent(string role, int position)
    {
        var source = Source();
        source[0].Role = new ChatRole(role);
        var candidate = Candidate();
        candidate.RemoveAt(0);
        candidate.Insert(position, source[0]);
        var request = Request(source);

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateResult(request, Completed(request, candidate)));

        Assert.Equal(HistoryCompactionFailureReason.ProtectedMessagesChanged, error.Reason);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("developer")]
    public void InterleavedInstructionsRejectCompactionEvenIfCandidatePreservesThem(string role)
    {
        var source = Source();
        source.Insert(2, new(new ChatRole(role), "interleaved instruction"));
        var candidate = source.Where((_, index) => index != 1).ToArray();

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Equal(HistoryCompactionFailureReason.ProtectedMessagesChanged, error.Reason);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("developer")]
    public void InterleavedInstructionsAllowOriginalFallbackAndUnchangedResult(string role)
    {
        var source = Source();
        source.Insert(2, new(new ChatRole(role), "interleaved instruction"));
        var request = Request(source);
        var size = HistoryCompactionValidation.Measure(source);
        var unchanged = new HistoryCompactionResult(
            HistoryCompactionStatus.Unchanged, request.SourceBinding, source, size, size);

        HistoryCompactionValidation.ValidateFallback(source);
        HistoryCompactionValidation.ValidateResult(request, unchanged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleProtectedInstructionsKeepExactPrefix(bool reversePrefix)
    {
        var source = Source();
        source.Insert(1, new(new ChatRole("developer"), "developer instruction"));
        var prefix = source.Take(2).ToArray();
        var candidate = (reversePrefix ? prefix.Reverse() : prefix)
            .Concat(source.TakeLast(2)).ToArray();
        var request = Request(source);
        var result = Completed(request, candidate);

        if (reversePrefix)
        {
            var error = Assert.Throws<HistoryCompactionValidationException>(() =>
                HistoryCompactionValidation.ValidateResult(request, result));
            Assert.Equal(HistoryCompactionFailureReason.ProtectedMessagesChanged, error.Reason);
        }
        else
        {
            HistoryCompactionValidation.ValidateResult(request, result);
        }
    }

    [Fact]
    public void CompletedPluginResultCannotDropPendingApproval()
    {
        var source = Source();
        source.Insert(2, new(ChatRole.Assistant,
            [new ToolApprovalRequestContent("approval", new FunctionCallContent("pending", "lookup", null))]));
        var request = Request(source);
        var result = Completed(request, Candidate());

        var error = Assert.Throws<HistoryCompactionValidationException>(() => HistoryCompactionValidation.ValidateResult(request, result));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
    }

    [Fact]
    public void CompletedPluginCannotInventToolExecution()
    {
        var request = Request();
        var candidate = Candidate();
        candidate.Add(new(ChatRole.Assistant, [new FunctionCallContent("invented", "lookup", null)]));
        candidate.Add(new(ChatRole.Tool, [new FunctionResultContent("invented", "answer")]));
        var result = Completed(request, candidate);

        var error = Assert.Throws<HistoryCompactionValidationException>(() => HistoryCompactionValidation.ValidateResult(request, result));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
    }

    [Fact]
    public void CompletedPluginCannotRewriteConsumedApprovalAsDenial()
    {
        var source = ExchangeSource();
        var call = new FunctionCallContent("first", "lookup", null) { InformationalOnly = true };
        source.Insert(2, new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", call)]));
        source.Insert(3, new(ChatRole.User, [new ToolApprovalResponseContent("approval", true, call)]));
        var request = Request(source);
        var candidate = source.Where((_, index) => index != 1).ToList();
        candidate[2] = new(ChatRole.User, [new ToolApprovalResponseContent("approval", false, call)]);
        var result = Completed(request, candidate);

        var error = Assert.Throws<HistoryCompactionValidationException>(() => HistoryCompactionValidation.ValidateResult(request, result));

        Assert.Equal(HistoryCompactionFailureReason.ProtectedMessagesChanged, error.Reason);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeniedApprovalsCannotBeCompactedDespiteMatchingCompletedExchange(
        bool informational, bool retainDenial)
    {
        var source = ExchangeSource();
        var call = new FunctionCallContent("first", "lookup", null) { InformationalOnly = informational };
        source.Insert(2, new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", call)]));
        source.Insert(3, new(ChatRole.User, [new ToolApprovalResponseContent("approval", false, call)]));
        var candidate = source.Where((_, index) => index != 1
            && (retainDenial || index is not (2 or 3))).ToArray();

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
        Assert.False(Assert.IsType<ToolApprovalResponseContent>(source[3].Contents[0]).Approved);
        Assert.Equal(informational, call.InformationalOnly);
        HistoryCompactionValidation.ValidateFallback(source);
    }

    [Theory]
    [InlineData("duplicate-call")]
    [InlineData("duplicate-result")]
    [InlineData("unrelated-result")]
    [InlineData("missing-parallel-result")]
    [InlineData("result-before-call")]
    [InlineData("interrupted-exchange")]
    [InlineData("wrong-call-role")]
    [InlineData("wrong-result-role")]
    [InlineData("call-in-result-message")]
    [InlineData("uncorrelated-text-result")]
    [InlineData("reused-call-id")]
    public void MalformedFunctionExchangeCannotBeRewritten(string condition)
    {
        var source = MalformedExchange(condition);

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(source, Candidate()));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
    }

    [Fact]
    public void ParallelResultsAreMatchedByIdRatherThanResultOrder()
    {
        var source = ExchangeSource();
        source[2].Contents.Add(new FunctionCallContent("second", "lookup", null));
        source[3].Contents.Insert(0, new FunctionResultContent("second", "second answer"));
        var candidate = source.Where((_, index) => index != 1).ToArray();

        var error = Record.Exception(() => HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Null(error);
    }

    [Fact]
    public void RetainedExchangesCannotBeReordered()
    {
        var source = ExchangeSource();
        source.Insert(4, new(ChatRole.Assistant, [new FunctionCallContent("second", "lookup", null)]));
        source.Insert(5, new(ChatRole.Tool, [new FunctionResultContent("second", "second answer")]));
        IReadOnlyList<ChatMessage> candidate = [source[0], source[4], source[5], source[2], source[3], source[6], source[7]];

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
    }

    [Fact]
    public void EntireCompletedExchangeMayBeRemovedWithoutAffectingLaterExchange()
    {
        var source = ExchangeSource();
        source.Insert(4, new(ChatRole.Assistant, [new FunctionCallContent("second", "lookup", null)]));
        source.Insert(5, new(ChatRole.Tool, [new FunctionResultContent("second", "second answer")]));
        IReadOnlyList<ChatMessage> candidate = [source[0], source[4], source[5], source[6], source[7]];

        var error = Record.Exception(() => HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Null(error);
    }

    [Fact]
    public void ReasoningAttachedToRetainedExchangeCannotBeDropped()
    {
        var source = ExchangeSource();
        source.Insert(2, new(ChatRole.Assistant, [new TextReasoningContent("reasoning") { ProtectedData = "opaque" }]));
        IReadOnlyList<ChatMessage> candidate = [source[0], source[3], source[4], source[5], source[6]];

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
    }

    [Fact]
    public void ReasoningAroundRetainedExchangeCanBePreserved()
    {
        var source = ExchangeSource();
        source.Insert(2, new(ChatRole.Assistant, [new TextReasoningContent("leading") { ProtectedData = "opaque leading" }]));
        source.Insert(5, new(ChatRole.Assistant, [new TextReasoningContent("trailing") { ProtectedData = "opaque trailing" }]));
        var candidate = source.Where((_, index) => index != 1).ToArray();

        var error = Record.Exception(() => HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Null(error);
    }

    [Theory]
    [InlineData("[Summary unavailable]", false)]
    [InlineData("[Summary]\n[Summary unavailable]", false)]
    [InlineData("[Summary]\n   ", false)]
    [InlineData("   ", true)]
    public void InvalidSummaryCannotBypassProviderByDroppingMetadata(string text, bool flagged)
    {
        var request = Request();
        var candidate = Candidate();
        candidate.Insert(1, new(ChatRole.Assistant, text)
        {
            AdditionalProperties = new() { [CompactionMessageGroup.SummaryPropertyKey] = flagged }
        });
        var result = Completed(request, candidate);

        var error = Assert.Throws<HistoryCompactionValidationException>(() => HistoryCompactionValidation.ValidateResult(request, result));

        Assert.Equal(HistoryCompactionFailureReason.InvalidSummary, error.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("The summary service reported [Summary unavailable].")]
    public void NewUnflaggedAssistantTextMustBeUsefulEvenWithValidRetainedMessages(string text)
    {
        var request = Request();
        var candidate = Candidate();
        candidate.Insert(1, new(ChatRole.Assistant, text));

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateResult(request, Completed(request, candidate)));

        Assert.Equal(HistoryCompactionFailureReason.InvalidSummary, error.Reason);
    }

    [Fact]
    public void RetainedEmptyAssistantTextIsNotGeneratedSummary()
    {
        var source = Source();
        source.Insert(4, new(ChatRole.Assistant, "   ") { MessageId = "old-empty-response" });
        var candidate = new[] { source[0], source[3], source[4], source[5] };

        HistoryCompactionValidation.ValidateCandidate(source, candidate);
        HistoryCompactionValidation.ValidateFallback(candidate);
    }

    [Theory]
    [InlineData("   ", 1, 1, true)]
    [InlineData("   ", 1, 2, false)]
    [InlineData("   ", 1, 5, false)]
    [InlineData("   ", 2, 1, true)]
    [InlineData("   ", 2, 2, true)]
    [InlineData("   ", 2, 3, false)]
    [InlineData("[Summary unavailable]", 1, 1, true)]
    [InlineData("[Summary unavailable]", 1, 2, false)]
    [InlineData("[Summary unavailable]", 1, 5, false)]
    [InlineData("[Summary unavailable]", 2, 1, true)]
    [InlineData("[Summary unavailable]", 2, 2, true)]
    [InlineData("[Summary unavailable]", 2, 3, false)]
    [InlineData("[Summary]", 1, 2, false)]
    public void RetentionExemptionCannotReuseSourceOccurrence(
        string text, int originalCount, int candidateCount, bool accepted)
    {
        var source = Source();
        for (var index = 0; index < originalCount; index++)
            source.Insert(3, new(ChatRole.Assistant, text));
        var candidate = Candidate();
        for (var index = 0; index < candidateCount; index++)
            candidate.Insert(1, new(ChatRole.Assistant, text));
        var before = JsonSerializer.Serialize(new { source, candidate });
        var request = Request(source);
        var result = Completed(request, candidate);

        if (accepted)
            HistoryCompactionValidation.ValidateResult(request, result);
        else
        {
            var error = Assert.Throws<HistoryCompactionValidationException>(() =>
                HistoryCompactionValidation.ValidateResult(request, result));
            Assert.Equal(HistoryCompactionFailureReason.InvalidSummary, error.Reason);
        }

        Assert.Equal(before, JsonSerializer.Serialize(new { source, candidate }));
    }

    [Fact]
    public void ReorderedMetadataDoesNotCreateAdditionalRetentionAllowance()
    {
        var source = Source();
        source.Insert(3, new(ChatRole.Assistant, "   ")
        {
            AdditionalProperties = new() { ["a"] = 1, ["b"] = 2 }
        });
        ChatMessage Retained() => new(ChatRole.Assistant, "   ")
        {
            AdditionalProperties = new() { ["b"] = 2, ["a"] = 1 }
        };
        var candidate = Candidate();
        candidate.Insert(1, Retained());
        HistoryCompactionValidation.ValidateCandidate(source, candidate);
        candidate.Insert(1, Retained());

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Equal(HistoryCompactionFailureReason.InvalidSummary, error.Reason);
    }

    [Fact]
    public void ExactRetentionWithDifferentMessagesUsesSeparateSourceOccurrences()
    {
        var source = Source();
        source.Insert(3, new(ChatRole.Assistant, "   "));
        source.Insert(4, new(ChatRole.Assistant, "[Summary unavailable]"));
        var candidate = Candidate();
        candidate.Insert(1, source[3]);
        candidate.Insert(2, source[4]);

        HistoryCompactionValidation.ValidateCandidate(source, candidate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NontextAssistantContentDoesNotRequireTextSummary(bool reasoning)
    {
        var source = Source();
        var candidate = Candidate();
        candidate.Insert(1, new(ChatRole.Assistant, reasoning
            ? [new TextReasoningContent("reasoning")]
            : [new DataContent(new byte[] { 1 }, "image/png")]));

        HistoryCompactionValidation.ValidateCandidate(source, candidate);
    }

    [Theory]
    [InlineData("[Summary]")]
    [InlineData("[Summary unavailable]")]
    [InlineData("[Summary]\nExplanation quoting [Summary unavailable]")]
    public void OrdinaryAssistantMarkerTextRemainsValidInHistoryAndUnchangedResults(string text)
    {
        var source = Source();
        source[^1] = new(ChatRole.Assistant, text);
        var request = Request(source);
        var size = HistoryCompactionValidation.Measure(source);

        HistoryCompactionValidation.ValidateFallback(source);
        HistoryCompactionValidation.ValidateResult(request,
            new(HistoryCompactionStatus.Unchanged, request.SourceBinding, source, size, size));
    }

    [Theory]
    [InlineData("[Summary]")]
    [InlineData("[Summary unavailable]")]
    [InlineData("[Summary]\nExplanation quoting [Summary unavailable]")]
    public void RetainedOrdinaryMarkerTextIsNotNewCompactorOutput(string text)
    {
        var source = Source();
        source[^1] = new(ChatRole.Assistant, text) { MessageId = "ordinary-response" };
        var candidate = new[] { source[0], source[^2], source[^1] };

        HistoryCompactionValidation.ValidateCandidate(source, candidate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitInvalidSummaryMetadataIsRejectedInCanonicalHistory(bool deserializedFlag)
    {
        var source = new ChatMessage(ChatRole.Assistant, "[Summary unavailable]")
        {
            AdditionalProperties = new()
            {
                [CompactionMessageGroup.SummaryPropertyKey] = deserializedFlag
                    ? JsonSerializer.SerializeToElement(true) : true
            }
        };

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateFallback([source]));

        Assert.Equal(HistoryCompactionFailureReason.InvalidSummary, error.Reason);
    }

    [Theory]
    [InlineData("   ", false)]
    [InlineData("   ", true)]
    [InlineData("[Summary unavailable]", false)]
    [InlineData("[Summary unavailable]", true)]
    public void CompletedCandidateCannotHideInvalidFlaggedSourceSummary(string text, bool deserializedFlag)
    {
        var source = Source();
        source.Insert(2, new(ChatRole.Assistant, text)
        {
            AdditionalProperties = new()
            {
                [CompactionMessageGroup.SummaryPropertyKey] = deserializedFlag
                    ? JsonSerializer.SerializeToElement(true) : true
            }
        });
        var before = JsonSerializer.Serialize(source);
        var request = Request(source);
        var candidate = Candidate();

        var direct = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(source, candidate));
        var result = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateResult(request, Completed(request, candidate)));

        Assert.Equal(HistoryCompactionFailureReason.InvalidSummary, direct.Reason);
        Assert.Equal(HistoryCompactionFailureReason.InvalidSummary, result.Reason);
        Assert.Equal(before, JsonSerializer.Serialize(source));
    }

    [Fact]
    public void ValidFlaggedSourceSummaryMayStillBeRemovedByCompaction()
    {
        var source = Source();
        source.Insert(2, new(ChatRole.Assistant, "[Summary]\nOld facts.")
        {
            AdditionalProperties = new() { [CompactionMessageGroup.SummaryPropertyKey] = true }
        });

        HistoryCompactionValidation.ValidateCandidate(source, Candidate());
    }

    [Fact]
    public void SmallerCandidatePassesIndependentValidation()
    {
        var candidate = Candidate();
        var request = Request();
        var result = Completed(request, candidate);

        var error = Record.Exception(() => HistoryCompactionValidation.ValidateResult(request, result));

        Assert.Null(error);
    }

    [Fact]
    public void ReorderedMetadataIsSemanticallyUnchanged()
    {
        var left = Source();
        left[0].AdditionalProperties = new() { ["a"] = 1, ["b"] = 2 };
        var right = Source();
        right[0].AdditionalProperties = new() { ["b"] = 2, ["a"] = 1 };
        var request = Request(left);
        var size = HistoryCompactionValidation.Measure(left);
        var result = new HistoryCompactionResult(HistoryCompactionStatus.Unchanged, request.SourceBinding, right, size, size);

        var error = Record.Exception(() => HistoryCompactionValidation.ValidateResult(request, result));

        Assert.Null(error);
    }

    [Fact]
    public void EmptyCandidateIsRejectedByReusableValidation()
    {
        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(Source(), Array.Empty<ChatMessage>()));

        Assert.Equal(HistoryCompactionFailureReason.EmptyCandidate, error.Reason);
    }

    [Fact]
    public void WhitespaceOnlyCandidateIsNotUsefulHistory()
    {
        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(Source(), new[] { new ChatMessage(ChatRole.Assistant, "  ") }));

        Assert.Equal(HistoryCompactionFailureReason.EmptyCandidate, error.Reason);
    }

    [Fact]
    public void ValidationDoesNotMutateMessages()
    {
        var source = Source();
        var candidate = Candidate();
        var before = JsonSerializer.Serialize(new { source, candidate });

        HistoryCompactionValidation.ValidateCandidate(source, candidate);

        Assert.Equal(before, JsonSerializer.Serialize(new { source, candidate }));
    }

    [Fact]
    public void UndefinedFailureReasonIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HistoryCompactionValidationException((HistoryCompactionFailureReason)999));
    }

    [Fact]
    public void FailureMessageDoesNotContainOpaqueBinding()
    {
        var request = Request();
        var result = Completed(request, Candidate(), binding: "different-private-binding");
        var error = Assert.Throws<HistoryCompactionValidationException>(() => HistoryCompactionValidation.ValidateResult(request, result));

        Assert.DoesNotContain("binding", error.Message, StringComparison.Ordinal);
    }

    private static List<ChatMessage> Source() =>
    [
        new(ChatRole.System, "instructions"),
        new(ChatRole.User, new string('q', 500)),
        new(ChatRole.Assistant, new string('a', 500)),
        new(ChatRole.User, "latest question"),
        new(ChatRole.Assistant, "latest answer")
    ];

    private static List<ChatMessage> Candidate() =>
    [
        new(ChatRole.System, "instructions"),
        new(ChatRole.User, "latest question"),
        new(ChatRole.Assistant, "latest answer")
    ];

    private static List<ChatMessage> ExchangeSource() =>
    [
        new(ChatRole.System, "instructions"),
        new(ChatRole.User, new string('q', 500)),
        new(ChatRole.Assistant, [new FunctionCallContent("first", "lookup", null)]),
        new(ChatRole.Tool, [new FunctionResultContent("first", "answer")]),
        new(ChatRole.User, "latest question"),
        new(ChatRole.Assistant, "latest answer")
    ];

    private static List<ChatMessage> MalformedExchange(string condition)
    {
        var source = ExchangeSource();
        switch (condition)
        {
            case "duplicate-call":
                source[2].Contents.Add(new FunctionCallContent("first", "lookup", null));
                break;
            case "duplicate-result":
                source[3].Contents.Add(new FunctionResultContent("first", "duplicate"));
                break;
            case "unrelated-result":
                source[3].Contents.Add(new FunctionResultContent("unknown", "unrelated"));
                break;
            case "missing-parallel-result":
                source[2].Contents.Add(new FunctionCallContent("second", "lookup", null));
                break;
            case "result-before-call":
                (source[2], source[3]) = (source[3], source[2]);
                break;
            case "interrupted-exchange":
                source.Insert(3, new(ChatRole.Assistant, "interruption"));
                break;
            case "wrong-call-role":
                source[2].Role = ChatRole.User;
                break;
            case "wrong-result-role":
                source[3].Role = ChatRole.Assistant;
                break;
            case "call-in-result-message":
                source[3].Contents.Add(new FunctionCallContent("second", "lookup", null));
                break;
            case "uncorrelated-text-result":
                source.Insert(4, new(ChatRole.Tool, "uncorrelated"));
                break;
            case "reused-call-id":
                source.Insert(4, new(ChatRole.Assistant, [new FunctionCallContent("first", "lookup", null)]));
                source.Insert(5, new(ChatRole.Tool, [new FunctionResultContent("first", "reused")]));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(condition));
        }
        return source;
    }

    private static HistoryCompactionRequest Request(IReadOnlyList<ChatMessage>? source = null) =>
        new("agent", "opaque-private-binding", source ?? Source(), new HistoryCompactionOptions
        {
            CompactorKey = "foreground-test"
        });

    private static HistoryCompactionResult Completed(
        HistoryCompactionRequest request, IReadOnlyList<ChatMessage> candidate, string? binding = null) =>
        new(HistoryCompactionStatus.Completed, binding ?? request.SourceBinding, candidate,
            HistoryCompactionValidation.Measure(request.Messages), HistoryCompactionValidation.Measure(candidate));
}
