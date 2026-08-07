using Microsoft.Extensions.AI;

namespace SharedServices;

public static class StreamingUsageChatClientExtensions
{
    public static ChatClientBuilder UseStreamingUsage(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Use(
            (messages, options, innerClient, cancellationToken) =>
                innerClient.GetResponseAsync(messages, options, cancellationToken),
            (messages, options, innerClient, cancellationToken) =>
                innerClient.GetStreamingResponseAsync(
                    messages,
                    WithStreamingUsage(options),
                    cancellationToken));
    }

    private static ChatOptions WithStreamingUsage(ChatOptions? options)
    {
        var streamingOptions = options?.Clone() ?? new ChatOptions();
        streamingOptions.AdditionalProperties ??= [];
        streamingOptions.AdditionalProperties["stream_options"] = new Dictionary<string, object?>
        {
            ["include_usage"] = true
        };

        return streamingOptions;
    }
}
