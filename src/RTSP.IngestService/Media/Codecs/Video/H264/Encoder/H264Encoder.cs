// The managed H.264 encoder: canonical High-profile, 4:2:0, 8-bit, progressive, BT.709 limited-range bitstreams with
// I and P pictures only (no B pictures), so presentation order equals decode order and every access unit is output
// for the frame that produced it. IDR pictures follow the keyframe interval in media time; timestamps are carried in
// 1/90000. It codes each submitted frame immediately (no lookahead), so Encode yields exactly one access unit per
// frame and Flush yields none; flushing ends the coded stream and the next frame starts with an IDR picture.
//
// Ownership: the input frame is only borrowed during Encode. Output units are pooled and owned by the receiver,
// bounded by MaxOutstandingUnits. Memory is fixed at construction: MaxReferenceFrames + 1 reconstructed pictures
// and one growable, bounded RBSP buffer. Invalid input (size, format, colour, timestamps) throws without changing
// state; any failure while coding leaves the encoder faulted, and it must then be replaced. Cancellation abandons
// the current picture without touching references or rate control. Not thread-safe.

using System.Buffers;
using System.Buffers.Binary;

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>Encodes 8-bit 4:2:0 BT.709 frames to H.264 High profile.</summary>
internal sealed class H264Encoder : IDisposable
{
    /// <summary>The output time base.</summary>
    public static readonly MediaTimeBase TimeBase = new(1, 90000);

    /// <summary>Test-only syntax controls, or null.</summary>
    private readonly H264EncoderTestHooks? _hooks;

    /// <summary>The reference pictures, real and non-existing, in no particular order.</summary>
    private readonly List<H264EncoderPicture> _dpb = [];

    /// <summary>Reconstruction buffers not in use.</summary>
    private readonly Stack<H264EncoderPicture> _free = new();

    /// <summary>Slice byte offsets of the current picture.</summary>
    private readonly List<int> _sliceEnds = [];

    /// <summary>The current picture header.</summary>
    private readonly H264PictureHeader _header = new();

    /// <summary>The options.</summary>
    private H264EncoderOptions _options = null!;

    /// <summary>The sequence layout.</summary>
    private H264SequenceLayout _layout = null!;

    /// <summary>The macroblock encoder.</summary>
    private H264PictureEncoder _coder = null!;

    /// <summary>The rate controller.</summary>
    private H264RateControl _rate = null!;

    /// <summary>The RBSP writer of the current picture.</summary>
    private H264BitWriter _writer = null!;

    /// <summary>The redundant-slice RBSP writer, when the hooks ask for redundant slices.</summary>
    private H264BitWriter? _redundantWriter;

    /// <summary>The slice start rows, then the row count.</summary>
    private int[] _slices = [];

    /// <summary>The SPS NAL unit.</summary>
    private byte[] _sps = [];

    /// <summary>The PPS NAL unit.</summary>
    private byte[] _pps = [];

    /// <summary>The avcC record.</summary>
    private byte[] _avcC = [];

    /// <summary>The last coded picture, kept for <see cref="CopyReconstruction"/>.</summary>
    private H264EncoderPicture? _last;

    /// <summary>Access units alive.</summary>
    private int _outstanding;

    /// <summary>Whether the next picture must be an IDR picture.</summary>
    private bool _needIdr = true;

    /// <summary>The timestamp of the previous picture, or null at the start of a stream.</summary>
    private long? _previous;

    /// <summary>The timestamp of the last IDR picture.</summary>
    private long _lastIdr;

    /// <summary>The frame_num of the previous reference picture.</summary>
    private int _prevRefFrameNum;

    /// <summary>The pictures coded since the last IDR or MMCO 5 (POC type 0).</summary>
    private int _pocCount;

    /// <summary>The next idr_pic_id.</summary>
    private int _idrPicId;

    /// <summary>MaxLongTermFrameIdx; -1 for "no long-term frame indices".</summary>
    private int _maxLongTermFrameIdx = -1;

    /// <summary>Pictures coded since construction.</summary>
    private long _index;

    /// <summary>A process-unique picture identity source.</summary>
    private long _nextId;

    /// <summary>Whether a failure left the encoder unusable.</summary>
    private bool _faulted;

