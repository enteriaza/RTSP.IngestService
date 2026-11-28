// The mutable state of coding one macroblock: source and prediction scratch, levels, the mode decision and the
// macroblock cursor. Per macroblock it runs the mode decision, prediction, transform, quantisation, reconstruction
// and CAVLC syntax, writes the macroblock_layer() bits (or the I_PCM samples) into the picture's records, and updates
// the picture's reconstruction and neighbour state. Mode decision compares SATD plus lambda-weighted header bits of
// P_Skip, P_L0_16x16, P_8x8 (four 8x8 partitions), Intra_16x16 and Intra_4x4; a macroblock whose coded size would
// exceed 3200 bits is replaced by I_PCM. The picture, source, references, header and half-sample planes are borrowed
// from the picture encoder for the duration of a picture; nothing is allocated per macroblock. A context belongs to
// one worker: concurrent workers each have their own, writing their own payload of the records.

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>Codes macroblocks of a picture, one at a time, into macroblock records.</summary>
internal sealed partial class H264MacroblockContext
{
    /// <summary>Raster 4x4 block index by luma4x4BlkIdx.</summary>
    private static readonly byte[] BlkRaster = [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15];

    /// <summary>The sequence layout.</summary>
    private readonly H264SequenceLayout _layout;

    /// <summary>The integer motion search range.</summary>
    private readonly int _searchRange;

    /// <summary>The records payload this context writes.</summary>
    private readonly int _writer;

    /// <summary>Source luma of the macroblock.</summary>
    private readonly byte[] _srcY = new byte[256];

    /// <summary>Source chroma of the macroblock: 64 Cb then 64 Cr.</summary>
    private readonly byte[] _srcC = new byte[128];

    /// <summary>Inter luma prediction.</summary>
    private readonly byte[] _predY = new byte[256];

    /// <summary>Inter chroma prediction: 64 Cb then 64 Cr.</summary>
    private readonly byte[] _predC = new byte[128];

    /// <summary>Best inter luma prediction kept while other modes are tried.</summary>
    private readonly byte[] _bestPredY = new byte[256];

    /// <summary>Luma levels in scan order by raster block, 16 per block.</summary>
    private readonly int[] _luma = new int[256];

    /// <summary>Intra16x16 DC levels in scan order.</summary>
    private readonly int[] _lumaDc = new int[16];

    /// <summary>Chroma DC levels, raster, 4 per component.</summary>
    private readonly int[] _chromaDc = new int[8];

    /// <summary>Chroma AC levels in scan order (positions 1 to 15), 15 per block, 4 blocks per component.</summary>
    private readonly int[] _chromaAc = new int[120];

    /// <summary>Intra4x4 modes chosen for the macroblock by raster block.</summary>
    private readonly sbyte[] _modes = new sbyte[16];

    /// <summary>The partition motion of the macroblock: x, y and reference index per 8x8 partition.</summary>
    private readonly int[] _partition = new int[12];

    /// <summary>The motion vector predictor of each 8x8 partition (x, y), for the mvd syntax.</summary>
    private readonly int[] _partitionMvp = new int[8];

    /// <summary>The picture being coded.</summary>
    private H264EncoderPicture _pic = null!;

    /// <summary>The source frame.</summary>
    private VideoFrame _source = null!;

    /// <summary>The reference list of the picture; null entries are non-existing frames.</summary>
    private H264EncoderPicture?[] _refs = [];

    /// <summary>The picture header.</summary>
    private H264PictureHeader _header = null!;

    /// <summary>The macroblock column.</summary>
    private int _mbx;

    /// <summary>The macroblock row.</summary>
    private int _mby;

    /// <summary>The macroblock address.</summary>
    private int _mbAddr;

    /// <summary>The slice index.</summary>
    private int _slice;

    /// <summary>The picture QP.</summary>
    private int _qp;

    /// <summary>sqrt(lambda) * 256 at the picture QP.</summary>
    private int _lambda;

    /// <summary>Raster blocks of the current macroblock whose motion is already decided.</summary>
    private int _doneBlocks;

    /// <summary>The counters of the picture.</summary>
    private H264PictureCounters _counters;

