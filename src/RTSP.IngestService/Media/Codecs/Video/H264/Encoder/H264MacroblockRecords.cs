// What each macroblock of a picture codes to, kept from macroblock coding until slice serialisation. A coded
// macroblock's macroblock_layer() bits and an I_PCM macroblock's byte-aligned samples sit back to back in a payload
// buffer, one per concurrent writer (the wavefront's workers; one for serial coding); the syntax that depends on a
// macroblock's position in its slice (mb_skip_run, the I_PCM mb_type and pcm_alignment_zero_bits) is written only when
// the slices are serialised, in raster order. A coded macroblock holds at most 3200 bits and an I_PCM one 384 bytes
// after at most 7 padding bits, so no payload outgrows the access-unit bound it is given. Each macroblock's record is
// written by the one writer that codes it.

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>The per-picture macroblock records and their raster serialiser.</summary>
internal sealed class H264MacroblockRecords
{
    /// <summary>The bytes of I_PCM samples: 256 luma, then 64 Cb and 64 Cr.</summary>
    public const int PcmBytes = 384;

    /// <summary>The kind of each macroblock's record.</summary>
    private readonly RecordKind[] _kind;

    /// <summary>The payload bit offset of each coded or I_PCM macroblock.</summary>
    private readonly int[] _start;

    /// <summary>The payload bit length of each coded macroblock.</summary>
    private readonly ushort[] _length;

    /// <summary>The payload of each writer.</summary>
    private readonly H264BitWriter[] _payloads;

    /// <summary>The writer of each coded or I_PCM macroblock; null with one writer.</summary>
    private readonly byte[]? _writer;

    /// <summary>Initializes a new instance of the <see cref="H264MacroblockRecords"/> class.</summary>
    /// <param name="macroblocks">The macroblocks per picture.</param>
    /// <param name="maxPayloadBytes">The bound of each payload buffer.</param>
    /// <param name="writers">The concurrent writers, 1 to 256.</param>
    public H264MacroblockRecords(int macroblocks, int maxPayloadBytes, int writers = 1)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPayloadBytes, int.MaxValue / 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(writers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(writers, 256);
        _kind = new RecordKind[macroblocks];
        _start = new int[macroblocks];
        _length = new ushort[macroblocks];
        _payloads = new H264BitWriter[writers];
        for (var i = 0; i < writers; i++)
        {
            _payloads[i] = new H264BitWriter(Math.Min(maxPayloadBytes, 65536), maxPayloadBytes);
        }

        _writer = writers > 1 ? new byte[macroblocks] : null;
    }

    /// <summary>What a macroblock's record holds.</summary>
    private enum RecordKind : byte
    {
        /// <summary>P_Skip: no payload, counted into mb_skip_run.</summary>
        Skip,

        /// <summary>macroblock_layer() bits.</summary>
        Coded,

        /// <summary>I_PCM samples, byte aligned in the payload.</summary>
        Pcm,
    }

    /// <summary>Gets the first writer's payload, the only one of serial coding.</summary>
    public H264BitWriter Payload => _payloads[0];

    /// <summary>Returns the bytes records hold at most: the payload bounds plus the per-macroblock metadata.</summary>
    /// <param name="macroblocks">The macroblocks per picture.</param>
    /// <param name="maxPayloadBytes">The bound of each payload buffer.</param>
    /// <param name="writers">The concurrent writers.</param>
    /// <returns>The bytes.</returns>
    public static long ByteCount(int macroblocks, int maxPayloadBytes, int writers = 1) => ((long)writers * maxPayloadBytes) + ((writers > 1 ? 8L : 7L) * macroblocks);

    /// <summary>Returns a writer's payload.</summary>
    /// <param name="writer">The writer.</param>
    /// <returns>The payload.</returns>
    public H264BitWriter PayloadOf(int writer) => _payloads[writer];

    /// <summary>Discards the payloads of the previous picture or attempt.</summary>
    public void Clear()
    {
        foreach (var payload in _payloads)
        {
            payload.Clear();
        }
    }

    /// <summary>Records a P_Skip macroblock.</summary>
    /// <param name="mbAddr">The macroblock address.</param>
    public void SetSkip(int mbAddr) => _kind[mbAddr] = RecordKind.Skip;

    /// <summary>Records a coded macroblock whose macroblock_layer() runs from <paramref name="start"/> to its writer's payload position.</summary>
    /// <param name="mbAddr">The macroblock address.</param>
    /// <param name="writer">The writer.</param>
    /// <param name="start">The payload bit offset of its first bit.</param>
    public void SetCoded(int mbAddr, int writer, long start)
    {
        _kind[mbAddr] = RecordKind.Coded;
        _start[mbAddr] = (int)start;
        _length[mbAddr] = (ushort)(_payloads[writer].Position - start);
        if (_writer is not null)
        {
            _writer[mbAddr] = (byte)writer;
        }
    }

    /// <summary>Records an I_PCM macroblock whose samples start at a byte-aligned offset of its writer's payload.</summary>
    /// <param name="mbAddr">The macroblock address.</param>
    /// <param name="writer">The writer.</param>
    /// <param name="start">The payload bit offset of the first sample.</param>
    public void SetPcm(int mbAddr, int writer, long start)
    {
        _kind[mbAddr] = RecordKind.Pcm;
        _start[mbAddr] = (int)start;
        if (_writer is not null)
        {
            _writer[mbAddr] = (byte)writer;
        }
    }

    /// <summary>Writes slice_data() (7.3.4) of a slice from its macroblocks' records, without the trailing bits.</summary>
    /// <param name="w">The slice writer, after the slice header.</param>
    /// <param name="firstMb">The slice's first macroblock.</param>
    /// <param name="endMb">The macroblock after its last.</param>
    /// <param name="intra">Whether the slice is an I slice (no mb_skip_run).</param>
    public void WriteSliceData(H264BitWriter w, int firstMb, int endMb, bool intra)
    {
        var first = _payloads[0].Written;
        var skipRun = 0;
        for (var mb = firstMb; mb < endMb; mb++)
        {
            var kind = _kind[mb];
            if (kind == RecordKind.Skip)
            {
                skipRun++;
                continue;
            }

            if (!intra)
            {
                w.WriteUe(skipRun);
                skipRun = 0;
            }

            var payload = _writer is null ? first : _payloads[_writer[mb]].Written;
            if (kind == RecordKind.Coded)
            {
                w.WriteBits(payload, _start[mb], _length[mb]);
                continue;
            }

            w.WriteUe(intra ? 25 : 30);
            w.AlignZero();
            w.WriteBytes(payload.Slice(_start[mb] >> 3, PcmBytes));
        }

        if (skipRun > 0)
        {
            w.WriteUe(skipRun);
        }
    }
}
