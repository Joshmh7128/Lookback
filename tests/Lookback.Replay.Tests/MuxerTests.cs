using System.Diagnostics;
using System.Globalization;
using Lookback.Media.Ffmpeg;
using Lookback.Replay.Mp4;
using Lookback.Replay.Packets;

namespace Lookback.Replay.Tests;

/// <summary>
/// End to end: ffmpeg encodes a test pattern, the packets go through the
/// ring, the clip is muxed by our fragmenter, and ffprobe/ffmpeg must read
/// it back with the right frame count and no decode errors.
/// </summary>
public class MuxerTests
{
    private const long Freq = 10_000_000;

    [Theory]
    [InlineData(CodecKind.H264)]
    [InlineData(CodecKind.Hevc)]
    public void Clip_from_ring_decodes_cleanly(CodecKind codec)
    {
        string? ffmpeg = FfmpegLocator.FindFfmpeg();
        Assert.SkipWhen(ffmpeg is null || FfmpegLocator.FindFfprobe() is null, "ffmpeg not installed");
        string dir = Directory.CreateTempSubdirectory("lookback-mux-").FullName;
        try
        {
            string es = Path.Combine(dir, codec == CodecKind.H264 ? "v.h264" : "v.hevc");
            string adts = Path.Combine(dir, "a.aac");
            string enc = codec == CodecKind.H264 ? "libx264 -bsf:v h264_metadata=aud=insert" : "libx265 -x265-params log-level=error -bsf:v hevc_metadata=aud=insert";
            Run(ffmpeg!, $"-f lavfi -i testsrc2=size=320x180:rate=30 -t 6 -c:v {enc} -g 30 -bf 0 -pix_fmt yuv420p -f {(codec == CodecKind.H264 ? "h264" : "hevc")} \"{es}\"");
            Run(ffmpeg!, $"-f lavfi -i sine=frequency=440:sample_rate=48000 -ac 2 -t 6 -c:a aac -b:a 128k -f adts \"{adts}\"");

            using var ring = new ReplayRing(1 << 24, 0);
            var video = Feeder.FeedVideo(ring, codec, File.ReadAllBytes(es), 320, 180, Freq);
            int audioFrames = Feeder.FeedAdts(ring, File.ReadAllBytes(adts), Freq);
            Assert.Equal(180, video);
            Assert.True(audioFrames > 250);

            Assert.True(ring.TrySnapshot(2 * Freq, 5 * Freq, out ClipSource clip));
            string clipPath = Path.Combine(dir, "clip.mp4");
            File.WriteAllBytes(clipPath, ClipWriter.Mux(clip, Freq));

            string frames = Run(FfmpegLocator.FindFfprobe()!, $"-v error -select_streams v:0 -count_frames -show_entries stream=nb_read_frames,codec_name -of csv=p=0 \"{clipPath}\"");
            Assert.Contains(codec == CodecKind.H264 ? "h264" : "hevc", frames, StringComparison.Ordinal);
            Assert.Contains("90", frames, StringComparison.Ordinal); // 3 s at 30 fps, starting on a keyframe

            string audio = Run(FfmpegLocator.FindFfprobe()!, $"-v error -select_streams a:0 -show_entries stream=codec_name,sample_rate,channels -of csv=p=0 \"{clipPath}\"");
            Assert.Equal("aac,48000,2", audio.Trim());

            string duration = Run(FfmpegLocator.FindFfprobe()!, $"-v error -show_entries format=duration -of csv=p=0 \"{clipPath}\"");
            Assert.InRange(double.Parse(duration, CultureInfo.InvariantCulture), 2.9, 3.1);

            string decodeErrors = Run(ffmpeg!, $"-v error -i \"{clipPath}\" -f null -", stderr: true);
            Assert.Equal("", decodeErrors.Trim());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Fragments_round_trip_through_the_reader()
    {
        TrackInfo v = TrackInfo.Video(1, CodecKind.H264, 64, 64) with { CodecConfig = [1, 2, 3] };
        TrackInfo a = TrackInfo.AacAudio(2, 48_000, 2);
        var f = new Mp4Fragmenter([v, a], originQpc: 1000, qpcFrequency: Freq);
        var packets = new List<EncodedPacket>();
        for (int i = 0; i < 10; i++)
        {
            long t = 1000 + (i * Freq / 30);
            packets.Add(new EncodedPacket(new PacketInfo(1, t, t, Freq / 30, i == 0), [(byte)i, 9]));
            packets.Add(new EncodedPacket(new PacketInfo(2, t, t, Freq / 30, true), [(byte)(100 + i)]));
        }

        byte[] frag = f.Fragment(7, packets);
        List<FragmentSample> samples = Mp4FragmentReader.ReadSamples(frag);
        Assert.Equal(20, samples.Count);
        FragmentSample s3 = samples.Where(s => s.TrackId == 1).ElementAt(3);
        Assert.Equal(3 * 3000, s3.DecodeTime);
        Assert.False(s3.IsSync);
        Assert.Equal(3, frag[s3.Offset]);
        Assert.True(samples.First(s => s.TrackId == 1).IsSync);
        Assert.Equal(103, frag[samples.Where(s => s.TrackId == 2).ElementAt(3).Offset]);
    }

    internal static string Run(string exe, string args, bool stderr = false)
    {
        var psi = new ProcessStartInfo(exe, "-hide_banner " + args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(exe) == "ffprobe")
        {
            psi.Arguments = args;
        }

        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{exe} {args} failed: {err.Result}");
        }

        return stderr ? err.Result : output;
    }
}

/// <summary>Splits elementary streams into access units and feeds them like an encoder would.</summary>
internal static class Feeder
{
    public static int FeedVideo(ReplayRing ring, CodecKind codec, byte[] es, int w, int h, long freq)
    {
        // Split at access unit delimiters.
        var units = new List<(int Start, int End)>();
        int unitStart = -1;
        int pos = 0;
        foreach ((int at, int nalType) in NalStarts(es, codec))
        {
            bool aud = codec == CodecKind.H264 ? nalType == 9 : nalType == 35;
            if (aud)
            {
                if (unitStart >= 0)
                {
                    units.Add((unitStart, at));
                }

                unitStart = at;
            }

            pos = at;
        }

        units.Add((unitStart, es.Length));
        _ = pos;

        TrackInfo? track = null;
        byte[] buf = new byte[AnnexB.MaxLengthPrefixedSize(es.Length)];
        for (int i = 0; i < units.Count; i++)
        {
            ReadOnlySpan<byte> au = es.AsSpan(units[i].Start, units[i].End - units[i].Start);
            bool key = CodecConfig.IsKeyframe(codec, au);
            if (track is null)
            {
                track = TrackInfo.Video(1, codec, w, h) with { CodecConfig = CodecConfig.FromKeyframe(codec, au)! };
                ring.OnTrack(track);
            }

            int n = AnnexB.ToLengthPrefixed(au, buf);
            long t = i * freq / 30;
            ring.OnPacket(new PacketInfo(1, t, t, freq / 30, key), buf.AsSpan(0, n));
        }

        return units.Count;
    }

