using System.Diagnostics;
using System.Globalization;
using Lookback.Capture.Gpu;
using Lookback.Encode;
using Lookback.Media.Ffmpeg;
using Lookback.Replay.Mp4;
using Lookback.Replay.Packets;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Lookback.Capture.Tests;

/// <summary>
/// End to end on the GPU: NV12 frames go through <see cref="MfVideoEncoder"/>
/// into a <see cref="ReplayRing"/>, the clip is muxed by
/// <see cref="ClipWriter"/>, and ffprobe must count every frame, a keyframe
/// every 2 s and no B-frames. On a real adapter the frames come from
/// <see cref="Nv12Converter"/> (BGRA in); WARP has no video processor, so the
/// CI case uploads NV12 built on the CPU and runs the software H.264 MFT.
/// Hardware cases run where a GPU encoder exists (NVENC on AETHER).
/// </summary>
public class EncoderTests(ITestOutputHelper output)
{
    private const long Freq = 10_000_000;
    private const int W = 640, H = 360, Fps = 30, Frames = 120;
    private const int SrcW = 1280, SrcH = 720;

    [Fact]
    public void Software_h264_on_warp_round_trips_through_the_muxer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only");
        SkipWithoutFfmpeg();
        SkipWithoutMediaFoundation();
        using GpuDevice gpu = GpuDevice.CreateWarp();
        EncodeAndCheck(gpu, CodecKind.H264, hardware: false);
    }

    [Fact]
    public void Software_h264_on_the_gpu_reads_converter_output()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only");
        SkipWithoutFfmpeg();
        using GpuDevice? gpu = TryHardwareDevice();
        Assert.SkipWhen(gpu is null, "no hardware adapter");
        EncodeAndCheck(gpu!, CodecKind.H264, hardware: false);
    }

    [Theory]
    [InlineData(CodecKind.H264)]
    [InlineData(CodecKind.Hevc)]
    public void Hardware_encoder_round_trips_through_the_muxer(CodecKind codec)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only");
        SkipWithoutFfmpeg();
        MediaFoundation.Startup();

        // Every adapter that drives a display: AETHER has NVENC on the RTX and AMF on the iGPU.
        int ran = 0;
        foreach (long luid in Targets.CaptureTarget.Outputs().Select(o => o.Luid).Distinct())
        {
            using GpuDevice gpu = GpuDevice.Create(luid);
            if (MfVideoEncoder.List(codec, gpu.Luid, hardware: true).Count == 0)
            {
                output.WriteLine($"no hardware {codec} encoder on {gpu.Name}");
                continue;
            }

            EncodeAndCheck(gpu, codec, hardware: true);
            ran++;
        }

        Assert.SkipWhen(ran == 0, $"no hardware {codec} encoder on any adapter");
    }

    private static GpuDevice? TryHardwareDevice()
    {
        try
        {
            return GpuDevice.Create();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Server SKUs without the Media Foundation feature have no MFTs at all.</summary>
    private static void SkipWithoutMediaFoundation()
    {
        try
        {
            MediaFoundation.Startup();
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            Assert.Skip($"Media Foundation unavailable: {ex.Message}");
        }

        Assert.SkipWhen(MfVideoEncoder.List(CodecKind.H264, luid: null, hardware: false).Count == 0, "no software H.264 MFT");
    }

    private static void SkipWithoutFfmpeg() =>
        Assert.SkipWhen(FfmpegLocator.FindFfmpeg() is null || FfmpegLocator.FindFfprobe() is null, "ffmpeg not installed");

    private void EncodeAndCheck(GpuDevice gpu, CodecKind codec, bool hardware)
    {
        MediaFoundation.Startup();
        using var ring = new ReplayRing(1 << 26, 0);
        using IFrameSource frames = gpu.IsWarp ? new CpuNv12(gpu) : new Converted(gpu);

        var settings = new VideoEncoderSettings(codec, W, H, Fps, 4_000_000) { PreferHardware = hardware };
        long origin = 5 * Freq;
        string name;
        long accepted = 0;
        var wall = Stopwatch.StartNew();
        using (MfVideoEncoder enc = MfVideoEncoder.Create(gpu, settings, ring, trackId: 1, Freq))
        {
            name = enc.Name;
            Assert.Equal(hardware, enc.IsHardware);
            for (int i = 0; i < Frames; i++)
            {
                ID3D11Texture2D nv12 = frames.Next(i);
                long qpc = origin + (i * Freq / Fps);
                var sw = Stopwatch.StartNew();
                while (!enc.Encode(nv12, qpc))
                {
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"{name} stopped accepting input at frame {i}");
                    Thread.Sleep(1);
                }

                accepted++;
            }

            // Dispose drains; it must not hang.
            var drain = Stopwatch.StartNew();
            enc.Dispose();
            Assert.True(drain.Elapsed < TimeSpan.FromSeconds(3), $"{name} took {drain.Elapsed} to drain");
            Assert.Equal(accepted, enc.FramesIn);
            Assert.Equal(enc.FramesIn, enc.PacketsOut);
            output.WriteLine($"{name} on {gpu.Name}: {Frames} frames in {wall.ElapsedMilliseconds} ms, {ring.BytesUsed / 1024} KiB, {enc.Dropped} busy retries");
        }

        Assert.True(ring.TrySnapshot(origin, origin + (Frames * Freq / Fps), out ClipSource clip), $"{name}: ring has no clip");
        string dir = Directory.CreateTempSubdirectory("lookback-enc-").FullName;
        try
        {
            string path = Path.Combine(dir, "clip.mp4");
            File.WriteAllBytes(path, ClipWriter.Mux(clip, Freq));
            string probe = FfmpegLocator.FindFfprobe()!;

            string stream = Run(probe, $"-v error -select_streams v:0 -count_frames -show_entries stream=codec_name,width,height,has_b_frames,nb_read_frames -of csv=p=0 \"{path}\"").Trim();
            string[] f = stream.Split(',');
            Assert.Equal(codec == CodecKind.H264 ? "h264" : "hevc", f[0]);
            Assert.Equal(W.ToString(CultureInfo.InvariantCulture), f[1]);
            Assert.Equal(H.ToString(CultureInfo.InvariantCulture), f[2]);
            Assert.Equal("0", f[3]);
            Assert.Equal(Frames.ToString(CultureInfo.InvariantCulture), f[4]);

            // Keyframes at 0 and 2 s (GOP = 60 frames at 30 fps).
            string keys = Run(probe, $"-v error -select_streams v:0 -skip_frame nokey -show_entries frame=pts_time -of csv=p=0 \"{path}\"");
            double[] keyTimes = [.. keys.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => double.Parse(s.TrimEnd(','), CultureInfo.InvariantCulture))];
            Assert.Equal(2, keyTimes.Length);
            Assert.InRange(keyTimes[0], -0.01, 0.01);
            Assert.InRange(keyTimes[1], 1.99, 2.01);

            string pictTypes = Run(probe, $"-v error -select_streams v:0 -show_entries frame=pict_type -of csv=p=0 \"{path}\"");
            Assert.DoesNotContain("B", pictTypes, StringComparison.Ordinal);

            string errors = Run(FfmpegLocator.FindFfmpeg()!, $"-hide_banner -v error -i \"{path}\" -f null -", stderr: true);
            Assert.Equal("", errors.Trim());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>White bar that moves 8 px per frame over a ramp, so every frame differs.</summary>
    private static byte Luma(int x, int y, int w, int h, int frame)
    {
        int bar = (frame * 8 * w / SrcW) % w;
        return x >= bar && x < bar + (40 * w / SrcW) ? (byte)235 : (byte)(16 + ((x + y) * 200 / (w + h)));
    }

    private static string Run(string exe, string args, bool stderr = false)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{exe} {args} failed: {err.Result}");
        return stderr ? err.Result : output;
    }

    private interface IFrameSource : IDisposable
    {
        ID3D11Texture2D Next(int frame);
    }

    /// <summary>BGRA at 720p through the video processor, as the live path does.</summary>
    private sealed class Converted : IFrameSource
    {
        private readonly GpuDevice _gpu;
        private readonly Nv12Converter _nv12;
        private readonly ID3D11Texture2D _bgra;
        private readonly byte[] _px = new byte[SrcW * SrcH * 4];

        public Converted(GpuDevice gpu)
        {
            _gpu = gpu;
            _nv12 = new Nv12Converter(gpu, W, H, Fps);
            _bgra = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, SrcW, SrcH, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget));
        }

        public ID3D11Texture2D Next(int frame)
        {
            for (int y = 0; y < SrcH; y++)
            {
                for (int x = 0; x < SrcW; x++)
                {
                    int i = ((y * SrcW) + x) * 4;
                    byte v = Luma(x, y, SrcW, SrcH, frame);
                    _px[i] = v;
                    _px[i + 1] = (byte)(v / 2);
                    _px[i + 2] = (byte)(255 - v);
                    _px[i + 3] = 255;
                }
            }

            lock (_gpu.Lock)
            {
                _gpu.Context.UpdateSubresource(_px, _bgra, 0, SrcW * 4, 0);
                _nv12.Convert(_bgra);
            }

            return _nv12.Latest;
        }

        public void Dispose()
        {
            _bgra.Dispose();
            _nv12.Dispose();
        }
    }

    /// <summary>NV12 written on the CPU and uploaded: WARP has no video processor.</summary>
    private sealed class CpuNv12 : IFrameSource
    {
        private readonly GpuDevice _gpu;
        private readonly ID3D11Texture2D _tex;
        private readonly byte[] _px = new byte[W * H * 3 / 2];

        public CpuNv12(GpuDevice gpu)
        {
            _gpu = gpu;
            _tex = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.NV12, W, H, 1, 1, BindFlags.ShaderResource));
        }

        public ID3D11Texture2D Next(int frame)
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    _px[(y * W) + x] = Luma(x, y, W, H, frame);
                }
            }

            _px.AsSpan(W * H).Fill(128);
            lock (_gpu.Lock)
            {
                _gpu.Context.UpdateSubresource(_px, _tex, 0, W, 0);
            }

            return _tex;
        }

        public void Dispose() => _tex.Dispose();
    }
}
