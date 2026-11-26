// One individual_channel_stream() of AAC-LC (ISO/IEC 14496-3, 4.4.2.7 and 4.6): ics_info, section data, scalefactors,
// pulse data, TNS data and the Huffman-coded spectral data, followed by inverse quantisation, scaling, perceptual
// noise substitution and TNS synthesis filtering. Written from the specification's syntax and decoding process. AAC
// Main prediction, LTP and SSR gain control are not part of AAC-LC and are rejected.

using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;

/// <summary>The parsed and reconstructed spectrum of one channel for one frame. Reused across frames.</summary>
internal sealed class AacChannelStream
{
    /// <summary>Band type: all coefficients zero.</summary>
    public const int ZeroBand = 0;

    /// <summary>Band type: perceptual noise substitution.</summary>
    public const int NoiseBand = 13;

    /// <summary>Band type: intensity stereo, out of phase.</summary>
    public const int IntensityBand2 = 14;

    /// <summary>Band type: intensity stereo, in phase.</summary>
    public const int IntensityBand = 15;

    /// <summary>Maximum scalefactor bands per window (32 kHz long windows have 51).</summary>
    public const int MaxBands = 64;

    /// <summary>Largest TNS filter order of AAC-LC, long and short windows.</summary>
    private const int MaxTnsOrderLong = 12;

    /// <summary>Largest TNS filter order of AAC-LC short windows.</summary>
    private const int MaxTnsOrderShort = 7;

    /// <summary>The sampling frequency index.</summary>
    private readonly int _frequencyIndex;

    /// <summary>Quantised coefficients.</summary>
    private readonly int[] _quantised = new int[AacTables.LongWindowLength];

    /// <summary>TNS filters per window: count.</summary>
    private readonly int[] _tnsFilters = new int[8];

    /// <summary>TNS filter length in bands, per window and filter (at most 3 filters on long windows).</summary>
    private readonly int[] _tnsLength = new int[8 * 4];

    /// <summary>TNS filter order per window and filter.</summary>
    private readonly int[] _tnsOrder = new int[8 * 4];

    /// <summary>TNS direction per window and filter (true = downward).</summary>
    private readonly bool[] _tnsDownward = new bool[8 * 4];

    /// <summary>Dequantised TNS reflection coefficients per window and filter.</summary>
    private readonly float[] _tnsCoefficients = new float[8 * 4 * 32];

    /// <summary>Pulse offsets and amplitudes.</summary>
    private readonly int[] _pulseOffset = new int[4];

    /// <summary>Pulse amplitudes.</summary>
    private readonly int[] _pulseAmplitude = new int[4];

    /// <summary>Initializes a new instance of the <see cref="AacChannelStream"/> class.</summary>
    /// <param name="frequencyIndex">The sampling frequency index, 0 to 12.</param>
    public AacChannelStream(int frequencyIndex)
    {
        _frequencyIndex = frequencyIndex;
    }

    /// <summary>Gets the window sequence.</summary>
    public AacWindowSequence WindowSequence { get; private set; }

    /// <summary>Gets the window shape (0 sine, 1 KBD).</summary>
    public int WindowShape { get; private set; }

    /// <summary>Gets the number of transmitted scalefactor bands.</summary>
    public int MaxSfb { get; private set; }

    /// <summary>Gets the number of window groups.</summary>
    public int GroupCount { get; private set; }

    /// <summary>Gets the windows per group.</summary>
    public int[] GroupLength { get; } = new int[8];

    /// <summary>Gets the band offsets of the current window length.</summary>
    public int[] BandOffsets { get; private set; } = [];

    /// <summary>Gets the band type per group and band (<c>group * MaxBands + band</c>).</summary>
    public byte[] BandType { get; } = new byte[8 * MaxBands];

    /// <summary>Gets the scalefactor, intensity position or noise energy per group and band.</summary>
    public int[] ScaleFactor { get; } = new int[8 * MaxBands];

