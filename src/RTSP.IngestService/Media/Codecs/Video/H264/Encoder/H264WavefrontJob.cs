// One encoder's row wavefront over a picture, worked by the encoding thread (worker 0) and any attached
// H264WavefrontHelpers threads (workers 1 to N), each with its own macroblock context, in three passes:
//  1. the half-sample planes of the first reference, in bands of output rows claimed one at a time; each band
//     recomputes the horizontal sums of the rows around its first row, so every output row is computed exactly as the
//     serial pass computes it, and macroblock coding starts only once every band is complete;
//  2. macroblock rows, claimed one at a time in raster order; macroblock (x, y) starts only after row y - 1 has
//     published column min(x + 2, W) - 1. Its left, top-left, top and top-right neighbours are the only macroblocks
//     any prediction, motion-vector, nC, slice-availability or I_PCM derivation reads, so every read sees exactly the
//     state the serial raster order gives it, and the records the serialiser later reads in raster order are the same;
//  3. deblocking rows, claimed one at a time once every macroblock is coded; macroblock (x, y) is filtered only after
//     (x + 1, y - 1), the last earlier macroblock whose filtering touches samples (x, y) reads or writes, so every
//     sample is filtered in the serial raster order.
// Claims are interlocked increments, so every band, row and macroblock has exactly one owner. Row progress is
// published with release stores and read with acquire loads, so a worker only reads what the publishing worker wrote
// before publishing. Waits spin (they are within a picture) and observe the abort flag and the cancellation token. The
// first failure is recorded and stops further claims; the encoding thread rethrows it after every helper has left, so
// no helper touches the picture, its references or the records once the encoding thread returns. Nothing is
// allocated per picture.

using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>What one worker did in a wavefront picture; times are Stopwatch ticks.</summary>
internal struct H264WavefrontWorkerStatistics
{
    /// <summary>When the worker attached, or 0 if it did not.</summary>
    public long Attached;

    /// <summary>When the worker left.</summary>
    public long Left;

    /// <summary>Time in the half-sample pass, waiting for other bands included.</summary>
    public long PrepareTicks;

    /// <summary>Time waiting for other workers' half-sample bands.</summary>
    public long PrepareWaitTicks;

    /// <summary>Time in the macroblock pass, waiting included.</summary>
    public long CodeTicks;

    /// <summary>Time waiting for the row above (and, for the encoding thread, for the last rows).</summary>
    public long CodeWaitTicks;

    /// <summary>Time in the deblocking pass, waiting included.</summary>
    public long DeblockTicks;

    /// <summary>Time waiting in the deblocking pass.</summary>
    public long DeblockWaitTicks;

    /// <summary>Half-sample bands computed.</summary>
    public int Bands;

    /// <summary>Macroblock rows coded.</summary>
    public int Rows;

    /// <summary>Rows deblocked.</summary>
    public int DeblockRows;
}

/// <summary>The wavefront state of one encoder, reused for every picture.</summary>
internal sealed class H264WavefrontJob
{
    /// <summary>The state bit that refuses new attachments.</summary>
    private const int Closed = 1 << 30;

    /// <summary>The distance in ints between row progress counters: one cache line each.</summary>
    private const int Stride = 16;

    /// <summary>The least half-sample band height in luma rows.</summary>
    private const int MinBandRows = 32;

    /// <summary>The half-sample planes.</summary>
    private readonly H264HalfSamplePlanes _halfSamples;

    /// <summary>The macroblock records.</summary>
    private readonly H264MacroblockRecords _records;

    /// <summary>One macroblock context per worker.</summary>
    private readonly H264MacroblockContext[] _contexts;

    /// <summary>The picture width in macroblocks.</summary>
    private readonly int _width;

    /// <summary>The picture height in macroblocks.</summary>
    private readonly int _rows;

    /// <summary>The slice of each macroblock row.</summary>
    private readonly int[] _rowSlice;

    /// <summary>Coded columns per row, at <see cref="Stride"/>.</summary>
    private readonly int[] _coded;

    /// <summary>Deblocked columns per row, at <see cref="Stride"/>.</summary>
    private readonly int[] _deblocked;

