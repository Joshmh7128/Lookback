using System.Diagnostics;
using System.Runtime.InteropServices;
using Lookback.Capture.Gpu;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Lookback.Capture.Hook;

/// <summary>What the hook said about the frame being delivered.</summary>
/// <param name="Record">Publish index in the hook's ring.</param>
/// <param name="Frame">Present number of the hooked swap chain (counts every present).</param>
/// <param name="Qpc">QPC at the start of Present, in the target process.</param>
/// <param name="HookDropped">Presents the hook had not published up to this one (cumulative).</param>
/// <param name="Generation">Shared-texture generation (bumps on resize and device recreation).</param>
public readonly record struct HookFrameInfo(long Record, long Frame, long Qpc, long HookDropped, uint Generation);

/// <summary>Receiver-side counters.</summary>
/// <param name="Delivered">Frames handed to the callback.</param>
/// <param name="Skipped">Records the hook published that the receiver did not deliver (a newer one was ready).</param>
/// <param name="AcquireTimeouts">Times a slot could not be taken within the bound and was skipped.</param>
/// <param name="Opens">Times the shared textures were opened (first frame, resize, device recreated).</param>
public readonly record struct HookReceiverStats(long Delivered, long Skipped, long AcquireTimeouts, long Opens);

/// <summary>
/// Frames from the present hook in one of our test apps (<see cref="HookInjector"/>).
/// Opens the hook's shared textures on the capture <see cref="GpuDevice"/>
/// (same adapter required), copies each newest frame into a texture it owns
/// and delivers it with the QPC taken at Present, so the analysis tap and the
/// encode path see it exactly like a WGC frame. D3D11 slots are taken through
/// their keyed mutex (key 1, bounded wait) and handed back with key 0; D3D12
/// slots by a CAS on the slot state, after the hook's shared fence reaches
/// the frame's value (bounded wait), and are handed back once our copy has
/// finished on the GPU. Every wait has a timeout; when no frame arrives for
/// the stall timeout, the target exits or the hook reports an error, the
/// source raises <see cref="Stopped"/> and its thread ends.
/// </summary>
public sealed unsafe class HookCaptureSource : ICaptureSource
{
    private const int KeyWriter = 0;
    private const int KeyReader = 1;

    private readonly GpuDevice _gpu;
    private readonly HookedProcess _target;
    private readonly HookChannel _channel;
    private readonly TimeSpan _stallTimeout;
    private readonly TimeSpan _firstFrameTimeout;
    private readonly ID3D11Device1 _device1;
    private readonly ID3D11Device5? _device5;
    private readonly ID3D11Texture2D?[] _shared = new ID3D11Texture2D?[HookProtocol.MaxSlots];
    private readonly IDXGIKeyedMutex?[] _mutex = new IDXGIKeyedMutex?[HookProtocol.MaxSlots];
    private readonly ulong[] _handles = new ulong[HookProtocol.MaxSlots];
    private readonly AutoResetEvent _fenceEvent = new(false);
    private readonly AutoResetEvent _readEvent = new(false);
    private ID3D11Fence? _readFence; // ours: signalled after our copy out of a D3D12 slot
    private ID3D11DeviceContext4? _context4;
    private ulong _readValue;
    private ID3D11Fence? _fence;
    private ID3D11Texture2D? _own;
    private HookConfig _config;
    private long _configSeq = -1;
    private bool _open;
    private CapturedFrameHandler? _onFrame;
    private Thread? _thread;
    private long _lastRecord = -1;
    private long _frames;
    private long _skipped;
    private long _acquireTimeouts;
    private long _opens;
    private int _stopped;
    private int _disposed;

    public HookCaptureSource(GpuDevice gpu, HookedProcess target, TimeSpan? stallTimeout = null, TimeSpan? firstFrameTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(target);
        _gpu = gpu;
        _target = target;
        _channel = target.Channel;
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(2);
        _firstFrameTimeout = firstFrameTimeout ?? TimeSpan.FromSeconds(10);
        _device1 = gpu.Device.QueryInterface<ID3D11Device1>();
        _device5 = gpu.Device.QueryInterfaceOrNull<ID3D11Device5>();
    }

