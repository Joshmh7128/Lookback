using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lookback.Capture.Hook;

// Mirror of native/include/lookback_hook_protocol.h, field for field. Change
// both together and bump Version; HookProtocolTests pins the layout to the
// same sizes and offsets the C++ static_asserts check.

/// <summary>What the hook reports about itself in the channel.</summary>
public enum HookState
{
    /// <summary>Channel created; the hook has not loaded yet.</summary>
    Waiting = 0,

    /// <summary>Vtables patched; no frame published yet.</summary>
    Installed = 1,

    Capturing = 2,

    /// <summary>The hook declined to install (wrong host, protocol mismatch). See <see cref="HookChannel.Status"/>.</summary>
    Refused = 3,

    Error = 4,

    /// <summary>The target process is shutting down.</summary>
    Exiting = 5,
}

/// <summary>The graphics API of the hooked swap chain.</summary>
public enum HookApi
{
    None = 0,
    D3D11 = 1,
    D3D12 = 2,
}

internal static class HookProtocol
{
    public const uint Magic = 0x4B48424C; // "LBHK"
    public const uint Version = 1;
    public const int RingCapacity = 64;
    public const int MaxSlots = 3;
    public const int StatusBytes = 256;

    public const int SyncKeyedMutex = 1;
    public const int SyncFence = 2;

    public const int SlotFree = 0;
    public const int SlotWriting = 1;
    public const int SlotReading = 2;

    /// <summary>The export every test app carries; the injector and the hook both check it.</summary>
    public const string MarkerExport = "LookbackTestAppMarker";
    public const string MarkerValue = "lookback-testapp/1";

    public static string ChannelName(uint pid) => $@"Local\Lookback.Hook.{pid}.Channel";

    public static string FrameEventName(uint pid) => $@"Local\Lookback.Hook.{pid}.Frame";
}

[StructLayout(LayoutKind.Sequential)]
internal struct LbhSlot
{
    public int State;
    public uint Generation;
    public long Record;
    public long Frame;
    public long Qpc;
    public long Dropped;
    public ulong FenceValue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LbhFrame
{
    public long Seq;
    public long Record;
    public long Frame;
    public long Qpc;
    public long Dropped;
    public ulong FenceValue;
    public uint Slot;
    public uint Generation;
    public uint Width;
    public uint Height;
    public uint Format;
    public uint Reserved;
}

[InlineArray(HookProtocol.MaxSlots + 1)]
internal struct LbhSlots
{
    private LbhSlot _e;
}

[InlineArray(HookProtocol.RingCapacity)]
internal struct LbhRing
{
    private LbhFrame _e;
}

[InlineArray(HookProtocol.MaxSlots + 1)]
internal struct LbhHandles
{
    private ulong _e;
}

[InlineArray(HookProtocol.StatusBytes)]
internal struct LbhStatus
{
    private byte _e;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LbhHeader
{
    public uint Magic;
    public uint Version;
    public uint HeaderSize;
    public uint RingCapacity;
    public uint TargetPid;
    public uint ReceiverPid;

    public int State;
    public uint HookVersion;
    public long QpcFrequency;
    public long HeartbeatQpc;
    public long Presents;
    public long Dropped;
    public long DroppedBusy;
    public long DroppedNotReady;
    public long DroppedDevice;
    public long WriteIndex;

    public long ConfigSeq;
    public uint Generation;
    public uint Api;
    public uint Sync;
    public uint SlotCount;
    public uint Width;
    public uint Height;
    public uint Format;
    public uint QueueSource;
    public long AdapterLuid;
    public LbhHandles TextureHandles;
    public ulong FenceHandle;

    public LbhStatus Status;

    public LbhSlots Slots;
    public LbhRing Ring;
}
