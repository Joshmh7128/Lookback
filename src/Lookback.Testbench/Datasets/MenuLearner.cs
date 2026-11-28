using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lookback.Contracts.V1;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;
using Lookback.Signals.Video;
using Lookback.Stream.SessionLog;

namespace Lookback.Testbench.Datasets;

/// <summary>A learned menu state and how well it separates on the training footage.</summary>
internal sealed record LearnedMenuState(
    string Name,
    MenuState? State,
    int Frames,
    int PlayFrames,
    int OtherFrames,
    int ComparedCells,
    double Recall,
    double PlayMatchRate,
    double OtherMatchRate,
    string? Problem);

/// <summary>
/// Learns menu-state signatures (<see cref="MenuSpec"/>) from footage whose
/// dataset entries mark <c>screens</c>: stretches showing a pause menu, the
/// main menu, a loading screen, a scoreboard, and so on. The rest of each such
/// file is play. For every state:
/// <list type="number">
/// <item>Its signature is the per-channel median of the colour grid over its frames.</item>
/// <item>Cells that change a lot while it is on screen (an animated hero, the game seen
/// through a translucent menu, a timer) are left out: those whose mean deviation
/// from the median is above <see cref="MaxCellSpread"/>, keeping at least
/// <see cref="MinCells"/> of the steadiest.</item>
/// <item><c>maxDistance</c> lies in the gap between its own frames (95th percentile
/// distance) and every other frame, play or another state (1st percentile), at
/// most <see cref="MaxSlack"/> above its own frames. No gap: it is reported, and the
/// state is not written.</item>
/// </list>
/// Frames within <see cref="EdgeSeconds"/> of a label's edge (fades) are left out of both sides.
/// The result is written as the game's profile file with <c>menus</c> filled in.
/// </summary>
internal static class MenuLearner
{
    public const double MaxCellSpread = 0.05;
    public const int MinCells = 6;
    public const double MaxSlack = 0.06;
    public const double EdgeSeconds = 0.75;

    /// <summary>A screen label with this state marks footage that is neither play nor a menu to learn (a cinematic, a map card); it is left out.</summary>
    public const string Ignore = "ignore";

    private static readonly JsonSerializerOptions ProfileJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Learns every labelled state (or, with <paramref name="check"/>, measures the
    /// game's existing menu states against the labels instead, writing nothing).
    /// </summary>
    public static async Task<(IReadOnlyList<LearnedMenuState> States, string? ProfilePath)> RunAsync(string manifestPath, string outputDirectory, IReadOnlyList<string> extraProfiles, CancellationToken ct, bool check = false)
    {
        DatasetManifest manifest = DatasetManifest.Load(manifestPath);
        string root = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        ProfileCatalog catalog = extraProfiles.Count > 0 ? ProfileCatalog.WithFiles(extraProfiles.SelectMany(TestbenchRunner.ExpandProfilePath)) : ProfileCatalog.BuiltIn;
        if (!catalog.Contains(manifest.Game))
        {
            throw new InvalidDataException($"{manifestPath}: no profile '{manifest.Game}'.");
        }

        var byState = new Dictionary<string, List<float[]>>(StringComparer.Ordinal);
        var play = new List<float[]>();
        foreach ((DatasetEntry entry, int index) in manifest.Entries.Select((e, i) => (e, i)))
        {
            if (entry.Screens is null)
            {
                continue;
            }

            foreach (ScreenLabel s in entry.Screens)
            {
                if (s.End <= s.Start)
                {
                    throw new InvalidDataException($"{entry.File}: screen '{s.State}' ends before it starts.");
                }

                if (s.State == "play" || string.IsNullOrEmpty(s.State))
                {
                    throw new InvalidDataException($"{entry.File}: label screens with a menu state or '{Ignore}'; play is whatever is not labelled.");
                }
            }

            AnalyzeResult r = await TestbenchRunner.AnalyzeAsync(
                new AnalyzeOptions
                {
                    File = Path.GetFullPath(Path.Combine(root, entry.File)),
                    OutputDirectory = outputDirectory,
                    Retention = entry.Retention.ToRetention(),
                    PermissionRef = entry.PermissionRef,
                    Title = entry.Title,
                    Profile = manifest.Game,
                    ExtraProfiles = extraProfiles,
                    Game = manifest.Game,
                    SessionId = $"{manifest.Game}-menus-{index:000}",
                },
                null,
                ct).ConfigureAwait(false);

            await foreach ((double at, float[] grid) in ReadGridAsync(r.SessionBasePath!, ct).ConfigureAwait(false))
            {
                switch (Classify(entry.Screens, at))
                {
                    case null:
                        break;
                    case "":
                        play.Add(grid);
                        break;
                    case string state:
                        (byState.TryGetValue(state, out List<float[]>? list) ? list : byState[state] = []).Add(grid);
                        break;
                }
            }
        }

        var learned = new List<LearnedMenuState>();
        if (check)
        {
            MenuSpec existing = catalog.Resolve(manifest.Game).Menus;
            foreach (MenuState s in existing.States)
            {
                List<float[]> frames = byState.GetValueOrDefault(s.Name) ?? [];
                List<float[]> others = [.. byState.Where(kv => kv.Key != s.Name).SelectMany(kv => kv.Value)];
                learned.Add(Measure(s, frames, play, others));
            }

            return (learned, null);
        }

        foreach ((string state, List<float[]> frames) in byState.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            List<float[]> others = [.. byState.Where(kv => kv.Key != state).SelectMany(kv => kv.Value)];
            learned.Add(Learn(state, frames, play, others));
        }

        string? path = null;
        if (learned.Any(l => l.State is not null))
        {
            GameProfile own = catalog.Get(manifest.Game);
            var fresh = learned.Where(l => l.State is not null).Select(l => l.State!).ToList();
            var names = fresh.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            GameProfile updated = own with
            {
                Version = own.Version + 1,
                Menus = own.Menus with { States = [.. own.Menus.States.Where(s => !names.Contains(s.Name)), .. fresh] },
            };

            _ = GameProfileLoader.Parse(JsonSerializer.Serialize(updated, ProfileJson), "learned profile"); // round-trips and validates
            string dir = Path.Combine(outputDirectory, "learned");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, manifest.Game + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(updated, ProfileJson), ct).ConfigureAwait(false);
        }

        return (learned, path);
    }

