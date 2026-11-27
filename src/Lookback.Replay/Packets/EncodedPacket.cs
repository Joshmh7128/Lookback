namespace Lookback.Replay.Packets;

/// <summary>
/// Timing and identity of one encoded access unit. Times are raw QPC ticks
/// from the session's clock, like every other timestamp in Lookback. The
/// payload travels separately (a span on the hot path, an array on the clip
/// path) so this struct stays unmanaged and can live in native rings.
/// </summary>
public readonly record struct PacketInfo(int TrackId, long PtsQpc, long DtsQpc, long DurationQpc, bool IsKeyframe);

/// <summary>A packet with an owned payload, used off the hot path (clip cutting, segment reading).</summary>
public sealed record EncodedPacket(PacketInfo Info, byte[] Payload)
{
    public int TrackId => Info.TrackId;

    public long PtsQpc => Info.PtsQpc;

    public long DtsQpc => Info.DtsQpc;

    public long DurationQpc => Info.DurationQpc;

    public bool IsKeyframe => Info.IsKeyframe;
}

/// <summary>Receives encoded packets from an encoder. Payloads are borrowed for the duration of the call.</summary>
public interface IPacketSink
{
    /// <summary>Called when a track's format (or codec config) is known or changes; always before its first packet.</summary>
    void OnTrack(TrackInfo track);

    /// <summary>
    /// One access unit, already in MP4 form (length-prefixed NAL units for
    /// H.264/HEVC, a raw AAC frame for audio). Must not block.
    /// </summary>
    void OnPacket(in PacketInfo info, ReadOnlySpan<byte> payload);
}
