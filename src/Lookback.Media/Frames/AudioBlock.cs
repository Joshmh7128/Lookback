namespace Lookback.Media.Frames;

/// <summary>
/// One hop of mono float PCM in [-1, 1]. Borrowed like <see cref="VideoFrame"/>:
/// the source reuses the buffer for the next block.
/// </summary>
public sealed class AudioBlock
{
    public AudioBlock(int sampleRate, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        SampleRate = sampleRate;
        Buffer = new float[capacity];
    }

    public int SampleRate { get; }

    /// <summary>Valid samples in this block (the last block of a file can be short).</summary>
    public int Count { get; set; }

    /// <summary>Index of the first sample in the stream.</summary>
    public long FirstSample { get; set; }

    /// <summary>QPC time of the first sample.</summary>
    public long Qpc { get; set; }

    public ReadOnlySpan<float> Samples => Buffer.AsSpan(0, Count);

    internal float[] Buffer { get; }
}
