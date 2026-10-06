using System.Globalization;
using System.Resources;
using System.Text;
using Microsoft.Azure.Cosmos;

namespace SharedServices;

/// <summary>Shared schema and validation rules for the session and history containers.</summary>
public static class StorageSchema
{
    /// <summary>The document schema written by the redesigned repositories.</summary>
    public const int Version = 2;

    /// <summary>The UTF-8 budget for the complete session envelope, including its snapshot.</summary>
    public const int MaxSessionDocumentBytes = 2_000_000;

    /// <summary>The maximum UTF-8 length of a Cosmos document id.</summary>
    public const int MaxDocumentIdBytes = 1023;

    /// <summary>Conservative application budget across all hierarchical key values, based on Cosmos's 2-KB key limit.</summary>
    public const int MaxPartitionKeyBytes = 2048;

    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    /// <summary>Rejects oversized serialized envelopes before a repository attempts a write.</summary>
    public static void ValidateSessionDocumentSize(ReadOnlySpan<byte> serializedDocument)
    {
        if (serializedDocument.Length > MaxSessionDocumentBytes)
        {
            throw new InvalidOperationException(StorageErrors.Get("SnapshotTooLarge"));
        }
    }

    /// <summary>
    /// Checks an existing container without changing it. Repositories should invoke this once per
    /// container per process and cache only successful validation.
    /// </summary>
    public static void ValidateContainer(ContainerProperties properties, bool isSessionContainer)
    {
        ArgumentNullException.ThrowIfNull(properties);
        string[] expected = isSessionContainer
            ? ["/scopeKey", "/sessionId"]
            : ["/scopeKey", "/conversationId"];

        if (properties.PartitionKeyDefinitionVersion != PartitionKeyDefinitionVersion.V2
            || properties.PartitionKeyPaths is not { Count: 2 } paths
            || !paths.SequenceEqual(expected, StringComparer.Ordinal)
            || properties.DefaultTimeToLive != -1)
        {
            throw new InvalidOperationException(StorageErrors.Get("WrongContainerSchema"));
        }
        // The SDK's public HPK constructor assigns MultiHash for multiple paths.
    }

    internal static void ValidateKey(string value, string parameterName, int maximumBytes = MaxPartitionKeyBytes)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new ArgumentException(StorageErrors.Get("InvalidKey"), parameterName);
        }

        int byteCount;
        try
        {
            byteCount = s_strictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(StorageErrors.Get("InvalidKey"), parameterName, exception);
        }

        if (byteCount > maximumBytes)
        {
            throw new ArgumentException(StorageErrors.Get("KeyTooLong"), parameterName);
        }
    }

    internal static void ValidatePartitionKey(string first, string second)
    {
        ValidateKey(first, nameof(first));
        ValidateKey(second, nameof(second));
        // Count the actual component values, not JSON transport/header escaping.
        if (s_strictUtf8.GetByteCount(first) + s_strictUtf8.GetByteCount(second) > MaxPartitionKeyBytes)
        {
            throw new ArgumentException(StorageErrors.Get("PartitionKeyTooLong"));
        }
    }
}

internal static class StorageErrors
{
    private static readonly ResourceManager s_resources = new(
        "SharedServices.StorageContractResources", typeof(StorageErrors).Assembly);

    internal static string Get(string name) =>
        s_resources.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException(name);
}
