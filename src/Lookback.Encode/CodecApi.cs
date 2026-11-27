using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Lookback.Encode;

/// <summary>
/// <c>ICodecAPI</c> on an encoder MFT (Vortice does not wrap it): sets
/// rate control, GOP length, B-frames, low latency and forced keyframes.
/// Every setter is best effort; encoders differ in what they accept.
/// </summary>
internal sealed unsafe class CodecApi : IDisposable
{
    private const ushort VtUi4 = 19;
    private const ushort VtBool = 11;
    private nint _ptr;

    private CodecApi(nint ptr) => _ptr = ptr;

    public static CodecApi? From(IMFTransform transform)
    {
        Guid iid = MfGuids.CodecApi;
        return Marshal.QueryInterface(transform.NativePointer, in iid, out nint p) == 0 ? new CodecApi(p) : null;
    }

    public bool SetUInt32(Guid key, uint value) => Set(key, VtUi4, value);

    public bool SetBool(Guid key, bool value) => Set(key, VtBool, value ? 0xFFFFu : 0u);

    private bool Set(Guid key, ushort vt, uint value)
    {
        if (_ptr == 0)
        {
            return false;
        }

        Variant v = default;
        v.Vt = vt;
        v.Value = value;
        var setValue = (delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)(*(nint**)_ptr)[9];
        return setValue(_ptr, &key, &v) >= 0;
    }

    public void Dispose()
    {
        if (_ptr != 0)
        {
            Marshal.Release(_ptr);
            _ptr = 0;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)]
        public ushort Vt;

        [FieldOffset(8)]
        public uint Value;
    }
}
