// The CABAC arithmetic decoding engine (ITU-T H.264 9.3.1.2 and 9.3.3.2), bit-serial as specified: a 9-bit offset
// register, one bit read per renormalisation step. Context state is a byte per ctxIdx: pStateIdx << 1 | valMPS.

using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Decodes CABAC bins from slice data.</summary>
internal ref struct H264CabacReader
{
    /// <summary>The context states.</summary>
    private readonly Span<byte> _states;

    /// <summary>The slice data reader.</summary>
    private BitstreamReader _reader;

    /// <summary>codIRange.</summary>
    private uint _range;

    /// <summary>codIOffset.</summary>
    private uint _offset;

    /// <summary>Initializes a new instance of the <see cref="H264CabacReader"/> struct and the decoding engine.</summary>
    /// <param name="reader">The reader, byte-aligned at the first bit of the arithmetic code.</param>
    /// <param name="states">The initialised context states.</param>
    /// <exception cref="CodecException">The arithmetic code is invalid or truncated.</exception>
    public H264CabacReader(BitstreamReader reader, Span<byte> states)
    {
        _reader = reader;
        _states = states;
        Start();
    }

    /// <summary>Gets the number of slice data bits consumed.</summary>
    public readonly int Position => _reader.Position;

    /// <summary>Initialises context states for a slice (9.3.1.1).</summary>
    /// <param name="states">The states, <see cref="H264CabacTables.ContextCount"/> entries.</param>
    /// <param name="model">0 for I slices, otherwise cabac_init_idc + 1.</param>
    /// <param name="sliceQp">SliceQPY.</param>
    public static void InitializeContexts(Span<byte> states, int model, int sliceQp)
    {
        var table = H264CabacTables.Initialization.Slice(model * H264CabacTables.ContextCount * 2, H264CabacTables.ContextCount * 2);
        var qp = Math.Clamp(sliceQp, 0, 51);
        for (var i = 0; i < H264CabacTables.ContextCount; i++)
        {
            int m = table[2 * i];
            int n = table[(2 * i) + 1];
            var pre = Math.Clamp(((m * qp) >> 4) + n, 1, 126);
            states[i] = pre <= 63 ? (byte)((63 - pre) << 1) : (byte)(((pre - 64) << 1) | 1);
        }
    }

    /// <summary>Decodes a bin with a context (9.3.3.2.1).</summary>
    /// <param name="ctxIdx">The context index.</param>
    /// <returns>The bin.</returns>
    /// <exception cref="CodecException">The slice data is truncated.</exception>
    public int Decision(int ctxIdx)
    {
        ref var state = ref _states[ctxIdx];
        var pState = state >> 1;
        var mps = state & 1;
        uint lps = H264CabacTables.RangeLps[(pState << 2) + (int)((_range >> 6) & 3)];
        _range -= lps;
        int bin;
        if (_offset >= _range)
        {
            bin = mps ^ 1;
            _offset -= _range;
            _range = lps;
            if (pState == 0)
            {
                mps ^= 1;
            }

            state = (byte)((H264CabacTables.NextStateLps[pState] << 1) | mps);
        }
        else
        {
            bin = mps;
            state = (byte)((H264CabacTables.NextStateMps[pState] << 1) | mps);
        }

        while (_range < 256)
        {
            _range <<= 1;
            _offset = (_offset << 1) | _reader.Read(1);
        }

        return bin;
    }

    /// <summary>Decodes a bypass bin (9.3.3.2.3).</summary>
    /// <returns>The bin.</returns>
    /// <exception cref="CodecException">The slice data is truncated.</exception>
    public int Bypass()
    {
        _offset = (_offset << 1) | _reader.Read(1);
        if (_offset >= _range)
        {
            _offset -= _range;
            return 1;
        }

        return 0;
    }

    /// <summary>Decodes a terminating bin (9.3.3.2.2); after a 1 the engine has consumed exactly the final bit.</summary>
    /// <returns>The bin.</returns>
    /// <exception cref="CodecException">The slice data is truncated.</exception>
    public int Terminate()
    {
        _range -= 2;
        if (_offset >= _range)
        {
            return 1;
        }

        while (_range < 256)
        {
            _range <<= 1;
            _offset = (_offset << 1) | _reader.Read(1);
        }

        return 0;
    }

    /// <summary>Reads I_PCM samples after a terminating bin of 1, then restarts the engine (9.3.1.2).</summary>
    /// <remarks>Alignment bits are skipped unchecked: some encoders (x264) leave a padding bit set after the CABAC flush.</remarks>
    /// <param name="samples">Receives 384 samples.</param>
    /// <exception cref="CodecException">The data is truncated.</exception>
    public void ReadPcm(Span<byte> samples)
    {
        ReadPcmSamples(ref _reader, samples, checkAlignment: false);
        Start();
    }

    /// <summary>Reads pcm_alignment_zero_bits and pcm samples.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="samples">Receives the samples.</param>
    /// <param name="checkAlignment">Whether the alignment bits must be zero.</param>
    /// <exception cref="CodecException">The alignment bits are not zero or the data is truncated.</exception>
    public static void ReadPcmSamples(ref BitstreamReader reader, Span<byte> samples, bool checkAlignment = true)
    {
        var pad = (8 - (reader.Position & 7)) & 7;
        if (reader.Read(pad) != 0 && checkAlignment)
        {
            CodecException.InvalidData("pcm_alignment_zero_bit is not zero.");
        }

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (byte)reader.Read(8);
        }
    }

    /// <summary>Initialises the decoding engine.</summary>
    /// <exception cref="CodecException">The arithmetic code is invalid or truncated.</exception>
    private void Start()
    {
        _range = 510;
        _offset = _reader.Read(9);
        if (_offset >= 510)
        {
            CodecException.InvalidData("The CABAC offset register is initialised to 510 or 511.");
        }
    }
}