    /// <summary>Gets the reconstructed spectrum: one window of 1024, or eight of 128.</summary>
    public float[] Spectrum { get; } = new float[AacTables.LongWindowLength];

    /// <summary>Gets a value indicating whether pulse data was present this frame.</summary>
    public bool HasPulses { get; private set; }

    /// <summary>Gets a value indicating whether any TNS filter was present this frame.</summary>
    public bool HasTns { get; private set; }

    /// <summary>Gets the number of windows: 8 for short windows, else 1.</summary>
    public int WindowCount => WindowSequence == AacWindowSequence.EightShort ? 8 : 1;

    /// <summary>Gets the coefficients per window.</summary>
    public int WindowLength => WindowSequence == AacWindowSequence.EightShort ? AacTables.ShortWindowLength : AacTables.LongWindowLength;

    /// <summary>Parses <c>ics_info()</c>.</summary>
    /// <param name="reader">The reader.</param>
    /// <exception cref="CodecException">Invalid or not AAC-LC.</exception>
    public void ReadInfo(ref BitstreamReader reader)
    {
        reader.Skip(1);
        WindowSequence = (AacWindowSequence)reader.Read(2);
        WindowShape = (int)reader.Read(1);
        if (WindowSequence == AacWindowSequence.EightShort)
        {
            BandOffsets = AacTables.ShortBandOffsets[_frequencyIndex];
            MaxSfb = (int)reader.Read(4);
            var grouping = reader.Read(7);
            GroupCount = 1;
            GroupLength[0] = 1;
            for (var w = 1; w < 8; w++)
            {
                if ((grouping & (1u << (7 - w))) != 0)
                {
                    GroupLength[GroupCount - 1]++;
                }
                else
                {
                    GroupLength[GroupCount++] = 1;
                }
            }
        }
        else
        {
            BandOffsets = AacTables.LongBandOffsets[_frequencyIndex];
            MaxSfb = (int)reader.Read(6);
            GroupCount = 1;
            GroupLength[0] = 1;
            if (reader.ReadBit())
            {
                CodecException.InvalidData("AAC-LC does not use prediction, but predictor_data_present is set.");
            }
        }

        if (MaxSfb > BandOffsets.Length - 1)
        {
            CodecException.InvalidData($"max_sfb {MaxSfb} exceeds the {BandOffsets.Length - 1} scalefactor bands of the window.");
        }
    }

    /// <summary>Copies <c>ics_info()</c> from the first channel of a common-window pair.</summary>
    /// <param name="other">The first channel.</param>
    public void CopyInfo(AacChannelStream other)
    {
        WindowSequence = other.WindowSequence;
        WindowShape = other.WindowShape;
        MaxSfb = other.MaxSfb;
        GroupCount = other.GroupCount;
        BandOffsets = other.BandOffsets;
        other.GroupLength.CopyTo(GroupLength, 0);
    }

    /// <summary>Parses the rest of <c>individual_channel_stream()</c> and reconstructs the quantised spectrum.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="commonWindow">Whether <c>ics_info()</c> was shared and already read.</param>
    /// <param name="allowIntensity">Whether intensity band types are allowed (second channel of a pair only).</param>
    /// <exception cref="CodecException">Invalid data.</exception>
    public void Read(ref BitstreamReader reader, bool commonWindow, bool allowIntensity)
    {
        var globalGain = (int)reader.Read(8);
        if (!commonWindow)
        {
            ReadInfo(ref reader);
        }

        ReadSections(ref reader, allowIntensity);
        ReadScaleFactors(ref reader, globalGain);
        HasPulses = reader.ReadBit();
        if (HasPulses)
        {
            ReadPulses(ref reader);
        }

        HasTns = reader.ReadBit();
        if (HasTns)
        {
            ReadTns(ref reader);
        }
        else
        {
            Array.Clear(_tnsFilters);
        }

        if (reader.ReadBit())
        {
            CodecException.InvalidData("AAC-LC does not use gain control, but gain_control_data_present is set.");
        }

        ReadSpectralData(ref reader);
        if (HasPulses)
        {
            ApplyPulses();
        }
    }

