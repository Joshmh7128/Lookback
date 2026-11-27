using System.Globalization;
using Lookback.Encode;
using Lookback.Media.Ffmpeg;
using Lookback.Replay.Mp4;
using Lookback.Replay.Packets;

namespace Lookback.Audio.Tests;

/// <summary>
/// End to end through the Microsoft AAC encoder: synthetic stereo float →
/// <see cref="MfAacEncoder"/> → <see cref="ReplayRing"/> →
/// <see cref="ClipWriter.Mux"/> → ffprobe/ffmpeg. Windows only (Media
/// Foundation); runs on the Windows CI runner too.
/// </summary>
public class AacEncoderTests(ITestOutputHelper output)
{
    private const long Freq = 10_000_000;
    private const int Rate = 48_000;
    private const int Hop = 480;
    private const double FrameSeconds = 1024.0 / Rate;

    private static float[] Signal(double seconds, double silenceSeconds = 0, double amplitude = 0.5)
    {
        int n = (int)(seconds * Rate);
        int quiet = (int)(silenceSeconds * Rate);
        float[] x = new float[n * 2];
        for (int i = quiet; i < n; i++)
        {
            double t = (double)(i - quiet) / Rate;
            x[2 * i] = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * t));
            x[(2 * i) + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * 660 * t));
        }

        return x;
    }

    private static void Feed(MfAacEncoder enc, float[] stereo, long startQpc)
    {
        int frames = stereo.Length / 2;
        for (int at = 0; at < frames; at += Hop)
        {
            int len = Math.Min(Hop, frames - at);
            enc.Encode(stereo.AsSpan(at * 2, len * 2), startQpc + (at * Freq / Rate));
        }
    }

    private static ClipSource Everything(ReplayRing ring)
    {
        var packets = new List<EncodedPacket>();
        ring.ReadFrom(ring.TailSequence, packets, int.MaxValue, out _);
        return new ClipSource(ring.Tracks, packets);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("lookback-aac-").FullName;

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static float[] DecodeLeft(string clip, string raw)
    {
        Tools.Run(FfmpegLocator.FindFfmpeg()!, $"-v error -y -i \"{clip}\" -af pan=mono|c0=c0 -f f32le -acodec pcm_f32le \"{raw}\"");
        byte[] bytes = File.ReadAllBytes(raw);
        float[] x = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, x, 0, x.Length * 4);
        return x;
    }

    [Theory]
    [InlineData(128_000)]
    [InlineData(192_000)]
    public void Sine_round_trips_through_ring_and_muxer(int bitrate)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only");
        Tools.SkipWithoutFfmpeg();
        const double Seconds = 3.0;
        using var ring = new ReplayRing(1 << 24, 0);
        long origin = 7 * Freq;
        string name;
        using (MfAacEncoder enc = MfAacEncoder.Create(new AacEncoderSettings(bitrate), ring, trackId: 2, Freq))
        {
            name = enc.Name;
            Feed(enc, Signal(Seconds), origin);
            Assert.Equal((long)(Seconds * Rate), enc.SamplesIn);
            Assert.Equal(0, enc.SilenceInserted);
            Assert.Equal(0, enc.SamplesDropped);
        }

        TrackInfo track = Assert.Single(ring.Tracks);
        Assert.Equal(CodecKind.Aac, track.Codec);
        Assert.Equal("game", track.Role);
        Assert.Equal(AacConfig.AudioSpecificConfig(Rate, 2), track.CodecConfig); // what MF_MT_USER_DATA carried

        ClipSource clip = Everything(ring);
        Assert.Equal(origin - (MfAacEncoder.EncoderDelay * Freq / Rate), clip.Packets[0].PtsQpc);
        output.WriteLine($"{name} @ {bitrate / 1000} kbps: {clip.Packets.Count} packets, {clip.Packets.Sum(p => p.Payload.Length)} bytes");

        using var dir = new TempDir();
        string path = dir.File("clip.mp4");
        File.WriteAllBytes(path, ClipWriter.Mux(clip, Freq));
        string probe = FfmpegLocator.FindFfprobe()!;

        string stream = Tools.Run(probe, $"-v error -select_streams a:0 -show_entries stream=codec_name,sample_rate,channels -of csv=p=0 \"{path}\"").Trim();
        Assert.Equal("aac,48000,2", stream);

        // The track is the input plus the priming delay, rounded up to whole frames.
        double duration = double.Parse(Tools.Run(probe, $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"").Trim(), CultureInfo.InvariantCulture);
        double expected = Seconds + ((double)MfAacEncoder.EncoderDelay / Rate);
        output.WriteLine($"duration {duration:F4} s, expected {expected:F4} ± {FrameSeconds:F4}");
        Assert.InRange(duration, expected - FrameSeconds, expected + FrameSeconds);

        string errors = Tools.Run(FfmpegLocator.FindFfmpeg()!, $"-v error -i \"{path}\" -f null -", stderr: true);
        Assert.Equal("", errors.Trim());

        // The left channel decodes back to 440 Hz at the level that went in.
        float[] left = DecodeLeft(path, dir.File("left.f32"));
        int skip = MfAacEncoder.EncoderDelay + 4800;
        Assert.InRange(Tools.PeakFrequency(left.AsSpan(skip, Rate), Rate, 300, 800), 439, 441);
        Assert.InRange(Tools.Rms(left.AsSpan(skip, Rate)), 0.33, 0.38);
    }

    [Fact]
    public void Decoded_onset_lands_at_its_capture_time()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only");
        Tools.SkipWithoutFfmpeg();
        using var ring = new ReplayRing(1 << 24, 0);
        long origin = 3 * Freq;
        float[] input = Signal(1.5, silenceSeconds: 0.5);
        using (MfAacEncoder enc = MfAacEncoder.Create(new AacEncoderSettings(), ring, trackId: 1, Freq))
        {
            Feed(enc, input, origin);
        }

        // Where the input itself first crosses the threshold (a few samples into the tone).
        int inputOnset = Array.FindIndex(input, v => Math.Abs(v) > 0.25f) / 2;

        ClipSource clip = Everything(ring);
        long firstPts = clip.Packets[0].PtsQpc; // media time zero
        using var dir = new TempDir();
        string path = dir.File("onset.mp4");
        File.WriteAllBytes(path, ClipWriter.Mux(clip, Freq));
        float[] left = DecodeLeft(path, dir.File("left.f32"));

        int onset = Array.FindIndex(left, v => Math.Abs(v) > 0.25f);
        long onsetQpc = origin + (inputOnset * Freq / Rate);
        double expected = (onsetQpc - firstPts) * (double)Rate / Freq;
        double errorMs = (onset - expected) * 1000.0 / Rate;
        output.WriteLine($"onset at sample {onset}, expected {expected:F0} (error {errorMs:F2} ms; encoder delay constant {MfAacEncoder.EncoderDelay})");
        Assert.InRange(errorMs, -1.0, 1.0);
    }

    [Fact]
    public void Late_input_is_preceded_by_silence_and_early_input_trimmed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only");
        Tools.SkipWithoutFfmpeg();
        using var ring = new ReplayRing(1 << 24, 0);
        long origin = 11 * Freq;
        long silence, dropped, samples;
        using (MfAacEncoder enc = MfAacEncoder.Create(new AacEncoderSettings { Role = "mic" }, ring, trackId: 3, Freq))
        {
            float[] second = Signal(1.0);
            Feed(enc, second, origin);
            Feed(enc, second, origin + (Freq * 6 / 5));          // 200 ms gap (a device switch)
            Feed(enc, second.AsSpan(0, Rate / 10 * 2).ToArray(), origin + (Freq * 2)); // 200 ms early
            silence = enc.SilenceInserted;
            dropped = enc.SamplesDropped;
            samples = enc.SamplesIn;
        }

        Assert.InRange(silence, 9590, 9610);
        Assert.Equal(Rate / 10, dropped); // the whole 100 ms chunk sits before where the timeline already is
        Assert.Equal("mic", Assert.Single(ring.Tracks).Role);
        Assert.InRange(samples, (long)(2.2 * Rate) - 10, (long)(2.2 * Rate) + 10);

        using var dir = new TempDir();
        string path = dir.File("gap.mp4");
        File.WriteAllBytes(path, ClipWriter.Mux(Everything(ring), Freq));
        double duration = double.Parse(Tools.Run(FfmpegLocator.FindFfprobe()!, $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"").Trim(), CultureInfo.InvariantCulture);
        double expected = ((double)samples / Rate) + ((double)MfAacEncoder.EncoderDelay / Rate);
        Assert.InRange(duration, expected - FrameSeconds, expected + FrameSeconds);
    }

    [Fact]
    public void Encoding_allocates_nothing_after_the_first_packet()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only");
        using var ring = new ReplayRing(1 << 24, 0);
        float[] x = Signal(4.0);
        using MfAacEncoder enc = MfAacEncoder.Create(new AacEncoderSettings(), ring, trackId: 1, Freq);
        int frames = x.Length / 2;
        int half = frames / 2 / Hop * Hop;
        for (int at = 0; at < half; at += Hop)
        {
            enc.Encode(x.AsSpan(at * 2, Hop * 2), (at * Freq / Rate) + Freq);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int at = half; at + Hop <= frames; at += Hop)
        {
            enc.Encode(x.AsSpan(at * 2, Hop * 2), (at * Freq / Rate) + Freq);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(enc.PacketsOut > 150);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Unsupported_bitrate_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only");
        using var ring = new ReplayRing(1 << 20, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => MfAacEncoder.Create(new AacEncoderSettings(64_000), ring, 1, Freq));
    }
}
