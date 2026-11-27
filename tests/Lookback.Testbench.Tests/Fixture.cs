using System.Diagnostics;
using Lookback.Media.Ffmpeg;

namespace Lookback.Testbench.Tests;

/// <summary>
/// Generates a short footage file with ffmpeg's built-in test sources: 3 s of
/// 320x180 video at 25 fps with a 440 Hz tone, and a bright flash at 2 s.
/// </summary>
public sealed class FootageFixture : IDisposable
{
    public FootageFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "lookback-testbench", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        File = Path.Combine(Directory, "fixture clip.mp4"); // space on purpose: exercises escaping

        var psi = new ProcessStartInfo(FfmpegLocator.RequireFfmpeg()) { RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in new[]
        {
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=3",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3",
            "-vf", "eq=brightness='if(between(t,2,2.2),0.6,0)':eval=frame",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", File,
        })
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("Could not generate fixture: " + err);
        }
    }

    public string Directory { get; }

    public string File { get; }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
