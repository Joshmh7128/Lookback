using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Lookback.Capture.Gpu;

/// <summary>
/// The D3D11 device every live stage shares: capture, the analysis
/// downscale, colour conversion and the encoder's DXGI device manager. It is
/// created on the adapter that owns the captured output (AETHER drives
/// monitors from both the RTX card and the iGPU), with BGRA and video
/// support. The immediate context is not thread-safe; take
/// <see cref="Lock"/> around any use of <see cref="Context"/>.
/// </summary>
public sealed class GpuDevice : IDisposable
{
    private GpuDevice(ID3D11Device device, IDXGIAdapter1? adapter, string name, long luid, bool isWarp)
    {
        Device = device;
        Context = device.ImmediateContext;
        Adapter = adapter;
        Name = name;
        Luid = luid;
        IsWarp = isWarp;
        Identity = isWarp ? "warp" : DescribeAdapter(adapter!);
        using ID3D11Multithread mt = Context.QueryInterface<ID3D11Multithread>();
        mt.SetMultithreadProtected(true); // the MF encoder uses the device from its own threads
    }

    /// <summary>
    /// "VEN_10DE&amp;DEV_2702 31.0.15.6094": vendor, device and user-mode driver
    /// version. Encoders that stalled are remembered per identity, so a driver
    /// update gets a fresh chance.
    /// </summary>
    public string Identity { get; }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public IDXGIAdapter1? Adapter { get; }

    public string Name { get; }

    /// <summary>Adapter LUID (low part | high part &lt;&lt; 32); the encoder is chosen by it.</summary>
    public long Luid { get; }

    public bool IsWarp { get; }

    public Lock Lock { get; } = new();

    /// <summary>Creates a device on the adapter with <paramref name="luid"/>, or the default adapter when null.</summary>
    public static GpuDevice Create(long? luid = null)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? chosen = null;
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
        {
            AdapterDescription1 d = adapter.Description1;
            if ((d.Flags & AdapterFlags.Software) != 0)
            {
                adapter.Dispose();
                continue;
            }

            if (chosen is null && (luid is null || ToLong(d.Luid) == luid))
            {
                chosen = adapter;
            }
            else
            {
                adapter.Dispose();
            }
        }

        if (chosen is null)
        {
            throw new InvalidOperationException(luid is null ? "No hardware adapter." : $"No adapter with LUID {luid:X}.");
        }

        DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, flags, [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device? device).CheckError();
        AdapterDescription1 desc = chosen.Description1;
        return new GpuDevice(device!, chosen, desc.Description, ToLong(desc.Luid), isWarp: false);
    }

    /// <summary>The WARP software rasteriser, for tests on machines without a GPU (CI).</summary>
    public static GpuDevice CreateWarp()
    {
        // WARP has no video support (no ID3D11VideoDevice), so Nv12Converter needs a real adapter.
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out ID3D11Device? device).CheckError();
        return new GpuDevice(device!, null, "WARP", 0, isWarp: true);
    }

    public static long ToLong(Vortice.Luid luid) => ((long)luid.HighPart << 32) | luid.LowPart;

    /// <summary>Another device on the same adapter (WARP for WARP), with its own immediate context and lock.</summary>
    public GpuDevice CreateSibling() => IsWarp ? CreateWarp() : Create(Luid);

    private static string DescribeAdapter(IDXGIAdapter1 adapter)
    {
        AdapterDescription1 d = adapter.Description1;
        string driver = "unknown";
        if (adapter.CheckInterfaceSupport(typeof(IDXGIDevice).GUID, out long umd).Success)
        {
            driver = $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}";
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"VEN_{d.VendorId:X4}&DEV_{d.DeviceId:X4} {driver}");
    }

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();
        Adapter?.Dispose();
    }
}