    /// <summary>Initializes a new instance of the <see cref="H264MacroblockContext"/> class.</summary>
    /// <param name="layout">The sequence layout.</param>
    /// <param name="searchRange">The motion search range.</param>
    /// <param name="halfSamples">The half-sample planes of the first reference, prepared before each P picture's macroblocks.</param>
    /// <param name="writer">The records payload this context writes: its worker.</param>
    /// <param name="bandSums">Whether to hold sums for computing half-sample bands (wavefront workers).</param>
    public H264MacroblockContext(H264SequenceLayout layout, int searchRange, H264HalfSamplePlanes halfSamples, int writer = 0, bool bandSums = false)
    {
        _layout = layout;
        _searchRange = searchRange;
        _halfSamples = halfSamples;
        _writer = writer;
        BandSums = bandSums ? new int[H264HalfSamplePlanes.SumsLength(layout.WidthInMbs * 16)] : [];
    }

    /// <summary>Gets the bytes of a context's scratch arrays, without band sums.</summary>
    public static int ScratchBytes => 256 + 128 + 256 + 128 + 256 + (4 * (256 + 16 + 8 + 120 + 12 + 8)) + 16;

    /// <summary>Gets the worker's horizontal-sum scratch for half-sample bands; empty unless requested.</summary>
    public int[] BandSums { get; }

    /// <summary>Gets the counters of the macroblocks coded since <see cref="BeginPicture"/>.</summary>
    public H264PictureCounters Counters => _counters;

    /// <summary>Starts a picture and resets the counters.</summary>
    /// <param name="source">The source frame.</param>
    /// <param name="picture">Receives the reconstruction and coding state.</param>
    /// <param name="references">The list 0 references.</param>
    /// <param name="header">The picture header.</param>
    public void BeginPicture(VideoFrame source, H264EncoderPicture picture, H264EncoderPicture?[] references, H264PictureHeader header)
    {
        _source = source;
        _pic = picture;
        _refs = references;
        _header = header;
        _qp = header.Qp;
        _lambda = H264EncoderTables.Lambda256[_qp];
        _counters = default;
    }

    /// <summary>Drops the picture's borrowed state; the counters stay readable.</summary>
    public void EndPicture()
    {
        _source = null!;
        _pic = null!;
        _refs = [];
        _header = null!;
    }

    /// <summary>Starts a slice; macroblocks of earlier slices become unavailable for prediction.</summary>
    /// <param name="slice">The slice index.</param>
    public void BeginSlice(int slice) => _slice = slice;

    /// <summary>Chooses and codes one macroblock, recording its syntax.</summary>
    /// <param name="mbAddr">The macroblock address; macroblocks are coded in raster order within each slice.</param>
    /// <param name="records">Receives the macroblock's record.</param>
    public void Encode(int mbAddr, H264MacroblockRecords records)
    {
        _mbAddr = mbAddr;
        _mbx = mbAddr % _layout.WidthInMbs;
        _mby = mbAddr / _layout.WidthInMbs;
        LoadSource();
        _pic.Slice[_mbAddr] = (short)_slice;
        _doneBlocks = 0;
        var forcePcm = _header.ForcePcm?.Invoke(_mbAddr) == true;
        var cost = 0;
        var mode = forcePcm ? MbChoice.Pcm : _header.IsIntra ? ChooseIntra(int.MaxValue, out cost) : ChooseInter(out cost);
        _counters.Satd += cost;
        if (mode == MbChoice.Skip)
        {
            records.SetSkip(_mbAddr);
            _counters.Skip++;
            return;
        }

        var w = records.PayloadOf(_writer);
        var start = w.Position;
        if (mode != MbChoice.Pcm)
        {
            WriteMacroblock(w, mode);
            if (w.Position - start <= H264PictureEncoder.MaxMbBits)
            {
                records.SetCoded(_mbAddr, _writer, start);
                if (mode != MbChoice.Inter)
                {
                    _counters.Intra++;
                }

                return;
            }

            w.Rewind(start);
        }

        WritePcm(records);
        _counters.Intra++;
        _counters.Pcm++;
    }

    /// <summary>Copies the macroblock's source samples, replicating the frame edge into the padding.</summary>
    private void LoadSource()
    {
        int width = _source.Width, height = _source.Height;
        var y = _source.GetPlane(0);
        for (var j = 0; j < 16; j++)
        {
            var row = Math.Min((_mby * 16) + j, height - 1) * width;
            for (var i = 0; i < 16; i++)
            {
                _srcY[(j * 16) + i] = y[row + Math.Min((_mbx * 16) + i, width - 1)];
            }
        }

        int cw = width / 2, ch = height / 2;
        for (var c = 0; c < 2; c++)
        {
            var plane = _source.GetPlane(c + 1);
            for (var j = 0; j < 8; j++)
            {
                var row = Math.Min((_mby * 8) + j, ch - 1) * cw;
                for (var i = 0; i < 8; i++)
                {
                    _srcC[(c * 64) + (j * 8) + i] = plane[row + Math.Min((_mbx * 8) + i, cw - 1)];
                }
            }
        }
    }

