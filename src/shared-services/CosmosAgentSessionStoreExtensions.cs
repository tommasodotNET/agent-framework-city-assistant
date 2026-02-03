using Azure.Core;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharedServices;

/// <summary>
/// Extension methods for registering <see cref="CosmosAgentSessionStore"/>.
/// </summary>
public static class CosmosAgentSessionStoreExtensions
{
    /// <summary>
    /// Adds <see cref="CosmosAgentSessionStore"/> using a keyed Container service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="containerServiceKey">The key used to register the Container.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Use this overload with Aspire's <c>AddKeyedAzureCosmosContainer</c>.
    /// <code>
    /// builder.AddKeyedAzureCosmosContainer("sessions", ...);
    /// builder.Services.AddCosmosAgentSessionStore("sessions");
    /// </code>
    /// </remarks>
    public static IServiceCollection AddCosmosAgentSessionStore(
        this IServiceCollection services,
        string containerServiceKey,
        Action<CosmosAgentSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerServiceKey);

        var options = new CosmosAgentSessionStoreOptions();
        configure?.Invoke(options);

        services.AddSingleton(sp =>
            new CosmosAgentSessionStore(
                sp.GetRequiredKeyedService<Container>(containerServiceKey),
                sp.GetRequiredService<ILogger<CosmosAgentSessionStore>>(),
                options.TtlSeconds));

        return services;
    }

    /// <summary>
    /// Adds <see cref="CosmosAgentSessionStore"/> using an existing Container.
    /// </summary>
    public static IServiceCollection AddCosmosAgentSessionStore(
        this IServiceCollection services,
        Container container,
        Action<CosmosAgentSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(container);

        var options = new CosmosAgentSessionStoreOptions();
        configure?.Invoke(options);

        services.AddSingleton(sp =>
            new CosmosAgentSessionStore(
                container,
                sp.GetRequiredService<ILogger<CosmosAgentSessionStore>>(),
                options.TtlSeconds));

        return services;
    }

    /// <summary>
    /// Adds <see cref="CosmosAgentSessionStore"/> using an existing CosmosClient.
    /// </summary>
    public static IServiceCollection AddCosmosAgentSessionStore(
        this IServiceCollection services,
        CosmosClient client,
        string databaseId,
        string containerId,
        Action<CosmosAgentSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerId);

        var container = client.GetContainer(databaseId, containerId);
        return services.AddCosmosAgentSessionStore(container, configure);
    }

    /// <summary>
    /// Adds <see cref="CosmosAgentSessionStore"/> using Entra ID (Managed Identity) authentication.
    /// </summary>
    public static IServiceCollection AddCosmosAgentSessionStore(
        this IServiceCollection services,
        string accountEndpoint,
        TokenCredential tokenCredential,
        string databaseId,
        string containerId,
        Action<CosmosAgentSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(accountEndpoint);
        ArgumentNullException.ThrowIfNull(tokenCredential);

        var client = new CosmosClient(accountEndpoint, tokenCredential);
        return services.AddCosmosAgentSessionStore(client, databaseId, containerId, configure);
    }

    /// <summary>
    /// Configures the hosted agent builder to use the registered <see cref="CosmosAgentSessionStore"/>.
    /// </summary>
    /// <param name="builder">The hosted agent builder to configure.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// <code>
    /// builder.Services.AddCosmosAgentSessionStore("sessions");
    /// builder.AddAIAgent("my-agent", (sp, key) => { /* ... */ })
    ///     .WithCosmosSessionStore();
    /// </code>
    /// </remarks>
    public static IHostedAgentBuilder WithCosmosSessionStore(this IHostedAgentBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithSessionStore((sp, _) => sp.GetRequiredService<CosmosAgentSessionStore>());
    }
}
