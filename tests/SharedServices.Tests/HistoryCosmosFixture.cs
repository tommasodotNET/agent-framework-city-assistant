using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Moq;

namespace SharedServices.Tests;

// Doubles only Cosmos SDK boundaries, exercising the real repository's JSON, queries, HPK and batches.
internal sealed class HistoryCosmosFixture
{
    private readonly Dictionary<(string Partition, string Id), (JsonElement Document, string ETag)> _documents = [];
    private readonly object _gate = new();
    private int _etag;

    internal HistoryCosmosFixture()
    {
        Container = new Mock<Container>(MockBehavior.Strict);
        Container.Setup(container => container.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                SchemaReadCount++;
                var response = new Mock<ContainerResponse>();
                response.SetupGet(value => value.Resource).Returns(Properties);
                return response.Object;
            });
        Container.Setup(container => container.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns((string id, PartitionKey partition, ItemRequestOptions? _, CancellationToken token) =>
            {
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    PointReadPartitions.Add(partition);
                    if (!_documents.TryGetValue((partition.ToString(), id), out var document))
                        return Task.FromResult(new ResponseMessage(HttpStatusCode.NotFound));
                    var response = JsonResponse(document.Document);
                    response.Headers["etag"] = document.ETag;
                    return Task.FromResult(response);
                }
            });
        Container.Setup(container => container.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string? _, QueryRequestOptions options) =>
            {
                Queries.Add((query, options));
                BeforeQuery?.Invoke();
                var partition = options.PartitionKey!.Value.ToString();
                var parameters = query.GetQueryParameters().ToDictionary(parameter => parameter.Name, parameter => parameter.Value);
                var documents = _documents.Where(item => item.Key.Partition == partition)
                    .Select(item => item.Value.Document).Where(item => item.GetProperty("type").GetString() == "ChatMessage")
                    .Where(item => item.GetProperty("scopeKey").GetString() == (string)parameters["@scopeKey"])
                    .Where(item => item.GetProperty("conversationId").GetString() == (string)parameters["@conversationId"]).ToArray();
                if (query.QueryText.Contains("COUNT(1)", StringComparison.Ordinal))
                    return new HistoryFeedIterator(JsonSerializer.SerializeToElement(new { Documents = new[] { documents.Length } }));
                IEnumerable<JsonElement> ordered = query.QueryText.Contains("DESC", StringComparison.Ordinal)
                    ? documents.OrderByDescending(document => document.GetProperty("sequence").GetInt64())
                    : documents.OrderBy(document => document.GetProperty("sequence").GetInt64());
                if (parameters.TryGetValue("@limit", out var limit)) ordered = ordered.Take((int)limit);
                return new HistoryFeedIterator(JsonSerializer.SerializeToElement(new { Documents = ordered.ToArray() }));
            });
        Container.Setup(container => container.CreateTransactionalBatch(It.IsAny<PartitionKey>()))
            .Returns((PartitionKey partition) => CreateBatch(partition));
    }

    internal Mock<Container> Container { get; }
    internal ContainerProperties Properties { get; set; } =
        new("conversations", new[] { "/scopeKey", "/conversationId" }) { DefaultTimeToLive = -1 };
    internal int SchemaReadCount { get; private set; }
    internal List<PartitionKey> PointReadPartitions { get; } = [];
    internal List<(QueryDefinition Query, QueryRequestOptions Options)> Queries { get; } = [];
    internal List<HistoryBatch> Batches { get; } = [];
    internal Dictionary<int, HttpStatusCode> BatchFailures { get; } = [];
    internal Action? BeforeQuery { get; set; }
    internal Func<Task>? BeforeExecuteAsync { get; set; }
    internal IReadOnlyList<JsonElement> Documents => _documents.Values.Select(value => value.Document).ToArray();
    internal CosmosChatMessageRepository CreateRepository() => new(Container.Object);

    internal void SeedHead(HistoryReference reference, long nextSequence = 0, int ttl = -1) =>
        Seed(reference.ToAddress(), JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 2, id = "history-head", scopeKey = reference.ScopeKey,
            conversationId = reference.ConversationId, type = "HistoryHead", revision = reference.Revision,
            nextSequence, ttl
        }));

    internal void Seed(HistoryStorageAddress address, JsonElement document)
    {
        lock (_gate)
        {
            _documents[(address.ToPartitionKey().ToString(), document.GetProperty("id").GetString()!)] =
                (document.Clone(), $"etag-{++_etag}");
        }
    }

    private TransactionalBatch CreateBatch(PartitionKey partition)
    {
        var captured = new HistoryBatch(partition);
        var batch = new Mock<TransactionalBatch>(MockBehavior.Strict);
        batch.Setup(value => value.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Returns((Stream stream, TransactionalBatchItemRequestOptions? _) =>
            {
                captured.Operations.Add(new("create", null, ReadDocument(stream), null));
                return batch.Object;
            });
        batch.Setup(value => value.ReplaceItemStream(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Returns((string id, Stream stream, TransactionalBatchItemRequestOptions options) =>
            {
                captured.Operations.Add(new("replace", id, ReadDocument(stream), options.IfMatchEtag));
                return batch.Object;
            });
        batch.Setup(value => value.DeleteItem(It.IsAny<string>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Returns((string id, TransactionalBatchItemRequestOptions? _) =>
            {
                captured.Operations.Add(new("delete", id, null, null));
                return batch.Object;
            });
        batch.Setup(value => value.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                token.ThrowIfCancellationRequested();
                if (BeforeExecuteAsync is { } beforeExecute) await beforeExecute();
                HttpStatusCode status;
                int number;
                lock (_gate)
                {
                    Batches.Add(captured);
                    number = Batches.Count;
                    status = BatchFailures.GetValueOrDefault(number, HttpStatusCode.OK);
                    foreach (var operation in captured.Operations)
                    {
                        var id = operation.Id ?? operation.Document!.Value.GetProperty("id").GetString()!;
                        var exists = _documents.TryGetValue((partition.ToString(), id), out var previous);
                        if (operation.Kind == "create" && exists) status = HttpStatusCode.Conflict;
                        if (operation.Kind == "replace" && (!exists || previous.ETag != operation.ETag)) status = HttpStatusCode.PreconditionFailed;
                        if (operation.Kind == "delete" && !exists) status = HttpStatusCode.NotFound;
                    }
                    if (status == HttpStatusCode.OK)
                    {
                        foreach (var operation in captured.Operations)
                        {
                            var id = operation.Id ?? operation.Document!.Value.GetProperty("id").GetString()!;
                            if (operation.Kind == "delete") _documents.Remove((partition.ToString(), id));
                            else _documents[(partition.ToString(), id)] = (operation.Document!.Value, $"etag-{++_etag}");
                        }
                    }
                }
                var response = new Mock<TransactionalBatchResponse>();
                response.SetupGet(value => value.IsSuccessStatusCode).Returns(status == HttpStatusCode.OK);
                response.SetupGet(value => value.StatusCode).Returns(status);
                response.SetupGet(value => value.Count).Returns(0);
                return response.Object;
            });
        return batch.Object;
    }

    private static JsonElement ReadDocument(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static ResponseMessage JsonResponse(JsonElement json) => new(HttpStatusCode.OK)
    {
        Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(json))
    };

    internal sealed record HistoryBatch(PartitionKey Partition)
    {
        internal List<HistoryOperation> Operations { get; } = [];
    }
    internal sealed record HistoryOperation(string Kind, string? Id, JsonElement? Document, string? ETag);

    private sealed class HistoryFeedIterator(JsonElement json) : FeedIterator
    {
        private bool _hasMore = true;
        public override bool HasMoreResults => _hasMore;
        public override Task<ResponseMessage> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _hasMore = false;
            return Task.FromResult(JsonResponse(json));
        }
    }
}
