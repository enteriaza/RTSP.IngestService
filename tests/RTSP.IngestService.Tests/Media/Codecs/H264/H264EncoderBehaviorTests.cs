// Behaviour of the managed H.264 encoder's public surface: option validation, frame validation that leaves the
// encoder usable, the faulted state, cancellation that leaves references and rate control untouched, the bounded
// outstanding-unit budget and unit ownership, IDR cadence on media time, flush, reset and reconfiguration, memory
// bounds and run-to-run determinism.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Managed H.264 encoder behaviour.</summary>
public sealed class H264EncoderBehaviorTests
{
    /// <summary>Gets invalid option sets.</summary>
    public static TheoryData<string> InvalidOptions => new()
    {
        "width", "odd", "height", "large", "interval-short", "interval-long", "qp", "qp-order", "maxrate", "target", "buffer", "refs", "search", "fps", "slices", "outstanding",
    };

    /// <summary>Invalid options are rejected with the option named.</summary>
    /// <param name="variant">The invalid variant.</param>
    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void RejectsInvalidOptions(string variant)
    {
        var valid = H264EncoderTestSupport.Options(64, 48);
        var options = variant switch
        {
            "width" => valid with { Width = 0 },
            "odd" => valid with { Width = 63 },
            "height" => valid with { Height = -2 },
            "large" => valid with { Width = 8192 },
            "interval-short" => valid with { KeyframeInterval = TimeSpan.FromMilliseconds(999) },
            "interval-long" => valid with { KeyframeInterval = TimeSpan.FromSeconds(61) },
            "qp" => valid with { BaseQp = 52 },
            "qp-order" => valid with { MinQp = 30, MaxQp = 20 },
            "maxrate" => valid with { MaxBitrateKilobits = 50 },
            "target" => valid with { RateControl = H264RateControlMode.AverageBitrate, TargetBitrateKilobits = 200000 },
            "buffer" => valid with { BufferSizeKilobits = -1 },
            "refs" => valid with { MaxReferenceFrames = 5 },
            "search" => valid with { SearchRange = 2 },
            "fps" => valid with { MaxFrameRate = 0 },
            "slices" => valid with { SlicesPerPicture = 4 },
            "outstanding" => valid with { MaxOutstandingUnits = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        var error = Assert.Throws<ArgumentException>(() => new H264Encoder(options));
        Assert.StartsWith("H264EncoderOptions.", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Invalid frames throw without faulting the encoder or changing its stream.</summary>
    [Fact]
    public void InvalidFramesLeaveEncoderUsable()
    {
        using var encoder = new H264Encoder(H264EncoderTestSupport.Options(64, 48));
        var units = new List<H264EncodedAccessUnit>();
        using (var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 64, 48, 1))
        {
            encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken);
        }

        var first = H264EncoderTestSupport.Take(encoder, units);
        using (var wrongSize = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 64, 64, 2))
        {
            Assert.Equal(CodecErrorKind.InvalidData, Assert.Throws<CodecException>(() => encoder.Encode(wrongSize, units, cancellationToken: TestContext.Current.CancellationToken)).Kind);
        }

        using (var wrongColor = new VideoFrame(64, 48, 7200, H264EncoderTestSupport.TimeBase, VideoColorInfo.Unspecified))
        {
            Assert.Equal(CodecErrorKind.Unsupported, Assert.Throws<CodecException>(() => encoder.Encode(wrongColor, units, cancellationToken: TestContext.Current.CancellationToken)).Kind);
        }

        using (var fullRange = new VideoFrame(64, 48, 7200, H264EncoderTestSupport.TimeBase, VideoColorInfo.Bt709 with { FullRange = true }))
        {
            Assert.Equal(CodecErrorKind.Unsupported, Assert.Throws<CodecException>(() => encoder.Encode(fullRange, units, cancellationToken: TestContext.Current.CancellationToken)).Kind);
        }

        using (var stale = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 64, 48, 1))
        {
            Assert.Equal(CodecErrorKind.InvalidData, Assert.Throws<CodecException>(() => encoder.Encode(stale, units, cancellationToken: TestContext.Current.CancellationToken)).Kind);
        }

        var disposed = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 64, 48, 2);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => encoder.Encode(disposed, units, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(units);
        Assert.False(encoder.IsFaulted);

        var rest = new List<EncodedFrame> { first };
        rest.AddRange(H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 5, 2));
        H264EncoderTestSupport.AssertMatchesReconstruction(rest, H264EncoderTestSupport.DecodeManaged(encoder.DecoderConfigurationRecord, rest));
    }

    /// <summary>A failure while coding faults the encoder; it then refuses work but can be disposed.</summary>
    [Fact]
    public void CodingFailureFaults()
    {
        var hooks = new H264EncoderTestHooks { Control = static i => i == 1 ? new H264PictureControl { ForcePcm = static _ => throw new InvalidOperationException("injected") } : null };
        using var encoder = new H264Encoder(H264EncoderTestSupport.Options(64, 48), hooks);
        var units = new List<H264EncodedAccessUnit>();
        using (var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 64, 48, 0))
        {
            encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken);
        }

        units[0].Dispose();
        units.Clear();
        using (var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 64, 48, 1))
        {
            Assert.Equal("injected", Assert.Throws<InvalidOperationException>(() => encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken)).Message);
            Assert.True(encoder.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Throws<InvalidOperationException>(() => encoder.Flush(units));
            Assert.Throws<InvalidOperationException>(() => encoder.Reconfigure(encoder.Options));
        }

        Assert.Empty(units);
        Assert.Equal(0, encoder.OutstandingUnits);
    }

    /// <summary>Cancellation drops the frame and leaves references and rate control as they were.</summary>
    [Fact]
    public void CancellationLeavesStreamIntact()
    {
        using var encoder = new H264Encoder(H264EncoderTestSupport.Options(96, 64) with { MaxReferenceFrames = 2 });
        var frames = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 4);
        var units = new List<H264EncodedAccessUnit>();
        using (var cancelled = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 96, 64, 4))
        {
            Assert.ThrowsAny<OperationCanceledException>(() => encoder.Encode(cancelled, units, cancellationToken: new CancellationToken(true)));
        }

        Assert.Empty(units);
        Assert.False(encoder.IsFaulted);
        frames.AddRange(H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 6, 5));
        Assert.Single(frames, static f => f.IsKeyframe);
        H264EncoderTestSupport.AssertMatchesReconstruction(frames, H264EncoderTestSupport.DecodeManaged(encoder.DecoderConfigurationRecord, frames));

        using var reference = new H264Encoder(H264EncoderTestSupport.Options(96, 64) with { MaxReferenceFrames = 2 });
        var expected = H264EncoderTestSupport.EncodeAll(reference, SyntheticPattern.Motion, 4);
        expected.AddRange(H264EncoderTestSupport.EncodeAll(reference, SyntheticPattern.Motion, 6, 5));
        Assert.Equal(expected.Select(static f => Convert.ToHexString(f.Data)), frames.Select(static f => Convert.ToHexString(f.Data)));
    }

    /// <summary>The outstanding-unit budget is enforced, released by disposal, and disposal is idempotent.</summary>
    [Fact]
    public void OutstandingUnitsAreBounded()
    {
        using var encoder = new H264Encoder(H264EncoderTestSupport.Options(64, 48) with { MaxOutstandingUnits = 2 });
        var units = new List<H264EncodedAccessUnit>();
        for (var i = 0; i < 2; i++)
        {
            using var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Gradient, 64, 48, i);
            encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, encoder.OutstandingUnits);
        using (var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Gradient, 64, 48, 2))
        {
            Assert.Equal(CodecErrorKind.ResourceLimit, Assert.Throws<CodecException>(() => encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken)).Kind);
            Assert.False(encoder.IsFaulted);
            units[0].Dispose();
            units[0].Dispose();
            Assert.Throws<ObjectDisposedException>(() => units[0].Data);
            Assert.Equal(1, encoder.OutstandingUnits);
            encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, units.Count);
        foreach (var unit in units)
        {
            unit.Dispose();
        }

        Assert.Equal(0, encoder.OutstandingUnits);
    }

    /// <summary>IDR pictures fall exactly on the keyframe interval in media time, with irregular frame timing too.</summary>
    [Fact]
    public void IdrCadenceIsExact()
    {
        using var encoder = new H264Encoder(H264EncoderTestSupport.Options(64, 48) with { KeyframeInterval = TimeSpan.FromSeconds(2) });
        var units = new List<H264EncodedAccessUnit>();
        var timestamps = new List<long>();
        long t = 0;
        for (var i = 0; i < 150; i++)
        {
            t += 3000 + ((i * 7919) % 1800) - 900;
            timestamps.Add(t);
        }

        var expected = new List<bool>();
        long? last = null;
        foreach (var timestamp in timestamps)
        {
            var idr = last is null || timestamp - last >= 180000;
            expected.Add(idr);
            last = idr ? timestamp : last;
        }

        var actual = new List<bool>();
        var forced = new List<long>();
        for (var i = 0; i < timestamps.Count; i++)
        {
            using var frame = new VideoFrame(64, 48, timestamps[i], H264EncoderTestSupport.TimeBase, VideoColorInfo.Bt709);
            H264EncoderTestSupport.Fill(frame, SyntheticPattern.Motion, i);
            encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken);
            using var unit = units[0];
            units.Clear();
            Assert.Equal(timestamps[i], unit.Timestamp);
            actual.Add(unit.IsKeyframe);
        }

        Assert.Equal(expected, actual);
        Assert.True(expected.Count(static k => k) >= 3);

        using (var frame = new VideoFrame(64, 48, t + 3000, H264EncoderTestSupport.TimeBase, VideoColorInfo.Bt709))
        {
            H264EncoderTestSupport.Fill(frame, SyntheticPattern.Motion, 0);
            encoder.Encode(frame, units, forceKeyframe: true, cancellationToken: TestContext.Current.CancellationToken);
        }

        using var forcedUnit = units[0];
        Assert.True(forcedUnit.IsKeyframe);
    }

    /// <summary>Timestamps in another time base are rescaled to 1/90000.</summary>
    [Fact]
    public void RescalesTimestamps()
    {
        using var encoder = new H264Encoder(H264EncoderTestSupport.Options(64, 48));
        var units = new List<H264EncodedAccessUnit>();
        foreach (var ms in new long[] { 0, 40, 80, 1000 })
        {
            using var frame = new VideoFrame(64, 48, ms, new MediaTimeBase(1, 1000), VideoColorInfo.Bt709);
            H264EncoderTestSupport.Fill(frame, SyntheticPattern.Gradient, (int)ms);
            encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal([0L, 3600, 7200, 90000], units.Select(static u => u.Timestamp));
        foreach (var unit in units)
        {
            unit.Dispose();
        }
    }

    /// <summary>Flush and reset start a new stream at an IDR picture that may restart timestamps; reconfigure regenerates the parameter sets.</summary>
    [Fact]
    public void FlushResetAndReconfigure()
    {
        using var encoder = new H264Encoder(H264EncoderTestSupport.Options(96, 64));
        var first = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 5);
        var units = new List<H264EncodedAccessUnit>();
        encoder.Flush(units);
        Assert.Empty(units);
        var second = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 5);
        Assert.True(second[0].IsKeyframe);
        Assert.Equal(first[0].Timestamp, second[0].Timestamp);
        H264EncoderTestSupport.AssertMatchesReconstruction(second, H264EncoderTestSupport.DecodeManaged(encoder.DecoderConfigurationRecord, second));

        encoder.Reset();
        var third = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 3);
        Assert.True(third[0].IsKeyframe);

        var oldAvcC = encoder.DecoderConfigurationRecord.ToArray();
        Assert.Equal(1, encoder.ConfigurationVersion);
        Assert.Throws<ArgumentException>(() => encoder.Reconfigure(encoder.Options with { Width = 3 }));
        Assert.Equal(1, encoder.ConfigurationVersion);
        encoder.Reconfigure(H264EncoderTestSupport.Options(160, 96) with { MaxReferenceFrames = 2 });
        Assert.Equal(2, encoder.ConfigurationVersion);
        Assert.NotEqual(oldAvcC, encoder.DecoderConfigurationRecord.ToArray());
        var resized = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Detail, 6);
        Assert.True(resized[0].IsKeyframe);
        H264EncoderTestSupport.AssertMatchesReconstruction(resized, H264EncoderTestSupport.DecodeManaged(encoder.DecoderConfigurationRecord, resized));

        var units2 = new List<H264EncodedAccessUnit>();
        using var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Detail, 160, 96, 6);
        encoder.Encode(frame, units2, cancellationToken: TestContext.Current.CancellationToken);
        using var unit = units2[0];
        Assert.Equal(2, unit.ConfigurationVersion);
    }

    /// <summary>After disposal every member that does work throws, and disposal is idempotent.</summary>
    [Fact]
    public void DisposedEncoderThrows()
    {
        var encoder = new H264Encoder(H264EncoderTestSupport.Options(64, 48));
        encoder.Dispose();
        encoder.Dispose();
        using var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Flat, 64, 48, 0);
        Assert.Throws<ObjectDisposedException>(() => encoder.Encode(frame, [], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => encoder.Flush([]));
        Assert.Throws<ObjectDisposedException>(encoder.Reset);
        Assert.Throws<ObjectDisposedException>(() => encoder.Reconfigure(encoder.Options));
    }

    /// <summary>The same input always yields the same bytes, and memory stays within its stated bounds.</summary>
    [Fact]
    public void DeterministicAndBounded()
    {
        var options = H264EncoderTestSupport.Options(176, 144, 4) with { MaxReferenceFrames = 3, SlicesPerPicture = 2 };
        using var a = new H264Encoder(options);
        using var b = new H264Encoder(options);
        var first = H264EncoderTestSupport.EncodeAll(a, SyntheticPattern.Noise, 6);
        first.AddRange(H264EncoderTestSupport.EncodeAll(a, SyntheticPattern.Cuts, 20, 6));
        var second = H264EncoderTestSupport.EncodeAll(b, SyntheticPattern.Noise, 6);
        second.AddRange(H264EncoderTestSupport.EncodeAll(b, SyntheticPattern.Cuts, 20, 6));
        Assert.Equal(first.Select(static f => Convert.ToHexString(f.Data)), second.Select(static f => Convert.ToHexString(f.Data)));

        var pictureBytes = 4L * (((176 * 144) + (2 * 88 * 72)) + (99 * 160));
        Assert.True(a.PictureBytes <= pictureBytes * 2, $"Reconstruction memory {a.PictureBytes} exceeds the bound.");
        Assert.True(a.BufferBytes <= a.MaxAccessUnitBytes);
        Assert.All(first, f => Assert.True(f.Data.Length <= a.MaxAccessUnitBytes + (a.MaxAccessUnitBytes / 2), "An access unit exceeded its RBSP bound plus emulation prevention."));
        Assert.Contains(first, static f => f.Statistics.PcmMacroblocks > 0 || f.Data.Length > 99 * 300);
    }
}
