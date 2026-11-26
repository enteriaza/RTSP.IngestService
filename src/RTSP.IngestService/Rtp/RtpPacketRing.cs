// The bounded hand-off between the media pipeline thread (the only producer) and an output's sender task (the only
// consumer): a ring of pre-allocated datagram buffers, so packetisation writes RTP packets in place and transmission
// sends them from the same memory, with no per-packet allocation and no lock. A producer writes a whole access unit
// into free slots and publishes it with one release write, or abandons it if the ring is too full; the consumer is
// woken through an allocation-free single-waiter signal.

using System.Threading.Tasks.Sources;

namespace RTSP.IngestService.Rtp;

/// <summary>A single-producer, single-consumer ring of pre-allocated packet buffers.</summary>
/// <remarks>
/// <para>
/// Producer: <see cref="TryWrite"/> (a slot at an offset from the published tail), <see cref="Publish"/>. Consumer:
/// <see cref="TryPeek"/>, <see cref="Advance"/>. Slots between the published tail and the head are owned by the
/// consumer; the rest by the producer. Publication and release use volatile writes, so slot contents written before
/// <see cref="Publish"/> are visible to the consumer that observes the new tail, and a slot released by
/// <see cref="Advance"/> is not reused before the consumer has finished with it.
/// </para>
/// <para>Each slot records the length of its packet and a channel byte (which destination it goes to).</para>
/// </remarks>
internal sealed class RtpPacketRing
{
    /// <summary>The slot buffers.</summary>
    private readonly byte[][] _slots;

    /// <summary>The packet length of each slot.</summary>
    private readonly int[] _lengths;

    /// <summary>The channel of each slot.</summary>
    private readonly byte[] _channels;

    /// <summary>Total packets released by the consumer.</summary>
    private long _head;

    /// <summary>Total packets published by the producer.</summary>
    private long _tail;

    /// <summary>Initializes a new instance of the <see cref="RtpPacketRing"/> class.</summary>
    /// <param name="capacity">The number of slots.</param>
    /// <param name="slotSize">The size of each slot: the largest packet.</param>
    public RtpPacketRing(int capacity, int slotSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(slotSize, RtpHeader.Length + 2);
        _slots = new byte[capacity][];
        for (var i = 0; i < capacity; i++)
        {
            _slots[i] = new byte[slotSize];
        }

        _lengths = new int[capacity];
        _channels = new byte[capacity];
        SlotSize = slotSize;
    }

    /// <summary>Gets the number of slots.</summary>
    public int Capacity => _slots.Length;

    /// <summary>Gets the size of each slot.</summary>
    public int SlotSize { get; }

    /// <summary>Gets the number of published packets not yet released; approximate from other threads.</summary>
    public int Count => (int)(Volatile.Read(ref _tail) - Volatile.Read(ref _head));

    /// <summary>Gets the number of free slots; exact for the producer, which is the only one that can reduce it.</summary>
    public int FreeSlots => Capacity - (int)(_tail - Volatile.Read(ref _head));

    /// <summary>Returns the buffer of the free slot <paramref name="offset"/> places after the published tail. Producer only.</summary>
    /// <param name="offset">Zero-based position within the batch being written.</param>
    /// <param name="slot">The whole slot buffer, of <see cref="SlotSize"/> bytes.</param>
    /// <returns><see langword="false"/> if the ring has no such free slot.</returns>
    public bool TryWrite(int offset, out Span<byte> slot)
    {
        if (offset >= FreeSlots)
        {
            slot = default;
            return false;
        }

        slot = _slots[(int)((_tail + offset) % Capacity)];
        return true;
    }

    /// <summary>Records the length and channel of a written slot. Producer only.</summary>
    /// <param name="offset">The slot's position within the batch.</param>
    /// <param name="length">The packet length.</param>
    /// <param name="channel">The destination channel.</param>
    public void Complete(int offset, int length, byte channel)
    {
        var index = (int)((_tail + offset) % Capacity);
        _lengths[index] = length;
        _channels[index] = channel;
    }

    /// <summary>Makes the first <paramref name="count"/> written slots visible to the consumer. Producer only.</summary>
    /// <param name="count">The number of packets written in the batch.</param>
    public void Publish(int count) => Volatile.Write(ref _tail, _tail + count);

    /// <summary>Returns the oldest published packet without releasing it. Consumer only.</summary>
    /// <param name="packet">The packet bytes, valid until <see cref="Advance"/>.</param>
    /// <param name="channel">Its channel.</param>
    /// <returns><see langword="false"/> if no packet is published.</returns>
    public bool TryPeek(out ReadOnlyMemory<byte> packet, out byte channel)
    {
        if (_head == Volatile.Read(ref _tail))
        {
            packet = default;
            channel = 0;
            return false;
        }

        var index = (int)(_head % Capacity);
        packet = _slots[index].AsMemory(0, _lengths[index]);
        channel = _channels[index];
        return true;
    }

    /// <summary>Releases the oldest published packet. Consumer only.</summary>
    public void Advance() => Volatile.Write(ref _head, _head + 1);
}

/// <summary>An auto-reset signal with at most one waiter whose waits do not allocate.</summary>
/// <remarks>
/// <see cref="Set"/> may be called from any thread at any time; a set with no waiter is remembered until the next
/// <see cref="WaitAsync"/>, which then completes synchronously. Only one <see cref="WaitAsync"/> may be outstanding.
/// </remarks>
internal sealed class RtpSenderSignal : IValueTaskSource
{
    /// <summary>No waiter, not set.</summary>
    private const int Idle = 0;

    /// <summary>Set, not yet consumed.</summary>
    private const int Signaled = 1;

    /// <summary>A waiter is pending.</summary>
    private const int Waiting = 2;

    /// <summary>The reusable completion source.</summary>
    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };

    /// <summary>One of <see cref="Idle"/>, <see cref="Signaled"/> or <see cref="Waiting"/>.</summary>
    private int _state;

    /// <summary>Wakes the waiter, or makes the next wait complete at once.</summary>
    public void Set()
    {
        while (true)
        {
            switch (Volatile.Read(ref _state))
            {
                case Signaled:
                    return;
                case Idle when Interlocked.CompareExchange(ref _state, Signaled, Idle) == Idle:
                    return;
                case Waiting when Interlocked.CompareExchange(ref _state, Idle, Waiting) == Waiting:
                    _core.SetResult(true);
                    return;
            }
        }
    }

    /// <summary>Waits until <see cref="Set"/> is called, or returns at once if it already was.</summary>
    /// <returns>A task that completes when signalled.</returns>
    public ValueTask WaitAsync()
    {
        if (Interlocked.CompareExchange(ref _state, Idle, Signaled) == Signaled)
        {
            return ValueTask.CompletedTask;
        }

        _core.Reset();
        if (Interlocked.CompareExchange(ref _state, Waiting, Idle) == Idle)
        {
            return new ValueTask(this, _core.Version);
        }

        // Set ran between the two exchanges and left the state Signaled; consume it.
        Volatile.Write(ref _state, Idle);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    void IValueTaskSource.GetResult(short token) => _core.GetResult(token);

    /// <inheritdoc />
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

    /// <inheritdoc />
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);
}
