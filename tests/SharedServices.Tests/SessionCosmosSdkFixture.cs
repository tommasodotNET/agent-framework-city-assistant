using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace SharedServices.Tests;

// Doubles only the SDK boundary: the repository, envelope, addresses, and MAF adapter are real.
internal sealed class SessionCosmosSdkFixture
{
    private readonly Dictionary<(string Id, string Partition), (byte[] Body, string ETag)> _items = [];
    private readonly List<SessionSdkRequest> _requests = [];
    private readonly object _gate = new();
    private int _version;
    private int _completedWrites;

    internal Mock<Container> Container { get; } = new(MockBehavior.Strict);
    internal ContainerProperties Properties { get; set; } =
        new("sessions", new[] { "/scopeKey", "/sessionId" }) { DefaultTimeToLive = -1 };
    internal int SchemaReads { get; private set; }
    internal HttpStatusCode? SchemaFailure { get; set; }
    internal HttpStatusCode? ItemFailure { get; set; }
    internal HttpStatusCode? WriteFailure { get; set; }
    internal bool OmitETag { get; set; }
    internal IReadOnlyList<SessionSdkRequest> Requests { get { lock (_gate) return _requests.ToArray(); } }
    internal int CompletedWrites => Volatile.Read(ref _completedWrites);
    internal CosmosSessionRepository Repository { get; }

    internal SessionCosmosSdkFixture()
    {
        Repository = new(Container.Object, NullLogger<CosmosSessionRepository>.Instance);
        Container.Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns((ContainerRequestOptions _, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                SchemaReads++;
                if (SchemaFailure is { } status)
                {
                    throw new CosmosException("schema failure", status, 0, "", 0);
                }
                var response = new Mock<ContainerResponse>();
                response.SetupGet(r => r.Resource).Returns(Properties);
                return Task.FromResult(response.Object);
            });
        Container.Setup(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns((string id, PartitionKey pk, ItemRequestOptions _, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    _requests.Add(new("read", id, pk, null, [], ct));
                    if (ItemFailure is { } status)
                    {
                        return Task.FromResult(Response(status));
                    }
                    return Task.FromResult(_items.TryGetValue((id, pk.ToString()), out var item)
                        ? Response(HttpStatusCode.OK, item.Body, item.ETag)
                        : Response(HttpStatusCode.NotFound));
                }
            });
        Container.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns((Stream body, PartitionKey pk, ItemRequestOptions options, CancellationToken ct) =>
                WriteAsync("create", body, null, pk, options, ct));
        Container.Setup(c => c.ReplaceItemStreamAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns((Stream body, string id, PartitionKey pk, ItemRequestOptions options, CancellationToken ct) =>
                WriteAsync("replace", body, id, pk, options, ct));
    }

    internal void Seed(SessionStorageAddress address, byte[] body, string etag = "seed")
    {
        lock (_gate)
        {
            _items[(address.DocumentId, address.ToPartitionKey().ToString())] = (body, etag);
        }
    }

    private async Task<ResponseMessage> WriteAsync(string operation, Stream body, string? id,
        PartitionKey pk, ItemRequestOptions options, CancellationToken ct)
    {
        using var copy = new MemoryStream();
        await body.CopyToAsync(copy, ct);
        var bytes = copy.ToArray();
        using var json = JsonDocument.Parse(bytes);
        id ??= json.RootElement.GetProperty("id").GetString()!;
        lock (_gate)
        {
            _requests.Add(new(operation, id, pk, options.IfMatchEtag, bytes, ct));
            if (WriteFailure is { } failure)
            {
                return Response(failure);
            }
            var key = (id, pk.ToString());
            if (operation == "create" && _items.ContainsKey(key))
            {
                return Response(HttpStatusCode.Conflict);
            }
            if (operation == "replace" && (!_items.TryGetValue(key, out var old) || old.ETag != options.IfMatchEtag))
            {
                return Response(HttpStatusCode.PreconditionFailed);
            }
            var etag = $"version-{++_version}";
            _items[key] = (bytes, etag);
            Interlocked.Increment(ref _completedWrites);
            return Response(operation == "create" ? HttpStatusCode.Created : HttpStatusCode.OK, etag: etag);
        }
    }

    private ResponseMessage Response(HttpStatusCode status, byte[]? body = null, string? etag = null)
    {
        var response = new ResponseMessage(status);
        if (body is not null)
        {
            response.Content = new MemoryStream(body);
        }
        if (!OmitETag && etag is not null)
        {
            response.Headers["etag"] = etag;
        }
        return response;
    }
}

internal sealed record SessionSdkRequest(
    string Operation, string Id, PartitionKey Partition, string? ETag, byte[] Body, CancellationToken CancellationToken);
