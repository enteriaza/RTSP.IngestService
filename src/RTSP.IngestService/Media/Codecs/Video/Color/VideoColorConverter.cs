// Converts a VideoImage to the encoder's canonical input: 8-bit 4:2:0 BT.709 limited range (a VideoFrame tagged
// VideoColorInfo.Bt709). Nothing is guessed: unspecified colour fields need an explicit UnspecifiedColorPolicy and
// HDR transfers (PQ, HLG) an explicit HdrPolicy, neither of which has a default, so HDR can never pass as SDR.
//
// Per sample: Y'CbCr (any supported matrix, limited or full range, any bit depth; subsampled chroma is read at the
// co-located lower-resolution sample) or R'G'B' is normalised to non-linear R'G'B'. When the source already has
// BT.709 primaries and a BT.1886 transfer, only the matrix and range change and no linearisation happens. Otherwise
// the EOTF gives display light, PQ and HLG are tone mapped (below), primaries are converted in linear light and
// clipped to [0, 1], and the inverse BT.1886 EOTF re-encodes. BT.709 Y'CbCr is formed at 4:4:4, chroma is averaged
// over each 2x2 block (centre siting), and values are rounded to limited-range codes clipped to [1, 254].
//
// Tone mapping (HDR): display light in cd/m2 (PQ directly; HLG through the BT.2100 OOTF at the content peak) is
// mapped on maxRGB by the BT.2390 EETF from the content peak (VideoImage.PeakNits, else 1000 cd/m2) to the SDR
// peak, which is set at the BT.2408 HDR reference white of 203 cd/m2, then scaled to [0, 1] and converted from
// BT.2020 to BT.709 primaries. Scaling all three components by the ratio for maxRGB keeps hue and is monotonic in
// luminance. It is not swscale's "perceptual" intent the former FFmpeg path used, so the two do not match bit for bit.
//
// A BT.709 8-bit 4:2:0 limited-range source is copied unchanged.

namespace RTSP.IngestService.Media.Codecs.Video.Color;

/// <summary>What to assume for colour fields coded as unspecified (H.273 code 2).</summary>
internal enum UnspecifiedColorPolicy
{
    /// <summary>Reject the image.</summary>
    Reject = 1,

    /// <summary>Assume BT.709 for each unspecified field.</summary>
    AssumeBt709 = 2,

    /// <summary>Assume BT.601 (SMPTE 170M primaries, BT.601 transfer and matrix) for each unspecified field.</summary>
    AssumeBt601 = 3,
}

/// <summary>What to do with HDR (PQ or HLG) sources.</summary>
internal enum HdrPolicy
{
    /// <summary>Reject the image.</summary>
    Reject = 1,

    /// <summary>Tone map to SDR.</summary>
    ToneMap = 2,
}

/// <summary>Conversion policies; both are required and have no default.</summary>
internal sealed record VideoColorConversionOptions
{
    /// <summary>Gets the policy for unspecified colour fields.</summary>
    public required UnspecifiedColorPolicy Unspecified { get; init; }

    /// <summary>Gets the policy for HDR sources.</summary>
    public required HdrPolicy Hdr { get; init; }

    /// <summary>Validates the options.</summary>
    /// <exception cref="ArgumentException">A policy is not set.</exception>
    public void Validate()
    {
        if (!Enum.IsDefined(Unspecified) || Unspecified == 0)
        {
            throw new ArgumentException("VideoColorConversionOptions.Unspecified must be set explicitly.", nameof(Unspecified));
        }

        if (!Enum.IsDefined(Hdr) || Hdr == 0)
        {
            throw new ArgumentException("VideoColorConversionOptions.Hdr must be set explicitly.", nameof(Hdr));
        }
    }
}

/// <summary>Converts images to 8-bit 4:2:0 BT.709 limited range.</summary>
internal static class VideoColorConverter
{
    /// <summary>The SDR peak in HDR display light: the BT.2408 HDR reference white.</summary>
    public const double SdrPeakNits = 203;

    /// <summary>The content peak assumed when an HDR image does not state one.</summary>
    public const double DefaultHdrPeakNits = 1000;

