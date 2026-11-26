// Type-IV discrete cosine transform, Y[k] = scale * sum_{n=0}^{M-1} X[n] cos(pi / M (n + 1/2)(k + 1/2)), evaluated
// with an M/2-point complex FFT and pre- and post-twiddles of e^(-i pi (j + 1/8) / M). The DCT-IV matrix is symmetric
// and, scaled by 2/M, its own inverse; the forward and inverse MDCT are both a DCT-IV plus a fold or unfold.

namespace RTSP.IngestService.Media.Codecs.Common.Transforms;

/// <summary>A DCT-IV of a fixed size. Not thread-safe: an instance owns its scratch buffers.</summary>
internal sealed class DctIv
{
    /// <summary>The half-size FFT.</summary>
    private readonly ComplexFft _fft;

    /// <summary>Twiddle cos(pi (j + 1/8) / M).</summary>
    private readonly float[] _twiddleCos;

    /// <summary>Twiddle -sin(pi (j + 1/8) / M).</summary>
    private readonly float[] _twiddleSin;

    /// <summary>FFT real scratch.</summary>
    private readonly float[] _real;

    /// <summary>FFT imaginary scratch.</summary>
    private readonly float[] _imaginary;

    /// <summary>Initializes a new instance of the <see cref="DctIv"/> class.</summary>
    /// <param name="length">The transform length M, a power of two of at least 4.</param>
    public DctIv(int length)
    {
        if (length < 4 || !int.IsPow2(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The DCT-IV length must be a power of two of at least 4.");
        }

        Length = length;
        _fft = new ComplexFft(length / 2);
        _twiddleCos = new float[length / 2];
        _twiddleSin = new float[length / 2];
        for (var j = 0; j < length / 2; j++)
        {
            var angle = Math.PI * (j + 0.125) / length;
            _twiddleCos[j] = (float)Math.Cos(angle);
            _twiddleSin[j] = (float)-Math.Sin(angle);
        }

        _real = new float[length / 2];
        _imaginary = new float[length / 2];
    }

    /// <summary>Gets the transform length M.</summary>
    public int Length { get; }

    /// <summary>Transforms <see cref="Length"/> values. The input and output must not overlap.</summary>
    /// <param name="input">M input values.</param>
    /// <param name="output">M output values.</param>
    /// <param name="scale">The factor applied to every output value.</param>
    public void Transform(ReadOnlySpan<float> input, Span<float> output, float scale)
    {
        var m = Length;
        var half = m / 2;
        ArgumentOutOfRangeException.ThrowIfNotEqual(input.Length, m);
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, m);

        for (var j = 0; j < half; j++)
        {
            var re = input[2 * j];
            var im = input[m - 1 - (2 * j)];
            _real[j] = (re * _twiddleCos[j]) - (im * _twiddleSin[j]);
            _imaginary[j] = (re * _twiddleSin[j]) + (im * _twiddleCos[j]);
        }

        _fft.Forward(_real, _imaginary);

        for (var j = 0; j < half; j++)
        {
            var re = (_real[j] * _twiddleCos[j]) - (_imaginary[j] * _twiddleSin[j]);
            var im = (_real[j] * _twiddleSin[j]) + (_imaginary[j] * _twiddleCos[j]);
            output[2 * j] = re * scale;
            output[m - 1 - (2 * j)] = -im * scale;
        }
    }
}
