using System.Buffers.Binary;
using System.Text;

namespace Lookback.Replay.Mp4;

/// <summary>A sample recovered from a fragment, in its track's media time.</summary>
public readonly record struct FragmentSample(int TrackId, long DecodeTime, long Duration, int CompositionOffset, bool IsSync, int Offset, int Size);

/// <summary>
/// Reads back the fragments <see cref="Mp4Fragmenter"/> writes (and common
/// fMP4 in general): <c>moof</c>/<c>traf</c>/<c>tfhd</c>/<c>tfdt</c>/<c>trun</c>
/// and the matching <c>mdat</c> bytes. Used to cut clips from disk segments
/// without re-encoding.
/// </summary>
public static class Mp4FragmentReader
{
    /// <summary>Lists every sample in <paramref name="data"/>; offsets index into <paramref name="data"/>.</summary>
    public static List<FragmentSample> ReadSamples(ReadOnlySpan<byte> data)
    {
        var samples = new List<FragmentSample>();
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            (long size, string type, int header) = BoxHeader(data, pos);
            if (size < header || pos + size > data.Length)
            {
                throw new InvalidDataException($"Bad box '{type}' at {pos}.");
            }

            if (type == "moof")
            {
                ReadMoof(data.Slice(pos, (int)size), pos, header, samples);
            }

            pos += (int)size;
        }

        return samples;
    }

    /// <summary>Lists the top-level boxes as (type, offset, size). For tests and diagnostics.</summary>
    public static List<(string Type, int Offset, int Size)> TopLevelBoxes(ReadOnlySpan<byte> data)
    {
        var boxes = new List<(string, int, int)>();
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            (long size, string type, _) = BoxHeader(data, pos);
            if (size < 8 || pos + size > data.Length)
            {
                throw new InvalidDataException($"Bad box '{type}' at {pos}.");
            }

            boxes.Add((type, pos, (int)size));
            pos += (int)size;
        }

        return boxes;
    }

    private static void ReadMoof(ReadOnlySpan<byte> moof, int moofOffset, int header, List<FragmentSample> samples)
    {
        int pos = header;
        while (pos + 8 <= moof.Length)
        {
            (long size, string type, int h) = BoxHeader(moof, pos);
            if (type == "traf")
            {
                ReadTraf(moof.Slice(pos + h, (int)size - h), moofOffset, samples);
            }

            pos += (int)size;
        }
    }

    private static void ReadTraf(ReadOnlySpan<byte> traf, int moofOffset, List<FragmentSample> samples)
    {
        int trackId = 0;
        long baseOffset = moofOffset;
        uint defaultDuration = 0, defaultSize = 0, defaultFlags = 0;
        long decodeTime = 0;
        int pos = 0;
        while (pos + 8 <= traf.Length)
        {
            (long size, string type, int h) = BoxHeader(traf, pos);
            ReadOnlySpan<byte> body = traf.Slice(pos + h, (int)size - h);
            switch (type)
            {
                case "tfhd":
                {
                    uint flags = BinaryPrimitives.ReadUInt32BigEndian(body) & 0xFFFFFF;
                    trackId = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    int p = 8;
                    if ((flags & 0x1) != 0)
                    {
                        baseOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(body[p..]);
                        p += 8;
                    }

                    if ((flags & 0x2) != 0)
                    {
                        p += 4;
                    }

                    if ((flags & 0x8) != 0)
                    {
                        defaultDuration = BinaryPrimitives.ReadUInt32BigEndian(body[p..]);
                        p += 4;
                    }

                    if ((flags & 0x10) != 0)
                    {
                        defaultSize = BinaryPrimitives.ReadUInt32BigEndian(body[p..]);
                        p += 4;
                    }

                    if ((flags & 0x20) != 0)
                    {
                        defaultFlags = BinaryPrimitives.ReadUInt32BigEndian(body[p..]);
                    }

                    break;
                }

                case "tfdt":
                    decodeTime = body[0] == 1
                        ? (long)BinaryPrimitives.ReadUInt64BigEndian(body[4..])
                        : BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    break;

                case "trun":
                {
                    byte version = body[0];
                    uint flags = BinaryPrimitives.ReadUInt32BigEndian(body) & 0xFFFFFF;
                    int count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    int p = 8;
                    long dataOffset = baseOffset;
                    if ((flags & 0x1) != 0)
                    {
                        dataOffset += BinaryPrimitives.ReadInt32BigEndian(body[p..]);
                        p += 4;
                    }

                    uint firstFlags = defaultFlags;
                    bool hasFirst = (flags & 0x4) != 0;
                    if (hasFirst)
                    {
                        firstFlags = BinaryPrimitives.ReadUInt32BigEndian(body[p..]);
                        p += 4;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        uint duration = defaultDuration, sampleSize = defaultSize, sampleFlags = i == 0 && hasFirst ? firstFlags : defaultFlags;
                        int cto = 0;
                        if ((flags & 0x100) != 0)
                        {
                            duration = BinaryPrimitives.ReadUInt32BigEndian(body[p..]);
                            p += 4;
                        }

                        if ((flags & 0x200) != 0)
                        {
                            sampleSize = BinaryPrimitives.ReadUInt32BigEndian(body[p..]);
                            p += 4;
                        }

                        if ((flags & 0x400) != 0)
                        {
                            sampleFlags = BinaryPrimitives.ReadUInt32BigEndian(body[p..]);
                            p += 4;
                        }

                        if ((flags & 0x800) != 0)
                        {
                            cto = version == 0 ? (int)BinaryPrimitives.ReadUInt32BigEndian(body[p..]) : BinaryPrimitives.ReadInt32BigEndian(body[p..]);
                            p += 4;
                        }

                        bool sync = (sampleFlags & 0x00010000) == 0;
                        samples.Add(new FragmentSample(trackId, decodeTime, duration, cto, sync, (int)dataOffset, (int)sampleSize));
                        decodeTime += duration;
                        dataOffset += sampleSize;
                    }

                    break;
                }
            }

            pos += (int)size;
        }
    }

    private static (long Size, string Type, int Header) BoxHeader(ReadOnlySpan<byte> data, int pos)
    {
        long size = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
        string type = Encoding.ASCII.GetString(data.Slice(pos + 4, 4));
        int header = 8;
        if (size == 1)
        {
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(pos + 8)..]);
            header = 16;
        }
        else if (size == 0)
        {
            size = data.Length - pos;
        }

        return (size, type, header);
    }
}
