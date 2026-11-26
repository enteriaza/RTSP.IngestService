// Managed H.264 decoder for progressive 8-bit 4:2:0 Baseline, Constrained Baseline, Main and High streams. One
// packet is one access unit carrying one primary coded picture. Decoding starts at an IDR picture. Pictures are
// output in presentation order through the DPB bumping process (C.4.5.3), copied into VideoFrames the caller owns.
//
// Failure policy: any exception while decoding a packet discards the picture being decoded, marks every reference
// picture unused, and makes the decoder skip pictures until the next IDR; pictures already decoded and waiting for
// output are kept and still output. CodecErrorKind.Unsupported identifies valid features outside the supported
// matrix; InvalidData identifies malformed or inconsistent input; ResourceLimit identifies a configured limit.

using System.Buffers;
using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Decodes H.264 access units into frames. Not thread-safe; use one instance per stream.</summary>
internal sealed partial class H264Decoder : IDisposable
{
    /// <summary>The limits.</summary>
    private readonly H264DecoderOptions _options;

    /// <summary>Accounts frames handed to the caller.</summary>
    private readonly VideoFrameBudget _budget;

    /// <summary>Stored sequence parameter sets by id.</summary>
    private readonly H264Sps?[] _sps = new H264Sps?[32];

    /// <summary>Stored picture parameter sets by id.</summary>
    private readonly H264Pps?[] _pps = new H264Pps?[256];

    /// <summary>The slice header being parsed.</summary>
    private readonly H264SliceHeader _header = new();

    /// <summary>NAL units of the current packet.</summary>
    private readonly List<(int Offset, int Length)> _units = [];

    /// <summary>Whether <see cref="Configure"/> has been called.</summary>
    private bool _configured;

    /// <summary>The framing.</summary>
    private H264StreamFormat _format;

    /// <summary>NAL unit length field size for length-prefixed input.</summary>
    private int _lengthSize;

    /// <summary>The active SPS.</summary>
    private H264Sps? _activeSps;

    /// <summary>The slice decoder for the active size.</summary>
    private H264SliceDecoder? _slice;

    /// <summary>The picture being decoded.</summary>
    private H264Picture? _current;

    /// <summary>Whether non-IDR pictures are skipped.</summary>
    private bool _awaitIdr = true;

    /// <summary>Whether the instance is disposed.</summary>
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="H264Decoder"/> class.</summary>
    /// <param name="options">The limits, or <see langword="null"/> for <see cref="H264DecoderOptions.Default"/>.</param>
    public H264Decoder(H264DecoderOptions? options = null)
    {
        _options = options ?? H264DecoderOptions.Default;
        _options.Validate();
        _budget = new VideoFrameBudget(_options.MaxOutstandingFrames);
    }

    /// <summary>Gets the number of output frames not yet disposed.</summary>
    public int OutstandingFrames => _budget.Outstanding;

    /// <summary>Gets the number of pictures skipped while waiting for an IDR picture.</summary>
    public long SkippedPictures { get; private set; }

    /// <summary>Gets the number of redundant coded slices discarded.</summary>
    public long DiscardedRedundantSlices { get; private set; }

    /// <summary>Gets a value indicating whether the decoder is waiting for an IDR picture.</summary>
    public bool AwaitingIdr => _awaitIdr;

    /// <summary>Gets the number of pictures holding pooled sample buffers.</summary>
    internal int AllocatedPictures
    {
        get
        {
            var count = _current?.IsAllocated == true ? 1 : 0;
            foreach (var picture in _dpb)
            {
                count += picture.IsAllocated ? 1 : 0;
            }

            foreach (var picture in _free)
            {
                count += picture.IsAllocated ? 1 : 0;
            }

            return count;
        }
    }

    /// <summary>Sets the input framing and, for length-prefixed input, loads the avcC parameter sets; resets decoding.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <exception cref="CodecException">The avcC record or one of its parameter sets is invalid.</exception>
    public void Configure(H264DecoderConfiguration configuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);
        Reset();
        _configured = false;
        if (configuration.Format == H264StreamFormat.LengthPrefixed)
        {
            if (!AvcDecoderConfiguration.TryParse(configuration.DecoderConfigurationRecord.Span, out var record, out var error))
            {
                CodecException.InvalidData($"The AVC decoder configuration record is invalid: {error}");
            }

            if (record.NalLengthSize == 3)
            {
                CodecException.InvalidData("The AVC decoder configuration record uses a reserved NAL length size.");
            }

            foreach (var nal in record.SequenceParameterSets)
            {
                StoreParameterSet(nal.Span, H264NalUnitType.Sps);
            }

            foreach (var nal in record.PictureParameterSets)
            {
                StoreParameterSet(nal.Span, H264NalUnitType.Pps);
            }

            _lengthSize = record.NalLengthSize;
        }

