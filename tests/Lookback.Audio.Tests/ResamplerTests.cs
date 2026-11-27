namespace Lookback.Audio.Tests;

/// <summary>The device → 48 kHz stereo conversion. Platform-neutral, runs everywhere.</summary>
public class ResamplerTests
{
    private static float[] Sine(int rate, int channels, double hz, int frames, double amplitude = 0.5)
    {
        float[] x = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * f / rate));
            for (int c = 0; c < channels; c++)
            {
                x[(f * channels) + c] = v;
            }
        }

        return x;
    }

    private static float[] Left(float[] stereo, int frames)
    {
        float[] l = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            l[i] = stereo[2 * i];
        }

        return l;
    }

    [Fact]
    public void Unity_ratio_passes_samples_through_two_frames_late()
    {
        var r = new StereoResampler();
        float[] input = new float[200];
        for (int i = 0; i < 100; i++)
        {
            input[2 * i] = i;
            input[(2 * i) + 1] = -i;
        }

        float[] output = new float[2 * r.MaxOutputFrames(100)];
        int n = r.Process(input, 2, output, out double offset);
        Assert.Equal(100, n);
        Assert.Equal(-2, offset);

        // The first two outputs are the primed history (frame 0); then frame k lands at output k + 2.
        for (int k = 0; k < 98; k++)
        {
            Assert.Equal(k, output[2 * (k + 2)], 4);
            Assert.Equal(-k, output[(2 * (k + 2)) + 1], 4);
        }
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(96_000)]
    [InlineData(47_990)]
    public void Resampling_keeps_frequency_and_count(int rate)
    {
        var r = new StereoResampler { Step = rate / 48_000.0 };
        int frames = rate; // one second
        float[] input = Sine(rate, 2, 1000, frames);
        float[] output = new float[2 * r.MaxOutputFrames(frames)];

        // In packets, as the device delivers them.
        int total = 0;
        int packet = rate / 100;
        for (int at = 0; at < frames; at += packet)
        {
            int len = Math.Min(packet, frames - at);
            total += r.Process(input.AsSpan(at * 2, len * 2), 2, output.AsSpan(total * 2), out _);
        }

        Assert.InRange(total, 47_990, 48_010);
        float[] left = Left(output, total);
        Assert.InRange(Tools.PeakFrequency(left.AsSpan(4800, 38400), 48_000, 900, 1100), 999, 1001);
        Assert.InRange(Tools.Rms(left.AsSpan(4800, 38400)), 0.35, 0.36); // 0.5 / sqrt 2
    }

    [Fact]
    public void Surround_downmix_cannot_clip_and_drops_lfe()
    {
        Span<float> full = [1, 1, 1, 1, 1, 1, 1, 1];
        StereoResampler.Downmix(full, 8, out float l, out float r);
        Assert.InRange(l, 0.99f, 1.0f);
        Assert.InRange(r, 0.99f, 1.0f);

        Span<float> lfeOnly = [0, 0, 0, 1, 0, 0];
        StereoResampler.Downmix(lfeOnly, 6, out l, out r);
        Assert.Equal(0, l);
        Assert.Equal(0, r);

        Span<float> leftOnly = [1, 0, 0, 0, 0, 0];
        StereoResampler.Downmix(leftOnly, 6, out l, out r);
        Assert.True(l > 0.3f);
        Assert.Equal(0, r);

        Span<float> mono = [0.25f];
        StereoResampler.Downmix(mono, 1, out l, out r);
        Assert.Equal(0.25f, l);
        Assert.Equal(0.25f, r);
    }

    [Fact]
    public void Reset_reprimes_on_the_next_frame()
    {
        var r = new StereoResampler();
        float[] output = new float[64];
        r.Process([1, 1, 1, 1, 1, 1], 2, output, out _);
        r.Reset();
        int n = r.Process([0.5f, 0.5f, 0.5f, 0.5f], 2, output, out _);
        Assert.Equal(2, n);
        Assert.Equal(0.5f, output[0]); // not a blend with the 1s before the reset
    }

    [Fact]
    public void Pcm_decoding_matches_float()
    {
        var f16 = new PcmFormat(48_000, 1, SampleEncoding.Pcm16, 2, 16);
        float[] dst = new float[3];
        f16.ToFloat([0x00, 0x80, 0x00, 0x00, 0xFF, 0x7F], 3, dst);
        Assert.Equal(-1f, dst[0]);
        Assert.Equal(0f, dst[1]);
        Assert.Equal(32767 / 32768f, dst[2]);

        var f24 = new PcmFormat(48_000, 1, SampleEncoding.Pcm24, 3, 24);
        f24.ToFloat([0x00, 0x00, 0xC0, 0x00, 0x00, 0x40], 2, dst);
        Assert.Equal(-0.5f, dst[0]);
        Assert.Equal(0.5f, dst[1]);

        var f32 = new PcmFormat(48_000, 2, SampleEncoding.IeeeFloat, 8, 32);
        byte[] raw = new byte[8];
        BitConverter.TryWriteBytes(raw.AsSpan(0, 4), 0.25f);
        BitConverter.TryWriteBytes(raw.AsSpan(4, 4), -0.75f);
        f32.ToFloat(raw, 1, dst);
        Assert.Equal(0.25f, dst[0]);
        Assert.Equal(-0.75f, dst[1]);
    }
}
