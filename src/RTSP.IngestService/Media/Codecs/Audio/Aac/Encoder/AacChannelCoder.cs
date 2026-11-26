// Quantisation and coding of one channel's spectrum into an individual_channel_stream (ISO/IEC 14496-3, 4.6.2):
//  * Quantiser: q = sign(x) floor((|x| / 2^((sf - 100) / 4))^(3/4) + 0.4054), the inverse of the decoder's
//    q^(4/3) 2^((sf - 100) / 4), with the customary 0.4054 rounding offset; |q| is capped at 8191.
//  * Scalefactor estimate per band from the allowed noise T: for step g = 2^((sf - 100) / 4) a uniform quantiser in the
//    companded domain gives noise of about n (4/27) g^(3/2) mean(sqrt|x|), so sf = 100 + (8/3) log2(T / (n (4/27)
//    mean(sqrt|x|))). The rate loop adds one global offset to every estimate; the result is raised where needed so
//    that no |q| exceeds 8191. The same offset scales the allowed noise by 2^(3/8 offset) when deciding whether a band
//    is inaudible and coded as zero, so spare bits at negative offsets also code bands that the model masks.
//  * Scalefactors are coded as differences, which must stay within +-60 between consecutive non-zero bands; a band that
//    breaks the limit is moved to the limit and requantised. The first non-zero band's scalefactor is global_gain.
//  * Sections: dynamic programming over the bands chooses, per band, the codebook that minimises spectral bits plus
//    section-header bits (4 + 5 long / 3 short). All-zero bands use the zero codebook.
// Short windows are coded as eight groups of one window. Pulse, TNS and gain-control data are never used.

using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;

/// <summary>Quantises and codes one channel. Not thread-safe: an instance belongs to one encoder.</summary>
internal sealed class AacChannelCoder
{
    /// <summary>Entries per window in the per-band arrays.</summary>
    public const int BandStride = 64;

    /// <summary>The largest quantised magnitude.</summary>
    private const int MaxMagnitude = 8191;

    /// <summary>The marker for a band coded as zero regardless of the offset.</summary>
    private const int Silent = int.MinValue;

    /// <summary>Codebooks 0 to 11.</summary>
    private const int BookCount = 12;

    /// <summary>2^(-3/16 (sf - 100)) by scalefactor: the companded reciprocal step.</summary>
    private static readonly float[] CompandedStep = CreateCompandedStep();

    /// <summary>|x|^(3/4) per coefficient.</summary>
    private readonly float[] _companded = new float[AacTables.LongWindowLength];

    /// <summary>The scalefactor estimate per band, or <see cref="Silent"/>.</summary>
    private readonly int[] _estimate = new int[8 * BandStride];

    /// <summary>The band energy over the allowed noise, per band.</summary>
    private readonly float[] _excess = new float[8 * BandStride];

    /// <summary>The smallest scalefactor that keeps |q| within 8191, per band.</summary>
    private readonly int[] _minimum = new int[8 * BandStride];

    /// <summary>Section dynamic-programming costs, per band and codebook.</summary>
    private readonly int[] _cost = new int[BandStride * BookCount];

    /// <summary>Section dynamic-programming back-pointers, per band and codebook.</summary>
    private readonly byte[] _from = new byte[BandStride * BookCount];

    /// <summary>The frame layout.</summary>
    private AacFrameLayout _layout;

    /// <summary>Gets the spectrum to code, in 16-bit scale, window-major for short windows.</summary>
    public float[] Spectrum { get; } = new float[AacTables.LongWindowLength];

    /// <summary>Gets the allowed noise per window and band.</summary>
    public float[] Threshold { get; } = new float[8 * BandStride];

    /// <summary>Gets the band energy per window and band.</summary>
    public float[] Energy { get; } = new float[8 * BandStride];

    /// <summary>Gets the quantised spectrum.</summary>
    public int[] Quantised { get; } = new int[AacTables.LongWindowLength];

    /// <summary>Gets the scalefactor per window and band.</summary>
    public int[] ScaleFactor { get; } = new int[8 * BandStride];

    /// <summary>Gets the codebook per window and band.</summary>
    public byte[] Book { get; } = new byte[8 * BandStride];

    /// <summary>Gets the global gain.</summary>
    public int GlobalGain { get; private set; }

