using Lookback.Core.Time;
using Lookback.Media.Frames;

namespace Lookback.Media.Live;

/// <summary>
/// Turns live device audio (interleaved float, any channel count and rate)
/// into the analysis format: mono, <see cref="AnalysisFormat.SampleRate"/>,
/// one <see cref="AudioBlock"/> per hop, stamped in QPC. Gaps where the
/// device delivered nothing (loopback goes quiet when nothing plays) are
/// filled with silence, so hops stay continuous and signals see real
/// silence instead of time jumping. Allocation-free after construction;
/// one thread.
/// </summary>
public sealed class LiveAudioAssembler
{
    private readonly AnalysisFormat _format;
    private readonly long _frequency;
    private readonly AudioBlock _block;
    private readonly double _step;
    private double _phase;      // input position of the next output sample, relative to the previous input sample
    private float _previous;
    private long _outSamples;   // output samples emitted since the start
    private long _startQpc = -1;
    private int _fill;
    private long _skip;

    public LiveAudioAssembler(int inputRate, AnalysisFormat? format = null, long qpcFrequency = 10_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        _format = format ?? AnalysisFormat.Default;
        _frequency = qpcFrequency;
        _block = new AudioBlock(_format.SampleRate, _format.HopSamples);
        _step = (double)inputRate / _format.SampleRate;
        InputRate = inputRate;
    }

    public int InputRate { get; }

    /// <summary>Output samples of silence inserted for gaps.</summary>
    public long SilenceInserted { get; private set; }

    /// <summary>Output samples dropped to pull the output back to the clock.</summary>
    public long SamplesDropped { get; private set; }

    /// <summary>
    /// Adds one device packet whose first frame was at <paramref name="qpc"/>
    /// (from <see cref="SampleClock"/>). <paramref name="onBlock"/> receives
    /// each completed hop; the block is borrowed for the call.
    /// </summary>
    public void Add(ReadOnlySpan<float> interleaved, int channels, long qpc, Action<AudioBlock> onBlock)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentNullException.ThrowIfNull(onBlock);
        if (_startQpc < 0)
        {
            _startQpc = qpc;
        }

        // Silence for any gap longer than one hop between where output is and where this packet starts.
        long expected = OutputQpc(_outSamples + _fill);
        long gap = qpc - expected;
        long hopTicks = QpcTime.Rescale(_format.HopSamples, _format.SampleRate, _frequency);
        if (gap > hopTicks)
        {
            long missing = QpcTime.Rescale(gap, _frequency, _format.SampleRate);
            for (long i = 0; i < missing; i++)
            {
                Emit(0f, onBlock);
            }

            SilenceInserted += missing;
            _previous = 0;
            _phase = 0;
        }
        else if (gap < -hopTicks)
        {
            // Output ran ahead of the clock (device faster than QPC, or a reset): drop the excess.
            _skip = QpcTime.Rescale(-gap, _frequency, _format.SampleRate);
            SamplesDropped += _skip;
        }

        int frames = interleaved.Length / channels;
        float scale = 1f / channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            ReadOnlySpan<float> frame = interleaved.Slice(f * channels, channels);
            foreach (float s in frame)
            {
                sum += s;
            }

            float mono = sum * scale;

            // Linear interpolation between the previous input sample and this one.
            while (_phase <= 1.0)
            {
                Emit(_previous + ((mono - _previous) * (float)_phase), onBlock);
                _phase += _step;
            }

            _phase -= 1.0;
            _previous = mono;
        }
    }

    private long OutputQpc(long sample) => _startQpc + QpcTime.Rescale(sample, _format.SampleRate, _frequency);

    private void Emit(float sample, Action<AudioBlock> onBlock)
    {
        if (_skip > 0)
        {
            _skip--;
            return;
        }

        _block.Buffer[_fill++] = sample;
        if (_fill == _format.HopSamples)
        {
            _block.Count = _fill;
            _block.FirstSample = _outSamples;
            _block.Qpc = OutputQpc(_outSamples);
            onBlock(_block);
            _outSamples += _fill;
            _fill = 0;
        }
    }
}