    /// <summary>When each row was coded.</summary>
    private readonly long[] _rowEnd;

    /// <summary>The worker that coded each row.</summary>
    private readonly byte[] _rowWorker;

    /// <summary>Per-worker statistics.</summary>
    private readonly H264WavefrontWorkerStatistics[] _statistics;

    /// <summary>The macroblock lag of a row behind the row above.</summary>
    private readonly int _lag;

    /// <summary>The test probe, or null.</summary>
    private readonly H264WavefrontProbe? _probe;

    /// <summary>Attached helpers, plus <see cref="Closed"/> while attachments are refused.</summary>
    private int _state = Closed;

    /// <summary>The picture being coded.</summary>
    private H264EncoderPicture? _picture;

    /// <summary>The reference whose half-sample planes are computed, or null.</summary>
    private H264EncoderPicture? _reference;

    /// <summary>The picture's cancellation token.</summary>
    private CancellationToken _token;

    /// <summary>When the picture started.</summary>
    private long _started;

    /// <summary>Half-sample bands, 0 when the planes need no update.</summary>
    private int _bands;

    /// <summary>The luma rows per band.</summary>
    private int _bandRows;

    /// <summary>The next band to claim.</summary>
    private int _nextBand;

    /// <summary>Bands computed.</summary>
    private int _bandsDone;

    /// <summary>1 once the planes are complete.</summary>
    private int _prepared;

    /// <summary>The next macroblock row to claim.</summary>
    private int _nextRow;

    /// <summary>Rows coded.</summary>
    private int _rowsDone;

    /// <summary>1 once the deblocking pass may start.</summary>
    private int _deblockOpen;

    /// <summary>The next row to deblock.</summary>
    private int _nextDeblockRow;

    /// <summary>Rows deblocked.</summary>
    private int _deblockRowsDone;

    /// <summary>1 once the picture is abandoned: no further claims.</summary>
    private int _abort;

    /// <summary>The first failure, or null.</summary>
    private Exception? _error;

    /// <summary>Initializes a new instance of the <see cref="H264WavefrontJob"/> class.</summary>
    /// <param name="layout">The sequence layout.</param>
    /// <param name="halfSamples">The half-sample planes.</param>
    /// <param name="records">The records, with one payload per context.</param>
    /// <param name="contexts">One macroblock context per worker, each with band sums.</param>
    /// <param name="helpers">The helpers the job is published to.</param>
    public H264WavefrontJob(H264SequenceLayout layout, H264HalfSamplePlanes halfSamples, H264MacroblockRecords records, H264MacroblockContext[] contexts, H264WavefrontHelpers helpers)
    {
        _halfSamples = halfSamples;
        _records = records;
        _contexts = contexts;
        _width = layout.WidthInMbs;
        _rows = layout.HeightInMbs;
        _rowSlice = new int[_rows];
        _coded = new int[_rows * Stride];
        _deblocked = new int[_rows * Stride];
        _rowEnd = new long[_rows];
        _rowWorker = new byte[_rows];
        _statistics = new H264WavefrontWorkerStatistics[contexts.Length];
        _lag = helpers.DependencyLag;
        _probe = helpers.Probe;
    }

    /// <summary>Gets when the last picture started, in Stopwatch ticks.</summary>
    public long Started => _started;

    /// <summary>Gets the half-sample bands of the last picture.</summary>
    public int Bands => _bands;

    /// <summary>Gets the per-worker statistics of the last picture.</summary>
    public ReadOnlySpan<H264WavefrontWorkerStatistics> Workers => _statistics;

    /// <summary>Gets when each row of the last picture was coded.</summary>
    public ReadOnlySpan<long> RowEnds => _rowEnd;

    /// <summary>Gets the worker that coded each row of the last picture.</summary>
    public ReadOnlySpan<byte> RowWorkers => _rowWorker;