    /// <summary>Dequantises and scales the spectrum; noise bands are left zero for <see cref="FillNoise"/>.</summary>
    public void Dequantise()
    {
        var spectrum = Spectrum.AsSpan();
        spectrum.Clear();
        var windowLength = WindowLength;
        var window = 0;
        for (var g = 0; g < GroupCount; g++)
        {
            for (var band = 0; band < MaxSfb; band++)
            {
                var index = (g * MaxBands) + band;
                var type = BandType[index];
                if (type is ZeroBand or >= NoiseBand)
                {
                    continue;
                }

                var gain = AacTables.ScaleFactorGain[ScaleFactor[index]];
                for (var w = 0; w < GroupLength[g]; w++)
                {
                    var start = ((window + w) * windowLength) + BandOffsets[band];
                    var end = ((window + w) * windowLength) + BandOffsets[band + 1];
                    for (var k = start; k < end; k++)
                    {
                        var q = _quantised[k];
                        var magnitude = AacTables.InverseQuantisation[Math.Abs(q)] * gain;
                        spectrum[k] = q < 0 ? -magnitude : magnitude;
                    }
                }
            }

            window += GroupLength[g];
        }
    }

    /// <summary>
    /// Fills noise bands (4.6.13): random values scaled to energy 2^(noise / 2) per window. When
    /// <paramref name="correlated"/> is set for a band, the band of <paramref name="source"/> is reused (4.6.13.3).
    /// </summary>
    /// <param name="random">The noise generator state.</param>
    /// <param name="source">The first channel of a pair, or <see langword="null"/>.</param>
    /// <param name="correlated">Per group and band, whether to reuse <paramref name="source"/>'s noise.</param>
    /// <returns>The number of noise bands.</returns>
    public int FillNoise(ref uint random, AacChannelStream? source, ReadOnlySpan<bool> correlated)
    {
        var count = 0;
        var windowLength = WindowLength;
        var window = 0;
        for (var g = 0; g < GroupCount; g++)
        {
            for (var band = 0; band < MaxSfb; band++)
            {
                var index = (g * MaxBands) + band;
                if (BandType[index] != NoiseBand)
                {
                    continue;
                }

                count++;
                var reuse = source is not null && !correlated.IsEmpty && correlated[index];
                var target = Math.Pow(2, 0.25 * ScaleFactor[index]);
                for (var w = 0; w < GroupLength[g]; w++)
                {
                    var start = ((window + w) * windowLength) + BandOffsets[band];
                    var values = Spectrum.AsSpan(start, BandOffsets[band + 1] - BandOffsets[band]);
                    if (reuse)
                    {
                        source!.Spectrum.AsSpan(start, values.Length).CopyTo(values);
                    }
                    else
                    {
                        foreach (ref var value in values)
                        {
                            random = (random * 1664525u) + 1013904223u;
                            value = (int)random;
                        }
                    }

                    var energy = 0.0;
                    foreach (var value in values)
                    {
                        energy += (double)value * value;
                    }

                    var scale = energy > 0 ? (float)(target / Math.Sqrt(energy)) : 0f;
                    foreach (ref var value in values)
                    {
                        value *= scale;
                    }
                }
            }

            window += GroupLength[g];
        }

        return count;
    }

