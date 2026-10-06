using Microsoft.Extensions.AI;
using Moq;
using SharedServices;

namespace SharedServices.Tests;

public class ProtocolChatClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protocol_metadata_is_removed_only_from_model_options(bool streaming)
    {
        var protocolConfiguration = new { ReturnImmediately = false };
        var options = new ChatOptions
        {
            ModelId = "test-model",
            AdditionalProperties = new()
            {
                ["a2a.configuration"] = protocolConfiguration,
                ["stream_options"] = new { include_usage = true }
            }
        };
        ChatOptions? received = null;
        var inner = new Mock<IChatClient>();
        inner.Setup(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions? value, CancellationToken _) =>
            {
                received = value;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
            });
        inner.Setup(client => client.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions? value, CancellationToken _) =>
            {
                received = value;
                return Updates();
            });
        using var client = inner.Object.AsBuilder().UseProtocolMetadataFilter().Build();
        if (streaming)
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "test")], options)) { }
        }
        else
        {
            await client.GetResponseAsync([new(ChatRole.User, "test")], options);
        }
        Assert.NotNull(received);
        Assert.NotSame(options, received);
        Assert.False(received.AdditionalProperties!.ContainsKey("a2a.configuration"));
        Assert.True(received.AdditionalProperties.ContainsKey("stream_options"));
        Assert.Equal("test-model", received.ModelId);
        Assert.Same(protocolConfiguration, options.AdditionalProperties["a2a.configuration"]);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Updates()
    {
        await Task.Yield();
        yield return new(ChatRole.Assistant, "ok");
    }
}
