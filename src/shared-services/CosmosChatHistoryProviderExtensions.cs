using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
namespace SharedServices;

/// <summary>
/// Extension methods for configuring <see cref="CosmosChatHistoryProvider"/> with <see cref="ChatClientAgentOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// These extensions enable persistent chat history storage in Azure Cosmos DB for AI agents.
/// The provider automatically detects if connected to the Cosmos DB Emulator and adjusts behavior accordingly.
/// </para>
/// 
/// <para><b>Configuration Examples:</b></para>
/// 
/// <para><b>1. With Aspire keyed container (recommended):</b></para>
/// <code>
/// // In Program.cs - register first
/// builder.AddKeyedAzureCosmosContainer("conversations", ...);
/// builder.Services.AddCosmosChatHistoryProvider("conversations", opt => opt.MessageTtlSeconds = 3600);
/// 
/// // Then in agent options
/// var agentOptions = new ChatClientAgentOptions()
///     .WithCosmosChatHistoryProvider(serviceProvider);
/// </code>
/// 
/// <para><b>2. With CosmosClient (user manages credentials):</b></para>
/// <code>
/// var client = new CosmosClient(endpoint, new DefaultAzureCredential());
/// // or: new CosmosClient(connectionString);
/// 
/// var agentOptions = new ChatClientAgentOptions()
///     .WithCosmosChatHistoryProvider(client, "DatabaseId", "ContainerId");
/// </code>
/// 
/// <para><b>3. Multi-tenant with hierarchical partition keys:</b></para>
/// <code>
/// builder.Services.AddHttpContextAccessor();
/// builder.Services.AddCosmosChatHistoryProvider("conversations", opt => 
/// {
///     opt.TenantIdFactory = sp => sp.GetService&lt;IHttpContextAccessor&gt;()?.HttpContext?.User?.FindFirst("tenant_id")?.Value;
///     opt.UserIdFactory = sp => sp.GetService&lt;IHttpContextAccessor&gt;()?.HttpContext?.User?.FindFirst("sub")?.Value;
/// });
/// 
/// // The provider will use partition key (tenantId, userId, conversationId) when both factories return values
/// </code>
/// </remarks>
public static class CosmosChatHistoryProviderExtensions
{
    /// <summary>
    /// Registers Cosmos DB chat history provider configuration using a keyed Container service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="containerServiceKey">The key used to register the Container (e.g., "conversations").</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// After calling this, use <see cref="WithCosmosChatHistoryProvider(ChatClientAgentOptions)"/> 
    /// without parameters in your agent options.
    /// <code>
    /// builder.AddKeyedAzureCosmosContainer("conversations", ...);
    /// builder.Services.AddCosmosChatHistoryProvider("conversations");
    /// 
    /// // Later in agent configuration
    /// var options = new ChatClientAgentOptions().WithCosmosChatHistoryProvider();
    /// </code>
    /// </remarks>
    public static IServiceCollection AddCosmosChatHistoryProvider(
        this IServiceCollection services,
        string containerServiceKey,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerServiceKey);

        var options = new CosmosChatHistoryProviderOptions();
        configure?.Invoke(options);

        // Register a factory that creates the provider configuration
        services.AddSingleton(new CosmosChatHistoryProviderRegistration(containerServiceKey, options));

