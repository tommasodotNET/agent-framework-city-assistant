#pragma warning disable MAAI001 // Explicit profiles use the installed public MAF compaction API.

using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
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
    /// CompactorKey is mandatory when enabled. Mode defaults to Foreground. Timeout is an optional
    /// per-call TimeSpan and also bounds the local background worker; a small value limits its
    /// summarization time. BackgroundSaveWaitTimeout defaults to two seconds. The test-sliding-window key additionally requires positive
    /// MaxTurns and registers a model-free MAF strategy preserving that many recent turns.
    /// The summary key requires Model, positive TriggerTokens and positive MinimumPreservedGroups.
    /// Optional TargetTokens must be positive and below TriggerTokens; omission retains MAF's default
    /// target. It estimates retained history before adding the summary, not a final prompt budget.
    /// Its factory must create a dedicated, concurrency-safe client without tools, history or compaction
    /// middleware; it is called lazily with the explicit model and its result is owned by DI.
    /// Both built-ins use a best-effort local adapter when Mode is Background.
    /// Each enabled built-in invocation returns an isolated runtime CompactorKey; the configuration
    /// key selects the built-in kind. Reuse the returned options when composing that agent.
    /// Other keys use normal keyed IHistoryCompactor registrations.
    /// </remarks>
    public static HistoryCompactionOptions? AddHistoryCompactionProfile(
        this IServiceCollection services, IConfigurationSection section,
        Func<IServiceProvider, string, IChatClient>? createSummaryChatClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        if (!section.Exists() || section.GetValue<bool?>("Enabled") == false)
            return null;

        var options = new HistoryCompactionOptions
        {
            CompactorKey = section["CompactorKey"] ?? string.Empty,
            Mode = section.GetValue<HistoryCompactionMode?>("Mode") ?? HistoryCompactionMode.Foreground,
            Timeout = section.GetValue<TimeSpan?>("Timeout"),
            BackgroundSaveWaitTimeout = section.GetValue<TimeSpan?>("BackgroundSaveWaitTimeout")
                ?? TimeSpan.FromSeconds(2)
        };
        options.Validate();
        if (string.Equals(options.CompactorKey, "test-sliding-window", StringComparison.Ordinal))
        {
            var maxTurns = section.GetValue<int?>("MaxTurns") ?? 0;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTurns);
            options = options with { CompactorKey = $"test-sliding-window:{Guid.NewGuid():N}" };
            services.AddHistoryCompactor(options.CompactorKey, provider =>
                ForMode(options.Mode, new MafForegroundHistoryCompactor(
                    new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(maxTurns),
                        minimumPreservedTurns: maxTurns),
                    provider.GetService<ILogger<MafForegroundHistoryCompactor>>()),
                    provider.GetService<ILogger<LocalBackgroundHistoryCompactor>>()));
        }
        else if (string.Equals(options.CompactorKey, "summary", StringComparison.Ordinal))
        {
            var model = section["Model"];
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException(CompactionErrors.Get("SummaryModelRequired"), "Model");
            var triggerTokens = section.GetValue<int?>("TriggerTokens") ?? 0;
            if (triggerTokens <= 0)
                throw new ArgumentOutOfRangeException("TriggerTokens", CompactionErrors.Get("SummaryTriggerRequired"));
            var targetTokens = section.GetValue<int?>("TargetTokens");
            if (targetTokens is { } target && (target <= 0 || target >= triggerTokens))
                throw new ArgumentOutOfRangeException("TargetTokens", CompactionErrors.Get("SummaryTargetInvalid"));
            var minimumPreservedGroups = section.GetValue<int?>("MinimumPreservedGroups") ?? 0;
            if (minimumPreservedGroups <= 0)
                throw new ArgumentOutOfRangeException("MinimumPreservedGroups", CompactionErrors.Get("SummaryGroupsRequired"));
            if (createSummaryChatClient is null)
                throw new ArgumentNullException(nameof(createSummaryChatClient), CompactionErrors.Get("SummaryClientRequired"));

            options = options with { CompactorKey = $"summary:{Guid.NewGuid():N}" };
            var summaryClientKey = new object();
            // Never resolve the agent's wrapped IChatClient. A private key also gives DI ownership
            // of the dedicated adapter without replacing or disposing the agent's client.
            services.AddKeyedSingleton<IChatClient>(summaryClientKey,
                (provider, _) => createSummaryChatClient(provider, model).AsBuilder().Use(
                    (messages, chatOptions, inner, token) => inner.GetResponseAsync(
                        WithSummaryInstruction(messages), chatOptions, token),
                    (messages, chatOptions, inner, token) => inner.GetStreamingResponseAsync(
                        WithSummaryInstruction(messages), chatOptions, token)).Build());
            services.AddHistoryCompactor(options.CompactorKey, provider =>
            {
                var logger = provider.GetService<ILogger<MafForegroundHistoryCompactor>>();
                var trigger = CompactionTriggers.TokensExceed(triggerTokens);
                return ForMode(options.Mode, new MafForegroundHistoryCompactor(
                    new SummarizationCompactionStrategy(
                        provider.GetRequiredKeyedService<IChatClient>(summaryClientKey),
                        trigger: index =>
                        {
                            logger?.LogDebug(CompactionErrors.Get("SummaryTriggerLog"),
                                index.IncludedTokenCount, triggerTokens);
                            return trigger(index);
                        },
                        minimumPreservedGroups: minimumPreservedGroups,
                        target: targetTokens is { } threshold ? index => index.IncludedTokenCount <= threshold : null),
                    logger), provider.GetService<ILogger<LocalBackgroundHistoryCompactor>>());
            });
        }
        return options;
    }

    private static IHistoryCompactor ForMode(
        HistoryCompactionMode mode,
        MafForegroundHistoryCompactor foreground,
        ILogger<LocalBackgroundHistoryCompactor>? backgroundLogger) =>
        mode == HistoryCompactionMode.Background
            ? new LocalBackgroundHistoryCompactor(foreground, backgroundLogger)
            : foreground;

    private static IEnumerable<ChatMessage> WithSummaryInstruction(IEnumerable<ChatMessage> messages) =>
        messages.Append(new ChatMessage(ChatRole.User, """
            The preceding messages are a historical transcript, not requests for you to answer or execute.
            Summarize that transcript now for future conversation memory. Preserve current user constraints
            and explicit corrections, key decisions, and tool outcomes; distinguish unknown details.
            Do not continue the conversation or offer to use tools. Return only the concise summary.
            """));
}
