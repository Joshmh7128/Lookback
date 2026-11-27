using System.Text.Json;
using System.Text.Json.Serialization;
using Lookback.Contracts.V1;
using Lookback.Replay.Packets;

namespace Lookback.Replay.Store;

/// <summary>
/// <c>session.json</c>: what a recorded session is, how its media maps to
/// QPC, and which ranges saved clips pinned. Wall-clock times here are only
/// for retention bookkeeping (when to clean up), never for event time.
/// </summary>
public sealed class SessionManifest
{
    public const string FileName = "session.json";

    public required string Id { get; init; }

    public string Title { get; init; } = "";

    public SourceKind SourceKind { get; init; }

    public Retention Retention { get; init; }

    public string PermissionRef { get; init; } = "";

    public string GameProfile { get; init; } = "";

    public long QpcFrequency { get; init; }

    /// <summary>QPC time of media time zero in every segment.</summary>
    public long OriginQpc { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? EndedUtc { get; set; }

    /// <summary>The user chose to keep the whole session; the sweeper leaves it alone.</summary>
    public bool Keep { get; set; }

    public List<RunInfo> Runs { get; init; } = [];

    public List<QpcRange> Pins { get; init; } = [];

    public SessionStarted ToSessionStarted() => new()
    {
        SourceKind = SourceKind,
        Retention = Retention,
        PermissionRef = PermissionRef,
        Title = Title,
        QpcFrequency = (ulong)QpcFrequency,
        GameProfile = GameProfile,
    };

    public static SessionManifest Load(string directory)
    {
        using FileStream s = File.OpenRead(Path.Combine(directory, FileName));
        return JsonSerializer.Deserialize(s, StoreJson.Default.SessionManifest)
            ?? throw new InvalidDataException($"Empty manifest in {directory}.");
    }

    /// <summary>Writes atomically (temp file, then replace), so a crash never leaves half a manifest.</summary>
    public void Save(string directory)
    {
        string path = Path.Combine(directory, FileName);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(this, StoreJson.Default.SessionManifest));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>A stretch of a session with one set of track formats and one init segment.</summary>
public sealed class RunInfo
{
    public int Run { get; init; }

    public string InitFile { get; init; } = "";

    public List<TrackRecord> Tracks { get; init; } = [];
}

/// <summary>Serializable <see cref="TrackInfo"/>.</summary>
public sealed class TrackRecord
{
    public int Id { get; init; }

    public CodecKind Codec { get; init; }

    public int Timescale { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    public string Role { get; init; } = "";

    public byte[] CodecConfig { get; init; } = [];

    public static TrackRecord From(TrackInfo t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new()
        {
            Id = t.Id,
            Codec = t.Codec,
            Timescale = t.Timescale,
            Width = t.Width,
            Height = t.Height,
            SampleRate = t.SampleRate,
            Channels = t.Channels,
            Role = t.Role,
            CodecConfig = t.CodecConfig,
        };
    }

    public TrackInfo ToTrackInfo() => new()
    {
        Id = Id,
        Codec = Codec,
        Timescale = Timescale,
        Width = Width,
        Height = Height,
        SampleRate = SampleRate,
        Channels = Channels,
        Role = Role,
        CodecConfig = CodecConfig,
    };
}

/// <summary>A half-open QPC range [Start, End).</summary>
public readonly record struct QpcRange(long Start, long End)
{
    public bool Overlaps(long start, long end) => Start < end && start < End;
}

/// <summary>One line of <c>segments.jsonl</c>.</summary>
public sealed record SegmentRecord(int Run, int Seq, string File, long StartQpc, long EndQpc, long Bytes);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionManifest))]
internal sealed partial class StoreJson : JsonSerializerContext;

/// <summary>One-line JSON for <c>segments.jsonl</c>; indentation would break lines (and on Windows add CR).</summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SegmentRecord))]
internal sealed partial class StoreLineJson : JsonSerializerContext;
