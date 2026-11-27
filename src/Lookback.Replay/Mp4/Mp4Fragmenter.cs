using Lookback.Core.Time;
using Lookback.Replay.Packets;

namespace Lookback.Replay.Mp4;

/// <summary>
/// Writes fragmented MP4 (ISO BMFF with <c>mvex</c>): one init segment for
/// a set of tracks, then self-contained <c>moof</c>+<c>mdat</c> fragments.
/// The disk store writes each fragment as a 2 s segment file; a clip is the
/// init segment followed by fragments, which every mainstream player and
/// editor reads. Media time zero is <c>originQpc</c>.
/// </summary>
public sealed class Mp4Fragmenter
{
    private const uint SampleFlagsSync = 0x02000000;     // depends on nothing
    private const uint SampleFlagsNonSync = 0x01010000;  // depends on others, non-sync

    private readonly TrackInfo[] _tracks;
    private readonly long _originQpc;
    private readonly long _frequency;
    private readonly BoxWriter _w = new(1 << 16);

    public Mp4Fragmenter(IEnumerable<TrackInfo> tracks, long originQpc, long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        _tracks = [.. tracks.OrderBy(t => t.Id)];
        if (_tracks.Length == 0)
        {
            throw new ArgumentException("At least one track is required.", nameof(tracks));
        }

        foreach (TrackInfo t in _tracks)
        {
            if (t.Codec == CodecKind.Av1)
            {
                throw new NotSupportedException("AV1 muxing is not implemented yet; the governor does not pick AV1 until it is.");
            }

            if (t.CodecConfig.Length == 0)
            {
                throw new ArgumentException($"Track {t.Id} has no codec configuration yet.", nameof(tracks));
            }
        }

        _originQpc = originQpc;
        _frequency = qpcFrequency;
    }

    public IReadOnlyList<TrackInfo> Tracks => _tracks;

    public long OriginQpc => _originQpc;

    /// <summary>Converts a QPC time to a track's media time.</summary>
    public long ToMedia(TrackInfo track, long qpc)
    {
        ArgumentNullException.ThrowIfNull(track);
        return QpcTime.Rescale(qpc - _originQpc, _frequency, track.Timescale);
    }

    public byte[] InitSegment()
    {
        BoxWriter w = _w;
        w.Clear();
        w.Begin("ftyp");
        w.FourCc("isom");
        w.U32(0x200);
        w.FourCc("isom");
        w.FourCc("iso6");
        w.FourCc("mp41");
        foreach (TrackInfo t in _tracks)
        {
            w.FourCc(t.Codec switch
            {
                CodecKind.H264 => "avc1",
                CodecKind.Hevc => "hvc1",
                _ => "mp41",
            });
        }

        w.End();

        w.Begin("moov");
        w.BeginFull("mvhd", 0, 0);
        w.U32(0); // creation time
        w.U32(0); // modification time
        w.U32(1000);
        w.U32(0); // duration: unknown, fragments carry it
        w.U32(0x00010000); // rate 1.0
        w.U16(0x0100);     // volume 1.0
        w.Zeros(10);
        WriteMatrix(w);
        w.Zeros(24);
        w.U32((uint)(_tracks[^1].Id + 1));
        w.End();

        foreach (TrackInfo t in _tracks)
        {
            WriteTrak(w, t);
        }

        w.Begin("mvex");
        foreach (TrackInfo t in _tracks)
        {
            w.BeginFull("trex", 0, 0);
            w.U32((uint)t.Id);
            w.U32(1); // sample description index
            w.U32(0);
            w.U32(0);
            w.U32(0);
            w.End();
        }

        w.End();
        w.End(); // moov
        return w.ToArray();
    }