    /// <summary>Prepares the per-band estimates from <see cref="Spectrum"/> and <see cref="Threshold"/>.</summary>
    /// <param name="layout">The frame layout.</param>
    public void Prepare(AacFrameLayout layout)
    {
        _layout = layout;
        for (var i = 0; i < Spectrum.Length; i++)
        {
            _companded[i] = MathF.Pow(Math.Abs(Spectrum[i]), 0.75f);
        }

        for (var w = 0; w < layout.Windows; w++)
        {
            for (var b = 0; b < layout.MaxSfb; b++)
            {
                var slot = (w * BandStride) + b;
                var start = (w * layout.WindowLength) + layout.Offsets[b];
                var end = (w * layout.WindowLength) + layout.Offsets[b + 1];
                if (Energy[slot] <= 0)
                {
                    _estimate[slot] = Silent;
                    continue;
                }

                _excess[slot] = Energy[slot] / Threshold[slot];

                var roots = 0.0;
                var largest = 0f;
                for (var i = start; i < end; i++)
                {
                    var magnitude = Math.Abs(Spectrum[i]);
                    roots += Math.Sqrt(magnitude);
                    largest = Math.Max(largest, magnitude);
                }

                var noise = (end - start) * (4.0 / 27) * (roots / (end - start));
                _estimate[slot] = (int)Math.Floor(100 + (8.0 / 3 * Math.Log2(Threshold[slot] / noise)));
                _minimum[slot] = Math.Max(0, (int)Math.Ceiling(100 + (4 * Math.Log2(largest)) - (16.0 / 3 * Math.Log2(MaxMagnitude))));
            }
        }
    }

    /// <summary>Quantises with every estimate moved by <paramref name="offset"/>, then chooses codebooks.</summary>
    /// <param name="offset">The global scalefactor offset; larger is coarser.</param>
    public void Quantise(int offset)
    {
        var layout = _layout;
        var previous = -1;
        var audible = MathF.Pow(2, 0.375f * offset);
        for (var w = 0; w < layout.Windows; w++)
        {
            for (var b = 0; b < layout.MaxSfb; b++)
            {
                var slot = (w * BandStride) + b;
                Book[slot] = 0;
                ScaleFactor[slot] = 0;
                if (_estimate[slot] == Silent || _excess[slot] <= audible)
                {
                    ClearBand(w, b);
                    continue;
                }

                var sf = Math.Clamp(_estimate[slot] + offset, _minimum[slot], 255);
                if (previous >= 0)
                {
                    sf = Math.Clamp(sf, previous - 60, previous + 60);
                }

                if (QuantiseBand(w, b, sf))
                {
                    ScaleFactor[slot] = sf;
                    Book[slot] = 1;
                    previous = sf;
                }
            }
        }

        GlobalGain = FirstScaleFactor() ?? 100;
        for (var w = 0; w < layout.Windows; w++)
        {
            ChooseSections(w);
        }
    }

    /// <summary>Writes the individual_channel_stream body that follows a common ics_info: global_gain to spectral_data.</summary>
    /// <param name="writer">The writer.</param>
    public void Write(ref BitstreamWriter writer)
    {
        var layout = _layout;
        writer.Write((uint)GlobalGain, 8);

        var lengthBits = layout.Windows == 8 ? 3 : 5;
        var escape = (1 << lengthBits) - 1;
        for (var w = 0; w < layout.Windows; w++)
        {
            var b = 0;
            while (b < layout.MaxSfb)
            {
                var book = Book[(w * BandStride) + b];
                var end = b + 1;
                while (end < layout.MaxSfb && Book[(w * BandStride) + end] == book)
                {
                    end++;
                }

                writer.Write(book, 4);
                var length = end - b;
                while (length >= escape)
                {
                    writer.Write((uint)escape, lengthBits);
                    length -= escape;
                }

                writer.Write((uint)length, lengthBits);
                b = end;
            }
        }

        var scaleFactor = GlobalGain;
        for (var w = 0; w < layout.Windows; w++)
        {
            for (var b = 0; b < layout.MaxSfb; b++)
            {
                var slot = (w * BandStride) + b;
                if (Book[slot] == 0)
                {
                    continue;
                }

                var index = ScaleFactor[slot] - scaleFactor + 60;
                writer.Write(AacHuffmanData.ScaleFactorCodes[index], AacHuffmanData.ScaleFactorLengths[index]);
                scaleFactor = ScaleFactor[slot];
            }
        }

        writer.WriteBit(false);
        writer.WriteBit(false);
        writer.WriteBit(false);

        for (var w = 0; w < layout.Windows; w++)
        {
            for (var b = 0; b < layout.MaxSfb; b++)
            {
                var book = Book[(w * BandStride) + b];
                if (book != 0)
                {
                    AacSpectralCoder.Write(ref writer, BandValues(w, b), book);
                }
            }
        }
    }

    /// <summary>Finds the scalefactor of the first non-zero band.</summary>
    /// <returns>The scalefactor, or <see langword="null"/> if every band is zero.</returns>
    private int? FirstScaleFactor()
    {
        for (var w = 0; w < _layout.Windows; w++)
        {
            for (var b = 0; b < _layout.MaxSfb; b++)
            {
                if (Book[(w * BandStride) + b] != 0)
                {
                    return ScaleFactor[(w * BandStride) + b];
                }
            }
        }

        return null;
    }

