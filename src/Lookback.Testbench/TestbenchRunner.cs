using System.Globalization;
using System.Text;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Ffmpeg;
using Lookback.Media.Frames;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Signals;
using Lookback.Signals.Audio;
using Lookback.Signals.Video;
using Lookback.Stream.SessionLog;
using Lookback.Testbench.Report;

namespace Lookback.Testbench;

/// <summary>Inputs for one footage analysis.</summary>
internal sealed record AnalyzeOptions
{
    public required string File { get; init; }

    public required string OutputDirectory { get; init; }

    public Retention Retention { get; init; } = Retention.Owned;

    public string PermissionRef { get; init; } = "";

    public string? Title { get; init; }

    public string? Game { get; init; }

    /// <summary>A built-in profile id or a path to a profile JSON file.</summary>
    public string? Profile { get; init; }

    public string? SessionId { get; init; }
}

/// <summary>What one analysis produced.</summary>
internal sealed record AnalyzeResult(
    string SessionId,
    MediaInfo Media,
    FootageAnalysisResult Analysis,
    string? SessionBasePath,
    string? ReportPath,
    IReadOnlyList<GameEvent> Events,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The testbench pipeline: footage file → FileSource → signal banks → bus →
/// session log → timeline report. Retention decides whether the last two
/// steps happen at all.
/// </summary>
internal static class TestbenchRunner
{
    public const long Frequency = 10_000_000;

    public static async Task<AnalyzeResult> AnalyzeAsync(AnalyzeOptions o, IProgress<TimeSpan>? progress, CancellationToken ct)
    {
        string file = Path.GetFullPath(o.File);
        GameProfile? profile = o.Profile is null ? null : GameProfileLoader.Load(o.Profile);
        MediaInfo media = await MediaProbe.ProbeAsync(file, ct).ConfigureAwait(false);
        if (!media.HasVideo)
        {
            throw new InvalidDataException("The file has no video stream.");
        }

        string sessionId = o.SessionId ?? MakeSessionId(file);
        var started = new SessionStarted
        {
            SourceKind = SourceKind.File,
            Retention = o.Retention,
            PermissionRef = o.PermissionRef,
            Title = o.Title ?? Path.GetFileName(file),
            QpcFrequency = Frequency,
            GameProfile = o.Game ?? profile?.Id ?? "",
        };

        string? problem = RetentionRules.Validate(started);
        if (problem is not null)
        {
            throw new RetentionViolationException(problem);
        }

        AnalysisFormat format = AnalysisFormat.Default;
        IVideoSignal[] extra = profile is { Regions.Count: > 0 } ? [new RegionSignal(profile.Regions, format.Width, format.Height)] : [];
        var warnings = new List<string>();
        if (profile is not null)
        {
            warnings.AddRange(MissingSignals(profile, format, extra).Select(s => $"profile '{profile.Id}' uses unknown signal '{s}'; rules using it never fire"));
        }

        var header = new EnvelopeFactory(new ManualClock(Frequency), sessionId, "testbench").Create(Topics.Session, 0);
        header.SessionStarted = started;

        using var bus = new InProcessEventBus();
        using IEventSubscription eventTap = bus.Subscribe(Topics.GameEvent, capacity: 1 << 20);
        SessionLogRecorder? recorder = null;
        string? basePath = null;
        if (RetentionRules.CanPersist(started))
        {
            Directory.CreateDirectory(o.OutputDirectory);
            SessionLogWriter writer = await SessionLogWriter.CreateAsync(o.OutputDirectory, header, cancellationToken: ct).ConfigureAwait(false);
            basePath = writer.BasePath;
            recorder = new SessionLogRecorder(bus, writer, sessionId);
        }

        ProfileRunner? runner = profile is null ? null : new ProfileRunner(bus, profile, sessionId, Frequency);
        FootageAnalysisResult result;
        try
        {
            bus.Publish(header);
            var analyzer = new FootageAnalyzer(bus, format, Frequency);
            var pacer = new ReplayPacer(() => Math.Max(recorder?.Queued ?? 0, runner?.Queued ?? 0));
            result = await analyzer.AnalyzeAsync(
                file,
                sessionId,
                new FootageAnalysisOptions { HasAudio = media.HasAudio, Pacer = pacer, Progress = progress, ExtraVideoSignals = extra },
                ct).ConfigureAwait(false);

            // The profile flushes its last events onto the bus before the log closes.
            if (runner is not null)
            {
                await runner.DisposeAsync().ConfigureAwait(false);
                runner = null;
            }

            Envelope end = new EnvelopeFactory(new ManualClock(Frequency), sessionId, "testbench")
                .Create(Topics.Session, QpcTime.FromTimeSpan(media.Duration, Frequency));
            end.SessionEnded = new SessionEnded { Reason = "end of file" };
            bus.Publish(end);
        }
        finally
        {
            if (runner is not null)
            {
                await runner.DisposeAsync().ConfigureAwait(false);
            }

            if (recorder is not null)
            {
                await recorder.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (recorder is not null && recorder.Dropped > 0)
        {
            throw new InvalidOperationException($"The session log dropped {recorder.Dropped} events; the analysis is incomplete.");
        }

        var events = new List<GameEvent>();
        while (eventTap.TryRead(out Envelope? e))
        {
            events.Add(e!.GameEvent);
        }

        string? reportPath = null;
        if (basePath is not null)
        {
            reportPath = basePath + ".html";
            TimelineData timeline = await TimelineBuilder.BuildAsync(basePath, RelativeHref(o.OutputDirectory, file), ct).ConfigureAwait(false);
            AddCalibration(timeline, profile, result.HudMask);
            await ReportWriter.WriteAsync(timeline, reportPath, ct).ConfigureAwait(false);
        }

        return new AnalyzeResult(sessionId, media, result, basePath, reportPath, events, warnings);
    }

    /// <summary>Adds the profile's regions and the learned HUD mask, so the report doubles as a calibration view.</summary>
    internal static void AddCalibration(TimelineData timeline, GameProfile? profile, HudMask? mask)
    {
        if (profile is not null)
        {
            timeline.Regions.AddRange(profile.Regions.Select(r => new TimelineData.RegionBox(r.Name, r.X, r.Y, r.W, r.H, r.Purpose)));
        }

        if (mask is not null)
        {
            byte[] rgba = new byte[mask.Width * mask.Height * 4];
            for (int i = 0; i < mask.Pixels.Length; i++)
            {
                if (mask.Pixels[i])
                {
                    rgba[(i * 4) + 0] = 80;   // teal, semi-transparent
                    rgba[(i * 4) + 1] = 230;
                    rgba[(i * 4) + 2] = 200;
                    rgba[(i * 4) + 3] = 150;
                }
            }

            timeline.HudMaskPng = "data:image/png;base64," + Convert.ToBase64String(PngWriter.EncodeRgba(mask.Width, mask.Height, rgba));
        }
    }

    private static IEnumerable<string> MissingSignals(GameProfile profile, AnalysisFormat format, IVideoSignal[] extra)
    {
        var names = new HashSet<string>(VideoSignalBank.CreateDefault(format, extra).Names, StringComparer.Ordinal);
        names.UnionWith(AudioSignalBank.CreateDefault(format).Names);
        return GameProfileLoader.ValidateSignals(profile, names);
    }

    /// <summary>Rebuilds the report for an existing session log.</summary>
    public static async Task<string> ReportAsync(string sessionBasePath, string? videoFile, CancellationToken ct)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(sessionBasePath)) ?? ".";
        string? href = videoFile is null ? null : RelativeHref(dir, Path.GetFullPath(videoFile));
        TimelineData timeline = await TimelineBuilder.BuildAsync(sessionBasePath, href, ct).ConfigureAwait(false);
        string path = sessionBasePath + ".html";
        await ReportWriter.WriteAsync(timeline, path, ct).ConfigureAwait(false);
        return path;
    }

    internal static string MakeSessionId(string file)
    {
        var name = new StringBuilder();
        foreach (char c in Path.GetFileNameWithoutExtension(file))
        {
            name.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
            if (name.Length >= 40)
            {
                break;
            }
        }

        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return $"{stamp}-{name.ToString().Trim('-')}";
    }

    /// <summary>A URL-escaped path from the report's folder to the footage, so the viewer can play it.</summary>
    internal static string RelativeHref(string fromDirectory, string file)
    {
        string rel = Path.GetRelativePath(Path.GetFullPath(fromDirectory), file);
        return string.Join('/', rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Select(Uri.EscapeDataString));
    }
}
