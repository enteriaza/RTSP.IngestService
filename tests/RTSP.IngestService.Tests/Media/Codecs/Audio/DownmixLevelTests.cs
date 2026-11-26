// Level and clipping analysis of the 5.1-to-stereo downmix on deterministic synthetic programmes (no third-party
// material). Each programme is C, L, R, Ls, Rs, LFE planes at 48 kHz; the candidates differ only in the overall gain
// applied to the BS.775 matrix (L' = L + 0.707 C + 0.707 Ls; LFE dropped):
//  * normalised (shipped): 1 / (1 + sqrt 2), -7.66 dB, so no input within full scale can clip;
//  * FFmpeg levels: 1, 0 dB, as libswresample does for float output (the encoder or a final clip handles overs);
//  * power-normalised: 1 / sqrt 2, -3 dB, unity gain for uncorrelated equal-level channels;
//  * front-normalised: 1 / (1 + 0.707), -4.65 dB, so L and C together cannot clip (surrounds may).
// Clipping is a final hard clip to [-1, 1]; there is no limiter or AGC.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Processing;

namespace RTSP.IngestService.Tests.Media.Codecs.Audio;

/// <summary>Downmix level, consistency and clipping measurements for <see cref="StereoDownmixer"/>.</summary>
public sealed class DownmixLevelTests
{
    /// <summary>Four seconds at 48 kHz.</summary>
    private const int Length = 4 * AudioQualitySignals.Rate;

    /// <summary>-1 dBFS, a common true-peak ceiling for mastered material.</summary>
    private const double MasteredPeak = 0.891;

    /// <summary>The shipped gain, 1 / (1 + sqrt 2).</summary>
    private static readonly double Normalised = 1 / (1 + Math.Sqrt(2));

    /// <summary>The power-normalised gain, 1 / sqrt 2.</summary>
    private static readonly double Power = 1 / Math.Sqrt(2);

    /// <summary>The front-normalised gain, 1 / (1 + 1 / sqrt 2): the gain 3.0 (C L R) already gets.</summary>
    private static readonly double Front = 1 / (1 + Math.Sqrt(0.5));

    /// <summary>
    /// Measured peak (dBFS, unclipped), clipped samples and level relative to the stereo original (upmix only) per
    /// programme and candidate gain. The bounds bracket the measured values by 0.2 dB.
    /// </summary>
    public static TheoryData<string, string, double, int, double> Expected => new()
    {
        { "film", "normalised", -10.9, 0, double.NaN },
        { "film", "ffmpeg", -3.3, 0, double.NaN },
        { "film", "power", -6.3, 0, double.NaN },
        { "film", "front", -7.9, 0, double.NaN },
        { "upmix", "normalised", -4.8, 0, -3.5 },
        { "upmix", "ffmpeg", 2.9, 155, 4.1 },
        { "upmix", "power", -0.1, 0, 1.1 },
        { "upmix", "front", -1.7, 0, -0.5 },
        { "concert", "normalised", -7.1, 0, double.NaN },
        { "concert", "ffmpeg", 0.6, 1, double.NaN },
        { "concert", "power", -2.4, 0, double.NaN },
        { "concert", "front", -4.1, 0, double.NaN },
        { "coherent", "normalised", -1.0, 0, double.NaN },
        { "coherent", "ffmpeg", 6.7, 265600, double.NaN },
        { "coherent", "power", 3.6, 208000, double.NaN },
        { "coherent", "front", 2.0, 159360, double.NaN },
    };

    /// <summary>Each candidate gain gives the recorded peak, clipping and level on each programme.</summary>
    /// <param name="programme">The programme.</param>
    /// <param name="candidate">normalised, ffmpeg, power or front.</param>
    /// <param name="peakDb">The expected unclipped peak in dBFS.</param>
    /// <param name="clipped">The expected count of output samples beyond full scale.</param>
    /// <param name="versusStereoDb">The expected level relative to the stereo original, or NaN.</param>
    [Theory]
    [MemberData(nameof(Expected))]
    public void CandidateLevelsAreAsRecorded(string programme, string candidate, double peakDb, int clipped, double versusStereoDb)
    {
        var planes = Programme(programme, out var stereo);
        var gain = candidate switch
        {
            "normalised" => Normalised,
            "ffmpeg" => 1,
            "power" => Power,
            _ => Front,
        };

        var (left, right) = Mix(planes, gain);
        Assert.InRange(Decibels(Math.Max(AudioTestSupport.Peak(left), AudioTestSupport.Peak(right))), peakDb - 0.2, peakDb + 0.2);
        Assert.Equal(clipped, left.Concat(right).Count(x => Math.Abs(x) > 1));
        if (stereo is not null)
        {
            Assert.InRange(Decibels(Rms(left, right) / Rms(stereo[0], stereo[1])), versusStereoDb - 0.2, versusStereoDb + 0.2);
        }
    }

    /// <summary>
    /// The shipped matrix is the front-normalised candidate scaled by a further (1 + 1 / sqrt 2) / (1 + sqrt 2),
    /// -3.0 dB, and 3.0 already uses the front-normalised gain: the shipped 5.1 matrix is 3.0 dB quieter than 3.0.
    /// </summary>
    [Fact]
    public void ShippedSurroundIsThreeDecibelsBelowThreePointZero()
    {
        var threeZero = new StereoDownmixer([AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight]);
        var fivePointOne = new StereoDownmixer([AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight, AudioChannel.SurroundLeft, AudioChannel.SurroundRight, AudioChannel.LowFrequency]);
        Assert.Equal(Front, threeZero.LeftCoefficients[1], 1e-6);
        Assert.Equal(Normalised, fivePointOne.LeftCoefficients[1], 1e-6);
        Assert.InRange(Decibels(fivePointOne.LeftCoefficients[1] / threeZero.LeftCoefficients[1]), -3.05, -2.95);
    }

