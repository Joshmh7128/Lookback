using Lookback.Capture.Gpu;
using Lookback.Capture.Targets;
using Lookback.Core.Time;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Lookback.Capture.Wgc;

/// <summary>
/// Windows.Graphics.Capture for one window or monitor: no injection, so it
/// is safe with anti-cheat, and the default backend. Uses a free-threaded
/// frame pool, so frames arrive on a WGC worker thread with no dispatcher.
/// Recreates the pool when the content size changes. Frame times are
/// <c>SystemRelativeTime</c>, which is QPC in 100 ns units.
/// </summary>
public sealed class WgcCaptureSource : ICaptureSource
{
    private readonly GpuDevice _gpu;
    private readonly CaptureTarget _target;
    private readonly bool _cursor;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _size;
    private CapturedFrameHandler? _onFrame;
    private long _frames;
    private int _disposed;

    public WgcCaptureSource(GpuDevice gpu, CaptureTarget target, bool captureCursor = false)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(target);
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        }

        _gpu = gpu;
        _target = target;
        _cursor = captureCursor;
        _winrtDevice = WgcInterop.WrapDevice(gpu.Device);
        _item = target.IsWindow ? WgcInterop.CreateForWindow(target.Window) : WgcInterop.CreateForMonitor(target.Monitor);
        _item.Closed += (_, _) => Stopped?.Invoke(this, "capture item closed");
    }

    public string Backend => "wgc";

    public string Target => _target.Description;

    public long FramesCaptured => Interlocked.Read(ref _frames);

    /// <summary>False on Windows 10: the yellow capture border cannot be turned off there.</summary>
    public static bool CanHideBorder => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public event EventHandler<string>? Stopped;

    public void Start(CapturedFrameHandler onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        _onFrame = onFrame;
        _size = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = _cursor;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            TryHideBorder(_session);
        }

        _session.StartCapture();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.22000")]
    private static void TryHideBorder(GraphicsCaptureSession session)
    {
        try
        {
            session.IsBorderRequired = false;
        }
        catch (UnauthorizedAccessException)
        {
            // Needs GraphicsCaptureAccess.RequestAccessAsync(Borderless) on some builds; the border stays.
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
        if (frame is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        SizeInt32 size = frame.ContentSize;
        if (size.Width != _size.Width || size.Height != _size.Height)
        {
            _size = size;
            if (size.Width > 0 && size.Height > 0)
            {
                pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            }

            return; // this frame was rendered at the old size
        }

        long qpc = QpcTime.Rescale(frame.SystemRelativeTime.Ticks, TimeSpan.TicksPerSecond, System.Diagnostics.Stopwatch.Frequency);
        using ID3D11Texture2D texture = WgcInterop.Texture(frame.Surface);
        Interlocked.Increment(ref _frames);
        _onFrame?.Invoke(texture, qpc, TransferFunction.Sdr);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_pool is not null)
        {
            _pool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _pool?.Dispose();
        _winrtDevice.Dispose();
    }
}
