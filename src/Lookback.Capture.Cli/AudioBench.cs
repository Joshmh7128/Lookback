using System.Globalization;
using Lookback.Audio;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Encode;
using Lookback.Media.Frames;
using Lookback.Replay.Mp4;
using Lookback.Replay.Packets;

/// <summary>
/// <c>lookback-capture audio</c>: live audio on its own. Asks
/// <see cref="AudioCapturePolicy"/> what the session may capture, runs the
/// game loopback (and the mic with <c>--mic</c>), prints a level meter and
/// the clock fit every second, and with <c>--out</c> encodes AAC into a RAM
/// ring and saves it as an audio-only clip at the end. Saving is refused
/// unless <see cref="RetentionRules.CanPersist"/> allows it, so an
/// <c>--official-player</c> run never creates an encoder or writes anything.
/// </summary>
internal static class AudioBench
{
    public const string Usage = "lookback-capture audio [--list] [--seconds 10] [--pid <game pid>] [--mic] [--official-player] [--kbps 160] [--out audio.mp4]";

    private const long RingBytes = 64L << 20;

    public static async Task<int> RunAsync(string[] args)
    {
        Console.WriteLine(Usage);
        if (args.Contains("--list"))
        {
            foreach (AudioDeviceInfo d in WasapiSource.ListDevices())
            {
                Console.WriteLine($"{(d.IsRender ? "render " : "capture")} {(d.IsDefault ? "*" : " ")} {d.Name}");
            }

            Console.WriteLine($"Windows build {AudioCapturePolicy.OsBuild}; process loopback {(AudioCapturePolicy.ProcessLoopbackSupported(AudioCapturePolicy.OsBuild) ? "available" : $"needs {AudioCapturePolicy.ProcessLoopbackMinBuild}+")}");
            return 0;
        }

        bool official = args.Contains("--official-player");
        double seconds = double.Parse(Arg(args, "--seconds") ?? "10", CultureInfo.InvariantCulture);
        uint? pid = Arg(args, "--pid") is string p ? uint.Parse(p, CultureInfo.InvariantCulture) : null;
        string? outPath = Arg(args, "--out");
        int kbps = int.Parse(Arg(args, "--kbps") ?? "160", CultureInfo.InvariantCulture);
        var started = new SessionStarted
        {
            SourceKind = official ? SourceKind.OfficialPlayer : SourceKind.LiveGame,
            Retention = official ? Retention.Ephemeral : Retention.Owned,
            QpcFrequency = (ulong)System.Diagnostics.Stopwatch.Frequency,
        };

        AudioPlan plan = AudioCapturePolicy.Decide(started, AudioCapturePolicy.OsBuild, pid, args.Contains("--mic"));
        Console.WriteLine($"plan: {plan.Reason}");
        if (outPath is not null && !(plan.Encode && RetentionRules.CanPersist(started)))
        {
            Console.Error.WriteLine("--out is refused: this session may not persist audio.");
            return 2;
        }

        if (plan.Game is null)
        {
            return 0;
        }

        long freq = System.Diagnostics.Stopwatch.Frequency;
        using ReplayRing? ring = outPath is null ? null : new ReplayRing(RingBytes, 0);
        using MfAacEncoder? gameAac = ring is null ? null : MfAacEncoder.Create(new AacEncoderSettings(kbps * 1000) { Role = "game" }, ring, trackId: 1, freq);
        using MfAacEncoder? micAac = ring is null || !plan.Microphone ? null : MfAacEncoder.Create(new AacEncoderSettings(kbps * 1000) { Role = "mic" }, ring, trackId: 2, freq);

        var meter = new Meter();
        using WasapiSource game = WasapiSource.Start(AudioSourceOptions.ForGame(plan, pid ?? 0), gameAac is null ? null : gameAac.Encode, meter.OnHop);
        using WasapiSource? mic = plan.Microphone ? WasapiSource.Start(new AudioSourceOptions { Kind = AudioSourceKind.Microphone }, micAac is null ? null : micAac.Encode) : null;
        Console.WriteLine($"game: {game.Description}{(game.FallbackReason is { } why ? $" ({why})" : "")}{(game.LastError is { } e ? $" [{e}]" : "")}");
        if (mic is not null)
        {
            Console.WriteLine($"mic:  {mic.Description}{(mic.LastError is { } me ? $" [{me}]" : "")}");
        }

        var until = DateTime.UtcNow.AddSeconds(seconds);
        var t = System.Diagnostics.Stopwatch.StartNew();
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            float peak = meter.TakePeak();
            string level = peak > 0 ? $"{20 * Math.Log10(peak),6:F1} dBFS" : "  -inf dBFS";
            Console.WriteLine($"{t.Elapsed.TotalSeconds,5:F1}s  game {level}  packets {game.Packets}  hops {meter.Hops}  rate {game.MeasuredRate:F2} Hz  resets {game.ClockResets}  glitches {game.Discontinuities}  " +
                $"alloc {game.CaptureThreadAllocatedBytes} B{(mic is null ? "" : $"  mic packets {mic.Packets}")}{(gameAac is null ? "" : $"  aac {gameAac.PacketsOut}")}");
        }

        game.Dispose();
        mic?.Dispose();
        gameAac?.Dispose();
        micAac?.Dispose();
        if (ring is not null && outPath is not null)
        {
            var packets = new List<EncodedPacket>();
            ring.ReadFrom(ring.TailSequence, packets, int.MaxValue, out _);
            if (packets.Count == 0)
            {
                Console.Error.WriteLine("nothing was encoded");
                return 1;
            }

            byte[] clip = ClipWriter.Mux(new ClipSource(ring.Tracks, packets), freq);
            await File.WriteAllBytesAsync(outPath, clip).ConfigureAwait(false);
            Console.WriteLine($"wrote {outPath}: {clip.Length / 1024} KiB, tracks {string.Join(", ", ring.Tracks.Select(tr => $"{tr.Id}:{tr.Role}"))}");
        }

        return 0;
    }

    private static string? Arg(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    /// <summary>Peak level and hop count from the analysis hops (capture thread writes, the console reads).</summary>
    private sealed class Meter
    {
        private float _peak;
        private long _hops;

        public long Hops => Interlocked.Read(ref _hops);

        public void OnHop(AudioBlock block)
        {
            float peak = 0;
            foreach (float s in block.Samples)
            {
                peak = Math.Max(peak, Math.Abs(s));
            }

            if (peak > Volatile.Read(ref _peak))
            {
                Volatile.Write(ref _peak, peak);
            }

            Interlocked.Increment(ref _hops);
        }

        public float TakePeak() => Interlocked.Exchange(ref _peak, 0);
    }
}
