namespace Lookback.Detection.Matching;

/// <summary>
/// Guesses a game's engine from files beside its executable on disk. It
/// never looks inside the running process (no module lists, no memory).
/// </summary>
public static class EngineGuess
{
    /// <summary><c>unity</c>, <c>unreal</c>, or empty.</summary>
    public static string FromPath(string? exePath, Func<string, bool>? fileExists = null, Func<string, bool>? dirExists = null)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return "";
        }

        fileExists ??= File.Exists;
        dirExists ??= Directory.Exists;
        try
        {
            string dir = Path.GetDirectoryName(exePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(exePath);
            if (fileExists(Path.Combine(dir, "UnityPlayer.dll")) || dirExists(Path.Combine(dir, stem + "_Data")))
            {
                return "unity";
            }

            // Unreal ships <Game>/Binaries/Win64/<Game>-Win64-Shipping.exe beside an Engine folder.
            string normalised = exePath.Replace('/', '\\');
            if (normalised.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase)
                && (stem.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase)
                    || dirExists(Path.Combine(dir, "..", "..", "..", "Engine"))))
            {
                return "unreal";
            }
        }
        catch (ArgumentException)
        {
        }

        return "";
    }
}