    /// <summary>The state shown at <paramref name="at"/>; empty for play; null near a label's edge.</summary>
    internal static string? Classify(IReadOnlyList<ScreenLabel> screens, double at)
    {
        foreach (ScreenLabel s in screens)
        {
            if (at >= s.Start + EdgeSeconds && at <= s.End - EdgeSeconds)
            {
                return s.State == Ignore ? null : s.State;
            }

            if (at > s.Start - EdgeSeconds && at < s.End + EdgeSeconds)
            {
                return null;
            }
        }

        return "";
    }

    internal static LearnedMenuState Learn(string name, IReadOnlyList<float[]> frames, IReadOnlyList<float[]> play, IReadOnlyList<float[]> others)
    {
        if (frames.Count < 5)
        {
            return new LearnedMenuState(name, null, frames.Count, play.Count, others.Count, 0, 0, 0, 0, "fewer than 5 frames of it; label a longer stretch");
        }

        if (play.Count == 0)
        {
            return new LearnedMenuState(name, null, frames.Count, 0, others.Count, 0, 0, 0, 0, "no play frames to compare with; leave some play in the labelled files");
        }

        // The signature: the per-channel median, robust to a few mislabelled frames.
        float[] reference = new float[ColorGridSignal.Length];
        float[] column = new float[frames.Count];
        for (int k = 0; k < reference.Length; k++)
        {
            for (int f = 0; f < frames.Count; f++)
            {
                column[f] = frames[f][k];
            }

            Array.Sort(column);
            reference[k] = column[column.Length / 2];
        }

        // Steady cells only.
        double[] spread = new double[ColorGridSignal.Cells];
        for (int c = 0; c < spread.Length; c++)
        {
            int s = c * ColorGridSignal.Channels;
            spread[c] = frames.Average(f => (Math.Abs(f[s] - reference[s]) + Math.Abs(f[s + 1] - reference[s + 1]) + Math.Abs(f[s + 2] - reference[s + 2])) / 3.0);
        }

        bool[] compared = new bool[ColorGridSignal.Cells];
        int[] steadiest = [.. Enumerable.Range(0, spread.Length).OrderBy(c => spread[c]).ThenBy(c => c)];
        for (int i = 0; i < steadiest.Length; i++)
        {
            compared[steadiest[i]] = i < MinCells || spread[steadiest[i]] <= MaxCellSpread;
        }

        // Snap to what the profile stores (#rrggbb), so the distances below are the ones the detector will see.
        IReadOnlyList<string?> colors = MenuSignature.Format(reference, compared);
        MenuSignature.TryParse(colors, reference, compared, out _);

        float[] own = Distances(frames, reference, compared);
        float[] rest = Distances([.. play, .. others], reference, compared);
        double a = Percentile(own, 0.95), b = Percentile(rest, 0.01);
        int cells = compared.Count(x => x);
        if (a >= b)
        {
            float atOwn = (float)a;
            return new LearnedMenuState(name, null, frames.Count, play.Count, others.Count, cells, 0.95,
                Rate(Distances(play, reference, compared), atOwn), Rate(Distances(others, reference, compared), atOwn),
                string.Create(CultureInfo.InvariantCulture, $"its frames are no closer to its signature (95% within {a:0.000}) than other frames are (1% within {b:0.000}); it cannot be told apart by colour"));
        }

        float threshold = (float)Math.Round(Math.Min((a + b) / 2, a + MaxSlack), 3);
        var state = new MenuState { Name = name, Colors = colors, MaxDistance = threshold };
        return new LearnedMenuState(name, state, frames.Count, play.Count, others.Count, cells,
            Rate(own, threshold), Rate(Distances(play, reference, compared), threshold), Rate(Distances(others, reference, compared), threshold), null);
    }

