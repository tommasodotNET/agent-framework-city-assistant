using Microsoft.Extensions.AI;

namespace SharedServices;

public static class ProtocolChatClientExtensions
{
    /// <summary>Keeps A2A routing metadata out of model-specific request parameters.</summary>
    public static ChatClientBuilder UseProtocolMetadataFilter(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(
            (messages, options, inner, cancellationToken) =>
                inner.GetResponseAsync(messages, ForModel(options), cancellationToken),
            (messages, options, inner, cancellationToken) =>
                inner.GetStreamingResponseAsync(messages, ForModel(options), cancellationToken));
    }

    private static ChatOptions? ForModel(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.ContainsKey("a2a.configuration") != true)
            return options;

        // Do not remove protocol metadata from the caller's options or nested agent context.
        var copy = options.Clone();
        copy.AdditionalProperties = new AdditionalPropertiesDictionary(options.AdditionalProperties);
        copy.AdditionalProperties.Remove("a2a.configuration");
        return copy;
    }
}
