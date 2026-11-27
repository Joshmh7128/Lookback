using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lookback.Core.Buffers;

/// <summary>
/// Bounded single-producer, single-consumer ring of unmanaged values in native
/// memory. No allocations, no locks and nothing for the GC to scan, so it is
/// safe on the capture and audio threads. Exactly one thread may call
/// <see cref="TryWrite"/> and exactly one thread may call <see cref="TryRead"/>.
/// </summary>
public sealed unsafe class SpscRing<T> : IDisposable
    where T : unmanaged
{
    private readonly T* _buffer;
    private readonly long _mask;

    // Head and tail sit on separate cache lines so producer and consumer do not false-share.
    private PaddedCounter _head; // next slot to write (producer-owned)
    private PaddedCounter _tail; // next slot to read (consumer-owned)
    private int _disposed;

    /// <param name="capacity">Rounded up to the next power of two.</param>
    public SpscRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        Capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)capacity);
        _mask = Capacity - 1;
        _buffer = (T*)NativeMemory.AlignedAlloc((nuint)(Capacity * sizeof(T)), 64);
    }

    public int Capacity { get; }

    /// <summary>Approximate number of items waiting; exact when called from either owner thread.</summary>
    public int Count => (int)(Volatile.Read(ref _head.Value) - Volatile.Read(ref _tail.Value));

    public bool TryWrite(in T item)
    {
        long head = _head.Value;
        if (head - Volatile.Read(ref _tail.Value) >= Capacity)
        {
            return false;
        }

        _buffer[head & _mask] = item;
        Volatile.Write(ref _head.Value, head + 1); // publish after the slot is written
        return true;
    }

    public bool TryRead(out T item)
    {
        long tail = _tail.Value;
        if (tail >= Volatile.Read(ref _head.Value))
        {
            item = default;
            return false;
        }

        item = _buffer[tail & _mask];
        Volatile.Write(ref _tail.Value, tail + 1); // release the slot after it is copied out
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            NativeMemory.AlignedFree(_buffer);
        }
    }
}

/// <summary>A counter alone on its cache line (explicit layout cannot live inside a generic type).</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct PaddedCounter
{
    [FieldOffset(64)]
    public long Value;
}
