// AAC synthesis filterbank (ISO/IEC 14496-3, 4.6.11): IMDCT of one long or eight short windows, windowing with the
// sine or Kaiser-Bessel-derived shape (alpha 4 long, 6 short; the previous frame's shape applies to the rising half),
// the four window sequences, and overlap-add with the previous frame's second half. Output is scaled to the nominal
// float range by dividing the specification's 16-bit-scale PCM by 32768.

using RTSP.IngestService.Media.Codecs.Common.Transforms;

namespace RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;

/// <summary>The synthesis filterbank of one decoder. Not thread-safe.</summary>
internal sealed class AacFilterbank
{
    /// <summary>The 2048-point IMDCT, scaled by 2/N and 1/32768.</summary>
    private readonly InverseMdct _long = new(2048, 2.0 / 2048 / 32768);

    /// <summary>The 256-point IMDCT, scaled by 2/N and 1/32768.</summary>
    private readonly InverseMdct _short = new(256, 2.0 / 256 / 32768);

    /// <summary>The windowed frame.</summary>
    private readonly float[] _frame = new float[2048];

    /// <summary>One short window's IMDCT output.</summary>
    private readonly float[] _shortFrame = new float[256];

    /// <summary>Synthesises one frame of one channel.</summary>
    /// <param name="spectrum">1024 coefficients (eight consecutive groups of 128 for short windows).</param>
    /// <param name="sequence">The window sequence.</param>
    /// <param name="shape">The window shape, 0 sine or 1 KBD.</param>
    /// <param name="previousShape">The previous frame's window shape.</param>
    /// <param name="overlap">The 1024-sample overlap, replaced with this frame's second half.</param>
    /// <param name="output">1024 output samples.</param>
    public void Synthesize(ReadOnlySpan<float> spectrum, AacWindowSequence sequence, int shape, int previousShape, Span<float> overlap, Span<float> output)
    {
        var frame = _frame.AsSpan();
        var longCurrent = AacWindows.Long[shape];
        var longPrevious = AacWindows.Long[previousShape];
        var shortCurrent = AacWindows.Short[shape];
        var shortPrevious = AacWindows.Short[previousShape];
        if (sequence == AacWindowSequence.EightShort)
        {
            frame.Clear();
            for (var w = 0; w < 8; w++)
            {
                _short.Transform(spectrum.Slice(w * 128, 128), _shortFrame);
                var rising = w == 0 ? shortPrevious : shortCurrent;
                var target = frame.Slice(448 + (w * 128), 256);
                for (var n = 0; n < 128; n++)
                {
                    target[n] += _shortFrame[n] * rising[n];
                    target[128 + n] += _shortFrame[128 + n] * shortCurrent[127 - n];
                }
            }
        }
        else
        {
            _long.Transform(spectrum[..1024], frame);
            if (sequence == AacWindowSequence.LongStop)
            {
                frame[..448].Clear();
                for (var n = 0; n < 128; n++)
                {
                    frame[448 + n] *= shortPrevious[n];
                }
            }
            else
            {
                for (var n = 0; n < 1024; n++)
                {
                    frame[n] *= longPrevious[n];
                }
            }

            if (sequence == AacWindowSequence.LongStart)
            {
                for (var n = 0; n < 128; n++)
                {
                    frame[1472 + n] *= shortCurrent[127 - n];
                }

                frame[1600..].Clear();
            }
            else
            {
                for (var n = 0; n < 1024; n++)
                {
                    frame[1024 + n] *= longCurrent[1023 - n];
                }
            }
        }

        for (var n = 0; n < 1024; n++)
        {
            output[n] = frame[n] + overlap[n];
        }

        frame[1024..].CopyTo(overlap);
    }
}
