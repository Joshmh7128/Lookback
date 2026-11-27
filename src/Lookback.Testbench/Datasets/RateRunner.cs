using System.Diagnostics;
using System.Globalization;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Media.Ffmpeg;
using Lookback.Scoring;
using Lookback.Scoring.Ratings;

namespace Lookback.Testbench.Datasets;

/// <summary>Options for <see cref="RateRunner"/>.</summary>
internal sealed record RateOptions
{
    public required string Manifest { get; init; }

    /// <summary>Where the analysis writes session logs and reports, one sub-folder per pass.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>The datasets folder ratings go under; defaults to the folder above the manifest's.</summary>
    public string? DatasetRoot { get; init; }

    public string? ProfileOverride { get; init; }

    public IReadOnlyList<string> ExtraProfiles { get; init; } = [];

    /// <summary>Ask again about candidates that already have a rating.</summary>
    public bool Again { get; init; }

    /// <summary>Play each candidate before asking.</summary>
    public bool AutoPlay { get; init; }

    /// <summary>A trained model folder, to show its probability next to the heuristic.</summary>
    public string? ModelDirectory { get; init; }
}

/// <summary>Counts from one rating pass.</summary>
internal sealed record RateSummary(int Candidates, int Rated, int Cleared, int Skipped, int AlreadyRated, int Refused, bool Quit, string DatasetRoot);

/// <summary>Plays part of a footage file so the person rating can watch it.</summary>
internal interface IClipPlayer
{
    /// <summary>Plays <paramref name="file"/> from <paramref name="start"/> for <paramref name="length"/>; returns a reason when it cannot.</summary>
    string? Play(string file, TimeSpan start, TimeSpan length);
}

/// <summary>Plays with ffplay, which ships with ffmpeg, and waits until the window closes.</summary>
internal sealed class FfplayPlayer : IClipPlayer
{
    public string? Play(string file, TimeSpan start, TimeSpan length)
    {
        string? exe = FfmpegLocator.FindFfplay();
        if (exe is null)
        {
            return $"ffplay not found; open the file at {start:hh\\:mm\\:ss\\.f} instead";
        }

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-autoexit", "-window_title", Path.GetFileName(file),
            "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-t", length.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), file,
        })
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return "ffplay did not start";
            }

            p.WaitForExit();
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"ffplay did not start ({ex.Message})";
        }
    }
}

/// <summary>One candidate to rate, with everything needed to store its rating.</summary>
internal sealed record RateItem(string File, string SessionId, SessionStarted Session, ClipCandidate Candidate);

/// <summary>
/// <c>lookback-testbench rate &lt;manifest&gt;</c>: the terminal fallback to the
/// shell's rating UI. Analyses each entry like <c>dataset</c> does, then asks
/// for 1 to 5 stars per candidate on its content (is this a highlight?) and
/// stores the answer through <see cref="RatingStore"/>, which checks the
/// entry's retention before anything is written. Session ids are stable per
/// footage file, so a second pass skips what is already rated.
/// </summary>
internal static class RateRunner
{
    public const string Help = "stars 1-5 (5 = a highlight), 0 clears, Enter skips, p plays, q quits";

    public static async Task<RateSummary> RunAsync(RateOptions o, TextReader input, TextWriter output, IClipPlayer? player, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(o);
        DatasetManifest manifest = DatasetManifest.Load(o.Manifest);
        string manifestDir = Path.GetDirectoryName(Path.GetFullPath(o.Manifest)) ?? ".";
        string root = Path.GetFullPath(o.DatasetRoot ?? Path.GetDirectoryName(manifestDir) ?? manifestDir);
        var store = new RatingStore(root);
        using LearnedClipScorer scorer = LearnedClipScorer.LoadOrHeuristic(o.ModelDirectory, w => output.WriteLine($"warning  {w}"));

        // Session ids are stable per footage file, so each pass gets its own folder for logs and reports.
        string runDirectory = RunDirectory(o.OutputDirectory);
        var items = new List<RateItem>();
        foreach ((DatasetEntry entry, int index) in manifest.Entries.Select((e, i) => (e, i)))
        {
            string file = Path.GetFullPath(Path.Combine(manifestDir, entry.File));
            string sessionId = StableSessionId(manifest.Game, file);
            output.WriteLine($"analysing {entry.File} ...");
            AnalyzeResult r = await TestbenchRunner.AnalyzeAsync(
                new AnalyzeOptions
                {
                    File = file,
                    OutputDirectory = runDirectory,
                    Retention = entry.Retention.ToRetention(),
                    PermissionRef = entry.PermissionRef,
                    Title = entry.Title,
                    Profile = o.ProfileOverride ?? manifest.Game,
                    ExtraProfiles = o.ExtraProfiles,
                    Game = manifest.Game,
                    SessionId = sessionId,
                },
                null,
                ct).ConfigureAwait(false);

            var session = new SessionStarted
            {
                SourceKind = SourceKind.File,
                Retention = entry.Retention.ToRetention(),
                PermissionRef = entry.PermissionRef,
                Title = entry.Title ?? Path.GetFileName(file),
                QpcFrequency = TestbenchRunner.Frequency,
                GameProfile = manifest.Game,
            };
            output.WriteLine($"          {r.Candidates.Count} candidates");
            items.AddRange(r.Candidates.OrderBy(c => c.StartQpc).Select(c =>
            {
                ClipCandidate copy = c.Clone();
                scorer.Score(copy, TestbenchRunner.Frequency);
                return new RateItem(file, sessionId, session, copy);
            }));
        }

        return Prompt(items, store, input, output, player, o.Again, o.AutoPlay);
    }

