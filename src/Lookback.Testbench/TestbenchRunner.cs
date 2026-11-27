using System.Globalization;
using System.Text;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Ffmpeg;
using Lookback.Signals;
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

    public string? SessionId { get; init; }
}

/// <summary>What one analysis produced.</summary>
internal sealed record AnalyzeResult(
    string SessionId,
    MediaInfo Media,
    FootageAnalysisResult Analysis,
    string? SessionBasePath,
    string? ReportPath);

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
            GameProfile = o.Game ?? "",
        };

        string? problem = RetentionRules.Validate(started);
        if (problem is not null)
        {
            throw new RetentionViolationException(problem);
        }

        var header = new EnvelopeFactory(new ManualClock(Frequency), sessionId, "testbench").Create(Topics.Session, 0);
        header.SessionStarted = started;

        using var bus = new InProcessEventBus();
        SessionLogRecorder? recorder = null;
        string? basePath = null;
        if (RetentionRules.CanPersist(started))
        {
            Directory.CreateDirectory(o.OutputDirectory);
            SessionLogWriter writer = await SessionLogWriter.CreateAsync(o.OutputDirectory, header, cancellationToken: ct).ConfigureAwait(false);
            basePath = writer.BasePath;
            recorder = new SessionLogRecorder(bus, writer, sessionId);
        }

        FootageAnalysisResult result;
        try
        {
            bus.Publish(header);
            var analyzer = new FootageAnalyzer(bus, frequency: Frequency);
            ReplayPacer? pacer = recorder is null ? null : new ReplayPacer(() => recorder.Queued);
            result = await analyzer.AnalyzeAsync(file, sessionId, media.HasAudio, pacer, progress, ct).ConfigureAwait(false);

            Envelope end = new EnvelopeFactory(new ManualClock(Frequency), sessionId, "testbench")
                .Create(Topics.Session, QpcTime.FromTimeSpan(media.Duration, Frequency));
            end.SessionEnded = new SessionEnded { Reason = "end of file" };
            bus.Publish(end);
        }
        finally
        {
            if (recorder is not null)
            {
                await recorder.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (recorder is not null && recorder.Dropped > 0)
        {
            throw new InvalidOperationException($"The session log dropped {recorder.Dropped} events; the analysis is incomplete.");
        }

        string? reportPath = null;
        if (basePath is not null)
        {
            reportPath = basePath + ".html";
            TimelineData timeline = await TimelineBuilder.BuildAsync(basePath, RelativeHref(o.OutputDirectory, file), ct).ConfigureAwait(false);
            await ReportWriter.WriteAsync(timeline, reportPath, ct).ConfigureAwait(false);
        }

        return new AnalyzeResult(sessionId, media, result, basePath, reportPath);
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