    /// <summary>Whether the encoder was disposed.</summary>
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="H264Encoder"/> class.</summary>
    /// <param name="options">The options; validated here.</param>
    /// <param name="hooks">Test-only syntax controls.</param>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    /// <exception cref="CodecException"><see cref="CodecErrorKind.Unsupported"/>: no H.264 level admits the configuration.</exception>
    public H264Encoder(H264EncoderOptions options, H264EncoderTestHooks? hooks = null)
    {
        _hooks = hooks;
        Configure(options);
    }

    /// <summary>Gets the options in effect.</summary>
    public H264EncoderOptions Options => _options;

    /// <summary>Gets the AVCDecoderConfigurationRecord of the current parameter sets.</summary>
    public ReadOnlyMemory<byte> DecoderConfigurationRecord => _avcC;

    /// <summary>Gets the SPS NAL unit.</summary>
    public ReadOnlyMemory<byte> SequenceParameterSet => _sps;

    /// <summary>Gets the PPS NAL unit.</summary>
    public ReadOnlyMemory<byte> PictureParameterSet => _pps;

    /// <summary>Gets the parameter-set version: 1 at construction, incremented by each <see cref="Reconfigure"/>.</summary>
    public int ConfigurationVersion { get; private set; }

    /// <summary>Gets level_idc.</summary>
    public int LevelIdc => _layout.LevelIdc;

    /// <summary>Gets the number of access units alive.</summary>
    public int OutstandingUnits => Volatile.Read(ref _outstanding);

    /// <summary>Gets whether the encoder is faulted.</summary>
    public bool IsFaulted => _faulted;

    /// <summary>Gets the bytes of reconstruction, motion-search, macroblock-record and (with test helpers) wavefront picture memory the encoder holds at most.</summary>
    public long PictureBytes =>
        ((_options.MaxReferenceFrames + 1) * H264EncoderPicture.ByteCount(_layout.WidthInMbs, _layout.HeightInMbs)) + H264HalfSamplePlanes.ByteCount(_layout.WidthInMbs * 16, _layout.HeightInMbs * 16)
        + H264PictureEncoder.ByteCount(_layout, MaxAccessUnitBytes, _hooks?.Wavefront?.Count ?? 0);

    /// <summary>Gets the pass durations of the last coded attempt (tests and benchmarks).</summary>
    internal H264PictureTimings LastTimings => _coder.LastTimings;

    /// <summary>Gets the wavefront state of the last coded attempt, or null without test helpers.</summary>
    internal H264WavefrontJob? Wavefront => _coder.Wavefront;

    /// <summary>Gets the most bytes the RBSP buffer may grow to.</summary>
    public int MaxAccessUnitBytes { get; private set; }

    /// <summary>Gets the bytes the RBSP buffers currently hold.</summary>
    public int BufferBytes => _writer.Capacity + (_redundantWriter?.Capacity ?? 0);

    /// <summary>Gets the statistics of the last coded picture.</summary>
    public H264FrameStatistics? LastFrame { get; private set; }

