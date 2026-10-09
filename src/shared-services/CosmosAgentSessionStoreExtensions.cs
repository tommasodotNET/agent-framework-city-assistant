using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable MAAI001 // Session store and isolation types are experimental.

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

        services.AddSingleton(options);
        services.AddSingleton(sp => new CosmosSessionRepository(
            sp.GetRequiredKeyedService<Container>(containerServiceKey),
            sp.GetRequiredService<ILogger<CosmosSessionRepository>>()));
        services.AddSingleton(sp =>
            new CosmosAgentSessionStore(
                sp.GetRequiredService<CosmosSessionRepository>(),
                sp.GetRequiredService<ILogger<CosmosAgentSessionStore>>(),
                options.TtlSeconds));

        return services;
    }

    /// <summary>
    /// Adds <see cref="CosmosAgentSessionStore"/> using an existing CosmosClient.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="client">The CosmosClient instance (user manages lifecycle and credentials).</param>
    /// <param name="databaseId">The database identifier.</param>
    /// <param name="containerId">The container identifier.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCosmosAgentSessionStore(
        this IServiceCollection services,
        CosmosClient client,
        string databaseId,
        string containerId,
        Action<CosmosAgentSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerId);

        var options = new CosmosAgentSessionStoreOptions();
        configure?.Invoke(options);

        var container = client.GetContainer(databaseId, containerId);
        services.AddSingleton(options);
        services.AddSingleton(sp => new CosmosSessionRepository(
            container, sp.GetRequiredService<ILogger<CosmosSessionRepository>>()));
        services.AddSingleton(sp =>
            new CosmosAgentSessionStore(
                sp.GetRequiredService<CosmosSessionRepository>(),
                sp.GetRequiredService<ILogger<CosmosAgentSessionStore>>(),
                options.TtlSeconds));

        return services;
    }

    /// <summary>
    /// Configures the hosted agent builder to use the registered <see cref="CosmosAgentSessionStore"/>,
    /// scoped by an isolation key when an <see cref="AgentIsolationKeyProvider"/> is registered.
    /// </summary>
    /// <param name="builder">The hosted agent builder to configure.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The store is always wrapped in <see cref="IsolationKeyScopedAgentSessionStore"/>, so every protocol
    /// endpoint (A2A and OpenAI-compatible) gets the same behavior:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     No <see cref="AgentIsolationKeyProvider"/> registered (anonymous host): sessions are keyed only by
    ///     the client-supplied continuation id. A warning is logged because any caller who knows that id can
    ///     resume the session.
    ///   </description></item>
    ///   <item><description>
    ///     Provider registered (for example with <c>UseClaimsBasedAgentIsolation()</c>): sessions are scoped
    ///     to the caller's isolation key. Strict is always enabled when a provider is registered:
    ///     requests without a key fail rather than silently falling back to anonymous ownership.
    ///   </description></item>
    /// </list>
    /// <code>
    /// builder.Services.AddCosmosAgentSessionStore("sessions");
    /// builder.AddAIAgent("my-agent", (sp, key) => { /* ... */ })
    ///     .WithCosmosSessionStore();
    /// </code>
    /// </remarks>
    public static IHostedAgentBuilder WithCosmosSessionStore(this IHostedAgentBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithCosmosSessionStore((sp, _) => sp.GetRequiredService<CosmosAgentSessionStore>());
    }

    /// <summary>Attaches an existing store to this hosted agent without a global store registration.</summary>
    /// <remarks>
    /// Uses the same isolation policy as the parameterless overload. The caller owns the store's
    /// Cosmos client; sharing a store does not share sessions between distinct agent ids.
    /// </remarks>
    public static IHostedAgentBuilder WithCosmosSessionStore(
        this IHostedAgentBuilder builder, CosmosAgentSessionStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        return builder.WithCosmosSessionStore((_, _) => store);
    }

    /// <summary>Creates a store for this hosted agent using standard MAF hosting and dependency injection.</summary>
    /// <remarks>
    /// The factory receives the service provider and registered agent name. It is evaluated lazily
    /// once per hosted store (singleton), independently of the agent's lifetime. Use it to select
    /// a shared client/container and retention per agent without Aspire or global store options.
    /// The existing anonymous-warning and strict authenticated-isolation policies always apply.
    /// </remarks>
    public static IHostedAgentBuilder WithCosmosSessionStore(
        this IHostedAgentBuilder builder,
        Func<IServiceProvider, string, CosmosAgentSessionStore> createStore)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createStore);

        return builder.WithSessionStore(
            (sp, agentName) =>
            {
                var store = createStore(sp, agentName);
                ArgumentNullException.ThrowIfNull(store);
                var isolationKeyProvider = sp.GetService<AgentIsolationKeyProvider>();

                IsolationKeyScopedAgentSessionStoreOptions options;
                if (isolationKeyProvider is null)
                {
                    sp.GetRequiredService<ILogger<CosmosAgentSessionStore>>().LogWarning(
                        SessionRepositoryErrors.Get("AnonymousWarning"),
                        agentName);
                    options = new IsolationKeyScopedAgentSessionStoreOptions { Strict = false };
                }
                else
                {
                    options = new IsolationKeyScopedAgentSessionStoreOptions { Strict = true };
                }

                return new IsolationKeyScopedAgentSessionStore(store, isolationKeyProvider, options);
            },
            withIsolation: false);
    }
}
