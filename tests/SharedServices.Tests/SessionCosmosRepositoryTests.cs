using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Moq;

namespace SharedServices.Tests;

public class SessionCosmosRepositoryTests
{
    private static SessionDocument Document(SessionStorageAddress address, string json = """{"unknown":{"unicode":"città"}}""") =>
        SessionDocument.Create(address, JsonSerializer.Deserialize<JsonElement>(json), DateTimeOffset.UnixEpoch, 604800);

    [Fact]
    public async Task CreateUsesReversibleAgentIdAndFullPartition()
    {
        var sdk = new SessionCosmosSdkFixture();
        var address = SessionStorageAddress.Create("agent/name", "resp_1",
            new Dictionary<string, string> { ["isolation"] = "owner", ["region"] = "it" });
        var document = Document(address);

        await sdk.Repository.WriteAsync(document, SessionWriteCondition.CreateOnly(address));

        Assert.Equal(new SessionSdkRequest("create", address.DocumentId, address.ToPartitionKey(), null,
            document.SerializeToUtf8Bytes(), default) with { Body = [] },
            Assert.Single(sdk.Requests) with { Body = [] });
    }

    [Fact]
    public async Task CreateWritesExactBudgetValidatedBytes()
    {
        var sdk = new SessionCosmosSdkFixture();
        var address = SessionStorageAddress.Create("agent", "lookup");
        var document = Document(address);

        await sdk.Repository.WriteAsync(document, SessionWriteCondition.CreateOnly(address));

        Assert.Equal(document.SerializeToUtf8Bytes(), Assert.Single(sdk.Requests).Body);
    }

    [Fact]
    public async Task ReadReturnsVersionAndSnapshotFromExactPointRead()
    {
        var sdk = new SessionCosmosSdkFixture();
        var address = SessionStorageAddress.Create("agent", "phone/+39");
        sdk.Seed(address, Document(address).SerializeToUtf8Bytes(), "loaded");

        var result = await sdk.Repository.ReadAsync(address);

        Assert.Equal(("loaded", address.ToPartitionKey(), "città"),
            (result!.Version.ETag, Assert.Single(sdk.Requests).Partition,
                result.Document.SerializedSession.GetProperty("unknown").GetProperty("unicode").GetString()));
    }

