using Lookback.Media.Frames;

namespace Lookback.Signals.Tests;

/// <summary>Synthetic frames and audio with known properties.</summary>
internal static class Synth
{
    public const int W = 64, H = 36;

    public static VideoFrame Solid(byte r, byte g, byte b, int w = W, int h = H)
    {
        var f = new VideoFrame(w, h);
        Span<byte> p = f.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            p[i] = b;
            p[i + 1] = g;
            p[i + 2] = r;
            p[i + 3] = 255;
        }

        return f;
    }

    public static void FillRect(VideoFrame f, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        Span<byte> p = f.Pixels;
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int o = ((y * f.Width) + x) * 4;
                p[o] = b;
                p[o + 1] = g;
                p[o + 2] = r;
            }
        }
    }

    /// <summary>Random mid-grey noise: "gameplay" that changes every frame.</summary>
    public static void Noise(VideoFrame f, Random rng)
    {
        Span<byte> p = f.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            byte v = (byte)rng.Next(40, 216);
            p[i] = p[i + 1] = p[i + 2] = v;
        }
    }

    public static AudioBlock Sine(double hz, double amplitude, int sampleRate = 48_000, int count = 480, long firstSample = 0)
    {
        var block = new AudioBlock(sampleRate, count) { Count = count, FirstSample = firstSample };
        Span<float> s = AudioBuffer(block);
        for (int i = 0; i < count; i++)
        {
            s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * (firstSample + i) / sampleRate));
        }

        return block;
    }

    public static AudioBlock Constant(float value, int count = 480)
    {
        var block = new AudioBlock(48_000, count) { Count = count };
        AudioBuffer(block).Fill(value);
        return block;
    }

    private static Span<float> AudioBuffer(AudioBlock b) =>
        System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(b.Samples), b.Count);
}
