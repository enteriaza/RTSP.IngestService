// Window functions shared by the codec filterbanks and the resampler's filter design: the sine window, the
// Kaiser-Bessel-derived window (ISO/IEC 14496-3, 4.6.11.3.2), the Kaiser window and the zeroth-order modified Bessel
// function they are built from.

namespace RTSP.IngestService.Media.Codecs.Common.Transforms;

/// <summary>Window functions.</summary>
internal static class WindowFunctions
{
    /// <summary>The rising half of a sine window: sin(pi / N (n + 1/2)).</summary>
    /// <param name="length">The full window length N.</param>
    /// <returns>N/2 values.</returns>
    public static float[] SineRisingHalf(int length)
    {
        var window = new float[length / 2];
        for (var n = 0; n < window.Length; n++)
        {
            window[n] = (float)Math.Sin(Math.PI / length * (n + 0.5));
        }

        return window;
    }

    /// <summary>The rising half of a Kaiser-Bessel-derived window.</summary>
    /// <param name="length">The full window length N.</param>
    /// <param name="alpha">The kernel alpha.</param>
    /// <returns>N/2 values.</returns>
    public static float[] KbdRisingHalf(int length, double alpha)
    {
        var half = length / 2;
        var kernel = new double[half + 1];
        var quarter = length / 4.0;
        for (var j = 0; j <= half; j++)
        {
            var x = (j - quarter) / quarter;
            kernel[j] = BesselI0(Math.PI * alpha * Math.Sqrt(Math.Max(0, 1 - (x * x))));
        }

        var total = kernel.Sum();
        var window = new float[half];
        var running = 0.0;
        for (var n = 0; n < half; n++)
        {
            running += kernel[n];
            window[n] = (float)Math.Sqrt(running / total);
        }

        return window;
    }

    /// <summary>The Kaiser window I0(beta sqrt(1 - x^2)) / I0(beta) at a position normalised to [-1, 1].</summary>
    /// <param name="x">The position; values outside [-1, 1] give zero.</param>
    /// <param name="beta">The shape parameter.</param>
    /// <returns>The window value.</returns>
    public static double Kaiser(double x, double beta) =>
        Math.Abs(x) > 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - (x * x))) / BesselI0(beta);

    /// <summary>The zeroth-order modified Bessel function of the first kind, by its power series.</summary>
    /// <param name="x">The argument.</param>
    /// <returns>I0(x).</returns>
    public static double BesselI0(double x)
    {
        var sum = 1.0;
        var term = 1.0;
        var quarterSquare = x * x / 4;
        for (var k = 1; k < 200; k++)
        {
            term *= quarterSquare / (k * k);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }
}
