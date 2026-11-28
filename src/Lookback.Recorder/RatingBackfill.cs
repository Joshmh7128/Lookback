using System.Globalization;
using System.Text.Json.Nodes;
using Lookback.Clipper;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Ffmpeg;
using Lookback.Profiles.Model;
using Lookback.Scoring.Features;
using Lookback.Scoring.Ratings;
using Microsoft.Extensions.Logging;

namespace Lookback.Recorder;

/// <summary>
/// Fills in the features of ratings stored before the clipper computed them
/// (a line with only the heuristic score, window length and peak position).
/// It re-analyses the footage the ratings came from, on a private bus that
/// publishes nothing, and asks the clipper to describe each rated window
/// exactly (<see cref="ClipperOptions.Probes"/>); the result is appended as the
/// rating's new line through <see cref="RatingStore.AppendBackfill"/>, which
/// applies <c>RetentionRules</c> again. The stars are never touched, so nothing
/// needs re-rating.
/// <list type="bullet">
/// <item>A live recording is read back from the store (the footage must still
/// be there) into the clip cache, analysed, and that file deleted.</item>
/// <item>A footage file is chosen by the user. When its ratings carry the
/// file's fingerprint (<see cref="FootageDigest"/>), the file must match it;
/// older ratings have none, so the file must at least be long enough to
/// cover every rated window.</item>
/// </list>
/// One job runs at a time; the shell polls <see cref="Plan"/> for its progress.
/// </summary>
internal sealed partial class RatingBackfill(RatingStore ratings, ILogger logger, CaptureService? capture = null, ProfileSource? profiles = null) : IAsyncDisposable
{
    /// <summary>A rating with fewer features than this is missing what the clipper now computes.</summary>
    public const int MinFeatures = 10;

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private Task _running = Task.CompletedTask;

    /// <summary>True when the rating lacks the clipper's features and was not backfilled already.</summary>
    public static bool NeedsFeatures(RatingRecord r) =>
        r.BackfilledAt is null && r.Source != RatingStore.CheckSource && r.Features.Count(f => f is not null) < MinFeatures;

    /// <summary>Every rated session with ratings that need features, with where its footage is and how a job on it went.</summary>
    public JsonObject Plan()
    {
        var sessions = new JsonArray();
        foreach (IGrouping<string, RatingRecord> s in Pending().GroupBy(r => r.SessionId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            RatingRecord first = s.First();
            bool live = first.SourceKind == "live-game";
            JsonObject row = new()
            {
                ["sessionId"] = s.Key,
                ["game"] = first.Game,
                ["sourceKind"] = first.SourceKind,
                ["ratings"] = s.Count(),
                ["ratedUtc"] = s.Min(r => r.RatedAt),
                ["windowEndSeconds"] = s.Max(r => r.QpcFrequency > 0 ? r.EndQpc / (double)r.QpcFrequency : 0),
                ["digest"] = s.Select(r => r.SourceDigest).FirstOrDefault(d => d is not null),
                ["footage"] = live ? (capture?.StoredFootage(s.Key) is { } f ? new JsonObject { ["bytes"] = f.Bytes, ["sweepUtc"] = f.SweepUtc } : null) : null,
            };
            lock (_lock)
            {
                if (_jobs.TryGetValue(s.Key, out Job? job))
                {
                    row["job"] = job.ToJson();
                }
            }

            sessions.Add(row);
        }

        // Finished jobs whose ratings no longer need anything still report how they went.
        lock (_lock)
        {
            foreach (Job job in _jobs.Values.Where(j => !sessions.Any(s => (string?)s!["sessionId"] == j.SessionId)))
            {
                sessions.Add(new JsonObject { ["sessionId"] = job.SessionId, ["game"] = job.Game, ["ratings"] = 0, ["job"] = job.ToJson() });
            }
        }

        return new JsonObject { ["sessions"] = sessions, ["busy"] = !_running.IsCompleted };
    }

    /// <summary>Starts backfilling one session; returns null, or why it cannot start.</summary>
    /// <param name="sessionId">A session from <see cref="Plan"/>.</param>
    /// <param name="path">The footage file, for a footage session; ignored for a live recording.</param>
    public string? Start(string sessionId, string? path)
    {
        List<RatingRecord> rows = [.. Pending().Where(r => r.SessionId == sessionId)];
        if (rows.Count == 0)
        {
            return $"No ratings of '{sessionId}' need features.";
        }

        bool live = rows[0].SourceKind == "live-game";
        if (!live && (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path)))
        {
            return "Choose the footage file these ratings came from.";
        }

        if (live && capture is null)
        {
            return "Live recordings can be re-analysed only while the recorder records (its store is closed).";
        }

        if (!_one.Wait(0))
        {
            return "Another backfill is running; wait for it to finish.";
        }

        var job = new Job(sessionId, rows[0].Game);
        lock (_lock)
        {
            _jobs[sessionId] = job;
        }

        _running = Task.Run(async () =>
        {
            try
            {
                await RunAsync(job, rows, live ? null : path, _stop.Token).ConfigureAwait(false);
            }
            finally
            {
                _one.Release();
            }
        });
        return null;
    }

    private IEnumerable<RatingRecord> Pending() =>
        ratings.Games().SelectMany(g => RatingStore.Current(ratings.Read(g).Records)).Where(NeedsFeatures);

