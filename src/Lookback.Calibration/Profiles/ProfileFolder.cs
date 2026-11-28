using Lookback.Clipper;
using Lookback.Media.Frames;
using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Signals.Audio;
using Lookback.Signals.Video;

namespace Lookback.Calibration.Profiles;

/// <summary>
/// A profiles folder on disk (the checkout's <c>profiles/</c>) loaded and
/// checked the way the recorder runs it: every file must load, the tree must
/// be valid, and every profile's clipper settings must still work once the
/// clipper's defaults fill in what the tree leaves unset.
/// </summary>
public static class ProfileFolder
{
    /// <summary>Profile files under <paramref name="directory"/>, by profile id.</summary>
    /// <exception cref="InvalidDataException">A file does not load, or two files share an id.</exception>
    public static Dictionary<string, string> Files(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string id = GameProfileLoader.Parse(File.ReadAllText(path), path).Id;
            if (!files.TryAdd(id, path))
            {
                throw new InvalidDataException($"'{id}' is defined by both {files[id]} and {path}.");
            }
        }

        return files;
    }

    /// <summary>The catalog the folder holds, checked like <see cref="Check"/>.</summary>
    /// <exception cref="InvalidDataException">Anything in it is invalid; the message says what.</exception>
    public static ProfileCatalog Load(string directory)
    {
        ProfileCatalog catalog = new(Files(directory).Values.Select(GameProfileLoader.Load));
        IReadOnlyList<string> problems = Check(catalog);
        return problems.Count == 0 ? catalog : throw new InvalidDataException("Profile catalog is invalid:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// What the catalog's own checks cannot see: each profile's clipper
    /// settings over the clipper's defaults (<c>low</c> below <c>high</c>, a
    /// window to keep), and components naming only signals its tree produces.
    /// </summary>
    public static IReadOnlyList<string> Check(ProfileCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var problems = new List<string>();
        var signals = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (GameProfile p in catalog.All.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            ClipperSpec spec = catalog.Resolve(p.Id).Clipper.Over(ClipperDefaults.Spec);
            if (spec is { High: { } high, Low: { } low } && low >= high)
            {
                problems.Add($"'{p.Id}': clipper: low ({low}) must be below high ({high}) once the clipper's defaults are applied.");
            }

            if (spec.PreMs + spec.PostMs <= 0)
            {
                problems.Add($"'{p.Id}': clipper: preMs + postMs must be positive once the clipper's defaults are applied.");
            }

            if (p.Clipper.Components.Count > 0)
            {
                string root = catalog.PathTo(p.Id)[0].Id;
                IReadOnlySet<string> known = signals.TryGetValue(root, out IReadOnlySet<string>? s) ? s : signals[root] = Signals(catalog, root);
                foreach (CurveComponent c in p.Clipper.Components.Where(c => !known.Contains(c.Signal)))
                {
                    problems.Add($"'{p.Id}': clipper: component '{c.Signal}' is not a signal Lookback produces for this game.");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Every signal the pipeline produces for a game's tree: the whole-frame
    /// video and audio signals, and the signals of the tree's HUD regions.
    /// A profile may weigh only these; it never adds a measurement.
    /// </summary>
    public static IReadOnlySet<string> Signals(ProfileCatalog catalog, string rootId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        AnalysisFormat format = AnalysisFormat.Default;
        var names = new HashSet<string>(VideoSignalBank.CreateDefault(format).Names, StringComparer.Ordinal);
        names.UnionWith(AudioSignalBank.CreateDefault(format).Names);
        IReadOnlyList<HudRegion> regions = catalog.SubtreeRegions(rootId);
        if (regions.Count > 0)
        {
            names.UnionWith(new RegionSignal(regions, format.Width, format.Height).Names);
        }

        return names;
    }
}
