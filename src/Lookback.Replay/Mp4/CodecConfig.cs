using Lookback.Replay.Packets;

namespace Lookback.Replay.Mp4;

/// <summary>
/// Builds MP4 sample-entry configuration records from in-band parameter sets,
/// so the muxer needs nothing from the encoder beyond its bitstream.
/// </summary>
public static class CodecConfig
{
    /// <summary>
    /// Derives <c>avcC</c> or <c>hvcC</c> from the parameter sets in a
    /// keyframe (Annex B or length-prefixed). Returns null if the access unit
    /// carries no complete set.
    /// </summary>
    public static byte[]? FromKeyframe(CodecKind codec, ReadOnlySpan<byte> accessUnit)
    {
        NalEnumerator nals = AnnexB.HasStartCode(accessUnit) ? AnnexB.Nals(accessUnit) : AnnexB.LengthPrefixedNals(accessUnit);
        switch (codec)
        {
            case CodecKind.H264:
            {
                byte[]? sps = null, pps = null;
                foreach (ReadOnlySpan<byte> nal in nals)
                {
                    int type = nal[0] & 0x1F;
                    if (type == 7)
                    {
                        sps ??= nal.ToArray();
                    }
                    else if (type == 8)
                    {
                        pps ??= nal.ToArray();
                    }
                }

                return sps is not null && pps is not null ? AvcConfig.Build(sps, pps) : null;
            }

            case CodecKind.Hevc:
            {
                byte[]? vps = null, sps = null, pps = null;
                foreach (ReadOnlySpan<byte> nal in nals)
                {
                    int type = (nal[0] >> 1) & 0x3F;
                    switch (type)
                    {
                        case 32: vps ??= nal.ToArray(); break;
                        case 33: sps ??= nal.ToArray(); break;
                        case 34: pps ??= nal.ToArray(); break;
                    }
                }

                return vps is not null && sps is not null && pps is not null ? HevcConfig.Build(vps, sps, pps) : null;
            }

            default:
                throw new NotSupportedException($"No in-band configuration for {codec}.");
        }
    }

