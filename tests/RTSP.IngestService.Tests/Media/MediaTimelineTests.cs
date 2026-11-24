// Tests of the per-publisher media timeline (32-bit unwrap, deltas, regression and jump classification), of the
// untrusted publisher metadata extraction, and of MediaOptions validation.

using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Amf;

namespace RTSP.IngestService.Tests.Media;

/// <summary>Tests for <see cref="MediaTimeline"/>.</summary>
public sealed class MediaTimelineTests
{
    /// <summary>The first timestamp is the origin, not zero; later ones progress by their difference.</summary>
    [Fact]
    public void Unwrap_StartsAtFirstTimestamp()
    {
        var timeline = new MediaTimeline(TimeSpan.FromSeconds(10));

        Assert.Equal(5000, timeline.Unwrap(5000));
        Assert.Equal(5040, timeline.Unwrap(5040));
        Assert.Equal(5020, timeline.Unwrap(5020));
    }

    /// <summary>Rollover from 0xFFFFFFFF to 0 continues forward; repeated rollovers accumulate.</summary>
    [Fact]
    public void Unwrap_HandlesRollover()
    {
        var timeline = new MediaTimeline(TimeSpan.FromSeconds(10));

        Assert.Equal(uint.MaxValue, timeline.Unwrap(uint.MaxValue));
        Assert.Equal(1L << 32, timeline.Unwrap(0));
        Assert.Equal((1L << 32) + 0x7FFFFFFF, timeline.Unwrap(0x7FFFFFFF));
        Assert.Equal((1L << 32) + 0xBFFFFFFF, timeline.Unwrap(0xBFFFFFFF));
        Assert.Equal((1L << 32) + 0xFFFFFFFF, timeline.Unwrap(0xFFFFFFFF));
        Assert.Equal(2L << 32, timeline.Unwrap(0));
    }

    /// <summary>A backward step across the wrap point stays a small backward step.</summary>
    [Fact]
    public void Unwrap_BackwardAcrossZero()
    {
        var timeline = new MediaTimeline(TimeSpan.FromSeconds(10));

        timeline.Unwrap(10);

        Assert.Equal(-6, timeline.Unwrap(0xFFFFFFFA));
    }

    /// <summary>Deltas are per track and classified against the threshold boundary.</summary>
    [Fact]
    public void Advance_ClassifiesPerTrack()
    {
        var timeline = new MediaTimeline(TimeSpan.FromSeconds(1));

        Assert.Equal(TimestampDiscontinuity.None, timeline.Advance(MediaTrack.Video, 100, out var first));
        Assert.Null(first);
        Assert.Equal(TimestampDiscontinuity.None, timeline.Advance(MediaTrack.Audio, 5000, out _));
        Assert.Equal(TimestampDiscontinuity.None, timeline.Advance(MediaTrack.Video, 1100, out var exact));
        Assert.Equal(1000, exact);
        Assert.Equal(TimestampDiscontinuity.Jump, timeline.Advance(MediaTrack.Video, 2101, out _));
        Assert.Equal(TimestampDiscontinuity.None, timeline.Advance(MediaTrack.Video, 2101, out var zero));
        Assert.Equal(0, zero);
        Assert.Equal(TimestampDiscontinuity.Regression, timeline.Advance(MediaTrack.Video, 2100, out var back));
        Assert.Equal(-1, back);
        Assert.Equal(TimestampDiscontinuity.Regression, timeline.Advance(MediaTrack.Audio, 4999, out _));
    }
}

