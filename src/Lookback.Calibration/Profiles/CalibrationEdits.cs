using Lookback.Calibration.Probe;
using Lookback.Profiles.Model;

namespace Lookback.Calibration.Profiles;

/// <summary>
/// Confirmed numbers for a profile tree: region rectangles by name and rule
/// thresholds by condition. This is everything a calibration can ever write;
/// it has no room for pixels, signal series, times or anything about the video.
/// </summary>
public sealed record CalibrationEdits
{
    public IReadOnlyDictionary<string, (double X, double Y, double W, double H)> Regions { get; init; } =
        new Dictionary<string, (double, double, double, double)>(StringComparer.Ordinal);

    public IReadOnlyDictionary<ConditionKey, float> Thresholds { get; init; } = new Dictionary<ConditionKey, float>();

    public bool IsEmpty => Regions.Count == 0 && Thresholds.Count == 0;

    /// <summary>These edits on top of <paramref name="earlier"/> (later values win).</summary>
    public CalibrationEdits Over(CalibrationEdits earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        var regions = new Dictionary<string, (double, double, double, double)>(earlier.Regions, StringComparer.Ordinal);
        foreach (var kv in Regions)
        {
            regions[kv.Key] = kv.Value;
        }

        var thresholds = new Dictionary<ConditionKey, float>(earlier.Thresholds);
        foreach (var kv in Thresholds)
        {
            thresholds[kv.Key] = kv.Value;
        }

        return new CalibrationEdits { Regions = regions, Thresholds = thresholds };
    }

    /// <summary>The catalog with these edits applied in memory, for trying them live before saving.</summary>
    public ProfileCatalog ApplyTo(ProfileCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new ProfileCatalog(catalog.All.Select(Apply));
    }

    private GameProfile Apply(GameProfile p) => p with
    {
        Regions = [.. p.Regions.Select(r => Regions.TryGetValue(r.Name, out var b) ? r with { X = b.X, Y = b.Y, W = b.W, H = b.H } : r)],
        Events = [.. p.Events.Select(e => Apply(p.Id, e))],
    };

    private EventRule Apply(string owner, EventRule e)
    {
        Condition Set(Condition c, int i) => Thresholds.TryGetValue(new ConditionKey(owner, e.Name, i), out float v)
            ? c.Rise is not null ? c with { Rise = v } : c.Above is not null ? c with { Above = v } : c with { Below = v }
            : c;

        int n = e.When.Count;
        return e with
        {
            When = [.. e.When.Select((c, i) => Set(c, i))],
            WhenAny = [.. e.WhenAny.Select((c, i) => Set(c, n + i))],
        };
    }
}