    /// <summary>Resolves an image's colour description under the policies, rejecting what cannot be converted.</summary>
    /// <param name="image">The image.</param>
    /// <param name="options">The policies.</param>
    /// <returns>The colour description used for conversion.</returns>
    /// <exception cref="CodecException"><see cref="CodecErrorKind.Unsupported"/>: unspecified or HDR colour the policies reject, or an unsupported code point.</exception>
    public static VideoColorInfo Resolve(VideoImage image, VideoColorConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var color = image.Color;
        var rgb = image.Layout == VideoImageLayout.Rgb;
        if (color.Primaries == 2 || color.Transfer == 2 || (color.Matrix == 2 && !rgb))
        {
            if (options.Unspecified == UnspecifiedColorPolicy.Reject)
            {
                throw new CodecException(CodecErrorKind.Unsupported, "The image's colour is unspecified and the policy rejects it.");
            }

            var bt709 = options.Unspecified == UnspecifiedColorPolicy.AssumeBt709;
            color = new VideoColorInfo(
                color.Primaries == 2 ? (byte)(bt709 ? 1 : 6) : color.Primaries,
                color.Transfer == 2 ? (byte)(bt709 ? 1 : 6) : color.Transfer,
                color.Matrix == 2 && !rgb ? (byte)(bt709 ? 1 : 6) : color.Matrix,
                color.FullRange);
        }

        if (!ColorPrimaries.IsSupported(color.Primaries))
        {
            throw new CodecException(CodecErrorKind.Unsupported, $"Colour primaries {color.Primaries} are not supported.");
        }

        if (!TransferFunctions.IsSupported(color.Transfer))
        {
            throw new CodecException(CodecErrorKind.Unsupported, $"Transfer characteristics {color.Transfer} are not supported.");
        }

        if (rgb ? color.Matrix is not (0 or 2) : !ColorPrimaries.IsSupportedMatrix(color.Matrix))
        {
            throw new CodecException(CodecErrorKind.Unsupported, rgb ? "R'G'B' images require the identity matrix." : $"Matrix coefficients {color.Matrix} are not supported.");
        }

        if (TransferFunctions.IsHdr(color.Transfer) && options.Hdr == HdrPolicy.Reject)
        {
            throw new CodecException(CodecErrorKind.Unsupported, "The image is HDR and the policy rejects it.");
        }

        return color;
    }

    /// <summary>Converts an image into a frame of the same size tagged BT.709 limited range.</summary>
    /// <param name="source">The image.</param>
    /// <param name="destination">The destination frame: the same width and height, <see cref="VideoColorInfo.Bt709"/>.</param>
    /// <param name="options">The policies.</param>
    /// <param name="cancellationToken">Observed between row pairs.</param>
    /// <exception cref="CodecException"><see cref="CodecErrorKind.Unsupported"/>: see <see cref="Resolve"/>.</exception>
    /// <exception cref="ArgumentException">The destination does not match.</exception>
    public static void Convert(VideoImage source, VideoFrame destination, VideoColorConversionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var color = Resolve(source, options);
        if (destination.Width != source.Width || destination.Height != source.Height || destination.Color != VideoColorInfo.Bt709)
        {
            throw new ArgumentException("The destination must have the source's size and be tagged BT.709 limited range.", nameof(destination));
        }

        if (source.IsBytes && source.Layout == VideoImageLayout.Yuv420 && color == VideoColorInfo.Bt709)
        {
            Copy(source, destination);
            return;
        }

        new Pipeline(source, color).Run(destination, cancellationToken);
    }

    /// <summary>Copies a canonical 8-bit 4:2:0 source.</summary>
    /// <param name="source">The source.</param>
    /// <param name="destination">The destination.</param>
    private static void Copy(VideoImage source, VideoFrame destination)
    {
        for (var plane = 0; plane < 3; plane++)
        {
            var target = destination.GetPlane(plane);
            var width = destination.GetStride(plane);
            var rows = plane == 0 ? destination.Height : destination.ChromaHeight;
            for (var y = 0; y < rows; y++)
            {
                source.ByteRow(plane, y).CopyTo(target.Slice(y * width, width));
            }
        }
    }

    /// <summary>The per-image conversion state.</summary>
    private sealed class Pipeline
    {
        /// <summary>The source.</summary>
        private readonly VideoImage _source;

        /// <summary>The resolved colour.</summary>
        private readonly VideoColorInfo _color;

        /// <summary>The source luma coefficients (Y'CbCr input).</summary>
        private readonly double _kr, _kb;

        /// <summary>Whether light must be linearised (primaries or transfer differ from BT.709 / BT.1886, or HDR).</summary>
        private readonly bool _linear;

        /// <summary>The primaries conversion to BT.709, or null when the primaries already are.</summary>
        private readonly double[]? _gamut;

