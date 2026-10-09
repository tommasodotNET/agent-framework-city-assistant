namespace SharedServices;

/// <summary>An explicit immutable profile; a null provider option disables compaction.</summary>
public sealed record HistoryCompactionOptions
{
    /// <summary>The provider lifecycle; the selected compactor must support this mode.</summary>
    public HistoryCompactionMode Mode { get; init; } = HistoryCompactionMode.Foreground;

    /// <summary>The nonempty keyed IHistoryCompactor registration identifying this strategy profile.</summary>
    public string CompactorKey { get; init; } = string.Empty;

    /// <summary>
    /// An optional timeout per foreground operation or background enqueue/retrieval call. This is
    /// not a background job deadline. Calls are cooperatively cancelled and awaited; null imposes no timeout.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Maximum time Save waits for a background result before appending the turn normally.
    /// Defaults to two seconds; zero performs a single non-blocking result check.
    /// </summary>
    public TimeSpan BackgroundSaveWaitTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Validates an enabled profile without resolving any services or invoking a strategy.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(CompactorKey);
        if (Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
        {
            // CancelAfter uses a finite unsigned-millisecond timer; infinite disables the safety limit.
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }
        if (BackgroundSaveWaitTimeout < TimeSpan.Zero
            || BackgroundSaveWaitTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(BackgroundSaveWaitTimeout));
        }
    }
}
