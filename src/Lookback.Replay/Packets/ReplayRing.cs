using System.Runtime.InteropServices;

namespace Lookback.Replay.Packets;

/// <summary>
/// The RAM replay tier: encoded packets of every track in one circular
/// native-memory arena, indexed by sequence number and QPC. Encoder threads
/// append without allocating (the payload is copied into the arena under a
/// short lock); the oldest packets fall out when the byte budget, the slot
/// count or the duration cap is reached. Clip cutting and the disk segment
/// writer read from it off the hot path.
/// </summary>
public sealed unsafe class ReplayRing : IPacketSink, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, TrackInfo> _tracks = [];
    private byte* _arena;
    private long _capacity;
    private Entry* _entries;
    private int _slots;
    private long _maxDuration;

    private long _head;  // sequence number of the next packet
    private long _tail;  // sequence number of the oldest retained packet
    private long _writePos;
    private bool _wrapped;
    private long _bytesUsed;
    private long _dropped;
    private int _disposed;

    /// <param name="capacityBytes">Arena size; the governor derives it from available RAM.</param>
    /// <param name="maxDurationQpc">Oldest-to-newest span kept, in QPC ticks (0 = limited by bytes only).</param>
    /// <param name="maxPackets">Index slots; 2 minutes at 240 fps video plus audio fits in 65,536.</param>
    public ReplayRing(long capacityBytes, long maxDurationQpc, int maxPackets = 1 << 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 4096);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDurationQpc);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPackets, 16);
        Allocate(capacityBytes, maxPackets);
        _maxDuration = maxDurationQpc;
    }

    public long CapacityBytes => Volatile.Read(ref _capacity);

    public long MaxDurationQpc
    {
        get { lock (_lock) { return _maxDuration; } }
    }

    /// <summary>Packets refused because they were larger than the whole arena.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Sequence number the next packet will get.</summary>
    public long HeadSequence
    {
        get { lock (_lock) { return _head; } }
    }

    /// <summary>Sequence number of the oldest packet still held.</summary>
    public long TailSequence
    {
        get { lock (_lock) { return _tail; } }
    }

    public int Count
    {
        get { lock (_lock) { return (int)(_head - _tail); } }
    }

    public long BytesUsed
    {
        get { lock (_lock) { return _bytesUsed; } }
    }

    /// <summary>QPC span from the oldest retained video keyframe to the newest packet, or 0.</summary>
    public long SpanQpc
    {
        get
        {
            lock (_lock)
            {
                long first = FirstKeyframeIndex();
                return first < 0 ? 0 : _entries[(_head - 1) % _slots].Info.DtsQpc - _entries[first % _slots].Info.DtsQpc;
            }
        }
    }

    public IReadOnlyList<TrackInfo> Tracks
    {
        get { lock (_lock) { return [.. _tracks.Values.OrderBy(t => t.Id)]; } }
    }

    public void OnTrack(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_lock)
        {
            _tracks[track.Id] = track;
        }
    }

    public void OnPacket(in PacketInfo info, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (payload.Length > _capacity)
            {
                _dropped++;
                return;
            }

            if (_head - _tail == _slots)
            {
                EvictOldest();
            }

            long at = Place(payload.Length);
            payload.CopyTo(new Span<byte>(_arena + at, payload.Length));
            _entries[_head % _slots] = new Entry(info, at, payload.Length);
            _head++;
            _bytesUsed += payload.Length;

            if (_maxDuration > 0)
            {
                while (_head - _tail > 1 && info.DtsQpc - _entries[_tail % _slots].Info.DtsQpc > _maxDuration)
                {
                    EvictOldest();
                }
            }
        }
    }

    /// <summary>
    /// Changes the budget (the governor does this when the tier changes).
    /// Packets that still fit are kept; the arena is reallocated only when
    /// it has to grow or shrinks by more than half.
    /// </summary>
    public void Reconfigure(long capacityBytes, long maxDurationQpc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 4096);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDurationQpc);
        lock (_lock)
        {
            _maxDuration = maxDurationQpc;
            if (capacityBytes == _capacity || (capacityBytes < _capacity && capacityBytes > _capacity / 2))
            {
                while (_bytesUsed > capacityBytes && _head > _tail)
                {
                    EvictOldest();
                }

                return;
            }

            // Copy the newest packets that fit into a fresh arena.
            long keepFrom = _head;
            long bytes = 0;
            while (keepFrom > _tail && bytes + _entries[(keepFrom - 1) % _slots].Length <= capacityBytes)
            {
                keepFrom--;
                bytes += _entries[keepFrom % _slots].Length;
            }

            byte* oldArena = _arena;
            Entry* oldEntries = _entries;
            int oldSlots = _slots;
            long oldHead = _head;
            Allocate(capacityBytes, oldSlots);

            long pos = 0;
            for (long s = keepFrom; s < oldHead; s++)
            {
                Entry e = oldEntries[s % oldSlots];
                Buffer.MemoryCopy(oldArena + e.Offset, _arena + pos, e.Length, e.Length);
                _entries[s % _slots] = e with { Offset = pos };
                pos += e.Length;
            }

            _tail = keepFrom;
            _head = oldHead;
            _writePos = pos;
            _wrapped = false;
            _bytesUsed = bytes;
            NativeMemory.Free(oldArena);
            NativeMemory.Free(oldEntries);
        }
    }

    /// <summary>
    /// Copies up to <paramref name="max"/> packets starting at sequence
    /// <paramref name="from"/> into <paramref name="into"/>. Returns the next
    /// sequence to ask for. If <paramref name="from"/> has already been
    /// evicted, reading resumes at the oldest packet and <paramref name="missed"/>
    /// says how many were lost.
    /// </summary>
    public long ReadFrom(long from, List<EncodedPacket> into, int max, out long missed)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_lock)
        {
            missed = Math.Max(0, _tail - from);
            long s = Math.Max(from, _tail);
            int n = 0;
            for (; s < _head && n < max; s++, n++)
            {
                into.Add(Copy(_entries[s % _slots]));
            }

            return s;
        }
    }

    /// <summary>
    /// The packets for a clip: from the last video keyframe at or before
    /// <paramref name="fromQpc"/> (or the oldest keyframe held) up to
    /// <paramref name="toQpc"/>. Returns false if the ring holds no keyframe
    /// at or before <paramref name="fromQpc"/>, meaning the start of the
    /// range has already left RAM.
    /// </summary>
    public bool TrySnapshot(long fromQpc, long toQpc, out ClipSource clip)
    {
        lock (_lock)
        {
            long first = FirstKeyframeIndex();
            long start = -1;
            for (long s = first; s >= 0 && s < _head; s++)
            {
                Entry e = _entries[s % _slots];
                if (IsVideo(e.Info.TrackId) && e.Info.IsKeyframe)
                {
                    if (e.Info.PtsQpc > fromQpc)
                    {
                        break;
                    }

                    start = s;
                }
            }

            if (start < 0)
            {
                clip = new ClipSource([], []);
                return false;
            }

            long startQpc = _entries[start % _slots].Info.DtsQpc;
            var packets = new List<EncodedPacket>();
            for (long s = _tail; s < _head; s++)
            {
                Entry e = _entries[s % _slots];
                bool video = IsVideo(e.Info.TrackId);
                if (video ? s >= start && e.Info.DtsQpc < toQpc : e.Info.PtsQpc >= startQpc && e.Info.PtsQpc < toQpc)
                {
                    packets.Add(Copy(e));
                }
            }

            clip = new ClipSource([.. _tracks.Values.OrderBy(t => t.Id)], packets);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            NativeMemory.Free(_arena);
            NativeMemory.Free(_entries);
            _arena = null;
            _entries = null;
        }
    }

    private void Allocate(long capacityBytes, int slots)
    {
        _arena = (byte*)NativeMemory.Alloc((nuint)capacityBytes);
        _entries = (Entry*)NativeMemory.Alloc((nuint)slots, (nuint)sizeof(Entry));
        Volatile.Write(ref _capacity, capacityBytes);
        _slots = slots;
    }

    private bool IsVideo(int trackId) => _tracks.TryGetValue(trackId, out TrackInfo? t) && t.IsVideo;

    private long FirstKeyframeIndex()
    {
        for (long s = _tail; s < _head; s++)
        {
            Entry e = _entries[s % _slots];
            if (e.Info.IsKeyframe && IsVideo(e.Info.TrackId))
            {
                return s;
            }
        }

        return -1;
    }

    private EncodedPacket Copy(in Entry e) =>
        new(e.Info, new ReadOnlySpan<byte>(_arena + e.Offset, e.Length).ToArray());

    /// <summary>Finds room for <paramref name="length"/> contiguous bytes, evicting the oldest packets as needed.</summary>
    private long Place(int length)
    {
        while (true)
        {
            if (_head == _tail)
            {
                _writePos = 0;
                _wrapped = false;
                return Commit(length);
            }

            long oldest = _entries[_tail % _slots].Offset;
            if (!_wrapped)
            {
                if (_writePos + length <= _capacity)
                {
                    return Commit(length);
                }

                // Not enough room before the end: continue from the start.
                _writePos = 0;
                _wrapped = true;
                continue;
            }

            if (_writePos + length <= oldest)
            {
                return Commit(length);
            }

            EvictOldest();
        }
    }

    private long Commit(int length)
    {
        long at = _writePos;
        _writePos += length;
        return at;
    }

    private void EvictOldest()
    {
        Entry e = _entries[_tail % _slots];
        _bytesUsed -= e.Length;
        _tail++;
        if (_head == _tail)
        {
            _writePos = 0;
            _wrapped = false;
        }
        else if (_wrapped && _entries[_tail % _slots].Offset < e.Offset)
        {
            // Every packet from the previous lap is gone.
            _wrapped = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Entry(PacketInfo Info, long Offset, int Length);
}

/// <summary>Tracks and packets for one clip, ready to mux.</summary>
public sealed record ClipSource(IReadOnlyList<TrackInfo> Tracks, IReadOnlyList<EncodedPacket> Packets);
