using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace SharedServices;

/// <summary>Shared schema-v2 snapshot persistence for hosted agents and voice application state.</summary>
public sealed class CosmosSessionRepository
{
    private readonly Container _container;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaValidated;

    /// <summary>Uses an existing container without creating, migrating, or deleting resources.</summary>
    public CosmosSessionRepository(Container container, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(logger);
        _container = container;
        _logger = logger;
    }

    /// <summary>Point-reads a complete address, returning null only when the item is absent.</summary>
    public async Task<SessionReadResult?> ReadAsync(
        SessionStorageAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        await ValidateSchemaAsync(cancellationToken).ConfigureAwait(false);
        using var response = await _container.ReadItemStreamAsync(
            address.DocumentId, address.ToPartitionKey(), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // A container removed after the cached validation must not look like a missing session.
            await ReadAndValidateSchemaAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        response.EnsureSuccessStatusCode();
        var etag = RequireETag(response);
        using var json = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        foreach (var name in new[] { "schemaVersion", "id", "agentId", "scopeKey", "sessionId", "serializedSession", "lastUpdated", "ttl" })
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out _))
            {
                throw new InvalidOperationException(SessionRepositoryErrors.Get("InvalidEnvelope"));
            }
        }
        var document = root.Deserialize<SessionDocument>()
            ?? throw new InvalidOperationException(SessionRepositoryErrors.Get("InvalidEnvelope"));
        document.ValidateFor(address);
        // Cosmos adds system properties to reads; budget the same application envelope used on writes.
        document.SerializeToUtf8Bytes();
        return new SessionReadResult(document, SessionWriteCondition.IfMatch(address, etag));
    }

    /// <summary>Creates or conditionally replaces the exact validated UTF-8 envelope; never upserts.</summary>
    public async Task<SessionWriteCondition> WriteAsync(
        SessionDocument document, SessionWriteCondition condition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(condition);
        cancellationToken.ThrowIfCancellationRequested();
        document.ValidateFor(condition.Address);
        var bytes = document.SerializeToUtf8Bytes();
        await ValidateSchemaAsync(cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream(bytes, writable: false);
        var options = new ItemRequestOptions
        {
            EnableContentResponseOnWrite = false,
            IfMatchEtag = condition.ETag
        };
        try
        {
            using var response = condition.IsCreateOnly
                ? await _container.CreateItemStreamAsync(stream, condition.Address.ToPartitionKey(),
                    options, cancellationToken).ConfigureAwait(false)
                : await _container.ReplaceItemStreamAsync(stream, condition.Address.DocumentId,
                    condition.Address.ToPartitionKey(), options, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return SessionWriteCondition.IfMatch(condition.Address, RequireETag(response));
        }
        catch (CosmosException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            _logger.LogWarning(SessionRepositoryErrors.Get("ConflictLog"),
                (int)exception.StatusCode);
            throw new SessionSnapshotConflictException(exception);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            _logger.LogWarning(SessionRepositoryErrors.Get("TooLargeLog"));
            throw new SessionSnapshotTooLargeException(exception);
        }
    }

    private static string RequireETag(ResponseMessage response) =>
        !string.IsNullOrWhiteSpace(response.Headers.ETag)
            ? response.Headers.ETag
            : throw new InvalidOperationException(SessionRepositoryErrors.Get("MissingETag"));

    private async Task ValidateSchemaAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_schemaValidated)
        {
            return;
        }
        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_schemaValidated)
            {
                await ReadAndValidateSchemaAsync(cancellationToken).ConfigureAwait(false);
                _schemaValidated = true;
            }
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private async Task ReadAndValidateSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            StorageSchema.ValidateContainer(response.Resource, isSessionContainer: true);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(SessionRepositoryErrors.Get("MissingContainer"), exception);
        }
    }
}

/// <summary>A stale working copy or duplicate create was rejected without overwriting a snapshot.</summary>
public sealed class SessionSnapshotConflictException : InvalidOperationException
{
    internal SessionSnapshotConflictException(CosmosException innerException)
        : base(SessionRepositoryErrors.Get("Conflict"), innerException) { }
}

/// <summary>Cosmos rejected a snapshot that exceeds its service-side document budget.</summary>
public sealed class SessionSnapshotTooLargeException : InvalidOperationException
{
    internal SessionSnapshotTooLargeException(CosmosException innerException)
        : base(SessionRepositoryErrors.Get("TooLarge"), innerException) { }
}

internal static class SessionRepositoryErrors
{
    private static readonly System.Resources.ResourceManager s_resources = new(
        "SharedServices.SessionRepositoryResources", typeof(SessionRepositoryErrors).Assembly);

    internal static string Get(string name) =>
        s_resources.GetString(name, System.Globalization.CultureInfo.CurrentUICulture)
        ?? throw new System.Resources.MissingManifestResourceException(name);
}
