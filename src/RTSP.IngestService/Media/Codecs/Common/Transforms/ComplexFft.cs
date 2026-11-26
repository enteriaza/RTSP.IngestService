// In-place iterative radix-2 decimation-in-time FFT over separate real and imaginary arrays, single precision with
// twiddles computed in double precision. Forward transform only: X[k] = sum x[n] e^(-2 pi i n k / N).

namespace RTSP.IngestService.Media.Codecs.Common.Transforms;

/// <summary>A forward complex FFT of a fixed power-of-two size. Instances are immutable and thread-safe.</summary>
internal sealed class ComplexFft
{
    /// <summary>Bit-reversal permutation.</summary>
    private readonly int[] _reverse;

    /// <summary>cos(2 pi k / N) for k &lt; N/2.</summary>
    private readonly float[] _cos;

    /// <summary>-sin(2 pi k / N) for k &lt; N/2.</summary>
    private readonly float[] _sin;

    /// <summary>Initializes a new instance of the <see cref="ComplexFft"/> class.</summary>
    /// <param name="size">The transform size, a power of two from 2 to 2^20.</param>
    public ComplexFft(int size)
    {
        if (size < 2 || size > 1 << 20 || !int.IsPow2(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The FFT size must be a power of two from 2 to 2^20.");
        }

        Size = size;
        var bits = int.Log2(size);
        _reverse = new int[size];
        for (var i = 0; i < size; i++)
        {
            _reverse[i] = (int)(ReverseBits((uint)i) >> (32 - bits));
        }

        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (var k = 0; k < size / 2; k++)
        {
            var angle = 2 * Math.PI * k / size;
            _cos[k] = (float)Math.Cos(angle);
            _sin[k] = (float)-Math.Sin(angle);
        }
    }

    /// <summary>Gets the transform size.</summary>
    public int Size { get; }

    /// <summary>Transforms in place.</summary>
    /// <param name="real">Real parts, <see cref="Size"/> values.</param>
    /// <param name="imaginary">Imaginary parts, <see cref="Size"/> values.</param>
    public void Forward(Span<float> real, Span<float> imaginary)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(real.Length, Size);
        ArgumentOutOfRangeException.ThrowIfNotEqual(imaginary.Length, Size);
        for (var i = 0; i < Size; i++)
        {
            var j = _reverse[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var half = 1; half < Size; half <<= 1)
        {
            var step = Size / (half * 2);
            for (var start = 0; start < Size; start += half * 2)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * step];
                    var wi = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = (real[b] * wr) - (imaginary[b] * wi);
                    var ti = (real[b] * wi) + (imaginary[b] * wr);
                    real[b] = real[a] - tr;
                    imaginary[b] = imaginary[a] - ti;
                    real[a] += tr;
                    imaginary[a] += ti;
                }
            }
        }
    }

    /// <summary>Reverses the bits of a 32-bit value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The reversed value.</returns>
    private static uint ReverseBits(uint value)
    {
        value = ((value >> 1) & 0x55555555u) | ((value & 0x55555555u) << 1);
        value = ((value >> 2) & 0x33333333u) | ((value & 0x33333333u) << 2);
        value = ((value >> 4) & 0x0F0F0F0Fu) | ((value & 0x0F0F0F0Fu) << 4);
        value = ((value >> 8) & 0x00FF00FFu) | ((value & 0x00FF00FFu) << 8);
        return (value >> 16) | (value << 16);
    }
}
