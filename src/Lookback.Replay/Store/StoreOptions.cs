namespace Lookback.Replay.Store;

/// <summary>Where the disk session store lives and how much it may use. The governor fills these in.</summary>
public sealed record StoreOptions
{
    /// <summary>Root folder; sessions go in <c>sessions/</c>, clips in <c>clips/</c>, previews in <c>previews/</c>.</summary>
    public required string Root { get; init; }

    /// <summary>Total bytes all session segments may take. Oldest unpinned segments are evicted beyond it.</summary>
    public long BudgetBytes { get; init; } = 50L << 30;

    /// <summary>Free space on the volume the store never goes below; eviction starts when it would.</summary>
    public long FreeSpaceFloorBytes { get; init; } = 10L << 30;

    /// <summary>Target segment length; segments are cut at the first video keyframe after it.</summary>
    public TimeSpan SegmentDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long after a session ends its unpinned segments are kept.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Total bytes the temporary previews may take; the least recently used go first beyond it.</summary>
    public long PreviewBudgetBytes { get; init; } = 1L << 30;

    /// <summary>How long a preview is kept after it was last watched.</summary>
    public TimeSpan PreviewMaxAge { get; init; } = TimeSpan.FromHours(24);

    /// <summary>For tests: reports free bytes on the store's volume. Null uses the real drive.</summary>
    public Func<long>? FreeSpaceProbe { get; init; }
}
