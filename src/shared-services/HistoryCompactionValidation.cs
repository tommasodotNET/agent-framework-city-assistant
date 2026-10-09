#pragma warning disable MAAI001 // The installed MAF compaction API is experimental.

using System.Globalization;
using System.Resources;
using System.Text.Json;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace SharedServices;

/// <summary>A machine-readable reason why history cannot be compacted or used safely.</summary>
public enum HistoryCompactionFailureReason
{
    // Keep the remaining diagnostic reason values stable.
    /// <summary>A replacement history is empty.</summary>
    EmptyCandidate = 1,
    /// <summary>The candidate is not bound to the requested source version.</summary>
    SourceBindingMismatch,
    /// <summary>Reported byte counts do not match independently measured history.</summary>
    InvalidDiagnostics,
    /// <summary>An unchanged result contains different history.</summary>
    InvalidUnchangedResult,
    /// <summary>A changed candidate does not reduce serialized history size.</summary>
    NotReduced,
    /// <summary>Protected instructions moved/changed or are interleaved, or a retained approval was tampered with.</summary>
    ProtectedMessagesChanged,
    /// <summary>Tools are pending, unsupported, malformed, or no longer atomic.</summary>
    UnsafeToolHistory,
    /// <summary>A summary is blank or contains the framework's unavailable-summary fallback.</summary>
    InvalidSummary,
    /// <summary>An outcome or ticket does not belong to the requested lifecycle.</summary>
    InvalidLifecycle
}

/// <summary>
/// Rejects an unsafe compaction result without changing history. A provider may fall back only after
/// validating the original history and the exact storage revision.
/// </summary>
public sealed class HistoryCompactionValidationException : InvalidOperationException
{
    /// <summary>Creates a localized failure with a stable, content-free reason.</summary>
    public HistoryCompactionValidationException(HistoryCompactionFailureReason reason)
        : base(GetMessage(reason))
    {
        Reason = reason;
    }

    /// <summary>The validation failure; no message contents or source binding are included.</summary>
    public HistoryCompactionFailureReason Reason { get; }

    private static string GetMessage(HistoryCompactionFailureReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return CompactionErrors.Get(reason.ToString());
    }
}

/// <summary>Storage-independent checks which providers must apply even to third-party compactors.</summary>
public static class HistoryCompactionValidation
{
    /// <summary>Measures UTF-8 JSON for the complete message array, including roles, contents and metadata.</summary>
    /// <remarks>This is a diagnostic/reduction metric, not a model context or Cosmos document limit.</remarks>
    public static long Measure(IReadOnlyList<ChatMessage> messages) => Serialize(messages).LongLength;

    /// <summary>
    /// Checks whether original history can be used after a no-op or failure. Pending tools are allowed
    /// in original history; this does not authorize rewriting them or prove the storage revision.
    /// Only explicit summary metadata identifies summaries in ordinary history.
    /// </summary>
    public static void ValidateFallback(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ValidateMessages(messages);
        ValidateSummaries(messages);
    }

    /// <summary>
    /// Independently validates binding, diagnostics, no-op identity, and replacement safety. Call
    /// before any persistence; this neither performs nor replaces the provider's revision check.
    /// </summary>
    public static void ValidateResult(HistoryCompactionRequest request, HistoryCompactionResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        request.Options.Validate();
        if (result.Status == HistoryCompactionStatus.Pending)
        {
            Fail(HistoryCompactionFailureReason.InvalidLifecycle);
        }
        if (!string.Equals(request.SourceBinding, result.SourceBinding, StringComparison.Ordinal))
        {
            Fail(HistoryCompactionFailureReason.SourceBindingMismatch);
        }

        var sourceJson = Serialize(request.Messages);
        if (sourceJson.LongLength != result.BeforeUtf8Bytes)
        {
            Fail(HistoryCompactionFailureReason.InvalidDiagnostics);
        }
        var resultJson = Serialize(result.Messages);
        if (resultJson.LongLength != result.AfterUtf8Bytes)
        {
            Fail(HistoryCompactionFailureReason.InvalidDiagnostics);
        }

        if (result.Status == HistoryCompactionStatus.Unchanged)
        {
            if (!Equivalent(sourceJson, resultJson))
            {
                Fail(HistoryCompactionFailureReason.InvalidUnchangedResult);
            }

            ValidateFallback(request.Messages);
            return;
        }

        ValidateCandidate(request.Messages, result.Messages);
    }

