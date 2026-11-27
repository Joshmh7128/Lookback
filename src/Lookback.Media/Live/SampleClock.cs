namespace Lookback.Media.Live;

/// <summary>
/// Maps an audio device's sample position to QPC. WASAPI reports, with each
/// packet, the device position and the QPC time it was sampled, but both
/// jitter and the device crystal drifts against the performance counter.
/// This fits a line through recent observations by exponentially weighted
/// least squares (a window of about 30 s), so jitter averages out while slow
/// drift is followed; the rate is clamped to ±0.5% of nominal. A jump
/// beyond the tolerance (a glitch, a device change, a discontinuity flag)
/// resets the fit. Allocation-free; one thread.
/// </summary>
public sealed class SampleClock
{
    private readonly long _qpcFrequency;
    private readonly double _nominalTicksPerSample;
    private readonly double _resetTicks;
    private readonly double _decayPerSample;
    private bool _started;
    private long _originPos;
    private long _originQpc;
    private long _lastPos;
    private double _sw, _sx, _sy, _sxx, _sxy;
    private double _a;
    private double _ticksPerSample;

    public SampleClock(int sampleRate, long qpcFrequency, TimeSpan? resetTolerance = null, TimeSpan? window = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        SampleRate = sampleRate;
        _qpcFrequency = qpcFrequency;
        _nominalTicksPerSample = (double)qpcFrequency / sampleRate;
        _ticksPerSample = _nominalTicksPerSample;
        _resetTicks = (resetTolerance ?? TimeSpan.FromMilliseconds(20)).TotalSeconds * qpcFrequency;
        _decayPerSample = 1.0 / ((window ?? TimeSpan.FromSeconds(30)).TotalSeconds * sampleRate);
    }

    public int SampleRate { get; }

    /// <summary>Times the fit was reset (first packet included).</summary>
    public int Resets { get; private set; }

    /// <summary>Current estimate of device samples per second, measured against QPC.</summary>
    public double MeasuredRate => _qpcFrequency / _ticksPerSample;

    /// <summary>
    /// Feeds one packet's position and QPC time and returns the smoothed QPC
    /// of its first sample.
    /// </summary>
    public long Observe(long devicePosition, long qpc, bool discontinuity = false)
    {
        if (!_started || discontinuity || devicePosition < _lastPos)
        {
            Reset(devicePosition, qpc);
            return qpc;
        }

        double x = devicePosition - _originPos;
        double y = qpc - _originQpc;
        double predicted = _a + (_ticksPerSample * x);
        if (Math.Abs(y - predicted) > _resetTicks)
        {
            Reset(devicePosition, qpc);
            return qpc;
        }

        // Age the sums by the samples elapsed, then add this observation.
        double keep = Math.Max(0, 1 - ((devicePosition - _lastPos) * _decayPerSample));
        _sw = (_sw * keep) + 1;
        _sx = (_sx * keep) + x;
        _sy = (_sy * keep) + y;
        _sxx = (_sxx * keep) + (x * x);
        _sxy = (_sxy * keep) + (x * y);
        _lastPos = devicePosition;

        double det = (_sw * _sxx) - (_sx * _sx);
        if (_sw > 2 && det > 1e-9 * _sxx * _sw)
        {
            double b = ((_sw * _sxy) - (_sx * _sy)) / det;
            _ticksPerSample = Math.Clamp(b, _nominalTicksPerSample * 0.995, _nominalTicksPerSample * 1.005);
        }

        _a = (_sy - (_ticksPerSample * _sx)) / _sw;
        return ToQpc(devicePosition);
    }

    /// <summary>QPC of a device position on the current fit (for samples inside a packet).</summary>
    public long ToQpc(long devicePosition) =>
        _originQpc + (long)Math.Round(_a + (_ticksPerSample * (devicePosition - _originPos)));

    private void Reset(long pos, long qpc)
    {
        _started = true;
        _originPos = _lastPos = pos;
        _originQpc = qpc;
        _sw = 1;
        _sx = _sy = _sxx = _sxy = 0;
        _a = 0;
        _ticksPerSample = _nominalTicksPerSample;
        Resets++;
    }
}
