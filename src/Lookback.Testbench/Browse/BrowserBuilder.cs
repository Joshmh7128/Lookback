using System.Globalization;
using System.Text.Json;
using Lookback.Media.Frames;
using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Signals.Audio;
using Lookback.Signals.Video;
using Lookback.Testbench.Datasets;

namespace Lookback.Testbench.Browse;

/// <summary>
/// Gathers the profile tree, every dataset manifest and any dataset results
/// into <see cref="BrowserData"/>. Reads only: profiles, manifests and
/// <c>scores.json</c> files. Media is checked for presence, never opened.
/// </summary>
internal static class BrowserBuilder
{
    private static readonly IReadOnlyDictionary<string, string> Meanings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["luma.mean"] = "Mean brightness of the frame (BT.709 luma, 0 to 1)",
        ["luma.std"] = "Contrast: standard deviation of brightness",
        ["luma.black"] = "1 when the frame is black (loading screens, cuts to black)",
        ["motion.energy"] = "How much the picture changed since the previous frame",
        ["motion.coverage"] = "Share of the picture that changed noticeably",
        ["motion.frozen_s"] = "Seconds the picture has not changed",
        ["flash.brighten"] = "Brightness above its recent baseline: explosions, ability effects",
        ["flash.red_edge"] = "Red at the screen edges compared with the centre: damage vignettes",
        ["scene.cut"] = "How completely the picture changed at once: cuts, deaths, respawns, menus",
        ["hud.ready"] = "1 once the static HUD has been learned",
        ["hud.coverage"] = "Share of the frame that is static HUD",
        ["hud.activity"] = "Change inside the learned HUD",
        ["audio.rms_db"] = "Loudness (RMS, dBFS)",
        ["audio.peak_db"] = "Peak level (dBFS)",
        ["audio.clipped"] = "Share of samples at full scale",
        ["audio.silent"] = "1 while the audio is silent",
        ["spectral.flux"] = "How fast the sound's spectrum is changing",
        ["spectral.onset"] = "A sharp new sound: gunshots, impacts, hits",
        ["spectral.centroid_hz"] = "Brightness of the sound (spectral centroid, Hz)",
    };

    private static readonly IReadOnlyDictionary<string, string> RegionMeanings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["luma"] = "Brightness inside the region",
        ["activity"] = "Change inside the region since the previous frame",
        ["saturation"] = "Colour saturation inside the region",
        ["red"] = "Redness inside the region",
    };

    public static BrowserData Build(ProfileCatalog catalog, string? datasetsDirectory, IReadOnlyList<string> resultDirectories, string outputFile)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resultDirectories);
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outputFile)) ?? ".";
        var notes = new List<string>();

        IReadOnlyList<SignalInfo> signals = KnownSignals(catalog);
        var known = signals.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        // Results first, so datasets can attach them.
        var results = new Dictionary<string, (ResultsView View, Dictionary<string, EntryResultView> Entries)>(StringComparer.Ordinal);
        foreach (string dir in resultDirectories)
        {
            string file = Directory.Exists(dir) ? Path.Combine(dir, "scores.json") : dir;
            if (!File.Exists(file))
            {
                notes.Add($"No scores.json at {dir}; run `lookback-testbench dataset <manifest> --out {dir}` first.");
                continue;
            }

            (string game, ResultsView view, Dictionary<string, EntryResultView> entries) = ReadResults(file, outDir);
            results[game] = (view, entries);
        }

        var datasets = new List<DatasetView>();
        var entriesByGame = new List<(string Game, EntryView Entry)>();
        if (datasetsDirectory is not null && Directory.Exists(datasetsDirectory))
        {
            foreach (string manifest in Directory.EnumerateFiles(datasetsDirectory, "manifest*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                DatasetView d = ReadDataset(manifest, catalog, results);
                datasets.Add(d);
                entriesByGame.AddRange(d.Entries.Select(e => (d.Game, e)));
            }
        }
        else
        {
            notes.Add("No datasets folder found; pass --datasets <dir>.");
        }

        var profiles = Ordered(catalog).Select(p => BuildProfile(catalog, p.Id, known, entriesByGame)).ToList();
        IReadOnlyList<string> games = [.. catalog.All.Where(p => string.IsNullOrEmpty(p.Parent)).Select(p => p.Id).Order(StringComparer.Ordinal)];
        return new BrowserData(games, profiles, datasets, signals, notes);
    }

    /// <summary>The nearest <c>datasets</c> folder at or above <paramref name="start"/>, as in a repository checkout.</summary>
    public static string? FindDatasets(string start)
    {
        for (DirectoryInfo? d = new(start); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "datasets");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<GameProfile> Ordered(ProfileCatalog catalog) =>
        catalog.All.Where(p => string.IsNullOrEmpty(p.Parent)).OrderBy(p => p.Id, StringComparer.Ordinal).SelectMany(g => catalog.Subtree(g.Id));

    internal static IReadOnlyList<SignalInfo> KnownSignals(ProfileCatalog catalog)
    {
        AnalysisFormat format = AnalysisFormat.Default;
        var list = new List<SignalInfo>();
        foreach (string n in VideoSignalBank.CreateDefault(format).Names)
        {
            list.Add(new SignalInfo(n, "video", n.StartsWith("color.", StringComparison.Ordinal)
                ? "Mean red, green or blue of one cell of the 6x4 colour grid (menu states match it)"
                : Meanings.GetValueOrDefault(n, "")));
        }

        foreach (string n in AudioSignalBank.CreateDefault(format).Names)
        {
            list.Add(new SignalInfo(n, "audio", n.StartsWith("mel.", StringComparison.Ordinal) ? "Energy in one mel band" : Meanings.GetValueOrDefault(n, "")));
        }

        var seen = list.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (GameProfile root in catalog.All.Where(p => string.IsNullOrEmpty(p.Parent)))
        {
            IReadOnlyList<HudRegion> regions = catalog.SubtreeRegions(root.Id);
            if (regions.Count == 0)
            {
                continue;
            }

            foreach (string n in new RegionSignal(regions, format.Width, format.Height).Names)
            {
                if (seen.Add(n))
                {
                    string region = RegionOf(n)!;
                    string measure = n[(n.LastIndexOf('.') + 1)..];
                    list.Add(new SignalInfo(n, "region", $"{RegionMeanings.GetValueOrDefault(measure, measure)} '{region}'"));
                }
            }
        }

        return list;
    }

    private static string? RegionOf(string signal)
    {
        if (!signal.StartsWith("region.", StringComparison.Ordinal))
        {
            return null;
        }

        int last = signal.LastIndexOf('.');
        return last > 7 ? signal[7..last] : null;
    }

    private static ProfileView BuildProfile(ProfileCatalog catalog, string id, HashSet<string> known, List<(string Game, EntryView Entry)> entries)
    {
        GameProfile own = catalog.Get(id);
        GameProfile r = catalog.Resolve(id);
        IReadOnlyList<GameProfile> path = catalog.PathTo(id);

        string DefinedBy(Func<GameProfile, bool> has)
        {
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (has(path[i]))
                {
                    return path[i].Id;
                }
            }

            return id;
        }

        string? Replaced(string name, string from)
        {
            int at = path.ToList().FindIndex(p => p.Id == from);
            for (int i = at - 1; i >= 0; i--)
            {
                if (path[i].Events.Any(e => e.Name == name) || path[i].Composites.Any(c => c.Name == name))
                {
                    return path[i].Id;
                }
            }

            return null;
        }

        // Effective weight: an event's own weight plus the composites built on it.
        double Effective(EventRule e) => e.Weight + r.Composites.Where(c => c.Of == e.Name).Sum(c => c.Weight);

        var events = r.Events
            .Select(e =>
            {
                string from = DefinedBy(p => p.Events.Any(x => x.Name == e.Name));
                return new EventView(
                    e.Name,
                    R(e.Weight),
                    R(e.BaseConfidence),
                    R(Math.Min(1, e.BaseConfidence + e.Boosts.Sum(b => b.Add))),
                    e.CooldownMs,
                    [.. e.When.Select(Cond)],
                    [.. e.WhenAny.Select(Cond)],
                    [.. e.Boosts.Select(b => new BoostView(b.Signal, R(b.Above), b.WithinMs, R(b.Add),
                        Inv($"+{b.Add:0.##} confidence if {b.Signal} is above {b.Above:0.###} within {b.WithinMs} ms")))],
                    e.Clip.PreMs,
                    e.Clip.PostMs,
                    from,
                    Replaced(e.Name, from),
                    EventSummary(e),
                    [.. r.Composites.Where(c => c.Of == e.Name).Select(c => c.Name)]);
            })
            .OrderByDescending(e => Effective(r.Events.First(x => x.Name == e.Name)))
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        var composites = r.Composites
            .Select(c => new CompositeView(c.Name, c.Of, c.AtLeast, c.WithinMs, R(c.Weight), c.Clip.PreMs, c.Clip.PostMs,
                DefinedBy(p => p.Composites.Any(x => x.Name == c.Name)),
                Inv($"{c.AtLeast} × {c.Of} within {c.WithinMs / 1000.0:0.#} s")))
            .OrderByDescending(c => c.Weight)
            .ToList();

        // Components: every signal the profile reads, and how much rides on it.
        var triggers = new Dictionary<string, List<EventRule>>(StringComparer.Ordinal);
        var boosts = new Dictionary<string, List<EventRule>>(StringComparer.Ordinal);
        foreach (EventRule e in r.Events)
        {
            foreach (Condition c in e.When.Concat(e.WhenAny))
            {
                Add(triggers, c.Signal, e);
            }

            foreach (Boost b in e.Boosts)
            {
                Add(boosts, b.Signal, e);
            }
        }

        var fingerprint = own.Detect.Visual.Select(c => c.Signal).ToHashSet(StringComparer.Ordinal);
        var components = triggers.Keys.Concat(boosts.Keys).Concat(fingerprint).Distinct(StringComparer.Ordinal)
            .Select(s =>
            {
                List<EventRule> t = triggers.GetValueOrDefault(s) ?? [];
                List<EventRule> b = boosts.GetValueOrDefault(s) ?? [];
                double influence = t.Distinct().Sum(Effective) + (0.5 * b.Distinct().Sum(Effective)) + (fingerprint.Contains(s) ? 1 : 0);
                string? region = RegionOf(s);
                string kind = region is not null ? "region" : s.StartsWith("audio.", StringComparison.Ordinal) || s.StartsWith("spectral.", StringComparison.Ordinal) || s.StartsWith("mel.", StringComparison.Ordinal) ? "audio" : "video";
                return new ComponentView(s, kind, region, [.. t.Select(e => e.Name).Distinct()], [.. b.Select(e => e.Name).Distinct()], fingerprint.Contains(s), Math.Round(influence, 3), known.Contains(s));
            })
            .OrderByDescending(c => c.Influence)
            .ThenBy(c => c.Signal, StringComparer.Ordinal)
            .ToList();

        var regions = r.Regions.Select(g =>
        {
            string prefix = $"region.{g.Name}.";
            var users = components.Where(c => c.Signal.StartsWith(prefix, StringComparison.Ordinal))
                .SelectMany(c => c.Triggers.Concat(c.Boosts).Concat(c.Fingerprint ? ["fingerprint"] : Array.Empty<string>()))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return new RegionView(g.Name, g.X, g.Y, g.W, g.H, g.Purpose, DefinedBy(p => p.Regions.Any(x => x.Name == g.Name)), users);
        }).ToList();

        var warnings = new List<string>();
        foreach (ComponentView c in components.Where(c => !c.Known))
        {
            warnings.Add($"'{c.Signal}' is not a signal the pipeline produces, so rules using it never fire.");
        }

        if (!r.Reference.Calibrated && r.Regions.Count > 0)
        {
            warnings.Add("Regions are estimates until calibrated against real footage (the calibration view in `analyze --profile`).");
        }

        if (own.Level != ProfileLevelKind.Game && own.Detect.Visual.Count == 0)
        {
            warnings.Add(own.Detect.Hints.Count > 0
                ? "No visual fingerprint yet: detected from hints during live capture only; footage needs --profile or a learned fingerprint."
                : "Not detectable yet: no hints and no fingerprint. Choose it with --profile, or learn a fingerprint with `fingerprint`.");
        }

        string[] unused = [.. regions.Where(g => g.UsedBy.Count == 0).Select(g => g.Name)];
        if (unused.Length > 0)
        {
            warnings.Add($"Measured but not used by any rule or fingerprint yet: region{(unused.Length > 1 ? "s" : "")} {string.Join(", ", unused)}.");
        }

        return new ProfileView
        {
            Id = id,
            DisplayName = own.DisplayName,
            Level = own.Level.ToString().ToLowerInvariant(),
            Parent = string.IsNullOrEmpty(own.Parent) ? null : own.Parent,
            Path = [.. path.Select(p => p.Id)],
            Children = [.. catalog.Children(id).Select(c => c.Id)],
            Version = own.Version,
            Executables = r.Executables,
            Capture = new CaptureView(r.CapturePolicy.AllowPresentHook, r.CapturePolicy.AllowMemoryReading, r.CapturePolicy.Notes),
            Reference = new ReferenceView(r.Reference.Aspect, r.Reference.Calibrated, r.Reference.Notes),
            Detect = Detect(own),
            Regions = regions,
            Events = events,
            Composites = composites,
            Disabled = [.. path.SelectMany(p => p.Disable).Distinct(StringComparer.Ordinal)],
            Components = components,
            Coverage = Coverage(catalog, id, r, entries),
            Warnings = warnings,
        };
    }

    private static void Add(Dictionary<string, List<EventRule>> map, string key, EventRule e)
    {
        if (!map.TryGetValue(key, out List<EventRule>? list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(e);
    }

    private static DetectView Detect(GameProfile p)
    {
        DetectSpec d = p.Detect;
        string summary = p.Level == ProfileLevelKind.Game
            ? (p.Executables.Count > 0 ? $"Recognised by its executable: {string.Join(", ", p.Executables)}." : "Chosen by hand; no executable.")
            : string.Join(" ", new[]
            {
                d.Hints.Count > 0 ? $"{d.Hints.Count} exact hint(s) decide it during live capture." : "",
                d.Visual.Count > 0
                    ? Inv($"On screen when at least {d.MinScore:P0} of {d.Visual.Count} fingerprint condition(s) hold on average over {d.WindowSeconds:0.#} s, held for {d.HoldSeconds:0.#} s.")
                    : "No visual fingerprint yet.",
            }.Where(s => s.Length > 0));
        return new DetectView(
            [.. d.Hints.Select(h => new HintView(JsonNamingPolicy.CamelCase.ConvertName(h.Source.ToString()), h.Regex, h.Values))],
            [.. d.Visual.Select(Cond)],
            d.WindowSeconds,
            d.MinScore,
            d.HoldSeconds,
            summary);
    }

    internal static ConditionView Cond(Condition c) => c switch
    {
        { Above: { } a } => new ConditionView(c.Signal, "above", R(a), Inv($"{c.Signal} is above {a:0.###}")),
        { Below: { } b } => new ConditionView(c.Signal, "below", R(b), Inv($"{c.Signal} is below {b:0.###}")),
        { Rise: { } x } => new ConditionView(c.Signal, "rise", R(x), Inv($"{c.Signal} rises {x:0.###} over its own recent baseline")),
        _ => new ConditionView(c.Signal, "?", 0, c.Signal),
    };

    internal static string EventSummary(EventRule e)
    {
        var parts = new List<string>();
        if (e.When.Count > 0)
        {
            parts.Add(string.Join(" and ", e.When.Select(c => Cond(c).Text)));
        }

        if (e.WhenAny.Count > 0)
        {
            parts.Add(e.WhenAny.Count == 1 ? Cond(e.WhenAny[0]).Text : "any of: " + string.Join("; ", e.WhenAny.Select(c => Cond(c).Text)));
        }

        string when = parts.Count == 0 ? "never (no conditions)" : string.Join(", and ", parts);
        return Inv($"Fires when {when}. At most once every {e.CooldownMs} ms.");
    }

    private static CoverageView Coverage(ProfileCatalog catalog, string id, GameProfile resolved, List<(string Game, EntryView Entry)> entries)
    {
        var subtree = catalog.Subtree(id).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var names = resolved.Events.Select(e => e.Name).Concat(resolved.Composites.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
        var mine = entries.Where(x => subtree.Contains(x.Entry.Profile ?? x.Game)).Select(x => x.Entry).ToList();

        var labels = mine.SelectMany(e => e.Labels).Where(l => names.Contains(l.Event))
            .GroupBy(l => l.Event, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var unknown = entries.Where(x => (x.Entry.Profile ?? x.Game) == id).SelectMany(x => x.Entry.Labels)
            .Select(l => l.Event).Where(n => !names.Contains(n)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var scores = mine.Where(e => e.Result is not null).SelectMany(e => e.Result!.Scores).Where(s => names.Contains(s.Event))
            .GroupBy(s => s.Event, StringComparer.Ordinal)
            .Select(g => new ScoreView(g.Key, g.Sum(s => s.Tp), g.Sum(s => s.Fp), g.Sum(s => s.Fn)))
            .OrderBy(s => s.Event, StringComparer.Ordinal)
            .ToList();
        return new CoverageView(mine.Count, labels, [.. names.Where(n => !labels.ContainsKey(n)).Order(StringComparer.Ordinal)], unknown, scores);
    }

    private static DatasetView ReadDataset(string manifestPath, ProfileCatalog catalog, Dictionary<string, (ResultsView View, Dictionary<string, EntryResultView> Entries)> results)
    {
        string display = Display(manifestPath);
        bool example = Path.GetFileName(manifestPath).Contains(".example", StringComparison.OrdinalIgnoreCase);
        DatasetManifest m;
        try
        {
            m = DatasetManifest.Load(manifestPath);
        }
        catch (InvalidDataException ex)
        {
            string game = Path.GetFileName(Path.GetDirectoryName(manifestPath)) ?? "?";
            return new DatasetView(game, display, example, [], null, [ex.Message]);
        }

        var problems = new List<string>();
        if (!catalog.Contains(m.Game))
        {
            problems.Add($"Game '{m.Game}' has no profile.");
        }

        string root = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        (ResultsView View, Dictionary<string, EntryResultView> Entries)? res = !example && results.TryGetValue(m.Game, out var found) ? found : null;
        var entries = m.Entries.Select((e, i) =>
        {
            var issues = new List<string>();
            string effective = e.Profile ?? m.Game;
            if (!catalog.Contains(effective))
            {
                issues.Add($"Profile '{effective}' does not exist.");
            }
            else
            {
                if (catalog.PathTo(effective)[0].Id != m.Game && catalog.Contains(m.Game))
                {
                    issues.Add($"Profile '{effective}' is not in the '{m.Game}' tree.");
                }

                GameProfile r = catalog.Resolve(effective);
                var names = r.Events.Select(x => x.Name).Concat(r.Composites.Select(x => x.Name)).ToHashSet(StringComparer.Ordinal);
                foreach (string missing in e.Labels.Select(l => l.Event).Where(n => !names.Contains(n)).Distinct(StringComparer.Ordinal))
                {
                    issues.Add($"Label '{missing}' is not an event of {effective}; it can never be matched.");
                }
            }

            if (e.Retention != DatasetRetention.Owned && string.IsNullOrWhiteSpace(e.PermissionRef))
            {
                issues.Add("Third-party footage without a permissionRef: the dataset run will refuse it.");
            }

            if (e.Clips.Any(c => c.End <= c.Start))
            {
                issues.Add("A labelled clip ends before it starts.");
            }

            if (e.Labels.Any(l => l.At < 0))
            {
                issues.Add("A label has a negative time.");
            }

            bool media = File.Exists(Path.Combine(root, e.File));
            return new EntryView
            {
                Index = i,
                File = e.File,
                Title = e.Title,
                Source = e.Source,
                Retention = e.Retention switch { DatasetRetention.OpenLicense => "open-license", var x => x.ToString().ToLowerInvariant() },
                PermissionRef = e.PermissionRef,
                Profile = e.Profile,
                Labels = [.. e.Labels.OrderBy(l => l.At).Select(l => new LabelView(l.Event, l.At))],
                Clips = [.. e.Clips.Select(c => new ClipView(c.Start, c.End))],
                MediaFound = media,
                Result = res?.Entries.GetValueOrDefault(e.File),
                Issues = issues,
            };
        }).ToList();

        return new DatasetView(m.Game, display, example, entries, res?.View, problems);
    }

    private static (string Game, ResultsView View, Dictionary<string, EntryResultView> Entries) ReadResults(string file, string outDir)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement root = doc.RootElement;
        string game = root.GetProperty("game").GetString() ?? "";
        var entries = new Dictionary<string, EntryResultView>(StringComparer.Ordinal);
        foreach (JsonElement e in root.GetProperty("entries").EnumerateArray())
        {
            string? report = Str(e, "report");
            entries[Str(e, "file") ?? ""] = new EntryResultView(
                e.TryGetProperty("events", out JsonElement ev) ? ev.GetInt32() : 0,
                report is null ? null : Href(outDir, ReportPath(file, report)),
                Str(e, "finalProfile"),
                Num(e, "detectionAccuracy"),
                Scores(e, "scores"),
                Cand(e));
        }

        var view = new ResultsView(Display(file), Num(root, "toleranceSeconds") ?? 1, Scores(root, "overall"), Num(root, "detectionAccuracy"), Cand(root));
        return (game, view, entries);
    }

    /// <summary>
    /// Reports are written next to scores.json. The run records the path it was
    /// given, which may be relative to wherever it ran, so prefer the copy beside
    /// the scores file unless the recorded path is absolute and still there.
    /// </summary>
    private static string ReportPath(string scoresFile, string recorded) =>
        Path.IsPathRooted(recorded) && File.Exists(recorded)
            ? recorded
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scoresFile)) ?? ".", Path.GetFileName(recorded));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static IReadOnlyList<ScoreView> Scores(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(s => new ScoreView(Str(s, "event") ?? "", (int)(Num(s, "truePositives") ?? 0), (int)(Num(s, "falsePositives") ?? 0), (int)(Num(s, "falseNegatives") ?? 0)))]
            : [];

    private static CandidateView? Cand(JsonElement e) =>
        e.TryGetProperty("candidates", out JsonElement c) && c.ValueKind == JsonValueKind.Object
            ? new CandidateView((int)(Num(c, "truePositives") ?? 0), (int)(Num(c, "falsePositives") ?? 0), (int)(Num(c, "falseNegatives") ?? 0), Num(c, "meanIoU") ?? 0)
            : null;

    private static string Display(string path) => Path.GetRelativePath(Environment.CurrentDirectory, Path.GetFullPath(path)).Replace('\\', '/');

    private static string Href(string fromDirectory, string file) => TestbenchRunner.RelativeHref(fromDirectory, Path.GetFullPath(file));

    /// <summary>Profile numbers are stored as float; show 0.15, not 0.150000006.</summary>
    private static double R(double v) => Math.Round(v, 4);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
