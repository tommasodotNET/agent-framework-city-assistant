using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace SharedServices;

/// <summary>
/// Extension methods for configuring <see cref="CosmosChatHistoryProvider"/> with <see cref="ChatClientAgentOptions"/>.
/// </summary>
/// <remarks>Standalone sessions must be explicitly initialized with SessionPersistenceState before running the agent.
/// No overload creates anonymous history or reads an ambient identity.</remarks>
public static class CosmosChatHistoryProviderExtensions
{
    /// <summary>
    /// Single factory: creates, configures, and returns a <see cref="CosmosChatHistoryProvider"/>.
    /// All public methods delegate here.
    /// </summary>
    private static CosmosChatHistoryProvider BuildProvider(
        CosmosClient cosmosClient,
        string databaseId,
        string containerId,
        CosmosChatHistoryProviderOptions options,
        bool ownsClient = false,
        ILogger<CosmosChatHistoryProvider>? logger = null,
        IHistoryCompactor? compactor = null)
    {
        options.Compaction?.Validate();
        if (options.Compaction is not null && options.MaxMessagesToRetrieve.HasValue)
            throw new ArgumentException(HistoryProviderErrors.Get("PartialHistoryConflict"), nameof(options.MaxMessagesToRetrieve));
        var provider = new CosmosChatHistoryProvider(
            cosmosClient, databaseId, containerId,
            ownsClient,
            options.ProvideOutputMessageFilter, options.StoreInputMessageFilter, logger,
            compactor, options.Compaction);

        if (options.MaxItemCount.HasValue) provider.MaxItemCount = options.MaxItemCount.Value;
        if (options.MaxBatchSize.HasValue) provider.MaxBatchSize = options.MaxBatchSize.Value;
        if (options.MaxMessagesToRetrieve.HasValue) provider.MaxMessagesToRetrieve = options.MaxMessagesToRetrieve;
        provider.MessageTtlSeconds = options.MessageTtlSeconds;
        options.ConfigureProvider?.Invoke(provider);

        return provider;
    }

    /// <summary>
    /// Clones base options (if any), then applies the caller's configure delegate.
    /// </summary>
    private static CosmosChatHistoryProviderOptions ResolveOptions(
        CosmosChatHistoryProviderOptions? baseOptions,
        Action<CosmosChatHistoryProviderOptions>? configure)
    {
        var resolved = baseOptions?.Clone() ?? new CosmosChatHistoryProviderOptions();
        configure?.Invoke(resolved);
        return resolved;
    }

    // ────────────────────────────────────────────────────────────
    //  Aspire / DI registration
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers Cosmos DB chat history provider configuration using a keyed <see cref="Container"/> service.
    /// </summary>
    public static IServiceCollection AddCosmosChatHistoryProvider(
        this IServiceCollection services,
        string containerServiceKey,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerServiceKey);

        var options = new CosmosChatHistoryProviderOptions();
        configure?.Invoke(options);

