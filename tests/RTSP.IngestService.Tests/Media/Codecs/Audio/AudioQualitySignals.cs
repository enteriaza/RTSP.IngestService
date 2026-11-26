// Deterministic synthetic programme material for encoder quality tests, generated in code so that no third-party
// recordings are needed. Each generator is a pure function of its seed and returns 48 kHz stereo planes:
//  * speech: a glottal pulse train through three vowel formant resonators, with fricative noise and pauses, centred;
//  * tonal music: decaying piano-like notes with slightly inharmonic partials, melody and chords panned apart;
//  * complex music: bass, detuned saw chords, kick, snare and hi-hat with decorrelated ambience;
//  * transients: castanet-like noise bursts and glockenspiel strikes over a quiet tone, with known onsets;
//  * near-silence: digital silence, noise at -90 dBFS and a 1 kHz tone fading from -60 to -100 dBFS;
//  * stereo: hard-panned instruments, decorrelated ambience, a centred voice and an anti-phase (side-only) tone.

namespace RTSP.IngestService.Tests.Media.Codecs.Audio;

/// <summary>Synthetic test programmes.</summary>
internal static class AudioQualitySignals
{
    /// <summary>The sample rate.</summary>
    public const int Rate = 48000;

    /// <summary>The programme names, in the order of <see cref="Create"/>.</summary>
    public static readonly string[] Names = ["speech", "tonal", "complex", "transients", "near-silence", "stereo"];

    /// <summary>Creates a programme by name.</summary>
    /// <param name="name">One of <see cref="Names"/>.</param>
    /// <param name="length">The length in samples.</param>
    /// <returns>Left and right planes.</returns>
    public static float[][] Create(string name, int length) => name switch
    {
        "speech" => Duplicate(Speech(length, 1)),
        "tonal" => Tonal(length),
        "complex" => Complex(length, 3),
        "transients" => Transients(length, 4, out _),
        "near-silence" => Duplicate(NearSilence(length, 5)),
        "stereo" => Stereo(length, 6),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown programme."),
    };

    /// <summary>Speech-like material: voiced vowels, fricatives and pauses, peak 0.5.</summary>
    /// <param name="length">The length.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>The samples.</returns>
    public static float[] Speech(int length, int seed)
    {
        double[][] vowels = [[700, 1220, 2600], [270, 2290, 3010], [300, 870, 2240], [530, 1840, 2480], [640, 1190, 2390]];
        var random = new Random(seed);
        var output = new float[length];
        var phase = 0.0;
        double tilt1 = 0, tilt2 = 0;
        var position = 0;
        var segment = 0;
        while (position < length)
        {
            var duration = Math.Min(length - position, (int)(Rate * (0.12 + (0.18 * random.NextDouble()))));
            var kind = segment++ % 5 switch { 0 or 1 or 3 => 0, 2 => 1, _ => 2 };
            var vowel = vowels[random.Next(vowels.Length)];
            var f0 = 105 + (110 * random.NextDouble());
            var resonators = kind == 0
                ? vowel.Select((f, i) => new Resonator(f, 60 + (40 * i), Rate)).ToArray()
                : [new Resonator(4000 + (2000 * random.NextDouble()), 2000, Rate)];
            for (var n = 0; n < duration; n++)
            {
                var t = n / (double)Rate;
                var envelope = Math.Min(1, Math.Min(n, duration - n) / (0.02 * Rate));
                double source;
                if (kind == 0)
                {
                    phase += f0 * (1 + (0.08 * Math.Sin(2 * Math.PI * 3 * t))) / Rate;
                    source = 0;
                    if (phase >= 1)
                    {
                        phase -= 1;
                        source = 1;
                    }

                    tilt1 = source + (0.97 * tilt1);
                    tilt2 = tilt1 + (0.9 * tilt2);
                    source = tilt2 * 0.05;
                }
                else
                {
                    source = kind == 1 ? ((random.NextDouble() * 2) - 1) * 0.3 : 0;
                }

                var y = source;
                foreach (var resonator in resonators)
                {
                    y = resonator.Process(y);
                }

                output[position + n] = (float)(y * envelope);
            }

            position += duration;
        }

        return Normalise(output, 0.5);
    }