        return services;
    }

    /// <summary>
    /// Registers Cosmos DB chat history provider configuration using a keyed Container service with access to the service provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="containerServiceKey">The key used to register the Container (e.g., "conversations").</param>
    /// <param name="configure">Configuration action with access to the service provider for resolving dependencies.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Use this overload when you need to resolve services from DI during configuration:
    /// <code>
    /// builder.Services.AddCosmosChatHistoryProvider("conversations", (sp, opt) => 
    /// {
    ///     opt.ChatReducer = sp.GetRequiredService&lt;IChatReducer&gt;();
    /// });
    /// </code>
    /// </remarks>
    public static IServiceCollection AddCosmosChatHistoryProvider(
        this IServiceCollection services,
        string containerServiceKey,
        Action<IServiceProvider, CosmosChatHistoryProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerServiceKey);
        ArgumentNullException.ThrowIfNull(configure);

        // Register a factory that defers configuration until the service provider is available
        services.AddSingleton(sp =>
        {
            var options = new CosmosChatHistoryProviderOptions();
            configure(sp, options);
            return new CosmosChatHistoryProviderRegistration(containerServiceKey, options);
        });

        return services;
    }

    /// <summary>
    /// Configures the agent to use Cosmos DB for chat history from pre-registered DI configuration.
    /// </summary>
    /// <param name="options">The agent options to configure.</param>
    /// <param name="serviceProvider">The service provider to resolve the container from.</param>
    /// <param name="configure">Optional configuration action to override or extend pre-registered options.</param>
    /// <returns>The configured options for method chaining.</returns>
    /// <remarks>
    /// Requires prior registration via <see cref="AddCosmosChatHistoryProvider(IServiceCollection, string, Action{CosmosChatHistoryProviderOptions}?)"/>.
    /// <code>
    /// // Register in Program.cs
    /// builder.Services.AddCosmosChatHistoryProvider("conversations", (sp, opt) => 
    /// {
    ///     opt.ChatReducer = sp.GetRequiredService&lt;IChatReducer&gt;();
    /// });
    /// 
    /// // Use in agent configuration
    /// var agentOptions = new ChatClientAgentOptions()
    ///     .WithCosmosChatHistoryProvider(serviceProvider);
    /// </code>
    /// </remarks>
    public static ChatClientAgentOptions WithCosmosChatHistoryProvider(
        this ChatClientAgentOptions options,
        IServiceProvider serviceProvider,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var registration = serviceProvider.GetRequiredService<CosmosChatHistoryProviderRegistration>();
        var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<CosmosChatHistoryProvider>();
        var container = serviceProvider.GetRequiredKeyedService<Container>(registration.ContainerServiceKey);

        // Clone options from registration and apply additional configuration
        var providerOptions = CloneOptions(registration.Options);
        configure?.Invoke(providerOptions);

        options.ChatHistoryProviderFactory = (context, _) =>
        {
            var provider = CreateProvider(
                container.Database.Client,
                container.Database.Id,
                container.Id,
                context,
                providerOptions,
                serviceProvider,
                logger);
            return new ValueTask<ChatHistoryProvider>(provider);
        };

        return options;
    }

    /// <summary>
    /// Configures the agent to use Cosmos DB for chat history with an existing <see cref="CosmosClient"/>.
    /// </summary>
    /// <param name="options">The agent options to configure.</param>
    /// <param name="client">The CosmosClient instance (user manages lifecycle and credentials).</param>
    /// <param name="databaseId">The database identifier.</param>
    /// <param name="containerId">The container identifier.</param>
    /// <param name="serviceProvider">Optional service provider for resolving tenant/user IDs via factories. 
    /// Required when using <see cref="CosmosChatHistoryProviderOptions.TenantIdFactory"/> or <see cref="CosmosChatHistoryProviderOptions.UserIdFactory"/>.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The configured options for method chaining.</returns>
    /// <remarks>
    /// Use this overload when not using Aspire or when you need full control over client configuration.
    /// <code>
    /// // With Managed Identity
    /// var client = new CosmosClient(endpoint, new DefaultAzureCredential());
    /// 
    /// // Or with connection string
    /// var client = new CosmosClient(connectionString);
    /// 
    /// var agentOptions = new ChatClientAgentOptions()
    ///     .WithCosmosChatHistoryProvider(client, "ChatHistory", "Conversations");
    /// </code>
    /// 
    /// <para><b>For multi-tenant scenarios with this overload:</b></para>
    /// <code>
    /// var agentOptions = new ChatClientAgentOptions()
    ///     .WithCosmosChatHistoryProvider(client, "ChatHistory", "Conversations", serviceProvider, opt =>
    ///     {
    ///         opt.TenantIdFactory = sp => sp.GetService&lt;IHttpContextAccessor&gt;()?.HttpContext?.User?.FindFirst("tenant_id")?.Value;
    ///         opt.UserIdFactory = sp => sp.GetService&lt;IHttpContextAccessor&gt;()?.HttpContext?.User?.FindFirst("sub")?.Value;
    ///     });
    /// </code>
    /// </remarks>
    public static ChatClientAgentOptions WithCosmosChatHistoryProvider(
        this ChatClientAgentOptions options,
        CosmosClient client,
        string databaseId,
        string containerId,
        IServiceProvider? serviceProvider = null,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerId);

        var providerOptions = new CosmosChatHistoryProviderOptions();
        configure?.Invoke(providerOptions);

        options.ChatHistoryProviderFactory = (context, _) =>
        {
            var provider = CreateProvider(client, databaseId, containerId, context, providerOptions, serviceProvider);
            return new ValueTask<ChatHistoryProvider>(provider);
        };

        return options;
    }

    private static CosmosChatHistoryProviderOptions CloneOptions(CosmosChatHistoryProviderOptions source) => new()
    {
        MessageTtlSeconds = source.MessageTtlSeconds,
        MaxMessagesToRetrieve = source.MaxMessagesToRetrieve,
        MaxItemCount = source.MaxItemCount,
        MaxBatchSize = source.MaxBatchSize,
        ChatReducer = source.ChatReducer,
        ReductionStoragePolicy = source.ReductionStoragePolicy,
        TenantIdFactory = source.TenantIdFactory,
        UserIdFactory = source.UserIdFactory
    };

    private static CosmosChatHistoryProvider CreateProvider(
        CosmosClient client,
        string databaseId,
        string containerId,
        ChatClientAgentOptions.ChatHistoryProviderFactoryContext context,
        CosmosChatHistoryProviderOptions options,
        IServiceProvider? serviceProvider = null,
        ILogger<CosmosChatHistoryProvider>? logger = null)
    {
        // Resolve tenant and user IDs at runtime via factories
        var tenantId = serviceProvider is not null ? options.TenantIdFactory?.Invoke(serviceProvider) : null;
        var userId = serviceProvider is not null ? options.UserIdFactory?.Invoke(serviceProvider) : null;
        var useHierarchicalPartitioning = !string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(userId);

        CosmosChatHistoryProvider provider = context.SerializedState.ValueKind == JsonValueKind.Object
            ? CosmosChatHistoryProvider.CreateFromSerializedState(
                client, context.SerializedState, databaseId, containerId,
                context.JsonSerializerOptions, options.ChatReducer, 
                options.ReductionStoragePolicy ?? default, logger)
            : useHierarchicalPartitioning
                ? new CosmosChatHistoryProvider(client, databaseId, containerId, tenantId!, userId!, Guid.NewGuid().ToString("N"), logger)
                  { ChatReducer = options.ChatReducer, ReductionStoragePolicy = options.ReductionStoragePolicy ?? default }
                : new CosmosChatHistoryProvider(client, databaseId, containerId, logger)
                  { ChatReducer = options.ChatReducer, ReductionStoragePolicy = options.ReductionStoragePolicy ?? default };

        if (options.MessageTtlSeconds.HasValue)
            provider.MessageTtlSeconds = options.MessageTtlSeconds;

        if (options.MaxMessagesToRetrieve.HasValue)
            provider.MaxMessagesToRetrieve = options.MaxMessagesToRetrieve;

        if (options.MaxItemCount.HasValue)
            provider.MaxItemCount = options.MaxItemCount.Value;

        if (options.MaxBatchSize.HasValue)
            provider.MaxBatchSize = options.MaxBatchSize.Value;

        return provider;
    }
}

