// Behaviour of the managed H.264 decoder beyond bit exactness: framing, parameter set changes, waiting for an IDR,
// unsupported features, configured limits, the outstanding-frame budget, reset, drain, disposal, frame metadata,
// cancellation and independence of concurrent instances.

using RTSP.IngestService.Hosting;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Video.H264;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Decoder behaviour tests.</summary>
public sealed class H264DecoderTests
{
    /// <summary>AVC length-prefixed input with avcC parameter sets decodes identically to Annex B.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="frames">The frame count.</param>
    [Theory]
    [MemberData(nameof(H264TestSupport.DecodableFixtures), MemberType = typeof(H264TestSupport))]
    public void LengthPrefixedMatchesReference(string name, int width, int height, int frames)
    {
        var units = H264TestSupport.ReadAccessUnits(name);
        using var decoder = new H264Decoder();
        decoder.Configure(H264DecoderConfiguration.FromAvcC(H264TestSupport.BuildAvcC(units[0])));
        var decoded = H264TestSupport.DecodeAll(decoder, [.. units.Select(H264TestSupport.ToLengthPrefixed)], H264TestSupport.ReadTimestamps(name));
        Assert.Equal(H264TestSupport.ReadReference(name), decoded.Select(static f => f.Md5));
        Assert.All(decoded, f => Assert.Equal((width, height), (f.Width, f.Height)));
        Assert.Equal(frames, decoded.Count);
    }

    /// <summary>A new SPS and PPS with a different size and profile take effect at the next IDR picture.</summary>
    [Fact]
    public void ChangesParameterSetsAtIdr()
    {
        var first = H264TestSupport.ReadAccessUnits("baseline-cavlc-176x144");
        var second = H264TestSupport.ReadAccessUnits("high-cavlc-8x8-256x192");
        using var decoder = H264TestSupport.CreateAnnexB();
        var decoded = H264TestSupport.DecodeAll(decoder, [.. first, .. second]);
        Assert.Equal([.. H264TestSupport.ReadReference("baseline-cavlc-176x144"), .. H264TestSupport.ReadReference("high-cavlc-8x8-256x192")], decoded.Select(static f => f.Md5));
        Assert.All(decoded.Take(first.Count), static f => Assert.Equal((176, 144), (f.Width, f.Height)));
        Assert.All(decoded.Skip(first.Count), static f => Assert.Equal((256, 192), (f.Width, f.Height)));
        Assert.Equal(0, decoder.AllocatedPictures);
    }

    /// <summary>Pictures before the first IDR are counted and skipped; decoding starts bit-exact at the IDR.</summary>
    [Fact]
    public void SkipsPicturesUntilIdr()
    {
        const string name = "baseline-cavlc-176x144";
        var units = H264TestSupport.ReadAccessUnits(name);
        var reference = H264TestSupport.ReadReference(name);
        using var decoder = H264TestSupport.CreateAnnexB();
        Assert.True(decoder.AwaitingIdr);
        var decoded = H264TestSupport.DecodeAll(decoder, units[1..]);
        Assert.Equal(14, decoder.SkippedPictures);
        Assert.Equal(reference[15..], decoded.Select(static f => f.Md5));
        Assert.True(decoded[0].IsKeyframe);
    }

    /// <summary>Valid streams outside the supported matrix fail as unsupported, and the decoder then decodes a supported stream.</summary>
    /// <param name="name">The rejection fixture.</param>
    [Theory]
    [InlineData("reject-high10")]
    [InlineData("reject-high422")]
    [InlineData("reject-interlaced")]
    [InlineData("reject-lossless444")]
    public void RejectsUnsupportedFixtureThenRecovers(string name)
    {
        using var decoder = H264TestSupport.CreateAnnexB();
        var frames = new List<VideoFrame>();
        var error = Assert.Throws<CodecException>(() => decoder.Decode(Packet(H264TestSupport.ReadAccessUnits(name)[0]), frames, TestContext.Current.CancellationToken));
        Assert.Equal(CodecErrorKind.Unsupported, error.Kind);
        Assert.Empty(frames);
        Assert.Equal(0, decoder.AllocatedPictures);
        AssertDecodesExactly(decoder, "high-intra-160x96");
    }

