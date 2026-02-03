using System.Text.Json;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// // Then in agent options - no parameters needed
/// var agentOptions = new ChatClientAgentOptions()
///     .WithCosmosChatHistoryProvider();
/// </code>
/// 
/// <para><b>2. Using existing Container directly:</b></para>
/// <code>
/// var agentOptions = new ChatClientAgentOptions()
///     .WithCosmosChatHistoryProvider(container, opt => opt.MaxMessagesToRetrieve = 50);
/// </code>
/// 
/// <para><b>3. Using Managed Identity:</b></para>
/// <code>
/// var agentOptions = new ChatClientAgentOptions()
///     .WithCosmosChatHistoryProvider(
///         accountEndpoint: "https://myaccount.documents.azure.com:443/",
///         tokenCredential: new DefaultAzureCredential(),
///         databaseId: "ChatHistory",
///         containerId: "Conversations");
/// </code>
/// </remarks>
public static class CosmosChatHistoryProviderExtensions
{
    #region IServiceCollection Extensions

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
    /// Registers Cosmos DB chat history provider configuration using an existing Container.
    /// </summary>
    public static IServiceCollection AddCosmosChatHistoryProvider(
        this IServiceCollection services,
        Container container,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(container);

        var options = new CosmosChatHistoryProviderOptions();
        configure?.Invoke(options);

        services.AddSingleton(new CosmosChatHistoryProviderRegistration(container, options));

        return services;
    }

    #endregion

    #region ChatClientAgentOptions Extensions

    /// <summary>
    /// Configures the agent to use Cosmos DB for chat history from pre-registered DI configuration.
    /// </summary>
    /// <param name="options">The agent options to configure.</param>
    /// <param name="serviceProvider">The service provider to resolve the container from.</param>
    /// <returns>The configured options for method chaining.</returns>
    /// <remarks>
    /// Requires prior registration via <see cref="AddCosmosChatHistoryProvider(IServiceCollection, string, Action{CosmosChatHistoryProviderOptions}?)"/>.
    /// </remarks>
    public static ChatClientAgentOptions WithCosmosChatHistoryProvider(
        this ChatClientAgentOptions options,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var registration = serviceProvider.GetRequiredService<CosmosChatHistoryProviderRegistration>();
        var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<CosmosChatHistoryProvider>();

        var container = registration.Container 
            ?? serviceProvider.GetRequiredKeyedService<Container>(registration.ContainerServiceKey);

        options.ChatHistoryProviderFactory = (context, _) =>
        {
            var provider = CreateProvider(
                container.Database.Client,
                container.Database.Id,
                container.Id,
                context,
                registration.Options,
                logger);
            return new ValueTask<ChatHistoryProvider>(provider);
        };

        return options;
    }

    /// <summary>
    /// Configures the agent to use Cosmos DB for chat history with Entra ID (Managed Identity) authentication.
    /// </summary>
    public static ChatClientAgentOptions WithCosmosChatHistoryProvider(
        this ChatClientAgentOptions options,
        string accountEndpoint,
        TokenCredential tokenCredential,
        string databaseId,
        string containerId,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenCredential);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(accountEndpoint);

        var providerOptions = new CosmosChatHistoryProviderOptions();
        configure?.Invoke(providerOptions);

        options.ChatHistoryProviderFactory = (context, _) =>
        {
            var client = new CosmosClient(accountEndpoint, tokenCredential);
            var provider = CreateProvider(client, databaseId, containerId, context, providerOptions);
            return new ValueTask<ChatHistoryProvider>(provider);
        };

        return options;
    }

    /// <summary>
    /// Configures the agent to use Cosmos DB for chat history with an existing <see cref="CosmosClient"/>.
    /// </summary>
    public static ChatClientAgentOptions WithCosmosChatHistoryProvider(
        this ChatClientAgentOptions options,
        CosmosClient client,
        string databaseId,
        string containerId,
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
            var provider = CreateProvider(client, databaseId, containerId, context, providerOptions);
            return new ValueTask<ChatHistoryProvider>(provider);
        };

        return options;
    }

    /// <summary>
    /// Configures the agent to use Cosmos DB for chat history with an existing <see cref="Container"/>.
    /// </summary>
    public static ChatClientAgentOptions WithCosmosChatHistoryProvider(
        this ChatClientAgentOptions options,
        Container container,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        return options.WithCosmosChatHistoryProvider(container, configure, null);
    }

    private static ChatClientAgentOptions WithCosmosChatHistoryProvider(
        this ChatClientAgentOptions options,
        Container container,
        Action<CosmosChatHistoryProviderOptions>? configure,
        CosmosChatHistoryProviderOptions? preConfiguredOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(container);

        var providerOptions = preConfiguredOptions ?? new CosmosChatHistoryProviderOptions();
        configure?.Invoke(providerOptions);

        options.ChatHistoryProviderFactory = (context, _) =>
        {
            var provider = CreateProvider(
                container.Database.Client,
                container.Database.Id,
                container.Id,
                context,
                providerOptions);
            return new ValueTask<ChatHistoryProvider>(provider);
        };

        return options;
    }

    #endregion

    #region Private Helpers

    private static CosmosChatHistoryProvider CreateProvider(
        CosmosClient client,
        string databaseId,
        string containerId,
        ChatClientAgentOptions.ChatHistoryProviderFactoryContext context,
        CosmosChatHistoryProviderOptions options,
        ILogger<CosmosChatHistoryProvider>? logger = null)
    {
        var provider = context.SerializedState.ValueKind == JsonValueKind.Object
            ? CosmosChatHistoryProvider.CreateFromSerializedState(
                client,
                context.SerializedState,
                databaseId,
                containerId,
                context.JsonSerializerOptions,
                logger)
            : new CosmosChatHistoryProvider(client, databaseId, containerId, logger);

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

    #endregion
}

/// <summary>
/// Internal registration holder for DI-based chat history provider configuration.
/// </summary>
internal sealed class CosmosChatHistoryProviderRegistration
{
    public string? ContainerServiceKey { get; }
    public Container? Container { get; }
    public CosmosChatHistoryProviderOptions Options { get; }

    public CosmosChatHistoryProviderRegistration(string containerServiceKey, CosmosChatHistoryProviderOptions options)
    {
        ContainerServiceKey = containerServiceKey;
        Options = options;
    }

    public CosmosChatHistoryProviderRegistration(Container container, CosmosChatHistoryProviderOptions options)
    {
        Container = container;
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
}
