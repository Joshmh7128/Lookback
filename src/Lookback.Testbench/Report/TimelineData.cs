namespace Lookback.Testbench.Report;

/// <summary>Everything the HTML timeline draws, serialised as JSON into the page.</summary>
internal sealed class TimelineData
{
    public required string Title { get; init; }

    public required string SessionId { get; init; }

    public required string SourceKind { get; init; }

    public required string Retention { get; init; }

    public string? Video { get; init; }

    public double DurationSeconds { get; set; }

    /// <summary>Scalar lanes, one per signal, already bucketed.</summary>
    public List<Lane> Lanes { get; } = [];

    /// <summary>Log-mel spectrogram, bucketed: Mel[bucket][band].</summary>
    public List<float[]> Mel { get; } = [];

    public double MelBucketSeconds { get; set; }

    internal sealed class Lane
    {
        public required string Name { get; init; }

        public required string Family { get; init; }

        /// <summary>Seconds per bucket.</summary>
        public required double Step { get; init; }

        public required List<float> Values { get; init; }
    }
}
