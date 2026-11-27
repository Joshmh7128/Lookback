namespace Lookback.Contracts;

/// <summary>Well-known topic names on the event stream.</summary>
public static class Topics
{
    public const string Session = "session";
    public const string GameLifecycle = "game.lifecycle";
    public const string GameEvent = "game.event";
    public const string GameProfile = "game.profile";
    public const string SignalsVideo = "signals.video";
    public const string SignalsAudio = "signals.audio";
    public const string SignalsInput = "signals.input";
    public const string SignalsRules = "signals.rules";

    /// <summary>The clipper's excitement curve (<c>SIGNAL_FAMILY_CLIPPER</c>), for viewers.</summary>
    public const string SignalsClipper = "signals.clipper";
    public const string ClipCandidate = "clip.candidate";
    public const string ClipSaved = "clip.saved";
    public const string QaReport = "qa.report";
    public const string ClipRating = "clip.rating";
    public const string Storage = "recorder.storage";

    /// <summary>
    /// True when <paramref name="topic"/> matches <paramref name="pattern"/>.
    /// Patterns are exact names, <c>*</c> for everything, or a prefix ending
    /// in <c>.*</c> (so <c>signals.*</c> matches <c>signals.video</c>).
    /// </summary>
    public static bool Matches(string pattern, string topic)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(topic);

        if (pattern == "*")
        {
            return true;
        }

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> prefix = pattern.AsSpan(0, pattern.Length - 1); // keep the dot
            return topic.AsSpan().StartsWith(prefix, StringComparison.Ordinal);
        }

        return string.Equals(pattern, topic, StringComparison.Ordinal);
    }
}
