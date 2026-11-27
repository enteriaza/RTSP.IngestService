// Deterministic synthetic video for the managed H.264 encoder tests, and helpers that run the encoder and collect
// its access units together with its own reconstruction. Every pattern is a pure function of (pattern, size, frame
// index), BT.709 limited range, so expected results never depend on files or on the platform.

using System.Diagnostics;
using System.Globalization;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Video.H264;
using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>A synthetic test pattern.</summary>
public enum SyntheticPattern
{
    /// <summary>A uniform mid-grey picture.</summary>
    Flat,

    /// <summary>Eight vertical colour bars.</summary>
    Bars,

    /// <summary>A horizontal luma ramp and vertical chroma ramps.</summary>
    Ramp,

    /// <summary>A smooth diagonal gradient that drifts slowly.</summary>
    Gradient,

    /// <summary>Fine detail: a zone plate, a checkerboard and thin lines.</summary>
    Detail,

    /// <summary>Independent pseudo-random noise per frame.</summary>
    Noise,

    /// <summary>A textured background panning by (3, 1) samples per frame with a square moving the other way.</summary>
    Motion,

    /// <summary>Motion that cuts to a different scene every 12 frames.</summary>
    Cuts,
}

/// <summary>One encoded frame: the access unit bytes and the encoder's reconstruction.</summary>
/// <param name="Data">The access unit as framed by the encoder.</param>
/// <param name="Timestamp">The 1/90000 timestamp.</param>
/// <param name="IsKeyframe">Whether it is an IDR picture.</param>
/// <param name="Reconstruction">The encoder's reconstruction, packed planar 4:2:0 at the output size.</param>
/// <param name="Statistics">The rate-control diagnostics.</param>
internal sealed record EncodedFrame(byte[] Data, long Timestamp, bool IsKeyframe, byte[] Reconstruction, H264FrameStatistics Statistics);

/// <summary>Synthetic video and encoder helpers.</summary>
internal static class H264EncoderTestSupport
{
    /// <summary>The 1/90000 time base.</summary>
    public static readonly MediaTimeBase TimeBase = new(1, 90000);

    /// <summary>Returns options for the canonical defaults at a size.</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="qp">The base QP.</param>
    /// <returns>The options.</returns>
    public static H264EncoderOptions Options(int width, int height, int qp = 26) => new()
    {
        Width = width,
        Height = height,
        KeyframeInterval = TimeSpan.FromSeconds(2),
        RateControl = H264RateControlMode.ConstantQuality,
        BaseQp = qp,
        MaxBitrateKilobits = 100000,
    };

    /// <summary>Creates frame <paramref name="index"/> of a pattern at 25 frames per second.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="index">The frame index.</param>
    /// <param name="frameDuration">The frame duration in 1/90000 ticks.</param>
    /// <returns>The frame; the caller disposes it.</returns>
    public static VideoFrame CreateFrame(SyntheticPattern pattern, int width, int height, int index, long frameDuration = 3600)
    {
        var frame = new VideoFrame(width, height, index * frameDuration, TimeBase, VideoColorInfo.Bt709);
        Fill(frame, pattern, index);
        return frame;
    }

