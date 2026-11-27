using Lookback.Capture.Gpu;
using Lookback.Capture.Targets;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Lookback.Capture.Dxgi;

/// <summary>
/// DXGI Desktop Duplication of one monitor: the fallback when WGC is
/// unavailable. Runs its own thread around <c>AcquireNextFrame</c>.
/// <c>LastPresentTime</c> is raw QPC, so frames need no clock conversion.
/// Recreates the duplication on <c>DXGI_ERROR_ACCESS_LOST</c> (mode change,
/// UAC desktop, fullscreen switch). The device must be on the adapter that
/// owns the output, which <see cref="CaptureTarget"/> resolves.
/// </summary>
public sealed class DxgiDuplicationSource : ICaptureSource
{
    private readonly GpuDevice _gpu;
    private readonly CaptureTarget _target;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private long _frames;
    private long _accessLost;

    public DxgiDuplicationSource(GpuDevice gpu, CaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(target);
        if (gpu.Luid != target.AdapterLuid && !gpu.IsWarp)
        {
            throw new ArgumentException("Desktop duplication must run on the adapter that owns the output.", nameof(gpu));
        }

        _gpu = gpu;
        _target = target;
    }

    public string Backend => "dxgi";

    public string Target => _target.Description;

    public long FramesCaptured => Interlocked.Read(ref _frames);

    /// <summary>How often the duplication had to be recreated.</summary>
    public long AccessLost => Interlocked.Read(ref _accessLost);

    public event EventHandler<string>? Stopped;

    public void Start(CapturedFrameHandler onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        _thread = new Thread(() => Run(onFrame)) { IsBackground = true, Name = "Lookback DXGI duplication", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private IDXGIOutputDuplication Duplicate()
    {
        IDXGIAdapter adapter = _gpu.Adapter ?? throw new InvalidOperationException("No adapter.");
        adapter.EnumOutputs((uint)_target.OutputIndex, out IDXGIOutput? output).CheckError();
        using (output)
        using (IDXGIOutput1 output1 = output!.QueryInterface<IDXGIOutput1>())
        {
            return output1.DuplicateOutput(_gpu.Device);
        }
    }

    private void Run(CapturedFrameHandler onFrame)
    {
        CancellationToken ct = _stop.Token;
        IDXGIOutputDuplication? dup = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (dup is null)
                {
                    try
                    {
                        dup = Duplicate();
                    }
                    catch (SharpGenException e) when (e.ResultCode == Vortice.DXGI.ResultCode.Unsupported || e.ResultCode == Vortice.DXGI.ResultCode.NotCurrentlyAvailable || e.ResultCode == Vortice.DXGI.ResultCode.SessionDisconnected)
                    {
                        Thread.Sleep(250); // secure desktop or too many duplications; try again
                        continue;
                    }
                }

                Result r = dup.AcquireNextFrame(100, out OutduplFrameInfo info, out IDXGIResource? resource);
                if (r == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    continue;
                }

                if (r == Vortice.DXGI.ResultCode.AccessLost)
                {
                    Interlocked.Increment(ref _accessLost);
                    dup.Dispose();
                    dup = null;
                    continue;
                }

                r.CheckError();
                try
                {
                    // A zero present time means only the pointer moved.
                    if (info.LastPresentTime != 0 && resource is not null)
                    {
                        using ID3D11Texture2D texture = resource.QueryInterface<ID3D11Texture2D>();
                        Interlocked.Increment(ref _frames);
                        onFrame(texture, info.LastPresentTime, TransferFunction.Sdr);
                    }
                }
                finally
                {
                    resource?.Dispose();
                    dup.ReleaseFrame();
                }
            }
        }
        catch (SharpGenException e)
        {
            Stopped?.Invoke(this, $"duplication failed: {e.Message}");
        }
        finally
        {
            dup?.Dispose();
        }
    }

    public void Dispose()
    {
        if (!_stop.IsCancellationRequested)
        {
            _stop.Cancel();
            _thread?.Join();
            _stop.Dispose();
        }
    }
}
