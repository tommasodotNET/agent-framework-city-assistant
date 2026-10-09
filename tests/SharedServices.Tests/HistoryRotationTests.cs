using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace SharedServices.Tests;

public class HistoryRotationTests
{
    [Fact]
    public async Task RotationPublishesCompleteMessagesAtInitialTargetRevision()
    {
        var fixture = new HistoryCosmosFixture();
        var source = Source();
        fixture.SeedHead(source, nextSequence: 12);
        var repository = fixture.CreateRepository();

        var target = await repository.RotateAsync(source, Messages(), 86400, OperationId());
        var read = await repository.ReadAsync(target);

        Assert.Equal(new[] { "summary", "recent" }, read.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task TargetStartsAtRevisionOneRegardlessOfChunkCount()
    {
        var fixture = SeededFixture();
        var repository = fixture.CreateRepository();
        repository.MaxBatchSize = 2;

        var target = await repository.RotateAsync(Source(), Messages(), 86400, OperationId());

        Assert.Equal(1, target.Revision);
    }

    [Fact]
    public async Task RetirementIncrementsSourceRevisionAndLeavesSequenceUnchanged()
    {
        var fixture = SeededFixture();

        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());

        Assert.Equal((8L, 12L), (Head(fixture, Source()).GetProperty("revision").GetInt64(),
            Head(fixture, Source()).GetProperty("nextSequence").GetInt64()));
    }

    [Fact]
    public async Task SourceMessageBytesAndTtlArePreservedExactly()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var source = Source();
        // Seed a genuine envelope without rewriting it during rotation.
        fixture.SeedHead(source, nextSequence: 1);
        var document = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 2, id = "original", scopeKey = source.ScopeKey,
            conversationId = source.ConversationId, type = "ChatMessage", sequence = 0,
            timestamp = DateTimeOffset.UnixEpoch, message = new ChatMessage(ChatRole.User, "original"), ttl = 604800
        });
        fixture.Seed(source.ToAddress(), document);
        var originalBytes = document.GetRawText();

        await repository.RotateAsync(source, Messages(), 86400, OperationId());

        Assert.Equal(originalBytes, fixture.Documents.Single(value => value.GetProperty("id").GetString() == "original").GetRawText());
    }

    [Theory]
    [InlineData(86400, 86400)]
    [InlineData(604800, 604800)]
    [InlineData(-1, -1)]
    [InlineData(null, -1)]
    public async Task TargetUsesConfiguredMessageTtlWhileMetadataRemainsPermanent(int? ttl, int expected)
    {
        var fixture = SeededFixture();

        var target = await fixture.CreateRepository().RotateAsync(Source(), Messages(), ttl, OperationId());

        Assert.Equal(new[] { expected, expected, -1 },
            fixture.Documents.Where(value => value.GetProperty("conversationId").GetString() == target.ConversationId)
                .Select(value => value.GetProperty("ttl").GetInt32()));
    }

    [Fact]
    public async Task PublicationTouchesOnlySourceMetadataUsingTheOriginalEtag()
    {
        var fixture = SeededFixture();

        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());

        Assert.Equal(("replace", "history-head", "etag-1"),
            (Assert.Single(fixture.Batches.Last().Operations).Kind,
             fixture.Batches.Last().Operations[0].Id, fixture.Batches.Last().Operations[0].ETag));
    }

    [Fact]
    public async Task TwoCompleteCandidatesCannotBothPublish()
    {
        var fixture = SeededFixture();
        var loser = fixture.CreateRepository();
        var winner = fixture.CreateRepository();
        fixture.BeforeExecuteAsync = async () =>
        {
            if (fixture.Batches.Count != 1) return;
            fixture.BeforeExecuteAsync = null;
            await winner.RotateAsync(Source(), Messages(), 86400, OperationId());
        };

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => loser.RotateAsync(Source(), Messages(), 86400, OperationId()));
    }

    [Fact]
    public async Task AppendDuringCandidateStagingMakesPublicationFailRatherThanCatchUp()
    {
        var fixture = SeededFixture();
        var repository = fixture.CreateRepository();
        fixture.BeforeExecuteAsync = async () =>
        {
            fixture.BeforeExecuteAsync = null;
            await fixture.CreateRepository().AppendAsync(Source(), [new(ChatRole.User, "concurrent turn")]);
        };

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => repository.RotateAsync(Source(), Messages(), 86400, OperationId()));
    }

    [Fact]
    public async Task AppendBetweenCandidateChunksMakesPublicationFail()
    {
        var fixture = SeededFixture();
        var repository = fixture.CreateRepository();
        repository.MaxBatchSize = 2;
        fixture.AfterCommitAsync = async _ =>
        {
            fixture.AfterCommitAsync = null;
            await fixture.CreateRepository().AppendAsync(Source(), [new(ChatRole.User, "during staging")]);
        };

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => repository.RotateAsync(Source(), Messages(), 86400, OperationId()));
    }

    [Fact]
    public async Task LosingCompleteCandidateCannotBeReadAfterAnotherCandidateWins()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        fixture.BeforeExecuteAsync = async () =>
        {
            if (fixture.Batches.Count != 1) return;
            fixture.BeforeExecuteAsync = null;
            await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());
        };
        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ReadAsync(Target(operation)));
    }

    [Fact]
    public async Task ClearWithCachedSourceEtagLosesToPublishedRotation()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var source = (await repository.AppendAsync(new(Source().ScopeKey, "source", 0),
            [new(ChatRole.User, "original")])).Reference;
        fixture.BeforeExecuteAsync = async () =>
        {
            fixture.BeforeExecuteAsync = null;
            await fixture.CreateRepository().RotateAsync(source, Messages(), 86400, OperationId());
        };

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => repository.ClearAsync(source));
    }

    [Fact]
    public async Task OldWriterWithCachedSourceEtagLosesToPublishedRotation()
    {
        var fixture = SeededFixture();
        fixture.BeforeExecuteAsync = async () =>
        {
            fixture.BeforeExecuteAsync = null;
            await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());
        };

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().AppendAsync(Source(), [new(ChatRole.User, "old turn")]));
    }

    [Fact]
    public async Task RetiredSourceReadDoesNotImplicitlyResolve()
    {
        var fixture = SeededFixture();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ReadAsync(Source()));
    }

    [Fact]
    public async Task RetiredSourceCannotBeReadEvenAtItsRetirementRevision()
    {
        var fixture = SeededFixture();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ReadAsync(Source().WithRevision(8)));
    }

    [Fact]
    public async Task RetiredSourceRejectsEmptyAppend()
    {
        var fixture = SeededFixture();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().AppendAsync(Source(), []));
    }

    [Fact]
    public async Task RetiredSourceRejectsClear()
    {
        var fixture = SeededFixture();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ClearAsync(Source()));
    }

    [Fact]
    public async Task CompleteCandidateCannotBeReadBeforeSourcePublication()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        fixture.AfterCommitAsync = async _ =>
        {
            fixture.AfterCommitAsync = null;
            await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ReadAsync(Target(operation)));
        };

        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);
    }

    [Fact]
    public async Task CompleteCandidateCannotBeAppendedBeforeSourcePublication()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        fixture.AfterCommitAsync = async _ =>
        {
            fixture.AfterCommitAsync = null;
            await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
                fixture.CreateRepository().AppendAsync(Target(operation), [new(ChatRole.User, "premature turn")]));
        };

        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);
    }

    [Fact]
    public async Task CompleteCandidateCannotBeClearedBeforeSourcePublication()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        fixture.AfterCommitAsync = async _ =>
        {
            fixture.AfterCommitAsync = null;
            await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ClearAsync(Target(operation)));
        };

        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);
    }

    [Fact]
    public async Task PreparingCandidateCannotBeCountedAtRevisionZero()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        var repository = fixture.CreateRepository();
        repository.MaxBatchSize = 2;
        fixture.AfterCommitAsync = async _ =>
        {
            fixture.AfterCommitAsync = null;
            await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().CountAsync(Target(operation, 0)));
        };

        await repository.RotateAsync(Source(), Messages(), 86400, operation);
    }

    [Fact]
    public async Task FailureOnLaterCandidateChunkLeavesSourceAuthoritative()
    {
        var fixture = SeededFixture();
        var repository = fixture.CreateRepository();
        repository.MaxBatchSize = 2;
        fixture.BatchFailures[2] = HttpStatusCode.BadRequest;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RotateAsync(Source(), Messages(), 86400, OperationId()));

        Assert.Null(await fixture.CreateRepository().ResolveRotationAsync(Source()));
    }

    [Fact]
    public async Task PartialCandidateIsNotResumedOrPublishedOnRetry()
    {
        var fixture = SeededFixture();
        var repository = fixture.CreateRepository();
        repository.MaxBatchSize = 2;
        var operation = OperationId();
        fixture.BatchFailures[2] = HttpStatusCode.BadRequest;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RotateAsync(Source(), Messages(), 86400, operation));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => repository.RotateAsync(Source(), Messages(), 86400, operation));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public async Task SourceCasFailureDoesNotPublishCompleteCandidate(HttpStatusCode status)
    {
        var fixture = SeededFixture();
        fixture.BatchFailures[2] = status;
        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId()));

        Assert.Equal(Source().Revision, Head(fixture, Source()).GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task CompleteCandidateCanRetryPublicationAfterTransientSourceBatchFailure()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        fixture.BatchFailures[2] = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));

        var target = await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);

        Assert.Equal(Target(operation), target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryRejectsExpiredStagedMessagesWithoutRetiringSource(bool expireAll)
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        fixture.BatchFailures[2] = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));
        var staged = fixture.Documents.Where(document =>
            document.GetProperty("conversationId").GetString() == Target(operation).ConversationId
            && document.GetProperty("type").GetString() == "ChatMessage").ToArray();
        foreach (var document in expireAll ? staged : staged.Take(1))
            fixture.ExpireMessage(Target(operation).ToAddress(), document.GetProperty("id").GetString()!);

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));

        Assert.Equal(2, fixture.Batches.Count);
        Assert.Equal(Source().Revision, Head(fixture, Source()).GetProperty("revision").GetInt64());
        Assert.Null(await fixture.CreateRepository().ResolveRotationAsync(Source()));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("sequence")]
    [InlineData("extra-message")]
    public async Task RetryRejectsCorruptedStagedCandidateWithoutRetiringSource(string corruption)
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        fixture.BatchFailures[2] = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));
        var original = fixture.Documents.First(document =>
            document.GetProperty("conversationId").GetString() == Target(operation).ConversationId
            && document.GetProperty("type").GetString() == "ChatMessage");
        var changed = JsonNode.Parse(original.GetRawText())!;
        if (corruption == "payload")
            changed["message"] = JsonSerializer.SerializeToNode(new ChatMessage(ChatRole.User, "changed"));
        else if (corruption == "sequence")
            changed["sequence"] = 1;
        else
        {
            changed["id"] = "extra-staged-message";
            changed["sequence"] = 2;
        }
        fixture.Seed(Target(operation).ToAddress(), JsonSerializer.SerializeToElement(changed));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));

        Assert.Equal(2, fixture.Batches.Count);
        Assert.Null(await fixture.CreateRepository().ResolveRotationAsync(Source()));
    }

    [Fact]
    public async Task RetryAcceptsCanonicalEquivalentStagedPayload()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        fixture.BatchFailures[2] = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));
        foreach (var original in fixture.Documents.Where(document =>
            document.GetProperty("conversationId").GetString() == Target(operation).ConversationId
            && document.GetProperty("type").GetString() == "ChatMessage"))
        {
            var changed = JsonNode.Parse(original.GetRawText())!;
            changed["message"] = JsonSerializer.SerializeToNode(original.GetProperty("message").EnumerateObject()
                .Reverse().ToDictionary(property => property.Name, property => property.Value));
            fixture.Seed(Target(operation).ToAddress(), JsonSerializer.SerializeToElement(changed));
        }

        var target = await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);

        Assert.Equal(Target(operation), target);
        Assert.Equal(new[] { "summary", "recent" },
            (await fixture.CreateRepository().ReadAsync(target)).Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task LostSourcePublicationResponseCanRecoverFromAnotherRepository()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        await PublishWithLostResponseAsync(fixture, operation);

        var recovered = await fixture.CreateRepository().ResolveRotationAsync(Source());

        Assert.Equal(Target(operation), recovered);
    }

    [Fact]
    public async Task RetryAfterLostSourceResponseReturnsTheSamePublishedTarget()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        await PublishWithLostResponseAsync(fixture, operation);

        var retried = await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);

        Assert.Equal(Target(operation), retried);
    }

    [Fact]
    public async Task PublishedRepeatOperationDoesNotWriteNewBatches()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);

        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);

        Assert.Equal(2, fixture.Batches.Count);
    }

    [Fact]
    public async Task LostFinalCandidateResponseCanRetryWithoutDuplicatingMessages()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        fixture.AfterCommitAsync = _ =>
        {
            fixture.AfterCommitAsync = null;
            throw new IOException("Simulated lost complete candidate response");
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));
        var target = await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);

        Assert.Equal(2, await fixture.CreateRepository().CountAsync(target));
    }

    [Fact]
    public async Task ExistingUnboundHeadAtCandidateAddressCannotBePublished()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        fixture.SeedHead(Target(operation));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));
    }

    [Fact]
    public async Task ReusingOperationWithDifferentPayloadConflicts()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), [new(ChatRole.Assistant, "different")], 86400, operation));
    }

    [Fact]
    public async Task ReusingOperationWithDifferentRetentionConflicts()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), -1, operation));
    }

    [Fact]
    public async Task ReusingOperationWithDifferentSourceConflicts()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);
        var other = new HistoryReference(Source().ScopeKey, "other-source", 7);
        fixture.SeedHead(other);

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().RotateAsync(other, Messages(), 86400, operation));
    }

    [Fact]
    public async Task HashCanonicalizesObjectOrderWithoutChangingArrayOrder()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        ChatMessage[] first = [new(ChatRole.Assistant,
            [new FunctionCallContent("call", "tool", new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 })])];
        ChatMessage[] reordered = [new(ChatRole.Assistant,
            [new FunctionCallContent("call", "tool", new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 })])];
        var target = await fixture.CreateRepository().RotateAsync(Source(), first, 86400, operation);

        var retried = await fixture.CreateRepository().RotateAsync(Source(), reordered, 86400, operation);

        Assert.Equal(target, retried);
    }

    [Fact]
    public async Task ReorderedMessageArrayIsNotTheSameOperation()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages().Reverse().ToArray(), 86400, operation));
    }

    [Fact]
    public async Task ExactRecoveryRejectsTargetAdvancedByLaterTurn()
    {
        var fixture = SeededFixture();
        var target = await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());
        await fixture.CreateRepository().AppendAsync(target, [new(ChatRole.User, "future turn")]);

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ResolveRotationAsync(Source()));
    }

    [Fact]
    public async Task RepeatOperationRejectsTargetAdvancedByLaterTurn()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        var target = await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);
        await fixture.CreateRepository().AppendAsync(target, [new(ChatRole.User, "future turn")]);

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));
    }

    [Fact]
    public async Task RecoveredTargetReadStillChecksRevisionAfterAConcurrentAppend()
    {
        var fixture = SeededFixture();
        var target = await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());
        var recovered = await fixture.CreateRepository().ResolveRotationAsync(Source())
            ?? throw new InvalidOperationException("Expected a published rotation");
        await fixture.CreateRepository().AppendAsync(target, [new(ChatRole.User, "future turn")]);

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ReadAsync(recovered));
    }

    [Fact]
    public async Task PublishedTargetAllowsNormalContinuationAtItsNewRevision()
    {
        var fixture = SeededFixture();
        var target = await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());
        var appended = await fixture.CreateRepository().AppendAsync(target, [new(ChatRole.User, "next turn")]);

        var read = await fixture.CreateRepository().ReadAsync(appended.Reference);

        Assert.Equal(new[] { "summary", "recent", "next turn" }, read.Messages.Select(message => message.Text));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public async Task ResolveRequiresTheOriginalSourceRevision(long revision)
    {
        var fixture = SeededFixture();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ResolveRotationAsync(
            new(Source().ScopeKey, Source().ConversationId, revision)));
    }

    [Fact]
    public async Task ResolveDoesNotFollowASecondRotation()
    {
        var fixture = SeededFixture();
        var target = await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());
        await fixture.CreateRepository().RotateAsync(target, Messages(), 86400, OperationId());

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ResolveRotationAsync(Source()));
    }

    [Fact]
    public async Task SameOperationIdInAnotherScopeDoesNotCrossPartitions()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation);
        var other = new HistoryReference(StorageScope.Create("different-owner"), "source", 7);
        fixture.SeedHead(other);

        var target = await fixture.CreateRepository().RotateAsync(other, Messages(), 86400, operation);

        Assert.Equal(other.ScopeKey, target.ScopeKey);
    }

    [Fact]
    public async Task WrongScopeCannotResolveAnotherOwnersTransition()
    {
        var fixture = SeededFixture();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());

        Assert.Null(await fixture.CreateRepository().ResolveRotationAsync(
            new(StorageScope.Create("different-owner"), Source().ConversationId, 0)));
    }

    [Theory]
    [InlineData("sourceRevision")]
    [InlineData("targetRevision")]
    [InlineData("targetScope")]
    [InlineData("sourceScope")]
    [InlineData("operationId")]
    [InlineData("hash")]
    [InlineData("count")]
    [InlineData("ttl")]
    public async Task RecoveryRejectsIncoherentSourceTransition(string corruption)
    {
        var fixture = SeededFixture();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());
        CorruptBinding(fixture, Source(), "rotationTransition", corruption);

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ResolveRotationAsync(Source()));
    }

    [Theory]
    [InlineData("sourceRevision")]
    [InlineData("targetRevision")]
    [InlineData("targetScope")]
    [InlineData("sourceScope")]
    [InlineData("operationId")]
    [InlineData("hash")]
    [InlineData("count")]
    [InlineData("ttl")]
    public async Task RecoveryRejectsIncoherentTargetProvenance(string corruption)
    {
        var fixture = SeededFixture();
        var target = await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());
        CorruptBinding(fixture, target, "rotationCandidate", corruption);

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ResolveRotationAsync(Source()));
    }

    [Fact]
    public async Task LegacyHeadsWithoutRotationFieldsResolveAsNormal()
    {
        var fixture = SeededFixture();

        var result = await fixture.CreateRepository().ResolveRotationAsync(Source());

        Assert.Null(result);
    }

    [Fact]
    public async Task ExplicitNullRotationFieldsBehaveLikeExistingSchemaTwoHeads()
    {
        var fixture = SeededFixture();
        var head = JsonNode.Parse(Head(fixture, Source()).GetRawText())!;
        head["rotationState"] = null;
        head["rotationCandidate"] = null;
        head["rotationTransition"] = null;
        fixture.Seed(Source().ToAddress(), JsonSerializer.SerializeToElement(head));

        var appended = await fixture.CreateRepository().AppendAsync(Source(), [new(ChatRole.User, "normal")]);

        Assert.Equal(8, appended.Reference.Revision);
    }

    [Fact]
    public async Task RotationDoesNotAcceptPreSchemaTwoHeads()
    {
        var fixture = SeededFixture();
        var head = JsonNode.Parse(Head(fixture, Source()).GetRawText())!;
        head["schemaVersion"] = 1;
        fixture.Seed(Source().ToAddress(), JsonSerializer.SerializeToElement(head));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId()));
    }

    [Fact]
    public async Task RotationDoesNotAcceptMissingRequiredHeadSchemaFields()
    {
        var fixture = SeededFixture();
        var head = JsonNode.Parse(Head(fixture, Source()).GetRawText())!.AsObject();
        head.Remove("schemaVersion");
        fixture.Seed(Source().ToAddress(), JsonSerializer.SerializeToElement(head));

        await Assert.ThrowsAsync<JsonException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId()));
    }

    [Fact]
    public async Task RecoveryDoesNotAcceptMissingRequiredTransitionFields()
    {
        var fixture = SeededFixture();
        await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());
        var head = JsonNode.Parse(Head(fixture, Source()).GetRawText())!;
        head["rotationTransition"]!.AsObject().Remove("source");
        fixture.Seed(Source().ToAddress(), JsonSerializer.SerializeToElement(head));

        await Assert.ThrowsAsync<JsonException>(() => fixture.CreateRepository().ResolveRotationAsync(Source()));
    }

    [Fact]
    public async Task ActiveTargetReadRequiresCoherentProvenance()
    {
        var fixture = SeededFixture();
        var target = await fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, OperationId());
        CorruptBinding(fixture, target, "rotationCandidate", "hash");

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ReadAsync(target));
    }

    [Fact]
    public async Task MissingSourceAtRevisionZeroCannotBeRotated()
    {
        var fixture = new HistoryCosmosFixture();
        var source = new HistoryReference(Source().ScopeKey, "missing", 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateRepository().RotateAsync(source, Messages(), 86400, OperationId()));
    }

    [Fact]
    public async Task ExistingRevisionZeroHeadCanBeRotatedWithAFence()
    {
        var fixture = new HistoryCosmosFixture();
        var source = new HistoryReference(Source().ScopeKey, "existing-empty", 0);
        fixture.SeedHead(source);

        await fixture.CreateRepository().RotateAsync(source, Messages(), 86400, OperationId());

        Assert.Equal(1, Head(fixture, source).GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task EmptyCompactedOutputDoesNotCreateCandidate()
    {
        var fixture = SeededFixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), [], 86400, OperationId()));

        Assert.Empty(fixture.Batches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("guessable-operation")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("5C18BE026D7C4CBAB6D4D3BFE20ABC6A")]
    public async Task NonRandomOrNonCanonicalOperationIdsAreRejected(string operation)
    {
        var fixture = SeededFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task InvalidTtlIsRejectedBeforeCandidateCreation(int ttl)
    {
        var fixture = SeededFixture();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), ttl, OperationId()));
    }

    [Fact]
    public async Task ChunkOperationLimitReservesHeadAndPublicationIsSeparate()
    {
        var fixture = SeededFixture();
        var messages = Enumerable.Range(0, 100).Select(index => new ChatMessage(ChatRole.User, index.ToString())).ToArray();

        await fixture.CreateRepository().RotateAsync(Source(), messages, 86400, OperationId());

        Assert.Equal(new[] { 100, 2, 1 }, fixture.Batches.Select(batch => batch.Operations.Count));
    }

    [Fact]
    public async Task PayloadBudgetSplitsCandidateChunks()
    {
        var fixture = SeededFixture();
        var payload = new string('x', 950_000);

        await fixture.CreateRepository().RotateAsync(Source(),
            [new(ChatRole.User, payload), new(ChatRole.User, payload)], 86400, OperationId());

        Assert.Equal(new[] { 2, 2, 1 }, fixture.Batches.Select(batch => batch.Operations.Count));
    }

    [Fact]
    public async Task OversizedCompactedMessageFailsBeforeAnyChunk()
    {
        var fixture = SeededFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateRepository().RotateAsync(Source(),
            [new(ChatRole.User, "small"), new(ChatRole.User, new string('x', 1_800_000))], 86400, OperationId()));

        Assert.Empty(fixture.Batches);
    }

    [Fact]
    public async Task CancellationAfterCandidateChunkDoesNotPublishOrDeletePartialState()
    {
        var fixture = SeededFixture();
        var repository = fixture.CreateRepository();
        repository.MaxBatchSize = 2;
        using var cancellation = new CancellationTokenSource();
        fixture.AfterCommitAsync = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.RotateAsync(Source(), Messages(), 86400, OperationId(), cancellation.Token));

        Assert.Equal("preparing", fixture.Documents.Single(value =>
            value.GetProperty("conversationId").GetString() != Source().ConversationId
            && value.GetProperty("type").GetString() == "HistoryHead").GetProperty("rotationState").GetString());
    }

    [Fact]
    public async Task CancellationAfterSourcePublicationCanBeResolvedExactly()
    {
        var fixture = SeededFixture();
        var operation = OperationId();
        using var cancellation = new CancellationTokenSource();
        fixture.AfterCommitAsync = batch =>
        {
            if (batch.Partition != Source().ToAddress().ToPartitionKey()) return Task.CompletedTask;
            fixture.AfterCommitAsync = null;
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation, cancellation.Token));

        Assert.Equal(Target(operation), await fixture.CreateRepository().ResolveRotationAsync(Source()));
    }

    [Fact]
    public async Task StaleSourceIsRejectedBeforeWritingAnyCandidate()
    {
        var fixture = SeededFixture();
        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            fixture.CreateRepository().RotateAsync(new(Source().ScopeKey, Source().ConversationId, 6), Messages(), 86400, OperationId()));

        Assert.Empty(fixture.Batches);
    }

    private static async Task PublishWithLostResponseAsync(HistoryCosmosFixture fixture, string operation)
    {
        fixture.AfterCommitAsync = batch =>
        {
            if (batch.Partition != Source().ToAddress().ToPartitionKey()) return Task.CompletedTask;
            fixture.AfterCommitAsync = null;
            throw new IOException("Simulated lost publication response");
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.CreateRepository().RotateAsync(Source(), Messages(), 86400, operation));
    }

    private static void CorruptBinding(HistoryCosmosFixture fixture, HistoryReference reference, string field, string corruption)
    {
        var head = JsonNode.Parse(Head(fixture, reference).GetRawText())!;
        var binding = head[field]!;
        switch (corruption)
        {
            case "sourceRevision": binding["source"]!["revision"] = 6; break;
            case "targetRevision": binding["target"]!["revision"] = 2; break;
            case "targetScope": binding["target"]!["scopeKey"] = StorageScope.Create("another-owner"); break;
            case "sourceScope": binding["source"]!["scopeKey"] = StorageScope.Create("another-owner"); break;
            case "operationId": binding["operationId"] = OperationId(); break;
            case "hash": binding["snapshotHash"] = new string('0', 64); break;
            case "count": binding["messageCount"] = 3; break;
            case "ttl": binding["messageTtl"] = 604800; break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        fixture.Seed(reference.ToAddress(), JsonSerializer.SerializeToElement(head));
    }

    private static JsonElement Head(HistoryCosmosFixture fixture, HistoryReference reference) =>
        fixture.Documents.Single(value => value.GetProperty("scopeKey").GetString() == reference.ScopeKey
            && value.GetProperty("conversationId").GetString() == reference.ConversationId
            && value.GetProperty("type").GetString() == "HistoryHead");

    private static HistoryCosmosFixture SeededFixture()
    {
        var fixture = new HistoryCosmosFixture();
        fixture.SeedHead(Source(), nextSequence: 12);
        return fixture;
    }

    private static HistoryReference Target(string operation, long revision = 1) =>
        new(Source().ScopeKey, "rotation-" + operation, revision);

    private static HistoryReference Source() => new(StorageScope.Create("rotation-owner"), "source", 7);
    private static ChatMessage[] Messages() => [new(ChatRole.Assistant, "summary"), new(ChatRole.User, "recent")];
    private static string OperationId() => Guid.NewGuid().ToString("N");
}