    [Fact]
    public async Task ReadAcceptsANonSeekableResponseAndDisposesIt()
    {
        var sdk = new SessionCosmosSdkFixture();
        var address = SessionStorageAddress.Create("agent", "nonseekable");
        var stream = new NonSeekableReadStream(Document(address).SerializeToUtf8Bytes());
        var response = new ResponseMessage(HttpStatusCode.OK) { Content = stream };
        response.Headers["etag"] = "loaded";
        sdk.Container.Setup(container => container.ReadItemStreamAsync(address.DocumentId,
            address.ToPartitionKey(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await sdk.Repository.ReadAsync(address);

        Assert.Equal("città", result!.Document.SerializedSession.GetProperty("unknown").GetProperty("unicode").GetString());
        Assert.Equal("loaded", result.Version.ETag);
        Assert.False(stream.CanRead);
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    [Fact]
    public async Task LoadedVersionReplacesWithIfMatchAndReturnsNewVersion()
    {
        var sdk = new SessionCosmosSdkFixture();
        var address = SessionStorageAddress.Create("agent", "lookup");
        sdk.Seed(address, Document(address).SerializeToUtf8Bytes(), "loaded");
        var read = await sdk.Repository.ReadAsync(address);

        var version = await sdk.Repository.WriteAsync(Document(address), read!.Version);

        Assert.Equal(("loaded", "replace", "version-1"),
            (sdk.Requests.Last().ETag, sdk.Requests.Last().Operation, version.ETag));
    }

    [Fact]
    public async Task MissingItemReturnsNullAfterRecheckingContainer()
    {
        var sdk = new SessionCosmosSdkFixture();

        var result = await sdk.Repository.ReadAsync(SessionStorageAddress.Create("agent", "missing"));

        Assert.Equal((null, 2), (result, sdk.SchemaReads));
    }

    [Fact]
    public async Task MissingContainerDoesNotReturnNull()
    {
        var sdk = new SessionCosmosSdkFixture { SchemaFailure = HttpStatusCode.NotFound };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sdk.Repository.ReadAsync(SessionStorageAddress.Create("agent", "lookup")));
    }

    [Fact]
    public async Task DeletedContainerAfterCachedValidationDoesNotReturnNull()
    {
        var sdk = new SessionCosmosSdkFixture();
        var address = SessionStorageAddress.Create("agent", "lookup");
        sdk.Seed(address, Document(address).SerializeToUtf8Bytes());
        await sdk.Repository.ReadAsync(address);
        sdk.SchemaFailure = HttpStatusCode.NotFound;
        sdk.ItemFailure = HttpStatusCode.NotFound;

        await Assert.ThrowsAsync<InvalidOperationException>(() => sdk.Repository.ReadAsync(address));
    }

    [Fact]
    public async Task SchemaValidationIsCachedOnlyAfterSuccess()
    {
        var sdk = new SessionCosmosSdkFixture
        {
            Properties = new ContainerProperties("sessions", "/conversationId") { DefaultTimeToLive = -1 }
        };
        var address = SessionStorageAddress.Create("agent", "lookup");
        await Assert.ThrowsAsync<InvalidOperationException>(() => sdk.Repository.ReadAsync(address));
        sdk.Properties = new("sessions", new[] { "/scopeKey", "/sessionId" }) { DefaultTimeToLive = -1 };
        sdk.Seed(address, Document(address).SerializeToUtf8Bytes());

        await sdk.Repository.ReadAsync(address);
        await sdk.Repository.ReadAsync(address);

        Assert.Equal(2, sdk.SchemaReads);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task NonMissingReadFaultsSurface(int status)
    {
        var sdk = new SessionCosmosSdkFixture { ItemFailure = (HttpStatusCode)status };

        var error = await Assert.ThrowsAsync<CosmosException>(() =>
            sdk.Repository.ReadAsync(SessionStorageAddress.Create("agent", "lookup")));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
    }

    [Theory]
    [InlineData(409)]
    [InlineData(412)]
    public async Task WriteConflictsAreExplicitWithoutFallback(int status)
    {
        var sdk = new SessionCosmosSdkFixture { WriteFailure = (HttpStatusCode)status };
        var address = SessionStorageAddress.Create("agent", "lookup");

        var error = await Record.ExceptionAsync(() =>
            sdk.Repository.WriteAsync(Document(address), SessionWriteCondition.CreateOnly(address)));

        Assert.Equal((typeof(SessionSnapshotConflictException), 1), (error?.GetType(), sdk.Requests.Count));
    }

    [Fact]
    public async Task ServiceSizeRejectionIsActionable()
    {
        var sdk = new SessionCosmosSdkFixture { WriteFailure = HttpStatusCode.RequestEntityTooLarge };
        var address = SessionStorageAddress.Create("agent", "lookup");

        await Assert.ThrowsAsync<SessionSnapshotTooLargeException>(() =>
            sdk.Repository.WriteAsync(Document(address), SessionWriteCondition.CreateOnly(address)));
    }

    [Fact]
    public async Task OversizedFullEnvelopeIsRejectedBeforeSdkRequest()
    {
        var sdk = new SessionCosmosSdkFixture();
        var address = SessionStorageAddress.Create("agent", "lookup");
        var document = Document(address, JsonSerializer.Serialize(new { text = new string('x', StorageSchema.MaxSessionDocumentBytes) }));

        var error = await Record.ExceptionAsync(() =>
            sdk.Repository.WriteAsync(document, SessionWriteCondition.CreateOnly(address)));

        Assert.Equal((typeof(InvalidOperationException), 0, 0), (error?.GetType(), sdk.Requests.Count, sdk.SchemaReads));
    }

    [Fact]
    public async Task ExactFullEnvelopeBudgetIsWrittenWithoutReserialization()
    {
        var sdk = new SessionCosmosSdkFixture();
        var address = SessionStorageAddress.Create("agent", "lookup");
        var empty = Document(address, """{"text":""}""");
        var remaining = StorageSchema.MaxSessionDocumentBytes - empty.SerializeToUtf8Bytes().Length;
        var document = Document(address, JsonSerializer.Serialize(new { text = new string('x', remaining) }));

        await sdk.Repository.WriteAsync(document, SessionWriteCondition.CreateOnly(address));

        Assert.Equal(StorageSchema.MaxSessionDocumentBytes, Assert.Single(sdk.Requests).Body.Length);
    }

    [Fact]
    public async Task ExactEnvelopeBudgetCanBeReadWithCosmosSystemMetadata()
    {
        var sdk = new SessionCosmosSdkFixture();
        var address = SessionStorageAddress.Create("agent", "lookup");
        var remaining = StorageSchema.MaxSessionDocumentBytes
            - Document(address, """{"text":""}""").SerializeToUtf8Bytes().Length;
        var document = Document(address, JsonSerializer.Serialize(new { text = new string('x', remaining) }));
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document.SerializeToUtf8Bytes())!;
        fields["_etag"] = JsonSerializer.Deserialize<JsonElement>("\"server-etag\"");
        fields["_rid"] = JsonSerializer.Deserialize<JsonElement>("\"server-resource-id\"");
        sdk.Seed(address, JsonSerializer.SerializeToUtf8Bytes(fields));

        var result = await sdk.Repository.ReadAsync(address);

        Assert.Equal(remaining, result!.Document.SerializedSession.GetProperty("text").GetString()!.Length);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    public async Task InvalidJsonOrIncompleteEnvelopeIsNotMissing(string json)
    {
        var sdk = new SessionCosmosSdkFixture();
        var address = SessionStorageAddress.Create("agent", "lookup");
        sdk.Seed(address, Encoding.UTF8.GetBytes(json));

        Assert.NotNull(await Record.ExceptionAsync(() => sdk.Repository.ReadAsync(address)));
    }

    [Theory]
    [InlineData("schemaVersion", "1")]
    [InlineData("serializedSession", "\"{}\"")]
    [InlineData("agentId", "\"other\"")]
    [InlineData("ttl", "0")]
    public async Task InvalidStoredEnvelopeIsRejected(string field, string value)
    {
        var sdk = new SessionCosmosSdkFixture();
        var address = SessionStorageAddress.Create("agent", "lookup");
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Document(address).SerializeToUtf8Bytes())!;
        fields[field] = JsonSerializer.Deserialize<JsonElement>(value);
        sdk.Seed(address, JsonSerializer.SerializeToUtf8Bytes(fields));

        Assert.NotNull(await Record.ExceptionAsync(() => sdk.Repository.ReadAsync(address)));
    }

    [Fact]
    public async Task ETagIsRequiredOnRead()
    {
        var sdk = new SessionCosmosSdkFixture { OmitETag = true };
        var address = SessionStorageAddress.Create("agent", "lookup");
        sdk.Seed(address, Document(address).SerializeToUtf8Bytes());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sdk.Repository.ReadAsync(address));
    }

    [Fact]
    public async Task ETagIsRequiredOnWrite()
    {
        var sdk = new SessionCosmosSdkFixture { OmitETag = true };
        var address = SessionStorageAddress.Create("agent", "lookup");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sdk.Repository.WriteAsync(Document(address), SessionWriteCondition.CreateOnly(address)));
    }

    [Fact]
    public async Task ConditionCannotBeAppliedToAnotherAddress()
    {
        var sdk = new SessionCosmosSdkFixture();
        var first = SessionStorageAddress.Create("agent", "one");
        var second = SessionStorageAddress.Create("agent", "two");

        var error = await Record.ExceptionAsync(() =>
            sdk.Repository.WriteAsync(Document(first), SessionWriteCondition.IfMatch(second, "etag")));

        Assert.Equal((typeof(InvalidOperationException), 0), (error?.GetType(), sdk.Requests.Count));
    }

    [Fact]
    public async Task CancellationPropagatesBeforeAnyRequest()
    {
        var sdk = new SessionCosmosSdkFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sdk.Repository.ReadAsync(SessionStorageAddress.Create("agent", "lookup"), cancellation.Token));
    }
}
