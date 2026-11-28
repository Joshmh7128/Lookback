using System.Globalization;
using System.Text;
using Lookback.Calibration.Profiles;
using Lookback.Profiles.Model;
using Microsoft.Extensions.Logging;

namespace Lookback.Recorder;

/// <summary>
/// The profiles live recording and footage analysis run on. With
/// <see cref="RecorderOptions.ProfilesDirectory"/> set (the desktop app passes
/// the checkout's <c>profiles/</c>), the catalog is loaded from that folder,
/// checked the way the profile editor checks a save
/// (<see cref="ProfileFolder.Load"/>), and reloaded whenever a file in it
/// changes, so an edit applies from the next recording or analysis without a
/// rebuild. If the folder does not load, the built-in profiles (embedded at
/// build time) are used and the problem is logged once. Unset, the built-in
/// profiles are used.
/// </summary>
internal sealed partial class ProfileSource(string? directory, ILogger logger)
{
    private readonly Lock _lock = new();
    private string? _stamp;
    private ProfileCatalog _catalog = ProfileCatalog.BuiltIn;
    private string? _lastProblem;

    /// <summary>The built-in profiles only.</summary>
    public static ProfileSource BuiltIn(ILogger logger) => new(null, logger);

    /// <summary>The folder in use, or null for the built-in profiles.</summary>
    public string? Directory { get; } = directory is { Length: > 0 } d ? Path.GetFullPath(d) : null;

    /// <summary>Whether <see cref="Current"/> last came from the folder (false: built-in).</summary>
    public bool FromFolder { get; private set; }

    /// <summary>The catalog to start a session with: the folder's when it loads, else the built-in one.</summary>
    public ProfileCatalog Current()
    {
        if (Directory is null)
        {
            return ProfileCatalog.BuiltIn;
        }

        lock (_lock)
        {
            string? stamp = Stamp(Directory);
            if (stamp is not null && stamp == _stamp)
            {
                return _catalog;
            }

            _stamp = stamp;
            try
            {
                if (stamp is null)
                {
                    throw new DirectoryNotFoundException($"The profiles folder {Directory} cannot be read.");
                }

                _catalog = ProfileFolder.Load(Directory);
                FromFolder = true;
                _lastProblem = null;
                int count = _catalog.All.Count();
                LogLoaded(logger, count, Directory);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _catalog = ProfileCatalog.BuiltIn;
                FromFolder = false;
                if (ex.Message != _lastProblem)
                {
                    _lastProblem = ex.Message;
                    LogFallback(logger, Directory, ex.Message);
                }
            }

            return _catalog;
        }
    }

    // Every file's path, size and write time: any change to the folder changes this.
    private static string? Stamp(string dir)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (FileInfo f in new DirectoryInfo(dir).EnumerateFiles("*.json", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.Ordinal))
            {
                sb.Append(f.FullName).Append('|').Append(f.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Profiles: {Count} loaded from {Directory}")]
    private static partial void LogLoaded(ILogger logger, int count, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Profiles in {Directory} do not load, using the built-in profiles instead: {Problem}")]
    private static partial void LogFallback(ILogger logger, string directory, string problem);
}
