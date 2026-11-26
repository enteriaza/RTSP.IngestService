// Robustness of the managed H.264 decoder against damaged input. Every truncation of selected access units must
// either fail with a CodecException or produce only frames identical to the reference; seeded random damage must
// never escape as anything but a CodecException; and after any failure the decoder must hold no pooled buffers once
// drained and must recover bit-exact at the next IDR picture.

using RTSP.IngestService.Media.Codecs;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Damaged-input tests.</summary>
public sealed class H264RobustnessTests
{
    /// <summary>Every prefix of an access unit fails cleanly or decodes to reference frames only, and never leaks buffers.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="index">The access unit to truncate, decoded after its predecessors.</param>
    [Theory]
    [InlineData("high-intra-160x96", 0)]
    [InlineData("baseline-cavlc-176x144", 1)]
    [InlineData("constrained-baseline-openh264-320x240", 1)]
    [InlineData("main-cabac-bpyramid-weightp-320x240", 2)]
    [InlineData("high-cropped-slices-bt709-198x110", 1)]
    public void EveryTruncationFailsCleanly(string name, int index)
    {
        var units = H264TestSupport.ReadAccessUnits(name);
        var expected = ExpectedByTimestamp(name);
        var timestamps = H264TestSupport.ReadTimestamps(name);
        using var decoder = H264TestSupport.CreateAnnexB();
        var frames = new List<VideoFrame>();
        var failures = 0;
        for (var length = 0; length < units[index].Length; length++)
        {
            decoder.Reset();
            for (var i = 0; i < index; i++)
            {
                decoder.Decode(new EncodedPacket(units[i], timestamps[i], H264TestSupport.TimeBase), frames, TestContext.Current.CancellationToken);
            }

            try
            {
                decoder.Decode(new EncodedPacket(units[index].AsMemory(0, length), timestamps[index], H264TestSupport.TimeBase), frames, TestContext.Current.CancellationToken);
            }
            catch (CodecException e)
            {
                Assert.NotEqual(CodecErrorKind.ResourceLimit, e.Kind);
                Assert.True(decoder.AwaitingIdr);
                failures++;
            }

            decoder.Drain(frames);
            AssertFramesMatch(frames, expected, $"{length}-byte prefix of access unit {index}");
            Assert.Equal(0, decoder.AllocatedPictures);
            Assert.Equal(0, decoder.OutstandingFrames);
        }

        Assert.True(failures > units[index].Length / 2, $"Only {failures} of {units[index].Length} truncations failed.");
        H264DecoderTests.AssertDecodesExactly(decoder, name);
    }

    /// <summary>Seeded bit flips, byte overwrites, truncations, duplications and drops only ever raise CodecException.</summary>
    /// <param name="name">The fixture.</param>
    [Theory]
    [InlineData("baseline-cavlc-176x144")]
    [InlineData("constrained-baseline-openh264-320x240")]
    [InlineData("main-cabac-bpyramid-weightp-320x240")]
    [InlineData("high-8x8-temporal-cqm-352x288")]
    [InlineData("high-cavlc-8x8-256x192")]
    [InlineData("nvenc-high-bframes-320x240")]
    public void SeededDamageRaisesOnlyCodecExceptions(string name)
    {
        var units = H264TestSupport.ReadAccessUnits(name);
        var reference = H264TestSupport.ReadReference(name);
        var (width, height) = FixtureSize(name);
        using var decoder = H264TestSupport.CreateAnnexB();
        var frames = new List<VideoFrame>();
        for (var seed = 0; seed < 24; seed++)
        {
            var random = new Random(seed);
            decoder.Reset();
            for (var i = 0; i < units.Count; i++)
            {
                var data = random.Next(4) == 0 ? Damage(units[i], random) : units[i];
                try
                {
                    decoder.Decode(new EncodedPacket(data, i, H264TestSupport.TimeBase), frames, TestContext.Current.CancellationToken);
                }
                catch (CodecException)
                {
                    Assert.True(decoder.AwaitingIdr);
                }

                Assert.All(frames, f => Assert.Equal((width, height), (f.Width, f.Height)));
                frames.ForEach(static f => f.Dispose());
                frames.Clear();
            }

            decoder.Drain(frames);
            frames.ForEach(static f => f.Dispose());
            frames.Clear();
            Assert.Equal(0, decoder.AllocatedPictures);
            Assert.Equal(0, decoder.OutstandingFrames);
        }

        var decoded = H264TestSupport.DecodeAll(decoder, units);
        Assert.Equal(reference, decoded.Select(static f => f.Md5));
    }

