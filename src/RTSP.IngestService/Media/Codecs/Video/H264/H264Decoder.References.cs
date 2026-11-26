// Reference and ordering state for progressive frames: picture order count (8.2.1), frame_num gaps (8.2.5.2),
// reference picture lists and their modification (8.2.4), reference marking (8.2.5), and DPB storage with the
// bumping process (C.4.4, C.4.5). The DPB holds exactly the pictures that are references or await output.

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Reference handling part of the decoder.</summary>
internal sealed partial class H264Decoder
{
    /// <summary>Pictures used for reference or needed for output.</summary>
    private readonly List<H264Picture> _dpb = new(H264DecoderOptions.MaxPicturesPerCall);

    /// <summary>Allocated pictures of the active size not in use.</summary>
    private readonly List<H264Picture> _free = [];

    /// <summary>RefPicList0.</summary>
    private readonly H264Picture?[] _list0 = new H264Picture?[32];

    /// <summary>RefPicList1.</summary>
    private readonly H264Picture?[] _list1 = new H264Picture?[32];

    /// <summary>Scratch for list initialisation and modification.</summary>
    private readonly H264Picture?[] _scratch = new H264Picture?[33];

    /// <summary>Scratch for the second initial B list.</summary>
    private readonly H264Picture?[] _scratch1 = new H264Picture?[33];

    /// <summary>memory_management_control_operation of the current picture.</summary>
    private readonly int[] _mmcoOp = new int[H264SliceHeader.MaxOperations];

    /// <summary>First operand per operation.</summary>
    private readonly int[] _mmcoA = new int[H264SliceHeader.MaxOperations];

    /// <summary>Second operand per operation.</summary>
    private readonly int[] _mmcoB = new int[H264SliceHeader.MaxOperations];

    /// <summary>The DPB size in frames.</summary>
    private int _dpbSize = 16;

    /// <summary>The most pictures that may wait for output (max_num_reorder_frames).</summary>
    private int _numReorder = 16;

    /// <summary>The last picture identity handed out.</summary>
    private long _nextPictureId;

    /// <summary>The next presentation index.</summary>
    private long _presentationIndex;

    /// <summary>PicOrderCnt of the last output picture since the last IDR or MMCO 5.</summary>
    private long _lastOutputPoc = long.MinValue;

    /// <summary>prevPicOrderCntMsb.</summary>
    private int _prevPocMsb;

    /// <summary>prevPicOrderCntLsb.</summary>
    private int _prevPocLsb;

    /// <summary>prevFrameNumOffset.</summary>
    private int _prevFrameNumOffset;

    /// <summary>frame_num of the previous picture.</summary>
    private int _prevFrameNum;

    /// <summary>PrevRefFrameNum.</summary>
    private int _prevRefFrameNum;

    /// <summary>MaxLongTermFrameIdx; -1 for "no long-term frame indices".</summary>
    private int _maxLongTermFrameIdx = -1;

    /// <summary>PicOrderCntMsb of the current picture.</summary>
    private int _currentPocMsb;

    /// <summary>FrameNumOffset of the current picture.</summary>
    private int _currentFrameNumOffset;

    /// <summary>First-slice identity: pic_parameter_set_id.</summary>
    private int _firstPpsId;

    /// <summary>First-slice identity: idr_pic_id.</summary>
    private int _firstIdrPicId;

    /// <summary>First-slice identity: pic_order_cnt_lsb.</summary>
    private int _firstPocLsb;

    /// <summary>First-slice identity: delta_pic_order_cnt_bottom.</summary>
    private int _firstDeltaPocBottom;

    /// <summary>First-slice identity: delta_pic_order_cnt[0].</summary>
    private int _firstDeltaPoc0;

    /// <summary>First-slice identity: delta_pic_order_cnt[1].</summary>
    private int _firstDeltaPoc1;

    /// <summary>long_term_reference_flag of the current IDR picture.</summary>
    private bool _longTermReference;

    /// <summary>adaptive_ref_pic_marking_mode_flag of the current picture.</summary>
    private bool _adaptiveMarking;

    /// <summary>Whether the current picture has memory_management_control_operation 5.</summary>
    private bool _hasMmco5;