/// <summary>
/// Internal registration holder for DI-based chat history provider configuration.
/// </summary>
internal sealed class CosmosChatHistoryProviderRegistration
{
    public string ContainerServiceKey { get; }
    public CosmosChatHistoryProviderOptions Options { get; }

    public CosmosChatHistoryProviderRegistration(string containerServiceKey, CosmosChatHistoryProviderOptions options)
    {
        ContainerServiceKey = containerServiceKey;
        Options = options;
    }
}

/// <summary>
/// Options for configuring <see cref="CosmosChatHistoryProvider"/> behavior.
/// </summary>
/// <remarks>
/// All properties are optional. When not set, the provider uses sensible defaults:
/// <list type="bullet">
///   <item><description>MessageTtlSeconds: 86400 (24 hours)</description></item>
///   <item><description>MaxMessagesToRetrieve: unlimited</description></item>
///   <item><description>MaxItemCount: 100</description></item>
///   <item><description>MaxBatchSize: 100</description></item>
/// </list>
/// </remarks>
public sealed class CosmosChatHistoryProviderOptions
{
    /// <summary>
    /// Time-To-Live in seconds for messages. Default is 86400 (24 hours). 
    /// Set to <c>null</c> to disable TTL (messages never expire).
    /// </summary>
    public int? MessageTtlSeconds { get; set; }