    /// <summary>
    /// Builds one <c>moof</c>+<c>mdat</c> from packets of any of the tracks.
    /// Packets are grouped per track and ordered by decode time.
    /// </summary>
    public byte[] Fragment(uint sequence, IReadOnlyList<EncodedPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        if (packets.Count == 0)
        {
            throw new ArgumentException("A fragment needs at least one packet.", nameof(packets));
        }

        var perTrack = new List<(TrackInfo Track, List<EncodedPacket> Packets)>();
        foreach (TrackInfo t in _tracks)
        {
            List<EncodedPacket> list = [.. packets.Where(p => p.TrackId == t.Id).OrderBy(p => p.DtsQpc)];
            if (list.Count > 0)
            {
                perTrack.Add((t, list));
            }
        }

        if (perTrack.Sum(p => p.Packets.Count) != packets.Count)
        {
            throw new ArgumentException("A packet belongs to a track this fragmenter does not carry.", nameof(packets));
        }

        BoxWriter w = _w;
        w.Clear();
        int moofStart = w.Begin("moof");
        w.BeginFull("mfhd", 0, 0);
        w.U32(sequence);
        w.End();

        var dataOffsetSlots = new int[perTrack.Count];
        for (int ti = 0; ti < perTrack.Count; ti++)
        {
            (TrackInfo track, List<EncodedPacket> list) = perTrack[ti];
            w.Begin("traf");
            w.BeginFull("tfhd", 0, 0x020000); // default-base-is-moof
            w.U32((uint)track.Id);
            w.End();

            long baseTime = ToMedia(track, list[0].DtsQpc);
            w.BeginFull("tfdt", 1, 0);
            w.U64((ulong)Math.Max(0, baseTime));
            w.End();

            // data-offset, duration, size, flags, composition offset (signed, v1)
            w.BeginFull("trun", 1, 0x000001 | 0x000100 | 0x000200 | 0x000400 | 0x000800);
            w.U32((uint)list.Count);
            dataOffsetSlots[ti] = w.Length;
            w.U32(0);
            long fallback = track.IsVideo ? track.Timescale / 30 : AacConfig.FrameSamples;
            for (int i = 0; i < list.Count; i++)
            {
                EncodedPacket p = list[i];
                long dts = ToMedia(track, p.DtsQpc);
                long duration = i + 1 < list.Count
                    ? ToMedia(track, list[i + 1].DtsQpc) - dts
                    : (p.DurationQpc > 0 ? ToMedia(track, p.DtsQpc + p.DurationQpc) - dts : fallback);
                if (duration <= 0)
                {
                    duration = fallback;
                }

                fallback = duration;
                w.U32((uint)duration);
                w.U32((uint)p.Payload.Length);
                w.U32(!track.IsVideo || p.IsKeyframe ? SampleFlagsSync : SampleFlagsNonSync);
                w.I32((int)(ToMedia(track, p.PtsQpc) - dts));
            }

            w.End(); // trun
            w.End(); // traf
        }

        w.End(); // moof
        int moofSize = w.Length - moofStart;

        long mdatSize = 8 + perTrack.Sum(p => p.Packets.Sum(x => (long)x.Payload.Length));
        if (mdatSize > uint.MaxValue)
        {
            throw new InvalidOperationException("Fragment too large.");
        }

        w.U32((uint)mdatSize);
        w.FourCc("mdat");
        int offset = moofSize + 8;
        for (int ti = 0; ti < perTrack.Count; ti++)
        {
            w.PatchU32(dataOffsetSlots[ti], (uint)offset);
            foreach (EncodedPacket p in perTrack[ti].Packets)
            {
                w.Bytes(p.Payload);
                offset += p.Payload.Length;
            }
        }

        return w.ToArray();
    }

    private static void WriteMatrix(BoxWriter w)
    {
        w.U32(0x00010000);
        w.U32(0);
        w.U32(0);
        w.U32(0);
        w.U32(0x00010000);
        w.U32(0);
        w.U32(0);
        w.U32(0);
        w.U32(0x40000000);
    }