    /// <summary>
    /// Checks a nonempty, genuinely smaller replacement, an unchanged protected instruction prefix,
    /// and complete tool groups. Interleaved system/developer instructions are not compactable.
    /// Retained tool groups must
    /// be byte-semantically identical; entire completed groups may be removed or summarized.
    /// Consumed approvals may be removed, but retained approval messages must be an unchanged
    /// subsequence of the source and remain informational with their complete function exchange.
    /// </summary>
    public static void ValidateCandidate(
        IReadOnlyList<ChatMessage> source,
        IReadOnlyList<ChatMessage> candidate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Count == 0 || !candidate.Any(message => message is not null && message.Contents.Any(content =>
            content is not TextContent text || !string.IsNullOrWhiteSpace(text.Text))))
        {
            Fail(HistoryCompactionFailureReason.EmptyCandidate);
        }

        ValidateMessages(candidate);
        ValidateSummaries(candidate, source);
        var sourceTools = GetToolGroups(source);
        ValidateProcessedApprovals(source, sourceTools);
        var candidateJson = Serialize(candidate);
        var sourceJson = Serialize(source);
        if (candidateJson.LongLength >= sourceJson.LongLength || Equivalent(sourceJson, candidateJson))
        {
            Fail(HistoryCompactionFailureReason.NotReduced);
        }

        var protectedSource = GetProtectedPrefix(source);
        var protectedCandidate = GetProtectedPrefix(candidate);
        if (!Equivalent(protectedSource, protectedCandidate))
        {
            Fail(HistoryCompactionFailureReason.ProtectedMessagesChanged);
        }

        ValidateRetainedApprovals(source, candidate);
        var candidateTools = GetToolGroups(candidate);
        var sourcePosition = 0;
        foreach (var group in candidateTools)
        {
            // Matching whole ordered groups also prevents partial removal of parallel tool calls,
            // orphan results, altered arguments/approval flags, and newly invented tool executions.
            while (sourcePosition < sourceTools.Count && !Equivalent(sourceTools[sourcePosition], group))
            {
                sourcePosition++;
            }

            if (sourcePosition == sourceTools.Count)
            {
                Fail(HistoryCompactionFailureReason.UnsafeToolHistory);
            }

            sourcePosition++;
        }