    /// <summary>Records an I_PCM macroblock's samples and makes its reconstruction the source samples.</summary>
    /// <param name="records">The records.</param>
    private void WritePcm(H264MacroblockRecords records)
    {
        var w = records.PayloadOf(_writer);
        w.AlignZero();
        var start = w.Position;
        w.WriteBytes(_srcY);
        w.WriteBytes(_srcC);
        records.SetPcm(_mbAddr, _writer, start);
        StoreSamples(_srcY, _srcC);
        SetIntraState(H264MbKind.Pcm);
        _pic.Qp[_mbAddr] = 0;
        _pic.TotalCoeff.AsSpan(_mbAddr * 16, 16).Fill(16);
        _pic.ChromaCoeff.AsSpan(_mbAddr * 8, 8).Fill(16);
    }

    /// <summary>Writes samples into the macroblock of the reconstruction.</summary>
    /// <param name="luma">256 luma samples.</param>
    /// <param name="chroma">64 Cb then 64 Cr samples.</param>
    private void StoreSamples(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> chroma)
    {
        var stride = _pic.Width;
        for (var j = 0; j < 16; j++)
        {
            luma.Slice(j * 16, 16).CopyTo(_pic.Luma.AsSpan((((_mby * 16) + j) * stride) + (_mbx * 16), 16));
        }

        var cs = stride / 2;
        for (var j = 0; j < 8; j++)
        {
            chroma.Slice(j * 8, 8).CopyTo(_pic.Cb.AsSpan((((_mby * 8) + j) * cs) + (_mbx * 8), 8));
            chroma.Slice(64 + (j * 8), 8).CopyTo(_pic.Cr.AsSpan((((_mby * 8) + j) * cs) + (_mbx * 8), 8));
        }
    }

    /// <summary>Records an intra macroblock's neighbour state: no motion, reference -1.</summary>
    /// <param name="kind">The kind.</param>
    private void SetIntraState(H264MbKind kind)
    {
        _pic.Kind[_mbAddr] = kind;
        for (var b = 0; b < 16; b++)
        {
            var index = _pic.Block(_mbx, _mby, b & 3, b >> 2);
            _pic.Mv[2 * index] = 0;
            _pic.Mv[(2 * index) + 1] = 0;
            _pic.RefIdx[index] = -1;
            _pic.RefId[index] = -1;
            _pic.IntraModes[(_mbAddr * 16) + b] = kind == H264MbKind.Intra4x4 ? _modes[b] : (sbyte)-1;
        }
    }

    /// <summary>Converts a lambda-weighted bit count into cost units.</summary>
    /// <param name="bits">The bits.</param>
    /// <returns>The cost.</returns>
    private int BitCost(int bits) => (int)(((long)_lambda * bits) + 128 >> 8);

    /// <summary>Returns whether a macroblock is available to the current one (6.4.8): inside the picture, already coded and in the same slice.</summary>
    /// <param name="mbx">Column.</param>
    /// <param name="mby">Row.</param>
    /// <returns>Whether it is available.</returns>
    private bool MbAvailable(int mbx, int mby)
    {
        if (mbx < 0 || mby < 0 || mbx >= _layout.WidthInMbs || mby >= _layout.HeightInMbs)
        {
            return false;
        }

        var addr = (mby * _layout.WidthInMbs) + mbx;
        return addr < _mbAddr && _pic.Slice[addr] == _slice;
    }

    /// <summary>What a macroblock is coded as.</summary>
    private enum MbChoice
    {
        /// <summary>P_Skip.</summary>
        Skip,

        /// <summary>P_L0_16x16 or P_8x8; the partition state holds the motion.</summary>
        Inter,

        /// <summary>Intra_16x16.</summary>
        Intra16x16,

        /// <summary>Intra_4x4.</summary>
        Intra4x4,

        /// <summary>I_PCM.</summary>
        Pcm,
    }
}