    /// <summary>Returns the bytes a job holds beyond its contexts and records.</summary>
    /// <param name="layout">The sequence layout.</param>
    /// <param name="workers">The workers, 1 + the helper count.</param>
    /// <returns>The bytes.</returns>
    public static long ByteCount(H264SequenceLayout layout, int workers) => (layout.HeightInMbs * ((2L * Stride * 4) + 4 + 8 + 1)) + (workers * 80L);

    /// <summary>Prepares a picture; the encoding thread then opens the job.</summary>
    /// <param name="picture">The picture being coded.</param>
    /// <param name="reference">The reference whose half-sample planes must be computed, or null.</param>
    /// <param name="slices">The macroblock rows at which each slice starts, then the row count.</param>
    /// <param name="cancellationToken">The picture's cancellation token.</param>
    public void Begin(H264EncoderPicture picture, H264EncoderPicture? reference, ReadOnlySpan<int> slices, CancellationToken cancellationToken)
    {
        _started = Stopwatch.GetTimestamp();
        _picture = picture;
        _token = cancellationToken;
        for (var s = 0; s < slices.Length - 1; s++)
        {
            _rowSlice.AsSpan(slices[s], slices[s + 1] - slices[s]).Fill(s);
        }

        _reference = reference is not null && _halfSamples.NeedsPrepare(reference) ? reference : null;
        var height = _rows * 16;
        _bandRows = Math.Max(MinBandRows, (((height + (2 * _contexts.Length) - 1) / (2 * _contexts.Length)) + 15) & ~15);
        _bands = _reference is null ? 0 : (height + _bandRows - 1) / _bandRows;
        _prepared = _bands == 0 ? 1 : 0;
        if (_reference is not null)
        {
            _halfSamples.Invalidate();
        }

        _nextBand = _bandsDone = 0;
        _nextRow = _rowsDone = 0;
        _deblockOpen = _nextDeblockRow = _deblockRowsDone = 0;
        _abort = 0;
        _error = null;
        for (var row = 0; row < _rows; row++)
        {
            _coded[row * Stride] = 0;
            _deblocked[row * Stride] = 0;
        }

        _statistics.AsSpan().Clear();
    }

    /// <summary>Admits helpers; every field <see cref="Begin"/> set is published to them first.</summary>
    public void Open() => Volatile.Write(ref _state, 0);

    /// <summary>Refuses new helpers and waits until every attached helper has left.</summary>
    public void Close()
    {
        Interlocked.Or(ref _state, Closed);
        var spin = default(SpinWait);
        while ((Volatile.Read(ref _state) & ~Closed) != 0)
        {
            spin.SpinOnce(-1);
        }
    }

    /// <summary>Runs the half-sample and macroblock passes as the encoding thread, then waits for every row.</summary>
    /// <returns>Whether every row is coded; false when the picture was abandoned.</returns>
    public bool Code()
    {
        ref var statistics = ref _statistics[0];
        return Code(0) && Wait(ref _rowsDone, _rows, ref statistics.CodeWaitTicks);
    }

    /// <summary>Lets helpers start deblocking: every macroblock is coded.</summary>
    public void OpenDeblocking() => Volatile.Write(ref _deblockOpen, 1);

    /// <summary>Runs the deblocking pass as the encoding thread, then waits for every row.</summary>
    /// <returns>Whether every row is deblocked; false when the picture was abandoned.</returns>
    public bool Deblock()
    {
        ref var statistics = ref _statistics[0];
        return Deblock(0) && Wait(ref _deblockRowsDone, _rows, ref statistics.DeblockWaitTicks);
    }

    /// <summary>Records a failure and abandons the picture; cancellation is recorded as cancellation.</summary>
    /// <param name="error">The failure.</param>
    public void Fail(Exception error)
    {
        if (error is not OperationCanceledException || !_token.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref _error, error, null);
        }