        services.AddSingleton(new CosmosChatHistoryProviderRegistration(containerServiceKey, options));
        return services;
    }

    /// <summary>
    /// Registers Cosmos DB chat history provider configuration using a keyed <see cref="Container"/> service,
    /// with access to the current <see cref="IServiceProvider"/> for resolving dependencies at registration time.
    /// </summary>
    public static IServiceCollection AddCosmosChatHistoryProvider(
        this IServiceCollection services,
        string containerServiceKey,
        Action<IServiceProvider, CosmosChatHistoryProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerServiceKey);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton(sp =>
        {
            var options = new CosmosChatHistoryProviderOptions();
            configure(sp, options);
            return new CosmosChatHistoryProviderRegistration(containerServiceKey, options);
        });

        return services;
    }

    // ────────────────────────────────────────────────────────────
    //  Aspire / DI consumption
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures the agent to use Cosmos DB chat history from a pre-registered DI configuration
    /// (see <see cref="AddCosmosChatHistoryProvider(IServiceCollection, string, Action{CosmosChatHistoryProviderOptions}?)"/>).
    /// </summary>
    [RequiresUnreferencedCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with trimming.")]
    [RequiresDynamicCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with NativeAOT.")]
    public static ChatClientAgentOptions WithCosmosChatHistoryProvider(
        this ChatClientAgentOptions options,
        IServiceProvider serviceProvider,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var registration = serviceProvider.GetRequiredService<CosmosChatHistoryProviderRegistration>();
        var container = serviceProvider.GetRequiredKeyedService<Container>(registration.ContainerServiceKey);
        var logger = serviceProvider.GetService<ILogger<CosmosChatHistoryProvider>>();
        var providerOptions = ResolveOptions(registration.Options, configure);
        var compactor = serviceProvider.GetHistoryCompactor(providerOptions.Compaction);

        options.ChatHistoryProvider = BuildProvider(
            container.Database.Client, container.Database.Id, container.Id,
            providerOptions, logger: logger, compactor: compactor);

        return options;
    }

    // ────────────────────────────────────────────────────────────
    //  Standalone (no Aspire)
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures the agent to use Cosmos DB for message storage with a connection string.
    /// </summary>
    [RequiresUnreferencedCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with trimming.")]
    [RequiresDynamicCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with NativeAOT.")]
    public static ChatClientAgentOptions WithCosmosDBChatHistoryProvider(
        this ChatClientAgentOptions options,
        string connectionString,
        string databaseId,
        string containerId,
        Action<CosmosChatHistoryProviderOptions>? configure = null,
        IHistoryCompactor? compactor = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var providerOptions = ResolveOptions(null, configure);
        options.ChatHistoryProvider = BuildProvider(
            new CosmosClient(connectionString), databaseId, containerId,
            providerOptions, ownsClient: true, compactor: compactor);

        return options;
    }

    /// <summary>
    /// Configures the agent to use Cosmos DB for message storage with managed identity authentication.
    /// </summary>
    [RequiresUnreferencedCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with trimming.")]
    [RequiresDynamicCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with NativeAOT.")]
    public static ChatClientAgentOptions WithCosmosDBChatHistoryProviderUsingManagedIdentity(
        this ChatClientAgentOptions options,
        string accountEndpoint,
        string databaseId,
        string containerId,
        TokenCredential tokenCredential,
        Action<CosmosChatHistoryProviderOptions>? configure = null,
        IHistoryCompactor? compactor = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenCredential);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountEndpoint);

        var providerOptions = ResolveOptions(null, configure);
        options.ChatHistoryProvider = BuildProvider(
            new CosmosClient(accountEndpoint, tokenCredential), databaseId, containerId,
            providerOptions, ownsClient: true, compactor: compactor);

        return options;
    }

    /// <summary>
    /// Configures the agent to use Cosmos DB for message storage with an existing <see cref="CosmosClient"/>.
    /// </summary>
    [RequiresUnreferencedCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with trimming.")]
    [RequiresDynamicCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with NativeAOT.")]
    public static ChatClientAgentOptions WithCosmosDBChatHistoryProvider(
        this ChatClientAgentOptions options,
        CosmosClient cosmosClient,
        string databaseId,
        string containerId,
        Action<CosmosChatHistoryProviderOptions>? configure = null,
        IHistoryCompactor? compactor = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cosmosClient);

        var providerOptions = ResolveOptions(null, configure);
        options.ChatHistoryProvider = BuildProvider(
            cosmosClient, databaseId, containerId,
            providerOptions, compactor: compactor);

        return options;
    }
}

// ────────────────────────────────────────────────────────────
//  Supporting types
// ────────────────────────────────────────────────────────────

internal sealed class CosmosChatHistoryProviderRegistration(string containerServiceKey, CosmosChatHistoryProviderOptions options)
{
    public string ContainerServiceKey { get; } = containerServiceKey ?? throw new ArgumentNullException(nameof(containerServiceKey));
    public CosmosChatHistoryProviderOptions Options { get; } = options?.Clone() ?? throw new ArgumentNullException(nameof(options));
}

/// <summary>
/// Options for configuring <see cref="CosmosChatHistoryProvider"/> behavior.
/// </summary>
public sealed class CosmosChatHistoryProviderOptions
{
    /// <summary>Optional compaction profile; null disables the feature. Persistence remains owned by the provider.</summary>
    public HistoryCompactionOptions? Compaction { get; set; }

    /// <summary>Filters provided history without changing persisted messages.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? ProvideOutputMessageFilter { get; set; }

    /// <summary>Filters request messages for persistence; null preserves the framework's history-exclusion default.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? StoreInputMessageFilter { get; set; }

    /// <summary>The query page size.</summary>
    public int? MaxItemCount { get; set; }

    /// <summary>Total batch operations (2..100), including one metadata operation.</summary>
    public int? MaxBatchSize { get; set; }

    /// <summary>Optional latest-message read limit; does not modify storage and cannot be combined with compaction.</summary>
    public int? MaxMessagesToRetrieve { get; set; }

    /// <summary>Message retention, default 24 hours; null/-1 disables expiration.</summary>
    public int? MessageTtlSeconds { get; set; } = 86400;

    /// <summary>
    /// Escape hatch: direct access to the provider instance after construction for advanced scenarios.
    /// </summary>
    public Action<CosmosChatHistoryProvider>? ConfigureProvider { get; set; }

    internal CosmosChatHistoryProviderOptions Clone() => new()
    {
        Compaction = Compaction is null ? null : Compaction with { },
        ProvideOutputMessageFilter = ProvideOutputMessageFilter,
        StoreInputMessageFilter = StoreInputMessageFilter,
        MaxItemCount = MaxItemCount,
        MaxBatchSize = MaxBatchSize,
        MaxMessagesToRetrieve = MaxMessagesToRetrieve,
        MessageTtlSeconds = MessageTtlSeconds,
        ConfigureProvider = ConfigureProvider
    };
}
