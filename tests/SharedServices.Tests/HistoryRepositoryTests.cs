using Microsoft.Extensions.AI;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace SharedServices.Tests;

public class HistoryRepositoryTests
{
    [Fact]
    public async Task AppendWritesMessagesAndRevisionInTheSameFullPartitionBatch()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var reference = new HistoryReference(StorageScope.Create("lookup"), "server-history", 0);

        await repository.AppendAsync(reference, [new(ChatRole.User, "hello")]);

        Assert.Equal(reference.ToAddress().ToPartitionKey(), Assert.Single(fixture.Batches).Partition);
    }

    [Fact]
    public async Task FirstAppendCreatesHeadWithoutUnconditionalUpsert()
    {
        var fixture = new HistoryCosmosFixture();

        await fixture.CreateRepository().AppendAsync(NewReference(), [new(ChatRole.User, "hello")]);

        Assert.Equal(new[] { "create", "create" }, Assert.Single(fixture.Batches).Operations.Select(operation => operation.Kind));
    }

    [Fact]
    public async Task ExistingAppendReplacesHeadUsingItsReadEtag()
    {
        var fixture = new HistoryCosmosFixture();
        var reference = NewReference().WithRevision(1);
        fixture.SeedHead(reference, nextSequence: 5);

        await fixture.CreateRepository().AppendAsync(reference, [new(ChatRole.User, "hello")]);

        Assert.Equal("etag-1", Assert.Single(fixture.Batches).Operations.Last().ETag);
    }

    [Fact]
    public async Task AppendReturnsTheCommittedRevision()
    {
        var fixture = new HistoryCosmosFixture();

        var result = await fixture.CreateRepository().AppendAsync(NewReference(), [new(ChatRole.User, "hello")]);

        Assert.Equal(1, result.Reference.Revision);
    }

    [Fact]
    public async Task EmptyAppendDoesNotCreateHeadOrClaimMessages()
    {
        var fixture = new HistoryCosmosFixture();

        var result = await fixture.CreateRepository().AppendAsync(NewReference(), []);

        Assert.Equal(new HistoryWriteResult(NewReference(), 0), result);
    }

    [Fact]
    public async Task EmptyAppendDoesNotExecuteBatch()
    {
        var fixture = new HistoryCosmosFixture();

        await fixture.CreateRepository().AppendAsync(NewReference(), []);

        Assert.Empty(fixture.Batches);
    }

    [Fact]
    public async Task EmptyAppendStillRejectsStaleRevision()
    {
        var fixture = new HistoryCosmosFixture();
        fixture.SeedHead(NewReference().WithRevision(1));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().AppendAsync(NewReference(), []));
    }

    [Fact]
    public async Task StaleReadFailsBeforeExecutingMessageQuery()
    {
        var fixture = new HistoryCosmosFixture();
        fixture.SeedHead(NewReference().WithRevision(1));
        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ReadAsync(NewReference()));

        Assert.Empty(fixture.Queries);
    }

    [Fact]
    public async Task MissingHeadAtNonzeroRevisionIsNotReset()
    {
        var fixture = new HistoryCosmosFixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateRepository().ReadAsync(NewReference().WithRevision(1)));
    }

    [Fact]
    public async Task ReadRejectsHeadChangedDuringQuery()
    {
        var fixture = new HistoryCosmosFixture();
        var reference = NewReference().WithRevision(1);
        fixture.SeedHead(reference);
        fixture.BeforeQuery = () => fixture.SeedHead(reference.WithRevision(2));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().ReadAsync(reference));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public async Task FailedCasReturnsExplicitConflict(HttpStatusCode status)
    {
        var fixture = new HistoryCosmosFixture();
        fixture.BatchFailures[1] = status;

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.CreateRepository().AppendAsync(NewReference(), [new(ChatRole.User, "hello")]));
    }

    [Fact]
    public async Task TwoBranchesWithTheSameRevisionCannotBothCommit()
    {
        var fixture = new HistoryCosmosFixture();
        var reference = NewReference().WithRevision(1);
        fixture.SeedHead(reference);
        var first = fixture.CreateRepository();
        var second = fixture.CreateRepository();
        // Pause the first branch after it read the ETag, then commit the second branch.
        fixture.BeforeExecuteAsync = async () =>
        {
            fixture.BeforeExecuteAsync = null;
            await second.AppendAsync(reference, [new(ChatRole.Assistant, "winner")]);
        };

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => first.AppendAsync(reference, [new(ChatRole.Assistant, "loser")]));
    }

    [Fact]
    public async Task SchemaIsValidatedOnlyOnceAfterSuccess()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        await repository.ReadAsync(NewReference());

        await repository.CountAsync(NewReference());

        Assert.Equal(1, fixture.SchemaReadCount);
    }

    [Fact]
    public async Task InvalidSchemaIsNotCachedAsSuccessful()
    {
        var fixture = new HistoryCosmosFixture { Properties = new("conversations", "/conversationId") { DefaultTimeToLive = -1 } };
        var repository = fixture.CreateRepository();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReadAsync(NewReference()));
        fixture.Properties = new("conversations", new[] { "/scopeKey", "/conversationId" }) { DefaultTimeToLive = -1 };

        await repository.ReadAsync(NewReference());

        Assert.Equal(2, fixture.SchemaReadCount);
    }

    [Fact]
    public async Task QueryUsesTheFullHistoryPartitionAndScope()
    {
        var fixture = new HistoryCosmosFixture();

        await fixture.CreateRepository().ReadAsync(NewReference());

        Assert.Equal(NewReference().ToAddress().ToPartitionKey(), Assert.Single(fixture.Queries).Options.PartitionKey);
    }

    [Fact]
    public async Task QueryUsesSequenceRatherThanEqualTimestamps()
    {
        var fixture = new HistoryCosmosFixture();

        await fixture.CreateRepository().ReadAsync(NewReference());

        Assert.EndsWith("ORDER BY c.sequence ASC", Assert.Single(fixture.Queries).Query.QueryText);
    }

    [Fact]
    public async Task LatestReadReturnsMessagesInAscendingSequence()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var write = await repository.AppendAsync(NewReference(), [new(ChatRole.User, "one"), new(ChatRole.Assistant, "two"), new(ChatRole.User, "three")]);

        var read = await repository.ReadAsync(write.Reference, 2);

        Assert.Equal(new[] { "two", "three" }, read.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task AppendPreservesUnicodeAndToolCallIdsAndResults()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        ChatMessage[] messages =
        [
            new(ChatRole.Assistant, [new TextContent("città 東京"), new FunctionCallContent("call-42", "weather", new Dictionary<string, object?> { ["city"] = "Roma" })]) { MessageId = "model-id" },
            new(ChatRole.Tool, [new FunctionResultContent("call-42", new { temperature = 23 })])
        ];
        var write = await repository.AppendAsync(NewReference(), messages);

        var read = await repository.ReadAsync(write.Reference);

        Assert.Equal(JsonSerializer.SerializeToElement(messages).GetRawText(), JsonSerializer.SerializeToElement(read.Messages).GetRawText());
    }

    [Fact]
    public async Task StoredMessageIsAnObjectNotDoubleEncodedJson()
    {
        var fixture = new HistoryCosmosFixture();

        await fixture.CreateRepository().AppendAsync(NewReference(), [new(ChatRole.User, "hello")]);

        Assert.Equal(JsonValueKind.Object, fixture.Documents.Single(document => document.GetProperty("type").GetString() == "ChatMessage").GetProperty("message").ValueKind);
    }

    [Fact]
    public async Task ServerStorageIdDoesNotReuseModelMessageId()
    {
        var fixture = new HistoryCosmosFixture();

        await fixture.CreateRepository().AppendAsync(NewReference(), [new(ChatRole.User, "hello") { MessageId = "model-id" }]);

        Assert.NotEqual("model-id", fixture.Documents.Single(document => document.GetProperty("type").GetString() == "ChatMessage").GetProperty("id").GetString());
    }

    [Fact]
    public async Task SequenceContinuesAfterClearRatherThanResetting()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var first = await repository.AppendAsync(NewReference(), [new(ChatRole.User, "one"), new(ChatRole.Assistant, "two")]);
        var cleared = await repository.ClearAsync(first.Reference);

        await repository.AppendAsync(cleared.Reference, [new(ChatRole.User, "three")]);

        Assert.Equal(2, fixture.Documents.Single(document => document.GetProperty("type").GetString() == "ChatMessage").GetProperty("sequence").GetInt64());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(101)]
    public void InvalidBatchLimitsAreRejected(int limit)
    {
        var repository = new HistoryCosmosFixture().CreateRepository();

        Assert.Throws<ArgumentOutOfRangeException>(() => repository.MaxBatchSize = limit);
    }

    [Fact]
    public async Task BatchReservesOneOperationForHead()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var messages = Enumerable.Range(0, 100).Select(index => new ChatMessage(ChatRole.User, index.ToString())).ToArray();

        await repository.AppendAsync(NewReference(), messages);

        Assert.Equal(new[] { 100, 2 }, fixture.Batches.Select(batch => batch.Operations.Count));
    }

    [Fact]
    public async Task CursorCallbackRunsOncePerSuccessfulChunk()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        repository.MaxBatchSize = 2;
        var revisions = new List<long>();

        await repository.AppendAsync(NewReference(), [new(ChatRole.User, "one"), new(ChatRole.User, "two")],
            onCommitted: reference => revisions.Add(reference.Revision));

        Assert.Equal(new long[] { 1, 2 }, revisions);
    }

    [Fact]
    public async Task PartialFailureExposesLastCommittedCursor()
    {
        var fixture = new HistoryCosmosFixture();
        fixture.BatchFailures[2] = HttpStatusCode.BadRequest;
        var repository = fixture.CreateRepository();
        repository.MaxBatchSize = 2;

        var exception = await Assert.ThrowsAsync<HistoryPartialWriteException>(() =>
            repository.AppendAsync(NewReference(), [new(ChatRole.User, "one"), new(ChatRole.User, "two")]));

        Assert.Equal(NewReference().WithRevision(1), exception.LastCommittedReference);
    }

    [Fact]
    public async Task PartialFailureExposesCommittedCount()
    {
        var fixture = new HistoryCosmosFixture();
        fixture.BatchFailures[2] = HttpStatusCode.BadRequest;
        var repository = fixture.CreateRepository();
        repository.MaxBatchSize = 2;

        var exception = await Assert.ThrowsAsync<HistoryPartialWriteException>(() =>
            repository.AppendAsync(NewReference(), [new(ChatRole.User, "one"), new(ChatRole.User, "two")]));

        Assert.Equal(1, exception.CommittedMessageCount);
    }

    [Fact]
    public async Task CancellationAfterCommitPreservesCommittedCursor()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        repository.MaxBatchSize = 2;
        using var cancellation = new CancellationTokenSource();

        var exception = await Assert.ThrowsAsync<HistoryWriteCanceledException>(() =>
            repository.AppendAsync(NewReference(), [new(ChatRole.User, "one"), new(ChatRole.User, "two")],
                onCommitted: _ => cancellation.Cancel(), cancellationToken: cancellation.Token));

        Assert.Equal(1, exception.LastCommittedReference.Revision);
    }

    [Fact]
    public async Task RequestSizeSplitsBatchBeforeCosmosLimit()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var payload = new string('x', 950_000);

        await repository.AppendAsync(NewReference(), [new(ChatRole.User, payload), new(ChatRole.User, payload)]);

        Assert.Equal(2, fixture.Batches.Count);
    }

    [Fact]
    public async Task OversizedMessageFailsBeforeAnyCommit()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AppendAsync(NewReference(),
            [new(ChatRole.User, "small"), new(ChatRole.User, new string('x', 1_800_000))]));

        Assert.Empty(fixture.Batches);
    }

    [Fact]
    public async Task UnsupportedTransactionalBatchDoesNotUseSequentialFallback()
    {
        var fixture = new HistoryCosmosFixture();
        fixture.BatchFailures[1] = HttpStatusCode.NotImplemented;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateRepository().AppendAsync(NewReference(), [new(ChatRole.User, "hello")]));
    }

    [Fact]
    public async Task CountExcludesHeadAndOtherHistoryPartitions()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var write = await repository.AppendAsync(NewReference(), [new(ChatRole.User, "one")]);
        await repository.AppendAsync(new(NewReference().ScopeKey, "other-history", 0), [new(ChatRole.User, "other")]);

        var count = await repository.CountAsync(write.Reference);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task FailedClearDoesNotReportSuccess()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var write = await repository.AppendAsync(NewReference(), [new(ChatRole.User, "one")]);
        fixture.BatchFailures[2] = HttpStatusCode.BadRequest;

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ClearAsync(write.Reference));
    }

    [Fact]
    public async Task ClearDoesNotDeleteOtherScopes()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var first = await repository.AppendAsync(NewReference(), [new(ChatRole.User, "one")]);
        var second = await repository.AppendAsync(new(StorageScope.Create("other-owner"), "server-history", 0), [new(ChatRole.User, "other")]);
        await repository.ClearAsync(first.Reference);

        var count = await repository.CountAsync(second.Reference);

        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(86400)]
    [InlineData(604800)]
    [InlineData(-1)]
    public async Task HeadNeverExpiresRegardlessOfMessageLifetime(int messageTtl)
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();

        await repository.AppendAsync(NewReference(), [new(ChatRole.User, "one")], messageTtl);

        Assert.Equal(-1, fixture.Documents.Single(document => document.GetProperty("type").GetString() == "HistoryHead").GetProperty("ttl").GetInt32());
    }

    [Fact]
    public async Task DefaultMessageRetentionRemainsTwentyFourHours()
    {
        var fixture = new HistoryCosmosFixture();

        await fixture.CreateRepository().AppendAsync(NewReference(), [new(ChatRole.User, "one")]);

        Assert.Equal(86400, fixture.Documents.Single(document => document.GetProperty("type").GetString() == "ChatMessage").GetProperty("ttl").GetInt32());
    }

    [Fact]
    public async Task PermanentHeadIsNeverShortenedByLaterFiniteConfiguration()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var write = await repository.AppendAsync(NewReference(), [new(ChatRole.User, "one")]);

        await repository.AppendAsync(write.Reference, [new(ChatRole.User, "two")], 604800);

        Assert.Equal(-1, fixture.Documents.Single(document => document.GetProperty("type").GetString() == "HistoryHead").GetProperty("ttl").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task InvalidTtlFailsWithoutCommit(int ttl)
    {
        var repository = new HistoryCosmosFixture().CreateRepository();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.AppendAsync(NewReference(), [new(ChatRole.User, "one")], ttl));
    }

    private static HistoryReference NewReference() => new(StorageScope.Create("lookup"), "server-history", 0);

    [Fact]
    public async Task MissingHeadSchemaVersionIsAnError()
    {
        var fixture = new HistoryCosmosFixture();
        var reference = NewReference().WithRevision(1);
        fixture.Seed(reference.ToAddress(), JsonSerializer.SerializeToElement(new
        {
            id = "history-head", scopeKey = reference.ScopeKey, conversationId = reference.ConversationId,
            type = "HistoryHead", revision = 1, nextSequence = 0, ttl = -1
        }));

        await Assert.ThrowsAsync<JsonException>(() => fixture.CreateRepository().ReadAsync(reference));
    }

    [Fact]
    public async Task InvalidMessageSchemaIsNotSilentlySkipped()
    {
        var fixture = new HistoryCosmosFixture();
        var reference = NewReference().WithRevision(1);
        fixture.SeedHead(reference, nextSequence: 1);
        fixture.Seed(reference.ToAddress(), JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, id = "old-message", scopeKey = reference.ScopeKey, conversationId = reference.ConversationId,
            type = "ChatMessage", sequence = 0, timestamp = DateTimeOffset.UnixEpoch,
            message = new { role = "user", contents = Array.Empty<object>() }, ttl = -1
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateRepository().ReadAsync(reference));
    }

    [Fact]
    public async Task EveryHeadPointReadUsesFullHistoryPartition()
    {
        var fixture = new HistoryCosmosFixture();
        var reference = NewReference();

        await fixture.CreateRepository().ReadAsync(reference);

        Assert.All(fixture.PointReadPartitions, partition => Assert.Equal(reference.ToAddress().ToPartitionKey(), partition));
    }

    [Fact]
    public async Task CancellationBeforeCommitCreatesNoDocuments()
    {
        var fixture = new HistoryCosmosFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.CreateRepository().AppendAsync(NewReference(), [new(ChatRole.User, "one")], cancellationToken: cancellation.Token));

        Assert.Empty(fixture.Documents);
    }

    [Fact]
    public async Task AppendingToFiniteLegacyHeadMakesMetadataPermanent()
    {
        var fixture = new HistoryCosmosFixture();
        var reference = NewReference().WithRevision(1);
        fixture.SeedHead(reference, ttl: 604800);
        var repository = fixture.CreateRepository();

        await repository.AppendAsync(reference, [new(ChatRole.User, "one")], 604800);

        Assert.Equal(-1, fixture.Documents.Single(document => document.GetProperty("type").GetString() == "HistoryHead").GetProperty("ttl").GetInt32());
    }

    [Fact]
    public async Task ClearingFiniteLegacyHeadMakesMetadataPermanent()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var write = await repository.AppendAsync(NewReference(), [new(ChatRole.User, "one")], 604800);
        fixture.SeedHead(write.Reference, nextSequence: 1, ttl: 604800);

        await repository.ClearAsync(write.Reference);

        Assert.Equal(-1, fixture.Documents.Single(document => document.GetProperty("type").GetString() == "HistoryHead").GetProperty("ttl").GetInt32());
    }
}
