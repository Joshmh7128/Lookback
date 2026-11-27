using System.Runtime.InteropServices;

namespace Lookback.Audio.Interop;

/// <summary>The kernel32, ole32, avrt and mmdevapi calls WASAPI capture needs.</summary>
internal static unsafe partial class Native
{
    public const uint ClsCtxAll = 0x17;
    public const uint CoInitMultithreaded = 0x0;
    public const uint Infinite = 0xFFFFFFFF;
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 0x102;

    public const int SOk = 0;
    public const int SFalse = 1;
    public const int RpcEChangedMode = unchecked((int)0x80010106);
    public const int ENotFound = unchecked((int)0x80070490);
    public const int ENoInterface = unchecked((int)0x80004002);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint clsContext, in Guid iid, out nint instance);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(void* propVariant);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForMultipleObjects(uint count, nint* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);

    /// <summary>Registers the thread with MMCSS so the scheduler treats it as audio.</summary>
    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvRevertMmThreadCharacteristics(nint handle);

    /// <summary>Activates an audio interface on a device path, including the virtual process-loopback device (build 20348+).</summary>
    [LibraryImport("mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ActivateAudioInterfaceAsync(string deviceInterfacePath, in Guid riid, void* activationParams, nint completionHandler, out nint operation);

    public static nint CreateEvent()
    {
        nint h = CreateEventW(0, manualReset: false, initialState: false, 0);
        if (h == 0)
        {
            throw new InvalidOperationException($"CreateEvent failed ({Marshal.GetLastPInvokeError()}).");
        }

        return h;
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new WasapiException(hr, what);
        }
    }

    public static void Release(ref nint p)
    {
        if (p != 0)
        {
            Marshal.Release(p);
            p = 0;
        }
    }
}

/// <summary>A failed WASAPI or MMDevice call, with its HRESULT.</summary>
public sealed class WasapiException : Exception
{
    public WasapiException()
    {
    }

    public WasapiException(string message)
        : base(message)
    {
    }

    public WasapiException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public WasapiException(int hresult, string what)
        : base($"{what} failed: 0x{hresult:X8}{Describe(hresult)}")
    {
        HResult = hresult;
    }

    /// <summary>True when the device went away (unplugged, disabled, or the format changed): re-open it.</summary>
    public bool DeviceInvalidated => HResult is Wasapi.DeviceInvalidated or Wasapi.ResourcesInvalidated or Native.ENotFound;

    private static string Describe(int hr) => hr switch
    {
        Wasapi.DeviceInvalidated => " (AUDCLNT_E_DEVICE_INVALIDATED)",
        Wasapi.UnsupportedFormat => " (AUDCLNT_E_UNSUPPORTED_FORMAT)",
        Wasapi.DeviceInUse => " (AUDCLNT_E_DEVICE_IN_USE)",
        Native.ENotFound => " (no such device)",
        _ => "",
    };
}