    /// <summary>Fills a frame with a pattern.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="pattern">The pattern.</param>
    /// <param name="index">The frame index.</param>
    public static void Fill(VideoFrame frame, SyntheticPattern pattern, int index)
    {
        int width = frame.Width, height = frame.Height, cw = frame.ChromaWidth, ch = frame.ChromaHeight;
        var y = frame.GetPlane(0);
        var cb = frame.GetPlane(1);
        var cr = frame.GetPlane(2);
        switch (pattern)
        {
            case SyntheticPattern.Flat:
                y.Fill(128);
                cb.Fill(128);
                cr.Fill(128);
                break;
            case SyntheticPattern.Bars:
                ReadOnlySpan<byte> by = [235, 219, 188, 173, 78, 63, 32, 16];
                ReadOnlySpan<byte> bu = [128, 16, 154, 42, 214, 102, 240, 128];
                ReadOnlySpan<byte> bv = [128, 138, 16, 26, 230, 240, 118, 128];
                for (var j = 0; j < height; j++)
                {
                    for (var i = 0; i < width; i++)
                    {
                        y[(j * width) + i] = by[i * 8 / width];
                    }
                }

                for (var j = 0; j < ch; j++)
                {
                    for (var i = 0; i < cw; i++)
                    {
                        cb[(j * cw) + i] = bu[i * 8 / cw];
                        cr[(j * cw) + i] = bv[i * 8 / cw];
                    }
                }

                break;
            case SyntheticPattern.Ramp:
                for (var j = 0; j < height; j++)
                {
                    for (var i = 0; i < width; i++)
                    {
                        y[(j * width) + i] = (byte)(16 + (i * 219 / Math.Max(1, width - 1)));
                    }
                }

                for (var j = 0; j < ch; j++)
                {
                    for (var i = 0; i < cw; i++)
                    {
                        cb[(j * cw) + i] = (byte)(16 + (j * 224 / Math.Max(1, ch - 1)));
                        cr[(j * cw) + i] = (byte)(240 - (j * 224 / Math.Max(1, ch - 1)));
                    }
                }

                break;
            case SyntheticPattern.Gradient:
                for (var j = 0; j < height; j++)
                {
                    for (var i = 0; i < width; i++)
                    {
                        y[(j * width) + i] = (byte)(40 + ((((i + j + index) * 160) / (width + height)) % 160));
                    }
                }

                for (var j = 0; j < ch; j++)
                {
                    for (var i = 0; i < cw; i++)
                    {
                        cb[(j * cw) + i] = (byte)(96 + (i * 64 / cw));
                        cr[(j * cw) + i] = (byte)(160 - (j * 64 / ch));
                    }
                }

                break;
            case SyntheticPattern.Detail:
                for (var j = 0; j < height; j++)
                {
                    for (var i = 0; i < width; i++)
                    {
                        byte v;
                        if (i < width / 2)
                        {
                            // Zone plate: cos(k r^2) from a fixed integer approximation of the phase.
                            int dx = i - (width / 4), dy = j - (height / 2);
                            var phase = ((dx * dx) + (dy * dy)) * 3 % 256;
                            v = (byte)(126 + Triangle(phase, 90));
                        }
                        else if (j < height / 2)
                        {
                            v = (byte)((((i >> 1) + (j >> 1)) & 1) == 0 ? 40 : 210);
                        }
                        else
                        {
                            v = (byte)(i % 5 == 0 || j % 7 == 0 ? 225 : 30 + (((i * 7) + (j * 3)) % 40));
                        }

                        y[(j * width) + i] = v;
                    }
                }

                for (var j = 0; j < ch; j++)
                {
                    for (var i = 0; i < cw; i++)
                    {
                        cb[(j * cw) + i] = (byte)((((i >> 2) + (j >> 2)) & 1) == 0 ? 90 : 170);
                        cr[(j * cw) + i] = (byte)(128 + Triangle(((i * i) + (j * 5)) % 256, 50));
                    }
                }

                break;
            case SyntheticPattern.Noise:
                {
                    var state = 0x9E3779B9u ^ (uint)(index * 0x85EBCA6B);
                    for (var k = 0; k < y.Length; k++)
                    {
                        y[k] = (byte)(16 + (Next(ref state) % 220));
                    }

                    for (var k = 0; k < cb.Length; k++)
                    {
                        cb[k] = (byte)(64 + (Next(ref state) % 128));
                        cr[k] = (byte)(64 + (Next(ref state) % 128));
                    }
                }

                break;
            case SyntheticPattern.Motion:
                Motion(frame, index, 0);
                break;
            case SyntheticPattern.Cuts:
                Motion(frame, index % 12, index / 12);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pattern));
        }
    }

    /// <summary>Encodes frames of a pattern and decodes nothing; returns the units with the reconstructions.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="pattern">The pattern.</param>
    /// <param name="count">The frame count.</param>
    /// <param name="first">The first frame index.</param>
    /// <returns>The encoded frames.</returns>
    public static List<EncodedFrame> EncodeAll(H264Encoder encoder, SyntheticPattern pattern, int count, int first = 0)
    {
        var result = new List<EncodedFrame>();
        var units = new List<H264EncodedAccessUnit>();
        var options = encoder.Options;
        for (var i = first; i < first + count; i++)
        {
            using var frame = CreateFrame(pattern, options.Width, options.Height, i);
            encoder.Encode(frame, units);
            result.Add(Take(encoder, units));
        }

        return result;
    }

    /// <summary>Takes the single unit an Encode call produced and the matching reconstruction.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="units">The output list; cleared and its unit disposed.</param>
    /// <returns>The encoded frame.</returns>
    public static EncodedFrame Take(H264Encoder encoder, List<H264EncodedAccessUnit> units)
    {
        Assert.Single(units);
        using var unit = units[0];
        units.Clear();
        var options = encoder.Options;
        using var reconstruction = new VideoFrame(options.Width, options.Height, 0, TimeBase, VideoColorInfo.Bt709);
        encoder.CopyReconstruction(reconstruction);
        return new EncodedFrame(unit.Data.ToArray(), unit.Timestamp, unit.IsKeyframe, Pack(reconstruction), unit.Statistics);
    }

    /// <summary>Packs a frame's planes.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>Y, Cb then Cr.</returns>
    public static byte[] Pack(VideoFrame frame)
    {
        var result = new byte[frame.ByteCount];
        var offset = 0;
        for (var plane = 0; plane < 3; plane++)
        {
            var samples = frame.GetPlane(plane);
            samples.CopyTo(result.AsSpan(offset));
            offset += samples.Length;
        }

        return result;
    }

    /// <summary>Decodes length-prefixed units with the managed decoder and returns the packed frames in output order.</summary>
    /// <param name="avcC">The decoder configuration record.</param>
    /// <param name="frames">The encoded frames.</param>
    /// <returns>The decoded frames with their timestamps.</returns>
    public static List<(long Timestamp, bool IsKeyframe, byte[] Samples)> DecodeManaged(ReadOnlyMemory<byte> avcC, IReadOnlyList<EncodedFrame> frames)
    {
        using var decoder = new H264Decoder();
        decoder.Configure(H264DecoderConfiguration.FromAvcC(avcC));
        return DecodeWith(decoder, frames);
    }

    /// <summary>Decodes Annex B units with the managed decoder.</summary>
    /// <param name="frames">The encoded frames.</param>
    /// <returns>The decoded frames with their timestamps.</returns>
    public static List<(long Timestamp, bool IsKeyframe, byte[] Samples)> DecodeManagedAnnexB(IReadOnlyList<EncodedFrame> frames)
    {
        using var decoder = H264TestSupport.CreateAnnexB();
        return DecodeWith(decoder, frames);
    }

    /// <summary>Asserts that every decoded frame equals the encoder's reconstruction of the same frame.</summary>
    /// <param name="frames">The encoded frames.</param>
    /// <param name="decoded">The decoded frames in output order.</param>
    public static void AssertMatchesReconstruction(IReadOnlyList<EncodedFrame> frames, IReadOnlyList<(long Timestamp, bool IsKeyframe, byte[] Samples)> decoded)
    {
        Assert.Equal(frames.Count, decoded.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].Timestamp, decoded[i].Timestamp);
            Assert.Equal(frames[i].IsKeyframe, decoded[i].IsKeyframe);
            if (!frames[i].Reconstruction.AsSpan().SequenceEqual(decoded[i].Samples))
            {
                var first = frames[i].Reconstruction.AsSpan().CommonPrefixLength(decoded[i].Samples);
                Assert.Fail(string.Create(CultureInfo.InvariantCulture, $"Frame {i} differs from the encoder reconstruction at byte {first} of {decoded[i].Samples.Length}."));
            }
        }
    }

    /// <summary>Concatenates Annex B access units into a stream.</summary>
    /// <param name="frames">The frames, Annex B framed.</param>
    /// <returns>The stream.</returns>
    public static byte[] Concatenate(IEnumerable<EncodedFrame> frames)
    {
        using var stream = new MemoryStream();
        foreach (var frame in frames)
        {
            stream.Write(frame.Data);
        }

        return stream.ToArray();
    }

    /// <summary>Builds an FLV file of one AVC track: the avcC sequence header, then one NALU tag per access unit.</summary>
    /// <param name="avcC">The decoder configuration record.</param>
    /// <param name="frames">Length-prefixed access units.</param>
    /// <returns>The file bytes.</returns>
    public static byte[] BuildFlv(ReadOnlySpan<byte> avcC, IReadOnlyList<EncodedFrame> frames)
    {
        using var stream = new MemoryStream();
        stream.Write("FLV\u0001\u0001\0\0\0\u0009\0\0\0\0"u8);
        WriteTag(stream, 0, [0x17, 0, 0, 0, 0, .. avcC]);
        foreach (var frame in frames)
        {
            WriteTag(stream, frame.Timestamp / 90, [frame.IsKeyframe ? (byte)0x17 : (byte)0x27, 1, 0, 0, 0, .. frame.Data]);
        }

        return stream.ToArray();
    }

    /// <summary>Runs a tool to completion and returns its standard output.</summary>
    /// <param name="tool">The executable.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The standard output.</returns>
    public static async Task<string> RunAsync(string tool, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var text = await output;
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(tool)} failed: {errors}");
        return text;
    }

    /// <summary>Writes one FLV video tag and its PreviousTagSize.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="milliseconds">The timestamp.</param>
    /// <param name="body">The tag body.</param>
    private static void WriteTag(MemoryStream stream, long milliseconds, ReadOnlySpan<byte> body)
    {
        var ms = (uint)milliseconds;
        stream.Write([9, (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, (byte)(ms >> 16), (byte)(ms >> 8), (byte)ms, (byte)(ms >> 24), 0, 0, 0]);
        stream.Write(body);
        var size = (uint)(body.Length + 11);
        stream.Write([(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size]);
    }

    /// <summary>Decodes with a configured decoder.</summary>
    /// <param name="decoder">The decoder.</param>
    /// <param name="frames">The frames.</param>
    /// <returns>The decoded frames.</returns>
    private static List<(long Timestamp, bool IsKeyframe, byte[] Samples)> DecodeWith(H264Decoder decoder, IReadOnlyList<EncodedFrame> frames)
    {
        var result = new List<(long, bool, byte[])>();
        var output = new List<VideoFrame>();
        foreach (var frame in frames)
        {
            decoder.Decode(new EncodedPacket(frame.Data, frame.Timestamp, TimeBase), output, TestContext.Current.CancellationToken);
            Collect(output, result);
        }

        decoder.Drain(output);
        Collect(output, result);
        Assert.Equal(0, decoder.SkippedPictures);
        return result;
    }

    /// <summary>Packs and disposes decoded frames.</summary>
    /// <param name="output">The frames; cleared.</param>
    /// <param name="result">Receives them.</param>
    private static void Collect(List<VideoFrame> output, List<(long, bool, byte[])> result)
    {
        foreach (var frame in output)
        {
            result.Add((frame.TimeBase.Rescale(frame.Timestamp, TimeBase), frame.IsKeyframe, Pack(frame)));
            frame.Dispose();
        }

        output.Clear();
    }

    /// <summary>Draws the motion scene.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="t">The time within the scene.</param>
    /// <param name="scene">The scene number.</param>
    private static void Motion(VideoFrame frame, int t, int scene)
    {
        int width = frame.Width, height = frame.Height, cw = frame.ChromaWidth, ch = frame.ChromaHeight;
        var y = frame.GetPlane(0);
        var cb = frame.GetPlane(1);
        var cr = frame.GetPlane(2);
        int ox = t * 3, oy = t, seed = scene * 977;
        int sx = width - 24 - (t * 2), sy = (height / 3) + t;
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                var u = i + ox + seed;
                var v = j + oy;
                var texture = 60 + Triangle((u * 5) + (v * 3), 50) + Triangle((u * 2) - (v * 7) + seed, 40) + (Hash(u >> 3, v >> 3, scene) % 24);
                var inSquare = i >= sx && i < sx + 20 && j >= sy && j < sy + 20;
                y[(j * width) + i] = (byte)Math.Clamp(inSquare ? 220 - ((i - sx) * 2) : texture, 16, 235);
            }
        }

        for (var j = 0; j < ch; j++)
        {
            for (var i = 0; i < cw; i++)
            {
                var u = (2 * i) + ox + seed;
                var v = (2 * j) + oy;
                var inSquare = 2 * i >= sx && 2 * i < sx + 20 && 2 * j >= sy && 2 * j < sy + 20;
                cb[(j * cw) + i] = (byte)(inSquare ? 200 : 110 + Triangle(u + (scene * 40), 25));
                cr[(j * cw) + i] = (byte)(inSquare ? 60 : 140 - Triangle(v + (scene * 70), 25));
            }
        }
    }

    /// <summary>A triangle wave with period 256 and the given amplitude, centred on zero.</summary>
    /// <param name="phase">The phase.</param>
    /// <param name="amplitude">The peak amplitude.</param>
    /// <returns>The value.</returns>
    private static int Triangle(int phase, int amplitude)
    {
        var p = phase & 255;
        var tri = p < 128 ? p : 255 - p;
        return ((tri * 2 * amplitude) / 127) - amplitude;
    }

    /// <summary>A small integer hash.</summary>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <param name="z">Z.</param>
    /// <returns>A non-negative hash.</returns>
    private static int Hash(int x, int y, int z)
    {
        var h = (uint)((x * 374761393) + (y * 668265263) + (z * 1274126177));
        h = (h ^ (h >> 13)) * 1274126177;
        return (int)((h ^ (h >> 16)) & 0x7FFFFFFF);
    }

    /// <summary>xorshift32.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The next value.</returns>
    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
