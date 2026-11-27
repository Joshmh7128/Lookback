using Lookback.Replay.Mp4;
using Lookback.Replay.Packets;

namespace Lookback.Replay.Tests;

public class BitstreamTests
{
    [Fact]
    public void Annex_b_converts_to_length_prefixed()
    {
        byte[] annexB = [0, 0, 0, 1, 0x67, 1, 2, 0, 0, 1, 0x68, 3, 0, 0, 0, 1, 0x65, 4, 5, 6];
        byte[] dst = new byte[AnnexB.MaxLengthPrefixedSize(annexB.Length)];
        int n = AnnexB.ToLengthPrefixed(annexB, dst);
        Assert.Equal(new byte[] { 0, 0, 0, 3, 0x67, 1, 2, 0, 0, 0, 2, 0x68, 3, 0, 0, 0, 4, 0x65, 4, 5, 6 }, dst[..n]);
        Assert.True(CodecConfig.IsKeyframe(CodecKind.H264, dst.AsSpan(0, n)));
    }

    [Fact]
    public void Rbsp_strips_emulation_prevention()
    {
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 2 }, AnnexB.ToRbsp([1, 0, 0, 3, 0, 0, 3, 0, 2]));
    }

    [Fact]
    public void Aac_config_for_48k_stereo()
    {
        Assert.Equal(new byte[] { 0x11, 0x90 }, AacConfig.AudioSpecificConfig(48_000, 2));
    }
}