    /// <summary>Tonal music: piano-like notes in a melody (left-heavy) over chords (right-heavy), peak 0.7.</summary>
    /// <param name="length">The length.</param>
    /// <returns>Left and right planes.</returns>
    public static float[][] Tonal(int length)
    {
        double[] melody = [440, 523.25, 587.33, 659.25, 783.99, 659.25, 587.33, 523.25, 493.88, 440, 392, 440];
        double[][] chords = [[220, 261.63, 329.63], [174.61, 220, 261.63], [196, 246.94, 293.66], [164.81, 207.65, 246.94]];
        var lead = new float[length];
        var pad = new float[length];
        var note = (int)(0.375 * Rate);
        for (var start = 0; start < length; start += note)
        {
            AddNote(lead, start, melody[start / note % melody.Length], 0.5);
            if (start / note % 4 == 0)
            {
                foreach (var f in chords[start / (4 * note) % chords.Length])
                {
                    AddNote(pad, start, f, 0.25);
                }
            }
        }

        var left = new float[length];
        var right = new float[length];
        for (var n = 0; n < length; n++)
        {
            left[n] = (0.8f * lead[n]) + (0.35f * pad[n]);
            right[n] = (0.3f * lead[n]) + (0.85f * pad[n]);
        }

        var scale = 0.7 / Math.Max(AudioTestSupport.Peak(left), AudioTestSupport.Peak(right));
        return [Scale(left, scale), Scale(right, scale)];
    }

    /// <summary>A dense mix: bass, chords, drums and ambience, peak 0.8.</summary>
    /// <param name="length">The length.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>Left and right planes.</returns>
    public static float[][] Complex(int length, int seed)
    {
        var random = new Random(seed);
        var left = new float[length];
        var right = new float[length];
        double[] bassNotes = [55, 55, 65.41, 49];
        double[][] chordNotes = [[220, 277.18, 329.63], [261.63, 329.63, 392], [196, 246.94, 293.66], [246.94, 311.13, 369.99]];
        var beat = Rate / 2;
        double bassLow = 0, chordLow = 0, hatHigh = 0, hatPrevious = 0;
        for (var n = 0; n < length; n++)
        {
            var bar = n / (4 * beat);
            var inBeat = n % beat;
            var t = n / (double)Rate;
            var bass = Saw(bassNotes[bar % 4] * t);
            bassLow += 0.05 * (bass - bassLow);
            var chord = 0.0;
            foreach (var f in chordNotes[bar % 4])
            {
                chord += Saw(f * t) + Saw(f * 1.004 * t);
            }

            chordLow += 0.3 * (chord - chordLow);
            var kickTime = inBeat / (double)Rate;
            var kick = Math.Sin(2 * Math.PI * (50 * kickTime + (100 * (1 - Math.Exp(-kickTime * 30)) / 30))) * Math.Exp(-kickTime * 25);
            var snareTime = ((n + beat) % (2 * beat)) / (double)Rate;
            var noise = (random.NextDouble() * 2) - 1;
            var snare = ((0.7 * noise) + (0.3 * Math.Sin(2 * Math.PI * 200 * snareTime))) * Math.Exp(-snareTime * 30);
            var hatTime = (n % (beat / 2)) / (double)Rate;
            hatHigh = 0.6 * (hatHigh + noise - hatPrevious);
            hatPrevious = noise;
            var hat = hatHigh * Math.Exp(-hatTime * 120);
            var ambience1 = 0.01 * ((random.NextDouble() * 2) - 1);
            var ambience2 = 0.01 * ((random.NextDouble() * 2) - 1);
            left[n] = (float)((0.35 * bassLow) + (0.12 * chordLow) + (0.5 * kick) + (0.25 * snare) + (0.25 * hat) + ambience1);
            right[n] = (float)((0.35 * bassLow) + (0.08 * chordLow) + (0.5 * kick) + (0.3 * snare) + (0.12 * hat) + ambience2);
        }

        var scale = 0.8 / Math.Max(AudioTestSupport.Peak(left), AudioTestSupport.Peak(right));
        return [Scale(left, scale), Scale(right, scale)];
    }

