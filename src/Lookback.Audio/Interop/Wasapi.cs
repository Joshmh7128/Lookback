using System.Runtime.InteropServices;

namespace Lookback.Audio.Interop;

/// <summary>
/// WASAPI and MMDevice through raw vtable calls, like the rest of the capture
/// interop: no built-in COM marshalling, nothing allocated per call, so the
/// capture loop stays allocation-free. Slot numbers follow the SDK headers
/// (audioclient.h, mmdeviceapi.h); slots 0–2 are IUnknown.
/// </summary>
internal static unsafe class Wasapi
{
    public const int DeviceInvalidated = unchecked((int)0x88890004);
    public const int UnsupportedFormat = unchecked((int)0x88890008);
    public const int DeviceInUse = unchecked((int)0x8889000A);
    public const int ResourcesInvalidated = unchecked((int)0x88890026);
    public const int BufferEmpty = 0x08890001;

    public const int ShareModeShared = 0;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;

    public const uint BufferFlagsDataDiscontinuity = 0x1;
    public const uint BufferFlagsSilent = 0x2;
    public const uint BufferFlagsTimestampError = 0x4;

    public const int FlowRender = 0;
    public const int FlowCapture = 1;
    public const int RoleConsole = 0;
    public const uint DeviceStateActive = 0x1;

    public static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IidMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IidMMNotificationClient = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
    public static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static readonly Guid IidAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IidAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    public static readonly Guid IidActivateCompletionHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");
    public static readonly Guid IidAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    public static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    /// <summary><c>VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK</c>.</summary>
    public const string ProcessLoopbackDevice = "VAD\\Process_Loopback";

    /// <summary>PKEY_Device_FriendlyName.</summary>
    public static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    private static void** Vtbl(nint p) => *(void***)p;

    // IMMDeviceEnumerator
    public static int GetDefaultAudioEndpoint(nint enumerator, int flow, int role, out nint device)
    {
        nint d;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Vtbl(enumerator)[4])(enumerator, flow, role, &d);
        device = hr >= 0 ? d : 0;
        return hr;
    }

    public static int EnumAudioEndpoints(nint enumerator, int flow, uint stateMask, out nint collection)
    {
        nint c;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)Vtbl(enumerator)[3])(enumerator, flow, stateMask, &c);
        collection = hr >= 0 ? c : 0;
        return hr;
    }

    public static int RegisterNotification(nint enumerator, nint client) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Vtbl(enumerator)[6])(enumerator, client);

    public static int UnregisterNotification(nint enumerator, nint client) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Vtbl(enumerator)[7])(enumerator, client);

    // IMMDeviceCollection
    public static int CollectionCount(nint collection, out uint count)
    {
        uint n;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl(collection)[3])(collection, &n);
        count = n;
        return hr;
    }

    public static int CollectionItem(nint collection, uint index, out nint device)
    {
        nint d;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(collection)[4])(collection, index, &d);
        device = hr >= 0 ? d : 0;
        return hr;
    }

    // IAudioMeterInformation
    public static int GetPeakValue(nint meter, out float peak)
    {
        float v = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, float*, int>)Vtbl(meter)[3])(meter, &v);
        peak = v;
        return hr;
    }

    // IMMDevice
    public static int Activate(nint device, in Guid iid, out nint iface)
    {
        nint p;
        Guid g = iid;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, void*, nint*, int>)Vtbl(device)[3])(device, &g, Native.ClsCtxAll, null, &p);
        iface = hr >= 0 ? p : 0;
        return hr;
    }

    public static string? GetId(nint device)
    {
        char* id;
        if (((delegate* unmanaged[Stdcall]<nint, char**, int>)Vtbl(device)[5])(device, &id) < 0 || id == null)
        {
            return null;
        }

        string s = new(id);
        Marshal.FreeCoTaskMem((nint)id);
        return s;
    }

    public static string? GetFriendlyName(nint device)
    {
        nint store;
        if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(device)[4])(device, 0 /* STGM_READ */, &store) < 0)
        {
            return null;
        }

        try
        {
            PropertyKey key = FriendlyName;
            byte* pv = stackalloc byte[24];
            new Span<byte>(pv, 24).Clear();
            if (((delegate* unmanaged[Stdcall]<nint, PropertyKey*, void*, int>)Vtbl(store)[5])(store, &key, pv) < 0)
            {
                return null;
            }

            string? name = *(ushort*)pv == 31 /* VT_LPWSTR */ ? new string(*(char**)(pv + 8)) : null;
            _ = Native.PropVariantClear(pv);
            return name;
        }
        finally
        {
            Marshal.Release(store);
        }
    }

    // IAudioClient
    public static int Initialize(nint client, int shareMode, uint flags, long bufferHns, long periodHns, WaveFormatExtensible* format) =>
        ((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormatExtensible*, Guid*, int>)Vtbl(client)[3])(client, shareMode, flags, bufferHns, periodHns, format, null);

    /// <remarks>
    /// A process-loopback client on Windows 10 (19045) returns S_OK here
    /// without writing the count, so <paramref name="frames"/> is 0 then;
    /// callers fall back to the requested buffer length.
    /// </remarks>
    public static int GetBufferSize(nint client, out uint frames)
    {
        uint n = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl(client)[4])(client, &n);
        frames = n;
        return hr;
    }

    public static int GetCurrentPadding(nint client, out uint frames)
    {
        uint n;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl(client)[6])(client, &n);
        frames = n;
        return hr;
    }

    /// <summary>The shared-mode mix format; the caller frees it with <see cref="Marshal.FreeCoTaskMem"/>.</summary>
    public static int GetMixFormat(nint client, out WaveFormatExtensible* format)
    {
        WaveFormatExtensible* f;
        int hr = ((delegate* unmanaged[Stdcall]<nint, WaveFormatExtensible**, int>)Vtbl(client)[8])(client, &f);
        format = hr >= 0 ? f : null;
        return hr;
    }

    public static int Start(nint client) => ((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(client)[10])(client);

    public static int Stop(nint client) => ((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(client)[11])(client);

    public static int SetEventHandle(nint client, nint handle) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Vtbl(client)[13])(client, handle);

    public static int GetService(nint client, in Guid iid, out nint service)
    {
        nint p;
        Guid g = iid;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl(client)[14])(client, &g, &p);
        service = hr >= 0 ? p : 0;
        return hr;
    }

    // IAudioCaptureClient
    public static int CaptureGetBuffer(nint capture, out byte* data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition)
    {
        byte* d;
        uint n, f;
        ulong pos, qpc;
        int hr = ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, ulong*, ulong*, int>)Vtbl(capture)[3])(capture, &d, &n, &f, &pos, &qpc);
        data = d;
        frames = n;
        flags = f;
        devicePosition = pos;
        qpcPosition = qpc;
        return hr;
    }

    public static int CaptureReleaseBuffer(nint capture, uint frames) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Vtbl(capture)[4])(capture, frames);

    public static int CaptureNextPacketSize(nint capture, out uint frames)
    {
        uint n;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl(capture)[5])(capture, &n);
        frames = n;
        return hr;
    }

    // IAudioRenderClient
    public static int RenderGetBuffer(nint render, uint frames, out byte* data)
    {
        byte* d;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, byte**, int>)Vtbl(render)[3])(render, frames, &d);
        data = d;
        return hr;
    }

    public static int RenderReleaseBuffer(nint render, uint frames, uint flags) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Vtbl(render)[4])(render, frames, flags);

    // IActivateAudioInterfaceAsyncOperation
    public static int GetActivateResult(nint operation, out int activateResult, out nint iface)
    {
        int r;
        nint p;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int*, nint*, int>)Vtbl(operation)[3])(operation, &r, &p);
        activateResult = r;
        iface = p;
        return hr;
    }

    public static nint CreateEnumerator()
    {
        Native.Check(Native.CoCreateInstance(ClsidMMDeviceEnumerator, 0, Native.ClsCtxAll, IidMMDeviceEnumerator, out nint e), "CoCreateInstance(MMDeviceEnumerator)");
        return e;
    }
}