    /// <summary>How an existing state does on labelled frames: share of its own frames, of play and of other states that it matches.</summary>
    internal static LearnedMenuState Measure(MenuState state, IReadOnlyList<float[]> frames, IReadOnlyList<float[]> play, IReadOnlyList<float[]> others)
    {
        float[] reference = new float[ColorGridSignal.Length];
        bool[] compared = new bool[ColorGridSignal.Cells];
        MenuSignature.TryParse(state.Colors, reference, compared, out _);
        return new LearnedMenuState(state.Name, state, frames.Count, play.Count, others.Count, compared.Count(x => x),
            Rate(Distances(frames, reference, compared), state.MaxDistance),
            Rate(Distances(play, reference, compared), state.MaxDistance),
            Rate(Distances(others, reference, compared), state.MaxDistance),
            frames.Count == 0 ? "no labelled frames of it in this footage" : null);
    }

    public static string Describe(LearnedMenuState s) => string.Create(CultureInfo.InvariantCulture,
        $"{s.Name,-14} {s.Frames,6} frames, {s.ComparedCells,2}/{ColorGridSignal.Cells} cells, maxDistance {s.State?.MaxDistance ?? 0:0.000}: matches {s.Recall:P0} of its frames, {s.PlayMatchRate:P1} of {s.PlayFrames} play frames, {s.OtherMatchRate:P1} of {s.OtherFrames} other-state frames");

    private static async IAsyncEnumerable<(double At, float[] Grid)> ReadGridAsync(string sessionBasePath, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using SessionLogReader reader = await SessionLogReader.OpenAsync(sessionBasePath, ct).ConfigureAwait(false);
        long frequency = reader.Header.SessionStarted.QpcFrequency > 0 ? (long)reader.Header.SessionStarted.QpcFrequency : 10_000_000;
        var grids = new ColorGridReader();
        await foreach (Envelope e in reader.ReadAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            if (e.PayloadCase == Envelope.PayloadOneofCase.SignalSchema)
            {
                grids.OnSchema(e.SignalSchema);
            }
            else if (e.PayloadCase == Envelope.PayloadOneofCase.SignalFrame)
            {
                float[] grid = new float[ColorGridSignal.Length];
                if (grids.TryRead(e.SignalFrame, grid))
                {
                    yield return (e.SignalFrame.WindowStartQpc / (double)frequency, grid);
                }
            }
        }
    }

    private static float[] Distances(IReadOnlyList<float[]> frames, float[] reference, bool[] compared) =>
        [.. frames.Select(f => MenuSignature.Distance(f, reference, compared))];

    private static double Percentile(float[] values, double p)
    {
        if (values.Length == 0)
        {
            return double.PositiveInfinity;
        }

        float[] sorted = [.. values.Order()];
        return sorted[(int)Math.Clamp(Math.Floor(p * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }

    private static double Rate(float[] distances, float threshold) =>
        distances.Length == 0 ? 0 : distances.Count(d => d <= threshold) / (double)distances.Length;
}
