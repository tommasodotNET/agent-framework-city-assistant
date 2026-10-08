using System.Data.Common;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SharedServices;

#pragma warning disable MAAI001 // Exercise the installed session-store contract on the real emulator.

namespace SharedServices.Tests;

/// <summary>
/// Opt-in verification against a real loopback emulator. Only this fixture's unique test database
/// is created/deleted; application containers and non-loopback accounts are never touched.
/// </summary>
public sealed class CosmosEmulatorIntegrationTests
{
    [CosmosEmulatorFact]
    public async Task Session_hierarchical_keys_point_reads_and_conditional_writes_work_on_Cosmos()
    {
        await using var database = await EmulatorDatabase.CreateAsync();
        var repository = new CosmosSessionRepository(database.Sessions, NullLogger.Instance);
        var address = SessionStorageAddress.Create("emulator-agent", "+39-external",
            new Dictionary<string, string> { ["isolation"] = "person-1" });
        var document = SessionDocument.Create(address, JsonSerializer.SerializeToElement(new { marker = "original" }),
            DateTimeOffset.UtcNow);

        Assert.Null(await repository.ReadAsync(address));
        var original = await repository.WriteAsync(document, SessionWriteCondition.CreateOnly(address));
        Assert.False(original.IsCreateOnly);
        var read = await repository.ReadAsync(address);
        Assert.NotNull(read);
        Assert.Equal("original", read.Document.SerializedSession.GetProperty("marker").GetString());
        await Assert.ThrowsAsync<SessionSnapshotConflictException>(() =>
            repository.WriteAsync(document, SessionWriteCondition.CreateOnly(address)));
        await repository.WriteAsync(document, read.Version);
        await Assert.ThrowsAsync<SessionSnapshotConflictException>(() => repository.WriteAsync(document, original));

        var otherCaller = SessionStorageAddress.Create("emulator-agent", "+39-external",
            new Dictionary<string, string> { ["isolation"] = "person-2" });
        Assert.Null(await repository.ReadAsync(otherCaller));
        using var iterator = database.Sessions.GetItemQueryStreamIterator(
            new QueryDefinition("SELECT * FROM c WHERE c.scopeKey = @scope").WithParameter("@scope", address.ScopeKey),
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKeyBuilder().Add(address.ScopeKey).Build()
            });
        using var response = await iterator.ReadNextAsync();
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(response.Content);
        Assert.Single(json.RootElement.GetProperty("Documents").EnumerateArray());
    }

    [CosmosEmulatorFact]
    public async Task History_transactional_batch_and_revision_checks_work_on_Cosmos()
    {
        await using var database = await EmulatorDatabase.CreateAsync();
        var repository = new CosmosChatMessageRepository(database.Conversations);
        var history = new HistoryReference(StorageScope.Create("context-1"), Guid.NewGuid().ToString("N"), 0);
        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "Unicode: \u00e8 \u6771\u4eac"),
            new(ChatRole.Assistant, "reply")
        };

        var written = await repository.AppendAsync(history, messages, 604800);
        var loaded = await repository.ReadAsync(written.Reference);
        Assert.Equal(messages.Select(message => message.Text), loaded.Messages.Select(message => message.Text));
        Assert.Equal(2, await repository.CountAsync(written.Reference));
        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => repository.AppendAsync(history, messages));

        using var metadata = await database.Conversations.ReadItemStreamAsync(
            "history-head", history.ToAddress().ToPartitionKey());
        metadata.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(metadata.Content);
        Assert.Equal(-1, json.RootElement.GetProperty("ttl").GetInt32());
        Assert.Equal(written.Reference.Revision, json.RootElement.GetProperty("revision").GetInt64());
    }

    [CosmosEmulatorFact]
    public async Task Per_document_TTL_is_enabled_with_container_default_minus_one()
    {
        await using var database = await EmulatorDatabase.CreateAsync();
        var repository = new CosmosSessionRepository(database.Sessions, NullLogger.Instance);
        var address = SessionStorageAddress.Create("ttl-agent", "temporary");
        var document = SessionDocument.Create(address, JsonSerializer.SerializeToElement(new { value = 1 }),
            DateTimeOffset.UtcNow, ttl: 2);
        await repository.WriteAsync(document, SessionWriteCondition.CreateOnly(address));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (await repository.ReadAsync(address, timeout.Token) is not null)
        {
            await Task.Delay(250, timeout.Token);
        }
        Assert.Equal(-1, (await database.Sessions.ReadContainerAsync()).Resource.DefaultTimeToLive);
    }

    [CosmosEmulatorFact]
    public async Task Maf_session_and_external_history_resume_after_a_real_Cosmos_roundtrip()
    {
        await using var database = await EmulatorDatabase.CreateAsync();
        var client = new Mock<IChatClient>();
        ChatMessage[] suppliedMessages = [];
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> messages, ChatOptions? _, CancellationToken _) =>
            {
                suppliedMessages = messages.ToArray();
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "test reply")));
            });
        using var history = new CosmosChatHistoryProvider(new CosmosChatMessageRepository(database.Conversations));
        var agent = new ChatClientAgent(client.Object, new ChatClientAgentOptions
        {
            Id = "real-roundtrip-agent",
            Name = "real-roundtrip-agent",
            ChatHistoryProvider = history
        });
        var store = new CosmosAgentSessionStore(
            new CosmosSessionRepository(database.Sessions, NullLogger.Instance),
            NullLogger<CosmosAgentSessionStore>.Instance);
        var key = new AgentSessionStoreKey("opaque-context");
        var session = await store.GetOrCreateSessionAsync(agent, key);
        session.StateBag.SetValue("custom-message", new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("pending-call", "lookup", new Dictionary<string, object?> { ["query"] = "value" })]));
        await agent.RunAsync("first turn", session);
        await store.SaveSessionAsync(agent, key, session);

        var restored = await store.GetSessionAsync(agent, key);
        Assert.NotNull(restored);
        Assert.NotSame(session, restored);
        var customMessage = restored.StateBag.GetValue<ChatMessage>("custom-message");
        Assert.NotNull(customMessage);
        Assert.Equal("pending-call", Assert.IsType<FunctionCallContent>(Assert.Single(customMessage.Contents)).CallId);
        await agent.RunAsync("second turn", restored);
        await store.SaveSessionAsync(agent, key, restored);

        Assert.Contains(suppliedMessages, message => message.Text == "first turn");
        Assert.Contains(suppliedMessages, message => message.Text == "test reply");
        Assert.Contains(suppliedMessages, message => message.Text == "second turn");
        Assert.Equal(4, await history.GetMessageCountAsync(restored));
    }

    [CosmosEmulatorFact]
    public async Task Compaction_rotation_preserves_source_and_fences_stale_writers_on_Cosmos()
    {
        await using var database = await EmulatorDatabase.CreateAsync();
        var repository = new CosmosChatMessageRepository(database.Conversations);
        var initial = new HistoryReference(StorageScope.Create("compaction-test"), Guid.NewGuid().ToString("N"), 0);
        var source = (await repository.AppendAsync(initial,
            [new(ChatRole.User, "Remember the museum."), new(ChatRole.Assistant, "Confirmed.")], 604800)).Reference;
        var operationId = Guid.NewGuid().ToString("N");
        ChatMessage[] compacted = [new(ChatRole.Assistant, "[Summary] The user asked about the museum.")];

        var target = await repository.RotateAsync(source, compacted, 604800, operationId);
        Assert.NotEqual(source.ConversationId, target.ConversationId);
        Assert.Equal(source.ScopeKey, target.ScopeKey);
        Assert.Equal(target, await repository.RotateAsync(source, compacted, 604800, operationId));
        Assert.Equal(target, await repository.ResolveRotationAsync(source));
        Assert.Equal(compacted[0].Text, Assert.Single((await repository.ReadAsync(target)).Messages).Text);
        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            repository.AppendAsync(source, [new(ChatRole.User, "stale turn")]));

        using var originalItems = database.Conversations.GetItemQueryIterator<JsonElement>(
            "SELECT * FROM c WHERE c.type = 'ChatMessage'",
            requestOptions: new QueryRequestOptions { PartitionKey = source.ToAddress().ToPartitionKey() });
        var sourceMessages = new List<JsonElement>();
        while (originalItems.HasMoreResults) sourceMessages.AddRange(await originalItems.ReadNextAsync());
        Assert.Equal(2, sourceMessages.Count);
        Assert.All(sourceMessages, item => Assert.Equal(604800, item.GetProperty("ttl").GetInt32()));

        var advanced = await repository.AppendAsync(target, [new(ChatRole.User, "next turn")]);
        Assert.Equal(2, await repository.CountAsync(advanced.Reference));
        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => repository.ResolveRotationAsync(source));
    }

    [CosmosEmulatorFact]
    public async Task Native_MAF_summary_can_be_published_without_rewriting_original_Cosmos_messages()
    {
        await using var database = await EmulatorDatabase.CreateAsync();
        var repository = new CosmosChatMessageRepository(database.Conversations);
        var initial = new HistoryReference(StorageScope.Create("maf-compaction"), Guid.NewGuid().ToString("N"), 0);
        ChatMessage[] messages =
        [
            new(ChatRole.User, "My reference code is EMERALD-42. " + new string('x', 2000)),
            new(ChatRole.Assistant, "Recorded. " + new string('y', 2000)),
            new(ChatRole.User, "Keep my last question."),
            new(ChatRole.Assistant, "I will.")
        ];
        var source = (await repository.AppendAsync(initial, messages, 604800)).Reference;
        var model = new Mock<IChatClient>();
        model.Setup(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The reference code is EMERALD-42.")));
        var compactor = new MafForegroundHistoryCompactor(new SummarizationCompactionStrategy(
            model.Object, CompactionTriggers.Always, minimumPreservedGroups: 2));
        var result = await compactor.CompactAsync(new HistoryCompactionRequest("test-agent", "exact-source",
            (await repository.ReadAsync(source)).Messages,
            new HistoryCompactionOptions { CompactorKey = "summary", MaxHistoryUtf8Bytes = 10_000 }));

        Assert.Equal(HistoryCompactionStatus.Completed, result.Status);
        var target = await repository.RotateAsync(source, result.Messages, 604800, Guid.NewGuid().ToString("N"));
        var loaded = await repository.ReadAsync(target);
        Assert.Contains(loaded.Messages, message => message.Text.Contains("EMERALD-42", StringComparison.Ordinal));
        Assert.Contains(loaded.Messages, message => message.Text == "Keep my last question.");
        Assert.True(result.AfterUtf8Bytes < result.BeforeUtf8Bytes);
        model.Verify(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);

        using var originalItems = database.Conversations.GetItemQueryIterator<JsonElement>(
            "SELECT * FROM c WHERE c.type = 'ChatMessage'",
            requestOptions: new QueryRequestOptions { PartitionKey = source.ToAddress().ToPartitionKey() });
        var originals = new List<JsonElement>();
        while (originalItems.HasMoreResults) originals.AddRange(await originalItems.ReadNextAsync());
        Assert.Equal(messages.Length, originals.Count);
        Assert.Contains(originals, item => item.GetRawText().Contains(new string('x', 2000), StringComparison.Ordinal));
    }

    [CosmosEmulatorFact]
    public async Task Foreground_provider_rotates_on_save_and_resumes_its_checkpoint_on_Cosmos()
    {
        await using var database = await EmulatorDatabase.CreateAsync();
        var repository = new CosmosChatMessageRepository(database.Conversations);
        using var provider = new CosmosChatHistoryProvider(repository,
            compactor: new MafForegroundHistoryCompactor(new SlidingWindowCompactionStrategy(
                CompactionTriggers.TurnsExceed(2), minimumPreservedTurns: 2)),
            compactionOptions: new() { CompactorKey = "test", MaxHistoryUtf8Bytes = 100_000 });
        var model = new Mock<IChatClient>();
        model.Setup(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        var agent = new ChatClientAgent(model.Object, new ChatClientAgentOptions
        {
            Id = "foreground-test", Name = "foreground-test", ChatHistoryProvider = provider
        });
        var store = new CosmosAgentSessionStore(new CosmosSessionRepository(database.Sessions, NullLogger.Instance),
            NullLogger<CosmosAgentSessionStore>.Instance);
        var key = new AgentSessionStoreKey("foreground-context");
        HistoryReference? original = null;
        HistoryReference? rotated = null;
        for (var turn = 1; turn <= 4; turn++)
        {
            var session = await store.GetOrCreateSessionAsync(agent, key);
            var beforeRun = SessionPersistenceState.GetRequired(session).ActiveHistory;
            model.Setup(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Assert.Equal(beforeRun, SessionPersistenceState.GetRequired(session).ActiveHistory);
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
                });
            await agent.RunAsync($"question-{turn}", session);
            await store.SaveSessionAsync(agent, key, session);
            var reference = SessionPersistenceState.GetRequired(session).ActiveHistory;
            if (turn == 3) original = reference;
            if (turn == 4) rotated = reference;
        }

        Assert.NotNull(original);
        Assert.NotNull(rotated);
        Assert.NotEqual(original.ConversationId, rotated.ConversationId);
        var checkpoint = await store.GetSessionAsync(agent, key);
        Assert.Equal(rotated, SessionPersistenceState.GetRequired(checkpoint!).ActiveHistory);
        var active = await repository.ReadAsync(rotated);
        Assert.Equal(6, active.Messages.Count);
        Assert.DoesNotContain(active.Messages, message => message.Text == "question-1");
        Assert.Contains(active.Messages, message => message.Text == "question-2");
        Assert.Contains(active.Messages, message => message.Text == "question-4");

        using var originals = database.Conversations.GetItemQueryIterator<int>(
            "SELECT VALUE COUNT(1) FROM c WHERE c.type = 'ChatMessage'",
            requestOptions: new QueryRequestOptions { PartitionKey = original.ToAddress().ToPartitionKey() });
        Assert.Equal(6, (await originals.ReadNextAsync()).Single());
    }

    [CosmosEmulatorFact]
    public async Task Background_provider_resumes_ticket_and_merges_exact_suffix_on_Cosmos()
    {
        await using var database = await EmulatorDatabase.CreateAsync();
        var repository = new CosmosChatMessageRepository(database.Conversations);
        var compactor = new Mock<IHistoryCompactor>();
        compactor.SetupGet(value => value.SupportedModes).Returns(new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Background });
        HistoryCompactionRequest? job = null;
        var ready = false;
        compactor.Setup(value => value.CompactAsync(It.IsAny<HistoryCompactionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((HistoryCompactionRequest request, CancellationToken _) =>
            {
                job = request;
                return Task.FromResult(HistoryCompactionResult.Pending(new("durable-test-job", request.SourceBinding)));
            });
        compactor.Setup(value => value.GetResultAsync(It.IsAny<HistoryCompactionTicket>(), It.IsAny<CancellationToken>()))
            .Returns((HistoryCompactionTicket ticket, CancellationToken _) =>
            {
                ChatMessage[] summary = [new(ChatRole.User, "summary")];
                return Task.FromResult(ready
                    ? new HistoryCompactionResult(HistoryCompactionStatus.Completed, ticket.SourceBinding, summary,
                        HistoryCompactionValidation.Measure(job!.Messages), HistoryCompactionValidation.Measure(summary))
                    : HistoryCompactionResult.Pending(ticket));
            });
        var options = new HistoryCompactionOptions { CompactorKey = "test", Mode = HistoryCompactionMode.Background };
        using var provider = new CosmosChatHistoryProvider(repository, compactor: compactor.Object, compactionOptions: options);
        var model = new Mock<IChatClient>();
        model.Setup(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")));
        ChatClientAgent Agent(CosmosChatHistoryProvider history) => new(model.Object,
            new ChatClientAgentOptions { Id = "background-emulator", Name = "background-emulator", ChatHistoryProvider = history });
        CosmosAgentSessionStore Store() => new(new CosmosSessionRepository(database.Sessions, NullLogger.Instance),
            NullLogger<CosmosAgentSessionStore>.Instance);
        var agent = Agent(provider);
        var store = Store();
        var key = new AgentSessionStoreKey("background-context");
        var session = await store.GetOrCreateSessionAsync(agent, key);
        await agent.RunAsync(new string('x', 800), session);
        await agent.RunAsync("first retained", session);
        await store.SaveSessionAsync(agent, key, session);
        var original = SessionPersistenceState.GetRequired(session);
        Assert.NotNull(original.PendingCompaction);

        using var replicaProvider = new CosmosChatHistoryProvider(new CosmosChatMessageRepository(database.Conversations),
            compactor: compactor.Object, compactionOptions: options);
        var replicaAgent = Agent(replicaProvider);
        var replicaStore = Store();
        var restored = await replicaStore.GetSessionAsync(replicaAgent, key);
        Assert.NotNull(restored);
        ready = true;
        await replicaAgent.RunAsync("second retained", restored);
        await replicaStore.SaveSessionAsync(replicaAgent, key, restored);

        var final = SessionPersistenceState.GetRequired(restored);
        Assert.Null(final.PendingCompaction);
        Assert.NotEqual(original.ActiveHistory.ConversationId, final.ActiveHistory.ConversationId);
        Assert.Equal(new[] { "summary", "first retained", "reply", "second retained", "reply" },
            (await repository.ReadAsync(final.ActiveHistory)).Messages.Select(message => message.Text));
        compactor.Verify(value => value.CompactAsync(It.IsAny<HistoryCompactionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            repository.AppendAsync(original.ActiveHistory, [new(ChatRole.User, "stale")]));
    }

    private sealed class EmulatorDatabase(CosmosClient client, Database database,
        Container sessions, Container conversations) : IAsyncDisposable
    {
        public Container Sessions { get; } = sessions;
        public Container Conversations { get; } = conversations;

        public static async Task<EmulatorDatabase> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable(CosmosEmulatorFactAttribute.ConnectionVariable)
                ?? throw new InvalidOperationException("Configure the local Cosmos emulator before running integration tests.");
            var connection = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (!connection.TryGetValue("AccountEndpoint", out var value)
                || !Uri.TryCreate(value.ToString(), UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback)
            {
                throw new InvalidOperationException("Integration tests only accept a loopback emulator endpoint, never an Azure account.");
            }
            var client = new CosmosClient(connectionString, new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                Serializer = new CosmosSystemTextJsonSerializer(),
                HttpClientFactory = () => new HttpClient(new HttpClientHandler
                {
                    // The connection target above is loopback; a self-signed emulator certificate is expected.
                    ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.IsLoopback == true
                })
            });
            Database? database = null;
            try
            {
                database = await client.CreateDatabaseAsync($"city-assistant-redesign-test-{Guid.NewGuid():N}");
                Container sessions = await database.CreateContainerAsync(
                    new ContainerProperties("sessions", new[] { "/scopeKey", "/sessionId" }) { DefaultTimeToLive = -1 });
                Container conversations = await database.CreateContainerAsync(
                    new ContainerProperties("conversations", new[] { "/scopeKey", "/conversationId" }) { DefaultTimeToLive = -1 });
                return new(client, database, sessions, conversations);
            }
            catch
            {
                try
                {
                    if (database is not null) await database.DeleteAsync();
                }
                finally
                {
                    client.Dispose();
                }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await database.DeleteAsync();
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}

public sealed class CosmosEmulatorFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "CITY_ASSISTANT_COSMOS_TEST_CONNECTION";

    public CosmosEmulatorFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            Skip = $"Real Cosmos emulator unavailable. Set {ConnectionVariable} to a loopback emulator connection string.";
        }
    }
}
