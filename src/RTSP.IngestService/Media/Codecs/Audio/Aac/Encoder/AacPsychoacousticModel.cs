// A band-energy masking model, simpler than the informative ISO/IEC 13818-7 / 14496-3 Annex psychoacoustic model 2
// (no FFT or unpredictability measure) but sufficient to shape quantisation noise:
//  1. energy E_b of each scalefactor band of the MDCT spectrum;
//  2. spreading: M_b = max(E_b, M_(b-1) * -15 dB, M_(b+1) * -25 dB) (upward masking is stronger than downward);
//  3. tonality from the band's spectral flatness (Johnston), selecting a signal-to-mask ratio between 6 dB for
//     noise-like and 24 dB for tonal bands, so that under bit pressure noise is coarsened before tones;
//  4. allowed noise T_b = max(M_b * 10^(-SMR_b / 10), ATH_b), where ATH_b is Terhardt's absolute threshold
//     of hearing, 3.64 f^-0.8 - 6.5 exp(-0.6 (f - 3.3)^2) + 0.001 f^4 dB (f in kHz), mapped so that 96 dB is a
//     full-scale sine and capped at 50 dB so audible high-frequency content within the coded bandwidth is kept.
// A band whose energy does not exceed T_b is inaudible and coded as zero.

namespace RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;

/// <summary>Computes allowed quantisation noise per scalefactor band. Stateless and thread-safe.</summary>
internal static class AacPsychoacousticModel
{
    /// <summary>The signal-to-mask ratio required for a fully tonal band, in decibels.</summary>
    public const double SignalToMaskDb = 24;

    /// <summary>The signal-to-mask ratio required for a noise-like band, in decibels.</summary>
    public const double NoiseSignalToMaskDb = 6;

    /// <summary>The spreading factor towards higher bands (-15 dB per band).</summary>
    private const float SpreadUp = 0.0316f;

    /// <summary>The spreading factor towards lower bands (-25 dB per band).</summary>
    private const float SpreadDown = 0.00316f;

    /// <summary>Absolute threshold energy per coefficient for each long-window band at 48 kHz.</summary>
    private static readonly float[] LongAbsoluteThreshold = CreateAbsoluteThreshold(AacTables.LongBandOffsets[3], 1024);

    /// <summary>Absolute threshold energy per coefficient for each short-window band at 48 kHz.</summary>
    private static readonly float[] ShortAbsoluteThreshold = CreateAbsoluteThreshold(AacTables.ShortBandOffsets[3], 128);

    /// <summary>Computes band energies and allowed noise for one window.</summary>
    /// <param name="spectrum">The window's coefficients in 16-bit scale.</param>
    /// <param name="offsets">The band offsets.</param>
    /// <param name="bands">The bands to analyse.</param>
    /// <param name="energy">The band energies.</param>
    /// <param name="threshold">The allowed noise energies.</param>
    public static void Analyse(ReadOnlySpan<float> spectrum, ReadOnlySpan<int> offsets, int bands, Span<float> energy, Span<float> threshold)
    {
        var shortWindow = spectrum.Length == AacTables.ShortWindowLength;
        var absolute = shortWindow ? ShortAbsoluteThreshold : LongAbsoluteThreshold;
        for (var b = 0; b < bands; b++)
        {
            var sum = 0f;
            foreach (var x in spectrum[offsets[b]..offsets[b + 1]])
            {
                sum += x * x;
            }

            energy[b] = sum;
        }

        var masking = 0f;
        for (var b = 0; b < bands; b++)
        {
            masking = Math.Max(energy[b], masking * SpreadUp);
            threshold[b] = masking;
        }

        masking = 0f;
        for (var b = bands - 1; b >= 0; b--)
        {
            masking = Math.Max(energy[b], masking * SpreadDown);
            var width = offsets[b + 1] - offsets[b];
            var ratio = (float)Math.Pow(10, -SignalToMask(spectrum[offsets[b]..offsets[b + 1]], energy[b]) / 10);
            threshold[b] = Math.Max(Math.Max(threshold[b], masking) * ratio, absolute[b] * width);
        }
    }

    /// <summary>
    /// The required signal-to-mask ratio of one band from its tonality, estimated by spectral flatness (Johnston):
    /// SFM = 10 log10(geometric mean / arithmetic mean of the coefficient energies); Gaussian noise gives about -5.5 dB.
    /// Tonality a = clamp((-SFM - 6) / 14, 0, 1) and SMR = NoiseSignalToMaskDb + a (SignalToMaskDb - NoiseSignalToMaskDb).
    /// </summary>
    /// <param name="band">The band's coefficients.</param>
    /// <param name="energy">The band energy.</param>
    /// <returns>The SMR in decibels.</returns>
    private static double SignalToMask(ReadOnlySpan<float> band, float energy)
    {
        if (energy <= 0)
        {
            return NoiseSignalToMaskDb;
        }

        var floor = energy / band.Length * 1e-6;
        var logSum = 0.0;
        foreach (var x in band)
        {
            logSum += Math.Log10(((double)x * x) + floor);
        }

        var flatness = 10 * ((logSum / band.Length) - Math.Log10(energy / band.Length));
        var tonality = Math.Clamp((-flatness - 6) / 14, 0, 1);
        return NoiseSignalToMaskDb + (tonality * (SignalToMaskDb - NoiseSignalToMaskDb));
    }

    /// <summary>Tabulates the absolute threshold per coefficient at each band's centre frequency.</summary>
    /// <param name="offsets">The band offsets.</param>
    /// <param name="windowLength">The coefficients per window.</param>
    /// <returns>The thresholds.</returns>
    private static float[] CreateAbsoluteThreshold(int[] offsets, int windowLength)
    {
        // A full-scale sine (amplitude 32768) gives MDCT coefficients of about 32768 * N / pi with N = 2 * windowLength.
        var fullScale = Math.Pow(32768.0 * 2 * windowLength / Math.PI, 2);
        var thresholds = new float[offsets.Length - 1];
        for (var b = 0; b < thresholds.Length; b++)
        {
            var kilohertz = Math.Max(0.02, (offsets[b] + offsets[b + 1]) / 2.0 / windowLength * 24.0);
            var decibels = (3.64 * Math.Pow(kilohertz, -0.8)) - (6.5 * Math.Exp(-0.6 * Math.Pow(kilohertz - 3.3, 2))) + (0.001 * Math.Pow(kilohertz, 4));
            thresholds[b] = (float)(fullScale * Math.Pow(10, (Math.Min(decibels, 50) - 96) / 10));
        }

        return thresholds;
    }
}