    private static void WriteTrak(BoxWriter w, TrackInfo t)
    {
        w.Begin("trak");
        w.BeginFull("tkhd", 0, 0x000003); // enabled, in movie
        w.U32(0);
        w.U32(0);
        w.U32((uint)t.Id);
        w.U32(0);
        w.U32(0); // duration
        w.Zeros(8);
        w.U16(0); // layer
        w.U16(0); // alternate group: none, so the game and mic tracks both stay selectable
        w.U16(t.IsVideo ? (ushort)0 : (ushort)0x0100);
        w.U16(0);
        WriteMatrix(w);
        w.U32(t.IsVideo ? (uint)t.Width << 16 : 0);
        w.U32(t.IsVideo ? (uint)t.Height << 16 : 0);
        w.End();

        w.Begin("mdia");
        w.BeginFull("mdhd", 0, 0);
        w.U32(0);
        w.U32(0);
        w.U32((uint)t.Timescale);
        w.U32(0);
        w.U16(0x55C4); // "und"
        w.U16(0);
        w.End();

        w.BeginFull("hdlr", 0, 0);
        w.U32(0);
        w.FourCc(t.IsVideo ? "vide" : "soun");
        w.Zeros(12);
        w.Bytes(System.Text.Encoding.ASCII.GetBytes(t.IsVideo ? "Lookback video\0" : $"Lookback {t.Role} audio\0"));
        w.End();

        w.Begin("minf");
        if (t.IsVideo)
        {
            w.BeginFull("vmhd", 0, 1);
            w.Zeros(8);
            w.End();
        }
        else
        {
            w.BeginFull("smhd", 0, 0);
            w.Zeros(4);
            w.End();
        }

        w.Begin("dinf");
        w.BeginFull("dref", 0, 0);
        w.U32(1);
        w.BeginFull("url ", 0, 1); // media is in this file
        w.End();
        w.End();
        w.End();

        w.Begin("stbl");
        w.BeginFull("stsd", 0, 0);
        w.U32(1);
        if (t.IsVideo)
        {
            WriteVisualEntry(w, t);
        }
        else
        {
            WriteAudioEntry(w, t);
        }

        w.End(); // stsd
        foreach (string empty in new[] { "stts", "stsc", "stco" })
        {
            w.BeginFull(empty, 0, 0);
            w.U32(0);
            w.End();
        }

        w.BeginFull("stsz", 0, 0);
        w.U32(0);
        w.U32(0);
        w.End();
        w.End(); // stbl
        w.End(); // minf
        w.End(); // mdia
        w.End(); // trak
    }

    private static void WriteVisualEntry(BoxWriter w, TrackInfo t)
    {
        w.Begin(t.Codec == CodecKind.H264 ? "avc1" : "hvc1");
        w.Zeros(6);
        w.U16(1); // data reference index
        w.Zeros(16);
        w.U16((ushort)t.Width);
        w.U16((ushort)t.Height);
        w.U32(0x00480000);
        w.U32(0x00480000);
        w.U32(0);
        w.U16(1); // frame count
        w.Zeros(32); // compressor name
        w.U16(0x0018);
        w.U16(0xFFFF);
        w.Begin(t.Codec == CodecKind.H264 ? "avcC" : "hvcC");
        w.Bytes(t.CodecConfig);
        w.End();
        w.End();
    }

    private static void WriteAudioEntry(BoxWriter w, TrackInfo t)
    {
        w.Begin("mp4a");
        w.Zeros(6);
        w.U16(1);
        w.Zeros(8);
        w.U16((ushort)t.Channels);
        w.U16(16);
        w.U16(0);
        w.U16(0);
        w.U32((uint)t.SampleRate << 16);

        w.BeginFull("esds", 0, 0);
        byte[] asc = t.CodecConfig;
        int decSpecific = 2 + asc.Length;
        int decoderConfig = 2 + 13 + decSpecific;
        int es = 2 + 3 + decoderConfig + 3;
        w.U8(0x03);
        w.U8((byte)(es - 2));
        w.U16(0);   // ES_ID
        w.U8(0);    // flags
        w.U8(0x04);
        w.U8((byte)(decoderConfig - 2));
        w.U8(0x40); // MPEG-4 audio
        w.U8(0x15); // audio stream
        w.U24(0);   // buffer size
        w.U32(0);   // max bitrate
        w.U32(0);   // average bitrate
        w.U8(0x05);
        w.U8((byte)asc.Length);
        w.Bytes(asc);
        w.U8(0x06);
        w.U8(1);
        w.U8(0x02);
        w.End();
        w.End();
    }
}