    /// <summary>Quantises one band.</summary>
    /// <param name="window">The window.</param>
    /// <param name="band">The band.</param>
    /// <param name="sf">The scalefactor.</param>
    /// <returns><see langword="true"/> if any value is non-zero.</returns>
    private bool QuantiseBand(int window, int band, int sf)
    {
        var start = (window * _layout.WindowLength) + _layout.Offsets[band];
        var end = (window * _layout.WindowLength) + _layout.Offsets[band + 1];
        var step = CompandedStep[sf];
        var any = false;
        for (var i = start; i < end; i++)
        {
            var q = Math.Min(MaxMagnitude, (int)((_companded[i] * step) + 0.4054f));
            Quantised[i] = Spectrum[i] < 0 ? -q : q;
            any |= q != 0;
        }

        return any;
    }

    /// <summary>Zeroes one band.</summary>
    /// <param name="window">The window.</param>
    /// <param name="band">The band.</param>
    private void ClearBand(int window, int band) => BandValues(window, band).Clear();

    /// <summary>Gets one band's quantised values.</summary>
    /// <param name="window">The window.</param>
    /// <param name="band">The band.</param>
    /// <returns>The values.</returns>
    private Span<int> BandValues(int window, int band) => Quantised.AsSpan(
        (window * _layout.WindowLength) + _layout.Offsets[band],
        _layout.Offsets[band + 1] - _layout.Offsets[band]);

    /// <summary>Chooses the codebook of every band of one window by dynamic programming over section costs.</summary>
    /// <param name="window">The window.</param>
    private void ChooseSections(int window)
    {
        var bands = _layout.MaxSfb;
        if (bands == 0)
        {
            return;
        }

        var header = 4 + (_layout.Windows == 8 ? 3 : 5);
        Span<int> bandCost = stackalloc int[BookCount];
        for (var b = 0; b < bands; b++)
        {
            var zero = Book[(window * BandStride) + b] == 0;
            var values = BandValues(window, b);
            var largest = 0;
            foreach (var value in values)
            {
                largest = Math.Max(largest, Math.Abs(value));
            }

            for (var book = 0; book < BookCount; book++)
            {
                bandCost[book] = zero
                    ? (book == 0 ? 0 : int.MaxValue)
                    : (book == 0 || AacSpectralCoder.GetLargestMagnitude(book) < largest ? int.MaxValue : AacSpectralCoder.Count(values, book));
            }

            var bestPrevious = 0;
            if (b > 0)
            {
                for (var book = 1; book < BookCount; book++)
                {
                    if (_cost[((b - 1) * BookCount) + book] < _cost[((b - 1) * BookCount) + bestPrevious])
                    {
                        bestPrevious = book;
                    }
                }
            }

            for (var book = 0; book < BookCount; book++)
            {
                var slot = (b * BookCount) + book;
                if (bandCost[book] == int.MaxValue)
                {
                    _cost[slot] = int.MaxValue;
                    continue;
                }

                if (b == 0)
                {
                    _cost[slot] = header + bandCost[book];
                    _from[slot] = (byte)book;
                    continue;
                }

                var stay = _cost[((b - 1) * BookCount) + book];
                var change = _cost[((b - 1) * BookCount) + bestPrevious];
                if (stay != int.MaxValue && stay <= change + header)
                {
                    _cost[slot] = stay + bandCost[book];
                    _from[slot] = (byte)book;
                }
                else
                {
                    _cost[slot] = change + header + bandCost[book];
                    _from[slot] = (byte)bestPrevious;
                }
            }
        }

        var choice = 0;
        for (var book = 1; book < BookCount; book++)
        {
            if (_cost[((bands - 1) * BookCount) + book] < _cost[((bands - 1) * BookCount) + choice])
            {
                choice = book;
            }
        }

        for (var b = bands - 1; b >= 0; b--)
        {
            Book[(window * BandStride) + b] = (byte)choice;
            choice = _from[(b * BookCount) + choice];
        }
    }

    /// <summary>Tabulates 2^(-3/16 (sf - 100)) for scalefactors 0 to 255.</summary>
    /// <returns>The table.</returns>
    private static float[] CreateCompandedStep()
    {
        var table = new float[256];
        for (var sf = 0; sf < table.Length; sf++)
        {
            table[sf] = (float)Math.Pow(2, -0.1875 * (sf - 100));
        }

        return table;
    }
}

/// <summary>The band structure of one frame.</summary>
/// <param name="Windows">1 for long sequences, 8 for eight short windows.</param>
/// <param name="WindowLength">Coefficients per window: 1024 or 128.</param>
/// <param name="Offsets">The scalefactor band offsets.</param>
/// <param name="MaxSfb">The number of coded bands.</param>
internal readonly record struct AacFrameLayout(int Windows, int WindowLength, int[] Offsets, int MaxSfb);
