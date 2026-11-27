using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Lookback.Capture.Wgc;

/// <summary>
/// The COM interop WGC needs from desktop code: creating a capture item for
/// an HWND or HMONITOR, wrapping our D3D11 device as a WinRT
/// <c>IDirect3DDevice</c>, and getting the <c>ID3D11Texture2D</c> behind a
/// frame's surface. Raw vtable calls keep it free of built-in COM marshalling.
/// </summary>
internal static unsafe partial class WgcInterop
{
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidTexture2D = typeof(ID3D11Texture2D).GUID;

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public static GraphicsCaptureItem CreateForWindow(nint hwnd) => Create(hwnd, slot: 3);

    public static GraphicsCaptureItem CreateForMonitor(nint monitor) => Create(monitor, slot: 4);

    public static IDirect3DDevice WrapDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out nint inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>The texture behind a capture frame's surface. The caller disposes it.</summary>
    public static ID3D11Texture2D Texture(IDirect3DSurface surface)
    {
        nint abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in IidDxgiInterfaceAccess, out nint access));
            try
            {
                nint texture;
                Guid iid = IidTexture2D;
                var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
                Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    private static GraphicsCaptureItem Create(nint handle, int slot)
    {
        IObjectReference factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in IidGraphicsCaptureItemInterop, out nint interop));
        try
        {
            nint item;
            Guid iid = IidGraphicsCaptureItem;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)interop)[slot];
            Marshal.ThrowExceptionForHR(create(interop, handle, &iid, &item));
            try
            {
                return GraphicsCaptureItem.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }
}
