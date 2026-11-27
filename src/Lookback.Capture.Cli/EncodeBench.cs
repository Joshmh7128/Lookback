using System.Globalization;
using Lookback.Capture;
using Lookback.Contracts;
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
    private EncodeStats _last;

    private EncodeBench(LiveCaptureSession session, ReplayRing ring, EncodeTicker ticker, double seconds, string output)
    {
        _session = session;
        _ring = ring;
        _ticker = ticker;
        _seconds = seconds;
        _out = output;
    }

    public static string Usage => "[--encode <seconds> --out clip.mp4 [--codec h264|hevc] [--fps 60] [--size 1920x1080] [--mbps 20] [--software]]";

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
        var ring = new ReplayRing(capacityBytes: Math.Max(64L << 20, (long)(bitrate / 8 * (seconds + 6) * 1.5)), maxDurationQpc: (long)((seconds + 4) * freq));
        try
        {
            var settings = new VideoEncoderSettings(codec, w, h, fps, bitrate) { PreferHardware = !args.Contains("--software") };
            EncodeTicker ticker = EncodeTicker.Start(session.Gpu, settings, ring, trackId: 1, SystemClock.Instance);
            session.FrameTap = ticker.FrameTap;
            Console.WriteLine($"encoding {codec} {w}x{h}@{fps} {bitrate / 1e6:F0} Mbps with {ticker.EncoderName} ({(ticker.IsHardware ? "hardware" : "software")})");
            return new EncodeBench(session, ring, ticker, seconds, output);
        }
        catch
        {
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

        _last = s;
        return line;
    }

    /// <summary>Stops the encoder (draining it), then writes the last <c>seconds</c> from the ring.</summary>
    public void Finish()
    {
        _session.FrameTap = null;
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
        _ticker.Dispose();
        _ring.Dispose();
    }

    private static string? Arg(string[] a, string name)
    {
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