    /// <summary>The number of marking operations of the current picture.</summary>
    private int _mmcoCount;

    /// <summary>Resets POC and frame_num state.</summary>
    private void ResetOrderState()
    {
        _prevPocMsb = 0;
        _prevPocLsb = 0;
        _prevFrameNumOffset = 0;
        _prevFrameNum = 0;
        _prevRefFrameNum = 0;
        _maxLongTermFrameIdx = -1;
        _lastOutputPoc = long.MinValue;
    }

    /// <summary>Handles the DPB at an IDR picture (C.4.4): outputs or discards prior pictures and drops all references.</summary>
    /// <param name="noOutputOfPriorPics">no_output_of_prior_pics_flag.</param>
    /// <param name="output">Receives frames.</param>
    private void FlushForIdr(bool noOutputOfPriorPics, ICollection<VideoFrame> output)
    {
        if (noOutputOfPriorPics)
        {
            foreach (var picture in _dpb)
            {
                picture.NeededForOutput = false;
            }
        }

        while (Bump(output))
        {
        }

        ClearReferences();
        ResetOrderState();
    }

    /// <summary>Marks every reference unused and removes pictures no longer needed.</summary>
    private void ClearReferences()
    {
        foreach (var picture in _dpb)
        {
            picture.Mark = H264ReferenceMark.None;
        }

        Prune();
    }

    /// <summary>Removes pictures that are neither references nor waiting for output.</summary>
    private void Prune()
    {
        for (var i = _dpb.Count - 1; i >= 0; i--)
        {
            var picture = _dpb[i];
            if (!picture.IsReference && !picture.NeededForOutput)
            {
                _dpb.RemoveAt(i);
                Recycle(picture);
            }
        }
    }

    /// <summary>Keeps a picture's buffers for reuse when it matches the active size, otherwise returns them.</summary>
    /// <param name="picture">The picture.</param>
    private void Recycle(H264Picture picture)
    {
        if (picture.IsAllocated && _slice is not null && picture.WidthInMbs == _slice.W && picture.HeightInMbs == _slice.H)
        {
            picture.ResetState();
            _free.Add(picture);
        }
        else
        {
            picture.Release();
        }
    }

    /// <summary>Infers "non-existing" frames for a frame_num gap (8.2.5.2).</summary>
    /// <param name="output">Receives frames bumped to make room.</param>
    private void FillFrameNumGap(ICollection<VideoFrame> output)
    {
        var sps = _activeSps!;
        var maxFrameNum = 1 << sps.Log2MaxFrameNum;
        var frameNum = _header.FrameNum;
        if (frameNum == _prevRefFrameNum || frameNum == (_prevRefFrameNum + 1) % maxFrameNum)
        {
            return;
        }

        if (!sps.GapsInFrameNumAllowed)
        {
            CodecException.InvalidData("frame_num has a gap: reference pictures are missing.");
        }

        var unused = (_prevRefFrameNum + 1) % maxFrameNum;
        while (unused != frameNum)
        {
            SlidingWindow(Math.Max(sps.MaxNumRefFrames, 1), unused, maxFrameNum);
            Prune();
            while (_dpb.Count >= _dpbSize)
            {
                if (!Bump(output))
                {
                    CodecException.InvalidData("The decoded picture buffer overflows.");
                }
            }

            var picture = new H264Picture(_slice!.W, _slice.H)
            {
                Id = ++_nextPictureId,
                NonExisting = true,
                FrameNum = unused,
                Mark = H264ReferenceMark.ShortTerm,
                IsReferencePicture = true,
                Sps = sps,
            };
            _dpb.Add(picture);
            if (_prevFrameNum > unused)
            {
                _prevFrameNumOffset += maxFrameNum;
            }

            _prevFrameNum = unused;
            _prevRefFrameNum = unused;
            unused = (unused + 1) % maxFrameNum;
        }
    }

