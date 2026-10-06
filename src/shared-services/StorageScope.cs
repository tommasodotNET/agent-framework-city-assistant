using System.Text.Encodings.Web;
using System.Text.Json;

namespace SharedServices;

/// <summary>
/// Builds readable, collision-free scopes from every trusted framework partition. This is encoding,
/// not authentication or anonymization; raw identifiers remain visible in storage.
/// </summary>
public static class StorageScope
{
    private static readonly JsonSerializerOptions s_canonicalJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Creates compact scope JSON, sorting all partition names with ordinal comparison.</summary>
    public static string Create(string lookupId, IReadOnlyDictionary<string, string>? partitions = null)
    {
        StorageSchema.ValidateKey(lookupId, nameof(lookupId));
        string scope;
        if (partitions is null || partitions.Count == 0)
        {
            scope = JsonSerializer.Serialize(new[] { "anonymous", lookupId }, s_canonicalJson);
        }
        else
        {
            var pairs = partitions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                {
                    StorageSchema.ValidateKey(pair.Key, nameof(partitions));
                    StorageSchema.ValidateKey(pair.Value, nameof(partitions));
                    return new[] { pair.Key, pair.Value };
                }).ToArray();
            scope = JsonSerializer.Serialize(new object[] { "partitions", pairs }, s_canonicalJson);
        }

        StorageSchema.ValidateKey(scope, nameof(partitions));
        return scope;
    }

    /// <summary>Validates stored scope JSON, rejecting noncanonical or ambiguous representations.</summary>
    public static void Validate(string scopeKey)
    {
        StorageSchema.ValidateKey(scopeKey, nameof(scopeKey));
        try
        {
            using var document = JsonDocument.Parse(scopeKey);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2)
            {
                throw new ArgumentException(StorageErrors.Get("InvalidScope"), nameof(scopeKey));
            }

            string canonical;
            switch (root[0].GetString())
            {
                case "anonymous":
                    canonical = Create(root[1].GetString() ?? string.Empty);
                    break;
                case "partitions":
                    var partitions = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var pair in root[1].EnumerateArray())
                    {
                        if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2
                            || !partitions.TryAdd(pair[0].GetString() ?? string.Empty, pair[1].GetString() ?? string.Empty))
                        {
                            throw new ArgumentException(StorageErrors.Get("InvalidScope"), nameof(scopeKey));
                        }
                    }
                    if (partitions.Count == 0)
                    {
                        throw new ArgumentException(StorageErrors.Get("InvalidScope"), nameof(scopeKey));
                    }
                    canonical = Create("validation", partitions);
                    break;
                default:
                    throw new ArgumentException(StorageErrors.Get("InvalidScope"), nameof(scopeKey));
            }

            if (!string.Equals(scopeKey, canonical, StringComparison.Ordinal))
            {
                throw new ArgumentException(StorageErrors.Get("InvalidScope"), nameof(scopeKey));
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(StorageErrors.Get("InvalidScope"), nameof(scopeKey), exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(StorageErrors.Get("InvalidScope"), nameof(scopeKey), exception);
        }
    }

    /// <summary>Returns whether a valid canonical scope is anonymous.</summary>
    public static bool IsAnonymous(string scopeKey)
    {
        Validate(scopeKey);
        return scopeKey.StartsWith("[\"anonymous\",", StringComparison.Ordinal);
    }
}
