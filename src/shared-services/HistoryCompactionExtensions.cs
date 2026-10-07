using Microsoft.Extensions.DependencyInjection;

namespace SharedServices;

/// <summary>Storage-independent keyed registration and composition-time capability validation.</summary>
public static class HistoryCompactionExtensions
{
    /// <summary>Registers a keyed singleton compactor; its strategy must support concurrent calls.</summary>
    /// <remarks>
    /// The factory can inject a Microsoft.Agents.AI.Compaction.CompactionStrategy and its dependencies.
    /// Do not capture scoped dependencies in a longer-lived provider; its lifetime must match the strategy.
    /// This registration creates no history provider and enables no compaction by itself.
    /// </remarks>
    public static IServiceCollection AddHistoryCompactor<TCompactor>(
        this IServiceCollection services,
        string compactorKey,
        Func<IServiceProvider, TCompactor> factory)
        where TCompactor : class, IHistoryCompactor
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(compactorKey);
        ArgumentNullException.ThrowIfNull(factory);

        return services.AddKeyedSingleton<IHistoryCompactor>(compactorKey, (provider, _) => factory(provider));
    }

    /// <summary>
    /// Resolves and validates a keyed compactor once at composition time; null options means disabled
    /// and performs no service lookup. Providers must retain the resolved dependency, not resolve on Load.
    /// </summary>
    public static IHistoryCompactor? GetHistoryCompactor(
        this IServiceProvider serviceProvider,
        HistoryCompactionOptions? options)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        if (options is null)
        {
            return null;
        }

        options.Validate();
        var compactor = serviceProvider.GetRequiredKeyedService<IHistoryCompactor>(options.CompactorKey);
        ValidateCapabilities(compactor, options);
        return compactor;
    }

    internal static void ValidateCapabilities(IHistoryCompactor compactor, HistoryCompactionOptions options)
    {
        if (compactor.SupportedModes is null || !compactor.SupportedModes.Contains(options.Mode)
            || (options.Mode == HistoryCompactionMode.Background && compactor is not IBackgroundHistoryCompactor))
        {
            throw new NotSupportedException(CompactionErrors.Get("UnsupportedMode"));
        }
    }
}