    /// <summary>Derives TopFieldOrderCnt, BottomFieldOrderCnt and PicOrderCnt (8.2.1).</summary>
    /// <param name="picture">The current picture.</param>
    private void ComputePictureOrder(H264Picture picture)
    {
        var sps = _activeSps!;
        var h = _header;
        var maxFrameNum = 1 << sps.Log2MaxFrameNum;
        var frameNum = h.FrameNum;
        long top, bottom;
        long offset = h.IsIdr ? 0 : _prevFrameNum > frameNum ? (long)_prevFrameNumOffset + maxFrameNum : _prevFrameNumOffset;
        switch (sps.PocType)
        {
            case 0:
                {
                    var prevMsb = h.IsIdr ? 0 : _prevPocMsb;
                    var prevLsb = h.IsIdr ? 0 : _prevPocLsb;
                    var maxLsb = 1 << sps.Log2MaxPocLsb;
                    var lsb = h.PocLsb;
                    long msb = lsb < prevLsb && prevLsb - lsb >= maxLsb / 2 ? (long)prevMsb + maxLsb
                        : lsb > prevLsb && lsb - prevLsb > maxLsb / 2 ? (long)prevMsb - maxLsb
                        : prevMsb;
                    _currentPocMsb = CheckInt(msb);
                    top = msb + lsb;
                    bottom = top + h.DeltaPocBottom;
                    break;
                }

            case 1:
                {
                    var cycle = sps.OffsetForRefFrame;
                    var absFrameNum = cycle.Length != 0 ? offset + frameNum : 0;
                    if (h.NalRefIdc == 0 && absFrameNum > 0)
                    {
                        absFrameNum--;
                    }

                    long expected = 0;
                    if (absFrameNum > 0)
                    {
                        long cycleDelta = 0;
                        foreach (var value in cycle)
                        {
                            cycleDelta += value;
                        }

                        var cycles = (absFrameNum - 1) / cycle.Length;
                        var inCycle = (absFrameNum - 1) % cycle.Length;
                        expected = cycles * cycleDelta;
                        for (var i = 0; i <= inCycle; i++)
                        {
                            expected += cycle[i];
                        }
                    }

                    if (h.NalRefIdc == 0)
                    {
                        expected += sps.OffsetForNonRefPic;
                    }

                    top = expected + h.DeltaPoc0;
                    bottom = top + sps.OffsetForTopToBottomField + h.DeltaPoc1;
                    break;
                }

            default:
                top = h.IsIdr ? 0 : h.NalRefIdc == 0 ? (2 * (offset + frameNum)) - 1 : 2 * (offset + frameNum);
                bottom = top;
                break;
        }

        _currentFrameNumOffset = CheckInt(offset);
        picture.TopPoc = CheckInt(top);
        picture.BottomPoc = CheckInt(bottom);
        picture.Poc = Math.Min(picture.TopPoc, picture.BottomPoc);
    }

    /// <summary>Narrows an order count, rejecting values outside 32 bits.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The value.</returns>
    private static int CheckInt(long value)
    {
        if (value is < int.MinValue or > int.MaxValue)
        {
            CodecException.InvalidData("A picture order count is out of range.");
        }

        return (int)value;
    }

    /// <summary>Records the first slice's identifying fields and marking operations.</summary>
    private void RememberPicture()
    {
        var h = _header;
        _firstPpsId = h.Pps.Id;
        _firstIdrPicId = h.IdrPicId;
        _firstPocLsb = h.PocLsb;
        _firstDeltaPocBottom = h.DeltaPocBottom;
        _firstDeltaPoc0 = h.DeltaPoc0;
        _firstDeltaPoc1 = h.DeltaPoc1;
        _longTermReference = h.LongTermReference;
        _adaptiveMarking = h.AdaptiveMarking;
        _hasMmco5 = h.HasMmco5;
        _mmcoCount = h.MmcoCount;
        Array.Copy(h.MmcoOp, _mmcoOp, h.MmcoCount);
        Array.Copy(h.MmcoA, _mmcoA, h.MmcoCount);
        Array.Copy(h.MmcoB, _mmcoB, h.MmcoCount);
    }

