using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Lookback.Media.Ffmpeg;

/// <summary>What ffprobe reports about a footage file.</summary>
public sealed record MediaInfo(TimeSpan Duration, int Width, int Height, double FramesPerSecond, bool HasVideo, bool HasAudio);

/// <summary>Reads stream information with ffprobe.</summary>
public static class MediaProbe
{
    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Footage file not found.", path);
        }

        var psi = new ProcessStartInfo(FfmpegLocator.RequireFfprobe())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path })
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffprobe.");
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        string json = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"ffprobe failed: {(await stderr.ConfigureAwait(false)).Trim()}");
        }

        return Parse(json);
    }

    internal static MediaInfo Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        double seconds = 0;
        if (root.TryGetProperty("format", out JsonElement format)
            && format.TryGetProperty("duration", out JsonElement d)
            && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fd))
        {
            seconds = fd;
        }

        int width = 0, height = 0;
        double fps = 0;
        bool hasVideo = false, hasAudio = false;
        if (root.TryGetProperty("streams", out JsonElement streams))
        {
            foreach (JsonElement s in streams.EnumerateArray())
            {
                string? type = s.TryGetProperty("codec_type", out JsonElement t) ? t.GetString() : null;
                if (type == "video" && !hasVideo)
                {
                    hasVideo = true;
                    width = s.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0;
                    height = s.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : 0;
                    fps = s.TryGetProperty("avg_frame_rate", out JsonElement r) ? ParseRate(r.GetString()) : 0;
                }
                else if (type == "audio")
                {
                    hasAudio = true;
                }
            }
        }

        return new MediaInfo(TimeSpan.FromSeconds(seconds), width, height, fps, hasVideo, hasAudio);
    }

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate))
        {
            return 0;
        }

        string[] parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den)
            && den != 0)
        {
            return num / den;
        }

        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }
}