    /// <summary>Data partitioning, SP slices and slice groups are unsupported.</summary>
    [Fact]
    public void RejectsUnsupportedSyntax()
    {
        using var decoder = H264TestSupport.CreateAnnexB();
        var frames = new List<VideoFrame>();
        AssertKind(CodecErrorKind.Unsupported, () => decoder.Decode(Packet([0, 0, 0, 1, 0x42, 0x80]), frames, TestContext.Current.CancellationToken));


        var units = H264TestSupport.SplitNalUnits(H264TestSupport.ReadAccessUnits("baseline-cavlc-176x144")[0]);
        var fmo = units.Select(static u => (u[0] & 0x1F) == 8 ? new byte[] { 0x68, 0b1100_0101 } : u);
        AssertKind(CodecErrorKind.Unsupported, () => decoder.Decode(Packet(H264TestSupport.Join(fmo)), frames, TestContext.Current.CancellationToken));
        Assert.Empty(frames);

        // After an IDR, a slice header is parsed: first_mb_in_slice ue(0) '1', slice_type ue(3) '00100' (SP), stop bit.
        decoder.Decode(Packet(H264TestSupport.ReadAccessUnits("baseline-cavlc-176x144")[0]), frames, TestContext.Current.CancellationToken);
        AssertKind(CodecErrorKind.Unsupported, () => decoder.Decode(Packet([0, 0, 0, 1, 0x21, 0b1001_0010]), frames, TestContext.Current.CancellationToken));
        decoder.Drain(frames);
        Assert.Equal(H264TestSupport.ReadReference("baseline-cavlc-176x144")[0], H264TestSupport.Md5(Assert.Single(frames)));
        frames[0].Dispose();
        frames.Clear();
        AssertDecodesExactly(decoder, "baseline-cavlc-176x144");
    }

    /// <summary>Malformed configuration records are invalid data.</summary>
    [Fact]
    public void RejectsInvalidAvcC()
    {
        using var decoder = new H264Decoder();
        var record = H264TestSupport.BuildAvcC(H264TestSupport.ReadAccessUnits("baseline-cavlc-176x144")[0]);
        record[4] = 0xFE;
        AssertKind(CodecErrorKind.InvalidData, () => decoder.Configure(H264DecoderConfiguration.FromAvcC(record)));
        AssertKind(CodecErrorKind.InvalidData, () => decoder.Configure(H264DecoderConfiguration.FromAvcC(new byte[] { 1, 66 })));
        Assert.Throws<InvalidOperationException>(() => decoder.Decode(Packet([0, 0, 1, 9, 0x10]), new List<VideoFrame>(), TestContext.Current.CancellationToken));
    }