    public string Backend => "hook";

    public string Target => $"{Path.GetFileNameWithoutExtension(_target.ExecutablePath)} (pid {_target.ProcessId}) via present hook";

    public long FramesCaptured => Interlocked.Read(ref _frames);

    /// <summary>The frame being delivered; read it inside the frame callback.</summary>
    public HookFrameInfo LastFrame { get; private set; }

    /// <summary>The graphics API of the hooked swap chain (after the first frame).</summary>
    public HookApi Api => _config.Api;

    public HookReceiverStats Stats => new(FramesCaptured, Interlocked.Read(ref _skipped), Interlocked.Read(ref _acquireTimeouts), Interlocked.Read(ref _opens));

    public HookCounters HookCounters => _channel.Counters;

    public event EventHandler<string>? Stopped;

    public void Start(CapturedFrameHandler onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (_thread is not null)
        {
            throw new InvalidOperationException("Already started.");
        }

        _onFrame = onFrame;
        _thread = new Thread(Run) { IsBackground = true, Name = "Lookback hook receiver", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            var clock = Stopwatch.StartNew();
            TimeSpan lastDelivery = TimeSpan.Zero;
            while (Volatile.Read(ref _disposed) == 0)
            {
                HookNative.WaitForSingleObject(_channel.FrameEvent, 100);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    break;
                }

                if (_target.HasExited)
                {
                    Stop($"test app exited (code {_target.ExitCode})");
                    return;
                }

                HookState state = _channel.State;
                if (state is HookState.Refused or HookState.Error)
                {
                    Stop($"hook {state.ToString().ToLowerInvariant()}: {_channel.Status}");
                    return;
                }

                if (state == HookState.Exiting)
                {
                    Stop("test app is exiting");
                    return;
                }

                if (TryDeliver())
                {
                    lastDelivery = clock.Elapsed;
                    continue;
                }

                TimeSpan limit = FramesCaptured == 0 ? _firstFrameTimeout : _stallTimeout;
                if (clock.Elapsed - lastDelivery > limit)
                {
                    HookCounters c = _channel.Counters;
                    string status = _channel.Status;
                    Stop($"no frame from the hook for {limit.TotalSeconds:F1} s (presents {c.Presents}, published {c.Published}, dropped {c.Dropped}{(status.Length > 0 ? ", " + status : "")})");
                    return;
                }
            }
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or COMException or InvalidOperationException)
        {
            Stop($"hook receiver failed: {e.Message}");
        }
    }

    private void Stop(string why)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            Stopped?.Invoke(this, why);
        }
    }

    /// <summary>Delivers the newest published frame if there is one; false when nothing new could be delivered.</summary>
    private bool TryDeliver()
    {
        if (!EnsureOpen() || !_channel.TryReadLatest(out LbhFrame latest))
        {
            return false;
        }

        if (latest.Generation != _config.Generation || latest.Record <= _lastRecord || latest.Slot >= HookProtocol.MaxSlots)
        {
            return false;
        }

        int slot = (int)latest.Slot;
        HookFrameInfo info;
        bool got = _config.Api == HookApi.D3D11 ? CopyKeyed(slot, out info) : CopyFenced(slot, out info);
        if (!got)
        {
            return false;
        }

        if (!_channel.ConfigUnchanged(_configSeq))
        {
            // The hook released its textures meanwhile (device removed or
            // resized): the copy may predate the frame's own GPU work.
            return false;
        }

        if (_lastRecord >= 0)
        {
            Interlocked.Add(ref _skipped, info.Record - _lastRecord - 1);
        }

        _lastRecord = info.Record;
        LastFrame = info;
        Interlocked.Increment(ref _frames);
        TransferFunction transfer = (Format)_config.Format == Format.R16G16B16A16_Float ? TransferFunction.ScRgb : TransferFunction.Sdr;
        _onFrame!(_own!, info.Qpc, transfer);
        return true;
    }

    // Reads the slot's metadata; the caller owns the slot.
    private bool ReadSlot(int slot, out HookFrameInfo info)
    {
        ref LbhSlot meta = ref _channel.Header.Slots[slot];
        info = new HookFrameInfo(meta.Record, meta.Frame, meta.Qpc, meta.Dropped, meta.Generation);
        return meta.Generation == _config.Generation && meta.Record > _lastRecord;
    }

    private bool CopyKeyed(int slot, out HookFrameInfo info)
    {
        info = default;
        IDXGIKeyedMutex m = _mutex[slot]!;
        int hr = AcquireSync(m, KeyReader, 50);
        if (hr == (int)HookNative.WaitTimeout)
        {
            Interlocked.Increment(ref _acquireTimeouts);
            return false;
        }

        if (hr != 0)
        {
            // WAIT_ABANDONED (the hook's device went away holding it) or a failure: reopen on the next generation.
            CloseShared();
            return false;
        }

        try
        {
            if (!ReadSlot(slot, out info))
            {
                return false;
            }

            lock (_gpu.Lock)
            {
                _gpu.Context.CopyResource(_own!, _shared[slot]!);
            }

            return true;
        }
        finally
        {
            m.ReleaseSync(KeyWriter); // keyed mutex orders the hook's next write after our copy on the GPU
        }
    }

    // Vortice's AcquireSync returns void, which hides WAIT_TIMEOUT (a success
    // code); call the vtable slot directly to see it. IDXGIKeyedMutex: IUnknown
    // 0-2, IDXGIObject 3-6, IDXGIDeviceSubObject 7, AcquireSync 8.
    private static int AcquireSync(IDXGIKeyedMutex mutex, ulong key, uint milliseconds)
    {
        nint self = mutex.NativePointer;
        var fn = (delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)(*(void***)self)[8];
        return fn(self, key, milliseconds);
    }

    private bool CopyFenced(int slot, out HookFrameInfo info)
    {
        info = default;
        ref int state = ref _channel.Header.Slots[slot].State;
        var spin = Stopwatch.StartNew();
        while (Interlocked.CompareExchange(ref state, HookProtocol.SlotReading, HookProtocol.SlotFree) != HookProtocol.SlotFree)
        {
            if (spin.ElapsedMilliseconds > 5)
            {
                Interlocked.Increment(ref _acquireTimeouts);
                return false; // the hook is writing it: a newer frame is on its way
            }

            Thread.Yield();
        }

        try
        {
            if (!ReadSlot(slot, out info))
            {
                return false;
            }

            ref LbhSlot meta = ref _channel.Header.Slots[slot];
            ulong value = meta.FenceValue;
            if (_fence!.CompletedValue == ulong.MaxValue)
            {
                // A removed device reports every fence as complete, including
                // copies that never ran: the texture may hold an older frame.
                CloseShared();
                return false;
            }

            if (_fence.CompletedValue < value)
            {
                _fence.SetEventOnCompletion(value, _fenceEvent.SafeWaitHandle.DangerousGetHandle());
                if (_fence.CompletedValue < value && !_fenceEvent.WaitOne(100))
                {
                    Interlocked.Increment(ref _acquireTimeouts);
                    return false;
                }
            }

            ulong read;
            lock (_gpu.Lock)
            {
                _gpu.Context.CopyResource(_own!, _shared[slot]!);
                read = ++_readValue;
                _context4!.Signal(_readFence!, read);
                _gpu.Context.Flush();
            }

            // Hand the slot back only after our GPU copy has read it; wait on our
            // own fence so the context lock is not held while waiting.
            if (_readFence!.CompletedValue < read)
            {
                _readFence.SetEventOnCompletion(read, _readEvent.SafeWaitHandle.DangerousGetHandle());
                if (_readFence.CompletedValue < read && !_readEvent.WaitOne(100))
                {
                    Interlocked.Increment(ref _acquireTimeouts); // delivered; at worst the next hook write races this one copy
                }
            }

            return true;
        }
        finally
        {
            Volatile.Write(ref state, HookProtocol.SlotFree);
        }
    }

    /// <summary>Opens the hook's textures (and fence) for the current configuration; false when not ready.</summary>
    private bool EnsureOpen()
    {
        if (!_channel.TryReadConfig(out HookConfig config, _handles, out ulong fenceHandle, out long seq))
        {
            return false;
        }

        if (_open && seq == _configSeq)
        {
            return true;
        }

        CloseShared();
        if (config.SlotCount <= 0 || config.SlotCount > HookProtocol.MaxSlots || config.Width <= 0 || config.Height <= 0)
        {
            return false; // the hook released its textures (resize, device loss); a new generation follows
        }

        if (config.AdapterLuid != _gpu.Luid)
        {
            throw new InvalidOperationException($"the test app renders on adapter {config.AdapterLuid:X}, but the capture device is on {_gpu.Luid:X} ({_gpu.Name}); create the GpuDevice from HookConfig.AdapterLuid");
        }

        try
        {
            for (int i = 0; i < config.SlotCount; i++)
            {
                nint h = Duplicate(_handles[i]);
                if (h == 0)
                {
                    CloseShared();
                    return false;
                }

                try
                {
                    _shared[i] = _device1.OpenSharedResource1<ID3D11Texture2D>(h);
                }
                finally
                {
                    HookNative.CloseHandle(h);
                }

                if (config.Api == HookApi.D3D11)
                {
                    _mutex[i] = _shared[i]!.QueryInterface<IDXGIKeyedMutex>();
                }
            }

            if (config.Api == HookApi.D3D12)
            {
                if (_device5 is null)
                {
                    throw new InvalidOperationException("the capture device has no ID3D11Device5 (shared fences need Windows 10 1703+)");
                }

                if (_readFence is null)
                {
                    _readFence = _device5.CreateFence<ID3D11Fence>(0, FenceFlags.None);
                    _context4 = _gpu.Context.QueryInterface<ID3D11DeviceContext4>();
                }

                nint h = Duplicate(fenceHandle);
                if (h == 0)
                {
                    CloseShared();
                    return false;
                }

                try
                {
                    _fence = _device5.OpenSharedFence<ID3D11Fence>(h);
                }
                finally
                {
                    HookNative.CloseHandle(h);
                }
            }
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Handles recycled by a reconfiguration between our read and the duplicate: retry.
            CloseShared();
            return false;
        }

        if (!_channel.ConfigUnchanged(seq))
        {
            CloseShared();
            return false;
        }

        if (_own is null || _config.Width != config.Width || _config.Height != config.Height || _config.Format != config.Format)
        {
            _own?.Dispose();
            _own = _gpu.Device.CreateTexture2D(new Texture2DDescription(
                (Format)config.Format, (uint)config.Width, (uint)config.Height, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget));
        }

        _config = config;
        _configSeq = seq;
        _open = true;
        Interlocked.Increment(ref _opens);
        return true;
    }

    private nint Duplicate(ulong remote)
    {
        if (remote == 0)
        {
            return 0;
        }

        return HookNative.DuplicateHandle(_target.Handle, (nint)remote, HookNative.GetCurrentProcess(), out nint local, 0, false, HookNative.DuplicateSameAccess) ? local : 0;
    }

    private void CloseShared()
    {
        for (int i = 0; i < HookProtocol.MaxSlots; i++)
        {
            _mutex[i]?.Dispose();
            _mutex[i] = null;
            _shared[i]?.Dispose();
            _shared[i] = null;
        }

        _fence?.Dispose();
        _fence = null;
        _open = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            // Every wait in Run is bounded, so this only happens if a frame callback hangs.
            Stop("hook receiver thread did not stop");
        }

        CloseShared();
        _own?.Dispose();
        _readFence?.Dispose();
        _context4?.Dispose();
        _readEvent.Dispose();
        _device5?.Dispose();
        _device1.Dispose();
        _fenceEvent.Dispose();
    }
}
