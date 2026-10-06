using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

#pragma warning disable MAAI001

namespace SharedServices.Tests;

public class SessionMafStoreTests
{
    private static ChatClientAgent Agent(string id = "agent") =>
        new(new Mock<IChatClient>().Object, new ChatClientAgentOptions { Id = id, Name = id });

    private static CosmosAgentSessionStore Store(SessionCosmosSdkFixture sdk, int ttl = -1) =>
        new(sdk.Repository, NullLogger<CosmosAgentSessionStore>.Instance, ttl);

    [Fact]
    public async Task FreshSessionHasPersistenceContextBeforeHistoryRuns()
    {
        var sdk = new SessionCosmosSdkFixture();
        var agent = Agent();

        var session = await Store(sdk).GetOrCreateSessionAsync(agent, new("lookup"));

        Assert.Equal(SessionStorageAddress.Create(agent.Id, "lookup").ScopeKey,
            SessionPersistenceState.GetRequired(session).ActiveHistory.ScopeKey);
    }

    [Fact]
    public async Task FullOwningAgentSerializationRoundTripsWithUnknownState()
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = Store(sdk);
        var agent = Agent();
        var key = new AgentSessionStoreKey("lookup");
        var session = await store.GetOrCreateSessionAsync(agent, key);
        session.StateBag.SetValue("unknown", new Dictionary<string, string> { ["tool"] = "città" });
        var expected = await agent.SerializeSessionAsync(session);
        await store.SaveSessionAsync(agent, key, session);

        var loaded = await store.GetSessionAsync(agent, key);
        var actual = await agent.SerializeSessionAsync(loaded!);