    /// <summary>Encodes a frame.</summary>
    /// <param name="frame">The frame: the configured size, <see cref="VideoPixelFormat.Yuv420P8"/>, BT.709 limited range; borrowed during the call.</param>
    /// <param name="output">Receives the access unit.</param>
    /// <param name="forceKeyframe">Whether to code an IDR picture regardless of the interval.</param>
    /// <param name="cancellationToken">Observed between macroblock rows; cancellation drops the frame and leaves the encoder usable.</param>
    /// <exception cref="CodecException">The frame is invalid (<see cref="CodecErrorKind.InvalidData"/>, <see cref="CodecErrorKind.Unsupported"/>; the encoder is unchanged), or the output budget is exhausted (<see cref="CodecErrorKind.ResourceLimit"/>).</exception>
    /// <exception cref="InvalidOperationException">The encoder is faulted.</exception>
    public void Encode(VideoFrame frame, ICollection<H264EncodedAccessUnit> output, bool forceKeyframe = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(output);
        ThrowIfUnusable();
        var timestamp = Validate(frame);
        if (Volatile.Read(ref _outstanding) >= _options.MaxOutstandingUnits)
        {
            throw new CodecException(CodecErrorKind.ResourceLimit, "Too many encoded access units are outstanding.");
        }

        var idr = _needIdr || forceKeyframe || IntervalElapsed(timestamp);
        var control = _hooks?.Control?.Invoke(_index);
        try
        {
            var unit = EncodePicture(frame, timestamp, idr, control, cancellationToken);
            output.Add(unit);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Ends the coded stream. No access unit is pending (there is no lookahead), so none is emitted; the next frame starts a new stream with an IDR picture and may restart its timestamps.</summary>
    /// <param name="output">Receives nothing.</param>
    public void Flush(ICollection<H264EncodedAccessUnit> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ThrowIfUnusable();
        Reset();
    }

    /// <summary>Drops all references and rate-control history; the next frame is an IDR picture and may restart timestamps.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseReferences();
        _needIdr = true;
        _previous = null;
        _rate = new H264RateControl(_options);
    }

    /// <summary>Applies new options: regenerates the parameter sets (incrementing <see cref="ConfigurationVersion"/>) and starts a new stream.</summary>
    /// <param name="options">The options; validated here.</param>
    /// <exception cref="ArgumentException">The options are invalid; the encoder is unchanged.</exception>
    /// <exception cref="CodecException"><see cref="CodecErrorKind.Unsupported"/>: no level admits them; the encoder is unchanged.</exception>
    public void Reconfigure(H264EncoderOptions options)
    {
        ThrowIfUnusable();
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _ = Layout(options, _hooks);
        Configure(options);
    }

    /// <summary>Copies the last coded picture's reconstruction (cropped to the output size) into a frame of that size.</summary>
    /// <param name="destination">The destination.</param>
    internal void CopyReconstruction(VideoFrame destination)
    {
        var picture = _last ?? throw new InvalidOperationException("No picture has been coded.");
        for (var plane = 0; plane < 3; plane++)
        {
            var source = plane == 0 ? picture.Luma : plane == 1 ? picture.Cb : picture.Cr;
            var stride = plane == 0 ? picture.Width : picture.Width / 2;
            var width = destination.GetStride(plane);
            var height = plane == 0 ? destination.Height : destination.ChromaHeight;
            var target = destination.GetPlane(plane);
            for (var y = 0; y < height; y++)
            {
                source.AsSpan(y * stride, width).CopyTo(target.Slice(y * width, width));
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseReferences();
        if (_last is { } last && !_free.Contains(last))
        {
            last.Dispose();
        }

        while (_free.TryPop(out var picture))
        {
            picture.Dispose();
        }

        _writer.Dispose();
        _redundantWriter?.Dispose();
    }

    /// <summary>Builds the sequence layout of a configuration.</summary>
    /// <param name="options">The options.</param>
    /// <param name="hooks">The hooks.</param>
    /// <returns>The layout.</returns>
    private static H264SequenceLayout Layout(H264EncoderOptions options, H264EncoderTestHooks? hooks)
    {
        int wmbs = (options.Width + 15) / 16, hmbs = (options.Height + 15) / 16;
        var (level, mv) = H264ParameterSetWriter.SelectLevel(wmbs, hmbs, options.MaxFrameRate, options.MaxReferenceFrames, options.MaxBitrateKilobits * 1000L, options.BufferBits);
        return new H264SequenceLayout(
            hooks?.ProfileIdc ?? 100,
            level,
            wmbs,
            hmbs,
            options.Width,
            options.Height,
            options.MaxReferenceFrames,
            hooks?.PocType ?? 2,
            hooks?.Log2MaxFrameNum ?? 8,
            8,
            hooks?.GapsAllowed ?? false,
            hooks?.RedundantSlices ?? false,
            hooks?.WeightedBipredIdc ?? 0,
            hooks?.MaxNumReorderFrames ?? 0,
            mv);
    }

    /// <summary>Applies validated options.</summary>
    /// <param name="options">The options.</param>
    private void Configure(H264EncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var layout = Layout(options, _hooks);
        var resized = _layout is null || layout.WidthInMbs != _layout.WidthInMbs || layout.HeightInMbs != _layout.HeightInMbs || options.MaxReferenceFrames != _options.MaxReferenceFrames;
        ReleaseReferences();
        if (resized)
        {
            if (_last is { } last && !_free.Contains(last))
            {
                last.Dispose();
            }

            _last = null;
            while (_free.TryPop(out var picture))
            {
                picture.Dispose();
            }

            for (var i = 0; i <= options.MaxReferenceFrames; i++)
            {
                _free.Push(new H264EncoderPicture(layout.WidthInMbs, layout.HeightInMbs));
            }

            _writer?.Dispose();
            _redundantWriter?.Dispose();
            MaxAccessUnitBytes = (layout.Macroblocks * ((H264PictureEncoder.MaxMbBits / 8) + 8)) + (options.SlicesPerPicture * 64) + 1024;
            var initial = Math.Min(MaxAccessUnitBytes, 65536);
            _writer = new H264BitWriter(initial, MaxAccessUnitBytes);
            _redundantWriter = _hooks?.RedundantSlices == true ? new H264BitWriter(initial, MaxAccessUnitBytes) : null;
        }

        _options = options;
        _layout = layout;
        _coder = new H264PictureEncoder(layout, options.SearchRange, MaxAccessUnitBytes, _hooks?.Wavefront);
        _rate = new H264RateControl(options);
        _slices = new int[options.SlicesPerPicture + 1];
        for (var s = 0; s <= options.SlicesPerPicture; s++)
        {
            _slices[s] = s * layout.HeightInMbs / options.SlicesPerPicture;
        }

        _sps = H264ParameterSetWriter.Nal(3, 7, H264ParameterSetWriter.WriteSps(layout));
        _pps = H264ParameterSetWriter.Nal(3, 8, H264ParameterSetWriter.WritePps(layout));
        _avcC = H264ParameterSetWriter.BuildAvcC(_sps, _pps);
        ConfigurationVersion++;
        _needIdr = true;
        _previous = null;
    }

    /// <summary>Validates a frame and returns its timestamp in 1/90000.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>The timestamp.</returns>
    private long Validate(VideoFrame frame)
    {
        if (frame.Format != VideoPixelFormat.Yuv420P8 || frame.BitDepth != 8)
        {
            throw new CodecException(CodecErrorKind.Unsupported, "The encoder accepts 8-bit 4:2:0 frames only.");
        }

        if (frame.Width != _options.Width || frame.Height != _options.Height)
        {
            throw new CodecException(CodecErrorKind.InvalidData, "The frame size differs from the configured size; reconfigure the encoder first.");
        }

        if (frame.Color != VideoColorInfo.Bt709)
        {
            throw new CodecException(CodecErrorKind.Unsupported, "The encoder accepts BT.709 limited-range frames only; convert the colour first.");
        }

        _ = frame.GetPlane(0);
        long timestamp;
        try
        {
            timestamp = frame.TimeBase.Rescale(frame.Timestamp, TimeBase);
        }
        catch (OverflowException)
        {
            throw new CodecException(CodecErrorKind.InvalidData, "The frame timestamp does not fit the 1/90000 time base.");
        }

        if (_previous is { } previous && timestamp <= previous)
        {
            throw new CodecException(CodecErrorKind.InvalidData, "Frame timestamps must increase strictly.");
        }

        return timestamp;
    }

    /// <summary>Returns whether the keyframe interval has elapsed since the last IDR picture.</summary>
    /// <param name="timestamp">The frame's timestamp.</param>
    /// <returns>Whether an IDR picture is due.</returns>
    private bool IntervalElapsed(long timestamp) =>
        (Int128)(timestamp - _lastIdr) * TimeSpan.TicksPerSecond >= (Int128)_options.KeyframeInterval.Ticks * TimeBase.Denominator;

    /// <summary>Codes one picture and builds its access unit.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="timestamp">Its timestamp.</param>
    /// <param name="idr">Whether to code an IDR picture.</param>
    /// <param name="control">Test-only overrides.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The access unit.</returns>
    private H264EncodedAccessUnit EncodePicture(VideoFrame frame, long timestamp, bool idr, H264PictureControl? control, CancellationToken cancellationToken)
    {
        var picture = TakePicture();
        try
        {
            var references = PrepareHeader(idr, control);
            var seconds = _previous is { } previous ? Math.Min(1.0, (timestamp - previous) / 90000.0) : 1.0 / _options.MaxFrameRate;
            var qp = _rate.Choose(idr, seconds);
            var attempts = 0;
            H264PictureCounters counters;
            long bits;
            _header.ForceSkip = false;
            while (true)
            {
                attempts++;
                _header.Qp = qp;
                _header.RedundantPicCnt = 0;
                _writer.Clear();
                _sliceEnds.Clear();
                counters = _coder.Encode(frame, picture, references, _header, _slices, _writer, _sliceEnds, cancellationToken);
                bits = PrimaryBytes(idr) * 8L;
                if (_rate.Fits(bits, seconds, idr) || _header.ForceSkip)
                {
                    break;
                }

                if (qp >= _options.MaxQp || attempts >= H264RateControl.MaxAttempts)
                {
                    // Even the highest QP overfills the bucket: a P picture repeats its reference (every
                    // macroblock P_Skip, a few bytes); an intra picture is kept and reported as an overflow.
                    if (_header.IsIntra)
                    {
                        break;
                    }

                    _header.ForceSkip = true;
                    continue;
                }

                qp = attempts == H264RateControl.MaxAttempts - 1 ? _options.MaxQp : _rate.Retry(qp, bits, seconds, idr);
            }

            var overflow = !_rate.Fits(bits, seconds, idr: true);
            List<int>? redundantEnds = null;
            if (_redundantWriter is not null)
            {
                _header.RedundantPicCnt = 1;
                _redundantWriter.Clear();
                redundantEnds = [];
                _coder.Encode(frame, picture, references, _header, _slices, _redundantWriter, redundantEnds, cancellationToken);
                _header.RedundantPicCnt = 0;
            }

            // Commit: everything below changes encoder state and cannot be cancelled.
            var (buffer, length) = BuildAccessUnit(idr, redundantEnds);
            _rate.Update(idr, qp, length * 8L, _header.ForceSkip ? 0 : counters.Satd, seconds);
            MarkReferences(picture, idr, control);
            _previous = timestamp;
            if (idr)
            {
                _lastIdr = timestamp;
                _needIdr = false;
            }

            var statistics = new H264FrameStatistics(_index, idr, qp, length, attempts, counters.Intra, counters.Skip, counters.Pcm, _rate.FullnessBits, _rate.BufferSizeBits, overflow);
            LastFrame = statistics;
            _index++;
            Interlocked.Increment(ref _outstanding);
            return new H264EncodedAccessUnit(buffer, length, () => Interlocked.Decrement(ref _outstanding))
            {
                Timestamp = timestamp,
                IsKeyframe = idr,
                Format = _options.Format,
                ConfigurationVersion = ConfigurationVersion,
                Statistics = statistics,
            };
        }
        catch
        {
            if (!picture.IsReference && !_free.Contains(picture) && _last != picture)
            {
                _free.Push(picture);
            }

            throw;
        }
    }

    /// <summary>Takes a free reconstruction buffer.</summary>
    /// <returns>The picture.</returns>
    private H264EncoderPicture TakePicture()
    {
        if (_last is { IsReference: false } last && !_free.Contains(last))
        {
            _free.Push(last);
        }

        _last = null;
        if (!_free.TryPop(out var picture))
        {
            throw new InvalidOperationException("No reconstruction buffer is free; the reference bookkeeping is inconsistent.");
        }

        picture.Id = ++_nextId;
        picture.ShortTerm = picture.LongTerm = picture.NonExisting = false;
        return picture;
    }

    /// <summary>Fills the picture header and builds the reference list (8.2.4) for the next picture.</summary>
    /// <param name="idr">Whether the picture is an IDR picture.</param>
    /// <param name="control">Test-only overrides.</param>
    /// <returns>List 0.</returns>
    private H264EncoderPicture?[] PrepareHeader(bool idr, H264PictureControl? control)
    {
        var h = _header;
        var maxFrameNum = 1 << _layout.Log2MaxFrameNum;
        h.IsIdr = idr;
        h.NalRefIdc = control?.NonReference == true && !idr ? 0 : idr ? 3 : 2;
        h.LongTermReference = idr && control?.LongTermReference == true;
        h.ListModifications.Clear();
        h.Mmcos.Clear();
        h.ForcePcm = control?.ForcePcm;
        if (idr)
        {
            ReleaseReferences();
            h.FrameNum = 0;
            h.IdrPicId = _idrPicId;
            _idrPicId = (_idrPicId + 1) & 0xFFFF;
            h.ActiveReferences = 0;
            _pocCount = 0;
            h.PocLsb = 0;
            return [];
        }

        var gap = control?.FrameNumGap ?? 0;
        if (gap > 0)
        {
            if (!_layout.GapsAllowed)
            {
                throw new InvalidOperationException("Frame number gaps need gaps_in_frame_num_value_allowed_flag.");
            }

            // 8.2.5.2: the decoder infers a non-existing short-term frame for each skipped frame_num, each marked
            // by the sliding window as the current picture, so PicNum ordering is relative to that frame.
            for (var i = 1; i <= gap; i++)
            {
                SlidingWindow();
                _prevRefFrameNum = (_prevRefFrameNum + 1) % maxFrameNum;
                _dpb.Add(new H264EncoderPicture(0, 0) { NonExisting = true, ShortTerm = true, FrameNum = _prevRefFrameNum, Id = ++_nextId });
            }
        }

        h.FrameNum = (_prevRefFrameNum + 1) % maxFrameNum;
        _pocCount++;
        h.PocLsb = (2 * _pocCount) % (1 << _layout.Log2MaxPocLsb);
        h.Mmcos.AddRange(control?.Mmcos ?? []);
        h.ListModifications.AddRange(control?.ListModifications ?? []);
        var shortTerm = _dpb.Where(static p => p.ShortTerm).OrderByDescending(p => PicNum(p, h.FrameNum, maxFrameNum)).ToList();
        var longTerm = _dpb.Where(static p => p.LongTerm).OrderBy(static p => p.LongTermFrameIdx).ToList();
        var list = shortTerm.Concat(longTerm).Take(_options.MaxReferenceFrames).Cast<H264EncoderPicture?>().ToList();
        ApplyModifications(list, h.FrameNum, maxFrameNum);
        h.ActiveReferences = list.Count;
        return [.. list.Select(static p => p is { NonExisting: false } ? p : null)];
    }

    /// <summary>Returns PicNum (FrameNumWrap) of a short-term frame.</summary>
    /// <param name="picture">The frame.</param>
    /// <param name="frameNum">The current frame_num.</param>
    /// <param name="maxFrameNum">MaxFrameNum.</param>
    /// <returns>PicNum.</returns>
    private static int PicNum(H264EncoderPicture picture, int frameNum, int maxFrameNum) => picture.FrameNum > frameNum ? picture.FrameNum - maxFrameNum : picture.FrameNum;

    /// <summary>Applies ref_pic_list_modification to list 0 (8.2.4.3).</summary>
    /// <param name="list">The initial list.</param>
    /// <param name="frameNum">The current frame_num.</param>
    /// <param name="maxFrameNum">MaxFrameNum.</param>
    private void ApplyModifications(List<H264EncoderPicture?> list, int frameNum, int maxFrameNum)
    {
        var count = list.Count;
        var predicted = frameNum;
        var refIdx = 0;
        foreach (var m in _header.ListModifications)
        {
            H264EncoderPicture? target;
            if (m.Idc < 2)
            {
                var noWrap = m.Idc == 0 ? predicted - (m.Value + 1) : predicted + (m.Value + 1);
                noWrap = noWrap < 0 ? noWrap + maxFrameNum : noWrap >= maxFrameNum ? noWrap - maxFrameNum : noWrap;
                predicted = noWrap;
                var picNum = noWrap > frameNum ? noWrap - maxFrameNum : noWrap;
                target = _dpb.FirstOrDefault(p => p.ShortTerm && PicNum(p, frameNum, maxFrameNum) == picNum);
            }
            else
            {
                target = _dpb.FirstOrDefault(p => p.LongTerm && p.LongTermFrameIdx == m.Value);
            }

            if (target is null)
            {
                throw new InvalidOperationException("A list modification names a picture that is not a reference.");
            }

            list.Remove(target);
            list.Insert(refIdx++, target);
        }

        while (list.Count > count)
        {
            list.RemoveAt(list.Count - 1);
        }
    }

    /// <summary>Marks the coded picture (8.2.5) and updates frame_num and POC state.</summary>
    /// <param name="picture">The picture.</param>
    /// <param name="idr">Whether it is an IDR picture.</param>
    /// <param name="control">Test-only overrides.</param>
    private void MarkReferences(H264EncoderPicture picture, bool idr, H264PictureControl? control)
    {
        _last = picture;
        picture.FrameNum = _header.FrameNum;
        if (_header.NalRefIdc == 0)
        {
            return;
        }

        var maxFrameNum = 1 << _layout.Log2MaxFrameNum;
        if (idr)
        {
            _maxLongTermFrameIdx = _header.LongTermReference ? 0 : -1;
            picture.LongTerm = _header.LongTermReference;
            picture.ShortTerm = !_header.LongTermReference;
            picture.LongTermFrameIdx = 0;
            _dpb.Add(picture);
            _prevRefFrameNum = 0;
            return;
        }

        var longTermCurrent = false;
        var reset = false;
        if (_header.Mmcos.Count == 0)
        {
            SlidingWindow();
        }

        foreach (var m in _header.Mmcos)
        {
            switch (m.Operation)
            {
                case 1:
                    Unmark(_dpb.FirstOrDefault(p => p.ShortTerm && PicNum(p, _header.FrameNum, maxFrameNum) == _header.FrameNum - (m.Value + 1)));
                    break;
                case 2:
                    Unmark(_dpb.FirstOrDefault(p => p.LongTerm && p.LongTermFrameIdx == m.Value));
                    break;
                case 3:
                    var shortTerm = _dpb.FirstOrDefault(p => p.ShortTerm && PicNum(p, _header.FrameNum, maxFrameNum) == _header.FrameNum - (m.Value + 1));
                    Unmark(_dpb.FirstOrDefault(p => p.LongTerm && p.LongTermFrameIdx == m.LongTermFrameIdx));
                    if (shortTerm is not null)
                    {
                        shortTerm.ShortTerm = false;
                        shortTerm.LongTerm = true;
                        shortTerm.LongTermFrameIdx = m.LongTermFrameIdx;
                    }

                    break;
                case 4:
                    _maxLongTermFrameIdx = m.Value - 1;
                    foreach (var p in _dpb.Where(p => p.LongTerm && p.LongTermFrameIdx > _maxLongTermFrameIdx).ToList())
                    {
                        Unmark(p);
                    }

                    break;
                case 5:
                    foreach (var p in _dpb.ToList())
                    {
                        Unmark(p);
                    }

                    _maxLongTermFrameIdx = -1;
                    reset = true;
                    break;
                case 6:
                    Unmark(_dpb.FirstOrDefault(p => p.LongTerm && p.LongTermFrameIdx == m.LongTermFrameIdx));
                    picture.LongTerm = true;
                    picture.LongTermFrameIdx = m.LongTermFrameIdx;
                    longTermCurrent = true;
                    break;
            }
        }

        picture.ShortTerm = !longTermCurrent;
        _dpb.Add(picture);
        if (_dpb.Count > _options.MaxReferenceFrames)
        {
            throw new InvalidOperationException("The marking leaves more reference frames than max_num_ref_frames.");
        }

        _prevRefFrameNum = reset ? 0 : _header.FrameNum;
        if (reset)
        {
            picture.FrameNum = 0;
            _pocCount = 0;
        }
    }

    /// <summary>Applies the sliding window (8.2.5.3) before a new short-term frame is added.</summary>
    private void SlidingWindow()
    {
        var maxFrameNum = 1 << _layout.Log2MaxFrameNum;
        if (_dpb.Count < Math.Max(1, _options.MaxReferenceFrames))
        {
            return;
        }

        var current = (_prevRefFrameNum + 1) % maxFrameNum;
        var oldest = _dpb.Where(static p => p.ShortTerm).OrderBy(p => PicNum(p, current, maxFrameNum)).FirstOrDefault()
            ?? throw new InvalidOperationException("The sliding window found no short-term frame to remove.");
        Unmark(oldest);
    }

    /// <summary>Marks a reference picture unused and recycles its buffer.</summary>
    /// <param name="picture">The picture, or null.</param>
    private void Unmark(H264EncoderPicture? picture)
    {
        if (picture is null)
        {
            return;
        }

        picture.ShortTerm = picture.LongTerm = false;
        _dpb.Remove(picture);
        if (!picture.NonExisting && picture != _last)
        {
            _free.Push(picture);
        }
    }

    /// <summary>Releases every reference picture.</summary>
    private void ReleaseReferences()
    {
        foreach (var picture in _dpb.ToList())
        {
            Unmark(picture);
        }

        _maxLongTermFrameIdx = -1;
        _prevRefFrameNum = 0;
    }

    /// <summary>Assembles the access unit from the parameter sets and slice RBSPs.</summary>
    /// <param name="idr">Whether the picture is an IDR picture.</param>
    /// <param name="redundantEnds">The redundant slice ends, or null.</param>
    /// <returns>The rented buffer and its used length.</returns>
    private (byte[] Buffer, int Length) BuildAccessUnit(bool idr, List<int>? redundantEnds)
    {
        var type = idr ? 5 : 1;
        var nals = new List<(int RefIdc, int Type, int Start, int End, bool Redundant)>();
        if (idr && _options.Format == H264OutputFormat.AnnexB)
        {
            nals.Add((-1, 7, 0, 0, false));
            nals.Add((-1, 8, 0, 0, false));
        }

        var order = Enumerable.Range(0, _sliceEnds.Count);
        if (_hooks?.ReverseSliceOrder == true)
        {
            order = order.Reverse();
        }

        foreach (var s in order)
        {
            nals.Add((_header.NalRefIdc, type, s == 0 ? 0 : _sliceEnds[s - 1], _sliceEnds[s], false));
        }

        // 7.4.1.2.3: the redundant coded picture follows the whole primary coded picture.
        if (redundantEnds is not null)
        {
            foreach (var s in order)
            {
                nals.Add((_header.NalRefIdc, type, s == 0 ? 0 : redundantEnds[s - 1], redundantEnds[s], true));
            }
        }

        var total = 0;
        foreach (var n in nals)
        {
            total += 4 + NalSize(n.RefIdc, n.Type, n.Start, n.End, n.Redundant);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(total);
        var length = 0;
        foreach (var n in nals)
        {
            var size = NalSize(n.RefIdc, n.Type, n.Start, n.End, n.Redundant);
            if (_options.Format == H264OutputFormat.AnnexB)
            {
                H264ParameterSetWriter.StartCode.CopyTo(buffer.AsSpan(length));
            }
            else
            {
                BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(length), size);
            }

            length += 4;
            if (n.RefIdc < 0)
            {
                (n.Type == 7 ? _sps : _pps).CopyTo(buffer.AsSpan(length));
                length += size;
            }
            else
            {
                length += H264ParameterSetWriter.WriteNal(n.RefIdc, n.Type, Rbsp(n.Start, n.End, n.Redundant), buffer.AsSpan(length));
            }
        }

        return (buffer, length);
    }

    /// <summary>Returns the size of the access unit without redundant slices, as framed.</summary>
    /// <param name="idr">Whether it is an IDR picture.</param>
    /// <returns>The size in bytes.</returns>
    private int PrimaryBytes(bool idr)
    {
        var total = idr && _options.Format == H264OutputFormat.AnnexB ? 8 + _sps.Length + _pps.Length : 0;
        for (var s = 0; s < _sliceEnds.Count; s++)
        {
            total += 4 + NalSize(_header.NalRefIdc, 1, s == 0 ? 0 : _sliceEnds[s - 1], _sliceEnds[s], false);
        }

        return total;
    }

    /// <summary>Returns a NAL unit's escaped size: a parameter set or a slice.</summary>
    /// <param name="refIdc">-1 for a parameter set.</param>
    /// <param name="type">The NAL type.</param>
    /// <param name="start">The RBSP start.</param>
    /// <param name="end">The RBSP end.</param>
    /// <param name="redundant">Whether it is a redundant slice.</param>
    /// <returns>The size in bytes, header included.</returns>
    private int NalSize(int refIdc, int type, int start, int end, bool redundant) =>
        refIdc < 0 ? (type == 7 ? _sps : _pps).Length : H264ParameterSetWriter.EscapedSize(Rbsp(start, end, redundant));

    /// <summary>Returns a slice RBSP.</summary>
    /// <param name="start">The start offset.</param>
    /// <param name="end">The end offset.</param>
    /// <param name="redundant">Whether it is from the redundant writer.</param>
    /// <returns>The RBSP.</returns>
    private ReadOnlySpan<byte> Rbsp(int start, int end, bool redundant) => (redundant ? _redundantWriter! : _writer).Written[start..end];

    /// <summary>Throws when the encoder cannot be used.</summary>
    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new InvalidOperationException("The H.264 encoder is faulted and must be replaced.");
        }
    }
}
