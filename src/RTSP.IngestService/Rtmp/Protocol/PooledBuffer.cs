// Growable byte buffer rented from an ArrayPool. It is the unit of ownership for RTMP message payloads: the chunk
// decoder assembles each message into one, and ownership then moves with the message until a single owner disposes
// it, which returns the array to the pool.

using System.Buffers;

namespace RTSP.IngestService.Rtmp.Protocol;

/// <summary>
/// A pooled, growable payload buffer with a single owner.
/// </summary>
/// <remarks>
/// Not thread-safe for writing. <see cref="Dispose"/> is idempotent and safe to call concurrently with itself; after
/// disposal the memory previously returned by <see cref="Memory"/> belongs to the pool and must not be used.
/// </remarks>
internal sealed class PooledBuffer : IDisposable
{
    /// <summary>Pool the array is rented from and returned to.</summary>
    private readonly ArrayPool<byte> _pool;

    /// <summary>The rented array, or <see langword="null"/> before the first allocation and after disposal.</summary>
    private byte[]? _array;

    /// <summary>Number of bytes written.</summary>
    private int _length;

    /// <summary>1 once disposed.</summary>
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PooledBuffer"/> class. No array is rented until
    /// <see cref="EnsureCapacity"/> is called.
    /// </summary>
    /// <param name="pool">Pool to rent from.</param>
    public PooledBuffer(ArrayPool<byte> pool)
    {
        _pool = pool;
    }

    /// <summary>Gets the number of bytes written.</summary>
    public int Length => _length;

    /// <summary>Gets the length of the rented array, which may exceed the requested capacity.</summary>
    public int Capacity => _array?.Length ?? 0;

    /// <summary>Gets the written bytes. The memory is valid only until <see cref="Dispose"/> is called.</summary>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _array is null ? ReadOnlyMemory<byte>.Empty : _array.AsMemory(0, _length);
        }
    }

    /// <summary>
    /// Ensures the buffer can hold at least <paramref name="capacity"/> bytes, renting a larger array and copying the
    /// written bytes if necessary.
    /// </summary>
    /// <param name="capacity">The required capacity.</param>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public void EnsureCapacity(int capacity)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (capacity <= Capacity)
        {
            return;
        }

        var next = _pool.Rent(capacity);
        if (_array is not null)
        {
            _array.AsSpan(0, _length).CopyTo(next);
            _pool.Return(_array);
        }

        _array = next;
    }

    /// <summary>Appends <paramref name="data"/>; the capacity must already be sufficient.</summary>
    /// <param name="data">Bytes to append.</param>
    /// <exception cref="InvalidOperationException">The capacity is insufficient.</exception>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public void Append(in ReadOnlySequence<byte> data)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var count = checked((int)data.Length);
        if (count == 0)
        {
            return;
        }

        if (_array is null || _length + count > _array.Length)
        {
            throw new InvalidOperationException("The buffer capacity is insufficient.");
        }

        data.CopyTo(_array.AsSpan(_length));
        _length += count;
    }

    /// <summary>Returns the array to the pool. Subsequent calls do nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_array is { } array)
        {
            _array = null;
            _pool.Return(array);
        }
    }
}
