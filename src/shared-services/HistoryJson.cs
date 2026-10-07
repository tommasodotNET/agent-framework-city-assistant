using System.Security.Cryptography;
using System.Text.Json;

namespace SharedServices;

internal static class HistoryJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        // Cosmos may reorder JSON properties, including polymorphic AIContent discriminators.
        AllowOutOfOrderMetadataProperties = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    internal static string Hash(IEnumerable<JsonElement> values, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, value);
            hash.AppendData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var element in value.EnumerateArray()) WriteCanonical(writer, element);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