    /// <summary>Applies the TNS synthesis filters (4.6.9.3).</summary>
    public void ApplyTns()
    {
        if (!HasTns)
        {
            return;
        }

        var shortWindows = WindowSequence == AacWindowSequence.EightShort;
        var maxBands = Math.Min(shortWindows ? AacTables.TnsMaxBandsShort[_frequencyIndex] : AacTables.TnsMaxBandsLong[_frequencyIndex], MaxSfb);
        var bandCount = BandOffsets.Length - 1;
        Span<float> lpc = stackalloc float[MaxTnsOrderLong + 1];
        Span<float> state = stackalloc float[MaxTnsOrderLong];
        for (var w = 0; w < WindowCount; w++)
        {
            var top = bandCount;
            for (var f = 0; f < _tnsFilters[w]; f++)
            {
                var slot = (w * 4) + f;
                var bottom = Math.Max(top - _tnsLength[slot], 0);
                var order = _tnsOrder[slot];
                var start = BandOffsets[Math.Min(bottom, maxBands)];
                var end = BandOffsets[Math.Min(top, maxBands)];
                top = bottom;
                var size = end - start;
                if (order == 0 || size <= 0)
                {
                    continue;
                }

                ToLpc(_tnsCoefficients.AsSpan(slot * 32, order), lpc[..(order + 1)]);
                var spectrum = Spectrum.AsSpan(w * WindowLength, WindowLength);
                var position = _tnsDownward[slot] ? end - 1 : start;
                var step = _tnsDownward[slot] ? -1 : 1;
                state[..order].Clear();
                for (var m = 0; m < size; m++, position += step)
                {
                    var y = spectrum[position];
                    for (var j = 0; j < order; j++)
                    {
                        y -= state[j] * lpc[j + 1];
                    }

                    for (var j = order - 1; j > 0; j--)
                    {
                        state[j] = state[j - 1];
                    }

                    state[0] = y;
                    spectrum[position] = y;
                }
            }
        }
    }