    /// <summary>Castanet-like bursts and glockenspiel strikes over a -40 dBFS tone.</summary>
    /// <param name="length">The length.</param>
    /// <param name="seed">The seed.</param>
    /// <param name="onsets">The onset sample positions.</param>
    /// <returns>Left and right planes (identical apart from a small level difference).</returns>
    public static float[][] Transients(int length, int seed, out int[] onsets)
    {
        var random = new Random(seed);
        var signal = AudioTestSupport.Sine(length, Rate, 330, 0.01);
        var positions = new List<int>();
        var position = Rate / 10;
        var index = 0;
        while (position < length - (Rate / 10))
        {
            positions.Add(position);
            if (index++ % 3 == 2)
            {
                for (var n = 0; n < Rate / 2 && position + n < length; n++)
                {
                    var t = n / (double)Rate;
                    signal[position + n] += (float)(0.35 * Math.Exp(-t * 6) * (Math.Sin(2 * Math.PI * 2637 * t) + (0.4 * Math.Sin(2 * Math.PI * 7380 * t))));
                }
            }
            else
            {
                var band = new Resonator(2500 + (2500 * random.NextDouble()), 1500, Rate);
                for (var n = 0; n < Rate / 50 && position + n < length; n++)
                {
                    signal[position + n] += (float)(3 * band.Process((random.NextDouble() * 2) - 1) * Math.Exp(-n / (0.004 * Rate)));
                }
            }

            position += (int)(Rate * (0.18 + (0.12 * random.NextDouble())));
        }

        onsets = [.. positions];
        var normalised = Normalise(signal, 0.8);
        return [normalised, Scale(normalised, 0.9)];
    }

