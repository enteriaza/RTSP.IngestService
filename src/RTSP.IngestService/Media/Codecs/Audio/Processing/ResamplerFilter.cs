// Polyphase filter design for rational sample-rate conversion by L/M (output rate / input rate reduced by their GCD).
// The prototype is a Kaiser-windowed sinc low-pass evaluated in continuous time, expressed in input-sample units:
//   g(t) = 2 fc sinc(2 fc t) w(t / W),  sinc(x) = sin(pi x) / (pi x),  w = Kaiser(beta).
// Design targets (Kaiser's formulas): passband edge 0.90 and stopband edge 1.00 of the lower Nyquist frequency of the
// two rates, cutoff fc midway, 100 dB stopband attenuation (beta = 0.1102 (A - 8.7)), and half length
// W = ceil((A - 7.95) / (2.285 * 2 pi * df) / 2) input samples, where df is the transition width in cycles per input
// sample. Phase p of L holds g(p / L - k) for taps k = -W + 1 .. W, normalised to unit sum so that every phase has
// exactly unity gain at DC. The filter is symmetric about the output instant, so it adds no group delay; it needs W
// input samples of look-ahead.

using System.Collections.Concurrent;
using RTSP.IngestService.Media.Codecs.Common.Transforms;

namespace RTSP.IngestService.Media.Codecs.Audio.Processing;

/// <summary>An immutable polyphase low-pass filter for one input/output rate pair. Shared between resamplers.</summary>
internal sealed class ResamplerFilter
{
    /// <summary>The stopband attenuation target in decibels.</summary>
    public const double StopbandAttenuationDb = 100;

    /// <summary>The passband edge as a fraction of the lower Nyquist frequency.</summary>
    public const double PassbandEdge = 0.90;

    /// <summary>The stopband edge as a fraction of the lower Nyquist frequency.</summary>
    public const double StopbandEdge = 1.00;

    /// <summary>The largest reduced interpolation or decimation factor, which bounds the table size.</summary>
    public const int MaxFactor = 1024;

    /// <summary>Filters by rate pair.</summary>
    private static readonly ConcurrentDictionary<(int Input, int Output), ResamplerFilter> Cache = new();

    /// <summary>The coefficients, <see cref="TapCount"/> per phase.</summary>
    private readonly float[] _coefficients;

    /// <summary>Initializes a new instance of the <see cref="ResamplerFilter"/> class.</summary>
    /// <param name="inputRate">The input rate in hertz.</param>
    /// <param name="outputRate">The output rate in hertz.</param>
    private ResamplerFilter(int inputRate, int outputRate)
    {
        var divisor = Gcd(inputRate, outputRate);
        Interpolation = outputRate / divisor;
        Decimation = inputRate / divisor;

        var scale = Math.Min(1.0, (double)Interpolation / Decimation);
        var transition = (StopbandEdge - PassbandEdge) * 0.5 * scale;
        var cutoff = (PassbandEdge + StopbandEdge) / 2 * 0.5 * scale;
        var beta = 0.1102 * (StopbandAttenuationDb - 8.7);
        HalfLength = (int)Math.Ceiling((StopbandAttenuationDb - 7.95) / (2.285 * 2 * Math.PI * transition) / 2);
        TapCount = 2 * HalfLength;

        _coefficients = new float[Interpolation * TapCount];
        var taps = new double[TapCount];
        for (var phase = 0; phase < Interpolation; phase++)
        {
            var fraction = (double)phase / Interpolation;
            var sum = 0.0;
            for (var i = 0; i < TapCount; i++)
            {
                var t = fraction - (i - HalfLength + 1);
                var x = 2 * cutoff * t;
                var sinc = x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                taps[i] = 2 * cutoff * sinc * WindowFunctions.Kaiser(t / HalfLength, beta);
                sum += taps[i];
            }

            for (var i = 0; i < TapCount; i++)
            {
                _coefficients[(phase * TapCount) + i] = (float)(taps[i] / sum);
            }
        }
    }

    /// <summary>Gets the interpolation factor L.</summary>
    public int Interpolation { get; }

    /// <summary>Gets the decimation factor M.</summary>
    public int Decimation { get; }

    /// <summary>Gets the half length W in input samples, which is also the look-ahead.</summary>
    public int HalfLength { get; }

    /// <summary>Gets the taps per phase, 2W.</summary>
    public int TapCount { get; }

    /// <summary>Gets the filter for a rate pair, designing it on first use.</summary>
    /// <param name="inputRate">The input rate in hertz.</param>
    /// <param name="outputRate">The output rate in hertz.</param>
    /// <returns>The shared filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ratio does not reduce to factors of at most <see cref="MaxFactor"/>.</exception>
    public static ResamplerFilter Get(int inputRate, int outputRate)
    {
        var divisor = Gcd(inputRate, outputRate);
        if (inputRate / divisor > MaxFactor || outputRate / divisor > MaxFactor)
        {
            throw new ArgumentOutOfRangeException(nameof(outputRate), outputRate, $"The rate ratio does not reduce to factors of at most {MaxFactor}.");
        }

        return Cache.GetOrAdd((inputRate, outputRate), static key => new ResamplerFilter(key.Input, key.Output));
    }

    /// <summary>Gets the taps of one phase, applied to input samples n0 - W + 1 .. n0 + W.</summary>
    /// <param name="phase">The phase, 0 to L - 1.</param>
    /// <returns><see cref="TapCount"/> coefficients.</returns>
    public ReadOnlySpan<float> GetPhase(int phase) => _coefficients.AsSpan(phase * TapCount, TapCount);

    /// <summary>The greatest common divisor.</summary>
    /// <param name="a">A positive integer.</param>
    /// <param name="b">A positive integer.</param>
    /// <returns>gcd(a, b).</returns>
    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
