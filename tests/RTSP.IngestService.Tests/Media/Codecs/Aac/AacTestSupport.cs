// Test helpers for the managed AAC decoder: an ADTS reader that turns the fixture files into an AudioSpecificConfig
// plus raw access units, the committed reference PCM (16-bit little-endian, interleaved, decoded by FFmpeg's AAC
// decoder), and decoding a whole stream into interleaved float samples.

using System.Buffers.Binary;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;

namespace RTSP.IngestService.Tests.Media.Codecs.Aac;

/// <summary>An ADTS stream split into its configuration and raw access units.</summary>
/// <param name="AudioSpecificConfig">The AudioSpecificConfig built from the first header.</param>
/// <param name="AccessUnits">The raw data blocks.</param>
/// <param name="SampleRate">The sampling frequency.</param>
/// <param name="Channels">The channel count.</param>
internal sealed record AdtsStream(byte[] AudioSpecificConfig, IReadOnlyList<byte[]> AccessUnits, int SampleRate, int Channels);

/// <summary>AAC test helpers.</summary>
internal static class AacTestSupport
{
    /// <summary>Sampling frequencies by index.</summary>
    private static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>Gets the fixture directory.</summary>
    public static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Media", "Codecs", "Fixtures");

    /// <summary>Reads an ADTS fixture.</summary>
    /// <param name="name">The fixture name without extension.</param>
    /// <returns>The stream.</returns>
    public static AdtsStream ReadFixture(string name) => ParseAdts(File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".aac")));

    /// <summary>Reads a fixture's reference PCM as interleaved floats.</summary>
    /// <param name="name">The fixture name without extension.</param>
    /// <returns>The samples, scaled by 1/32768.</returns>
    public static float[] ReadReference(string name) => ToFloat(File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".s16")));

    /// <summary>Converts 16-bit little-endian PCM to floats.</summary>
    /// <param name="pcm">The PCM.</param>
    /// <returns>The samples, scaled by 1/32768.</returns>
    public static float[] ToFloat(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]) / 32768f;
        }

        return samples;
    }

    /// <summary>Splits ADTS frames (ISO/IEC 13818-7, 6.2) into raw data blocks.</summary>
    /// <param name="data">The ADTS stream.</param>
    /// <returns>The stream.</returns>
    public static AdtsStream ParseAdts(ReadOnlySpan<byte> data)
    {
        var units = new List<byte[]>();
        byte[]? config = null;
        var rate = 0;
        var channels = 0;
        var offset = 0;
        while (offset + 7 <= data.Length)
        {
            var header = data[offset..];
            Assert.True(header[0] == 0xFF && (header[1] & 0xF0) == 0xF0, "ADTS sync word expected.");
            var protectionAbsent = (header[1] & 1) == 1;
            var profile = header[2] >> 6;
            var frequency = (header[2] >> 2) & 0x0F;
            var channelConfiguration = ((header[2] & 1) << 2) | (header[3] >> 6);
            var length = ((header[3] & 3) << 11) | (header[4] << 3) | (header[5] >> 5);
            Assert.Equal(0, header[6] & 3);
            var headerLength = protectionAbsent ? 7 : 9;
            units.Add(header[headerLength..length].ToArray());
            if (config is null)
            {
                var objectType = profile + 1;
                config = [(byte)((objectType << 3) | (frequency >> 1)), (byte)(((frequency & 1) << 7) | (channelConfiguration << 3))];
                rate = SampleRates[frequency];
                channels = channelConfiguration == 7 ? 8 : channelConfiguration;
            }

            offset += length;
        }

        Assert.Equal(data.Length, offset);
        return new AdtsStream(config!, units, rate, channels);
    }

    /// <summary>Decodes every access unit, interleaving the output in bitstream channel order.</summary>
    /// <param name="decoder">The decoder.</param>
    /// <param name="stream">The stream.</param>
    /// <returns>The interleaved samples.</returns>
    public static float[] DecodeAll(AacDecoder decoder, AdtsStream stream)
    {
        var channels = decoder.Layout.Length;
        var output = new float[stream.AccessUnits.Count * AacDecoder.FrameLength * channels];
        for (var i = 0; i < stream.AccessUnits.Count; i++)
        {
            using var frame = decoder.Decode(new EncodedPacket(stream.AccessUnits[i], i * 1024L, new MediaTimeBase(1, stream.SampleRate)));
            for (var c = 0; c < channels; c++)
            {
                var plane = frame.GetChannel(c);
                for (var n = 0; n < plane.Length; n++)
                {
                    output[(((i * AacDecoder.FrameLength) + n) * channels) + c] = plane[n];
                }
            }
        }

        return output;
    }

    /// <summary>Largest absolute difference after rounding ours to 16 bits as the reference was.</summary>
    /// <param name="actual">Our samples, interleaved.</param>
    /// <param name="reference">The reference, interleaved.</param>
    /// <param name="channelMap">Reference channel per our channel, or <see langword="null"/> for identity.</param>
    /// <param name="channels">The channel count.</param>
    /// <returns>The largest difference, in 16-bit steps.</returns>
    public static int MaxDifference(float[] actual, float[] reference, int[]? channelMap, int channels)
    {
        Assert.Equal(reference.Length, actual.Length);
        var max = 0;
        for (var i = 0; i < actual.Length; i += channels)
        {
            for (var c = 0; c < channels; c++)
            {
                var ours = Math.Clamp((int)MathF.Round(actual[i + c] * 32768f), short.MinValue, short.MaxValue);
                var theirs = (int)MathF.Round(reference[i + (channelMap?[c] ?? c)] * 32768f);
                max = Math.Max(max, Math.Abs(ours - theirs));
            }
        }

        return max;
    }
}