    /// <summary>Builds a programme.</summary>
    /// <param name="name">film, upmix, concert or coherent.</param>
    /// <param name="stereo">The stereo original for an upmix, otherwise <see langword="null"/>.</param>
    /// <returns>C, L, R, Ls, Rs, LFE planes.</returns>
    private static float[][] Programme(string name, out float[][]? stereo)
    {
        stereo = null;
        switch (name)
        {
            case "film":
                {
                    // Dialogue in the centre peaking at -3 dBFS, a music bed in L/R, decorrelated ambience, LFE rumble.
                    var music = AudioQualitySignals.Complex(Length, 12);
                    return
                    [
                        Scale(AudioQualitySignals.Speech(Length, 11), 0.7 / 0.5),
                    Scale(music[0], 0.5), Scale(music[1], 0.5),
                    AudioTestSupport.Noise(Length, 0.08, 13), AudioTestSupport.Noise(Length, 0.08, 14),
                    AudioTestSupport.Sine(Length, AudioQualitySignals.Rate, 40, 0.7),
                ];
                }

            case "upmix":
                {
                    // A -1 dBFS mastered stereo mix upmixed passively: phantom centre to C, delayed half-level copies to
                    // the surrounds; the stereo original is the level reference.
                    var music = AudioQualitySignals.Complex(Length, 21);
                    var scale = MasteredPeak / Math.Max(AudioTestSupport.Peak(music[0]), AudioTestSupport.Peak(music[1]));
                    var l = Scale(music[0], scale);
                    var r = Scale(music[1], scale);
                    stereo = [l, r];
                    return
                    [
                        [.. l.Zip(r, (a, b) => (float)(0.5 * Math.Sqrt(0.5) * (a + b)))],
                    l, r, Delay(Scale(l, 0.5), 720), Delay(Scale(r, 0.5), 720), new float[Length],
                ];
                }

            case "concert":
                {
                    // Mastered music in L/R, a lead vocal in C, crowd noise plus hall reflections in the surrounds.
                    var music = AudioQualitySignals.Tonal(Length);
                    var scale = MasteredPeak / Math.Max(AudioTestSupport.Peak(music[0]), AudioTestSupport.Peak(music[1]));
                    var l = Scale(music[0], scale);
                    var r = Scale(music[1], scale);
                    return
                    [
                        Scale(AudioQualitySignals.Speech(Length, 31), 0.6 / 0.5),
                    l, r,
                    AudioTestSupport.Add(AudioTestSupport.Noise(Length, 0.15, 32), Delay(Scale(l, 0.3), 1900)),
                    AudioTestSupport.Add(AudioTestSupport.Noise(Length, 0.15, 33), Delay(Scale(r, 0.3), 2300)),
                    new float[Length],
                ];
                }

            default:
                {
                    // The theoretical worst case: the same -1 dBFS tone in every main channel.
                    var tone = AudioTestSupport.Sine(Length, AudioQualitySignals.Rate, 440, MasteredPeak);
                    return [tone, tone, tone, tone, tone, new float[Length]];
                }
        }
    }

    /// <summary>Downmixes with the shipped matrix rescaled to an overall gain, then hard-clips nothing (raw output).</summary>
    /// <param name="planes">C, L, R, Ls, Rs, LFE.</param>
    /// <param name="gain">The overall gain applied to the unnormalised BS.775 matrix.</param>
    /// <returns>The raw left and right outputs.</returns>
    private static (float[] Left, float[] Right) Mix(float[][] planes, double gain)
    {
        AudioChannel[] layout = [AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight, AudioChannel.SurroundLeft, AudioChannel.SurroundRight, AudioChannel.LowFrequency];
        var downmixer = new StereoDownmixer(layout);
        var rescale = (float)(gain * (1 + Math.Sqrt(2)));
        using var frame = AudioTestSupport.Frame(layout, AudioQualitySignals.Rate, planes, 0, Length, 0);
        using var output = downmixer.Process(frame);
        return ([.. output.GetChannel(0).ToArray().Select(x => x * rescale)], [.. output.GetChannel(1).ToArray().Select(x => x * rescale)]);
    }

    /// <summary>The RMS of two planes together.</summary>
    /// <param name="left">The left plane.</param>
    /// <param name="right">The right plane.</param>
    /// <returns>The RMS.</returns>
    private static double Rms(float[] left, float[] right) => Math.Sqrt((Math.Pow(AudioTestSupport.Rms(left), 2) + Math.Pow(AudioTestSupport.Rms(right), 2)) / 2);

    /// <summary>Converts an amplitude ratio to decibels.</summary>
    /// <param name="ratio">The ratio.</param>
    /// <returns>The decibels.</returns>
    private static double Decibels(double ratio) => 20 * Math.Log10(ratio);

    /// <summary>Scales a copy.</summary>
    /// <param name="signal">The signal.</param>
    /// <param name="gain">The gain.</param>
    /// <returns>The copy.</returns>
    private static float[] Scale(float[] signal, double gain) => [.. signal.Select(x => (float)(x * gain))];

    /// <summary>Delays a signal, keeping its length.</summary>
    /// <param name="signal">The signal.</param>
    /// <param name="samples">The delay.</param>
    /// <returns>The delayed copy.</returns>
    private static float[] Delay(float[] signal, int samples) => [.. new float[samples].Concat(signal.Take(signal.Length - samples))];
}
