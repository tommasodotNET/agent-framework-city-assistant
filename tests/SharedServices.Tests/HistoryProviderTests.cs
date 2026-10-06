using System.Net;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;

#pragma warning disable MAAI001 // Exercise the public MAF 1.23 provider contexts.
#pragma warning disable MEAI001 // Exercise the preserved opt-in reducer, not a new compaction algorithm.

namespace SharedServices.Tests;

public class HistoryProviderTests
{
    [Fact]
    public void StateKeysIncludesOnlySharedPersistenceContext()
    {
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository());

        Assert.Equal(new[] { SessionPersistenceState.StateKey }, provider.StateKeys);
    }

    [Fact]
    public async Task MissingContextFailsWithoutAnonymousFallback()
    {
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetMessageCountAsync(new TestAgentSession()));
    }

    [Fact]
    public async Task InitializedStandaloneSessionCanReadHistory()
    {
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository());
        var session = NewSession();

        var count = await provider.GetMessageCountAsync(session);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ProviderUsesHistoryAnchorNotCurrentContinuationId()
    {
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository());
        var session = NewSession();
        var context = SessionPersistenceState.GetRequired(session);
        context.ValidateFor(SessionStorageAddress.Create("history-test-agent", "different-response-id"));

        await provider.GetMessageCountAsync(session);

        Assert.Equal(context.ActiveHistory.ToAddress().ToPartitionKey(), Assert.Single(fixture.Queries).Options.PartitionKey);
    }

    [Fact]
    public async Task StoringMessagesUpdatesTheSingleSharedCursor()
    {
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository());
        var session = NewSession();

        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], [new(ChatRole.Assistant, "two")]));

        Assert.Equal(1, SessionPersistenceState.GetRequired(session).ActiveHistory.Revision);
    }

    [Fact]
    public async Task SuccessfulClearAdvancesSharedCursor()
    {
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository());
        var session = NewSession();
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], []));

        await provider.ClearMessagesAsync(session);

        Assert.Equal(2, SessionPersistenceState.GetRequired(session).ActiveHistory.Revision);
    }

    [Fact]
    public async Task FailedStoreDoesNotAdvanceSharedCursor()
    {
        var fixture = new HistoryCosmosFixture();
        fixture.BatchFailures[1] = HttpStatusCode.BadRequest;
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository());
        var session = NewSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], [])).AsTask());

        Assert.Equal(0, SessionPersistenceState.GetRequired(session).ActiveHistory.Revision);
    }

    [Fact]
    public async Task PartialStoreAdvancesSharedCursorOnlyForCommittedChunks()
    {
        var fixture = new HistoryCosmosFixture();
        fixture.BatchFailures[2] = HttpStatusCode.BadRequest;
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository()) { MaxBatchSize = 2 };
        var session = NewSession();
        await Assert.ThrowsAsync<HistoryPartialWriteException>(() =>
            provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], [new(ChatRole.Assistant, "two")])).AsTask());

        Assert.Equal(1, SessionPersistenceState.GetRequired(session).ActiveHistory.Revision);
    }

    [Fact]
    public async Task FailedClearDoesNotAdvanceSharedCursor()
    {
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository());
        var session = NewSession();
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], []));
        fixture.BatchFailures[2] = HttpStatusCode.BadRequest;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ClearMessagesAsync(session));

        Assert.Equal(1, SessionPersistenceState.GetRequired(session).ActiveHistory.Revision);
    }

    [Fact]
    public async Task StaleContextIsRejectedBeforeProviderReturnsHistory()
    {
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository());
        var session = NewSession();
        fixture.SeedHead(SessionPersistenceState.GetRequired(session).ActiveHistory.WithRevision(1));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => provider.InvokingAsync(new(Agent(), session, [])).AsTask());
    }

    [Fact]
    public async Task DefaultRequestFilterDoesNotPersistProvidedHistoryAgain()
    {
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository());
        var session = NewSession();
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], []));
        var invoking = await provider.InvokingAsync(new(Agent(), session, [new(ChatRole.User, "two")]));

        await provider.InvokedAsync(new(Agent(), session, invoking, [new(ChatRole.Assistant, "three")]));

        Assert.Equal(3, await provider.GetMessageCountAsync(session));
    }

    [Fact]
    public async Task CustomRequestFilterPreservesResponseMessages()
    {
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository(),
            storeInputMessageFilter: _ => []);
        var session = NewSession();

        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], [new(ChatRole.Assistant, "two")]));

        Assert.Equal(1, await provider.GetMessageCountAsync(session));
    }

    [Fact]
    public async Task OutputFilterDoesNotDeletePersistedHistory()
    {
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository(),
            provideOutputMessageFilter: _ => []);
        var session = NewSession();
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], []));
        await provider.InvokingAsync(new(Agent(), session, []));

        Assert.Equal(1, await provider.GetMessageCountAsync(session));
    }

    [Fact]
    public async Task LatestMessageLimitIsPreservedByProvider()
    {
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository()) { MaxMessagesToRetrieve = 1 };
        var session = NewSession();
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], [new(ChatRole.Assistant, "two")]));

        var messages = await provider.InvokingAsync(new(Agent(), session, []));

        Assert.Equal("two", Assert.Single(messages).Text);
    }

    [Fact]
    public void ReducerIsOffByDefault()
    {
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository());

        Assert.Null(provider.ChatReducer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(604800)]
    public async Task SessionRetentionCannotExpireHistoryMetadata(int? sessionTtl)
    {
        var fixture = new HistoryCosmosFixture();
        var client = new Mock<CosmosClient>();
        var database = new Mock<Database>();
        database.SetupGet(value => value.Id).Returns("database");
        database.SetupGet(value => value.Client).Returns(client.Object);
        fixture.Container.SetupGet(value => value.Database).Returns(database.Object);
        fixture.Container.SetupGet(value => value.Id).Returns("conversations");
        client.Setup(value => value.GetContainer("database", "conversations")).Returns(fixture.Container.Object);
        var services = new ServiceCollection();
        services.AddKeyedSingleton("history", fixture.Container.Object);
        services.AddCosmosChatHistoryProvider("history");
        if (sessionTtl.HasValue) services.AddSingleton(new CosmosAgentSessionStoreOptions { TtlSeconds = sessionTtl.Value });
        using var serviceProvider = services.BuildServiceProvider();

        var options = new ChatClientAgentOptions().WithCosmosChatHistoryProvider(serviceProvider);
        using var provider = Assert.IsType<CosmosChatHistoryProvider>(options.ChatHistoryProvider);
        await provider.InvokedAsync(new(Agent(), NewSession(), [new(ChatRole.User, "one")], []));

        Assert.Equal(-1, fixture.Documents.Single(document => document.GetProperty("type").GetString() == "HistoryHead").GetProperty("ttl").GetInt32());
    }

    [Fact]
    public void StandaloneExtensionCanDisableMessageExpiry()
    {
        var fixture = new HistoryCosmosFixture();
        var client = new Mock<CosmosClient>();
        client.Setup(value => value.GetContainer("database", "conversations")).Returns(fixture.Container.Object);

        var options = new ChatClientAgentOptions().WithCosmosDBChatHistoryProvider(client.Object, "database", "conversations",
            configuration => configuration.MessageTtlSeconds = null);
        using var provider = Assert.IsType<CosmosChatHistoryProvider>(options.ChatHistoryProvider);

        Assert.Null(provider.MessageTtlSeconds);
    }

    [Fact]
    public async Task DisposedProviderRejectsClear()
    {
        var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository());
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.ClearMessagesAsync(NewSession()));
    }

    [Theory]
    [InlineData(ReductionStoragePolicy.Clear)]
    [InlineData(ReductionStoragePolicy.Archive)]
    public async Task ExplicitLegacyReductionReplacesActiveHistoryUsingCas(ReductionStoragePolicy policy)
    {
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository())
        {
            ChatReducer = Reducer(), ReductionStoragePolicy = policy
        };
        var session = NewSession();
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], [new(ChatRole.Assistant, "two")]));

        await provider.InvokingAsync(new(Agent(), session, []));

        Assert.Equal(3, SessionPersistenceState.GetRequired(session).ActiveHistory.Revision);
    }

    [Fact]
    public async Task ExplicitLegacyArchivePreservesUnexpiredOriginalsInSeparatePartition()
    {
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository())
        {
            ChatReducer = Reducer(), ReductionStoragePolicy = ReductionStoragePolicy.Archive
        };
        var session = NewSession();
        var originalId = SessionPersistenceState.GetRequired(session).ActiveHistory.ConversationId;
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], [new(ChatRole.Assistant, "two")]));

        await provider.InvokingAsync(new(Agent(), session, []));

        Assert.Equal(2, fixture.Documents.Count(document => document.GetProperty("type").GetString() == "ChatMessage"
            && document.GetProperty("conversationId").GetString() != originalId && document.GetProperty("ttl").GetInt32() == -1));
    }

    [Fact]
    public async Task LimitedReadBypassesExplicitLegacyReducer()
    {
        var fixture = new HistoryCosmosFixture();
        var reducer = new Mock<IChatReducer>(MockBehavior.Strict);
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository())
        {
            ChatReducer = reducer.Object, MaxMessagesToRetrieve = 1
        };
        var session = NewSession();
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "one")], [new(ChatRole.Assistant, "two")]));

        await provider.InvokingAsync(new(Agent(), session, []));

        Assert.Equal(1, SessionPersistenceState.GetRequired(session).ActiveHistory.Revision);
    }

    private static IChatReducer Reducer()
    {
        var reducer = new Mock<IChatReducer>();
        reducer.Setup(value => value.ReduceAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ChatMessage(ChatRole.System, "legacy summary") });
        return reducer.Object;
    }

    private static AIAgent Agent() => new Mock<AIAgent>().Object;

    private static TestAgentSession NewSession()
    {
        var session = new TestAgentSession();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create("history-test-agent", "response-id"));
        return session;
    }
}