    /// <summary>Computes FrameNumWrap of every short-term reference (8-27).</summary>
    /// <param name="frameNum">The current frame_num.</param>
    /// <param name="maxFrameNum">MaxFrameNum.</param>
    private void UpdateFrameNumWrap(int frameNum, int maxFrameNum)
    {
        foreach (var picture in _dpb)
        {
            if (picture.Mark == H264ReferenceMark.ShortTerm)
            {
                picture.FrameNumWrap = picture.FrameNum > frameNum ? picture.FrameNum - maxFrameNum : picture.FrameNum;
            }
        }
    }

    /// <summary>Builds RefPicList0 and RefPicList1 for the current slice (8.2.4).</summary>
    private void BuildReferenceLists()
    {
        Array.Clear(_list0);
        Array.Clear(_list1);
        var h = _header;
        if (h.Type == H264SliceType.I)
        {
            return;
        }

        var maxFrameNum = 1 << _activeSps!.Log2MaxFrameNum;
        UpdateFrameNumWrap(h.FrameNum, maxFrameNum);
        var poc = _current!.Poc;
        var count0 = 0;
        if (h.Type == H264SliceType.P)
        {
            foreach (var picture in _dpb)
            {
                if (picture.Mark == H264ReferenceMark.ShortTerm)
                {
                    InsertSorted(_scratch, ref count0, 0, picture, static (a, b) => b.FrameNumWrap.CompareTo(a.FrameNumWrap));
                }
            }

            AppendLongTerm(_scratch, ref count0);
            FinishList(_scratch, count0, 0, _list0, maxFrameNum);
            return;
        }

        var count1 = 0;
        AppendShortTermByPoc(_scratch, ref count0, poc, before: true);
        AppendShortTermByPoc(_scratch, ref count0, poc, before: false);
        AppendLongTerm(_scratch, ref count0);
        AppendShortTermByPoc(_scratch1, ref count1, poc, before: false);
        AppendShortTermByPoc(_scratch1, ref count1, poc, before: true);
        AppendLongTerm(_scratch1, ref count1);
        if (count1 > 1 && count0 == count1 && _scratch.AsSpan(0, count0).SequenceEqual(_scratch1.AsSpan(0, count1)))
        {
            (_scratch1[0], _scratch1[1]) = (_scratch1[1], _scratch1[0]);
        }

        FinishList(_scratch, count0, 0, _list0, maxFrameNum);
        FinishList(_scratch1, count1, 1, _list1, maxFrameNum);
    }

    /// <summary>Inserts a picture in order into the run of a list that starts at <paramref name="start"/> and ends at its end.</summary>
    /// <param name="list">The list.</param>
    /// <param name="count">The number of entries; incremented.</param>
    /// <param name="start">The first index of the run.</param>
    /// <param name="picture">The picture.</param>
    /// <param name="compare">The order within the run.</param>
    private static void InsertSorted(H264Picture?[] list, ref int count, int start, H264Picture picture, Comparison<H264Picture> compare)
    {
        if (count >= 32)
        {
            CodecException.InvalidData("Too many reference pictures.");
        }

        var index = count;
        while (index > start && compare(list[index - 1]!, picture) > 0)
        {
            list[index] = list[index - 1];
            index--;
        }

        list[index] = picture;
        count++;
    }

    /// <summary>
    /// Appends the short-term references that precede (descending POC) or follow (ascending POC) the current picture
    /// (8.2.4.2.3); non-existing frames are not used for B prediction.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="count">The number of entries; incremented.</param>
    /// <param name="poc">The current PicOrderCnt.</param>
    /// <param name="before">Whether to append the preceding pictures.</param>
    private void AppendShortTermByPoc(H264Picture?[] list, ref int count, int poc, bool before)
    {
        var start = count;
        foreach (var picture in _dpb)
        {
            if (picture.Mark != H264ReferenceMark.ShortTerm || picture.NonExisting || (picture.Poc < poc) != before || picture.Poc == poc)
            {
                continue;
            }

            if (before)
            {
                InsertSorted(list, ref count, start, picture, static (a, b) => b.Poc.CompareTo(a.Poc));
            }
            else
            {
                InsertSorted(list, ref count, start, picture, static (a, b) => a.Poc.CompareTo(b.Poc));
            }
        }
    }

