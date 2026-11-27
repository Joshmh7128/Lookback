using System.Diagnostics;
using Lookback.Media.Ffmpeg;

namespace Lookback.Audio.Tests;

internal static class Tools
{
    public static void SkipWithoutFfmpeg() =>
        Assert.SkipWhen(FfmpegLocator.FindFfmpeg() is null || FfmpegLocator.FindFfprobe() is null, "ffmpeg not installed");

    public static string Run(string exe, string args, bool stderr = false)
    {
        bool probe = Path.GetFileNameWithoutExtension(exe) == "ffprobe";
        var psi = new ProcessStartInfo(exe, probe ? args : "-hide_banner " + args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{exe} {args} failed: {err.Result}");
        }

        return stderr ? err.Result : output;
    }

    /// <summary>Power of <paramref name="hz"/> in <paramref name="x"/> (Goertzel), normalised so a full-scale sine gives about 0.25.</summary>
    public static double Goertzel(ReadOnlySpan<float> x, double hz, int rate)
    {
        double w = 2 * Math.PI * hz / rate;
        double c = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        foreach (float v in x)
        {
            double s0 = v + (c * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        double power = (s1 * s1) + (s2 * s2) - (c * s1 * s2);
        return power / ((double)x.Length * x.Length);
    }

    /// <summary>The strongest frequency between <paramref name="lo"/> and <paramref name="hi"/> Hz, in 1 Hz steps.</summary>
    public static double PeakFrequency(ReadOnlySpan<float> x, int rate, double lo, double hi)
    {
        double best = lo, bestPower = -1;
        for (double f = lo; f <= hi; f += 1)
        {
            double p = Goertzel(x, f, rate);
            if (p > bestPower)
            {
                bestPower = p;
                best = f;
            }
        }

        return best;
    }

    /// <summary>
    /// The half-second window of <paramref name="x"/> where <paramref name="hz"/> is
    /// strongest, and that tone's amplitude there (other sounds barely move it).
    /// </summary>
    public static double FindTone(ReadOnlySpan<float> x, double hz, int rate, out int at)
    {
        int window = rate / 2;
        at = 0;
        double best = 0;
        for (int i = 0; i + window <= x.Length; i += rate / 20)
        {
            double p = Goertzel(x.Slice(i, window), hz, rate);
            if (p > best)
            {
                best = p;
                at = i;
            }
        }

        return 2 * Math.Sqrt(best);
    }

    public static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (float v in x)
        {
            sum += v * v;
        }

        return x.Length == 0 ? 0 : Math.Sqrt(sum / x.Length);
    }

    /// <summary>A 48 kHz mono PCM16 WAV of a sine with 50 ms fades, for another process to play.</summary>
    public static void WriteToneWav(string path, double hz, double seconds, double amplitude)
    {
        const int rate = 48_000;
        int frames = (int)(seconds * rate);
        int fade = rate / 20;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + (frames * 2));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(frames * 2);
        for (int i = 0; i < frames; i++)
        {
            double gain = Math.Min(1.0, Math.Min(i, frames - i) / (double)fade);
            w.Write((short)Math.Round(32767 * amplitude * gain * Math.Sin(2 * Math.PI * hz * i / rate)));
        }
    }
}
