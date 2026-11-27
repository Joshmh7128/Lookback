using Lookback.Core.Time;
using Lookback.Profiles.Model;

namespace Lookback.Clipper.Curve;

/// <summary>
/// One profile's curve settings, resolved over the defaults and laid out
/// against the clipper's component list (the union of every profile's
/// components in the tree), with times in ticks.
/// </summary>
internal sealed class CurveProfile
{
    private CurveProfile(string id, float[] weights, float[] minSpread, ClipperSpec spec, long frequency)
    {
        Id = id;
        Weights = weights;
        MinSpread = minSpread;
        High = spec.High!.Value;
        Low = spec.Low!.Value;
        Pre = QpcTime.Rescale(spec.PreMs!.Value, 1000, frequency);
        Post = QpcTime.Rescale(spec.PostMs!.Value, 1000, frequency);
        Refractory = QpcTime.Rescale(spec.RefractoryMs!.Value, 1000, frequency);
        PeakScore = spec.PeakScore!.Value;
    }

    /// <summary>The profile id, or empty for the defaults on their own.</summary>
    public string Id { get; }

    /// <summary>Weight per component; 0 where this profile does not use it.</summary>
    public float[] Weights { get; }

    public float[] MinSpread { get; }

    public float High { get; }

    public float Low { get; }

    public long Pre { get; }

    public long Post { get; }

    public long Refractory { get; }

    public float PeakScore { get; }

    /// <summary>
    /// Resolves each spec over the defaults and lays them all out against one
    /// component list. Component order: the defaults first, then any signal a
    /// profile adds, in the order first seen.
    /// </summary>
    public static (IReadOnlyList<string> Components, IReadOnlyList<CurveProfile> Profiles) Build(
        IEnumerable<(string Id, ClipperSpec Spec)> specs, long frequency)
    {
        ArgumentNullException.ThrowIfNull(specs);
        var resolved = specs.Select(s => (s.Id, Spec: s.Spec.Over(ClipperDefaults.Spec))).ToList();
        var names = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CurveComponent c in resolved.SelectMany(r => r.Spec.Components).Where(c => c.Weight > 0))
        {
            if (index.TryAdd(c.Signal, names.Count))
            {
                names.Add(c.Signal);
            }
        }

        var profiles = new List<CurveProfile>();
        foreach ((string id, ClipperSpec spec) in resolved)
        {
            if (spec is not { High: { } high, Low: { } low } || low >= high)
            {
                throw new InvalidDataException($"clipper settings for '{id}': low must be below high.");
            }

            float[] weights = new float[names.Count];
            float[] spread = new float[names.Count];
            Array.Fill(spread, DefaultMinSpread);
            foreach (CurveComponent c in spec.Components)
            {
                if (index.TryGetValue(c.Signal, out int i))
                {
                    weights[i] = c.Weight;
                    spread[i] = c.MinSpread ?? DefaultMinSpread;
                }
            }

            profiles.Add(new CurveProfile(id, weights, spread, spec, frequency));
        }

        return (names, profiles);
    }

    /// <summary>Used for a component a profile adds without a <c>minSpread</c> and that the defaults do not know.</summary>
    public const float DefaultMinSpread = 0.01f;
}
