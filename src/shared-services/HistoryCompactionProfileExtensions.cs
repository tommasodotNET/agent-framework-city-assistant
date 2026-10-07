#pragma warning disable MAAI001 // Explicit test profiles use the installed public MAF compaction API.

using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharedServices;

/// <summary>Opt-in composition of a history profile, separate from the agent's model and tools.</summary>
public static class HistoryCompactionProfileExtensions
{
    /// <summary>
    /// Reads an explicit HistoryCompaction section and returns its immutable provider options.
    /// An absent/empty section or Enabled=false disables the feature without registering a service.
    /// </summary>
    /// <remarks>
    /// CompactorKey is mandatory when enabled. MaxHistoryUtf8Bytes is an optional positive cap;
    /// missing or null disables that cap. Mode defaults to Foreground;
    /// Timeout is an optional TimeSpan. The test-sliding-window key additionally requires positive
    /// MaxTurns and registers a model-free MAF strategy preserving that many recent turns. Other
    /// keys are supplied by the caller through normal keyed IHistoryCompactor registrations.
    /// </remarks>
    public static HistoryCompactionOptions? AddHistoryCompactionProfile(
        this IServiceCollection services, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        if (!section.Exists() || section.GetValue<bool?>("Enabled") == false)
            return null;

        var options = new HistoryCompactionOptions
        {
            CompactorKey = section["CompactorKey"] ?? string.Empty,
            Mode = section.GetValue<HistoryCompactionMode?>("Mode") ?? HistoryCompactionMode.Foreground,
            MaxHistoryUtf8Bytes = section.GetValue<long?>("MaxHistoryUtf8Bytes"),
            Timeout = section.GetValue<TimeSpan?>("Timeout")
        };
        options.Validate();
        if (string.Equals(options.CompactorKey, "test-sliding-window", StringComparison.Ordinal))
        {
            if (options.Mode != HistoryCompactionMode.Foreground)
                throw new NotSupportedException(CompactionErrors.Get("UnsupportedMode"));
            var maxTurns = section.GetValue<int?>("MaxTurns") ?? 0;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTurns);
            services.AddHistoryCompactor(options.CompactorKey, provider =>
                new MafForegroundHistoryCompactor(
                    new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(maxTurns),
                        minimumPreservedTurns: maxTurns),
                    provider.GetService<ILogger<MafForegroundHistoryCompactor>>()));
        }
        return options;
    }
}