/// <summary><c>PROPERTYKEY</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid fmtid, uint pid)
{
    public readonly Guid FormatId = fmtid;
    public readonly uint PropertyId = pid;
}

/// <summary><c>WAVEFORMATEXTENSIBLE</c> (the first 18 bytes are <c>WAVEFORMATEX</c>).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatExtensible
{
    public const ushort TagPcm = 1;
    public const ushort TagFloat = 3;
    public const ushort TagExtensible = 0xFFFE;
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;

    /// <summary>32-bit float, interleaved, at <paramref name="rate"/> with <paramref name="channels"/> channels.</summary>
    public static WaveFormatExtensible Float(int rate, int channels) => new()
    {
        FormatTag = TagExtensible,
        Channels = (ushort)channels,
        SamplesPerSec = (uint)rate,
        BitsPerSample = 32,
        BlockAlign = (ushort)(channels * 4),
        AvgBytesPerSec = (uint)(rate * channels * 4),
        Size = 22,
        ValidBitsPerSample = 32,
        ChannelMask = channels == 2 ? 0x3u : 0u,
        SubFormat = SubtypeFloat,
    };

    /// <summary>Reads what the stream delivers, or null for a format we do not convert.</summary>
    public static unsafe PcmFormat? Describe(WaveFormatExtensible* f)
    {
        bool extensible = f->FormatTag == TagExtensible && f->Size >= 22;
        bool isFloat = f->FormatTag == TagFloat || (extensible && f->SubFormat == SubtypeFloat);
        bool isPcm = f->FormatTag == TagPcm || (extensible && f->SubFormat == SubtypePcm);
        int container = f->BitsPerSample;
        int valid = extensible && f->ValidBitsPerSample != 0 ? f->ValidBitsPerSample : container;
        SampleEncoding? encoding = (isFloat, isPcm, container) switch
        {
            (true, _, 32) => SampleEncoding.IeeeFloat,
            (_, true, 16) => SampleEncoding.Pcm16,
            (_, true, 24) => SampleEncoding.Pcm24,
            (_, true, 32) => SampleEncoding.Pcm32,
            _ => null,
        };
        return encoding is null || f->Channels == 0 || f->SamplesPerSec == 0
            ? null
            : new PcmFormat((int)f->SamplesPerSec, f->Channels, encoding.Value, f->BlockAlign, valid);
    }
}
