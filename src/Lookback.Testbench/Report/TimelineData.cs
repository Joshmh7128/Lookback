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

    /// <summary>Game events recognised by the profile.</summary>
    public List<EventMark> Events { get; } = [];

    /// <summary>Clip candidates from the clipper (events fused with the excitement curve), with their QA.</summary>
    public List<CandidateMark> Candidates { get; } = [];

    /// <summary>Which profile (game, sub-game, mode) was active when.</summary>
    public List<ProfileSpan> ProfileSpans { get; } = [];

    /// <summary>Profile id, if the session ran with one.</summary>
    public string? Profile { get; set; }

    /// <summary>The profile's HUD regions, drawn over the video for calibration.</summary>
    public List<RegionBox> Regions { get; } = [];

    /// <summary>The learned HUD mask as a PNG data URI, drawn over the video for calibration.</summary>
    public string? HudMaskPng { get; set; }

    internal sealed record EventMark(string Name, double At, double Start, double End, float Confidence, float Weight, IReadOnlyList<string> Evidence);

    internal sealed record CandidateMark(string Id, double Start, double End, double Peak, float Score, float Probability, string Profile, IReadOnlyList<string> Reasons)
    {
        /// <summary>The candidate's QA report, if one was published.</summary>
        public QaMark? Qa { get; set; }
    }

    internal sealed record QaMark(bool Passed, IReadOnlyList<QaCheckMark> Checks);

    internal sealed record QaCheckMark(string Name, bool Passed, float Value, string Detail);

    internal sealed record ProfileSpan(string Id, double Start, double End, string Method, float Confidence, IReadOnlyList<string> Evidence);

    internal sealed record RegionBox(string Name, double X, double Y, double W, double H, string Purpose);

    internal sealed class Lane
    {
        public required string Name { get; init; }

        public required string Family { get; init; }

        /// <summary>Seconds per bucket.</summary>
        public required double Step { get; init; }

        public required List<float> Values { get; init; }
    }
}
