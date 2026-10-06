using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using VoiceOrchestratorAgent;

namespace SharedServices.Tests;

public class VoiceConversationStoreTests
{
    [Fact]
    public async Task MissingSnapshotCreatesAnIndependentServerGeneratedHistory()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("caller-phone");

        var copy = await fixture.Store.LoadAsync(address);

        Assert.NotEqual(address.SessionId, copy.Context!.ActiveHistory.ConversationId);
    }

    [Fact]
    public async Task NullContinuationDoesNotReadEitherContainer()
    {
        var fixture = new VoiceStoreFixture();

        await fixture.Store.LoadAsync(null);

        Assert.Equal((0, 0), (fixture.Sessions.SchemaReads, fixture.History.SchemaReadCount));
    }

    [Fact]
    public async Task NullContinuationDoesNotPersistEvenWithMessages()
    {
        var fixture = new VoiceStoreFixture();
        var copy = await fixture.Store.LoadAsync(null);

        await fixture.Store.SaveAsync(copy, [fixture.Text("not persisted")]);

        Assert.Equal((0, 0), (fixture.Sessions.Requests.Count, fixture.History.Batches.Count));
    }

    [Fact]
    public async Task ResumeReadsThePreviouslyStoredUnicodeTranscript()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        var copy = await fixture.Store.LoadAsync(address);
        await fixture.Store.SaveAsync(copy, [fixture.Text("Caffè 東京 😀")]);

        var resumed = await fixture.Store.LoadAsync(address);

        Assert.Equal("Caffè 東京 😀", Assert.Single(resumed.Messages).Text);
    }

    [Fact]
    public async Task ResumeAppendsOnlyNewMessages()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address), [fixture.Text("first")]);
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address), [fixture.Text("second")]);

        var resumed = await fixture.Store.LoadAsync(address);

        Assert.Equal(new[] { "first", "second" }, resumed.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task SnapshotContainsOnlyTheSharedPersistenceContext()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address), []);

        var stored = await fixture.Sessions.Repository.ReadAsync(address);

        Assert.Equal(new[] { "agentId", "activeHistory" },
            stored!.Document.SerializedSession.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task SnapshotUsesSevenDayTtl()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address), []);

        var stored = await fixture.Sessions.Repository.ReadAsync(address);

        Assert.Equal(604800, stored!.Document.Ttl);
    }

    [Fact]
    public async Task MessagesUseSevenDayTtl()
    {
        var fixture = new VoiceStoreFixture();
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(fixture.Address("lookup")), [fixture.Text("hello")]);

        Assert.Equal(604800, Assert.Single(fixture.History.Documents,
            document => document.GetProperty("type").GetString() == "ChatMessage").GetProperty("ttl").GetInt32());
    }

    [Fact]
    public async Task NewSnapshotUsesCreateOnly()
    {
        var fixture = new VoiceStoreFixture();
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(fixture.Address("lookup")), []);

        Assert.Equal("create", Assert.Single(fixture.Sessions.Requests, request => request.Operation != "read").Operation);
    }

    [Fact]
    public async Task ResumedSnapshotUsesConditionalReplace()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address), []);
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address), []);

        Assert.Equal("version-1", Assert.Single(fixture.Sessions.Requests,
            request => request.Operation == "replace").ETag);
    }

    [Fact]
    public async Task TwoNewCopiesCannotOverwriteTheSameSnapshot()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        var first = await fixture.Store.LoadAsync(address);
        var second = await fixture.Store.LoadAsync(address);
        await fixture.Store.SaveAsync(first, []);

        await Assert.ThrowsAsync<SessionSnapshotConflictException>(() => fixture.Store.SaveAsync(second, []));
    }

    [Fact]
    public async Task LoadedCopiesDoNotShareEtagUpdates()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address), []);
        var first = await fixture.Store.LoadAsync(address);
        var second = await fixture.Store.LoadAsync(address);
        await fixture.Store.SaveAsync(first, []);

        await Assert.ThrowsAsync<SessionSnapshotConflictException>(() => fixture.Store.SaveAsync(second, []));
    }

    [Fact]
    public async Task SameContinuationForTwoUsersKeepsSeparateTranscripts()
    {
        var fixture = new VoiceStoreFixture();
        var alice = fixture.Address("lookup", "alice");
        var bob = fixture.Address("lookup", "bob");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(alice), [fixture.Text("alice")]);
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(bob), [fixture.Text("bob")]);

        Assert.Equal("bob", Assert.Single((await fixture.Store.LoadAsync(bob)).Messages).Text);
    }

    [Fact]
    public async Task TextSnapshotInSameScopeDoesNotResumeAsVoice()
    {
        var fixture = new VoiceStoreFixture();
        var textAddress = SessionStorageAddress.Create("text-agent", "lookup");
        var context = SessionPersistenceContext.Create(textAddress);
        var written = await fixture.Repository.AppendAsync(context.ActiveHistory, [new(ChatRole.User, "text only")]);
        var document = SessionDocument.Create(textAddress,
            JsonSerializer.SerializeToElement(context.WithHistory(written.Reference)), DateTimeOffset.UtcNow, 604800);
        await fixture.Sessions.Repository.WriteAsync(document, SessionWriteCondition.CreateOnly(textAddress));

        var voice = await fixture.Store.LoadAsync(fixture.Address("lookup"));

        Assert.Empty(voice.Messages);
    }

    [Fact]
    public async Task SameScopeHistoriesStillHaveDifferentInternalIds()
    {
        var fixture = new VoiceStoreFixture();
        var text = SessionPersistenceContext.Create(SessionStorageAddress.Create("text-agent", "lookup"));

        var voice = await fixture.Store.LoadAsync(fixture.Address("lookup"));

        Assert.NotEqual(text.ActiveHistory.ConversationId, voice.Context!.ActiveHistory.ConversationId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ReadFailureIsNotAnEmptyResume(HttpStatusCode status)
    {
        var fixture = new VoiceStoreFixture();
        fixture.Sessions.ItemFailure = status;

        await Assert.ThrowsAsync<CosmosException>(() => fixture.Store.LoadAsync(fixture.Address("lookup")));
    }

    [Fact]
    public async Task StaleHistoryFailsBeforeReturningAResume()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        var copy = await fixture.Store.LoadAsync(address);
        await fixture.Store.SaveAsync(copy, []);
        await fixture.Repository.AppendAsync(copy.Context!.ActiveHistory, [new(ChatRole.User, "competing turn")]);

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.Store.LoadAsync(address));
    }

    [Fact]
    public async Task SnapshotWriteFailureAfterAppendLeavesAnExplicitlyStaleContinuation()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address), []);
        var resumed = await fixture.Store.LoadAsync(address);
        fixture.Sessions.WriteFailure = HttpStatusCode.Forbidden;
        await Assert.ThrowsAsync<CosmosException>(() => fixture.Store.SaveAsync(resumed, [fixture.Text("committed")]));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => fixture.Store.LoadAsync(address));
    }

    [Fact]
    public async Task PartialHistoryWriteRetainsLastCommittedCursorOnTheWorkingCopy()
    {
        var fixture = new VoiceStoreFixture();
        fixture.Repository.MaxBatchSize = 2;
        fixture.History.BatchFailures[2] = HttpStatusCode.BadRequest;
        var copy = await fixture.Store.LoadAsync(fixture.Address("lookup"));
        await Assert.ThrowsAsync<HistoryPartialWriteException>(() =>
            fixture.Store.SaveAsync(copy, [fixture.Text("one"), fixture.Text("two")]));

        Assert.Equal(1, copy.Context!.ActiveHistory.Revision);
    }

    [Fact]
    public async Task PartialHistoryWriteDoesNotSaveASnapshot()
    {
        var fixture = new VoiceStoreFixture();
        fixture.Repository.MaxBatchSize = 2;
        fixture.History.BatchFailures[2] = HttpStatusCode.BadRequest;
        var copy = await fixture.Store.LoadAsync(fixture.Address("lookup"));
        await Assert.ThrowsAsync<HistoryPartialWriteException>(() =>
            fixture.Store.SaveAsync(copy, [fixture.Text("one"), fixture.Text("two")]));

        Assert.DoesNotContain(fixture.Sessions.Requests, request => request.Operation != "read");
    }

    [Fact]
    public async Task FailedSaveCannotReplayAlreadyCommittedMessages()
    {
        var fixture = new VoiceStoreFixture();
        fixture.Sessions.WriteFailure = HttpStatusCode.Forbidden;
        var copy = await fixture.Store.LoadAsync(fixture.Address("lookup"));
        await Assert.ThrowsAsync<CosmosException>(() => fixture.Store.SaveAsync(copy, [fixture.Text("one")]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveAsync(copy, [fixture.Text("one")]));
    }

    [Fact]
    public async Task CancellationIsNotAnEmptyResume()
    {
        var fixture = new VoiceStoreFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.LoadAsync(fixture.Address("lookup"), cancellation.Token));
    }

    [Fact]
    public async Task SnapshotWithDifferentOwnerIsRejected()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        var otherContext = SessionPersistenceContext.Create(SessionStorageAddress.Create("text-agent", "lookup"));
        fixture.Sessions.Seed(address, SessionDocument.Create(address,
            JsonSerializer.SerializeToElement(otherContext), DateTimeOffset.UtcNow).SerializeToUtf8Bytes());

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.LoadAsync(address));
    }

    [Fact]
    public async Task ToolCallAndResultRoundTripThroughTheRealHistoryRepository()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address),
        [
            new(DateTimeOffset.UtcNow, "assistant", "tool_call", ToolCallId: "call-東京",
                ToolName: "weather", ToolArguments: """{"city":"Zürich"}"""),
            new(DateTimeOffset.UtcNow, "tool", "tool_call_response", ToolCallId: "call-東京",
                ToolName: "weather", ToolResult: "15°C ☀️")
        ]);

        var resumed = await fixture.Store.LoadAsync(address);

        Assert.Equal("15°C ☀️", Assert.IsType<JsonElement>(
            Assert.IsType<FunctionResultContent>(resumed.Messages[1].Contents[0]).Result).GetString());
    }
}

internal sealed class VoiceStoreFixture
{
    internal SessionCosmosSdkFixture Sessions { get; } = new();
    internal HistoryCosmosFixture History { get; } = new();
    internal CosmosChatMessageRepository Repository { get; }
    internal VoiceConversationStore Store { get; }

    internal VoiceStoreFixture()
    {
        Repository = History.CreateRepository();
        Store = new(Sessions.Repository, Repository);
    }

    internal SessionStorageAddress Address(string lookup, string? user = null) =>
        SessionStorageAddress.Create(VoiceConversationStore.AgentId, lookup,
            user is null ? null : new Dictionary<string, string> { ["isolation"] = user });

    internal ConversationMessage Text(string text) => new(DateTimeOffset.UtcNow, "user", "text", text);
}