    /// <summary>Appends the long-term references in ascending LongTermPicNum order.</summary>
    /// <param name="list">The list.</param>
    /// <param name="count">The number of entries; incremented.</param>
    private void AppendLongTerm(H264Picture?[] list, ref int count)
    {
        var start = count;
        foreach (var picture in _dpb)
        {
            if (picture.Mark == H264ReferenceMark.LongTerm)
            {
                InsertSorted(list, ref count, start, picture, static (a, b) => a.LongTermFrameIdx.CompareTo(b.LongTermFrameIdx));
            }
        }
    }
    /// <summary>Truncates an initial list, applies ref_pic_list_modification (8.2.4.3), and copies the result.</summary>
    /// <param name="list">The initial list; used as scratch.</param>
    /// <param name="count">The initial entry count.</param>
    /// <param name="index">0 or 1.</param>
    /// <param name="target">Receives the final list.</param>
    /// <param name="maxFrameNum">MaxPicNum.</param>
    private void FinishList(H264Picture?[] list, int count, int index, H264Picture?[] target, int maxFrameNum)
    {
        var h = _header;
        var active = h.NumRefIdxActive[index];
        for (var i = Math.Min(count, active); i < list.Length; i++)
        {
            list[i] = null;
        }

        var currPicNum = h.FrameNum;
        var picNumPred = currPicNum;
        var refIdx = 0;
        for (var op = 0; op < h.ModificationCount[index]; op++)
        {
            var idc = h.ModificationIdc[(index * H264SliceHeader.MaxOperations) + op];
            var value = h.ModificationValue[(index * H264SliceHeader.MaxOperations) + op];
            H264Picture? picture = null;
            bool longTerm;
            int number;
            if (idc < 2)
            {
                var abs = value + 1;
                var noWrap = idc == 0 ? picNumPred - abs : picNumPred + abs;
                if (noWrap < 0)
                {
                    noWrap += maxFrameNum;
                }
                else if (noWrap >= maxFrameNum)
                {
                    noWrap -= maxFrameNum;
                }

                picNumPred = noWrap;
                number = noWrap > currPicNum ? noWrap - maxFrameNum : noWrap;
                longTerm = false;
                foreach (var candidate in _dpb)
                {
                    if (candidate.Mark == H264ReferenceMark.ShortTerm && candidate.FrameNumWrap == number)
                    {
                        picture = candidate;
                        break;
                    }
                }
            }
            else
            {
                number = value;
                longTerm = true;
                foreach (var candidate in _dpb)
                {
                    if (candidate.Mark == H264ReferenceMark.LongTerm && candidate.LongTermFrameIdx == number)
                    {
                        picture = candidate;
                        break;
                    }
                }
            }

            if (picture is null)
            {
                CodecException.InvalidData("A reference picture list modification names a missing picture.");
            }

            for (var c = active; c > refIdx; c--)
            {
                list[c] = list[c - 1];
            }

            list[refIdx++] = picture;
            var n = refIdx;
            for (var c = refIdx; c <= active; c++)
            {
                var entry = list[c];
                var same = entry is not null
                    && (longTerm
                        ? entry.Mark == H264ReferenceMark.LongTerm && entry.LongTermFrameIdx == number
                        : entry.Mark == H264ReferenceMark.ShortTerm && entry.FrameNumWrap == number);
                if (!same)
                {
                    list[n++] = entry;
                }
            }

            for (; n <= active; n++)
            {
                list[n] = null;
            }
        }

        Array.Copy(list, target, active);
    }

