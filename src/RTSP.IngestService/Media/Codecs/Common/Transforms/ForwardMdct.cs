// Forward modified discrete cosine transform, the transpose of InverseMdct:
//   X[k] = scale * sum_{n=0}^{N-1} z[n] cos(2 pi / N (n + n0)(k + 1/2)),  n0 = (N/2 + 1) / 2,  0 <= k < N/2.
// The N windowed samples are folded into M = N/2 values u[m] = (m >= M/2 ? z[m - M/2] : -z[m + 3M/2]) - z[3M/2 - 1 - m]
// (the transpose of the inverse transform's unfold) and transformed with a DCT-IV. With scale 2 and an inverse scale of
// 2/N, windows satisfying the Princen-Bradley condition reconstruct the input by overlap-add.

namespace RTSP.IngestService.Media.Codecs.Common.Transforms;

/// <summary>A forward MDCT of a fixed size. Not thread-safe: an instance owns its scratch buffers.</summary>
internal sealed class ForwardMdct
{
    /// <summary>The DCT-IV of size N/2.</summary>
    private readonly DctIv _dctIv;

    /// <summary>Folded input scratch.</summary>
    private readonly float[] _folded;

    /// <summary>The output scale.</summary>
    private readonly float _scale;

    /// <summary>Initializes a new instance of the <see cref="ForwardMdct"/> class.</summary>
    /// <param name="length">The input length N, a power of two of at least 8.</param>
    /// <param name="scale">The factor applied to every coefficient.</param>
    public ForwardMdct(int length, double scale)
    {
        if (length < 8 || !int.IsPow2(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The MDCT length must be a power of two of at least 8.");
        }

        Length = length;
        _dctIv = new DctIv(length / 2);
        _folded = new float[length / 2];
        _scale = (float)scale;
    }

    /// <summary>Gets the input length N.</summary>
    public int Length { get; }

    /// <summary>Transforms N windowed samples into N/2 coefficients.</summary>
    /// <param name="samples">N windowed input samples.</param>
    /// <param name="coefficients">N/2 output coefficients.</param>
    public void Transform(ReadOnlySpan<float> samples, Span<float> coefficients)
    {
        var m = Length / 2;
        var half = m / 2;
        ArgumentOutOfRangeException.ThrowIfNotEqual(samples.Length, Length);
        for (var i = 0; i < half; i++)
        {
            _folded[i] = -samples[i + (3 * half)] - samples[(3 * half) - 1 - i];
        }

        for (var i = half; i < m; i++)
        {
            _folded[i] = samples[i - half] - samples[(3 * half) - 1 - i];
        }

        _dctIv.Transform(_folded, coefficients, _scale);
    }
}
