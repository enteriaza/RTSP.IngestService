// Dedicated helper threads that join the managed H.264 encoder's row wavefront (H264WavefrontJob) for tests and
// benchmarks. Nothing in production creates a set, so production encoders code every picture serially. A set serves
// one encoder picture at a time; a picture that finds the set busy is coded by its encoding thread alone, with the
// same output. Helpers only add capacity: the encoding thread claims work like any helper, never waits for one to
// arrive, and before it returns waits only for attached helpers to leave. Between pictures a helper spins briefly,
// then blocks. A helper's failure is recorded in the job it was helping; the thread itself keeps serving.

using System.Diagnostics;

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>A point in a wavefront picture's lifecycle, reported to a <see cref="H264WavefrontProbe"/>.</summary>
internal enum H264WavefrontPoint
{
    /// <summary>A helper attached to a picture; row and column are -1.</summary>
    HelperAttach,

    /// <summary>A half-sample band was claimed; row is the band.</summary>
    BandClaim,

    /// <summary>A macroblock row was claimed.</summary>
    RowClaim,

    /// <summary>Coding of a claimed row starts.</summary>
    RowStart,

    /// <summary>A macroblock's dependencies are complete and its coding starts.</summary>
    MacroblockStart,

    /// <summary>A macroblock is coded, before its progress is published.</summary>
    MacroblockComplete,

    /// <summary>A macroblock's progress is published.</summary>
    ProgressPublished,

    /// <summary>A macroblock row is coded.</summary>
    RowComplete,

    /// <summary>A macroblock's dependencies in the deblocking pass are complete and its filtering starts.</summary>
    DeblockStart,

    /// <summary>A macroblock is deblocked, before its progress is published.</summary>
    DeblockComplete,

    /// <summary>A helper leaves a picture; row and column are -1.</summary>
    HelperLeave,

    /// <summary>The encoding thread completes a picture, after every helper left; worker is 0.</summary>
    PictureComplete,
}

/// <summary>A test callback invoked at <see cref="H264WavefrontPoint"/>s, on the worker's thread; it may delay, yield or throw.</summary>
/// <param name="point">The point.</param>
/// <param name="worker">0 for the encoding thread, 1 + the helper index otherwise.</param>
/// <param name="row">The macroblock row (or band), or -1.</param>
/// <param name="column">The macroblock column, or -1.</param>
internal delegate void H264WavefrontProbe(H264WavefrontPoint point, int worker, int row, int column);

/// <summary>A fixed set of helper threads serving one encoder picture at a time.</summary>
internal sealed class H264WavefrontHelpers : IDisposable
{
    /// <summary>The most helpers a set may have.</summary>
    public const int MaxCount = 63;

    /// <summary>How long <see cref="Dispose"/> waits for each helper thread to end.</summary>
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The longest a blocked idle helper sleeps before it rechecks for work.</summary>
    private static readonly TimeSpan IdleWaitTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The helper threads.</summary>
    private readonly Thread[] _threads;

    /// <summary>Wakes blocked helpers when a picture is published or the set is disposed.</summary>
    private readonly ManualResetEventSlim _wake = new(false, 0);

    /// <summary>How long an idle helper spins before it blocks, in Stopwatch ticks.</summary>
    private readonly long _idleSpinTicks;

    /// <summary>The published job, or null.</summary>
    private H264WavefrontJob? _job;

    /// <summary>Incremented for every published job.</summary>
    private long _generation;

    /// <summary>Set by <see cref="Dispose"/>: no further jobs are accepted and the threads end.</summary>
    private volatile bool _stopping;

    /// <summary>Helper attachments to pictures since construction.</summary>
    private long _attachments;