        ValidateProcessedApprovals(candidate, candidateTools);
    }

    internal static byte[] Serialize(IReadOnlyList<ChatMessage> messages)
    {
        ValidateMessages(messages);
        return JsonSerializer.SerializeToUtf8Bytes<IReadOnlyList<ChatMessage>>(messages, JsonSerializerOptions.Default);
    }

    internal static List<ChatMessage> Detach(byte[] json) =>
        JsonSerializer.Deserialize<List<ChatMessage>>(json, HistoryJson.Options)
        ?? throw new JsonException(CompactionErrors.Get("InvalidSerialization"));

    private static void ValidateMessages(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Any(message => message is null))
        {
            throw new ArgumentException(CompactionErrors.Get("NullMessage"), nameof(messages));
        }
        if (messages.Any(message => message.Contents.Any(content => content is null)))
        {
            throw new ArgumentException(CompactionErrors.Get("NullContent"), nameof(messages));
        }
    }

    internal static bool Equivalent(IReadOnlyList<ChatMessage> left, IReadOnlyList<ChatMessage> right) =>
        Equivalent(Serialize(left), Serialize(right));

    private static bool Equivalent(byte[] left, byte[] right)
    {
        using var leftJson = JsonDocument.Parse(left);
        using var rightJson = JsonDocument.Parse(right);
        // JSON objects can have reordered properties after storage or a supported round-trip.
        return JsonElement.DeepEquals(leftJson.RootElement, rightJson.RootElement);
    }

    internal static void ValidateSourceForCompaction(IReadOnlyList<ChatMessage> source)
    {
        _ = GetProtectedPrefix(source);
        ValidateProcessedApprovals(source, GetToolGroups(source));
    }

    internal static Dictionary<string, FunctionCallContent> GetCompletedFunctionCalls(IReadOnlyList<ChatMessage> messages)
    {
        ValidateMessages(messages);
        return GetToolGroups(messages).SelectMany(group => group)
            .SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .ToDictionary(call => call.CallId, StringComparer.Ordinal);
    }

    private static ChatMessage[] GetProtectedPrefix(IReadOnlyList<ChatMessage> messages)
    {
        ValidateMessages(messages);
        var prefix = messages.TakeWhile(IsProtected).ToArray();
        if (messages.Skip(prefix.Length).Any(IsProtected))
            Fail(HistoryCompactionFailureReason.ProtectedMessagesChanged);
        return prefix;
    }

    private static List<IReadOnlyList<ChatMessage>> GetToolGroups(IReadOnlyList<ChatMessage> messages)
    {
        var groups = new List<IReadOnlyList<ChatMessage>>();
        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var results = new HashSet<string>(StringComparer.Ordinal);
        var leadingReasoning = new List<ChatMessage>();
        List<ChatMessage>? exchange = null;

        // This is a bounded, single-pass protocol check, not a reduction/grouping algorithm. Accept
        // only complete contiguous function exchanges, with reasoning kept attached. No MAF index
        // factory or strategy-specific mutable state is needed to validate a plugin's output.
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is ToolCallContent and not FunctionCallContent
                    || content is ToolResultContent and not FunctionResultContent)
                {
                    // Other hosted tool protocols require their own pairing rules before rewriting.
                    Fail(HistoryCompactionFailureReason.UnsafeToolHistory);
                }
            }

            var reasoningOnly = message.Role == ChatRole.Assistant
                && message.Contents.All(content => content is TextReasoningContent);
            if (exchange is not null && (message.Role == ChatRole.Tool || reasoningOnly))
            {
                if (message.Role == ChatRole.Tool)
                {
                    var resultCount = 0;
                    foreach (var content in message.Contents)
                    {
                        if (content is FunctionCallContent)
                        {
                            Fail(HistoryCompactionFailureReason.UnsafeToolHistory);
                        }
                        if (content is FunctionResultContent result)
                        {
                            if (!calls.Contains(result.CallId) || !results.Add(result.CallId))
                            {
                                Fail(HistoryCompactionFailureReason.UnsafeToolHistory);
                            }
                            resultCount++;
                        }
                    }
                    if (resultCount == 0)
                    {
                        Fail(HistoryCompactionFailureReason.UnsafeToolHistory);
                    }
                }
                exchange.Add(message);
                continue;
            }

            if (exchange is not null)
            {
                CompleteExchange(exchange, calls, results, groups);
                exchange = null;
                calls.Clear();
                results.Clear();
            }

            if (message.Role == ChatRole.Tool || message.Contents.Any(content => content is FunctionResultContent))
            {
                Fail(HistoryCompactionFailureReason.UnsafeToolHistory);
            }

            var hasCalls = false;
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    if (message.Role != ChatRole.Assistant || string.IsNullOrWhiteSpace(call.CallId)
                        || !calls.Add(call.CallId) || !seenCallIds.Add(call.CallId))
                    {
                        Fail(HistoryCompactionFailureReason.UnsafeToolHistory);
                    }
                    hasCalls = true;
                }
            }

            if (hasCalls)
            {
                exchange = [.. leadingReasoning, message];
                leadingReasoning.Clear();
            }
            else if (reasoningOnly)
            {
                leadingReasoning.Add(message);
            }
            else
            {
                leadingReasoning.Clear();
            }
        }

        if (exchange is not null)
        {
            CompleteExchange(exchange, calls, results, groups);
        }
        return groups;
    }

    private static void CompleteExchange(
        List<ChatMessage> exchange,
        HashSet<string> calls,
        HashSet<string> results,
        List<IReadOnlyList<ChatMessage>> groups)
    {
        if (!calls.SetEquals(results))
        {
            Fail(HistoryCompactionFailureReason.UnsafeToolHistory);
        }
        groups.Add(exchange);
    }

    private static bool IsProtected(ChatMessage message) =>
        message.Role == ChatRole.System
        || string.Equals(message.Role.Value, "developer", StringComparison.Ordinal);

    private static void ValidateRetainedApprovals(
        IReadOnlyList<ChatMessage> source, IReadOnlyList<ChatMessage> candidate)
    {
        var original = source.Where(HasApproval).ToArray();
        var position = 0;
        foreach (var retained in candidate.Where(HasApproval))
        {
            // C1 retains the audit. Completed old records are removable, not permanent system
            // instructions. Exact ordered matching prevents new approvals, changed decisions,
            // metadata changes, duplicates, or re-arming an informational nested tool call.
            while (position < original.Length && !Equivalent([original[position]], [retained]))
            {
                position++;
            }
            if (position == original.Length)
            {
                Fail(HistoryCompactionFailureReason.ProtectedMessagesChanged);
            }
            position++;
        }
    }

    private static bool HasApproval(ChatMessage message) =>
        message.Contents.Any(content => content is ToolApprovalRequestContent or ToolApprovalResponseContent);

    private static void ValidateProcessedApprovals(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<IReadOnlyList<ChatMessage>> completedGroups)
    {
        var completedCalls = completedGroups.SelectMany(group => group)
            .SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .ToDictionary(call => call.CallId, StringComparer.Ordinal);
        foreach (var content in messages.SelectMany(message => message.Contents))
        {
            var approvalCall = content switch
            {
                ToolApprovalRequestContent request => request.ToolCall,
                ToolApprovalResponseContent response => response.ToolCall,
                _ => null
            };
            if (approvalCall is null)
            {
                continue;
            }

            // An informational flag alone is not proof of consumption. Require a completed
            // matching function exchange, including in the candidate, so future loads cannot
            // mistake a leftover approval for pending work after its result was summarized away.
            if (approvalCall is not FunctionCallContent { InformationalOnly: true } call
                || !completedCalls.TryGetValue(call.CallId, out var completed) || !EquivalentCall(call, completed))
            {
                Fail(HistoryCompactionFailureReason.UnsafeToolHistory);
            }
        }
    }

    // Persisted direct calls can have their earlier informational flag while the provider restores
    // consumed approval flags. Compare execution identity/metadata here; retained full groups and
    // retained approval messages separately require exact equality, including their original flags.
    internal static bool EquivalentCall(FunctionCallContent left, FunctionCallContent right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(new { left.CallId, left.Name, left.Arguments, left.Annotations, left.AdditionalProperties }),
            JsonSerializer.SerializeToElement(new { right.CallId, right.Name, right.Arguments, right.Annotations, right.AdditionalProperties }));

    private static void ValidateSummaries(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatMessage>? original = null)
    {
        foreach (var message in messages)
        {
            var text = message.Text.Trim();
            var isSummary = message.AdditionalProperties?.TryGetValue(CompactionMessageGroup.SummaryPropertyKey, out var value) == true
                && (value is true || value is JsonElement { ValueKind: JsonValueKind.True });
            if (!isSummary && (original is null || message.Role != ChatRole.Assistant
                || (!text.StartsWith("[Summary]", StringComparison.Ordinal)
                    && !string.Equals(text, "[Summary unavailable]", StringComparison.Ordinal))
                || original.Any(source => Equivalent([source], [message]))))
            {
                continue;
            }

            if (text.StartsWith("[Summary]", StringComparison.Ordinal))
            {
                text = text["[Summary]".Length..].Trim();
            }

            // Guard new compactor output even when it drops summary metadata. An unchanged source
            // message or current-turn reply is not a generated summary merely because of its text.
            if (message.Role != ChatRole.Assistant || string.IsNullOrWhiteSpace(text)
                || text.Contains("[Summary unavailable]", StringComparison.Ordinal))
            {
                Fail(HistoryCompactionFailureReason.InvalidSummary);
            }
        }
    }

    private static void Fail(HistoryCompactionFailureReason reason) => throw new HistoryCompactionValidationException(reason);
}

internal static class CompactionErrors
{
    private static readonly ResourceManager s_resources = new("SharedServices.CompactionResources", typeof(CompactionErrors).Assembly);

    internal static string Get(string name) => s_resources.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException(name);
}
