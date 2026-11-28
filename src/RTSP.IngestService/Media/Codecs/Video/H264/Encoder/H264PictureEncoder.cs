// Codes one picture: prepares the half-sample planes of the first reference, codes every macroblock in raster order
// through the macroblock context into per-macroblock records, serialises each slice (slice header (7.3.3), then
// slice_data() from the records), then runs the deblocking filter over the finished reconstruction. A macroblock
// whose coded size would exceed 3200 bits is replaced by I_PCM so every macroblock and therefore every picture has a
// hard size bound. All scratch is per encoder; nothing is allocated per picture or macroblock. Given helper threads
// (tests and benchmarks only), the same passes run as a row wavefront (H264WavefrontJob) with one context per worker;
// the serialiser still writes every slice alone, in raster order, so the output is identical.

using System.Diagnostics;

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>One memory_management_control_operation (7.3.3.3).</summary>
/// <param name="Operation">memory_management_control_operation, 1 to 6.</param>
/// <param name="Value">difference_of_pic_nums_minus1 (1, 3), long_term_pic_num (2) or max_long_term_frame_idx_plus1 (4).</param>
/// <param name="LongTermFrameIdx">long_term_frame_idx (3, 6).</param>
internal readonly record struct H264Mmco(int Operation, int Value = 0, int LongTermFrameIdx = 0);

/// <summary>One ref_pic_list_modification command (7.3.3.1).</summary>
/// <param name="Idc">modification_of_pic_nums_idc, 0 to 2.</param>
/// <param name="Value">abs_diff_pic_num_minus1 or long_term_pic_num.</param>
internal readonly record struct H264ListModification(int Idc, int Value);

/// <summary>The slice-header values shared by every slice of a picture.</summary>
internal sealed class H264PictureHeader
{
    /// <summary>Gets or sets whether the picture is an IDR picture.</summary>
    public bool IsIdr { get; set; }

    /// <summary>Gets or sets nal_ref_idc; 0 for a non-reference picture.</summary>
    public int NalRefIdc { get; set; }

    /// <summary>Gets or sets frame_num.</summary>
    public int FrameNum { get; set; }

    /// <summary>Gets or sets idr_pic_id.</summary>
    public int IdrPicId { get; set; }

    /// <summary>Gets or sets pic_order_cnt_lsb (POC type 0).</summary>
    public int PocLsb { get; set; }

    /// <summary>Gets or sets num_ref_idx_l0_active_minus1 + 1 (P pictures).</summary>
    public int ActiveReferences { get; set; }

    /// <summary>Gets or sets long_term_reference_flag (IDR pictures).</summary>
    public bool LongTermReference { get; set; }

    /// <summary>Gets the ref_pic_list_modification commands of list 0.</summary>
    public List<H264ListModification> ListModifications { get; } = [];

    /// <summary>Gets the memory management operations; empty selects the sliding window.</summary>
    public List<H264Mmco> Mmcos { get; } = [];

    /// <summary>Gets or sets the picture QP.</summary>
    public int Qp { get; set; }

    /// <summary>Gets or sets whether every macroblock with a reference is coded P_Skip (the VBV's last resort).</summary>
    public bool ForceSkip { get; set; }

    /// <summary>Gets or sets redundant_pic_cnt, written when the PPS signals it.</summary>
    public int RedundantPicCnt { get; set; }

    /// <summary>Gets or sets a predicate selecting macroblocks to code as I_PCM regardless of cost (conformance streams).</summary>
    public Func<int, bool>? ForcePcm { get; set; }

    /// <summary>Gets whether the picture is coded with I slices.</summary>
    public bool IsIntra => IsIdr || ActiveReferences == 0;
}

/// <summary>The outcome of coding one picture.</summary>
internal struct H264PictureCounters
{
    /// <summary>Intra macroblocks.</summary>
    public int Intra;

    /// <summary>P_Skip macroblocks.</summary>
    public int Skip;

    /// <summary>I_PCM macroblocks.</summary>
    public int Pcm;

    /// <summary>The summed mode-decision distortion (SATD) of the chosen modes, the rate controller's complexity.</summary>
    public long Satd;
}