        _format = configuration.Format;
        _configured = true;
    }

    /// <summary>Decodes one access unit and appends the frames that become ready for output, in presentation order.</summary>
    /// <param name="packet">The access unit; its timestamp is the picture's presentation timestamp.</param>
    /// <param name="output">Receives frames; the caller owns and must dispose them.</param>
    /// <param name="cancellationToken">Checked before decoding starts.</param>
    /// <exception cref="CodecException">The packet is malformed, unsupported, or exceeds a limit.</exception>
    /// <exception cref="InvalidOperationException">The decoder is not configured.</exception>
    public void Decode(EncodedPacket packet, ICollection<VideoFrame> output, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_configured)
        {
            throw new InvalidOperationException("The decoder is not configured.");
        }

        var data = packet.Data.Span;
        if (data.Length > _options.MaxAccessUnitBytes)
        {
            throw new CodecException(CodecErrorKind.ResourceLimit, "The access unit exceeds the configured size limit.");
        }

        EnsureOutputCapacity(H264DecoderOptions.MaxPicturesPerCall);
        var rbsp = ArrayPool<byte>.Shared.Rent(Math.Max(data.Length, 1));
        try
        {
            _units.Clear();
            if (_format == H264StreamFormat.AnnexB)
            {
                H264NalUnits.SplitAnnexB(data, _units, _options.MaxNalUnitsPerAccessUnit);
            }
            else
            {
                H264NalUnits.SplitLengthPrefixed(data, _lengthSize, _units, _options.MaxNalUnitsPerAccessUnit);
            }

            foreach (var (offset, length) in _units)
            {
                ProcessNal(data.Slice(offset, length), rbsp, packet, output);
            }

            if (_current is not null)
            {
                FinishPicture(output);
            }
        }
        catch
        {
            Abort();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rbsp);
        }
    }

    /// <summary>Outputs every picture still waiting (end of stream); decoding then resumes at the next IDR.</summary>
    /// <param name="output">Receives frames in presentation order.</param>
    /// <exception cref="CodecException">The caller holds too many frames.</exception>
    public void Drain(ICollection<VideoFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        var waiting = 0;
        foreach (var picture in _dpb)
        {
            waiting += picture.NeededForOutput ? 1 : 0;
        }

        EnsureOutputCapacity(waiting);
        while (Bump(output))
        {
        }

        ClearReferences();
        ReleaseFree();
        _awaitIdr = true;
    }

    /// <summary>Discards all pictures without output and waits for the next IDR; parameter sets are kept.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseAll();
        _awaitIdr = true;
        ResetOrderState();
    }

    /// <summary>Returns every pooled buffer. Frames already output stay valid.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseAll();
        _slice = null;
        _disposed = true;
    }

    /// <summary>Throws when the caller holds so many frames that <paramref name="count"/> more would exceed the budget.</summary>
    /// <param name="count">The most frames the operation can output.</param>
    /// <exception cref="CodecException"><see cref="CodecErrorKind.ResourceLimit"/>.</exception>
    private void EnsureOutputCapacity(int count)
    {
        if (_budget.Outstanding + count > _budget.MaxOutstanding)
        {
            throw new CodecException(CodecErrorKind.ResourceLimit, "Too many decoded video frames are outstanding; dispose frames before decoding more.");
        }
    }

    /// <summary>Dispatches one NAL unit.</summary>
    /// <param name="nal">The NAL unit including its header byte.</param>
    /// <param name="rbsp">Scratch for the RBSP.</param>
    /// <param name="packet">The packet.</param>
    /// <param name="output">Receives frames.</param>
    private void ProcessNal(ReadOnlySpan<byte> nal, byte[] rbsp, EncodedPacket packet, ICollection<VideoFrame> output)
    {
        if ((nal[0] & 0x80) != 0)
        {
            CodecException.InvalidData("forbidden_zero_bit is set.");
        }

        var refIdc = (nal[0] >> 5) & 3;
        var type = (H264NalUnitType)(nal[0] & 0x1F);
        switch (type)
        {
            case H264NalUnitType.Slice:
            case H264NalUnitType.IdrSlice:
                DecodeSliceNal(nal, rbsp, type, refIdc, packet, output);
                break;
            case H264NalUnitType.PartitionA:
            case H264NalUnitType.PartitionB:
            case H264NalUnitType.PartitionC:
                H264Syntax.Unsupported("Data partitioning is not supported.");
                break;
            case H264NalUnitType.Sps:
            case H264NalUnitType.Pps:
                if (_current is not null)
                {
                    CodecException.InvalidData("A parameter set follows the first slice of a picture.");
                }

                StoreParameterSet(nal, type);
                break;
            default:
                break;
        }
    }

    /// <summary>Parses and stores an SPS or PPS NAL unit, replacing any set with the same id.</summary>
    /// <param name="nal">The NAL unit including its header byte.</param>
    /// <param name="type">The expected type.</param>
    private void StoreParameterSet(ReadOnlySpan<byte> nal, H264NalUnitType type)
    {
        if (nal.Length > _options.MaxParameterSetBytes)
        {
            throw new CodecException(CodecErrorKind.ResourceLimit, "A parameter set exceeds the configured size limit.");
        }

        if (nal.Length < 2 || (nal[0] & 0x80) != 0 || (H264NalUnitType)(nal[0] & 0x1F) != type)
        {
            CodecException.InvalidData("A parameter set NAL unit is malformed.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(nal.Length);
        try
        {
            var rbsp = buffer.AsSpan(0, H264NalUnits.Unescape(nal[1..], buffer));
            if (type == H264NalUnitType.Sps)
            {
                var sps = H264Sps.Parse(rbsp);
                _sps[sps.Id] = sps;
            }
            else
            {
                var pps = H264Pps.Parse(rbsp);
                _pps[pps.Id] = pps;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Decodes a coded slice NAL unit.</summary>
    /// <param name="nal">The NAL unit including its header byte.</param>
    /// <param name="buffer">Scratch for the RBSP.</param>
    /// <param name="type">The NAL unit type.</param>
    /// <param name="refIdc">nal_ref_idc.</param>
    /// <param name="packet">The packet.</param>
    /// <param name="output">Receives frames.</param>
    private void DecodeSliceNal(ReadOnlySpan<byte> nal, byte[] buffer, H264NalUnitType type, int refIdc, EncodedPacket packet, ICollection<VideoFrame> output)
    {
        if (type == H264NalUnitType.IdrSlice && refIdc == 0)
        {
            CodecException.InvalidData("An IDR slice has nal_ref_idc equal to 0.");
        }

        // Non-IDR slices before the first IDR are skipped unparsed: their parameter sets may not have been received.
        if (_awaitIdr && _current is null && type != H264NalUnitType.IdrSlice)
        {
            if (nal.Length < 2)
            {
                CodecException.InvalidData("A slice NAL unit is empty.");
            }

            SkippedPictures += (nal[1] & 0x80) != 0 ? 1 : 0;
            return;
        }

        var length = H264NalUnits.Unescape(nal[1..], buffer);
        var rbsp = buffer.AsSpan(0, length);
        var payloadBits = H264NalUnits.PayloadBits(rbsp);
        var reader = new BitstreamReader(rbsp);
        _header.Parse(ref reader, type, refIdc, _sps, _pps);
        var dataStart = reader.Position;
        if (dataStart > payloadBits)
        {
            CodecException.InvalidData("The slice header overruns the RBSP.");
        }

        if (_header.RedundantPicCnt > 0)
        {
            DiscardedRedundantSlices++;
            return;
        }

        if (_current is null)
        {
            StartPicture(packet, output);
        }
        else if (!SamePicture())
        {
            CodecException.InvalidData("A packet contains more than one primary coded picture.");
        }

        if (_header.FirstMb >= _slice!.MbCount)
        {
            CodecException.InvalidData("first_mb_in_slice is outside the picture.");
        }

        BuildReferenceLists();
        _slice.DecodeSlice(rbsp, payloadBits, dataStart, _header, _list0, _list1);
    }

    /// <summary>Begins decoding a picture from its first slice header.</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="output">Receives frames output by an IDR flush.</param>
    private void StartPicture(EncodedPacket packet, ICollection<VideoFrame> output)
    {
        var h = _header;
        var sps = h.Sps;
        var changed = _activeSps is null || (!ReferenceEquals(sps, _activeSps) && !sps.Rbsp.AsSpan().SequenceEqual(_activeSps.Rbsp));
        if (changed)
        {
            if (!h.IsIdr)
            {
                CodecException.InvalidData("The active SPS changes at a non-IDR picture.");
            }

            ValidateLimits(sps);
        }

        if (h.IsIdr)
        {
            FlushForIdr(h.NoOutputOfPriorPics, output);
            _awaitIdr = false;
        }

        if (changed)
        {
            Activate(sps);
        }
        else
        {
            _activeSps = sps;
        }

        if (!h.IsIdr)
        {
            FillFrameNumGap(output);
        }

        var picture = AcquirePicture();
        picture.IsIdr = h.IsIdr;
        picture.IsReferencePicture = h.NalRefIdc != 0;
        picture.FrameNum = h.FrameNum;
        picture.Timestamp = packet.Timestamp;
        picture.TimeBase = packet.TimeBase;
        picture.Sps = sps;
        ComputePictureOrder(picture);
        RememberPicture();
        _slice!.BeginPicture(picture);
        _current = picture;
    }

    /// <summary>Checks an SPS against the configured limits before anything is allocated for it.</summary>
    /// <param name="sps">The SPS.</param>
    private void ValidateLimits(H264Sps sps)
    {
        if (sps.WidthInMbs * 16 > _options.MaxWidth || sps.HeightInMbs * 16 > _options.MaxHeight || sps.MbCount > _options.MaxMacroblocks)
        {
            throw new CodecException(CodecErrorKind.ResourceLimit, "The picture size exceeds the configured limits.");
        }

        if ((sps.DisplayWidth & 1) != 0 || (sps.DisplayHeight & 1) != 0 || sps.DisplayWidth < 2 || sps.DisplayHeight < 2)
        {
            CodecException.InvalidData("The cropped picture size is invalid for 4:2:0.");
        }

        var bytes = (DpbSize(sps) + 1L) * H264Picture.ByteCount(sps.WidthInMbs, sps.HeightInMbs);
        if (bytes > _options.MaxDpbBytes)
        {
            throw new CodecException(CodecErrorKind.ResourceLimit, "The decoded picture buffer would exceed the configured memory limit.");
        }
    }

    /// <summary>Activates an SPS, replacing per-size state when the size changes.</summary>
    /// <param name="sps">The SPS.</param>
    private void Activate(H264Sps sps)
    {
        if (_slice is null || _slice.W != sps.WidthInMbs || _slice.H != sps.HeightInMbs)
        {
            ReleaseFree();
            _slice = new H264SliceDecoder(sps.WidthInMbs, sps.HeightInMbs);
        }

        _activeSps = sps;
        _dpbSize = DpbSize(sps);
        _numReorder = sps.MaxNumReorderFrames >= 0
            ? Math.Min(sps.MaxNumReorderFrames, _dpbSize)
            : (sps.ProfileIdc is 44 or 86 or 100 or 110 or 122 or 244) && sps.ConstraintSet3 ? 0 : _dpbSize;
    }

    /// <summary>Returns the DPB size in frames for an SPS (A.3.1, A.3.2, E.2.1).</summary>
    /// <param name="sps">The SPS.</param>
    /// <returns>1 to 16.</returns>
    private static int DpbSize(H264Sps sps)
    {
        var maxDpbMbs = H264Tables.MaxDpbMbs(sps.LevelIdc, sps.ConstraintSet3, sps.ProfileIdc);
        var size = maxDpbMbs == 0 ? 16 : Math.Min(maxDpbMbs / sps.MbCount, 16);
        if (sps.MaxDecFrameBuffering >= 0)
        {
            size = sps.MaxDecFrameBuffering;
        }

        return Math.Clamp(Math.Max(size, sps.MaxNumRefFrames), 1, 16);
    }

    /// <summary>Returns whether the current slice belongs to the picture being decoded (7.4.1.2.4).</summary>
    /// <returns><see langword="true"/> when every identifying field matches the first slice.</returns>
    private bool SamePicture()
    {
        var h = _header;
        return h.Pps.Id == _firstPpsId
            && h.FrameNum == _current!.FrameNum
            && h.IsIdr == _current.IsIdr
            && (h.NalRefIdc != 0) == _current.IsReferencePicture
            && h.IdrPicId == _firstIdrPicId
            && h.PocLsb == _firstPocLsb
            && h.DeltaPocBottom == _firstDeltaPocBottom
            && h.DeltaPoc0 == _firstDeltaPoc0
            && h.DeltaPoc1 == _firstDeltaPoc1;
    }

    /// <summary>Completes the picture: checks coverage, deblocks, marks references and stores it in the DPB.</summary>
    /// <param name="output">Receives frames.</param>
    private void FinishPicture(ICollection<VideoFrame> output)
    {
        var slice = _slice!;
        if (slice.DecodedMbCount != slice.MbCount)
        {
            CodecException.InvalidData("The slices of a picture do not cover every macroblock.");
        }

        slice.Deblock();
        StorePicture(_current!, output);
        _current = null;
    }

    /// <summary>Recovers from a failure: discards the current picture and all references, keeping pictures awaiting output.</summary>
    private void Abort()
    {
        if (_current is not null)
        {
            _current.ResetState();
            _free.Add(_current);
            _current = null;
        }

        ClearReferences();
        ReleaseFree();
        _awaitIdr = true;
    }

    /// <summary>Returns the sample buffers of every unused picture.</summary>
    private void ReleaseFree()
    {
        foreach (var picture in _free)
        {
            picture.Release();
        }

        _free.Clear();
    }

    /// <summary>Releases every picture.</summary>
    private void ReleaseAll()
    {
        if (_current is not null)
        {
            _free.Add(_current);
            _current = null;
        }

        _free.AddRange(_dpb);
        _dpb.Clear();
        ReleaseFree();
    }

    /// <summary>Returns a picture with buffers for the active size.</summary>
    /// <returns>The picture.</returns>
    private H264Picture AcquirePicture()
    {
        H264Picture picture;
        if (_free.Count > 0)
        {
            picture = _free[^1];
            _free.RemoveAt(_free.Count - 1);
        }
        else
        {
            if (_dpb.Count > _dpbSize)
            {
                CodecException.InvalidData("The decoded picture buffer overflows.");
            }

            picture = new H264Picture(_slice!.W, _slice.H);
        }

        if (!picture.IsAllocated)
        {
            picture.Allocate();
        }

        picture.ResetState();
        picture.Id = ++_nextPictureId;
        return picture;
    }

    /// <summary>Copies a picture's cropped samples into a new frame and appends it.</summary>
    /// <param name="picture">The picture.</param>
    /// <param name="output">Receives the frame.</param>
    private void Output(H264Picture picture, ICollection<VideoFrame> output)
    {
        var sps = picture.Sps!;
        var frame = new VideoFrame(sps.DisplayWidth, sps.DisplayHeight, picture.Timestamp, picture.TimeBase, sps.Color, _budget)
        {
            IsKeyframe = picture.IsIdr,
            IsReference = picture.IsReferencePicture,
            PresentationIndex = _presentationIndex++,
        };
        try
        {
            CopyPlane(picture.Luma, picture.Width, sps.CropLeft, sps.CropTop, frame.GetPlane(0), frame.Width, frame.Height);
            var cstride = picture.Width / 2;
            CopyPlane(picture.Cb, cstride, sps.CropLeft / 2, sps.CropTop / 2, frame.GetPlane(1), frame.ChromaWidth, frame.ChromaHeight);
            CopyPlane(picture.Cr, cstride, sps.CropLeft / 2, sps.CropTop / 2, frame.GetPlane(2), frame.ChromaWidth, frame.ChromaHeight);
            output.Add(frame);
        }
        catch
        {
            frame.Dispose();
            throw;
        }

        _lastOutputPoc = picture.Poc;
    }

    /// <summary>Copies a rectangle of a plane.</summary>
    /// <param name="source">The source plane.</param>
    /// <param name="stride">The source stride.</param>
    /// <param name="x">The left column.</param>
    /// <param name="y">The top row.</param>
    /// <param name="destination">The destination plane, stride <paramref name="width"/>.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    private static void CopyPlane(byte[] source, int stride, int x, int y, Span<byte> destination, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            source.AsSpan(((y + row) * stride) + x, width).CopyTo(destination.Slice(row * width, width));
        }
    }
}