/// <summary>Tests for <see cref="PublisherMetadata"/>.</summary>
public sealed class PublisherMetadataTests
{
    /// <summary>Valid values are kept.</summary>
    [Fact]
    public void ValidValues_AreKept()
    {
        var metadata = PublisherMetadata.FromProperties(
        [
            new("width", AmfValue.FromNumber(1920)),
            new("height", AmfValue.FromNumber(1080)),
            new("framerate", AmfValue.FromNumber(29.97)),
            new("videocodecid", AmfValue.FromNumber(7)),
            new("audiocodecid", AmfValue.FromNumber(10)),
            new("audiosamplerate", AmfValue.FromNumber(48000)),
            new("stereo", AmfValue.FromBoolean(true)),
            new("duration", AmfValue.FromNumber(0)),
            new("videodatarate", AmfValue.FromNumber(2500)),
            new("audiodatarate", AmfValue.FromNumber(128)),
            new("encoder", AmfValue.FromString("Lavf61.7.100")),
        ]);

        Assert.Equal(1920, metadata.Width);
        Assert.Equal(1080, metadata.Height);
        Assert.Equal(29.97, metadata.FrameRate);
        Assert.Equal(7, metadata.VideoCodecId);
        Assert.Equal(10, metadata.AudioCodecId);
        Assert.Equal(48000, metadata.AudioSampleRate);
        Assert.Equal(2, metadata.AudioChannels);
        Assert.Equal(TimeSpan.Zero, metadata.Duration);
        Assert.Equal(2500, metadata.VideoDataRate);
        Assert.Equal(128, metadata.AudioDataRate);
        Assert.Equal("Lavf61.7.100", metadata.Encoder);
    }

    /// <summary>Out-of-range, non-finite, fractional, wrongly typed and unprintable values are discarded.</summary>
    [Fact]
    public void InvalidValues_AreDiscarded()
    {
        var metadata = PublisherMetadata.FromProperties(
        [
            new("width", AmfValue.FromNumber(0)),
            new("height", AmfValue.FromNumber(16385)),
            new("framerate", AmfValue.FromNumber(double.NaN)),
            new("videocodecid", AmfValue.FromString("avc1")),
            new("audiocodecid", AmfValue.FromNumber(7.5)),
            new("audiosamplerate", AmfValue.FromNumber(double.PositiveInfinity)),
            new("audiochannels", AmfValue.FromNumber(65)),
            new("duration", AmfValue.FromNumber(-1)),
            new("videodatarate", AmfValue.FromNumber(2e6)),
            new("encoder", AmfValue.FromString("bad\nvalue")),
        ]);

        Assert.Equal(PublisherMetadata.Empty, metadata);
    }

    /// <summary><c>audiochannels</c> takes precedence over <c>stereo</c>; overlong encoders are discarded.</summary>
    [Fact]
    public void Channels_AndEncoderLength()
    {
        var metadata = PublisherMetadata.FromProperties(
        [
            new("audiochannels", AmfValue.FromNumber(6)),
            new("stereo", AmfValue.FromBoolean(false)),
            new("encoder", AmfValue.FromString(new string('x', PublisherMetadata.MaxEncoderLength + 1))),
        ]);

        Assert.Equal(6, metadata.AudioChannels);
        Assert.Null(metadata.Encoder);
        Assert.Equal(1, PublisherMetadata.FromProperties([new("stereo", AmfValue.FromBoolean(false))]).AudioChannels);
    }
}

/// <summary>Tests for <see cref="MediaOptions"/> validation.</summary>
public sealed class MediaOptionsTests
{
    /// <summary>Defaults are valid.</summary>
    [Fact]
    public void Defaults_AreValid()
    {
        Assert.True(new MediaOptionsValidator().Validate(null, new MediaOptions()).Succeeded);
    }

    /// <summary>Each limit is validated at its boundaries.</summary>
    /// <param name="maxSampleSize">MaxSampleSize.</param>
    /// <param name="maxNalUnits">MaxNalUnitsPerSample.</param>
    /// <param name="threshold">DiscontinuityThreshold.</param>
    /// <param name="valid">Whether valid.</param>
    [Theory]
    [InlineData(1024, 1, "00:00:01", true)]
    [InlineData(16777215, 4096, "01:00:00", true)]
    [InlineData(1023, 256, "00:00:10", false)]
    [InlineData(16777216, 256, "00:00:10", false)]
    [InlineData(8388608, 0, "00:00:10", false)]
    [InlineData(8388608, 4097, "00:00:10", false)]
    [InlineData(8388608, 256, "00:00:00.999", false)]
    [InlineData(8388608, 256, "01:00:00.001", false)]
    public void Limits_AreValidated(int maxSampleSize, int maxNalUnits, string threshold, bool valid)
    {
        var options = new MediaOptions
        {
            MaxSampleSize = maxSampleSize,
            MaxNalUnitsPerSample = maxNalUnits,
            DiscontinuityThreshold = TimeSpan.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture),
        };

        ValidateOptionsResult result = new MediaOptionsValidator().Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
    }
}