/// <summary>The duration of each pass of the last coded picture, in Stopwatch ticks, as seen by the encoding thread.</summary>
/// <param name="Prepare">The half-sample planes.</param>
/// <param name="Code">The macroblocks.</param>
/// <param name="Serialise">The slices.</param>
/// <param name="Deblock">The deblocking filter; in a wavefront picture it starts with the serialiser, which it overlaps.</param>
internal readonly record struct H264PictureTimings(long Prepare, long Code, long Serialise, long Deblock);

/// <summary>Codes pictures into slice RBSPs.</summary>
internal sealed class H264PictureEncoder
{
    /// <summary>The macroblock size bound: 128 + RawMbBits for 8-bit 4:2:0 (A.3.1).</summary>
    public const int MaxMbBits = 3200;

    /// <summary>The sequence layout.</summary>
    private readonly H264SequenceLayout _layout;

    /// <summary>The half-sample planes of the first reference, for quarter-sample refinement.</summary>
    private readonly H264HalfSamplePlanes _halfSamples;

    /// <summary>The records of the picture being coded.</summary>
    private readonly H264MacroblockRecords _records;

    /// <summary>The macroblock context of the encoding thread.</summary>
    private readonly H264MacroblockContext _context;

    /// <summary>The helper threads, or null for serial coding.</summary>
    private readonly H264WavefrontHelpers? _helpers;

    /// <summary>The contexts of the encoding thread and every helper; null for serial coding.</summary>
    private readonly H264MacroblockContext[]? _contexts;

    /// <summary>The wavefront state; null for serial coding.</summary>
    private readonly H264WavefrontJob? _job;

    /// <summary>Initializes a new instance of the <see cref="H264PictureEncoder"/> class.</summary>
    /// <param name="layout">The sequence layout.</param>
    /// <param name="searchRange">The motion search range.</param>
    /// <param name="maxPayloadBytes">The bound of the macroblock records' payload: the access-unit RBSP bound.</param>
    /// <param name="helpers">Helper threads for a row wavefront (tests and benchmarks); null or none selects serial coding.</param>
    public H264PictureEncoder(H264SequenceLayout layout, int searchRange, int maxPayloadBytes, H264WavefrontHelpers? helpers = null)
    {
        _layout = layout;
        _halfSamples = new H264HalfSamplePlanes(layout.WidthInMbs * 16, layout.HeightInMbs * 16);
        if (helpers is null || helpers.Count == 0)
        {
            _records = new H264MacroblockRecords(layout.Macroblocks, maxPayloadBytes);
            _context = new H264MacroblockContext(layout, searchRange, _halfSamples);
            return;
        }

        var workers = 1 + helpers.Count;
        _helpers = helpers;
        _records = new H264MacroblockRecords(layout.Macroblocks, maxPayloadBytes, workers);
        _contexts = new H264MacroblockContext[workers];
        for (var i = 0; i < workers; i++)
        {
            _contexts[i] = new H264MacroblockContext(layout, searchRange, _halfSamples, i, bandSums: true);
        }

        _context = _contexts[0];
        _job = new H264WavefrontJob(layout, _halfSamples, _records, _contexts, helpers);
    }

    /// <summary>Gets the pass durations of the last coded picture.</summary>
    public H264PictureTimings LastTimings { get; private set; }

    /// <summary>Gets the wavefront state of the last coded picture, or null for serial coding.</summary>
    public H264WavefrontJob? Wavefront => _job;

    /// <summary>Returns the bytes the picture encoder holds beyond the planes: records, contexts and wavefront state.</summary>
    /// <param name="layout">The sequence layout.</param>
    /// <param name="maxPayloadBytes">The bound of each records payload.</param>
    /// <param name="helpers">The helper count; 0 for serial coding.</param>
    /// <returns>The bytes.</returns>
    public static long ByteCount(H264SequenceLayout layout, int maxPayloadBytes, int helpers)
    {
        var bytes = H264MacroblockRecords.ByteCount(layout.Macroblocks, maxPayloadBytes, 1 + helpers);
        if (helpers > 0)
        {
            bytes += (helpers * (long)H264MacroblockContext.ScratchBytes) + ((1L + helpers) * 4 * H264HalfSamplePlanes.SumsLength(layout.WidthInMbs * 16)) + H264WavefrontJob.ByteCount(layout, 1 + helpers);
        }

        return bytes;
    }

