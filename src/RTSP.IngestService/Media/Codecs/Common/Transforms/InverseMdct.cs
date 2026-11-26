// Inverse modified discrete cosine transform as defined for MPEG audio (ISO/IEC 14496-3, 4.6.11.3.1):
//   x[n] = scale * sum_{k=0}^{N/2-1} X[k] cos(2 pi / N (n + n0)(k + 1/2)),  n0 = (N/2 + 1) / 2,  0 <= n < N.
// It is computed as a DCT-IV of size M = N/2, then unfolded to N samples using the DCT-IV's odd symmetry.

namespace RTSP.IngestService.Media.Codecs.Common.Transforms;

/// <summary>An inverse MDCT of a fixed size. Not thread-safe: an instance owns its scratch buffers.</summary>
internal sealed class InverseMdct
{
    /// <summary>The DCT-IV of size N/2.</summary>
    private readonly DctIv _dctIv;

    /// <summary>DCT-IV output scratch.</summary>
    private readonly float[] _dct;

    /// <summary>The output scale.</summary>
    private readonly float _scale;

    /// <summary>Initializes a new instance of the <see cref="InverseMdct"/> class.</summary>
    /// <param name="length">The output length N, a power of two of at least 8.</param>
    /// <param name="scale">The factor applied to every output sample.</param>
    public InverseMdct(int length, double scale)
    {
        if (length < 8 || !int.IsPow2(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The IMDCT length must be a power of two of at least 8.");
        }

        Length = length;
        _dctIv = new DctIv(length / 2);
        _dct = new float[length / 2];
        _scale = (float)scale;
    }

    /// <summary>Gets the output length N.</summary>
    public int Length { get; }

    /// <summary>Transforms N/2 coefficients into N samples.</summary>
    /// <param name="coefficients">N/2 spectral coefficients.</param>
    /// <param name="output">N output samples.</param>
    public void Transform(ReadOnlySpan<float> coefficients, Span<float> output)
    {
        var quarter = Length / 4;
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, Length);
        _dctIv.Transform(coefficients, _dct, _scale);

        for (var n = 0; n < quarter; n++)
        {
            output[n] = _dct[n + quarter];
        }

        for (var n = quarter; n < 3 * quarter; n++)
        {
            output[n] = -_dct[(3 * quarter) - 1 - n];
        }

        for (var n = 3 * quarter; n < Length; n++)
        {
            output[n] = -_dct[n - (3 * quarter)];
        }
    }
}
