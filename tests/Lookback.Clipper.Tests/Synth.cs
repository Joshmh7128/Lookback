using Lookback.Contracts.V1;

namespace Lookback.Clipper.Tests;

/// <summary>
/// Synthetic signal frames: 30 fps video and 10 ms audio hops on a 10 MHz
/// clock, with a quiet, slightly noisy baseline and optional spikes.
/// </summary>
internal static class Synth
{
    public const long F = 10_000_000;
    public const long VideoTicks = F / 30;
    public const long AudioTicks = F / 100;

    public static readonly SignalSchema Video = new()
    {
        SchemaId = 1,
        Family = SignalFamily.Video,
        Names = { "luma.black", "motion.frozen_s", "flash.brighten", "motion.energy", "scene.cut" },
    };

    public static readonly SignalSchema Audio = new()
    {
        SchemaId = 2,
        Family = SignalFamily.Audio,
        Names = { "audio.rms_db", "audio.silent", "spectral.flux" },
    };

    public static long Ticks(double seconds) => (long)Math.Round(seconds * F);

    public static double Seconds(ulong ticks) => ticks / (double)F;

    /// <summary>A deterministic small wobble so the MAD is not zero.</summary>
    public static float Noise(long i, float amplitude) => amplitude * (float)Math.Sin(i * 1.7) * (float)Math.Cos(i * 0.31);

    public static SignalFrame VideoFrame(long i, float flash = 0, bool black = false, float frozenSeconds = 0) => new()
    {
        SchemaId = 1,
        WindowStartQpc = (ulong)(i * VideoTicks),
        WindowEndQpc = (ulong)((i + 1) * VideoTicks),
        Values = { black ? 1 : 0, frozenSeconds, flash + Noise(i, 0.004f), 0.02f + Noise(i + 7, 0.003f) + (flash > 0 ? 0.3f : 0), 0.01f + Noise(i + 3, 0.004f) },
    };

    public static SignalFrame AudioFrame(long i, float db = -40, bool silent = false) => new()
    {
        SchemaId = 2,
        WindowStartQpc = (ulong)(i * AudioTicks),
        WindowEndQpc = (ulong)((i + 1) * AudioTicks),
        Values = { db + Noise(i, 1.5f), silent ? 1 : 0, 0.01f + Noise(i + 5, 0.004f) + (db > -20 ? 1 : 0) },
    };

    /// <summary>
    /// Every frame of <paramref name="seconds"/> in time order. <paramref name="spike"/>
    /// says, for a time in seconds, whether a flash and a loud burst are on.
    /// </summary>
    public static IEnumerable<SignalFrame> Session(double seconds, Func<double, bool> spike, Func<double, bool>? black = null, Func<double, bool>? silent = null)
    {
        long video = 0, audio = 0;
        long end = Ticks(seconds);
        while (video * VideoTicks < end || audio * AudioTicks < end)
        {
            if (audio * AudioTicks <= video * VideoTicks)
            {
                double t = audio * AudioTicks / (double)F;
                bool quiet = silent?.Invoke(t) == true;
                yield return AudioFrame(audio++, quiet ? -90 : spike(t) ? -5 : -40, quiet);
            }
            else
            {
                double t = video * VideoTicks / (double)F;
                yield return VideoFrame(video++, spike(t) ? 0.4f : 0, black?.Invoke(t) == true);
            }
        }
    }

    public static List<ClipperOutput> Run(ClipperEngine engine, IEnumerable<SignalFrame> frames, Action<long, ClipperEngine>? atVideoFrame = null)
    {
        var output = new List<ClipperOutput>();
        engine.OnSchema(Video);
        engine.OnSchema(Audio);
        foreach (SignalFrame f in frames)
        {
            if (f.SchemaId == 1)
            {
                atVideoFrame?.Invoke((long)f.WindowStartQpc, engine);
            }

            engine.OnFrame(f, output);
        }

        engine.Flush(output);
        return output;
    }

    public static bool Between(double t, double a, double b) => t >= a && t < b;
}