    /// <summary>
    /// Maximum number of messages to retrieve from history.
    /// Helps prevent exceeding LLM context windows in long conversations.
    /// </summary>
    public int? MaxMessagesToRetrieve { get; set; }

    /// <summary>
    /// Maximum number of items per Cosmos DB query page. Default is 100.
    /// </summary>
    public int? MaxItemCount { get; set; }

    /// <summary>
    /// Maximum items per transactional batch operation. Default is 100 (Cosmos DB limit).
    /// Ignored when connected to the Cosmos DB Emulator.
    /// </summary>
    public int? MaxBatchSize { get; set; }

    /// <summary>
    /// Gets the chat reducer used to process or reduce chat messages. If null, no reduction logic will be applied.
    /// </summary>
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    public IChatReducer? ChatReducer { get; set; }
#pragma warning restore MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

    /// <summary>
    /// Gets or sets the storage policy to apply when chat history reduction occurs.
    /// Default is <see cref="ReductionStoragePolicy.Clear"/> which deletes old messages.
    /// Use <see cref="ReductionStoragePolicy.Archive"/> to preserve original messages with an archived suffix.
    /// </summary>
    public ReductionStoragePolicy? ReductionStoragePolicy { get; set; }

    /// <summary>
    /// Factory function to resolve tenant ID at runtime from the current request context.
    /// When both <see cref="TenantIdFactory"/> and <see cref="UserIdFactory"/> return non-null values, 
    /// the provider uses a hierarchical partition key of (tenantId, userId, conversationId) for multi-tenant isolation.
    /// </summary>
    /// <remarks>
    /// <para><b>Example with JWT claims:</b></para>
    /// <code>
    /// builder.Services.AddHttpContextAccessor();
    /// builder.Services.AddCosmosChatHistoryProvider("conversations", opt => 
    /// {
    ///     opt.TenantIdFactory = sp => 
    ///     {
    ///         var httpContext = sp.GetService&lt;IHttpContextAccessor&gt;()?.HttpContext;
    ///         return httpContext?.User?.FindFirst("tenant_id")?.Value;
    ///     };
    /// });
    /// </code>
    /// </remarks>
    public Func<IServiceProvider, string?>? TenantIdFactory { get; set; }

    /// <summary>
    /// Factory function to resolve user ID at runtime from the current request context.
    /// When both <see cref="TenantIdFactory"/> and <see cref="UserIdFactory"/> return non-null values, 
    /// the provider uses a hierarchical partition key of (tenantId, userId, conversationId) for multi-tenant isolation.
    /// </summary>
    /// <remarks>
    /// <para><b>Example with JWT claims:</b></para>
    /// <code>
    /// builder.Services.AddHttpContextAccessor();
    /// builder.Services.AddCosmosChatHistoryProvider("conversations", opt => 
    /// {
    ///     opt.UserIdFactory = sp => 
    ///     {
    ///         var httpContext = sp.GetService&lt;IHttpContextAccessor&gt;()?.HttpContext;
    ///         return httpContext?.User?.FindFirst("sub")?.Value 
    ///             ?? httpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    ///     };
    /// });
    /// </code>
    /// </remarks>
    public Func<IServiceProvider, string?>? UserIdFactory { get; set; }
}