        Assert.True(JsonElement.DeepEquals(expected, actual));
    }

    [Fact]
    public async Task RepeatedReadsReturnIndependentWorkingCopies()
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = Store(sdk);
        var agent = Agent();
        var key = new AgentSessionStoreKey("lookup");
        var original = await store.GetOrCreateSessionAsync(agent, key);
        await store.SaveSessionAsync(agent, key, original);

        var first = await store.GetSessionAsync(agent, key);
        var second = await store.GetSessionAsync(agent, key);

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task IndependentlyLoadedCopiesDoNotShareSuccessfulWriteTokens()
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = Store(sdk);
        var agent = Agent();
        var key = new AgentSessionStoreKey("lookup");
        await store.SaveSessionAsync(agent, key, await store.GetOrCreateSessionAsync(agent, key));
        var first = await store.GetSessionAsync(agent, key);
        var second = await store.GetSessionAsync(agent, key);
        await store.SaveSessionAsync(agent, key, first!);

        await Assert.ThrowsAsync<SessionSnapshotConflictException>(() =>
            store.SaveSessionAsync(agent, key, second!).AsTask());
    }

    [Fact]
    public async Task IndependentlyCreatedCopiesUseCreateOnlyEvenAfterAnotherCopySaves()
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = Store(sdk);
        var agent = Agent();
        var key = new AgentSessionStoreKey("lookup");
        var first = await store.GetOrCreateSessionAsync(agent, key);
        var second = await store.GetOrCreateSessionAsync(agent, key);
        await store.SaveSessionAsync(agent, key, first);

        await Assert.ThrowsAsync<SessionSnapshotConflictException>(() =>
            store.SaveSessionAsync(agent, key, second).AsTask());
    }

    [Fact]
    public async Task AnonymousResponsesAliasesKeepOriginalHistoryAnchor()
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = Store(sdk);
        var agent = Agent();
        var initialKey = new AgentSessionStoreKey("resp_1");
        var alias = new AgentSessionStoreKey("conv_1");
        var session = await store.GetOrCreateSessionAsync(agent, initialKey);
        var originalHistory = SessionPersistenceState.GetRequired(session).ActiveHistory;
        await store.SaveSessionAsync(agent, initialKey, session);
        await store.SaveSessionAsync(agent, alias, session);

        var loadedAlias = await store.GetSessionAsync(agent, alias);

        Assert.Equal(originalHistory, SessionPersistenceState.GetRequired(loadedAlias!).ActiveHistory);
    }

    [Fact]
    public async Task EachResponsesAliasHasItsOwnCreateAndReplaceETag()
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = Store(sdk);
        var agent = Agent();
        var first = new AgentSessionStoreKey("resp_1");
        var second = new AgentSessionStoreKey("conv_1");
        var session = await store.GetOrCreateSessionAsync(agent, first);
        await store.SaveSessionAsync(agent, first, session);
        await store.SaveSessionAsync(agent, second, session);
        await store.SaveSessionAsync(agent, first, session);
        await store.SaveSessionAsync(agent, second, session);

        Assert.Equal(new[] { ("create", (string?)null), ("create", null),
            ("replace", "version-1"), ("replace", "version-2") },
            sdk.Requests.Where(r => r.Operation != "read").Select(r => (r.Operation, r.ETag)).ToArray());
    }

    [Fact]
    public async Task FailedSaveDoesNotAdvanceWorkingCopyToken()
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = Store(sdk);
        var agent = Agent();
        var key = new AgentSessionStoreKey("lookup");
        var session = await store.GetOrCreateSessionAsync(agent, key);
        await store.SaveSessionAsync(agent, key, session);
        sdk.WriteFailure = System.Net.HttpStatusCode.PreconditionFailed;
        await Assert.ThrowsAsync<SessionSnapshotConflictException>(() =>
            store.SaveSessionAsync(agent, key, session).AsTask());
        sdk.WriteFailure = null;

        await store.SaveSessionAsync(agent, key, session);

        Assert.Equal("version-1", sdk.Requests.Last().ETag);
    }

    [Fact]
    public async Task ChangedAuthenticatedOwnerCannotSaveExistingContext()
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = Store(sdk);
        var agent = Agent();
        var key = new AgentSessionStoreKey("lookup", new Dictionary<string, string> { ["isolation"] = "owner" });
        var session = await store.GetOrCreateSessionAsync(agent, key);
        var other = new AgentSessionStoreKey("alias", new Dictionary<string, string> { ["isolation"] = "other" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSessionAsync(agent, other, session).AsTask());
    }

    [Fact]
    public async Task SnapshotContextCannotBeReadUnderDifferentAgent()
    {
        var sdk = new SessionCosmosSdkFixture();
        var agent = Agent();
        var session = await agent.CreateSessionAsync();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create("other", "lookup"));
        var address = SessionStorageAddress.Create(agent.Id, "lookup");
        var document = SessionDocument.Create(address, await agent.SerializeSessionAsync(session), DateTimeOffset.UtcNow);
        sdk.Seed(address, document.SerializeToUtf8Bytes());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(sdk).GetSessionAsync(agent, new("lookup")).AsTask());
    }

    [Fact]
    public async Task MissingSnapshotContextIsNotSilentlyInitializedOnRead()
    {
        var sdk = new SessionCosmosSdkFixture();
        var agent = Agent();
        var address = SessionStorageAddress.Create(agent.Id, "lookup");
        var document = SessionDocument.Create(address, await agent.SerializeSessionAsync(await agent.CreateSessionAsync()),
            DateTimeOffset.UtcNow);
        sdk.Seed(address, document.SerializeToUtf8Bytes());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(sdk).GetOrCreateSessionAsync(agent, new("lookup")).AsTask());
    }

    [Fact]
    public async Task MissingContextCannotBeSaved()
    {
        var sdk = new SessionCosmosSdkFixture();
        var agent = Agent();
        var session = await agent.CreateSessionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Store(sdk).SaveSessionAsync(agent, new("lookup"), session).AsTask());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(604800)]
    public async Task SnapshotRetentionIsPreserved(int ttl)
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = Store(sdk, ttl);
        var agent = Agent();
        var key = new AgentSessionStoreKey("lookup");
        await store.SaveSessionAsync(agent, key, await store.GetOrCreateSessionAsync(agent, key));

        using var body = JsonDocument.Parse(sdk.Requests.Last().Body);

        Assert.Equal(ttl, body.RootElement.GetProperty("ttl").GetInt32());
    }

    [Fact]
    public async Task SameLookupForDifferentAuthenticatedOwnersHasDifferentPartitions()
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = Store(sdk);
        var agent = Agent();
        var first = new AgentSessionStoreKey("lookup", new Dictionary<string, string> { ["isolation"] = "one" });
        var second = new AgentSessionStoreKey("lookup", new Dictionary<string, string> { ["isolation"] = "two" });
        await store.SaveSessionAsync(agent, first, await store.GetOrCreateSessionAsync(agent, first));
        await store.SaveSessionAsync(agent, second, await store.GetOrCreateSessionAsync(agent, second));

        Assert.Equal(2, sdk.Requests.Where(r => r.Operation == "create").Select(r => r.Partition).Distinct().Count());
    }

    [Fact]
    public async Task SavePassesCancellationToSdk()
    {
        var sdk = new SessionCosmosSdkFixture();
        var store = Store(sdk);
        var agent = Agent();
        var key = new AgentSessionStoreKey("lookup");
        var session = await store.GetOrCreateSessionAsync(agent, key);
        using var cancellation = new CancellationTokenSource();

        await store.SaveSessionAsync(agent, key, session, cancellation.Token);

        Assert.Equal(cancellation.Token, sdk.Requests.Last().CancellationToken);
    }
}
