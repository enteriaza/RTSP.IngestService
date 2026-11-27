// Picture-level rate control. Both modes are constrained VBR under a VBV (leaky bucket) model of the canonical
// FFmpeg settings: the encoder buffer drains at the peak rate while it holds bits, never runs dry into credit, and
// a picture is admissible only if it fits in the room left (so a decoder buffer of the same size never underflows).
// ConstantQuality keeps the base QP and only raises it as far as the bucket needs. AverageBitrate steers a P-picture
// quantiser step from a per-picture-type linear model, bits = k * complexity / qstep, against the bits a
// two-second window of the target owes. A picture that still overfills the bucket is re-coded at a QP raised by
// the logarithm of the overshoot, at most MaxAttempts times. All state is a handful of numbers; arithmetic is
// IEEE double with only +, -, * and /, so decisions are identical on every platform.

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>Per-picture rate-control diagnostics.</summary>
/// <param name="Index">The picture index since the encoder was created.</param>
/// <param name="IsIdr">Whether the picture is an IDR picture.</param>
/// <param name="Qp">The QP coded.</param>
/// <param name="Bytes">The access unit's size.</param>
/// <param name="Attempts">How many times the picture was coded (1 unless VBV forced a re-code).</param>
/// <param name="IntraMacroblocks">Intra macroblocks, I_PCM included.</param>
/// <param name="SkipMacroblocks">P_Skip macroblocks.</param>
/// <param name="PcmMacroblocks">I_PCM macroblocks.</param>
/// <param name="BufferFullnessBits">The VBV bucket after the picture.</param>
/// <param name="BufferSizeBits">The VBV bucket size.</param>
/// <param name="VbvOverflow">Whether the picture overfilled the bucket even at the highest QP.</param>
internal readonly record struct H264FrameStatistics(
    long Index,
    bool IsIdr,
    int Qp,
    int Bytes,
    int Attempts,
    int IntraMacroblocks,
    int SkipMacroblocks,
    int PcmMacroblocks,
    long BufferFullnessBits,
    long BufferSizeBits,
    bool VbvOverflow);

/// <summary>Chooses picture QPs and models the VBV buffer.</summary>
internal sealed class H264RateControl
{
    /// <summary>The most times one picture is coded.</summary>
    public const int MaxAttempts = 4;

    /// <summary>The fraction of the bucket the first picture may use, as libx264's default vbv-init of 0.9.</summary>
    private const double InitialRoom = 0.9;

    /// <summary>The averaging window of <see cref="H264RateControlMode.AverageBitrate"/>, in seconds.</summary>
    private const double WindowSeconds = 2;

    /// <summary>The most a P-picture QP moves from the previous P picture.</summary>
    private const int MaxQpStep = 3;

    /// <summary>2^(1/6): the quantiser-step ratio of one QP.</summary>
    private const double QpStepRatio = 1.122462048309373;

    /// <summary>The options.</summary>
    private readonly H264EncoderOptions _options;

    /// <summary>The bucket size in bits.</summary>
    private readonly double _bufferBits;

    /// <summary>The peak drain rate in bit/s.</summary>
    private readonly double _peakRate;

    /// <summary>The model coefficients k for I and P pictures; 0 until measured.</summary>
    private readonly double[] _k = new double[2];

    /// <summary>The bits held in the encoder-side bucket.</summary>
    private double _fullness;

    /// <summary>Bits the average target owes (positive) or has been overspent (negative).</summary>
    private double _debt;

    /// <summary>The size of the last IDR picture in bits.</summary>
    private double _idrBits;

    /// <summary>The last P-picture QP.</summary>
    private int _lastPQp;

    /// <summary>The complexity of the last picture of each type.</summary>
    private readonly long[] _complexity = new long[2];

    /// <summary>Initializes a new instance of the <see cref="H264RateControl"/> class.</summary>
    /// <param name="options">The validated options.</param>
    public H264RateControl(H264EncoderOptions options)
    {
        _options = options;
        _bufferBits = options.BufferBits;
        _peakRate = options.MaxBitrateKilobits * 1000.0;
        _fullness = _bufferBits * (1 - InitialRoom);
        _lastPQp = options.BaseQp;
    }

    /// <summary>Gets the bucket size in bits.</summary>
    public long BufferSizeBits => (long)_bufferBits;

    /// <summary>Gets the bits currently in the bucket.</summary>
    public long FullnessBits => (long)_fullness;