    /// <summary>Marks the current reference picture and applies the marking process (8.2.5).</summary>
    /// <param name="current">The current picture.</param>
    /// <returns>Whether memory_management_control_operation 5 was applied.</returns>
    private bool MarkReferences(H264Picture current)
    {
        var sps = _activeSps!;
        var maxRefs = Math.Max(sps.MaxNumRefFrames, 1);
        var maxFrameNum = 1 << sps.Log2MaxFrameNum;
        if (current.IsIdr)
        {
            current.Mark = _longTermReference ? H264ReferenceMark.LongTerm : H264ReferenceMark.ShortTerm;
            current.LongTermFrameIdx = 0;
            _maxLongTermFrameIdx = _longTermReference ? 0 : -1;
            return false;
        }

        UpdateFrameNumWrap(current.FrameNum, maxFrameNum);
        var mmco5 = false;
        if (_adaptiveMarking)
        {
            for (var i = 0; i < _mmcoCount; i++)
            {
                int a = _mmcoA[i], b = _mmcoB[i];
                switch (_mmcoOp[i])
                {
                    case 1:
                        ShortTerm(current.FrameNum - (a + 1))?.Mark = H264ReferenceMark.None;
                        break;
                    case 2:
                        LongTerm(a)?.Mark = H264ReferenceMark.None;
                        break;
                    case 3:
                        {
                            CheckLongTermIndex(b);
                            var target = ShortTerm(current.FrameNum - (a + 1));
                            var existing = LongTerm(b);
                            if (existing is not null && existing != target)
                            {
                                existing.Mark = H264ReferenceMark.None;
                            }

                            if (target is not null)
                            {
                                target.Mark = H264ReferenceMark.LongTerm;
                                target.LongTermFrameIdx = b;
                            }

                            break;
                        }

                    case 4:
                        _maxLongTermFrameIdx = b - 1;
                        foreach (var picture in _dpb)
                        {
                            if (picture.Mark == H264ReferenceMark.LongTerm && picture.LongTermFrameIdx > _maxLongTermFrameIdx)
                            {
                                picture.Mark = H264ReferenceMark.None;
                            }
                        }

                        break;
                    case 5:
                        foreach (var picture in _dpb)
                        {
                            picture.Mark = H264ReferenceMark.None;
                        }

                        _maxLongTermFrameIdx = -1;
                        mmco5 = true;
                        break;
                    default:
                        CheckLongTermIndex(b);
                        LongTerm(b)?.Mark = H264ReferenceMark.None;
                        current.Mark = H264ReferenceMark.LongTerm;
                        current.LongTermFrameIdx = b;
                        break;
                }
            }

            if (current.Mark != H264ReferenceMark.LongTerm)
            {
                current.Mark = H264ReferenceMark.ShortTerm;
            }
        }
        else
        {
            SlidingWindow(maxRefs, current.FrameNum, maxFrameNum);
            current.Mark = H264ReferenceMark.ShortTerm;
        }

        var references = 1;
        foreach (var picture in _dpb)
        {
            references += picture.IsReference ? 1 : 0;
        }

        if (references > maxRefs)
        {
            CodecException.InvalidData("More pictures are marked as reference than max_num_ref_frames allows.");
        }

        return mmco5;
    }

    /// <summary>Rejects a LongTermFrameIdx above MaxLongTermFrameIdx.</summary>
    /// <param name="index">The index.</param>
    private void CheckLongTermIndex(int index)
    {
        if (index > _maxLongTermFrameIdx)
        {
            CodecException.InvalidData("long_term_frame_idx exceeds MaxLongTermFrameIdx.");
        }
    }

    /// <summary>Finds the short-term reference with a PicNum.</summary>
    /// <param name="picNum">The PicNum.</param>
    /// <returns>The picture, or <see langword="null"/>.</returns>
    private H264Picture? ShortTerm(int picNum)
    {
        foreach (var picture in _dpb)
        {
            if (picture.Mark == H264ReferenceMark.ShortTerm && picture.FrameNumWrap == picNum)
            {
                return picture;
            }
        }

        return null;
    }

    /// <summary>Finds the long-term reference with a LongTermPicNum.</summary>
    /// <param name="number">The LongTermPicNum.</param>
    /// <returns>The picture, or <see langword="null"/>.</returns>
    private H264Picture? LongTerm(int number)
    {
        foreach (var picture in _dpb)
        {
            if (picture.Mark == H264ReferenceMark.LongTerm && picture.LongTermFrameIdx == number)
            {
                return picture;
            }
        }

        return null;
    }