        /// <summary>The content peak for HDR.</summary>
        private readonly double _peak;

        /// <summary>The HLG system gamma.</summary>
        private readonly double _hlgGamma;

        /// <summary>The BT.2390 EETF parameters: PQ of the content peak, the normalised target, and the knee start.</summary>
        private readonly double _pqPeak, _maxLum, _knee;

        /// <summary>The PQ value of zero light.</summary>
        private readonly double _pqBlack;

        /// <summary>Initializes a new instance of the <see cref="Pipeline"/> class.</summary>
        /// <param name="source">The source.</param>
        /// <param name="color">The resolved colour.</param>
        public Pipeline(VideoImage source, VideoColorInfo color)
        {
            _source = source;
            _color = color;
            if (source.Layout != VideoImageLayout.Rgb)
            {
                (_kr, _kb) = ColorPrimaries.LumaCoefficients(color.Matrix);
            }

            var hdr = TransferFunctions.IsHdr(color.Transfer);
            _linear = hdr || color.Primaries != 1 || !TransferFunctions.IsBt1886(color.Transfer);
            _gamut = color.Primaries == 1 ? null : ColorPrimaries.Conversion(color.Primaries, 1);
            _peak = source.PeakNits ?? DefaultHdrPeakNits;
            _hlgGamma = TransferFunctions.HlgSystemGamma(_peak);
            _pqBlack = TransferFunctions.PqInverse(0);
            _pqPeak = TransferFunctions.PqInverse(_peak);
            _maxLum = (TransferFunctions.PqInverse(SdrPeakNits) - _pqBlack) / (_pqPeak - _pqBlack);
            _knee = (1.5 * _maxLum) - 0.5;
        }

        /// <summary>Converts into the destination.</summary>
        /// <param name="destination">The destination.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public void Run(VideoFrame destination, CancellationToken cancellationToken)
        {
            int width = destination.Width, height = destination.Height, cw = destination.ChromaWidth;
            var y = destination.GetPlane(0);
            var cb = destination.GetPlane(1);
            var cr = destination.GetPlane(2);
            var rows = new int[6][];
            for (var i = 0; i < 6; i++)
            {
                rows[i] = new int[Math.Max(_source.PlaneWidth(0), 1)];
            }

            var sumB = new double[cw];
            var sumR = new double[cw];
            for (var row = 0; row < height; row += 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Array.Clear(sumB);
                Array.Clear(sumR);
                for (var dy = 0; dy < 2; dy++)
                {
                    var sy = row + dy;
                    var source = rows.AsSpan(dy * 3, 3);
                    _source.ReadRow(0, sy, source[0]);
                    _source.ReadRow(1, sy >> _source.ChromaShiftY, source[1]);
                    _source.ReadRow(2, sy >> _source.ChromaShiftY, source[2]);
                    for (var x = 0; x < width; x++)
                    {
                        var cx = x >> _source.ChromaShiftX;
                        var (ly, lb, lr) = Pixel(source[0][x], source[1][cx], source[2][cx]);
                        y[(sy * width) + x] = Code(16 + (219 * ly));
                        sumB[x >> 1] += lb;
                        sumR[x >> 1] += lr;
                    }
                }

                for (var x = 0; x < cw; x++)
                {
                    cb[((row >> 1) * cw) + x] = Code(128 + (224 * sumB[x] / 4));
                    cr[((row >> 1) * cw) + x] = Code(128 + (224 * sumR[x] / 4));
                }
            }
        }