    public static int FeedAdts(ReplayRing ring, byte[] adts, long freq)
    {
        ring.OnTrack(TrackInfo.AacAudio(2, 48_000, 2));
        int pos = 0, count = 0;
        while (pos + 7 <= adts.Length)
        {
            bool crc = (adts[pos + 1] & 1) == 0;
            int len = ((adts[pos + 3] & 3) << 11) | (adts[pos + 4] << 3) | (adts[pos + 5] >> 5);
            int header = crc ? 9 : 7;
            long t = count * 1024L * freq / 48_000;
            ring.OnPacket(new PacketInfo(2, t, t, 1024L * freq / 48_000, true), adts.AsSpan(pos + header, len - header));
            pos += len;
            count++;
        }

        return count;
    }

    private static IEnumerable<(int At, int Type)> NalStarts(byte[] es, CodecKind codec)
    {
        for (int i = 0; i + 3 < es.Length; i++)
        {
            if (es[i] == 0 && es[i + 1] == 0 && es[i + 2] == 1)
            {
                int at = i > 0 && es[i - 1] == 0 ? i - 1 : i;
                byte b = es[i + 3];
                yield return (at, codec == CodecKind.H264 ? b & 0x1F : (b >> 1) & 0x3F);
                i += 2;
            }
        }
    }
}