    /// <summary>A failure mid-stream keeps already decoded frames, skips to the next IDR, and resumes bit-exact.</summary>
    [Fact]
    public void RecoversAtNextIdrAfterFailure()
    {
        const string name = "baseline-cavlc-176x144";
        var units = H264TestSupport.ReadAccessUnits(name);
        var reference = H264TestSupport.ReadReference(name);
        using var decoder = H264TestSupport.CreateAnnexB();
        var frames = new List<VideoFrame>();
        var result = new List<DecodedFrameInfo>();
        for (var i = 0; i < units.Count; i++)
        {
            if (i == 5)
            {
                Assert.Throws<CodecException>(() => decoder.Decode(new EncodedPacket(units[i].AsMemory(0, units[i].Length / 2), i, H264TestSupport.TimeBase), frames, TestContext.Current.CancellationToken));
                Assert.True(decoder.AwaitingIdr);
                continue;
            }

            decoder.Decode(new EncodedPacket(units[i], i, H264TestSupport.TimeBase), frames, TestContext.Current.CancellationToken);
            H264TestSupport.Collect(frames, result);
        }

        decoder.Drain(frames);
        H264TestSupport.Collect(frames, result);
        Assert.Equal(9, decoder.SkippedPictures);
        Assert.Equal([.. reference[..5], .. reference[15..]], result.Select(static f => f.Md5));
        Assert.Equal(0, decoder.AllocatedPictures);
    }

    /// <summary>Random NAL units of small lengths raise only CodecException and do not disturb an already decoded picture.</summary>
    [Fact]
    public void RandomPacketsAreRejected()
    {
        var random = new Random(42);
        using var decoder = H264TestSupport.CreateAnnexB();
        var frames = new List<VideoFrame>();
        decoder.Decode(new EncodedPacket(H264TestSupport.ReadAccessUnits("high-intra-160x96")[0], 0, H264TestSupport.TimeBase), frames, TestContext.Current.CancellationToken);
        for (var i = 0; i < 2000; i++)
        {
            var data = new byte[4 + random.Next(64)];
            random.NextBytes(data);
            data[0] = 0;
            data[1] = 0;
            data[2] = 1;
            data[3] = (byte)(data[3] & 0x7F);
            try
            {
                decoder.Decode(new EncodedPacket(data, i + 1, H264TestSupport.TimeBase), frames, TestContext.Current.CancellationToken);
            }
            catch (CodecException)
            {
            }
        }

        decoder.Drain(frames);
        Assert.Equal(H264TestSupport.ReadReference("high-intra-160x96")[0], H264TestSupport.Md5(frames[0]));
        frames.ForEach(static f => f.Dispose());
        Assert.Equal(0, decoder.AllocatedPictures);
    }

    /// <summary>Applies one random kind of damage to a slice NAL unit of an access unit; parameter sets stay intact so the frame size is known.</summary>
    /// <param name="unit">The access unit.</param>
    /// <param name="random">The random source.</param>
    /// <returns>The damaged copy.</returns>
    private static byte[] Damage(byte[] unit, Random random)
    {
        var nals = H264TestSupport.SplitNalUnits(unit);
        var slices = Enumerable.Range(0, nals.Count).Where(i => (nals[i][0] & 0x1F) is 1 or 5).ToArray();
        var k = slices[random.Next(slices.Length)];
        var nal = (byte[])nals[k].Clone();
        switch (random.Next(5))
        {
            case 0:
                for (var n = random.Next(1, 9); n > 0; n--)
                {
                    var bit = random.Next(8, nal.Length * 8);
                    nal[bit >> 3] ^= (byte)(0x80 >> (bit & 7));
                }

                break;
            case 1:
                nal[random.Next(1, nal.Length)] = (byte)random.Next(256);
                break;
            case 2:
                nal = nal[..random.Next(1, nal.Length)];
                break;
            case 3:
                nals.Insert(k, nal);
                break;
            default:
                nals.RemoveAt(k);
                return H264TestSupport.Join(nals);
        }

        nals[k] = nal;
        return H264TestSupport.Join(nals);
    }

    /// <summary>Asserts that each frame equals the reference frame with its timestamp, then disposes them.</summary>
    /// <param name="frames">The frames; cleared.</param>
    /// <param name="expected">The reference MD5 by timestamp.</param>
    /// <param name="context">Context for failures.</param>
    private static void AssertFramesMatch(List<VideoFrame> frames, Dictionary<long, string> expected, string context)
    {
        try
        {
            foreach (var frame in frames)
            {
                Assert.True(expected[frame.Timestamp] == H264TestSupport.Md5(frame), $"The frame at {frame.Timestamp} differs from the reference after the {context}.");
            }
        }
        finally
        {
            frames.ForEach(static f => f.Dispose());
            frames.Clear();
        }
    }

    /// <summary>Maps a fixture's presentation timestamps to the reference MD5s.</summary>
    /// <param name="name">The fixture.</param>
    /// <returns>The map.</returns>
    private static Dictionary<long, string> ExpectedByTimestamp(string name) =>
        H264TestSupport.ReadTimestamps(name).Order().Zip(H264TestSupport.ReadReference(name)).ToDictionary(static p => p.First, static p => p.Second);

    /// <summary>Returns a fixture's display size.</summary>
    /// <param name="name">The fixture.</param>
    /// <returns>The size.</returns>
    private static (int Width, int Height) FixtureSize(string name)
    {
        var size = name[(name.LastIndexOf('-') + 1)..].Split('x');
        return (int.Parse(size[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(size[1], System.Globalization.CultureInfo.InvariantCulture));
    }
}
