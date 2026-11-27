using System.Buffers.Binary;
using System.Text;

namespace Lookback.Replay.Mp4;

/// <summary>Growable big-endian writer for ISO BMFF boxes. Used on the segment path, not per frame.</summary>
public sealed class BoxWriter
{
    private byte[] _buffer;
    private readonly Stack<int> _open = new();

    public BoxWriter(int capacity = 4096)
    {
        _buffer = new byte[Math.Max(16, capacity)];
    }

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, Length);

    public byte[] ToArray() => Written.ToArray();

    public void Clear()
    {
        Length = 0;
        _open.Clear();
    }

    /// <summary>Starts a box; returns its offset. Close it with <see cref="End"/>.</summary>
    public int Begin(string type)
    {
        int at = Length;
        U32(0);
        FourCc(type);
        _open.Push(at);
        return at;
    }

    /// <summary>Starts a full box (version and flags).</summary>
    public int BeginFull(string type, byte version, uint flags)
    {
        int at = Begin(type);
        U32(((uint)version << 24) | (flags & 0xFFFFFF));
        return at;
    }

    public void End()
    {
        int at = _open.Pop();
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(at), (uint)(Length - at));
    }

    public void U8(byte v) => Reserve(1)[0] = v;

    public void U16(ushort v) => BinaryPrimitives.WriteUInt16BigEndian(Reserve(2), v);

    public void U24(uint v)
    {
        Span<byte> s = Reserve(3);
        s[0] = (byte)(v >> 16);
        s[1] = (byte)(v >> 8);
        s[2] = (byte)v;
    }

    public void U32(uint v) => BinaryPrimitives.WriteUInt32BigEndian(Reserve(4), v);

    public void I32(int v) => BinaryPrimitives.WriteInt32BigEndian(Reserve(4), v);

    public void U64(ulong v) => BinaryPrimitives.WriteUInt64BigEndian(Reserve(8), v);

    public void Zeros(int count) => Reserve(count).Clear();

    public void Bytes(ReadOnlySpan<byte> data) => data.CopyTo(Reserve(data.Length));

    public void FourCc(string type)
    {
        if (type.Length != 4)
        {
            throw new ArgumentException("A box type has four characters.", nameof(type));
        }

        Encoding.ASCII.GetBytes(type, Reserve(4));
    }

    /// <summary>Overwrites a 32-bit value at <paramref name="offset"/>.</summary>
    public void PatchU32(int offset, uint v) => BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(offset), v);

    /// <summary>Reserves <paramref name="count"/> bytes and returns them for the caller to fill.</summary>
    public Span<byte> Reserve(int count)
    {
        if (Length + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
        }

        Span<byte> s = _buffer.AsSpan(Length, count);
        Length += count;
        return s;
    }
}
