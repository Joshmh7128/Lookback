using System.Text.Json;
using Lookback.Replay.Packets;
using Vortice.Direct3D11;

namespace Lookback.Encode;

/// <summary>One encoder MFT the fallback chain can try: its codec, friendly name, and whether it is a hardware MFT.</summary>
public sealed record EncoderCandidate(CodecKind Codec, string Name, bool Hardware)
{
    public override string ToString() => $"{Name} ({Codec}, {(Hardware ? "hardware" : "software")})";
}

/// <summary>
/// A video encoder the <see cref="EncodeTicker"/> drives. <see cref="MfVideoEncoder"/>
/// is the real one; tests supply fakes that hang or refuse input on purpose.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    EncoderCandidate Candidate { get; }

    VideoEncoderSettings Settings { get; }

    long FramesIn { get; }

    long PacketsOut { get; }

    long Dropped { get; }

    /// <summary>Backlog over the input pool, for the governor.</summary>
    double Load { get; }

    /// <summary>What the encoder's own threads are doing, for stall reports.</summary>
    string Phase { get; }

    /// <summary>Encodes one NV12 frame (on the capture device) at <paramref name="qpc"/>; false if it had no room.</summary>
    bool Encode(ID3D11Texture2D nv12, long qpc);

    void ForceKeyframe();

    /// <summary>
    /// Stops using the encoder without waiting on it: its threads may be stuck
    /// in the driver. Releases it on a background thread once no call is in
    /// flight, or never, if a call stays stuck. Never blocks.
    /// </summary>
    void Abandon();
}

/// <summary>
/// Encoders that stalled on this machine, keyed by adapter identity (vendor,
/// device, driver version) and MFT name, so the next run skips straight to the
/// fallback instead of stalling again. This is device compatibility data, not
/// session data; it holds no frames, audio or times.
/// </summary>
public sealed class EncoderBlocklist
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string? _path;
    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];

    /// <param name="path">Where to keep it; null keeps it in memory only.</param>
    public EncoderBlocklist(string? path)
    {
        _path = path;
        if (path is not null && File.Exists(path))
        {
            try
            {
                _entries.AddRange(JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? []);
            }
            catch (JsonException)
            {
                // A damaged file only costs a retry of an encoder that may stall again.
            }
        }
    }

    /// <summary><c>%LOCALAPPDATA%\Lookback\encoder-blocklist.json</c>.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lookback", "encoder-blocklist.json");

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public bool IsBlocked(string adapter, EncoderCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_lock)
        {
            return _entries.Exists(e => e.Adapter == adapter && e.Encoder == candidate.Name && e.Codec == candidate.Codec);
        }
    }

    public void Add(string adapter, EncoderCandidate candidate, string reason)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_lock)
        {
            if (_entries.Exists(e => e.Adapter == adapter && e.Encoder == candidate.Name && e.Codec == candidate.Codec))
            {
                return;
            }

            _entries.Add(new Entry(adapter, candidate.Name, candidate.Codec, reason, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
            if (_path is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (IOException)
            {
                // Remembered for this run only.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>One remembered stall. <see cref="AddedUtc"/> is wall-clock time for people reading the file, not event time.</summary>
    public sealed record Entry(string Adapter, string Encoder, CodecKind Codec, string Reason, string AddedUtc);
}
