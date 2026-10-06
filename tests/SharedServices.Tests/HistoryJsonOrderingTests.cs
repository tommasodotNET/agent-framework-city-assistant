using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace SharedServices.Tests;

public sealed class HistoryJsonOrderingTests
{
    [Fact]
    public async Task Repository_reads_text_and_tool_content_when_Cosmos_reorders_type_metadata()
    {
        var fixture = new HistoryCosmosFixture();
        var repository = fixture.CreateRepository();
        var reference = new HistoryReference(StorageScope.Create("context"), "history", 0);
        ChatMessage[] messages =
        [
            new(ChatRole.User, "Unicode: \u00e8 \u6771\u4eac"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["location"] = "\u6771\u4eac" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")])
        ];
        var written = await repository.AppendAsync(reference, messages);
        foreach (var document in fixture.Documents.Where(item => item.GetProperty("type").GetString() == "ChatMessage"))
        {
            var reordered = MoveMetadataLast(JsonNode.Parse(document.GetRawText()));
            fixture.Seed(written.Reference.ToAddress(), JsonSerializer.SerializeToElement(reordered));
        }

        var loaded = await repository.ReadAsync(written.Reference);

        Assert.Equal(messages[0].Text, loaded.Messages[0].Text);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(loaded.Messages[1].Contents));
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("\u6771\u4eac", Assert.IsType<JsonElement>(call.Arguments!["location"]).GetString());
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(loaded.Messages[2].Contents));
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("result", Assert.IsType<JsonElement>(result.Result).GetString());
    }

    [Fact]
    public void Unknown_content_discriminator_is_still_rejected()
    {
        var node = JsonSerializer.SerializeToNode(new ChatMessage(ChatRole.User, "text"))!;
        var content = node["Contents"]![0]!.AsObject();
        content.Remove("$type");
        content.Add("$type", "not-a-supported-content-type");
        var document = new HistoryMessageDocument
        {
            ScopeKey = StorageScope.Create("context"),
            ConversationId = "history",
            Message = JsonSerializer.SerializeToElement(node),
            Ttl = 86400
        };

        Assert.Throws<JsonException>(() => document.ToChatMessage());
    }

    private static JsonNode? MoveMetadataLast(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(property => property.Key == "$type" ? 1 : 0)
            .Select(property => new KeyValuePair<string, JsonNode?>(property.Key, MoveMetadataLast(property.Value)))),
        JsonArray array => new JsonArray(array.Select(MoveMetadataLast).ToArray()),
        _ => node?.DeepClone()
    };
}
