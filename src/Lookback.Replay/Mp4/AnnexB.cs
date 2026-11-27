using System.Buffers.Binary;

namespace Lookback.Replay.Mp4;

/// <summary>
/// Annex B (start-code) bitstream helpers. Hardware encoders emit Annex B;
/// MP4 wants each NAL unit prefixed with its 4-byte length.
/// </summary>
public static class AnnexB
{
    /// <summary>Enumerates the NAL units of an Annex B buffer, without their start codes. Allocation-free.</summary>
    public static NalEnumerator Nals(ReadOnlySpan<byte> data) => new(data, lengthPrefixed: false);

    /// <summary>Enumerates the NAL units of a 4-byte length-prefixed access unit. Allocation-free.</summary>
    public static NalEnumerator LengthPrefixedNals(ReadOnlySpan<byte> data) => new(data, lengthPrefixed: true);

    /// <summary>True when <paramref name="data"/> starts with a 3- or 4-byte start code.</summary>
    public static bool HasStartCode(ReadOnlySpan<byte> data) =>
        (data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1)
        || (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1);

    /// <summary>Upper bound of <see cref="ToLengthPrefixed"/>'s output for an input of <paramref name="annexBLength"/> bytes.</summary>
    public static int MaxLengthPrefixedSize(int annexBLength) => annexBLength + (annexBLength / 3) + 4;

    /// <summary>
    /// Converts Annex B to 4-byte length-prefixed NAL units in
    /// <paramref name="destination"/> and returns the bytes written.
    /// Allocation-free. Input without a start code is taken to be
    /// length-prefixed already and copied as is.
    /// </summary>
    public static int ToLengthPrefixed(ReadOnlySpan<byte> annexB, Span<byte> destination)
    {
        if (!HasStartCode(annexB))
        {
            annexB.CopyTo(destination);
            return annexB.Length;
        }

        int written = 0;
        foreach (ReadOnlySpan<byte> nal in Nals(annexB))
        {
            if (written + 4 + nal.Length > destination.Length)
            {
                throw new ArgumentException("Destination too small for the converted access unit.", nameof(destination));
            }

            BinaryPrimitives.WriteUInt32BigEndian(destination[written..], (uint)nal.Length);
            nal.CopyTo(destination[(written + 4)..]);
            written += 4 + nal.Length;
        }

        return written;
    }

    /// <summary>Removes emulation-prevention bytes (00 00 03 → 00 00), giving the RBSP.</summary>
    public static byte[] ToRbsp(ReadOnlySpan<byte> nal)
    {
        byte[] result = new byte[nal.Length];
        int n = 0;
        int zeros = 0;
        foreach (byte b in nal)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            result[n++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return result.AsSpan(0, n).ToArray();
    }

    internal static int FindStart(ReadOnlySpan<byte> data, int from, out int length)
    {
        for (int i = from; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
            {
                continue;
            }

            if (data[i + 2] == 1)
            {
                length = 3;
                return i;
            }

            if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
            {
                length = 4;
                return i;
            }
        }

        length = 0;
        return -1;
    }
}

/// <summary>Enumerator over NAL units; use with <c>foreach</c>.</summary>
public ref struct NalEnumerator
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly bool _lengthPrefixed;
    private int _pos;
    private int _codeLength;

    internal NalEnumerator(ReadOnlySpan<byte> data, bool lengthPrefixed)
    {
        _data = data;
        _lengthPrefixed = lengthPrefixed;
        _pos = lengthPrefixed ? 0 : AnnexB.FindStart(data, 0, out _codeLength);
        Current = default;
    }

    public ReadOnlySpan<byte> Current { get; private set; }

    public readonly NalEnumerator GetEnumerator() => this;

    public bool MoveNext()
    {
        if (_lengthPrefixed)
        {
            if (_pos + 4 > _data.Length)
            {
                return false;
            }

            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(_data[_pos..]);
            if (len < 0 || _pos + 4 + len > _data.Length)
            {
                throw new InvalidDataException("Truncated length-prefixed NAL unit.");
            }

            Current = _data.Slice(_pos + 4, len);
            _pos += 4 + len;
            return true;
        }

        while (_pos >= 0)
        {
            int nalStart = _pos + _codeLength;
            int next = AnnexB.FindStart(_data, nalStart, out int nextLength);
            int nalEnd = next >= 0 ? next : _data.Length;

            // Trailing zeros belong to the next start code (zero_byte) or are padding.
            while (nalEnd > nalStart && _data[nalEnd - 1] == 0)
            {
                nalEnd--;
            }

            _pos = next;
            _codeLength = nextLength;
            if (nalEnd > nalStart)
            {
                Current = _data[nalStart..nalEnd];
                return true;
            }
        }

        return false;
    }
}
