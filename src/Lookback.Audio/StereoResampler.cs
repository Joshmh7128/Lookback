namespace Lookback.Audio;

/// <summary>
/// Turns device audio (interleaved float, any channel count and rate) into
/// the encode format: 48 kHz stereo float. Downmixes surround to stereo and
/// resamples with 4-point Hermite interpolation at a ratio the caller can
/// trim while running (<see cref="Step"/>), so the capture can follow the
/// device clock's measured drift against QPC instead of letting it pile up.
/// At a ratio of exactly 1 the output equals the input, two frames late.
/// Allocation-free; one thread.
/// </summary>
public sealed class StereoResampler
{
    public const int OutputRate = 48_000;
    private const float Center = 0.70710677f;

    private float _l0, _l1, _l2, _l3, _r0, _r1, _r2, _r3;
    private double _pos;
    private bool _primed;

    /// <summary>Input frames per output frame (input rate / 48 000). Changes take effect on the next frame.</summary>
    public double Step { get; set; } = 1.0;

    /// <summary>Frames of output space <see cref="Process"/> may need for <paramref name="inputFrames"/>.</summary>
    public int MaxOutputFrames(int inputFrames) => (int)Math.Ceiling(inputFrames / Math.Min(Step, 1.0) * 1.01) + 4;

    /// <summary>Forgets the history (after a discontinuity or a device change).</summary>
    public void Reset()
    {
        _primed = false;
        _pos = 0;
    }

    /// <summary>
    /// Resamples one packet. Writes interleaved stereo to <paramref name="output"/>
    /// and returns the frames written. <paramref name="firstOffset"/> is where
    /// the first output frame sits, in input frames from the packet's first
    /// frame (about -2, the interpolator's delay), so its time is the packet's
    /// time plus that many input frames.
    /// </summary>
    public int Process(ReadOnlySpan<float> interleaved, int channels, Span<float> output, out double firstOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        int frames = interleaved.Length / channels;
        int written = 0;
        firstOffset = 0;
        double step = Step;
        for (int f = 0; f < frames; f++)
        {
            Downmix(interleaved.Slice(f * channels, channels), channels, out float l, out float r);
            if (!_primed)
            {
                _l0 = _l1 = _l2 = _l3 = l;
                _r0 = _r1 = _r2 = _r3 = r;
                _primed = true;
            }

            _l0 = _l1;
            _l1 = _l2;
            _l2 = _l3;
            _l3 = l;
            _r0 = _r1;
            _r1 = _r2;
            _r2 = _r3;
            _r3 = r;

            // Output points between frames f-2 (_x1) and f-1 (_x2).
            while (_pos < 1.0)
            {
                if (written == 0)
                {
                    firstOffset = f - 2 + _pos;
                }

                float t = (float)_pos;
                output[2 * written] = Hermite(_l0, _l1, _l2, _l3, t);
                output[(2 * written) + 1] = Hermite(_r0, _r1, _r2, _r3, t);
                written++;
                _pos += step;
            }

            _pos -= 1.0;
        }

        return written;
    }

    private static float Hermite(float x0, float x1, float x2, float x3, float t)
    {
        float c1 = 0.5f * (x2 - x0);
        float c2 = x0 - (2.5f * x1) + (2f * x2) - (0.5f * x3);
        float c3 = (0.5f * (x3 - x0)) + (1.5f * (x1 - x2));
        return (((((c3 * t) + c2) * t) + c1) * t) + x1;
    }

    /// <summary>
    /// Stereo from WAVE channel order (FL FR FC LFE BL BR SL SR). Centre and
    /// surrounds fold in at -3 dB, LFE is dropped, and the sum is scaled so a
    /// full-scale signal on every channel cannot clip.
    /// </summary>
    internal static void Downmix(ReadOnlySpan<float> frame, int channels, out float l, out float r)
    {
        switch (channels)
        {
            case 1:
                l = r = frame[0];
                return;
            case 2:
                l = frame[0];
                r = frame[1];
                return;
            case 4: // quad: FL FR BL BR
                l = (frame[0] + (Center * frame[2])) / (1 + Center);
                r = (frame[1] + (Center * frame[3])) / (1 + Center);
                return;
        }

        float sl = 0, sr = 0;
        int pairs = 0;
        for (int i = channels >= 6 ? 4 : 3; i + 1 < channels; i += 2)
        {
            sl += frame[i];
            sr += frame[i + 1];
            pairs++;
        }

        float c = Center * frame[2];
        float surround = pairs > 0 ? Center / pairs : 0;
        float gain = 1f / (1 + Center + (pairs > 0 ? Center : 0));
        l = (frame[0] + c + (surround * sl)) * gain;
        r = (frame[1] + c + (surround * sr)) * gain;
    }
}
