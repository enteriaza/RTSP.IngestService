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
//  * TNS (4.6.9), long windows only: the spectrum from 1.4 kHz to the TNS limit (band 40 or max_sfb), weighted by the
//    inverse band RMS, gives an autocorrelation (Gaussian lag window) whose Levinson-Durbin prediction gain must lie
//    in [3, 16] (4.8 to 12 dB). One filter of order up to 4 is coded with 4-bit coefficients, upward, uncompressed; the
//    encoder filters with the dequantised coefficients and the decoder's reflection-to-LPC recursion, so the decoder's
//    all-pole filter inverts it exactly. Band energies in the filtered range are recomputed and the allowed noise is
//    scaled down with any energy reduction (never up), so the noise the decoder's filter shapes back along the
//    temporal envelope keeps at least the model's energy-to-noise ratio. The caller decides whether the frame's
//    temporal envelope is uneven enough to shape at all.
// Short windows are coded as eight groups of one window. Pulse and gain-control data are never used.

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

    /// <summary>The TNS filter order searched (AAC-LC allows up to 12 for long windows).</summary>
    private const int TnsMaxOrder = 4;

    /// <summary>The lowest frequency TNS filters.</summary>
    private const double TnsStartHz = 1400;

    /// <summary>The smallest prediction gain (about 4.8 dB) for which TNS is used.</summary>
    private const double TnsMinimumGain = 3.0;

    /// <summary>
    /// The largest prediction gain (12 dB) for which TNS is used: higher gains come from near-line spectra, such as a
    /// tone stopping abruptly, where the shaped noise overshoots the envelope instead of following it.
    /// </summary>
    private const double TnsMaximumGain = 16;

    /// <summary>The Gaussian lag-window parameter that smooths the TNS autocorrelation.</summary>
    private const double TnsLagWindow = 0.1;

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

    /// <summary>The quantised TNS reflection coefficients, 4-bit indices.</summary>
    private readonly int[] _tnsIndex = new int[TnsMaxOrder];

    /// <summary>The frame layout.</summary>
    private AacFrameLayout _layout;

    /// <summary>The TNS filter order, 0 when TNS is off.</summary>
    private int _tnsOrder;

    /// <summary>The TNS filter length in bands, counted down from the last scalefactor band.</summary>
    private int _tnsLength;

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

    /// <summary>Gets a value indicating whether the current frame uses a TNS filter.</summary>
    public bool HasTns => _tnsOrder > 0;

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

    /// <summary>
    /// Decides and applies temporal noise shaping for a long window (see the file header): the spectrum from
    /// <see cref="TnsStartHz"/> to the TNS limit is filtered with the quantised prediction-error filter, and the band
    /// energies and allowed noise in that range are updated. Clears TNS when not enabled, for short windows, or for a
    /// prediction gain outside [<see cref="TnsMinimumGain"/>, <see cref="TnsMaximumGain"/>].
    /// </summary>
    /// <param name="layout">The frame layout, before <see cref="Prepare"/>.</param>
    /// <param name="enabled">Whether the frame may use TNS.</param>
    public void ApplyTns(AacFrameLayout layout, bool enabled)
    {
        _tnsOrder = 0;
        var top = Math.Min(AacTables.TnsMaxBandsLong[3], layout.MaxSfb);
        var bottom = 0;
        while (bottom < top && layout.Offsets[bottom] * 24000.0 / AacTables.LongWindowLength < TnsStartHz)
        {
            bottom++;
        }

        if (!enabled || layout.Windows != 1 || top - bottom < 4)
        {
            return;
        }

        var start = layout.Offsets[bottom];
        var end = layout.Offsets[top];
        Span<double> weighted = stackalloc double[end - start];
        for (var b = bottom; b < top; b++)
        {
            var width = layout.Offsets[b + 1] - layout.Offsets[b];
            var scale = 1 / Math.Sqrt((Energy[b] / width) + 1e-9);
            for (var i = layout.Offsets[b]; i < layout.Offsets[b + 1]; i++)
            {
                weighted[i - start] = Spectrum[i] * scale;
            }
        }

        Span<double> correlation = stackalloc double[TnsMaxOrder + 1];
        for (var lag = 0; lag <= TnsMaxOrder; lag++)
        {
            var sum = 0.0;
            for (var i = lag; i < weighted.Length; i++)
            {
                sum += weighted[i] * weighted[i - lag];
            }

            correlation[lag] = sum * Math.Exp(-0.5 * Math.Pow(TnsLagWindow * lag, 2));
        }

        if (correlation[0] <= 0)
        {
            return;
        }

        Span<double> reflection = stackalloc double[TnsMaxOrder];
        var gain = correlation[0] / LevinsonDurbin(correlation, reflection);
        if (gain < TnsMinimumGain || gain > TnsMaximumGain)
        {
            return;
        }

        var order = 0;
        for (var i = 0; i < TnsMaxOrder; i++)
        {
            var index = (int)Math.Round(Math.Asin(reflection[i]) * (reflection[i] >= 0 ? 7.5 : 8.5) / (Math.PI / 2));
            _tnsIndex[i] = Math.Clamp(index, -8, 7);
            order = _tnsIndex[i] != 0 ? i + 1 : order;
        }

        if (order == 0)
        {
            return;
        }

        Span<float> parcor = stackalloc float[order];
        for (var i = 0; i < order; i++)
        {
            parcor[i] = (float)Math.Sin(_tnsIndex[i] / ((_tnsIndex[i] >= 0 ? 7.5 : 8.5) / (Math.PI / 2)));
        }

        Span<float> lpc = stackalloc float[order + 1];
        ToLpc(parcor, lpc);
        Span<float> history = stackalloc float[order];
        for (var i = start; i < end; i++)
        {
            var x = Spectrum[i];
            var residual = x;
            for (var j = 0; j < order; j++)
            {
                residual += lpc[j + 1] * history[j];
            }

            for (var j = order - 1; j > 0; j--)
            {
                history[j] = history[j - 1];
            }

            history[0] = x;
            Spectrum[i] = residual;
        }

        for (var b = bottom; b < top; b++)
        {
            var energy = 0f;
            for (var i = layout.Offsets[b]; i < layout.Offsets[b + 1]; i++)
            {
                energy += Spectrum[i] * Spectrum[i];
            }

            Threshold[b] = Energy[b] > 0 ? Threshold[b] * Math.Min(1, energy / Energy[b]) : Threshold[b];
            Energy[b] = energy;
        }

        _tnsOrder = order;
        _tnsLength = AacTables.LongBandOffsets[3].Length - 1 - bottom;
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
        writer.WriteBit(_tnsOrder > 0);
        if (_tnsOrder > 0)
        {
            writer.Write(1, 2);
            writer.WriteBit(true);
            writer.Write((uint)_tnsLength, 6);
            writer.Write((uint)_tnsOrder, 5);
            writer.WriteBit(false);
            writer.WriteBit(false);
            for (var i = 0; i < _tnsOrder; i++)
            {
                writer.Write((uint)_tnsIndex[i] & 15, 4);
            }
        }

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

    /// <summary>Solves the normal equations by the Levinson-Durbin recursion, with the decoder's sign convention.</summary>
    /// <param name="correlation">The autocorrelation, lags 0 to the order.</param>
    /// <param name="reflection">Receives the reflection coefficients.</param>
    /// <returns>The prediction error energy.</returns>
    private static double LevinsonDurbin(ReadOnlySpan<double> correlation, Span<double> reflection)
    {
        Span<double> lpc = stackalloc double[reflection.Length + 1];
        Span<double> previous = stackalloc double[reflection.Length + 1];
        lpc[0] = 1;
        var error = correlation[0];
        for (var m = 1; m <= reflection.Length; m++)
        {
            var sum = correlation[m];
            for (var i = 1; i < m; i++)
            {
                sum += lpc[i] * correlation[m - i];
            }

            var k = error > 0 ? Math.Clamp(-sum / error, -0.999, 0.999) : 0;
            reflection[m - 1] = k;
            lpc.CopyTo(previous);
            for (var i = 1; i < m; i++)
            {
                lpc[i] = previous[i] + (k * previous[m - i]);
            }

            lpc[m] = k;
            error *= 1 - (k * k);
        }

        return error;
    }

    /// <summary>Converts reflection coefficients to direct-form coefficients exactly as the decoder does, <c>lpc[0] = 1</c>.</summary>
    /// <param name="reflection">The dequantised reflection coefficients.</param>
    /// <param name="lpc">order + 1 outputs.</param>
    private static void ToLpc(ReadOnlySpan<float> reflection, Span<float> lpc)
    {
        Span<float> previous = stackalloc float[TnsMaxOrder + 1];
        lpc.Clear();
        lpc[0] = 1;
        for (var m = 1; m <= reflection.Length; m++)
        {
            lpc[..m].CopyTo(previous);
            for (var i = 1; i < m; i++)
            {
                lpc[i] = previous[i] + (reflection[m - 1] * previous[m - i]);
            }

            lpc[m] = reflection[m - 1];
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
