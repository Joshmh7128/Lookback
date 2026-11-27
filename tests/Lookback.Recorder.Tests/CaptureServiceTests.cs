using System.Diagnostics;
using System.Globalization;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Ffmpeg;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lookback.Recorder.Tests;

/// <summary>
/// Live capture in the recorder on a real machine: game detection sees our own
/// stand-in game window (allowed through a temporary detection list), the
/// recorder captures, encodes and stores it, a candidate's window is saved as
/// a clip, and closing the window ends the session. Everything goes to temp
/// folders. Needs Windows, a GPU with video support and ffmpeg; skips otherwise.
/// </summary>
public sealed class CaptureServiceTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lookback-capture-svc-").FullName;

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static string? TestWindowExe()
    {
        string root = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && root is not null; i++, root = Path.GetDirectoryName(root)!)
        {
            string candidate = Path.Combine(root, "tests", "Lookback.Detection.TestWindow", "bin", "Release", "net10.0-windows10.0.22621.0", "lookback-testwindow.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan within, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > within)
            {
                return false;
            }

            await Task.Delay(100, ct);
        }

        return true;
    }

    [Fact]
    public async Task A_detected_game_is_recorded_a_candidate_is_saved_and_exit_ends_the_session()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "live capture is Windows only");
        Assert.SkipWhen(FfmpegLocator.FindFfprobe() is null, "ffprobe not installed");
        string? window = TestWindowExe();
        Assert.SkipWhen(window is null, "build tests/Lookback.Detection.TestWindow first");
        try
        {
            using var probe = Capture.Gpu.GpuDevice.Create();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SharpGen.Runtime.SharpGenException)
        {
            Assert.Skip("no adapter with D3D11 video support");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        string settings = Path.Combine(_dir, "detection.json");
        await File.WriteAllTextAsync(settings, "{ \"allow\": [ { \"exe\": \"lookback-testwindow.exe\", \"profile\": \"generic\" } ] }", ct);
        var options = new RecorderOptions
        {
            StoreDirectory = Path.Combine(_dir, "store"),
            DatasetsDirectory = Path.Combine(_dir, "datasets"),
            DetectionSettings = settings,
            ClipsBudgetGB = 1,
        };

        using var bus = new InProcessEventBus();
        using IEventSubscription all = bus.Subscribe("*", capacity: 1 << 18);
        string? liveId = null;
        await using var sessions = new SessionDirectory(bus);
        await using CaptureService capture = CaptureService.Start(bus, options, sessions, NullLogger.Instance);

        using Process game = Process.Start(new ProcessStartInfo(window!, "\"Lookback capture test\" 120") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        try
        {
            // Detection holds a new game for a few seconds before announcing it.
            Assert.True(await UntilAsync(() => capture.LiveSessionId is not null, TimeSpan.FromSeconds(20), ct), "the test window was never recorded");
            string sessionId = capture.LiveSessionId!;
            SessionStarted? started = null;
            Assert.True(await UntilAsync(() => (started = sessions.Session(sessionId)) is not null, TimeSpan.FromSeconds(5), ct));
            Assert.Equal(SourceKind.LiveGame, started!.SourceKind);

            // The recorder records one game at a time; a real game already running is recorded instead.
            Assert.SkipWhen(started.GameProfile != "generic", $"another game is running ({started.Title}): close it to run this test");
            Assert.Equal("generic", started.GameProfile);

            // Record a while, then save the last few seconds as if the clipper had proposed them.
            await Task.Delay(TimeSpan.FromSeconds(7), ct);
            liveId = sessionId;
            long now = SystemClock.Instance.Now, f = SystemClock.Instance.Frequency;
            Envelope cand = new EnvelopeFactory(SystemClock.Instance, sessionId, "test").Create(Topics.ClipCandidate);
            cand.ClipCandidate = new ClipCandidate { CandidateId = "t1", StartQpc = (ulong)(now - (5 * f)), EndQpc = (ulong)(now - f), PeakQpc = (ulong)(now - (3 * f)), GameProfile = "generic" };
            sessions.Apply(cand);
            SaveResult saved = await capture.SaveAsync(sessionId, "t1");
            Assert.True(saved.Path is not null, $"{saved.Error}: {saved.Message}");
            Assert.True(File.Exists(saved.Path));

            string streams = Run(FfmpegLocator.FindFfprobe()!, $"-v error -show_entries stream=codec_type,codec_name -show_entries format=duration -of csv=p=0 \"{saved.Path}\"");
            Assert.Contains("video", streams, StringComparison.Ordinal);
            double duration = double.Parse(streams.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1], CultureInfo.InvariantCulture);
            Assert.InRange(duration, 3.5, 7.0); // starts on the keyframe at or before the window (2 s GOP)

            StorageStatus status = capture.Status();
            Assert.True(status.ClipsBytes > 0);
            Assert.False(status.ClipsFull);
            Assert.True(status.FootageBytes > 0, "no segments were written");
        }
        finally
        {
            game.StandardInput.Close(); // the test window closes when stdin ends
            game.WaitForExit(5000);
        }

        // Detection gives an exiting game a few seconds' grace, then the session ends.
        Assert.True(await UntilAsync(() => capture.LiveSessionId is null, TimeSpan.FromSeconds(20), ct), "the session did not end after the game exited");
        bool ended = false, started2 = false, schemaAfterStart = false, curve = false;
        while (all.TryRead(out Envelope? e))
        {
            ended |= e!.PayloadCase == Envelope.PayloadOneofCase.SessionEnded;
            if (e.SessionId != liveId)
            {
                continue;
            }

            started2 |= e.PayloadCase == Envelope.PayloadOneofCase.SessionStarted;
            schemaAfterStart |= started2 && e.Topic == Topics.SignalsVideo && e.PayloadCase == Envelope.PayloadOneofCase.SignalSchema;
            curve |= e.Topic == Topics.SignalsClipper && e.PayloadCase == Envelope.PayloadOneofCase.SignalFrame;
        }

        Assert.True(ended);

        // The profile and the clipper read the live signals: the clipper published its excitement
        // curve, which it does only for frames it can name. And the video schema came again after
        // the session started, so a page that joins late (the profiler) can name them too.
        Assert.True(curve, "the clipper never read the live signals (it never saw their schema)");
        Assert.True(schemaAfterStart, "no video schema after SessionStarted: late joiners cannot name the signals");
    }

    private static string Run(string exe, string args)
    {
        using Process p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.Result);
        return output;
    }
}