    /// <summary>True when a length-prefixed or Annex B access unit holds an IDR/IRAP picture.</summary>
    public static bool IsKeyframe(CodecKind codec, ReadOnlySpan<byte> accessUnit)
    {
        NalEnumerator nals = AnnexB.HasStartCode(accessUnit) ? AnnexB.Nals(accessUnit) : AnnexB.LengthPrefixedNals(accessUnit);
        foreach (ReadOnlySpan<byte> nal in nals)
        {
            if (codec == CodecKind.H264 && (nal[0] & 0x1F) == 5)
            {
                return true;
            }

            if (codec == CodecKind.Hevc)
            {
                int type = (nal[0] >> 1) & 0x3F;
                if (type is >= 16 and <= 21)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>AVCDecoderConfigurationRecord (ISO/IEC 14496-15 §5.3.3).</summary>
public static class AvcConfig
{
    public static byte[] Build(ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        if (sps.Length < 4)
        {
            throw new InvalidDataException("SPS too short.");
        }

        byte profile = sps[1];
        var w = new BoxWriter(64);
        w.U8(1);
        w.U8(profile);
        w.U8(sps[2]); // constraint flags
        w.U8(sps[3]); // level
        w.U8(0xFF);   // lengthSizeMinusOne = 3
        w.U8(0xE1);   // one SPS
        w.U16((ushort)sps.Length);
        w.Bytes(sps);
        w.U8(1);
        w.U16((ushort)pps.Length);
        w.Bytes(pps);

        if (profile is 100 or 110 or 122 or 144)
        {
            (int chroma, int lumaDepth, int chromaDepth) = ParseHighProfileFields(sps);
            w.U8((byte)(0xFC | chroma));
            w.U8((byte)(0xF8 | (lumaDepth - 8)));
            w.U8((byte)(0xF8 | (chromaDepth - 8)));
            w.U8(0); // no SPS extensions
        }

        return w.ToArray();
    }

    private static (int Chroma, int LumaDepth, int ChromaDepth) ParseHighProfileFields(ReadOnlySpan<byte> sps)
    {
        byte[] rbsp = AnnexB.ToRbsp(sps[1..]);
        var r = new BitReader(rbsp);
        r.Skip(24); // profile, constraints, level
        r.Ue();     // seq_parameter_set_id
        int chroma = (int)r.Ue();
        if (chroma == 3)
        {
            r.Skip(1);
        }

        int luma = (int)r.Ue() + 8;
        int chromaDepth = (int)r.Ue() + 8;
        return (chroma, luma, chromaDepth);
    }
}

/// <summary>HEVCDecoderConfigurationRecord (ISO/IEC 14496-15 §8.3.3).</summary>
public static class HevcConfig
{
    public static byte[] Build(ReadOnlySpan<byte> vps, ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        SpsFields f = Parse(sps);
        var w = new BoxWriter(128);
        w.U8(1);
        w.U8((byte)((f.ProfileSpace << 6) | (f.Tier << 5) | f.ProfileIdc));
        w.U32(f.CompatibilityFlags);
        w.U16((ushort)(f.ConstraintFlags >> 32));
        w.U32((uint)f.ConstraintFlags);
        w.U8(f.LevelIdc);
        w.U16(0xF000); // min_spatial_segmentation_idc = 0
        w.U8(0xFC);    // parallelismType = 0
        w.U8((byte)(0xFC | f.ChromaFormat));
        w.U8((byte)(0xF8 | (f.LumaDepth - 8)));
        w.U8((byte)(0xF8 | (f.ChromaDepth - 8)));
        w.U16(0);      // avgFrameRate unknown
        w.U8((byte)((f.MaxSubLayers << 3) | (f.TemporalIdNested << 2) | 3)); // constantFrameRate 0, lengthSizeMinusOne 3
        w.U8(3);
        WriteArray(w, 32, vps);
        WriteArray(w, 33, sps);
        WriteArray(w, 34, pps);
        return w.ToArray();
    }

    private static void WriteArray(BoxWriter w, int type, ReadOnlySpan<byte> nal)
    {
        w.U8((byte)(0x80 | type)); // array_completeness = 1
        w.U16(1);
        w.U16((ushort)nal.Length);
        w.Bytes(nal);
    }

    internal readonly record struct SpsFields(
        int ProfileSpace, int Tier, int ProfileIdc, uint CompatibilityFlags, ulong ConstraintFlags, byte LevelIdc,
        int MaxSubLayers, int TemporalIdNested, int ChromaFormat, int LumaDepth, int ChromaDepth, int Width, int Height);

    internal static SpsFields Parse(ReadOnlySpan<byte> sps)
    {
        byte[] rbsp = AnnexB.ToRbsp(sps);
        var r = new BitReader(rbsp);
        r.Skip(16); // NAL header
        r.Skip(4);  // sps_video_parameter_set_id
        int maxSubLayersMinus1 = (int)r.Bits(3);
        int nested = (int)r.Bits(1);

        int space = (int)r.Bits(2);
        int tier = (int)r.Bits(1);
        int profile = (int)r.Bits(5);
        uint compat = r.Bits(32);
        ulong constraints = r.Bits64(48);
        byte level = (byte)r.Bits(8);

        Span<bool> profilePresent = stackalloc bool[8];
        Span<bool> levelPresent = stackalloc bool[8];
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            profilePresent[i] = r.Flag();
            levelPresent[i] = r.Flag();
        }

        if (maxSubLayersMinus1 > 0)
        {
            for (int i = maxSubLayersMinus1; i < 8; i++)
            {
                r.Skip(2);
            }
        }

        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profilePresent[i])
            {
                r.Skip(88);
            }

            if (levelPresent[i])
            {
                r.Skip(8);
            }
        }

        r.Ue(); // sps_seq_parameter_set_id
        int chroma = (int)r.Ue();
        if (chroma == 3)
        {
            r.Skip(1);
        }

        int width = (int)r.Ue();
        int height = (int)r.Ue();
        if (r.Flag())
        {
            // Conformance window: crop in chroma units.
            int left = (int)r.Ue(), right = (int)r.Ue(), top = (int)r.Ue(), bottom = (int)r.Ue();
            int subW = chroma is 1 or 2 ? 2 : 1;
            int subH = chroma == 1 ? 2 : 1;
            width -= subW * (left + right);
            height -= subH * (top + bottom);
        }

        int luma = (int)r.Ue() + 8;
        int chromaDepth = (int)r.Ue() + 8;
        return new SpsFields(space, tier, profile, compat, constraints, level, maxSubLayersMinus1 + 1, nested, chroma, luma, chromaDepth, width, height);
    }
}

/// <summary>AAC-LC AudioSpecificConfig (ISO/IEC 14496-3 §1.6.2.1).</summary>
public static class AacConfig
{
    private static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    public static byte[] AudioSpecificConfig(int sampleRate, int channels)
    {
        int index = Array.IndexOf(Rates, sampleRate);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Not an AAC sampling frequency.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 7);
        const int AacLc = 2;
        int bits = (AacLc << 11) | (index << 7) | (channels << 3);
        return [(byte)(bits >> 8), (byte)bits];
    }

    /// <summary>Samples per AAC-LC access unit.</summary>
    public const int FrameSamples = 1024;
}
