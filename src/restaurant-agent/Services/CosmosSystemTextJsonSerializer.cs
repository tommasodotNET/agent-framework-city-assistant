using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace RestaurantAgent.Services;

public class CosmosSystemTextJsonSerializer : CosmosSerializer
{
    private readonly JsonSerializerOptions _options;

    public CosmosSystemTextJsonSerializer()
    {
        _options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

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

    public override Stream ToStream<T>(T input)
    {
        var json = JsonSerializer.Serialize(input, _options);
        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }
}
