using System.Data.Common;
using System.Text.Json;
using Microsoft.Agents.AI;
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