    /// <summary>The sliding window marking process (8.2.5.3), applied before the current picture is marked.</summary>
    /// <param name="maxRefs">Max(max_num_ref_frames, 1).</param>
    /// <param name="frameNum">The frame_num of the picture about to be marked.</param>
    /// <param name="maxFrameNum">MaxFrameNum.</param>
    private void SlidingWindow(int maxRefs, int frameNum, int maxFrameNum)
    {
        UpdateFrameNumWrap(frameNum, maxFrameNum);
        while (true)
        {
            var references = 0;
            H264Picture? oldest = null;
            foreach (var picture in _dpb)
            {
                if (!picture.IsReference)
                {
                    continue;
                }

                references++;
                if (picture.Mark == H264ReferenceMark.ShortTerm && (oldest is null || picture.FrameNumWrap < oldest.FrameNumWrap))
                {
                    oldest = picture;
                }
            }

            if (references < maxRefs)
            {
                return;
            }

            if (oldest is null)
            {
                CodecException.InvalidData("The sliding window has no short-term reference to remove.");
            }

            oldest.Mark = H264ReferenceMark.None;
        }
    }

    /// <summary>Marks and stores the decoded picture, outputting pictures as the bumping process requires (C.4.5).</summary>
    /// <param name="current">The current picture.</param>
    /// <param name="output">Receives frames.</param>
    private void StorePicture(H264Picture current, ICollection<VideoFrame> output)
    {
        if (!_hasMmco5 && current.Poc < _lastOutputPoc)
        {
            CodecException.InvalidData("A picture precedes one already output in presentation order.");
        }

        var mmco5 = current.IsReferencePicture && MarkReferences(current);
        Prune();
        var sps = _activeSps!;
        if (mmco5)
        {
            while (Bump(output))
            {
            }

            var temp = Math.Min(current.TopPoc, current.BottomPoc);
            current.TopPoc -= temp;
            current.BottomPoc -= temp;
            current.Poc = 0;
            current.FrameNum = 0;
            _lastOutputPoc = long.MinValue;
        }

        if (sps.PocType == 0 && current.IsReferencePicture)
        {
            _prevPocMsb = mmco5 ? 0 : _currentPocMsb;
            _prevPocLsb = mmco5 ? current.TopPoc : _firstPocLsb;
        }

        _prevFrameNumOffset = mmco5 ? 0 : _currentFrameNumOffset;
        _prevFrameNum = current.FrameNum;
        if (current.IsReferencePicture)
        {
            _prevRefFrameNum = current.FrameNum;
        }

        if (!current.IsReferencePicture)
        {
            while (_dpb.Count >= _dpbSize)
            {
                if (!HasWaitingBefore(current.Poc))
                {
                    Output(current, output);
                    Recycle(current);
                    return;
                }

                Bump(output);
            }
        }
        else
        {
            while (_dpb.Count >= _dpbSize)
            {
                if (!Bump(output))
                {
                    CodecException.InvalidData("The decoded picture buffer overflows.");
                }
            }
        }

        current.NeededForOutput = true;
        _dpb.Add(current);
        while (WaitingCount() > _numReorder)
        {
            Bump(output);
        }
    }

    /// <summary>Returns whether a picture waiting for output precedes a POC.</summary>
    /// <param name="poc">The POC.</param>
    /// <returns><see langword="true"/> when one does.</returns>
    private bool HasWaitingBefore(int poc)
    {
        foreach (var picture in _dpb)
        {
            if (picture.NeededForOutput && picture.Poc < poc)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Counts pictures waiting for output.</summary>
    /// <returns>The count.</returns>
    private int WaitingCount()
    {
        var count = 0;
        foreach (var picture in _dpb)
        {
            count += picture.NeededForOutput ? 1 : 0;
        }

        return count;
    }

    /// <summary>Outputs the waiting picture with the smallest POC (the bumping process).</summary>
    /// <param name="output">Receives the frame.</param>
    /// <returns><see langword="false"/> when no picture is waiting.</returns>
    private bool Bump(ICollection<VideoFrame> output)
    {
        H264Picture? next = null;
        foreach (var picture in _dpb)
        {
            if (picture.NeededForOutput && (next is null || picture.Poc < next.Poc))
            {
                next = picture;
            }
        }

        if (next is null)
        {
            return false;
        }

        Output(next, output);
        next.NeededForOutput = false;
        if (!next.IsReference)
        {
            _dpb.Remove(next);
            Recycle(next);
        }

        return true;
    }
}
