using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lookback.Core.Time;
using Lookback.Media.Frames;

namespace Lookback.Media.Ffmpeg;

/// <summary>
/// FileSource backed by an ffmpeg child process. ffmpeg decodes, scales and
/// resamples to the <see cref="AnalysisFormat"/> and streams raw BGRA frames
/// or mono float PCM over stdout; this class turns that into borrowed
/// <see cref="VideoFrame"/>s and <see cref="AudioBlock"/>s with synthetic QPC
/// timestamps. Output is constant frame rate, so frame N is always at
/// N / fps seconds and replays are deterministic.
/// <para>
/// Windows live capture uses a GPU path instead (M3); this decoder is the
/// platform-neutral way to run footage through the same signals.
/// </para>
/// </summary>
public sealed class FfmpegFileSource
{
    private readonly string _path;
    private readonly AnalysisFormat _format;
    private readonly long _frequency;
    private readonly long _startQpc;

    /// <param name="path">Footage file.</param>
    /// <param name="format">Analysis format to convert to.</param>
    /// <param name="frequency">QPC frequency for synthetic timestamps.</param>
    /// <param name="startQpc">Timestamp of the first frame and first sample.</param>
    public FfmpegFileSource(string path, AnalysisFormat? format = null, long frequency = 10_000_000, long startQpc = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _path = path;
        _format = format ?? AnalysisFormat.Default;
        _frequency = frequency;
        _startQpc = startQpc;
    }

    public AnalysisFormat Format => _format;

    public async IAsyncEnumerable<VideoFrame> ReadVideoAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int w = _format.Width, h = _format.Height;
        string filter = string.Create(
            CultureInfo.InvariantCulture,
            $"fps={_format.FramesPerSecond},scale={w}:{h}:force_original_aspect_ratio=decrease:flags=area,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,format=bgra");

        using var lease = new ProcessLease(Start(["-i", _path, "-map", "0:v:0", "-vf", filter, "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"]));
        Process process = lease.Process;
        using CancellationTokenRegistration reg = KillOnCancel(process, cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        Stream stdout = process.StandardOutput.BaseStream;

        var frame = new VideoFrame(w, h);
        long index = 0;
        while (true)
        {
            int read = await stdout.ReadAtLeastAsync(frame.Buffer, frame.Buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (read < frame.Buffer.Length)
            {
                break; // end of stream; a partial trailing frame is discarded
            }

            frame.Index = index;
            frame.Qpc = _startQpc + QpcTime.Rescale(index, _format.FramesPerSecond, _frequency);
            yield return frame;
            index++;
        }

        await FinishAsync(process, stderr, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AudioBlock> ReadAudioAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string rate = _format.SampleRate.ToString(CultureInfo.InvariantCulture);
        using var lease = new ProcessLease(Start(["-i", _path, "-map", "0:a:0?", "-ac", "1", "-ar", rate, "-f", "f32le", "-acodec", "pcm_f32le", "pipe:1"]));
        Process process = lease.Process;
        using CancellationTokenRegistration reg = KillOnCancel(process, cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        Stream stdout = process.StandardOutput.BaseStream;

        var block = new AudioBlock(_format.SampleRate, _format.HopSamples);
        byte[] bytes = new byte[_format.HopSamples * sizeof(float)];
        long sample = 0;
        while (true)
        {
            int read = await stdout.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            int samples = read / sizeof(float);
            if (samples == 0)
            {
                break;
            }

            MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, samples * sizeof(float))).CopyTo(block.Buffer);
            block.Count = samples;
            block.FirstSample = sample;
            block.Qpc = _startQpc + QpcTime.Rescale(sample, _format.SampleRate, _frequency);
            yield return block;
            sample += samples;

            if (read < bytes.Length)
            {
                break;
            }
        }

        await FinishAsync(process, stderr, cancellationToken).ConfigureAwait(false);
    }

    private static Process Start(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(FfmpegLocator.RequireFfmpeg())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // so ffmpeg never waits on the console
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin" })
        {
            psi.ArgumentList.Add(a);
        }

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a); // ArgumentList quotes each argument; no shell, no injection
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
    }

    private static CancellationTokenRegistration KillOnCancel(Process process, CancellationToken ct) =>
        ct.Register(static p =>
        {
            try
            {
                ((Process)p!).Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
        }, process);

    private static async Task FinishAsync(Process process, Task<string> stderr, CancellationToken ct)
    {
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"ffmpeg exited with {process.ExitCode}: {(await stderr.ConfigureAwait(false)).Trim()}");
        }
    }

    /// <summary>Kills ffmpeg if the consumer stops early, so no decoder outlives its reader.</summary>
    private sealed class ProcessLease(Process process) : IDisposable
    {
        public Process Process { get; } = process;

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            Process.Dispose();
        }
    }
}