    /// <summary>Converts reflection coefficients to direct-form LPC coefficients, <c>lpc[0] = 1</c>.</summary>
    /// <param name="reflection">The reflection coefficients.</param>
    /// <param name="lpc">order + 1 outputs.</param>
    private static void ToLpc(ReadOnlySpan<float> reflection, Span<float> lpc)
    {
        Span<float> previous = stackalloc float[MaxTnsOrderLong + 1];
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

    /// <summary>Parses <c>section_data()</c>.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="allowIntensity">Whether intensity band types are allowed.</param>
    private void ReadSections(ref BitstreamReader reader, bool allowIntensity)
    {
        var lengthBits = WindowSequence == AacWindowSequence.EightShort ? 3 : 5;
        var escape = (1u << lengthBits) - 1;
        for (var g = 0; g < GroupCount; g++)
        {
            var band = 0;
            while (band < MaxSfb)
            {
                var type = (byte)reader.Read(4);
                if (type == 12)
                {
                    CodecException.InvalidData("Section codebook 12 is reserved.");
                }

                if (type is IntensityBand or IntensityBand2 && !allowIntensity)
                {
                    CodecException.InvalidData("Intensity stereo is only valid in the second channel of a channel pair.");
                }

                var length = 0;
                uint increment;
                do
                {
                    increment = reader.Read(lengthBits);
                    length += (int)increment;
                }
                while (increment == escape);

                if (length == 0 || band + length > MaxSfb)
                {
                    CodecException.InvalidData("A section extends beyond max_sfb.");
                }

                BandType.AsSpan((g * MaxBands) + band, length).Fill(type);
                band += length;
            }
        }
    }

    /// <summary>Parses <c>scale_factor_data()</c> (4.6.2.3.2).</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="globalGain">The global gain.</param>
    private void ReadScaleFactors(ref BitstreamReader reader, int globalGain)
    {
        var scaleFactor = globalGain;
        var position = 0;
        var noise = globalGain - 90;
        var firstNoise = true;
        for (var g = 0; g < GroupCount; g++)
        {
            for (var band = 0; band < MaxSfb; band++)
            {
                var index = (g * MaxBands) + band;
                switch (BandType[index])
                {
                    case ZeroBand:
                        ScaleFactor[index] = 0;
                        break;
                    case IntensityBand or IntensityBand2:
                        position += AacTables.ScaleFactorCodebook.Decode(ref reader) - 60;
                        if (Math.Abs(position) > 255)
                        {
                            CodecException.InvalidData("An intensity stereo position is out of range.");
                        }

                        ScaleFactor[index] = position;
                        break;
                    case NoiseBand:
                        if (firstNoise)
                        {
                            noise += (int)reader.Read(9) - 256;
                            firstNoise = false;
                        }
                        else
                        {
                            noise += AacTables.ScaleFactorCodebook.Decode(ref reader) - 60;
                        }

                        if (Math.Abs(noise) > 400)
                        {
                            CodecException.InvalidData("A noise energy is out of range.");
                        }

                        ScaleFactor[index] = noise;
                        break;
                    default:
                        scaleFactor += AacTables.ScaleFactorCodebook.Decode(ref reader) - 60;
                        if (scaleFactor is < 0 or > 255)
                        {
                            CodecException.InvalidData("A scalefactor is outside 0 to 255.");
                        }

                        ScaleFactor[index] = scaleFactor;
                        break;
                }
            }
        }
    }

    /// <summary>Parses <c>pulse_data()</c>.</summary>
    /// <param name="reader">The reader.</param>
    private void ReadPulses(ref BitstreamReader reader)
    {
        if (WindowSequence == AacWindowSequence.EightShort)
        {
            CodecException.InvalidData("Pulse data is not allowed with short windows.");
        }

        var count = (int)reader.Read(2) + 1;
        var startBand = (int)reader.Read(6);
        if (startBand >= BandOffsets.Length - 1)
        {
            CodecException.InvalidData("pulse_start_sfb is beyond the scalefactor bands.");
        }

        var position = BandOffsets[startBand];
        Array.Clear(_pulseOffset);
        for (var i = 0; i < count; i++)
        {
            position += (int)reader.Read(5);
            if (position >= AacTables.LongWindowLength)
            {
                CodecException.InvalidData("A pulse is beyond the spectrum.");
            }

            _pulseOffset[i] = position;
            _pulseAmplitude[i] = (int)reader.Read(4);
        }

        for (var i = count; i < 4; i++)
        {
            _pulseAmplitude[i] = 0;
        }
    }

    /// <summary>Adds the pulses to the quantised spectrum.</summary>
    private void ApplyPulses()
    {
        for (var i = 0; i < 4; i++)
        {
            ref var q = ref _quantised[_pulseOffset[i]];
            q = q > 0 ? q + _pulseAmplitude[i] : q - _pulseAmplitude[i];
        }
    }

    /// <summary>Parses <c>tns_data()</c> and dequantises the coefficients (4.6.9.3).</summary>
    /// <param name="reader">The reader.</param>
    private void ReadTns(ref BitstreamReader reader)
    {
        var shortWindows = WindowSequence == AacWindowSequence.EightShort;
        var filterBits = shortWindows ? 1 : 2;
        var lengthBits = shortWindows ? 4 : 6;
        var orderBits = shortWindows ? 3 : 5;
        var maxOrder = shortWindows ? MaxTnsOrderShort : MaxTnsOrderLong;
        for (var w = 0; w < WindowCount; w++)
        {
            _tnsFilters[w] = (int)reader.Read(filterBits);
            if (_tnsFilters[w] == 0)
            {
                continue;
            }

            var resolution = (int)reader.Read(1) + 3;
            for (var f = 0; f < _tnsFilters[w]; f++)
            {
                var slot = (w * 4) + f;
                _tnsLength[slot] = (int)reader.Read(lengthBits);
                var order = (int)reader.Read(orderBits);
                if (order > maxOrder)
                {
                    CodecException.InvalidData($"TNS order {order} exceeds the AAC-LC maximum of {maxOrder}.");
                }

                _tnsOrder[slot] = order;
                if (order == 0)
                {
                    continue;
                }

                _tnsDownward[slot] = reader.ReadBit();
                var bits = resolution - (int)reader.Read(1);
                var positive = ((1 << (resolution - 1)) - 0.5) / (Math.PI / 2);
                var negative = ((1 << (resolution - 1)) + 0.5) / (Math.PI / 2);
                for (var i = 0; i < order; i++)
                {
                    var raw = (int)reader.Read(bits);
                    var value = (raw << (32 - bits)) >> (32 - bits);
                    _tnsCoefficients[(slot * 32) + i] = (float)Math.Sin(value / (value >= 0 ? positive : negative));
                }
            }
        }

        for (var w = WindowCount; w < 8; w++)
        {
            _tnsFilters[w] = 0;
        }
    }

    /// <summary>Parses <c>spectral_data()</c> into the quantised spectrum, interleaving grouped short windows.</summary>
    /// <param name="reader">The reader.</param>
    private void ReadSpectralData(ref BitstreamReader reader)
    {
        Array.Clear(_quantised);
        var windowLength = WindowLength;
        var window = 0;
        for (var g = 0; g < GroupCount; g++)
        {
            for (var band = 0; band < MaxSfb; band++)
            {
                int type = BandType[(g * MaxBands) + band];
                if (type is ZeroBand or >= NoiseBand)
                {
                    continue;
                }

                var codebook = AacTables.SpectralCodebooks[type]!;
                for (var w = 0; w < GroupLength[g]; w++)
                {
                    var start = ((window + w) * windowLength) + BandOffsets[band];
                    var end = ((window + w) * windowLength) + BandOffsets[band + 1];
                    var k = start;
                    while (k < end)
                    {
                        var symbol = codebook.Decode(ref reader);
                        if (type <= 4)
                        {
                            DecodeQuad(ref reader, type, symbol, _quantised.AsSpan(k, 4));
                            k += 4;
                        }
                        else
                        {
                            DecodePair(ref reader, type, symbol, _quantised.AsSpan(k, 2));
                            k += 2;
                        }
                    }
                }
            }

            window += GroupLength[g];
        }
    }

    /// <summary>Expands a codebook 1 to 4 symbol into four values.</summary>
    /// <param name="reader">The reader, for sign bits.</param>
    /// <param name="type">The codebook.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="values">The four values.</param>
    private static void DecodeQuad(ref BitstreamReader reader, int type, int symbol, Span<int> values)
    {
        values[0] = symbol / 27;
        values[1] = symbol / 9 % 3;
        values[2] = symbol / 3 % 3;
        values[3] = symbol % 3;
        if (type <= 2)
        {
            for (var i = 0; i < 4; i++)
            {
                values[i]--;
            }

            return;
        }

        ReadSigns(ref reader, values);
    }

    /// <summary>Expands a codebook 5 to 11 symbol into two values, with sign bits and escapes.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="type">The codebook.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="values">The two values.</param>
    private static void DecodePair(ref BitstreamReader reader, int type, int symbol, Span<int> values)
    {
        var modulus = type switch { 5 or 6 => 9, 7 or 8 => 8, 9 or 10 => 13, _ => 17 };
        values[0] = symbol / modulus;
        values[1] = symbol % modulus;
        if (type <= 6)
        {
            values[0] -= 4;
            values[1] -= 4;
            return;
        }

        var escape0 = type == 11 && values[0] == 16;
        var escape1 = type == 11 && values[1] == 16;
        ReadSigns(ref reader, values);
        if (escape0)
        {
            values[0] = ReadEscape(ref reader, values[0] < 0);
        }

        if (escape1)
        {
            values[1] = ReadEscape(ref reader, values[1] < 0);
        }
    }

    /// <summary>Reads one sign bit per non-zero value (1 = negative).</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="values">The values.</param>
    private static void ReadSigns(ref BitstreamReader reader, Span<int> values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] != 0 && reader.ReadBit())
            {
                values[i] = -values[i];
            }
        }
    }

    /// <summary>Reads an <c>escape_sequence</c>: N one bits, a zero, then N + 4 bits; value 2^(N+4) + bits.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="negative">Whether the value is negative.</param>
    /// <returns>The signed value, magnitude 16 to 8191.</returns>
    private static int ReadEscape(ref BitstreamReader reader, bool negative)
    {
        var prefix = 0;
        while (reader.ReadBit())
        {
            if (++prefix > 8)
            {
                CodecException.InvalidData("An escape sequence exceeds 8191.");
            }
        }

        var magnitude = (1 << (prefix + 4)) + (int)reader.Read(prefix + 4);
        return negative ? -magnitude : magnitude;
    }
}