    /// <summary>Initializes a new instance of the <see cref="H264WavefrontHelpers"/> class and starts its threads.</summary>
    /// <param name="count">The helper count, 0 to <see cref="MaxCount"/>.</param>
    /// <param name="probe">An optional test probe.</param>
    /// <param name="idleSpin">How long an idle helper spins before blocking; null selects 200 microseconds.</param>
    /// <param name="dependencyLag">The macroblock lag of a row behind the row above: 2 is the H.264 dependency; smaller values exist only so tests can prove they detect a violation.</param>
    public H264WavefrontHelpers(int count, H264WavefrontProbe? probe = null, TimeSpan? idleSpin = null, int dependencyLag = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(dependencyLag, 1);
        Count = count;
        Probe = probe;
        DependencyLag = dependencyLag;
        _idleSpinTicks = (long)((idleSpin ?? TimeSpan.FromMicroseconds(200)).TotalSeconds * Stopwatch.Frequency);
        _threads = new Thread[count];
        for (var i = 0; i < count; i++)
        {
            _threads[i] = new Thread(Run) { IsBackground = true, Name = $"H.264 wavefront helper {i + 1}" };
            _threads[i].Start(i + 1);
        }
    }

    /// <summary>Gets the helper count.</summary>
    public int Count { get; }

    /// <summary>Gets the test probe, or null.</summary>
    public H264WavefrontProbe? Probe { get; }

    /// <summary>Gets the macroblock lag of a row behind the row above.</summary>
    public int DependencyLag { get; }

    /// <summary>Gets the helper attachments to pictures since construction.</summary>
    public long Attachments => Interlocked.Read(ref _attachments);

    /// <summary>Gets whether every helper thread has ended; true after a <see cref="Dispose"/> that joined them all.</summary>
    public bool Ended => Array.TrueForAll(_threads, static t => !t.IsAlive);

    /// <summary>Stops the threads and waits up to <see cref="JoinTimeout"/> for each; a helper inside a picture finishes it first.</summary>
    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _wake.Set();
        var joined = true;
        foreach (var thread in _threads)
        {
            joined &= thread.Join(JoinTimeout);
        }

        if (joined)
        {
            _wake.Dispose();
        }
    }

    /// <summary>Publishes an opened job to the helpers unless another job holds the set.</summary>
    /// <param name="job">The job, already open.</param>
    /// <returns>Whether the job was published; otherwise its owner codes it alone.</returns>
    internal bool TryPublish(H264WavefrontJob job)
    {
        if (_stopping || Interlocked.CompareExchange(ref _job, job, null) is not null)
        {
            return false;
        }

        Interlocked.Increment(ref _generation);
        _wake.Set();
        return true;
    }

    /// <summary>Withdraws a published job once its owner has closed it and every helper left.</summary>
    /// <param name="job">The job.</param>
    internal void Withdraw(H264WavefrontJob job)
    {
        if (Interlocked.CompareExchange(ref _job, null, job) == job && !_stopping)
        {
            _wake.Reset();
        }
    }

    /// <summary>A helper thread: helps each newly published job once, spinning and then blocking in between.</summary>
    /// <param name="state">The worker index, 1 + the helper index.</param>
    private void Run(object? state)
    {
        var worker = (int)state!;
        long seen = 0;
        while (!_stopping)
        {
            var generation = Interlocked.Read(ref _generation);
            if (generation != seen && Volatile.Read(ref _job) is { } job)
            {
                seen = generation;
                if (job.Help(worker))
                {
                    Interlocked.Increment(ref _attachments);
                }

                continue;
            }

            var start = Stopwatch.GetTimestamp();
            var spin = default(SpinWait);
            while (!_stopping && Interlocked.Read(ref _generation) == seen && Stopwatch.GetTimestamp() - start < _idleSpinTicks)
            {
                spin.SpinOnce(-1);
            }

            // A bounded wait: a Reset racing a Dispose's Set cannot strand the thread.
            if (!_stopping && Interlocked.Read(ref _generation) == seen)
            {
                _wake.Wait(IdleWaitTimeout);
            }
        }
    }
}