    /// <summary>Chooses the first QP to code a picture with.</summary>
    /// <param name="idr">Whether the picture is an IDR picture.</param>
    /// <param name="seconds">The picture's expected duration.</param>
    /// <returns>The QP.</returns>
    public int Choose(bool idr, double seconds)
    {
        var type = idr ? 0 : 1;
        var pQp = _options.BaseQp;
        if (_options.RateControl == H264RateControlMode.AverageBitrate && _k[1] > 0 && _complexity[1] > 0)
        {
            var perPicture = _options.TargetBitrateKilobits * 1000.0 * seconds;
            var target = Math.Clamp(perPicture + (_debt * seconds / WindowSeconds), perPicture / 4, perPicture * 4);
            pQp = QpForStep(_k[1] * _complexity[1] / target);
            pQp = Math.Clamp(pQp, _lastPQp - MaxQpStep, _lastPQp + MaxQpStep);
        }

        var qp = Math.Clamp(idr ? pQp - _options.IntraQpOffset : pQp, _options.MinQp, _options.MaxQp);

        // Keep the predicted picture within 80 % of the room the bucket has left.
        if (_k[type] > 0 && _complexity[type] > 0)
        {
            var room = (Limit(idr) - Drained(seconds)) * 0.8;
            while (qp < _options.MaxQp && Predict(type, qp) > room)
            {
                qp++;
            }
        }

        return qp;
    }

    /// <summary>Returns whether a coded picture fits in the bucket.</summary>
    /// <param name="bits">The picture's bits.</param>
    /// <param name="seconds">The interval since the previous picture.</param>
    /// <param name="idr">Whether it is an IDR picture.</param>
    /// <returns>Whether it fits.</returns>
    public bool Fits(long bits, double seconds, bool idr) => Drained(seconds) + bits <= Limit(idr);

    /// <summary>Returns the QP to re-code a picture that did not fit.</summary>
    /// <param name="qp">The QP it was coded with.</param>
    /// <param name="bits">Its bits.</param>
    /// <param name="seconds">The interval since the previous picture.</param>
    /// <param name="idr">Whether it is an IDR picture.</param>
    /// <returns>A higher QP, at most <see cref="H264EncoderOptions.MaxQp"/>.</returns>
    public int Retry(int qp, long bits, double seconds, bool idr)
    {
        var room = Math.Max(1, (Limit(idr) - Drained(seconds)) * 0.9);
        var ratio = bits / room;
        var step = 1;
        while (ratio > QpStepRatio && step < 51)
        {
            ratio /= QpStepRatio;
            step++;
        }

        return Math.Min(_options.MaxQp, qp + step);
    }

    /// <summary>Records a coded picture.</summary>
    /// <param name="idr">Whether it is an IDR picture.</param>
    /// <param name="qp">Its QP.</param>
    /// <param name="bits">Its bits.</param>
    /// <param name="complexity">Its summed mode-decision SATD.</param>
    /// <param name="seconds">The interval since the previous picture.</param>
    public void Update(bool idr, int qp, long bits, long complexity, double seconds)
    {
        _fullness = Drained(seconds);
        if (_options.RateControl == H264RateControlMode.AverageBitrate)
        {
            var window = _options.TargetBitrateKilobits * 1000.0 * WindowSeconds;
            _debt = Math.Clamp(_debt + (_options.TargetBitrateKilobits * 1000.0 * seconds), -window, window);
        }

        var type = idr ? 0 : 1;
        if (complexity > 0)
        {
            var k = bits * QStep(qp) / complexity;
            _k[type] = _k[type] == 0 ? k : (_k[type] + k) / 2;
            _complexity[type] = complexity;
        }

        _fullness = Math.Min(_bufferBits, _fullness + bits);
        if (_options.RateControl == H264RateControlMode.AverageBitrate)
        {
            var window = _options.TargetBitrateKilobits * 1000.0 * WindowSeconds;
            _debt = Math.Clamp(_debt - bits, -window, window);
        }

        if (idr)
        {
            _idrBits = bits;
        }
        else
        {
            _lastPQp = qp;
        }
    }

    /// <summary>Returns the fullness a picture may raise the bucket to: all of it for an IDR picture; for a P picture, less the last IDR picture's size (at most half), so the next IDR picture finds room.</summary>
    /// <param name="idr">Whether the picture is an IDR picture.</param>
    /// <returns>The limit in bits.</returns>
    private double Limit(bool idr) => idr ? _bufferBits : _bufferBits - Math.Min(_idrBits, _bufferBits / 2);

    /// <summary>Returns the bucket's fullness after draining for an interval, without changing it.</summary>
    /// <param name="seconds">The interval since the previous picture.</param>
    /// <returns>The drained fullness.</returns>
    private double Drained(double seconds) => Math.Max(0, _fullness - (_peakRate * seconds));

    /// <summary>Returns the quantiser step of a QP.</summary>
    /// <param name="qp">The QP.</param>
    /// <returns>The step.</returns>
    private static double QStep(int qp) => H264EncoderTables.QScale65536[qp] / 65536.0;

    /// <summary>Returns the lowest QP whose step is at least the given step.</summary>
    /// <param name="step">The step.</param>
    /// <returns>The QP, 0 to 51.</returns>
    private static int QpForStep(double step)
    {
        for (var qp = 0; qp < 51; qp++)
        {
            if (QStep(qp) >= step)
            {
                return qp;
            }
        }

        return 51;
    }

    /// <summary>Predicts a picture's bits at a QP from the model.</summary>
    /// <param name="type">0 for IDR, 1 for P.</param>
    /// <param name="qp">The QP.</param>
    /// <returns>The predicted bits.</returns>
    private double Predict(int type, int qp) => _k[type] * _complexity[type] / QStep(qp);
}