    private async Task RunAsync(Job job, List<RatingRecord> rows, string? path, CancellationToken ct)
    {
        string? export = null;
        try
        {
            long offset = 0; // the file's time 0, in the ratings' ticks
            string? digest = null;
            if (path is null)
            {
                job.Set("reading the recording");
                (string? p, long start, string? error) = await capture!.ExportSessionAsync(job.SessionId).ConfigureAwait(false);
                if (p is null)
                {
                    job.Fail(error ?? "The recording could not be read.");
                    return;
                }

                export = path = p;
                offset = start;
            }
            else
            {
                job.Set("checking the file");
                digest = FootageDigest.Of(path);
                string? expected = rows.Select(r => r.SourceDigest).FirstOrDefault(d => d is not null);
                if (expected is not null && expected != digest)
                {
                    job.Fail("That file is not the footage these ratings came from (its fingerprint differs).");
                    return;
                }
            }

            MediaInfo media = await MediaProbe.ProbeAsync(path, ct).ConfigureAwait(false);
            long freq = (long)rows[0].QpcFrequency;
            if (freq <= 0 || rows.Any(r => (long)r.QpcFrequency != freq))
            {
                job.Fail("The ratings have no common time base to map into the footage.");
                return;
            }

            double needed = rows.Max(r => (long)r.EndQpc - offset) / (double)freq;
            if (media.Duration.TotalSeconds + 1 < needed)
            {
                job.Fail(string.Create(CultureInfo.InvariantCulture, $"The footage is {media.Duration.TotalSeconds:0} s long, but the ratings reach {needed:0} s: it is not their footage."));
                return;
            }

            // Each rated window, in the analysis's ticks from the file's start.
            long rate = FootageSessions.Frequency;
            long Map(ulong qpc) => Math.Max(0, QpcTime.Rescale((long)qpc - offset, freq, rate));
            var byId = rows.ToDictionary(r => r.CandidateId, StringComparer.Ordinal);
            ProbeWindow[] probes = [.. rows
                .Select(r => new ProbeWindow(r.CandidateId, Map(r.StartQpc), Map(r.EndQpc), Map(r.PeakQpc)))
                .Where(p => p.End > p.Start)];
            var described = new List<(RatingRecord Row, float[] Features)>();
            var clipping = new ClipperOptions
            {
                Probes = probes,
                Probed = (w, o) =>
                {
                    float[] features = ClipperFeatureMap.FromClipper(o.Features);
                    if (!media.HasAudio)
                    {
                        foreach (string a in ClipFeatureSchema.AudioDerived)
                        {
                            features[ClipFeatureSchema.IndexOf(a)] = float.NaN;
                        }
                    }

                    lock (described)
                    {
                        described.Add((byId[w.Id], features));
                    }
                },
            };

            ProfileCatalog catalog = profiles?.Current() ?? ProfileCatalog.BuiltIn;
            string? startId = StartProfile(catalog, rows);
            job.Set(string.Create(CultureInfo.InvariantCulture, $"analysing {media.Duration.TotalSeconds:0} s of footage"));
            using var privateBus = new InProcessEventBus(); // nothing leaves it: no candidates, no frames
            await FootageSessions.AnalyseAsync(privateBus, path, media, catalog, startId, "backfill-" + job.SessionId, clipping, ct).ConfigureAwait(false);

            int written = 0;
            var refused = new List<string>();
            foreach ((RatingRecord row, float[] features) in described)
            {
                RatingWriteResult r = ratings.AppendBackfill(row, features, digest);
                if (r.Written)
                {
                    written++;
                }
                else
                {
                    refused.Add($"{row.CandidateId}: {r.Reason}");
                }
            }

            job.Finish(written, rows.Count, refused);
            LogDone(logger, job.SessionId, written, rows.Count);
        }
        catch (OperationCanceledException)
        {
            job.Fail("The recorder is stopping.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            job.Fail(ex.Message);
            LogFailed(logger, job.SessionId, ex.Message);
        }
        finally
        {
            if (export is not null)
            {
                capture?.DeleteExport(export);
            }
        }
    }

    /// <summary>The deepest profile every rating's profile sits under, as the session's analysis likely started; null for the generic curve.</summary>
    internal static string? StartProfile(ProfileCatalog catalog, IReadOnlyList<RatingRecord> rows)
    {
        List<string>? common = null;
        foreach (RatingRecord r in rows)
        {
            string id = r.Profile is { Length: > 0 } p && catalog.Contains(p) ? p : r.Game;
            if (!catalog.Contains(id))
            {
                return null;
            }

            List<string> path = [.. catalog.PathTo(id).Select(x => x.Id)];
            common = common is null ? path : [.. common.Zip(path).TakeWhile(z => z.First == z.Second).Select(z => z.First)];
        }

        return common is { Count: > 0 } ? common[^1] : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _stop.Dispose();
        _one.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backfilled the features of {Written} of {Total} ratings of {Session}")]
    private static partial void LogDone(ILogger logger, string session, int written, int total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backfill of {Session} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string session, string reason);

    private sealed class Job(string sessionId, string game)
    {
        private readonly Lock _lock = new();
        private string _state = "starting";
        private string _message = "";
        private int _written;
        private int _total;
        private List<string> _refused = [];

        public string SessionId { get; } = sessionId;

        public string Game { get; } = game;

        public void Set(string message)
        {
            lock (_lock)
            {
                _state = "running";
                _message = message;
            }
        }

        public void Fail(string message)
        {
            lock (_lock)
            {
                _state = "failed";
                _message = message;
            }
        }

        public void Finish(int written, int total, List<string> refused)
        {
            lock (_lock)
            {
                _state = "done";
                _written = written;
                _total = total;
                _refused = refused;
                _message = string.Create(CultureInfo.InvariantCulture, $"{written} of {total} ratings now have full features");
            }
        }

        public JsonObject ToJson()
        {
            lock (_lock)
            {
                return new JsonObject
                {
                    ["state"] = _state,
                    ["message"] = _message,
                    ["written"] = _written,
                    ["total"] = _total,
                    ["refused"] = new JsonArray([.. _refused.Select(r => (JsonNode)r)]),
                };
            }
        }
    }
}
