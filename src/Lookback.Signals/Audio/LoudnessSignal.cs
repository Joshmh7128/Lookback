using Lookback.Media.Frames;

namespace Lookback.Signals.Audio;

/// <summary>
/// Level per hop: RMS and peak in dBFS, the fraction of clipped samples, and
/// a silence flag. Loudness spikes against the game's own baseline are a
/// highlight cue; clipping and silence feed audio QA.
/// </summary>
public sealed class LoudnessSignal : IAudioSignal
{
    public const float FloorDb = -120f;
    public const float SilenceDb = -60f;
    public const float ClipLevel = 0.999f;

    public IReadOnlyList<string> Names { get; } = ["audio.rms_db", "audio.peak_db", "audio.clipped", "audio.silent"];

    public void Process(AudioBlock block, SpectrumFrame spectrum, Span<float> output)
    {
        ArgumentNullException.ThrowIfNull(block);
        ReadOnlySpan<float> s = block.Samples;
        double sumSq = 0;
        float peak = 0;
        int clipped = 0;
        foreach (float v in s)
        {
            float a = Math.Abs(v);
            sumSq += v * v;
            peak = Math.Max(peak, a);
            if (a >= ClipLevel)
            {
                clipped++;
            }
        }

        float rmsDb = s.IsEmpty ? FloorDb : ToDb(Math.Sqrt(sumSq / s.Length));
        output[0] = rmsDb;
        output[1] = ToDb(peak);
        output[2] = s.IsEmpty ? 0 : (float)clipped / s.Length;
        output[3] = rmsDb < SilenceDb ? 1 : 0;
    }

    internal static float ToDb(double amplitude) =>
        amplitude <= 0 ? FloorDb : Math.Max(FloorDb, (float)(20 * Math.Log10(amplitude)));

    public void Reset()
    {
    }
}
