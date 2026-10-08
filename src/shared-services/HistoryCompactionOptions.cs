namespace SharedServices;

/// <summary>An explicit immutable profile; a null provider option disables compaction.</summary>
public sealed record HistoryCompactionOptions
{
    /// <summary>The provider lifecycle; the selected compactor must support this mode.</summary>
    public HistoryCompactionMode Mode { get; init; } = HistoryCompactionMode.Foreground;

    /// <summary>The nonempty keyed IHistoryCompactor registration identifying this strategy profile.</summary>
    public string CompactorKey { get; init; } = string.Empty;

    /// <summary>Optional positive byte cap for the complete history. Null (the default) disables the cap.</summary>
    /// <remarks>
    /// Measure the UTF-8 JSON array produced by System.Text.Json.JsonSerializer.SerializeToUtf8Bytes
    /// with the declared type IReadOnlyList&lt;Microsoft.Extensions.AI.ChatMessage&gt; and
    /// System.Text.Json.JsonSerializerOptions.Default, including roles, contents and metadata.
    /// This conservative history-only byte limit is not a tokenizer count or a full-prompt budget:
    /// callers must separately reserve capacity for system instructions, tools, new input and output.
    /// When set, unchanged history and completed candidates must fit before use or persistence.
    /// Null skips only this application byte cap: result integrity, source revisions and Cosmos limits
    /// still apply. Byte diagnostics remain available. This option never determines MAF's trigger.
    /// </remarks>
    public long? MaxHistoryUtf8Bytes { get; init; }

    /// <summary>
    /// An optional timeout per foreground operation or background enqueue/retrieval call. This is
    /// not a background job deadline. Calls are cooperatively cancelled and awaited; null imposes no timeout.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Validates an enabled profile without resolving any services or invoking a strategy.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(CompactorKey);
        if (MaxHistoryUtf8Bytes is { } budget)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget, nameof(MaxHistoryUtf8Bytes));
        }
        if (Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
        {
            // CancelAfter uses a finite unsigned-millisecond timer; infinite disables the safety limit.
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }
    }
}
