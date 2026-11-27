using System.Globalization;
using Lookback.Capture;
using Lookback.Contracts;
using Lookback.Core.Diagnostics;
using Lookback.Core.Time;
using Lookback.Encode;
using Lookback.Replay.Mp4;
using Lookback.Replay.Packets;

/// <summary>
/// <c>--encode &lt;seconds&gt; --out clip.mp4</c>: runs the encode path
/// (NV12 convert → CFR ticker → MF encoder → RAM ring) beside the analysis
/// tap, and at the end saves the last <c>seconds</c> as a clip, cut from the
/// ring on a keyframe with no re-encode. Refused for sessions that may not
/// persist (official player), and then nothing of the encode path exists.
/// </summary>
internal sealed class EncodeBench : IDisposable
{
    private readonly LiveCaptureSession _session;
    private readonly ReplayRing _ring;
    private readonly EncodeTicker _ticker;
    private readonly double _seconds;
    private readonly string _out;
    private readonly StallWatchdog _watchdog;
    private readonly IDisposable _captureWatch;
    private EncodeStats _last;

    private EncodeBench(LiveCaptureSession session, ReplayRing ring, EncodeTicker ticker, double seconds, string output, StallWatchdog watchdog, IDisposable captureWatch)
    {
        _session = session;
        _ring = ring;
        _ticker = ticker;
        _seconds = seconds;
        _out = output;
        _watchdog = watchdog;
        _captureWatch = captureWatch;
    }

    public static string Usage => "[--encode <seconds> --out clip.mp4 [--codec h264|hevc] [--fps 60] [--size 1920x1080] [--mbps 20] [--software] [--isolate] [--stall-ms 2000] [--no-blocklist]]";

    /// <summary>Starts the encode path when <c>--encode</c> is given; null otherwise. Throws on a refused or bad request.</summary>
    public static EncodeBench? TryStart(string[] args, LiveCaptureSession session)
    {
        string? encode = Arg(args, "--encode");
        if (encode is null)
        {
            return null;
        }

        string output = Arg(args, "--out") ?? throw new ArgumentException("--encode needs --out <clip.mp4>.");
        if (!RetentionRules.CanPersist(session.Started))
        {
            throw new InvalidOperationException("This session may not be persisted (official player or ephemeral): nothing is encoded or written.");
        }

        double seconds = double.Parse(encode, CultureInfo.InvariantCulture);
        CodecKind codec = (Arg(args, "--codec") ?? "hevc").ToUpperInvariant() switch
        {
            "H264" or "AVC" => CodecKind.H264,
            "HEVC" or "H265" => CodecKind.Hevc,
            string c => throw new ArgumentException($"Unknown codec {c} (h264 or hevc; AV1 is not muxed yet)."),
        };
        int fps = int.Parse(Arg(args, "--fps") ?? "60", CultureInfo.InvariantCulture);
        string[] size = (Arg(args, "--size") ?? "1920x1080").Split('x');
        int w = int.Parse(size[0], CultureInfo.InvariantCulture), h = int.Parse(size[1], CultureInfo.InvariantCulture);
        long bitrate = (long)(double.Parse(Arg(args, "--mbps") ?? "20", CultureInfo.InvariantCulture) * 1_000_000);

        long freq = SystemClock.Instance.Frequency;
        var stallAfter = TimeSpan.FromMilliseconds(int.Parse(Arg(args, "--stall-ms") ?? "2000", CultureInfo.InvariantCulture));
        var ring = new ReplayRing(capacityBytes: Math.Max(64L << 20, (long)(bitrate / 8 * (seconds + 6) * 1.5)), maxDurationQpc: (long)((seconds + 4) * freq));
        var watchdog = new StallWatchdog(SystemClock.Instance, TimeSpan.FromMilliseconds(250));
        try
        {
            var settings = new VideoEncoderSettings(codec, w, h, fps, bitrate) { PreferHardware = !args.Contains("--software"), IsolateDevice = args.Contains("--isolate") };
            var options = new EncodeTickerOptions
            {
                Watchdog = watchdog,
                StallAfter = stallAfter,
                Blocklist = args.Contains("--no-blocklist") ? null : new EncoderBlocklist(EncoderBlocklist.DefaultPath),
                Log = m => Console.Error.WriteLine(m),
            };
            EncodeTicker ticker = EncodeTicker.Start(session.Gpu, settings, ring, trackId: 1, SystemClock.Instance, options);
            session.FrameTap = ticker.FrameTap;

            // A wedged capture thread cannot be recovered in-process: end it rather than hang.
            IDisposable captureWatch = watchdog.Watch("capture thread", session.CaptureBeat, stallAfter * 2, r => Fatal.Default($"capture thread stuck in {r.Step} for {r.For.TotalSeconds:F1} s [{ticker.Phase}]"));
            Console.WriteLine($"encoding {ticker.Settings.Codec} {w}x{h}@{fps} {bitrate / 1e6:F0} Mbps with {ticker.EncoderName} ({(ticker.IsHardware ? "hardware" : "software")}, {(ticker.IsIsolated ? "own device" : "capture device")})");
            return new EncodeBench(session, ring, ticker, seconds, output, watchdog, captureWatch);
        }
        catch
        {
            watchdog.Dispose();
            ring.Dispose();
            throw;
        }
    }

    /// <summary>One line of per-second counters.</summary>
    public string Tick(double dt)
    {
        EncodeStats s = _ticker.Stats;
        string line = $"  encode {(s.Encoded - _last.Encoded) / dt,5:F1} fps  repeated {s.Repeated - _last.Repeated,3}  skipped {s.Skipped}  busy {s.EncoderBusy}  ring {_ring.BytesUsed / 1048576.0,6:F1} MB";
        if (s.Ticks == _last.Ticks && s.Captured == _last.Captured && _last.Ticks > 0)
        {
            line += $"  STALLED: {_ticker.Phase}";
        }

        if (s.Recoveries != _last.Recoveries)
        {
            line += $"  now on {_ticker.EncoderName}";
        }

        _last = s;
        return line;
    }

    /// <summary>Stops the encoder (draining it), then writes the last <c>seconds</c> from the ring.</summary>
    public void Finish()
    {
        _session.FrameTap = null;
        _captureWatch.Dispose();
        _ticker.Dispose();
        var first = new List<EncodedPacket>(1);
        _ring.ReadFrom(_ring.TailSequence, first, 1, out _);
        if (first.Count == 0)
        {
            Console.Error.WriteLine("nothing was encoded");
            return;
        }

        long freq = SystemClock.Instance.Frequency;
        long end = SystemClock.Instance.Now;
        long from = Math.Max(first[0].PtsQpc, end - (long)(_seconds * freq));
        if (!_ring.TrySnapshot(from, end, out ClipSource clip))
        {
            Console.Error.WriteLine("the ring no longer holds the clip start");
            return;
        }

        byte[] mp4 = ClipWriter.Mux(clip, freq);
        File.WriteAllBytes(_out, mp4);
        int frames = clip.Packets.Count(p => p.TrackId == 1);
        Console.WriteLine($"wrote {_out}: {frames} frames, {mp4.Length / 1048576.0:F1} MB; ticker {_ticker.Stats}");
    }

    public void Dispose()
    {
        _captureWatch.Dispose();
        _ticker.Dispose();
        _watchdog.Dispose();
        _ring.Dispose();
    }

    private static string? Arg(string[] a, string name)
    {
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