    /// <summary>Each configured limit is enforced as a resource limit before memory is committed.</summary>
    /// <param name="limit">The limit to lower.</param>
    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("macroblocks")]
    [InlineData("accessUnit")]
    [InlineData("nalUnits")]
    [InlineData("parameterSet")]
    [InlineData("dpb")]
    public void EnforcesLimits(string limit)
    {
        var options = limit switch
        {
            "width" => new H264DecoderOptions { MaxWidth = 160 },
            "height" => new H264DecoderOptions { MaxHeight = 128 },
            "macroblocks" => new H264DecoderOptions { MaxMacroblocks = 98 },
            "accessUnit" => new H264DecoderOptions { MaxAccessUnitBytes = 100 },
            "nalUnits" => new H264DecoderOptions { MaxNalUnitsPerAccessUnit = 2 },
            "parameterSet" => new H264DecoderOptions { MaxParameterSetBytes = 16 },
            _ => new H264DecoderOptions { MaxDpbBytes = 100_000 },
        };
        using var decoder = H264TestSupport.CreateAnnexB(options);
        var frames = new List<VideoFrame>();
        AssertKind(CodecErrorKind.ResourceLimit, () => decoder.Decode(Packet(H264TestSupport.ReadAccessUnits("baseline-cavlc-176x144")[0]), frames, TestContext.Current.CancellationToken));
        Assert.Empty(frames);
        Assert.Equal(0, decoder.AllocatedPictures);
    }

    /// <summary>Invalid limits are rejected at construction.</summary>
    [Fact]
    public void ValidatesOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264Decoder(new H264DecoderOptions { MaxOutstandingFrames = H264DecoderOptions.MaxPicturesPerCall - 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264Decoder(new H264DecoderOptions { MaxWidth = 8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264Decoder(new H264DecoderOptions { MaxParameterSetBytes = 8 }));
    }

    /// <summary>A caller holding frames is refused before any state changes; after disposing them, decoding continues exactly.</summary>
    [Fact]
    public void EnforcesOutstandingFrameBudget()
    {
        const string name = "nvenc-high-bframes-320x240";
        var units = H264TestSupport.ReadAccessUnits(name);
        var options = new H264DecoderOptions { MaxOutstandingFrames = H264DecoderOptions.MaxPicturesPerCall + 2 };
        using var decoder = H264TestSupport.CreateAnnexB(options);
        var held = new List<VideoFrame>();
        var result = new List<DecodedFrameInfo>();
        var refusals = 0;
        var i = 0;
        while (i < units.Count)
        {
            try
            {
                decoder.Decode(Packet(units[i], i), held, TestContext.Current.CancellationToken);
                i++;
            }
            catch (CodecException e) when (e.Kind == CodecErrorKind.ResourceLimit)
            {
                refusals++;
                Assert.True(decoder.OutstandingFrames > 2);
                Assert.False(decoder.AwaitingIdr);
                H264TestSupport.Collect(held, result);
                Assert.Equal(0, decoder.OutstandingFrames);
            }
        }

        decoder.Drain(held);
        H264TestSupport.Collect(held, result);
        Assert.True(refusals > 0);
        Assert.Equal(H264TestSupport.ReadReference(name), result.Select(static f => f.Md5));
    }

    /// <summary>Reset discards pending pictures and returns every buffer; decoding restarts bit-exact.</summary>
    [Fact]
    public void ResetDiscardsAndRestarts()
    {
        const string name = "main-cabac-bpyramid-weightp-320x240";
        var units = H264TestSupport.ReadAccessUnits(name);
        using var decoder = H264TestSupport.CreateAnnexB();
        var frames = new List<VideoFrame>();
        for (var i = 0; i < 6; i++)
        {
            decoder.Decode(Packet(units[i], i), frames, TestContext.Current.CancellationToken);
        }

        Assert.True(decoder.AllocatedPictures > 0);
        frames.ForEach(static f => f.Dispose());
        frames.Clear();
        decoder.Reset();
        Assert.Equal(0, decoder.AllocatedPictures);
        Assert.True(decoder.AwaitingIdr);
        decoder.Drain(frames);
        Assert.Empty(frames);
        AssertDecodesExactly(decoder, name);
    }

    /// <summary>After a drain, the next non-IDR picture is skipped; a second drain outputs nothing.</summary>
    [Fact]
    public void DrainEndsTheStream()
    {
        const string name = "baseline-cavlc-176x144";
        var units = H264TestSupport.ReadAccessUnits(name);
        using var decoder = H264TestSupport.CreateAnnexB();
        var decoded = H264TestSupport.DecodeAll(decoder, units[..3]);
        Assert.Equal(3, decoded.Count);
        Assert.Equal(0, decoder.AllocatedPictures);
        Assert.True(decoder.AwaitingIdr);

        var frames = new List<VideoFrame>();
        decoder.Decode(Packet(units[3]), frames, TestContext.Current.CancellationToken);
        decoder.Drain(frames);
        Assert.Empty(frames);
        Assert.Equal(1, decoder.SkippedPictures);
    }

    /// <summary>Frames stay valid after the decoder is disposed; disposing them releases the budget.</summary>
    [Fact]
    public void FramesOutliveDecoder()
    {
        const string name = "high-cropped-slices-bt709-198x110";
        var units = H264TestSupport.ReadAccessUnits(name);
        var reference = H264TestSupport.ReadReference(name);
        var decoder = H264TestSupport.CreateAnnexB();
        var frames = new List<VideoFrame>();
        for (var i = 0; i < units.Count; i++)
        {
            decoder.Decode(Packet(units[i], i), frames, TestContext.Current.CancellationToken);
            if (frames.Count >= 8)
            {
                break;
            }
        }

        decoder.Dispose();
        decoder.Dispose();
        Assert.Equal(0, decoder.AllocatedPictures);
        Assert.Equal(frames.Count, decoder.OutstandingFrames);
        Assert.Equal(reference[..frames.Count], frames.Select(H264TestSupport.Md5));
        foreach (var frame in frames)
        {
            frame.Dispose();
            frame.Dispose();
        }

        Assert.Equal(0, decoder.OutstandingFrames);
        Assert.Throws<ObjectDisposedException>(() => frames[0].GetPlane(0));
        Assert.Throws<ObjectDisposedException>(() => decoder.Decode(Packet(units[0]), frames, TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => decoder.Drain(frames));
    }

    /// <summary>Frames carry the format, colour, keyframe flags and a contiguous presentation index.</summary>
    [Fact]
    public void FramesCarryMetadata()
    {
        const string name = "high-cropped-slices-bt709-198x110";
        var units = H264TestSupport.ReadAccessUnits(name);
        using var decoder = H264TestSupport.CreateAnnexB();
        var frames = new List<VideoFrame>();
        var timestamps = H264TestSupport.ReadTimestamps(name);
        var index = 0;
        for (var i = 0; i <= units.Count; i++)
        {
            if (i < units.Count)
            {
                decoder.Decode(new EncodedPacket(units[i], timestamps[i], H264TestSupport.TimeBase), frames, TestContext.Current.CancellationToken);
            }
            else
            {
                decoder.Drain(frames);
            }

            foreach (var frame in frames)
            {
                Assert.Equal(VideoPixelFormat.Yuv420P8, frame.Format);
                Assert.Equal(8, frame.BitDepth);
                Assert.Equal(VideoColorInfo.Bt709, frame.Color);
                Assert.Equal(H264TestSupport.TimeBase, frame.TimeBase);
                Assert.Equal(index, frame.PresentationIndex);
                Assert.Equal((198, 99), (frame.GetStride(0), frame.GetStride(1)));
                Assert.Equal((198 * 110) + (2 * 99 * 55), frame.ByteCount);
                Assert.Equal(index % 10 == 0, frame.IsKeyframe);
                frame.Dispose();
                index++;
            }

            frames.Clear();
        }

        Assert.Equal(units.Count, index);
    }

    /// <summary>A cancelled token is observed before decoding and leaves the decoder usable.</summary>
    [Fact]
    public void ObservesCancellationBeforeDecoding()
    {
        using var decoder = H264TestSupport.CreateAnnexB();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var units = H264TestSupport.ReadAccessUnits("high-intra-160x96");
        Assert.Throws<OperationCanceledException>(() => decoder.Decode(Packet(units[0]), new List<VideoFrame>(), cancelled.Token));
        Assert.Equal(0, decoder.AllocatedPictures);
        AssertDecodesExactly(decoder, "high-intra-160x96");
    }

    /// <summary>Decoders on different threads share no state and are deterministic.</summary>
    [Fact]
    public async Task ConcurrentDecodersAreIndependentAndDeterministic()
    {
        var names = H264TestSupport.DecodableFixtureNames;
        var tasks = names.Concat(names).Select(name => Task.Run(
            () =>
            {
                using var decoder = H264TestSupport.CreateAnnexB();
                return (name, H264TestSupport.DecodeAll(decoder, H264TestSupport.ReadAccessUnits(name)).Select(static f => f.Md5).ToList());
            },
            TestContext.Current.CancellationToken));
        foreach (var (name, md5) in await Task.WhenAll(tasks))
        {
            Assert.Equal(H264TestSupport.ReadReference(name), md5);
        }
    }

    /// <summary>The codec-smoke h264 command writes the reference frames and passes its own checks; bad input fails.</summary>
    [Fact]
    public void SmokeCommandDecodesFixture()
    {
        const string name = "main-cabac-bpyramid-weightp-320x240";
        var directory = Directory.CreateTempSubdirectory("h264-smoke-");
        try
        {
            var input = Path.Combine(H264TestSupport.FixtureDirectory, $"h264-{name}.h264");
            var output = Path.Combine(directory.FullName, "out.yuv");
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Assert.True(CodecSmokeCommand.TryRun([CodecSmokeCommand.CommandName, "h264", input, output], stdout, stderr, out var exitCode));
            Assert.True(exitCode == ExitCodes.Success, stderr.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.Contains("Output: 36 frames, 320x240 Yuv420P8 8-bit, 2 keyframes, 0 skipped pictures", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Digest: first frame ", stdout.ToString(), StringComparison.Ordinal);

            var raw = File.ReadAllBytes(output);
            const int frameBytes = 320 * 240 * 3 / 2;
            Assert.Equal(36 * frameBytes, raw.Length);
            var reference = H264TestSupport.ReadReference(name);
            for (var i = 0; i < 36; i++)
            {
                Assert.Equal(reference[i], H264TestSupport.Md5(raw.AsSpan(i * frameBytes, frameBytes)));
            }

            var garbage = Path.Combine(directory.FullName, "garbage.h264");
            File.WriteAllBytes(garbage, [0, 0, 1, 0x65, 0x88, 0x84, 0x21]);
            Assert.True(CodecSmokeCommand.TryRun([CodecSmokeCommand.CommandName, "h264", garbage, output], stdout, stderr, out exitCode));
            Assert.Equal(ExitCodes.Failure, exitCode);
            Assert.True(CodecSmokeCommand.TryRun([CodecSmokeCommand.CommandName, "h264", Path.Combine(directory.FullName, "missing.h264"), output], stdout, stderr, out exitCode));
            Assert.Equal(ExitCodes.UsageError, exitCode);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>Decodes a fixture from its start and asserts bit exactness.</summary>
    /// <param name="decoder">The decoder.</param>
    /// <param name="name">The fixture.</param>
    internal static void AssertDecodesExactly(H264Decoder decoder, string name)
    {
        var decoded = H264TestSupport.DecodeAll(decoder, H264TestSupport.ReadAccessUnits(name));
        Assert.Equal(H264TestSupport.ReadReference(name), decoded.Select(static f => f.Md5));
        Assert.Equal(0, decoder.AllocatedPictures);
        Assert.Equal(0, decoder.OutstandingFrames);
    }

    /// <summary>Wraps an access unit.</summary>
    /// <param name="data">The access unit.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <returns>The packet.</returns>
    internal static EncodedPacket Packet(byte[] data, long timestamp = 0) => new(data, timestamp, H264TestSupport.TimeBase);

    /// <summary>Asserts that an action throws a codec exception of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="action">The action.</param>
    private static void AssertKind(CodecErrorKind kind, Action action) => Assert.Equal(kind, Assert.Throws<CodecException>(action).Kind);
}