    /// <summary>Digital silence, -90 dBFS noise, then a 1 kHz tone fading from -60 to -100 dBFS, in thirds.</summary>
    /// <param name="length">The length.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>The samples.</returns>
    public static float[] NearSilence(int length, int seed)
    {
        var output = new float[length];
        var third = length / 3;
        var noise = AudioTestSupport.Noise(third, Math.Pow(10, -90 / 20.0) * Math.Sqrt(3), seed);
        noise.CopyTo(output, third);
        for (var n = 2 * third; n < length; n++)
        {
            var progress = (n - (2 * third)) / (double)(length - (2 * third));
            var amplitude = Math.Pow(10, (-60 - (40 * progress)) / 20) * Math.Sqrt(2);
            output[n] = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * n / Rate));
        }

        return output;
    }

    /// <summary>A wide stereo scene, peak 0.8.</summary>
    /// <param name="length">The length.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>Left and right planes.</returns>
    public static float[][] Stereo(int length, int seed)
    {
        var voice = Speech(length, seed);
        var leftInstrument = AudioTestSupport.Add(AudioTestSupport.Sine(length, Rate, 523.25, 0.15), AudioTestSupport.Sine(length, Rate, 1046.5, 0.05));
        var rightInstrument = AudioTestSupport.Add(AudioTestSupport.Sine(length, Rate, 311.13, 0.15), AudioTestSupport.Sine(length, Rate, 2489, 0.04));
        var side = AudioTestSupport.Sine(length, Rate, 1800, 0.05);
        var ambienceLeft = AudioTestSupport.Noise(length, 0.02, seed + 10);
        var ambienceRight = AudioTestSupport.Noise(length, 0.02, seed + 11);
        var left = new float[length];
        var right = new float[length];
        for (var n = 0; n < length; n++)
        {
            left[n] = (0.5f * voice[n]) + leftInstrument[n] + side[n] + ambienceLeft[n];
            right[n] = (0.5f * voice[n]) + rightInstrument[n] - side[n] + ambienceRight[n];
        }

        var scale = 0.8 / Math.Max(AudioTestSupport.Peak(left), AudioTestSupport.Peak(right));
        return [Scale(left, scale), Scale(right, scale)];
    }

    /// <summary>Adds a decaying piano-like note.</summary>
    /// <param name="target">The signal.</param>
    /// <param name="start">The start sample.</param>
    /// <param name="frequency">The fundamental.</param>
    /// <param name="amplitude">The amplitude.</param>
    private static void AddNote(float[] target, int start, double frequency, double amplitude)
    {
        var duration = Math.Min(target.Length - start, (int)(1.5 * Rate));
        for (var k = 1; k <= 10 && k * frequency < 16000; k++)
        {
            var partial = k * frequency * Math.Sqrt(1 + (0.0004 * k * k));
            var level = amplitude / Math.Pow(k, 1.2);
            var decay = 2.5 * (1 + (0.3 * k));
            for (var n = 0; n < duration; n++)
            {
                var t = n / (double)Rate;
                var attack = Math.Min(1, n / (0.002 * Rate));
                target[start + n] += (float)(level * attack * Math.Exp(-decay * t) * Math.Sin(2 * Math.PI * partial * t));
            }
        }
    }

    /// <summary>A naive sawtooth in [-1, 1].</summary>
    /// <param name="cycles">The phase in cycles.</param>
    /// <returns>The value.</returns>
    private static double Saw(double cycles) => (2 * (cycles - Math.Floor(cycles))) - 1;

    /// <summary>Scales a signal to a peak.</summary>
    /// <param name="signal">The signal, scaled in place.</param>
    /// <param name="peak">The peak.</param>
    /// <returns>The signal.</returns>
    private static float[] Normalise(float[] signal, double peak) => Scale(signal, peak / Math.Max(AudioTestSupport.Peak(signal), 1e-30));

    /// <summary>Scales a copy of a signal.</summary>
    /// <param name="signal">The signal.</param>
    /// <param name="scale">The factor.</param>
    /// <returns>The scaled copy.</returns>
    private static float[] Scale(float[] signal, double scale) => [.. signal.Select(x => (float)(x * scale))];

    /// <summary>Uses one plane for both channels.</summary>
    /// <param name="signal">The signal.</param>
    /// <returns>Two references to copies of the signal.</returns>
    private static float[][] Duplicate(float[] signal) => [signal, [.. signal]];

    /// <summary>A two-pole resonator normalised to roughly unit peak gain.</summary>
    private sealed class Resonator
    {
        /// <summary>The feedback coefficients.</summary>
        private readonly double _a1;

        /// <summary>The second feedback coefficient.</summary>
        private readonly double _a2;

        /// <summary>The input gain.</summary>
        private readonly double _gain;

        /// <summary>The previous outputs.</summary>
        private double _y1;

        /// <summary>The output before the previous one.</summary>
        private double _y2;

        /// <summary>Initializes a new instance of the <see cref="Resonator"/> class.</summary>
        /// <param name="frequency">The centre frequency.</param>
        /// <param name="bandwidth">The bandwidth.</param>
        /// <param name="rate">The sample rate.</param>
        public Resonator(double frequency, double bandwidth, int rate)
        {
            var r = Math.Exp(-Math.PI * bandwidth / rate);
            _a1 = 2 * r * Math.Cos(2 * Math.PI * frequency / rate);
            _a2 = -r * r;
            _gain = 1 - r;
        }

        /// <summary>Filters one sample.</summary>
        /// <param name="x">The input.</param>
        /// <returns>The output.</returns>
        public double Process(double x)
        {
            var y = (_gain * x) + (_a1 * _y1) + (_a2 * _y2);
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }
}