    /// <summary>The question loop, apart from analysis so it can be tested without footage.</summary>
    internal static RateSummary Prompt(IReadOnlyList<RateItem> items, RatingStore store, TextReader input, TextWriter output, IClipPlayer? player, bool again, bool autoPlay)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        var rated = new HashSet<(string, string)>();
        if (!again)
        {
            foreach (string game in items.Select(i => RatingStore.GameOf(i.Candidate.GameProfile) ?? RatingStore.GameOf(i.Session.GameProfile)).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                foreach (RatingRecord r in RatingStore.Current(store.Read(game).Records))
                {
                    rated.Add((r.SessionId, r.CandidateId));
                }
            }
        }

        int done = 0, cleared = 0, skipped = 0, already = 0, refused = 0;
        output.WriteLine(Help);
        for (int i = 0; i < items.Count; i++)
        {
            RateItem item = items[i];
            ClipCandidate c = item.Candidate;
            if (rated.Contains((item.SessionId, c.CandidateId)))
            {
                already++;
                continue;
            }

            TimeSpan start = TimeSpan.FromSeconds(c.StartQpc / (double)item.Session.QpcFrequency);
            TimeSpan end = TimeSpan.FromSeconds(c.EndQpc / (double)item.Session.QpcFrequency);
            string model = c.HasModelScore ? string.Create(CultureInfo.InvariantCulture, $"  model {c.HighlightProbability:P0}") : "";
            output.WriteLine();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[{i + 1}/{items.Count}] {Path.GetFileName(item.File)}  {start:hh\\:mm\\:ss\\.f}-{end:hh\\:mm\\:ss\\.f}  {c.GameProfile}  heuristic {c.HeuristicScore:0.00}{model}"));
            if (c.Reasons.Count > 0)
            {
                output.WriteLine($"        {string.Join(", ", c.Reasons)}");
            }

            if (autoPlay && player is not null)
            {
                Report(player.Play(item.File, start, end - start), output);
            }

            while (true)
            {
                output.Write("stars> ");
                string? line = input.ReadLine();
                if (line is null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine();
                    return new RateSummary(items.Count, done, cleared, skipped, already, refused, Quit: true, store.DatasetRoot);
                }

                line = line.Trim();
                if (line.Length == 0 || line.Equals("s", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    break;
                }

                if (line.Equals("p", StringComparison.OrdinalIgnoreCase))
                {
                    Report(player?.Play(item.File, start, end - start) ?? "no player", output);
                    continue;
                }

                if (line.Length == 1 && line[0] is >= '0' and <= '5')
                {
                    uint stars = (uint)(line[0] - '0');
                    RatingWriteResult w = store.Append(item.Session, new ClipRating { SessionId = item.SessionId, CandidateId = c.CandidateId, Stars = stars }, c, "testbench");
                    if (!w.Written)
                    {
                        output.WriteLine($"refused  {w.Reason}");
                        refused++;
                    }
                    else if (stars == 0)
                    {
                        cleared++;
                    }
                    else
                    {
                        done++;
                    }

                    break;
                }

                output.WriteLine(Help);
            }
        }

        return new RateSummary(items.Count, done, cleared, skipped, already, refused, Quit: false, store.DatasetRoot);
    }

    /// <summary>The same id for the same footage on every run, so ratings line up across passes.</summary>
    internal static string StableSessionId(string game, string file) =>
        $"{game}-{TestbenchRunner.MakeSessionId(file)[16..]}";

    private static string RunDirectory(string parent)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string dir = Path.Combine(parent, stamp);
        for (int n = 2; Directory.Exists(dir); n++)
        {
            dir = Path.Combine(parent, $"{stamp}-{n}");
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Report(string? problem, TextWriter output)
    {
        if (problem is not null)
        {
            output.WriteLine($"play     {problem}");
        }
    }
}
