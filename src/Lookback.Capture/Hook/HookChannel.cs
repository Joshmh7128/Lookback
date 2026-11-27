using System.ComponentModel;
using System.Text;

namespace Lookback.Capture.Hook;

/// <summary>The shared-texture configuration the hook last published.</summary>
public readonly record struct HookConfig(uint Generation, HookApi Api, int SlotCount, int Width, int Height, int Format, long AdapterLuid);

/// <summary>Counters the hook keeps in the channel.</summary>
public readonly record struct HookCounters(long Presents, long Published, long Dropped, long DroppedBusy, long DroppedNotReady, long DroppedDevice);

/// <summary>
/// The receiver's end of the shared-memory channel for one target process:
/// a named file mapping holding the protocol header and frame ring, and an
/// auto-reset event the hook sets after each published frame. The receiver
/// creates both before injecting, so the hook only ever runs where a Lookback
/// receiver asked for it. Reads use the protocol's seqlocks and never wait.
/// </summary>
public sealed unsafe class HookChannel : IDisposable
{
    private readonly nint _mapping;
    private readonly LbhHeader* _header;
    private int _disposed;

    internal HookChannel(uint targetPid)
    {
        uint size = (uint)sizeof(LbhHeader);
        _mapping = HookNative.CreateFileMapping(-1, 0, HookNative.PageReadWrite, 0, size, HookProtocol.ChannelName(targetPid));
        int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        if (_mapping == 0)
        {
            throw new Win32Exception(error, "Could not create the hook channel.");
        }

        if (error == HookNative.ErrorAlreadyExists)
        {
            HookNative.CloseHandle(_mapping);
            throw new InvalidOperationException($"A hook channel for process {targetPid} already exists.");
        }

        _header = (LbhHeader*)HookNative.MapViewOfFile(_mapping, HookNative.FileMapAllAccess, 0, 0, size);
        if (_header == null)
        {
            error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            HookNative.CloseHandle(_mapping);
            throw new Win32Exception(error, "Could not map the hook channel.");
        }

        FrameEvent = HookNative.CreateEvent(0, manualReset: false, initialState: false, HookProtocol.FrameEventName(targetPid));
        if (FrameEvent == 0)
        {
            error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            HookNative.UnmapViewOfFile((nint)_header);
            HookNative.CloseHandle(_mapping);
            throw new Win32Exception(error, "Could not create the hook frame event.");
        }

        new Span<byte>(_header, (int)size).Clear();
        _header->Magic = HookProtocol.Magic;
        _header->Version = HookProtocol.Version;
        _header->HeaderSize = size;
        _header->RingCapacity = HookProtocol.RingCapacity;
        _header->TargetPid = targetPid;
        _header->ReceiverPid = (uint)Environment.ProcessId;
        TargetPid = targetPid;
    }

    public uint TargetPid { get; }

    /// <summary>Set by the hook after each published frame.</summary>
    internal nint FrameEvent { get; }

    internal ref LbhHeader Header => ref *_header;

    public HookState State => (HookState)Volatile.Read(ref _header->State);

    /// <summary>The hook's last refusal or error text, or empty.</summary>
    public string Status
    {
        get
        {
            ReadOnlySpan<byte> s = _header->Status;
            int n = s.IndexOf((byte)0);
            return Encoding.UTF8.GetString(n < 0 ? s : s[..n]);
        }
    }

    public long QpcFrequency => Volatile.Read(ref _header->QpcFrequency);

    public long Published => Volatile.Read(ref _header->WriteIndex);

    public HookCounters Counters => new(
        Volatile.Read(ref _header->Presents),
        Volatile.Read(ref _header->WriteIndex),
        Volatile.Read(ref _header->Dropped),
        Volatile.Read(ref _header->DroppedBusy),
        Volatile.Read(ref _header->DroppedNotReady),
        Volatile.Read(ref _header->DroppedDevice));

    /// <summary>
    /// Reads the shared-texture configuration with its seqlock. False while
    /// the hook is changing it. Handles are values in the target process.
    /// </summary>
    internal bool TryReadConfig(out HookConfig config, Span<ulong> handles, out ulong fenceHandle, out long seq)
    {
        ref LbhHeader h = ref *_header;
        seq = Volatile.Read(ref h.ConfigSeq);
        config = default;
        fenceHandle = 0;
        if ((seq & 1) != 0)
        {
            return false;
        }

        config = new HookConfig(h.Generation, (HookApi)h.Api, (int)h.SlotCount, (int)h.Width, (int)h.Height, (int)h.Format, h.AdapterLuid);
        for (int i = 0; i < HookProtocol.MaxSlots && i < handles.Length; i++)
        {
            handles[i] = h.TextureHandles[i];
        }

        fenceHandle = h.FenceHandle;
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref h.ConfigSeq) == seq;
    }

    /// <summary>True when the configuration has not changed since it was read with sequence <paramref name="seq"/>.</summary>
    internal bool ConfigUnchanged(long seq) => Volatile.Read(ref _header->ConfigSeq) == seq;

    /// <summary>The newest complete ring record, or false when none is published or it is being overwritten.</summary>
    internal bool TryReadLatest(out LbhFrame frame)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            long n = Volatile.Read(ref _header->WriteIndex);
            if (n <= 0)
            {
                break;
            }

            long record = n - 1;
            ref LbhFrame slot = ref _header->Ring[(int)(record % HookProtocol.RingCapacity)];
            long seq = Volatile.Read(ref slot.Seq);
            if (seq != (2 * record) + 2)
            {
                continue; // overwritten by a newer record meanwhile
            }

            frame = slot;
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref slot.Seq) == seq)
            {
                return true;
            }
        }

        frame = default;
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        HookNative.UnmapViewOfFile((nint)_header);
        HookNative.CloseHandle(_mapping);
        HookNative.CloseHandle(FrameEvent);
    }
}
