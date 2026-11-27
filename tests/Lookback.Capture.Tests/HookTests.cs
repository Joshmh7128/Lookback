using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lookback.Capture.Gpu;
using Lookback.Capture.Hook;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Lookback.Capture.Tests;

/// <summary>
/// Builds native/ once (CMake + MSVC) when its output is missing, so the hook
/// tests run on any Windows machine with Visual Studio; they skip when it
/// cannot be built.
/// </summary>
public sealed class NativeBuild
{
    private static readonly Lazy<(string? Dir, string Error)> Built = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Require()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The present hook is Windows only");
        (string? dir, string error) = Built.Value;
        Assert.SkipWhen(dir is null, error);
        return dir!;
    }

    private static (string?, string) Build()
    {
        if (TestAppAllowlist.FindNativeDirectory() is string existing)
        {
            return (existing, "");
        }

        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "native", "build.ps1")))
            {
                root = d.FullName;
                break;
            }
        }

        if (root is null)
        {
            return (null, "native/build.ps1 not found");
        }

        try
        {
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(root, "native", "build.ps1")}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process p = Process.Start(psi)!;
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromMinutes(6)))
            {
                p.Kill(entireProcessTree: true);
                return (null, "native build timed out");
            }

            string? dir = TestAppAllowlist.FindNativeDirectory();
            return dir is null ? (null, $"native build failed (exit {p.ExitCode}): {stderr.Result}{stdout.Result[^Math.Min(2000, stdout.Result.Length)..]}") : (dir, "");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (null, "could not run the native build: " + e.Message);
        }
    }
}

public class HookProtocolTests
{
    [Fact]
    public unsafe void Layout_matches_the_native_header()
    {
        // native/include/lookback_hook_protocol.h static_asserts the same numbers.
        Assert.Equal(48, sizeof(LbhSlot));
        Assert.Equal(72, sizeof(LbhFrame));
        Assert.Equal(5240, sizeof(LbhHeader));
        LbhHeader h = default;
        byte* b = (byte*)&h;
        Assert.Equal(96, (byte*)&h.ConfigSeq - b);
        Assert.Equal(184, (byte*)Unsafe.AsPointer(ref h.Status[0]) - b);
        Assert.Equal(440, (byte*)Unsafe.AsPointer(ref h.Slots[0]) - b);
        Assert.Equal(632, (byte*)Unsafe.AsPointer(ref h.Ring[0]) - b);
    }
}

public class HookAllowlistTests
{
    [Fact]
    public void Notepad_is_refused_before_anything_starts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        int before = Process.GetProcessesByName("notepad").Length;
        var e = Assert.Throws<HookRefusedException>(() => HookInjector.Launch(new HookLaunchRequest { ExecutablePath = notepad, AllowPresentHook = true }));
        Assert.StartsWith("Refused", e.Message, StringComparison.Ordinal);
        Assert.Equal(before, Process.GetProcessesByName("notepad").Length);

