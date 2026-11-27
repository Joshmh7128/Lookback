namespace Lookback.Replay.Mp4;

/// <summary>MSB-first bit reader with Exp-Golomb support, for parameter-set parsing.</summary>
internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bit;

    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bit = 0;
    }

    public uint Bits(int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            int byteIndex = _bit >> 3;
            if (byteIndex >= _data.Length)
            {
                throw new InvalidDataException("Parameter set ended early.");
            }

            value = (value << 1) | (uint)((_data[byteIndex] >> (7 - (_bit & 7))) & 1);
            _bit++;
        }

        return value;
    }

    public ulong Bits64(int count)
    {
        ulong v = 0;
        while (count > 0)
        {
            int take = Math.Min(count, 32);
            v = (v << take) | Bits(take);
            count -= take;
        }

        return v;
    }

    public bool Flag() => Bits(1) == 1;

    public void Skip(int count)
    {
        _bit += count;
        if ((_bit + 7) >> 3 > _data.Length)
        {
            throw new InvalidDataException("Parameter set ended early.");
        }
    }

    public uint Ue()
    {
        int zeros = 0;
        while (Bits(1) == 0)
        {
            zeros++;
            if (zeros > 31)
            {
                throw new InvalidDataException("Bad Exp-Golomb code.");
            }
        }

        return zeros == 0 ? 0 : ((1u << zeros) - 1) + Bits(zeros);
    }
}
