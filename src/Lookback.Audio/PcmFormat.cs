using System.Buffers.Binary;

namespace Lookback.Audio;

/// <summary>Sample encodings WASAPI shared mode delivers (in practice almost always 32-bit float).</summary>
public enum SampleEncoding
{
    /// <summary>32-bit IEEE float, the shared-mode mix format nearly everywhere.</summary>
    IeeeFloat,
    Pcm16,
    Pcm24,
    Pcm32,
}

/// <summary>An interleaved device stream format.</summary>
public sealed record PcmFormat(int SampleRate, int Channels, SampleEncoding Encoding, int BlockAlign, int ValidBits)
{
    public int BytesPerSample => BlockAlign / Channels;

    public override string ToString() => $"{SampleRate} Hz {Channels} ch {Encoding}";

    /// <summary>
    /// Decodes <paramref name="frames"/> frames of <paramref name="src"/> into
    /// interleaved float in [-1, 1]. Allocation-free.
    /// </summary>
    public void ToFloat(ReadOnlySpan<byte> src, int frames, Span<float> dst)
    {
        int n = frames * Channels;
        int step = BytesPerSample;
        switch (Encoding)
        {
            case SampleEncoding.IeeeFloat when step == 4:
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(src[..(n * 4)]).CopyTo(dst);
                break;
            case SampleEncoding.IeeeFloat:
                for (int i = 0; i < n; i++)
                {
                    dst[i] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(i * step, 4));
                }

                break;
            case SampleEncoding.Pcm16:
                for (int i = 0; i < n; i++)
                {
                    dst[i] = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(i * step, 2)) / 32768f;
                }

                break;
            case SampleEncoding.Pcm24:
                for (int i = 0; i < n; i++)
                {
                    ReadOnlySpan<byte> s = src.Slice(i * step, 3);
                    int v = (s[0] << 8) | (s[1] << 16) | (s[2] << 24);
                    dst[i] = v / 2147483648f;
                }

                break;
            case SampleEncoding.Pcm32:
                for (int i = 0; i < n; i++)
                {
                    dst[i] = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(i * step, 4)) / 2147483648f;
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported encoding {Encoding}.");
        }
    }
}
