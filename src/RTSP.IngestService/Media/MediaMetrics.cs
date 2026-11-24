// System.Diagnostics.Metrics instruments for media processing, created from the host's IMeterFactory. Tags are
// fixed enumerations (track, error kind, discontinuity kind); publisher names, stream identities, codec strings and
// metadata values are never used as tags.

using System.Diagnostics.Metrics;

namespace RTSP.IngestService.Media;

/// <summary>
/// Media processing metrics, published on the <see cref="MeterName"/> meter.
/// </summary>
/// <remarks>Thread-safe.</remarks>
internal sealed class MediaMetrics
{
    /// <summary>Name of the meter.</summary>
    public const string MeterName = "RTSP.IngestService.Media";

    /// <summary>Delivered samples by track.</summary>
    private readonly Counter<long> _samples;

    /// <summary>Delivered video samples marked as keyframes.</summary>
    private readonly Counter<long> _keyframes;

    /// <summary>Delivered sample bytes by track.</summary>
    private readonly Counter<long> _sampleBytes;

    /// <summary>Accepted codec configurations by track.</summary>
    private readonly Counter<long> _configurations;

    /// <summary>Dropped media packets by track and kind.</summary>
    private readonly Counter<long> _errors;

    /// <summary>Timestamp discontinuities by track and kind.</summary>
    private readonly Counter<long> _discontinuities;

    /// <summary>Sessions with media state.</summary>
    private readonly UpDownCounter<long> _activeSessions;

    /// <summary>Initializes a new instance of the <see cref="MediaMetrics"/> class.</summary>
    /// <param name="meterFactory">Factory owning the meter's lifetime.</param>
    public MediaMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);

        _samples = meter.CreateCounter<long>("media.samples", "{sample}", "Encoded samples delivered downstream; tag track.");
        _keyframes = meter.CreateCounter<long>("media.keyframes", "{sample}", "Delivered video samples marked as keyframes.");
        _sampleBytes = meter.CreateCounter<long>("media.sample_bytes", "By", "Encoded sample bytes delivered downstream; tag track.");
        _configurations = meter.CreateCounter<long>("media.configurations", "{configuration}", "New valid codec configurations; tag track.");
        _errors = meter.CreateCounter<long>("media.errors", "{packet}", "Media packets dropped instead of delivered; tags track and kind.");
        _discontinuities = meter.CreateCounter<long>("media.timestamp_discontinuities", "{sample}", "Delivered samples with a timestamp regression or jump; tags track and kind.");
        _activeSessions = meter.CreateUpDownCounter<long>("media.sessions.active", "{session}", "Published streams with media processing state.");
    }

    /// <summary>Records a session whose media state was created.</summary>
    public void SessionStarted() => _activeSessions.Add(1);

    /// <summary>Records a session whose media state was released.</summary>
    public void SessionEnded() => _activeSessions.Add(-1);

    /// <summary>Records a delivered sample.</summary>
    /// <param name="track">Its track.</param>
    /// <param name="length">Its data length.</param>
    /// <param name="keyframe">Whether it is a keyframe.</param>
    public void Sample(MediaTrack track, int length, bool keyframe)
    {
        var tag = TrackTag(track);
        _samples.Add(1, tag);
        _sampleBytes.Add(length, tag);
        if (keyframe)
        {
            _keyframes.Add(1);
        }
    }

    /// <summary>Records an accepted configuration.</summary>
    /// <param name="track">Its track.</param>
    public void Configuration(MediaTrack track) => _configurations.Add(1, TrackTag(track));

    /// <summary>Records a dropped packet.</summary>
    /// <param name="track">Its track.</param>
    /// <param name="kind">Why it was dropped.</param>
    public void Error(MediaTrack track, MediaErrorKind kind) =>
        _errors.Add(1, TrackTag(track), new KeyValuePair<string, object?>("kind", Name(kind)));

    /// <summary>Records a timestamp discontinuity.</summary>
    /// <param name="track">Its track.</param>
    /// <param name="kind">Its kind; not <see cref="TimestampDiscontinuity.None"/>.</param>
    public void Discontinuity(MediaTrack track, TimestampDiscontinuity kind) =>
        _discontinuities.Add(1, TrackTag(track), new KeyValuePair<string, object?>("kind", kind == TimestampDiscontinuity.Regression ? "regression" : "jump"));

    /// <summary>Returns the track tag.</summary>
    /// <param name="track">The track.</param>
    /// <returns>The tag.</returns>
    private static KeyValuePair<string, object?> TrackTag(MediaTrack track) => new("track", track == MediaTrack.Video ? "video" : "audio");

    /// <summary>Returns the snake-case metric value of <paramref name="kind"/>.</summary>
    /// <param name="kind">The error kind.</param>
    /// <returns>The tag value.</returns>
    private static string Name(MediaErrorKind kind) => kind switch
    {
        MediaErrorKind.UnsupportedCodec => "unsupported_codec",
        MediaErrorKind.MalformedPacket => "malformed_packet",
        MediaErrorKind.InvalidConfiguration => "invalid_configuration",
        MediaErrorKind.MissingConfiguration => "missing_configuration",
        MediaErrorKind.SampleTooLarge => "sample_too_large",
        _ => "too_many_nal_units",
    };
}
