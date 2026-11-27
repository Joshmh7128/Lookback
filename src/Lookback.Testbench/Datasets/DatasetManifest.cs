using System.Text.Json;
using System.Text.Json.Serialization;
using Lookback.Contracts.V1;

namespace Lookback.Testbench.Datasets;

/// <summary>
/// A per-game dataset: footage files with their provenance and, optionally,
/// hand-labelled moments to score the profile against. Lives at
/// <c>datasets/&lt;game&gt;/manifest.json</c>; file paths are relative to it.
/// Media files are never committed.
/// </summary>
internal sealed record DatasetManifest
{
    public required string Game { get; init; }

    public IReadOnlyList<DatasetEntry> Entries { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static DatasetManifest Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<DatasetManifest>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"{path}: empty manifest.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }
}

internal sealed record DatasetEntry
{
    public required string File { get; init; }

    /// <summary>Where the footage came from, for the record (for example "own recording").</summary>
    public string Source { get; init; } = "";

    public DatasetRetention Retention { get; init; } = DatasetRetention.Owned;

    /// <summary>Where the permission or licence is recorded; required for permissioned and open-license.</summary>
    public string PermissionRef { get; init; } = "";

    public string? Title { get; init; }

    public IReadOnlyList<Label> Labels { get; init; } = [];
}

/// <summary>A hand-marked moment: event name and time in seconds from the start of the file.</summary>
internal sealed record Label(string Event, double At);

/// <summary>Retention values allowed in a manifest. Ephemeral is excluded: a dataset is stored by definition.</summary>
internal enum DatasetRetention
{
    Owned,
    Permissioned,
    OpenLicense,
}

internal static class DatasetRetentionExtensions
{
    public static Retention ToRetention(this DatasetRetention r) => r switch
    {
        DatasetRetention.Owned => Retention.Owned,
        DatasetRetention.Permissioned => Retention.Permissioned,
        DatasetRetention.OpenLicense => Retention.OpenLicense,
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };
}
