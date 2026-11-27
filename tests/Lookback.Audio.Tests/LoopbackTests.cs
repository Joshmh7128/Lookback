using System.Diagnostics;
using Lookback.Audio.Interop;
using Lookback.Media.Frames;

namespace Lookback.Audio.Tests;

/// <summary>
/// Real WASAPI on the machine's default devices. A quiet 997 Hz tone (-20 dBFS,
/// about a second) is played through a render stream and captured back by
/// endpoint loopback; the capture must hear the tone, stamp it with a sane
/// QPC, fit the device clock close to nominal and allocate nothing per packet.
/// Skips where there is no render device (CI runners).
/// </summary>
[Collection("audio devices")]
public class LoopbackTests(ITestOutputHelper output)
{
    private const double ToneHz = 997;

    private static bool HasDevice(bool render)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return WasapiSource.ListDevices().Any(d => d.IsRender == render && d.IsDefault);
        }
        catch (WasapiException)
        {
            return false;
        }
    }

    /// <summary>Copies captured stereo into a preallocated buffer, allocation-free, and tracks QPC sanity.</summary>
    private sealed class Recorder(int capacityFrames)
    {
        private readonly long _now = Stopwatch.Frequency;

        public float[] Left { get; } = new float[capacityFrames];

        public int Frames { get; private set; }

        public long Calls { get; private set; }

        public long Hops { get; private set; }

        public long LastHopQpc { get; private set; }

        public double MinLagMs { get; private set; } = double.MaxValue;

        public double MaxLagMs { get; private set; } = double.MinValue;

        public long PreviousQpc { get; private set; } = long.MinValue;

        public long Backwards { get; private set; }

        public void OnPcm(ReadOnlySpan<float> stereo, long qpc)
        {
            double lag = (Stopwatch.GetTimestamp() - qpc) * 1000.0 / _now;
            MinLagMs = Math.Min(MinLagMs, lag);
            MaxLagMs = Math.Max(MaxLagMs, lag);
            if (qpc < PreviousQpc)
            {
                Backwards++;
            }

            PreviousQpc = qpc;
            Calls++;
            int n = Math.Min(stereo.Length / 2, Left.Length - Frames);
            for (int i = 0; i < n; i++)
            {
                Left[Frames + i] = stereo[2 * i];
            }

            Frames += n;
        }

        public void OnHop(AudioBlock block)
        {
            Hops++;
            LastHopQpc = block.Qpc;
        }
    }

    [Fact]
    public void Endpoint_loopback_hears_a_rendered_tone()
    {
        Assert.SkipUnless(HasDevice(render: true), "no default render device");
        var rec = new Recorder(48_000 * 10);
        using WasapiSource source = WasapiSource.Start(new AudioSourceOptions { Kind = AudioSourceKind.SystemLoopback }, rec.OnPcm, rec.OnHop);
        Assert.True(source.Device is not null, $"loopback did not open: {source.LastError}");
        output.WriteLine($"capturing {source.Description}");

        PlayTone(TimeSpan.FromSeconds(1.2));
        Thread.Sleep(2500); // keep capturing silence, so the clock fit sees a few seconds
        long packets = source.Packets;
        double measured = source.MeasuredRate;
        int nominal = source.Format!.SampleRate;
        long allocated = source.CaptureThreadAllocatedBytes;
        int resets = source.ClockResets;
        source.Dispose();

        output.WriteLine($"{packets} packets ({source.SilentPackets} silent), {rec.Frames} frames out, {rec.Hops} hops, " +
            $"lag {rec.MinLagMs:F1}..{rec.MaxLagMs:F1} ms, rate {measured:F2} vs {nominal}, resets {resets}, " +
            $"discontinuities {source.Discontinuities}, allocated {allocated} B");

        // The tone is in there. Loopback hears everything the machine plays, so look for this tone, not the loudest sound.
        double amplitude = Tools.FindTone(rec.Left.AsSpan(0, rec.Frames), ToneHz, 48_000, out int toneAt);
        double peak = Tools.PeakFrequency(rec.Left.AsSpan(toneAt, 24_000), 48_000, ToneHz - 20, ToneHz + 20);
        output.WriteLine($"tone: amplitude {amplitude:F4} at {toneAt / 48.0:F0} ms, peak {peak} Hz");
        Assert.True(amplitude > 0.07, $"tone not captured (amplitude {amplitude:F4}); is the default device muted?");
        Assert.True(amplitude < 0.13, $"tone came back at {amplitude:F4}, not 0.1");
        Assert.InRange(peak, ToneHz - 2, ToneHz + 2);

        // Samples were stamped near capture. Loopback stamps are the engine's presentation time (when the
        // sound plays), about one device period after the packet is readable, so allow up to 25 ms ahead.
        Assert.True(rec.MinLagMs > -25, $"a packet was stamped {-rec.MinLagMs:F1} ms in the future");
        Assert.True(rec.MaxLagMs < 250, $"a packet arrived {rec.MaxLagMs:F1} ms after its timestamp");
        Assert.Equal(0, rec.Backwards);

        // ~48 000 frames per second of capture, and ~100 hops per second of it, with the keep-alive filling quiet stretches.
        Assert.InRange(rec.Hops * 480, rec.Frames * 0.95, rec.Frames * 1.05);
        Assert.InRange(Math.Abs((measured / nominal) - 1), 0, 1e-3);
        Assert.InRange(resets, 1, 2);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Default_device_change_reopens_and_the_timeline_carries_on()
    {
        Assert.SkipUnless(HasDevice(render: true), "no default render device");
        var rec = new Recorder(48_000 * 10);
        using WasapiSource source = WasapiSource.Start(new AudioSourceOptions { Kind = AudioSourceKind.SystemLoopback }, rec.OnPcm, rec.OnHop);
        Assert.True(source.Device is not null, $"loopback did not open: {source.LastError}");
        Thread.Sleep(700);

        // What IMMNotificationClient delivers when the user picks another default (here it re-opens the same device).
        ((IDeviceNotifications)source).DefaultDeviceChanged(Wasapi.FlowRender, Wasapi.RoleConsole, "{another-device}");
        ((IDeviceNotifications)source).DefaultDeviceChanged(Wasapi.FlowCapture, Wasapi.RoleConsole, "{a-mic}"); // other flow: ignored
        Thread.Sleep(1000);
        long hops = rec.Hops;
        long lastHop = rec.LastHopQpc;
        long now = Stopwatch.GetTimestamp();
        source.Dispose();

        output.WriteLine($"opens {source.Opens}, device changes {source.DeviceChanges}, resets {source.ClockResets}, silence inserted {source.SilenceInserted} samples, hops {hops}");
        Assert.Equal(2, source.Opens);
        Assert.Equal(1, source.DeviceChanges);
        Assert.Equal(0, rec.Backwards);
        Assert.InRange(source.SilenceInserted, 0, 48_000 / 10); // the re-open gap is filled, and short
        Assert.InRange((now - lastHop) * 1000.0 / Stopwatch.Frequency, -25, 100); // still current after the switch
        Assert.InRange(hops, 150, 185); // ~1.7 s of 10 ms hops, continuous across the re-open
        Assert.Equal(0, source.CaptureThreadAllocatedBytes);
    }

    [Fact]
    public void Process_loopback_falls_back_only_where_allowed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WASAPI is Windows only");
        Assert.SkipWhen(AudioCapturePolicy.ProcessLoopbackSupported(AudioCapturePolicy.OsBuild), "this OS has process loopback; nothing to fall back from");
        uint pid = (uint)Environment.ProcessId;

        Assert.Throws<NotSupportedException>(() => WasapiSource.Start(new AudioSourceOptions { Kind = AudioSourceKind.ProcessLoopback, ProcessId = pid, AllowEndpointFallback = false }, null));

        Assert.SkipUnless(HasDevice(render: true), "no default render device");
        using WasapiSource source = WasapiSource.Start(new AudioSourceOptions { Kind = AudioSourceKind.ProcessLoopback, ProcessId = pid }, null);
        output.WriteLine($"{source.Description}: {source.FallbackReason}");
        Assert.Equal(AudioSourceKind.ProcessLoopback, source.Requested);
        Assert.Equal(AudioSourceKind.SystemLoopback, source.Kind);
        Assert.Contains("20348", source.FallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_loopback_hears_this_process()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WASAPI is Windows only");
        Assert.SkipUnless(AudioCapturePolicy.ProcessLoopbackSupported(AudioCapturePolicy.OsBuild), $"process loopback needs build 20348+, this is {AudioCapturePolicy.OsBuild}");
        Assert.SkipUnless(HasDevice(render: true), "no default render device");
        var rec = new Recorder(48_000 * 10);
        using WasapiSource source = WasapiSource.Start(new AudioSourceOptions { Kind = AudioSourceKind.ProcessLoopback, ProcessId = (uint)Environment.ProcessId }, rec.OnPcm);
        Assert.True(source.Device is not null, $"process loopback did not open: {source.LastError}");
        PlayTone(TimeSpan.FromSeconds(1.2));
        Thread.Sleep(300);
        source.Dispose();
        double amplitude = Tools.FindTone(rec.Left.AsSpan(0, rec.Frames), ToneHz, 48_000, out int toneAt);
        Assert.True(amplitude > 0.07, $"tone not captured (amplitude {amplitude:F4})");
        Assert.InRange(Tools.PeakFrequency(rec.Left.AsSpan(toneAt, 24_000), 48_000, ToneHz - 20, ToneHz + 20), ToneHz - 2, ToneHz + 2);
    }

    [Fact]
    public void Microphone_track_opens_and_streams()
    {
        Assert.SkipUnless(HasDevice(render: false), "no default capture device");
        var rec = new Recorder(48_000 * 10);
        using WasapiSource mic = WasapiSource.Start(new AudioSourceOptions { Kind = AudioSourceKind.Microphone }, rec.OnPcm);
        Assert.True(mic.Device is not null, $"mic did not open: {mic.LastError}");
        Thread.Sleep(1500);
        long allocated = mic.CaptureThreadAllocatedBytes;
        mic.Dispose();
        output.WriteLine($"{mic.Description}: {mic.Packets} packets, {rec.Frames} frames, lag {rec.MinLagMs:F1}..{rec.MaxLagMs:F1} ms, rate {mic.MeasuredRate:F1}, allocated {allocated} B");
        Assert.InRange(rec.Frames, 48_000, 48_000 * 2);
        Assert.True(rec.MinLagMs > 0);
        Assert.True(rec.MaxLagMs < 250);
        Assert.Equal(0, allocated);
    }

    /// <summary>Plays the tone on the default render device, faded in and out, and waits for it to finish.</summary>
    private static void PlayTone(TimeSpan length)
    {
        int init = Native.CoInitializeEx(0, Native.CoInitMultithreaded);
        try
        {
            using RenderStream render = RenderStream.OpenDefault();
            int rate = render.Format.SampleRate;
            int channels = render.Format.Channels;
            long total = (long)(length.TotalSeconds * rate);
            long fade = rate / 20;
            long position = 0;
            RenderFill fill = (span, frames) =>
            {
                for (int f = 0; f < frames; f++, position++)
                {
                    double gain = position >= total ? 0 : Math.Min(1.0, Math.Min(position, total - position) / (double)fade);
                    float v = (float)(0.1 * gain * Math.Sin(2 * Math.PI * ToneHz * position / rate));
                    for (int c = 0; c < channels; c++)
                    {
                        span[(f * channels) + c] = v;
                    }
                }
            };

            render.TopUp(fill, out _);
            render.Start();
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < length + TimeSpan.FromMilliseconds(300))
            {
                Thread.Sleep(5);
                Assert.True(render.TopUp(fill, out _) >= 0, "render device went away");
            }
        }
        finally
        {
            if (init >= 0)
            {
                Native.CoUninitialize();
            }
        }
    }
}