        string dir = NativeBuild.Require();
        Assert.Contains("not a Lookback test app", TestAppAllowlist.Check(notepad, dir, out _), StringComparison.Ordinal);
        Assert.Contains("not a Lookback test app", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_process_the_injector_did_not_start_is_refused()
    {
        string dir = NativeBuild.Require();
        // A genuine test app, but started by someone else: still refused.
        using Process p = Process.Start(new ProcessStartInfo(Path.Combine(dir, TestAppAllowlist.FileName(TestApp.D3D11)), "--frames 600") { UseShellExecute = false })!;
        try
        {
            var e = Assert.Throws<HookRefusedException>(() => HookInjector.Attach(p.Id));
            Assert.Contains("not started by the Lookback injector", e.Message, StringComparison.Ordinal);
            Assert.Throws<HookRefusedException>(() => HookInjector.Attach(Environment.ProcessId));
            Assert.Throws<HookRefusedException>(() => HookInjector.Attach(4)); // System
        }
        finally
        {
            p.Kill();
        }
    }

    [Fact]
    public void A_capture_policy_without_allowPresentHook_is_refused()
    {
        NativeBuild.Require();
        HookLaunchRequest r = HookLaunchRequest.ForTestApp(TestApp.D3D11) with { AllowPresentHook = false };
        var e = Assert.Throws<HookRefusedException>(() => HookInjector.Launch(r));
        Assert.Contains("allowPresentHook = false", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_app_copied_out_of_the_build_output_is_refused()
    {
        string dir = NativeBuild.Require();
        string temp = Directory.CreateTempSubdirectory("lookback-hook-").FullName;
        try
        {
            string copy = Path.Combine(temp, TestAppAllowlist.FileName(TestApp.D3D11));
            File.Copy(Path.Combine(dir, TestAppAllowlist.FileName(TestApp.D3D11)), copy);
            File.Copy(Path.Combine(dir, TestAppAllowlist.HookDllName), Path.Combine(temp, TestAppAllowlist.HookDllName));
            Assert.Contains("not in the native test-app build output", TestAppAllowlist.Check(copy, dir, out _), StringComparison.Ordinal);
            Assert.Throws<HookRefusedException>(() => HookInjector.Launch(new HookLaunchRequest { ExecutablePath = copy, AllowPresentHook = true }));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Only_the_test_apps_carry_the_marker()
    {
        string dir = NativeBuild.Require();
        Assert.True(TestAppAllowlist.HasMarker(Path.Combine(dir, TestAppAllowlist.FileName(TestApp.D3D11))));
        Assert.True(TestAppAllowlist.HasMarker(Path.Combine(dir, TestAppAllowlist.FileName(TestApp.D3D12))));
        Assert.False(TestAppAllowlist.HasMarker(Path.Combine(dir, TestAppAllowlist.HookDllName)));
        Assert.False(TestAppAllowlist.HasMarker(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe")));
    }
}

/// <summary>
/// The GPU hook tests run alone: they drive two processes at 120 fps, which
/// would disturb timing-sensitive tests (EncodeTicker) running beside them.
/// </summary>
[CollectionDefinition(nameof(HookCaptureCollection), DisableParallelization = true)]
public sealed class HookCaptureCollection
{
}

/// <summary>Launches each test app with the hook and checks what arrives. Needs a GPU (skips without one).</summary>
[Collection(nameof(HookCaptureCollection))]
public class HookCaptureTests
{
    private sealed record Received(HookFrameInfo Info, uint Pixels, long Qpc, TransferFunction Transfer);

    /// <summary>The native build plus a hardware adapter (CI runners have only the software one).</summary>
    private static void RequireGpu()
    {
        NativeBuild.Require();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        bool hardware = false;
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 a).Success; i++)
        {
            using (a)
            {
                hardware |= (a.Description1.Flags & AdapterFlags.Software) == 0 && a.Description1.VendorId != 0x1414; // 0x1414: Microsoft Basic Render
            }
        }

        Assert.SkipUnless(hardware, "No hardware GPU: the test apps and the hook need one");
    }

    private static GpuDevice CreateDevice(HookConfig config)
    {
        try
        {
            return GpuDevice.Create(config.AdapterLuid);
        }
        catch (InvalidOperationException e)
        {
            Assert.Skip("No hardware adapter for the hook's device: " + e.Message);
            throw;
        }
    }

    /// <summary>Reads the 32 frame-number blocks the test app draws in its top-left corner.</summary>
    private sealed class PatternReader : IDisposable
    {
        private const int Width = 8 + (32 * 8) + 8;
        private const int Height = 24;
        private readonly GpuDevice _gpu;
        private readonly ID3D11Texture2D _staging;

        public PatternReader(GpuDevice gpu)
        {
            _gpu = gpu;
            _staging = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, Width, Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        }

        public unsafe uint Read(ID3D11Texture2D texture)
        {
            lock (_gpu.Lock)
            {
                _gpu.Context.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, Width, Height, 1));
                MappedSubresource m = _gpu.Context.Map(_staging, 0, MapMode.Read);
                try
                {
                    uint n = 0;
                    byte* row = (byte*)m.DataPointer + (12 * m.RowPitch);
                    for (int i = 0; i < 32; i++)
                    {
                        byte r = row[((8 + (8 * i) + 4) * 4) + 2];
                        n |= (r > 128 ? 1u : 0u) << i;
                    }

                    return n;
                }
                finally
                {
                    _gpu.Context.Unmap(_staging, 0);
                }
            }
        }

        public void Dispose() => _staging.Dispose();
    }

    private static List<Received> Capture(HookedProcess app, HookCaptureSource source, GpuDevice gpu, int frames, TimeSpan timeout, List<string> stopped)
    {
        var got = new List<Received>();
        using var reader = new PatternReader(gpu);
        using var done = new ManualResetEventSlim();
        source.Stopped += (_, why) =>
        {
            lock (stopped)
            {
                stopped.Add(why);
            }

            done.Set();
        };
        source.Start((tex, qpc, transfer) =>
        {
            // No asserts here: this runs on the receiver thread.
            HookFrameInfo info = source.LastFrame;
            lock (got)
            {
                if (got.Count < frames)
                {
                    got.Add(new Received(info, reader.Read(tex), qpc, transfer));
                }

                if (got.Count >= frames)
                {
                    done.Set();
                }
            }
        });
        done.Wait(timeout, TestContext.Current.CancellationToken);
        lock (got)
        {
            return [.. got];
        }
    }

    /// <summary>Checks order, QPC and drop accounting; returns the indices whose pixels do not match their frame number.</summary>
    private static List<int> AssertFrames(IReadOnlyList<Received> got)
    {
        Assert.All(got, r => Assert.Equal((r.Info.Qpc, TransferFunction.Sdr), (r.Qpc, r.Transfer)));
        var mismatched = new List<int>();
        for (int i = 1; i < got.Count; i++)
        {
            HookFrameInfo a = got[i - 1].Info, b = got[i].Info;
            Assert.True(b.Qpc > a.Qpc, $"QPC not monotonic at {i}: {a.Qpc} then {b.Qpc}");
            Assert.True(b.Frame > a.Frame, $"frame numbers not increasing at {i}");
            Assert.True(b.Record > a.Record);
            // Every gap in present numbers is accounted for: presents the hook
            // dropped, plus records the receiver skipped for a newer one.
            Assert.Equal(b.Frame - a.Frame - 1, (b.HookDropped - a.HookDropped) + (b.Record - a.Record - 1));
            // The pixels are the frame the metadata describes: the app's frame
            // counter runs a constant offset ahead of the hook's (within one
            // generation; a recreated device can skip app frames).
            if (a.Generation == b.Generation && got[i - 1].Pixels - (uint)a.Frame != got[i].Pixels - (uint)b.Frame)
            {
                mismatched.Add(i);
            }
        }

        return mismatched;
    }

    [Theory]
    [InlineData(TestApp.D3D11, false)]
    [InlineData(TestApp.D3D11, true)]
    [InlineData(TestApp.D3D12, false)]
    [InlineData(TestApp.D3D12, true)]
    public void Test_app_frames_arrive_numbered_in_order_with_monotonic_qpc(TestApp app, bool present1)
    {
        RequireGpu();
        string[] args = present1 ? ["--fps", "120", "--present1"] : ["--fps", "120"];
        using HookedProcess p = HookInjector.Launch(HookLaunchRequest.ForTestApp(app, args));
        Assert.Same(p, HookInjector.Attach((int)p.ProcessId));
        HookConfig config = p.WaitForFirstFrame(TimeSpan.FromSeconds(10));
        Assert.Equal(app == TestApp.D3D11 ? HookApi.D3D11 : HookApi.D3D12, config.Api);
        Assert.Equal((int)Format.B8G8R8A8_UNorm, config.Format);
        Assert.Equal((640, 360), (config.Width, config.Height));
        using GpuDevice gpu = CreateDevice(config);
        var stopped = new List<string>();
        List<Received> got;
        using (var source = new HookCaptureSource(gpu, p))
        {
            got = Capture(p, source, gpu, 150, TimeSpan.FromSeconds(15), stopped);
            Assert.Empty(stopped);
            Assert.True(got.Count >= 120, $"only {got.Count} frames (hook {source.HookCounters}, receiver {source.Stats})");
            Assert.Empty(AssertFrames(got));
            HookReceiverStats s = source.Stats;
            Assert.Equal(1, s.Opens);
            Assert.Equal("hook", source.Backend);
            HookCounters c = source.HookCounters;
            Assert.True(c.Presents >= got[^1].Info.Frame + 1);
            Assert.Equal(c.Dropped, c.DroppedBusy + c.DroppedNotReady + c.DroppedDevice);
            Assert.True(got[^1].Info.HookDropped <= 0.1 * got[^1].Info.Frame, $"the hook dropped {got[^1].Info.HookDropped} of {got[^1].Info.Frame} presents");
        }
    }

    [Theory]
    [InlineData(TestApp.D3D11)]
    [InlineData(TestApp.D3D12)]
    public void Capture_survives_a_resize_and_a_recreated_device(TestApp app)
    {
        RequireGpu();
        // D3D12 --recreate-at removes the device (ID3D12Device5::RemoveDevice) and recovers like a game.
        using HookedProcess p = HookInjector.Launch(HookLaunchRequest.ForTestApp(app, "--fps", "120", "--resize-at", "60", "--recreate-at", "180"));
        HookConfig config = p.WaitForFirstFrame(TimeSpan.FromSeconds(10));
        using GpuDevice gpu = CreateDevice(config);
        var stopped = new List<string>();
        using var source = new HookCaptureSource(gpu, p);
        List<Received> got = Capture(p, source, gpu, 330, TimeSpan.FromSeconds(20), stopped);
        Assert.Empty(stopped);
        Assert.True(got.Count >= 300, $"only {got.Count} frames (hook {source.HookCounters} \"{p.Channel.Status}\", receiver {source.Stats})");
        // Frames still in flight when the device is removed lose their GPU
        // copy, so up to one per slot right before the device change may show
        // an older image. Nothing else may.
        List<int> bad = AssertFrames(got);
        Assert.True(bad.Count <= 3, $"{bad.Count} frames do not match their pixels (status \"{p.Channel.Status}\"): {string.Join(", ", bad.Select(i => $"{i}:{(long)got[i].Pixels - got[i].Info.Frame - ((long)got[0].Pixels - got[0].Info.Frame)}"))}");
        foreach (int i in bad)
        {
            Assert.True(app == TestApp.D3D12 && got.Skip(i).Take(4).Any(r => r.Info.Generation != got[i].Info.Generation),
                $"frame {i} (hook frame {got[i].Info.Frame}, pixels {got[i].Pixels}) does not match and is not just before a device removal");
        }
        // The hook installs once the window is up, which under load can be after the resize frame.
        bool sawResize = got[0].Pixels < 60;
        Assert.True(source.Stats.Opens >= (sawResize ? 3 : 2), $"expected the textures to be reopened after the resize and the new device (first app frame {got[0].Pixels}): {source.Stats}");
        Assert.True(got[0].Info.Generation < got[^1].Info.Generation);
    }

    [Fact]
    public void Source_stops_when_the_hook_stops_producing()
    {
        RequireGpu();
        using HookedProcess p = HookInjector.Launch(HookLaunchRequest.ForTestApp(TestApp.D3D11, "--fps", "120"));
        HookConfig config = p.WaitForFirstFrame(TimeSpan.FromSeconds(10));
        using GpuDevice gpu = CreateDevice(config);
        using var source = new HookCaptureSource(gpu, p, stallTimeout: TimeSpan.FromSeconds(1));
        var stopped = new List<string>();
        Assert.True(Capture(p, source, gpu, 30, TimeSpan.FromSeconds(10), stopped).Count == 30);

        // Freeze the test app: no more presents, so nothing new is published.
        var sw = Stopwatch.StartNew();
        using var stop = new ManualResetEventSlim();
        source.Stopped += (_, _) => stop.Set();
        Suspend(p.ProcessId, suspend: true);
        try
        {
            Assert.True(stop.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "Stopped was not raised");
            Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 3);
            lock (stopped)
            {
                Assert.Contains("no frame from the hook", stopped[0], StringComparison.Ordinal);
            }
        }
        finally
        {
            Suspend(p.ProcessId, suspend: false);
        }
    }

    [Fact]
    public void Source_stops_when_the_test_app_exits()
    {
        RequireGpu();
        using HookedProcess p = HookInjector.Launch(HookLaunchRequest.ForTestApp(TestApp.D3D12, "--fps", "120", "--frames", "240"));
        HookConfig config = p.WaitForFirstFrame(TimeSpan.FromSeconds(10));
        using GpuDevice gpu = CreateDevice(config);
        using var source = new HookCaptureSource(gpu, p);
        var stopped = new List<string>();
        Capture(p, source, gpu, int.MaxValue, TimeSpan.FromSeconds(10), stopped);
        lock (stopped)
        {
            Assert.Single(stopped);
            Assert.True(stopped[0].Contains("exit", StringComparison.Ordinal), stopped[0]);
        }

        Assert.InRange(source.FramesCaptured, 120, 240);
    }

    private static void Suspend(uint pid, bool suspend)
    {
        foreach (ProcessThread t in Process.GetProcessById((int)pid).Threads)
        {
            nint h = OpenThread(0x0002 /* THREAD_SUSPEND_RESUME */, false, (uint)t.Id);
            if (h != 0)
            {
                _ = suspend ? SuspendThread(h) : ResumeThread(h);
                CloseHandle(h);
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint OpenThread(uint access, bool inherit, uint id);

    [DllImport("kernel32.dll")]
    private static extern uint SuspendThread(nint thread);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
