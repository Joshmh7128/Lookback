namespace Lookback.Media.Ffmpeg;

/// <summary>
/// Finds the ffmpeg, ffprobe and ffplay executables: <c>LOOKBACK_FFMPEG_DIR</c> first,
/// then PATH. Lookback runs them as separate processes and never links them.
/// </summary>
public static class FfmpegLocator
{
    public static string? FindFfmpeg() => Find("ffmpeg");

    public static string? FindFfprobe() => Find("ffprobe");

    /// <summary>ffmpeg's player, used only to show a clip to someone rating it.</summary>
    public static string? FindFfplay() => Find("ffplay");

    public static string RequireFfmpeg() =>
        FindFfmpeg() ?? throw new FileNotFoundException("ffmpeg was not found. Install it or set LOOKBACK_FFMPEG_DIR.");

    public static string RequireFfprobe() =>
        FindFfprobe() ?? throw new FileNotFoundException("ffprobe was not found. Install it or set LOOKBACK_FFMPEG_DIR.");

    private static string? Find(string tool)
    {
        string exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;

        string? dir = Environment.GetEnvironmentVariable("LOOKBACK_FFMPEG_DIR");
        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, exe)))
        {
            return Path.Combine(dir, exe);
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
        {
            return null;
        }

        foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(entry, exe);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
