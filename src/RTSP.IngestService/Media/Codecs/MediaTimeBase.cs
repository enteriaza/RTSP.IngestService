// Rational time bases for codec timestamps. FLV timestamps are milliseconds (1/1000); audio sample positions are
// 1/sample-rate. Rescaling rounds to nearest, halves away from zero, in 128-bit arithmetic so it cannot overflow.

namespace RTSP.IngestService.Media.Codecs;

/// <summary>A time base: one tick is <see cref="Numerator"/>/<see cref="Denominator"/> seconds.</summary>
internal readonly record struct MediaTimeBase
{
    /// <summary>Milliseconds, the FLV/RTMP time base.</summary>
    public static readonly MediaTimeBase Milliseconds = new(1, 1000);

    /// <summary>Initializes a new instance of the <see cref="MediaTimeBase"/> struct.</summary>
    /// <param name="numerator">Seconds numerator, positive.</param>
    /// <param name="denominator">Seconds denominator, positive.</param>
    public MediaTimeBase(int numerator, int denominator)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);
        Numerator = numerator;
        Denominator = denominator;
    }

    /// <summary>Gets the numerator.</summary>
    public int Numerator { get; }

    /// <summary>Gets the denominator.</summary>
    public int Denominator { get; }

    /// <summary>Converts <paramref name="value"/> ticks of this time base to ticks of <paramref name="target"/>.</summary>
    /// <param name="value">The value.</param>
    /// <param name="target">The target time base.</param>
    /// <returns>The nearest tick count, halves rounded away from zero.</returns>
    /// <exception cref="OverflowException">The result does not fit in 64 bits.</exception>
    public long Rescale(long value, MediaTimeBase target)
    {
        var numerator = (Int128)value * Numerator * target.Denominator;
        var denominator = (Int128)Denominator * target.Numerator;
        var half = denominator / 2;
        var result = numerator >= 0 ? (numerator + half) / denominator : (numerator - half) / denominator;
        return checked((long)result);
    }
}