    /// <summary>Codes a picture into one RBSP per slice and deblocks the reconstruction.</summary>
    /// <param name="source">The source frame, at the layout's size.</param>
    /// <param name="picture">Receives the reconstruction and coding state.</param>
    /// <param name="references">The list 0 references (P pictures); null for a non-existing frame.</param>
    /// <param name="header">The picture header.</param>
    /// <param name="slices">The macroblock rows at which each slice starts, then the row count.</param>
    /// <param name="writer">Receives the slice RBSPs back to back, each byte aligned.</param>
    /// <param name="sliceEnds">Receives the byte offset after each slice.</param>
    /// <param name="cancellationToken">Observed between macroblock rows.</param>
    /// <returns>The picture counters.</returns>
    public H264PictureCounters Encode(VideoFrame source, H264EncoderPicture picture, H264EncoderPicture?[] references, H264PictureHeader header, ReadOnlySpan<int> slices, H264BitWriter writer, List<int> sliceEnds, CancellationToken cancellationToken)
    {
        // Only P macroblocks that are not P_Skip refine against the first reference; a forced-skip picture has none.
        var ref0 = !header.IsIntra && !header.ForceSkip && references.Length > 0 ? references[0] : null;
        if (_job is not null)
        {
            return EncodeWavefront(source, picture, references, ref0, header, slices, writer, sliceEnds, cancellationToken);
        }

        var started = Stopwatch.GetTimestamp();
        if (ref0 is not null)
        {
            _halfSamples.Prepare(ref0);
        }

        var prepared = Stopwatch.GetTimestamp();
        var width = _layout.WidthInMbs;
        _records.Clear();
        _context.BeginPicture(source, picture, references, header);
        for (var slice = 0; slice < slices.Length - 1; slice++)
        {
            _context.BeginSlice(slice);
            for (var mbAddr = slices[slice] * width; mbAddr < slices[slice + 1] * width; mbAddr++)
            {
                if (mbAddr % width == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                _context.Encode(mbAddr, _records);
            }
        }

        var coded = Stopwatch.GetTimestamp();
        WriteSlices(header, slices, writer, sliceEnds);
        var serialised = Stopwatch.GetTimestamp();
        H264EncoderDeblocking.Filter(picture);
        LastTimings = new H264PictureTimings(prepared - started, coded - prepared, serialised - coded, Stopwatch.GetTimestamp() - serialised);
        return _context.Counters;
    }

    /// <summary>Codes a picture as a row wavefront with the helpers; the output equals <see cref="Encode"/>'s serial path.</summary>
    /// <param name="source">The source frame.</param>
    /// <param name="picture">Receives the reconstruction and coding state.</param>
    /// <param name="references">The list 0 references.</param>
    /// <param name="ref0">The reference whose half-sample planes the picture needs, or null.</param>
    /// <param name="header">The picture header.</param>
    /// <param name="slices">The slice start rows, then the row count.</param>
    /// <param name="writer">Receives the slice RBSPs.</param>
    /// <param name="sliceEnds">Receives the byte offset after each slice.</param>
    /// <param name="cancellationToken">Observed at row claims and while waiting.</param>
    /// <returns>The picture counters.</returns>
    private H264PictureCounters EncodeWavefront(VideoFrame source, H264EncoderPicture picture, H264EncoderPicture?[] references, H264EncoderPicture? ref0, H264PictureHeader header, ReadOnlySpan<int> slices, H264BitWriter writer, List<int> sliceEnds, CancellationToken cancellationToken)
    {
        var job = _job!;
        var contexts = _contexts!;
        _records.Clear();
        foreach (var context in contexts)
        {
            context.BeginPicture(source, picture, references, header);
        }

        job.Begin(picture, ref0, slices, cancellationToken);
        job.Open();
        var published = _helpers!.TryPublish(job);
        long started = job.Started, coded = 0, serialised = 0, deblocked = 0;
        try
        {
            if (job.Code())
            {
                coded = Stopwatch.GetTimestamp();
                job.OpenDeblocking();
                WriteSlices(header, slices, writer, sliceEnds);
                serialised = Stopwatch.GetTimestamp();
                if (job.Deblock())
                {
                    deblocked = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (Exception e)
        {
            job.Fail(e);
        }
        finally
        {
            job.Close();
            if (published)
            {
                _helpers.Withdraw(job);
            }
        }

        job.Complete();
        var prepare = job.Workers[0].PrepareTicks;
        LastTimings = new H264PictureTimings(prepare, coded - started - prepare, serialised - coded, deblocked - coded);
        var counters = default(H264PictureCounters);
        foreach (var context in contexts)
        {
            var c = context.Counters;
            counters.Intra += c.Intra;
            counters.Skip += c.Skip;
            counters.Pcm += c.Pcm;
            counters.Satd += c.Satd;
        }

        return counters;
    }

    /// <summary>Writes every slice of the picture from the records.</summary>
    /// <param name="header">The picture header.</param>
    /// <param name="slices">The slice start rows, then the row count.</param>
    /// <param name="writer">Receives the slice RBSPs.</param>
    /// <param name="sliceEnds">Receives the byte offset after each slice.</param>
    private void WriteSlices(H264PictureHeader header, ReadOnlySpan<int> slices, H264BitWriter writer, List<int> sliceEnds)
    {
        var width = _layout.WidthInMbs;
        for (var slice = 0; slice < slices.Length - 1; slice++)
        {
            var firstMb = slices[slice] * width;
            WriteSliceHeader(writer, header, firstMb);
            _records.WriteSliceData(writer, firstMb, slices[slice + 1] * width, header.IsIntra);
            writer.WriteTrailingBits();
            sliceEnds.Add((int)(writer.Position >> 3));
        }
    }

    /// <summary>Writes slice_header() (7.3.3) for a slice starting at a macroblock.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="h">The picture header.</param>
    /// <param name="firstMb">first_mb_in_slice.</param>
    private void WriteSliceHeader(H264BitWriter w, H264PictureHeader h, int firstMb)
    {
        w.WriteUe(firstMb);
        w.WriteUe(h.IsIntra ? 7 : 5);
        w.WriteUe(0);
        w.Write((uint)h.FrameNum, _layout.Log2MaxFrameNum);
        if (h.IsIdr)
        {
            w.WriteUe(h.IdrPicId);
        }

        if (_layout.PocType == 0)
        {
            w.Write((uint)h.PocLsb, _layout.Log2MaxPocLsb);
        }
        else if (_layout.PocType == 1)
        {
            // 8.2.1.2: a non-reference picture's expected POC is 2 * frame_num - 3, before the previous reference
            // picture; delta_pic_order_cnt[0] moves it to 2 * frame_num - 1 so output order stays decoding order.
            w.WriteSe(h.NalRefIdc == 0 ? 1 - H264SequenceLayout.OffsetForNonRefPic : 0);
        }

        if (_layout.RedundantPicCntPresent)
        {
            w.WriteUe(h.RedundantPicCnt);
        }

        if (!h.IsIntra)
        {
            var overriding = h.ActiveReferences != _layout.MaxReferenceFrames;
            w.WriteBit(overriding);
            if (overriding)
            {
                w.WriteUe(h.ActiveReferences - 1);
            }

            w.WriteBit(h.ListModifications.Count != 0);
            if (h.ListModifications.Count != 0)
            {
                foreach (var m in h.ListModifications)
                {
                    w.WriteUe(m.Idc);
                    w.WriteUe(m.Value);
                }

                w.WriteUe(3);
            }
        }

        if (h.NalRefIdc != 0)
        {
            if (h.IsIdr)
            {
                w.WriteBit(false);
                w.WriteBit(h.LongTermReference);
            }
            else
            {
                w.WriteBit(h.Mmcos.Count != 0);
                if (h.Mmcos.Count != 0)
                {
                    foreach (var m in h.Mmcos)
                    {
                        w.WriteUe(m.Operation);
                        if (m.Operation is 1 or 2 or 3 or 4)
                        {
                            w.WriteUe(m.Value);
                        }

                        if (m.Operation is 3 or 6)
                        {
                            w.WriteUe(m.LongTermFrameIdx);
                        }
                    }

                    w.WriteUe(0);
                }
            }
        }

        w.WriteSe(h.Qp - 26);
        w.WriteUe(0);
        w.WriteSe(0);
        w.WriteSe(0);
    }
}
