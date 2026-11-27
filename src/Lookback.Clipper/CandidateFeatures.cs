using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Lookback.Clipper;

/// <summary>
/// Game-agnostic numbers describing one candidate, by name, for the learned
/// scorer (M4) to map onto its own feature schema. In memory only: they are
/// not part of <c>ClipCandidate</c> on the wire, which M4 owns.
/// <para>
/// Names (version <see cref="Version"/>), all floats:
/// <list type="bullet">
/// <item><c>score.heuristic</c>, <c>score.raw</c>: the candidate's heuristic score (0..1) and the raw sum behind it.</item>
/// <item><c>window.seconds</c>; <c>window.peak_position</c>: where the peak sits, 0 at the start, 1 at the end.</item>
/// <item><c>source.events</c>, <c>source.curve</c>: 1 when events, or curve peaks, contributed.</item>
/// <item><c>events.count</c>, <c>events.kinds</c>, <c>events.score_sum</c>, <c>events.score_max</c>,
/// <c>events.confidence_mean</c>, <c>events.confidence_max</c>, <c>events.weight_max</c>.</item>
/// <item><c>curve.peaks</c> (folded peaks counted), <c>curve.peak_max</c>, <c>curve.score_sum</c>.</item>
/// <item><c>curve.mean</c>, <c>curve.max</c>, <c>curve.above_high</c>: the excitement curve over the window,
/// and the share of frames at or above the active threshold.</item>
/// <item><c>qa.black</c>, <c>qa.frozen</c>, <c>qa.silent</c>: the QA fractions (1 when nothing was measured);
/// <c>qa.passed</c>.</item>
/// <item><c>z_mean.&lt;signal&gt;</c>, <c>z_max.&lt;signal&gt;</c> for each curve component, in component order:
/// the clamped robust z-score over the window.</item>
/// </list>
/// </para>
/// </summary>
[SuppressMessage("Naming", "CA1710", Justification = "Named for what it is (the name M4 maps from); it is a read-only name-to-value map.")]
public sealed class CandidateFeatures : IReadOnlyDictionary<string, float>
{
    /// <summary>Changes whenever a name is added, removed or redefined.</summary>
    public const string Version = "clipper-features/1";

    private readonly Dictionary<string, int> _index;
    private readonly float[] _values;

    internal CandidateFeatures(FeatureNames names, float[] values)
    {
        Names = names.Names;
        _index = names.Index;
        _values = values;
    }

    /// <summary>Every name, in a fixed order for this engine.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Values in the order of <see cref="Names"/>.</summary>
    public IReadOnlyList<float> Values => _values;

    public int Count => _values.Length;

    public IEnumerable<string> Keys => Names;

    IEnumerable<float> IReadOnlyDictionary<string, float>.Values => _values;

    public float this[string key] => _values[_index[key]];

    public bool ContainsKey(string key) => _index.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out float value)
    {
        if (_index.TryGetValue(key, out int i))
        {
            value = _values[i];
            return true;
        }

        value = default;
        return false;
    }

    public IEnumerator<KeyValuePair<string, float>> GetEnumerator()
    {
        for (int i = 0; i < _values.Length; i++)
        {
            yield return new KeyValuePair<string, float>(Names[i], _values[i]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A finished candidate with its QA report and features (see <see cref="ClipperRunner.CandidateFinished"/>).</summary>
public sealed class CandidateFinishedEventArgs(ClipperOutput output) : EventArgs
{
    public ClipperOutput Output { get; } = output;
}

/// <summary>The feature names for one engine (they depend on its components), shared by every candidate.</summary>
internal sealed class FeatureNames
{
    public static readonly string[] Fixed =
    [
        "score.heuristic", "score.raw",
        "window.seconds", "window.peak_position",
        "source.events", "source.curve",
        "events.count", "events.kinds", "events.score_sum", "events.score_max", "events.confidence_mean", "events.confidence_max", "events.weight_max",
        "curve.peaks", "curve.peak_max", "curve.score_sum",
        "curve.mean", "curve.max", "curve.above_high",
        "qa.black", "qa.frozen", "qa.silent", "qa.passed",
    ];

    public FeatureNames(IReadOnlyList<string> components)
    {
        Names = [.. Fixed, .. components.Select(c => "z_mean." + c), .. components.Select(c => "z_max." + c)];
        Index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            Index[Names[i]] = i;
        }
    }

    public string[] Names { get; }

    public Dictionary<string, int> Index { get; }
}