        /// <summary>Converts one source sample triplet to BT.709 Y', Cb, Cr (normalised; Y' in [0, 1], chroma in [-0.5, 0.5]).</summary>
        /// <param name="a">Y' or R'.</param>
        /// <param name="b">Cb or G'.</param>
        /// <param name="c">Cr or B'.</param>
        /// <returns>Y', Cb and Cr.</returns>
        private (double Y, double Cb, double Cr) Pixel(int a, int b, int c)
        {
            var depth = _source.BitDepth;
            var scale = (double)(1 << (depth - 8));
            var max = (double)((1 << depth) - 1);
            double r, g, bl;
            if (_source.Layout == VideoImageLayout.Rgb)
            {
                (r, g, bl) = _color.FullRange ? (a / max, b / max, c / max) : ((a - (16 * scale)) / (219 * scale), (b - (16 * scale)) / (219 * scale), (c - (16 * scale)) / (219 * scale));
            }
            else
            {
                double yn, cbn, crn;
                if (_color.FullRange)
                {
                    var mid = (double)(1 << (depth - 1));
                    (yn, cbn, crn) = (a / max, (b - mid) / max, (c - mid) / max);
                }
                else
                {
                    (yn, cbn, crn) = ((a - (16 * scale)) / (219 * scale), (b - (128 * scale)) / (224 * scale), (c - (128 * scale)) / (224 * scale));
                }

                r = yn + (2 * (1 - _kr) * crn);
                bl = yn + (2 * (1 - _kb) * cbn);
                g = (yn - (_kr * r) - (_kb * bl)) / (1 - _kr - _kb);
            }

            if (_linear)
            {
                (r, g, bl) = Linearize(r, g, bl);
                if (_gamut is { } m)
                {
                    (r, g, bl) = ((m[0] * r) + (m[1] * g) + (m[2] * bl), (m[3] * r) + (m[4] * g) + (m[5] * bl), (m[6] * r) + (m[7] * g) + (m[8] * bl));
                }

                r = TransferFunctions.Bt1886Inverse(r);
                g = TransferFunctions.Bt1886Inverse(g);
                bl = TransferFunctions.Bt1886Inverse(bl);
            }

            var luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * bl);
            return (luma, (bl - luma) / 1.8556, (r - luma) / 1.5748);
        }

        /// <summary>Returns relative linear light (1 = the SDR peak), tone mapped for HDR.</summary>
        /// <param name="r">R'.</param>
        /// <param name="g">G'.</param>
        /// <param name="b">B'.</param>
        /// <returns>Linear R, G, B.</returns>
        private (double R, double G, double B) Linearize(double r, double g, double b)
        {
            var transfer = _color.Transfer;
            if (!TransferFunctions.IsHdr(transfer))
            {
                return (TransferFunctions.SdrEotf(transfer, r), TransferFunctions.SdrEotf(transfer, g), TransferFunctions.SdrEotf(transfer, b));
            }

            double lr, lg, lb;
            if (transfer == 16)
            {
                (lr, lg, lb) = (TransferFunctions.PqEotf(r), TransferFunctions.PqEotf(g), TransferFunctions.PqEotf(b));
            }
            else
            {
                (lr, lg, lb) = (TransferFunctions.HlgInverseOetf(r), TransferFunctions.HlgInverseOetf(g), TransferFunctions.HlgInverseOetf(b));
                var ys = (0.2627 * lr) + (0.6780 * lg) + (0.0593 * lb);
                var gain = ys > 0 ? _peak * DeterministicMath.Pow(ys, _hlgGamma - 1) : 0;
                (lr, lg, lb) = (lr * gain, lg * gain, lb * gain);
            }

            var peak = Math.Max(lr, Math.Max(lg, lb));
            if (peak <= 0)
            {
                return (0, 0, 0);
            }

            var ratio = Eetf(peak) / peak / SdrPeakNits;
            return (lr * ratio, lg * ratio, lb * ratio);
        }

        /// <summary>The BT.2390 EETF with no black lift: content light to target light, both in cd/m2.</summary>
        /// <param name="nits">The content light.</param>
        /// <returns>The target light, at most <see cref="SdrPeakNits"/>.</returns>
        private double Eetf(double nits)
        {
            if (_peak <= SdrPeakNits)
            {
                return Math.Min(nits, SdrPeakNits);
            }

            var e1 = Math.Clamp((TransferFunctions.PqInverse(nits) - _pqBlack) / (_pqPeak - _pqBlack), 0, 1);
            double e2;
            if (e1 < _knee)
            {
                e2 = e1;
            }
            else
            {
                var t = (e1 - _knee) / (1 - _knee);
                double t2 = t * t, t3 = t2 * t;
                e2 = (((2 * t3) - (3 * t2) + 1) * _knee) + ((t3 - (2 * t2) + t) * (1 - _knee)) + (((-2 * t3) + (3 * t2)) * _maxLum);
            }

            return Math.Min(SdrPeakNits, TransferFunctions.PqEotf((e2 * (_pqPeak - _pqBlack)) + _pqBlack));
        }

        /// <summary>Rounds to a code value, clipped to [1, 254].</summary>
        /// <param name="v">The value.</param>
        /// <returns>The code.</returns>
        private static byte Code(double v) => (byte)Math.Clamp(Math.Floor(v + 0.5), 1, 254);
    }
}
