using Microsoft.Azure.Cosmos;
using System.Text.Json;

namespace SharedServices;

/// <summary>
/// A custom <see cref="CosmosSerializer"/> that uses System.Text.Json for serialization.
/// </summary>
/// <remarks>
/// <para>
/// This serializer allows using System.Text.Json instead of the default Newtonsoft.Json
/// serializer used by the Cosmos DB SDK, providing better performance and smaller allocations.
/// </para>
/// 
/// <para><b>Usage:</b></para>
/// <code>
/// builder.AddKeyedAzureCosmosContainer("sessions",
///     configureClientOptions: options => options.Serializer = new CosmosSystemTextJsonSerializer());
/// </code>
/// </remarks>
public sealed class CosmosSystemTextJsonSerializer : CosmosSerializer
{
    private readonly JsonSerializerOptions? _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosSystemTextJsonSerializer"/> class.
    /// </summary>
    /// <param name="options">Optional JSON serializer options. If null, default options are used.</param>
    public CosmosSystemTextJsonSerializer(JsonSerializerOptions? options = null)
    {
        _options = options;
    }

    /// <inheritdoc />
    public override T FromStream<T>(Stream stream)
    {
        using (stream)
        {
            if (stream.CanSeek && stream.Length == 0)
            {
                return default!;
            }

            return JsonSerializer.Deserialize<T>(stream, _options)!;
        }
    }

    /// <inheritdoc />
    public override Stream ToStream<T>(T input)
    {
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, input, _options);
        stream.Position = 0;
        return stream;
    }
}
