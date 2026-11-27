using Lookback.Media.Frames;
using Lookback.Signals.Audio;
using Lookback.Signals.Dsp;

namespace Lookback.Signals.Tests;

public class AudioSignalTests
{
    [Fact]
    public void FftMatchesNaiveDft()
    {
        const int n = 64;
        var rng = new Random(11);
        float[] re = Enumerable.Range(0, n).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray();
        float[] im = new float[n];
        float[] input = (float[])re.Clone();

        new Fft(n).Forward(re, im);

        for (int k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (int t = 0; t < n; t++)
            {
                double a = -2 * Math.PI * k * t / n;
                sr += input[t] * Math.Cos(a);
                si += input[t] * Math.Sin(a);
            }

            Assert.Equal(sr, re[k], 3);
            Assert.Equal(si, im[k], 3);
        }
    }

    [Fact]
    public void FftRejectsNonPowerOfTwo() => Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(100));

    [Fact]
    public void MelScaleRoundTrips()
    {
        Assert.Equal(1000, MelFilterbank.MelToHz(MelFilterbank.HzToMel(1000)), 6);
        Assert.Equal(1000, MelFilterbank.HzToMel(1000), 0); // 1 kHz ≈ 1000 mel by construction
    }

    [Fact]
    public void LoudnessOfAKnownSine()
    {
        var s = new LoudnessSignal();
        float[] o = new float[4];
        // A sine at amplitude 0.5 peaks at -6.02 dBFS; its RMS is 3.01 dB lower.
        s.Process(Synth.Sine(1000, 0.5), new SpectrumFrame(1024, 48_000), o);
        Assert.Equal(-9.03f, o[0], 1);
        Assert.Equal(-6.02f, o[1], 1);
        Assert.Equal(0f, o[2]);
        Assert.Equal(0f, o[3]);
    }

    [Fact]
    public void SilenceAndClippingAreFlagged()
    {
        var s = new LoudnessSignal();
        float[] o = new float[4];
        s.Process(Synth.Constant(0), new SpectrumFrame(1024, 48_000), o);
        Assert.Equal(LoudnessSignal.FloorDb, o[0]);
        Assert.Equal(1f, o[3]);

        s.Process(Synth.Constant(1f), new SpectrumFrame(1024, 48_000), o);
        Assert.Equal(1f, o[2]);
    }

    [Fact]
    public void CentroidAndMelPeakFollowTheTone()
    {
        var bank = new AudioSignalBank(1024, 48_000, [new SpectralSignal(1024, 48_000, melBands: 32)]);
        ReadOnlySpan<float> v = default;
        for (int i = 0; i < 5; i++)
        {
            v = bank.Process(Synth.Sine(1000, 0.5, firstSample: i * 480));
        }

        Assert.InRange(v[2], 900, 1100); // centroid near the tone

        // The loudest mel band is the one whose centre is nearest 1 kHz.
        ReadOnlySpan<float> mel = v[3..];
        int loudest = mel.IndexOf(MaxOf(mel));
        double melStep = (MelFilterbank.HzToMel(24_000) - MelFilterbank.HzToMel(20)) / 33;
        double centre = MelFilterbank.MelToHz(MelFilterbank.HzToMel(20) + (melStep * (loudest + 1)));
        Assert.InRange(centre, 700, 1400);
    }

    [Fact]
    public void OnsetFiresOnATransientNotOnASteadyTone()
    {
        var bank = new AudioSignalBank(1024, 48_000, [new SpectralSignal(1024, 48_000)]);
        int onsets = 0;
        for (int i = 0; i < 100; i++)
        {
            onsets += (int)bank.Process(Synth.Sine(440, 0.3, firstSample: i * 480))[1];
        }

        Assert.Equal(0, onsets);

        // A broadband burst on top of the tone.
        AudioBlock burst = Synth.Sine(440, 0.3, firstSample: 100 * 480);
        var rng = new Random(2);
        float[] noisy = burst.Samples.ToArray();
        for (int i = 0; i < noisy.Length; i++)
        {
            noisy[i] += (float)(rng.NextDouble() - 0.5);
        }

        var block = new AudioBlock(48_000, noisy.Length) { Count = noisy.Length };
        noisy.CopyTo(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(block.Samples), block.Count));
        Assert.Equal(1f, bank.Process(block)[1]);
    }

    [Fact]
    public void BankDoesNotAllocatePerHop()
    {
        AudioSignalBank bank = AudioSignalBank.CreateDefault(AnalysisFormat.Default);
        AudioBlock b = Synth.Sine(440, 0.3);
        for (int i = 0; i < 3; i++)
        {
            bank.Process(b);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            bank.Process(b);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static float MaxOf(ReadOnlySpan<float> s)
    {
        float m = float.MinValue;
        foreach (float x in s)
        {
            m = Math.Max(m, x);
        }

        return m;
    }
}