        Volatile.Write(ref _abort, 1);
    }

    /// <summary>Ends a closed picture: drops its references and rethrows its first failure or cancellation.</summary>
    public void Complete()
    {
        _picture = null;
        _reference = null;
        foreach (var context in _contexts)
        {
            context.EndPicture();
        }

        if (_error is { } error)
        {
            ExceptionDispatchInfo.Throw(error);
        }

        if (_abort != 0)
        {
            _token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The wavefront picture was abandoned without a failure.");
        }

        _probe?.Invoke(H264WavefrontPoint.PictureComplete, 0, -1, -1);
    }

    /// <summary>Helps the current picture as a helper, unless it is closed.</summary>
    /// <param name="worker">The worker index, 1 + the helper index.</param>
    /// <returns>Whether the helper attached.</returns>
    public bool Help(int worker)
    {
        var state = Volatile.Read(ref _state);
        while (true)
        {
            if ((state & Closed) != 0 || worker >= _contexts.Length)
            {
                return false;
            }

            var seen = Interlocked.CompareExchange(ref _state, state + 1, state);
            if (seen == state)
            {
                break;
            }

            state = seen;
        }

        // A helper that read a stale generation may find the same picture again; it has already helped it.
        ref var statistics = ref _statistics[worker];
        if (statistics.Attached != 0)
        {
            Interlocked.Decrement(ref _state);
            return false;
        }

        statistics.Attached = Stopwatch.GetTimestamp();
        try
        {
            _probe?.Invoke(H264WavefrontPoint.HelperAttach, worker, -1, -1);
            if (Code(worker) && Wait(ref _deblockOpen, 1, ref statistics.DeblockWaitTicks))
            {
                _ = Deblock(worker);
            }

            _probe?.Invoke(H264WavefrontPoint.HelperLeave, worker, -1, -1);
        }
        catch (Exception e)
        {
            Fail(e);
        }
        finally
        {
            statistics.Left = Stopwatch.GetTimestamp();
            Interlocked.Decrement(ref _state);
        }

        return true;
    }

    /// <summary>Computes claimed half-sample bands until none is left, then waits for the planes.</summary>
    /// <param name="worker">The worker.</param>
    /// <returns>Whether the planes are complete.</returns>
    private bool Prepare(int worker)
    {
        ref var statistics = ref _statistics[worker];
        var started = Stopwatch.GetTimestamp();
        var sums = _contexts[worker].BandSums;
        while (Volatile.Read(ref _abort) == 0)
        {
            var band = Interlocked.Increment(ref _nextBand) - 1;
            if (band >= _bands)
            {
                break;
            }

            _probe?.Invoke(H264WavefrontPoint.BandClaim, worker, band, -1);
            var first = band * _bandRows;
            _halfSamples.PrepareRows(_reference!.Luma, first, Math.Min(first + _bandRows, _rows * 16), sums);
            statistics.Bands++;
            if (Interlocked.Increment(ref _bandsDone) == _bands)
            {
                _halfSamples.Complete(_reference);
                Volatile.Write(ref _prepared, 1);
            }
        }

        var ready = Wait(ref _prepared, 1, ref statistics.PrepareWaitTicks);
        statistics.PrepareTicks = Stopwatch.GetTimestamp() - started;
        return ready;
    }

    /// <summary>Runs the half-sample pass, then codes claimed rows until none is left.</summary>
    /// <param name="worker">The worker.</param>
    /// <returns>Whether the worker ran out of rows; false when the picture was abandoned.</returns>
    private bool Code(int worker)
    {
        if (!Prepare(worker))
        {
            return false;
        }

        ref var statistics = ref _statistics[worker];
        var started = Stopwatch.GetTimestamp();
        var done = CodeRows(worker, ref statistics);
        statistics.CodeTicks = Stopwatch.GetTimestamp() - started;
        return done;
    }

    /// <summary>Codes claimed macroblock rows until none is left.</summary>
    /// <param name="worker">The worker.</param>
    /// <param name="statistics">The worker's statistics.</param>
    /// <returns>Whether the worker ran out of rows.</returns>
    private bool CodeRows(int worker, ref H264WavefrontWorkerStatistics statistics)
    {
        var context = _contexts[worker];
        while (Volatile.Read(ref _abort) == 0)
        {
            if (_token.IsCancellationRequested)
            {
                Volatile.Write(ref _abort, 1);
                return false;
            }

            var row = Interlocked.Increment(ref _nextRow) - 1;
            if (row >= _rows)
            {
                return true;
            }

            _probe?.Invoke(H264WavefrontPoint.RowClaim, worker, row, -1);
            context.BeginSlice(_rowSlice[row]);
            _probe?.Invoke(H264WavefrontPoint.RowStart, worker, row, 0);
            ref var published = ref _coded[row * Stride];
            var first = row * _width;
            for (var x = 0; x < _width; x++)
            {
                if (Volatile.Read(ref _abort) != 0 || (row > 0 && !Wait(ref _coded[(row - 1) * Stride], Math.Min(x + _lag, _width), ref statistics.CodeWaitTicks)))
                {
                    return false;
                }

                _probe?.Invoke(H264WavefrontPoint.MacroblockStart, worker, row, x);
                context.Encode(first + x, _records);
                _probe?.Invoke(H264WavefrontPoint.MacroblockComplete, worker, row, x);
                Volatile.Write(ref published, x + 1);
                _probe?.Invoke(H264WavefrontPoint.ProgressPublished, worker, row, x);
            }

            _rowEnd[row] = Stopwatch.GetTimestamp();
            _rowWorker[row] = (byte)worker;
            statistics.Rows++;
            Interlocked.Increment(ref _rowsDone);
            _probe?.Invoke(H264WavefrontPoint.RowComplete, worker, row, -1);
        }

        return false;
    }

    /// <summary>Deblocks claimed rows until none is left.</summary>
    /// <param name="worker">The worker.</param>
    /// <returns>Whether the worker ran out of rows; false when the picture was abandoned.</returns>
    private bool Deblock(int worker)
    {
        ref var statistics = ref _statistics[worker];
        var started = Stopwatch.GetTimestamp();
        Span<int> bs = stackalloc int[16];
        var picture = _picture!;
        var done = false;
        while (Volatile.Read(ref _abort) == 0)
        {
            if (_token.IsCancellationRequested)
            {
                Volatile.Write(ref _abort, 1);
                break;
            }

            var row = Interlocked.Increment(ref _nextDeblockRow) - 1;
            if (row >= _rows)
            {
                done = true;
                break;
            }

            ref var published = ref _deblocked[row * Stride];
            var aborted = false;
            for (var x = 0; x < _width; x++)
            {
                if (Volatile.Read(ref _abort) != 0 || (row > 0 && !Wait(ref _deblocked[(row - 1) * Stride], Math.Min(x + _lag, _width), ref statistics.DeblockWaitTicks)))
                {
                    aborted = true;
                    break;
                }

                _probe?.Invoke(H264WavefrontPoint.DeblockStart, worker, row, x);
                H264EncoderDeblocking.FilterMacroblock(picture, x, row, bs);
                _probe?.Invoke(H264WavefrontPoint.DeblockComplete, worker, row, x);
                Volatile.Write(ref published, x + 1);
            }

            if (aborted)
            {
                break;
            }

            statistics.DeblockRows++;
            Interlocked.Increment(ref _deblockRowsDone);
        }

        statistics.DeblockTicks = Stopwatch.GetTimestamp() - started;
        return done;
    }

    /// <summary>Spins until a counter reaches a value, unless the picture is abandoned or cancelled.</summary>
    /// <param name="location">The counter.</param>
    /// <param name="value">The value.</param>
    /// <param name="waited">Accumulates the ticks spent waiting.</param>
    /// <returns>Whether the value was reached.</returns>
    private bool Wait(ref int location, int value, ref long waited)
    {
        if (Volatile.Read(ref location) >= value)
        {
            return true;
        }

        var started = Stopwatch.GetTimestamp();
        var spin = default(SpinWait);
        var reached = true;
        while (Volatile.Read(ref location) < value)
        {
            if (Volatile.Read(ref _abort) != 0)
            {
                reached = false;
                break;
            }

            if (_token.IsCancellationRequested)
            {
                Volatile.Write(ref _abort, 1);
                reached = false;
                break;
            }

            spin.SpinOnce(-1);
        }

        waited += Stopwatch.GetTimestamp() - started;
        return reached;
    }
}
