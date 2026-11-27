using Lookback.Replay.Packets;

namespace Lookback.Replay.Mp4;

/// <summary>
/// Muxes a <see cref="ClipSource"/> into one fragmented MP4 file without
/// re-encoding: the init segment, then a fragment per video GOP. Media time
/// zero is the clip's first video keyframe.
/// </summary>
public static class ClipWriter
{
    /// <summary>Returns the clip as bytes. Callers that write it to disk must have passed <c>RetentionRules</c>.</summary>
    public static byte[] Mux(ClipSource clip, long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Packets.Count == 0)
        {
            throw new ArgumentException("The clip has no packets.", nameof(clip));
        }

        HashSet<int> used = [.. clip.Packets.Select(p => p.TrackId)];
        TrackInfo[] tracks = [.. clip.Tracks.Where(t => used.Contains(t.Id))];
        HashSet<int> video = [.. tracks.Where(t => t.IsVideo).Select(t => t.Id)];
        long origin = clip.Packets.Where(p => video.Contains(p.TrackId)).Select(p => p.DtsQpc).DefaultIfEmpty(clip.Packets.Min(p => p.DtsQpc)).Min();

        var fragmenter = new Mp4Fragmenter(tracks, origin, qpcFrequency);
        using var output = new MemoryStream();
        output.Write(fragmenter.InitSegment());

        // Cut fragments at video keyframes; audio goes with the GOP it starts in.
        List<long> cuts = [.. clip.Packets.Where(p => video.Contains(p.TrackId) && p.IsKeyframe).Select(p => p.DtsQpc).Distinct().Order()];
        if (cuts.Count == 0)
        {
            cuts.Add(origin);
        }

        uint sequence = 1;
        for (int i = 0; i < cuts.Count; i++)
        {
            long from = i == 0 ? long.MinValue : cuts[i];
            long to = i + 1 < cuts.Count ? cuts[i + 1] : long.MaxValue;
            List<EncodedPacket> group = [.. clip.Packets.Where(p => p.DtsQpc >= from && p.DtsQpc < to && (video.Contains(p.TrackId) || p.PtsQpc >= origin))];
            if (group.Count > 0)
            {
                output.Write(fragmenter.Fragment(sequence++, group));
            }
        }

        return output.ToArray();
    }
}
