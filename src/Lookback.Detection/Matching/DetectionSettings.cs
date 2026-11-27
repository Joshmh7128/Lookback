using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lookback.Detection.Matching;

/// <summary>
/// The user's choices about which programs are games. These are settings,
/// not session data: they hold executable names or paths and profile ids,
/// never anything seen or heard during a session.
/// <list type="bullet">
/// <item><see cref="Allow"/> opts an executable in: one no profile lists, or
/// one several profiles list (ambiguous). It is recorded with the profile
/// named, <c>generic</c> by default.</item>
/// <item><see cref="Deny"/> keeps an executable out, even one a profile lists.</item>
/// </list>
/// An entry is a file name (<c>MyGame.exe</c>, any folder) or a full path
/// (that copy only). Matching ignores case.
/// </summary>
public sealed record DetectionSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public IReadOnlyList<AllowEntry> Allow { get; init; } = [];

    public IReadOnlyList<string> Deny { get; init; } = [];

    /// <summary><c>%LOCALAPPDATA%\Lookback\detection.json</c> (the platform's local application data folder elsewhere).</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "Lookback",
        "detection.json");

    /// <summary>The settings in <paramref name="path"/>, or empty settings when the file does not exist.</summary>
    public static DetectionSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return new DetectionSettings();
        }

        try
        {
            DetectionSettings s = JsonSerializer.Deserialize<DetectionSettings>(File.ReadAllText(path), Json) ?? new DetectionSettings();
            IReadOnlyList<string> problems = s.Validate();
            return problems.Count == 0 ? s : throw new InvalidDataException($"{path} is invalid:\n  " + string.Join("\n  ", problems));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }

    /// <summary>Writes the settings, replacing the file in one step.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        foreach (AllowEntry a in Allow)
        {
            if (string.IsNullOrWhiteSpace(a.Exe))
            {
                problems.Add("allow: an entry has no exe.");
            }

            if (string.IsNullOrWhiteSpace(a.Profile))
            {
                problems.Add($"allow: '{a.Exe}' has an empty profile.");
            }
        }

        if (Deny.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("deny: an entry is empty.");
        }

        return problems;
    }

    /// <summary>These settings with <paramref name="exe"/> opted in (and no longer denied).</summary>
    public DetectionSettings WithAllowed(string exe, string profile = "generic") => this with
    {
        Allow = [.. Allow.Where(a => !Same(a.Exe, exe)), new AllowEntry { Exe = exe, Profile = profile }],
        Deny = [.. Deny.Where(d => !Same(d, exe))],
    };

    /// <summary>These settings with <paramref name="exe"/> kept out (and no longer allowed).</summary>
    public DetectionSettings WithDenied(string exe) => this with
    {
        Allow = [.. Allow.Where(a => !Same(a.Exe, exe))],
        Deny = [.. Deny.Where(d => !Same(d, exe)), exe],
    };

    /// <summary>The allow entry that covers this process, if any. A full-path entry wins over a name entry.</summary>
    public AllowEntry? FindAllowed(string exeName, string? exePath) =>
        Allow.FirstOrDefault(a => IsPath(a.Exe) && exePath is not null && Same(a.Exe, exePath))
        ?? Allow.FirstOrDefault(a => !IsPath(a.Exe) && Same(a.Exe, exeName));

    public bool IsDenied(string exeName, string? exePath) =>
        Deny.Any(d => IsPath(d) ? exePath is not null && Same(d, exePath) : Same(d, exeName));

    /// <summary>True when an entry names a file name (not a path) equal to <paramref name="exeName"/>; used to decide which processes need their path looked up.</summary>
    internal bool MentionsName(string exeName) =>
        Allow.Any(a => Same(FileName(a.Exe), exeName)) || Deny.Any(d => Same(FileName(d), exeName));

    internal static string FileName(string exe) => exe[(exe.LastIndexOfAny(['\\', '/']) + 1)..];

    private static bool IsPath(string exe) => exe.IndexOfAny(['\\', '/']) >= 0;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>An executable the user opted in, and the profile to run for it.</summary>
public sealed record AllowEntry
{
    public required string Exe { get; init; }

    public string Profile { get; init; } = "generic";
}
