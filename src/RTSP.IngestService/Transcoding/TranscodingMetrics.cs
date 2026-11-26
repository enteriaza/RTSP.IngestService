// System.Diagnostics.Metrics instruments for the transcoding stage, created from the host's IMeterFactory. Every tag
// value comes from a fixed enumeration in this file (track, reason, stage, error kind, source codec); stream names,
// application names, addresses, keys, incarnation IDs, configuration values and publisher-supplied text are never
// used as tags, so cardinality is bounded regardless of what publishers send.

using System.Diagnostics.Metrics;
using RTSP.IngestService.Media;

namespace RTSP.IngestService.Transcoding;

/// <summary>Why a publish was refused before or during transcoding.</summary>
internal enum TranscodingRejectionReason
{
    /// <summary>All transcoding sessions are in use.</summary>
    Capacity,

    /// <summary>Transcoding is disabled, so no publish can produce canonical output.</summary>
    Disabled,

    /// <summary>The codec is not supported.</summary>
    UnsupportedCodec,

    /// <summary>The codec is excluded by configuration.</summary>
    CodecNotAccepted,

    /// <summary>The picture is too large.</summary>
    ResolutionLimit,

    /// <summary>The frame rate is too high.</summary>
    FrameRateLimit,
}

/// <summary>Why a source sample was dropped before reaching the decoder, or an output sample after the encoder.</summary>
internal enum TranscodingDropReason
{
    /// <summary>The session queue was full.</summary>
    QueueFull,

    /// <summary>The aggregate buffer budget was exhausted.</summary>
    BudgetExhausted,

    /// <summary>A video frame that depends on a dropped or missing reference, waiting for the next keyframe.</summary>
    AwaitingKeyframe,

    /// <summary>The sample's timestamp cannot be represented for the transcoder.</summary>
    Timestamp,

    /// <summary>Encoder output that overlaps media already delivered by the previous transcoder process.</summary>
    Overlap,
}

/// <summary>Which part of the transcoder failed.</summary>
internal enum TranscodingStage
{
    /// <summary>The decoder or demuxer.</summary>
    Decoder,

    /// <summary>The filter graph or encoder.</summary>
    Encoder,

    /// <summary>The process itself (start failure, unexpected exit, invalid output).</summary>
    Process,
}

/// <summary>Why a transcoder process was started.</summary>
internal enum TranscoderStartReason
{
    /// <summary>The session's first process.</summary>
    Initial,

    /// <summary>A track configuration or the colour description changed, or a track appeared or disappeared.</summary>
    Configuration,

    /// <summary>A timestamp regression or jump.</summary>
    Discontinuity,

    /// <summary>The process's timestamp range was exhausted.</summary>
    Timeline,
}

/// <summary>Transcoding metrics, published on the <see cref="MeterName"/> meter.</summary>
/// <remarks>Thread-safe.</remarks>
internal sealed class TranscodingMetrics
{
    /// <summary>Name of the meter.</summary>
    public const string MeterName = "RTSP.IngestService.Transcoding";

    /// <summary>Active transcoding sessions.</summary>
    private readonly UpDownCounter<long> _activeSessions;

    /// <summary>Transcoder processes started, by reason.</summary>
    private readonly Counter<long> _processStarts;

    /// <summary>Transcoder initialisation failures, by stage.</summary>
    private readonly Counter<long> _initializationFailures;

    /// <summary>Transcoder runtime failures, by stage.</summary>
    private readonly Counter<long> _failures;

    /// <summary>Publishes refused, by reason.</summary>
    private readonly Counter<long> _rejections;

    /// <summary>Source frames written to a decoder, by track and source codec.</summary>
    private readonly Counter<long> _inputFrames;

    /// <summary>Canonical frames received from the encoder, by track.</summary>
    private readonly Counter<long> _outputFrames;

    /// <summary>Canonical audio sample frames (per channel) received from the encoder.</summary>
    private readonly Counter<long> _outputAudioSamples;

    /// <summary>Frames dropped, by track and reason.</summary>
    private readonly Counter<long> _droppedFrames;

    /// <summary>Source packets dropped by the parser, by track and kind.</summary>
    private readonly Counter<long> _sourceErrors;

    /// <summary>Times a session queue had no room, by track.</summary>
    private readonly Counter<long> _saturations;

    /// <summary>Bytes held in session queues.</summary>
    private readonly UpDownCounter<long> _bufferedBytes;

    /// <summary>Time from submitting a source video frame to receiving its encoded frame.</summary>
    private readonly Histogram<double> _latency;

    /// <summary>Initializes a new instance of the <see cref="TranscodingMetrics"/> class.</summary>
    /// <param name="meterFactory">Factory owning the meter's lifetime.</param>
    public TranscodingMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);

        _activeSessions = meter.CreateUpDownCounter<long>("transcoding.sessions.active", "{session}", "Published streams holding a transcoding slot.");
        _processStarts = meter.CreateCounter<long>("transcoding.process.starts", "{process}", "Transcoder processes started; tag reason.");
        _initializationFailures = meter.CreateCounter<long>("transcoding.initialization.failures", "{process}", "Transcoder processes that failed before producing output; tag stage.");
        _failures = meter.CreateCounter<long>("transcoding.failures", "{process}", "Transcoder processes that failed after producing output; tag stage.");
        _rejections = meter.CreateCounter<long>("transcoding.rejections", "{session}", "Publishes refused or ended by transcoding admission or codec policy; tag reason.");
        _inputFrames = meter.CreateCounter<long>("transcoding.frames.decoded", "{frame}", "Source frames submitted to a decoder; tags track and codec.");
        _outputFrames = meter.CreateCounter<long>("transcoding.frames.encoded", "{frame}", "Canonical frames produced by the encoder; tag track.");
        _outputAudioSamples = meter.CreateCounter<long>("transcoding.audio.samples", "{sample}", "Canonical audio samples per channel produced by the encoder.");
        _droppedFrames = meter.CreateCounter<long>("transcoding.frames.dropped", "{frame}", "Frames dropped by overload policy or timestamp handling; tags track and reason.");
        _sourceErrors = meter.CreateCounter<long>("transcoding.errors", "{packet}", "Source packets dropped as malformed or unusable before decoding; tags track and kind.");
        _saturations = meter.CreateCounter<long>("transcoding.queue.saturations", "{event}", "Source samples that found their session queue or the aggregate budget full; tag track.");
        _bufferedBytes = meter.CreateUpDownCounter<long>("transcoding.queue.bytes", "By", "Source bytes held in session queues.");
        _latency = meter.CreateHistogram<double>("transcoding.latency", "s", "Time from submitting a source video frame to receiving the encoded frame with the same timestamp.");
    }

    /// <summary>Records a session that took a slot.</summary>
    public void SessionStarted() => _activeSessions.Add(1);

    /// <summary>Records a session that released its slot.</summary>
    public void SessionEnded() => _activeSessions.Add(-1);

    /// <summary>Records a process start.</summary>
    /// <param name="reason">Why.</param>
    public void ProcessStarted(TranscoderStartReason reason) => _processStarts.Add(1, new KeyValuePair<string, object?>("reason", Name(reason)));

    /// <summary>Records a process failure.</summary>
    /// <param name="stage">The failing stage.</param>
    /// <param name="initialization">Whether it failed before producing output.</param>
    public void Failure(TranscodingStage stage, bool initialization) =>
        (initialization ? _initializationFailures : _failures).Add(1, new KeyValuePair<string, object?>("stage", Name(stage)));

    /// <summary>Records a refused publish.</summary>
    /// <param name="reason">Why.</param>
    public void Rejection(TranscodingRejectionReason reason) => _rejections.Add(1, new KeyValuePair<string, object?>("reason", Name(reason)));

    /// <summary>Records a source frame written to a decoder.</summary>
    /// <param name="track">Its track.</param>
    /// <param name="codec">Its codec name, from <see cref="CodecName(SourceVideoCodec)"/> or <see cref="CodecName(SourceAudioCodec)"/>.</param>
    public void InputFrame(MediaTrack track, string codec) => _inputFrames.Add(1, TrackTag(track), new KeyValuePair<string, object?>("codec", codec));

    /// <summary>Records an encoded frame.</summary>
    /// <param name="track">Its track.</param>
    public void OutputFrame(MediaTrack track)
    {
        _outputFrames.Add(1, TrackTag(track));
        if (track == MediaTrack.Audio)
        {
            _outputAudioSamples.Add(1024);
        }
    }

    /// <summary>Records a dropped frame.</summary>
    /// <param name="track">Its track.</param>
    /// <param name="reason">Why.</param>
    public void Dropped(MediaTrack track, TranscodingDropReason reason) => _droppedFrames.Add(1, TrackTag(track), new KeyValuePair<string, object?>("reason", Name(reason)));

    /// <summary>Records a source packet the parser dropped.</summary>
    /// <param name="track">Its track.</param>
    /// <param name="kind">Why.</param>
    public void SourceError(MediaTrack track, MediaErrorKind kind) => _sourceErrors.Add(1, TrackTag(track), new KeyValuePair<string, object?>("kind", Name(kind)));

    /// <summary>Records a full queue.</summary>
    /// <param name="track">The track of the sample that found it full.</param>
    public void Saturated(MediaTrack track) => _saturations.Add(1, TrackTag(track));

    /// <summary>Records a change in queued bytes.</summary>
    /// <param name="delta">The change.</param>
    public void Buffered(long delta) => _bufferedBytes.Add(delta);

    /// <summary>Records a frame's transcoding latency.</summary>
    /// <param name="seconds">The latency.</param>
    public void Latency(double seconds) => _latency.Record(seconds);

    /// <summary>Returns the codec tag value.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>The value.</returns>
    public static string CodecName(SourceVideoCodec codec) => codec switch
    {
        SourceVideoCodec.H264 => "h264",
        SourceVideoCodec.Hevc => "hevc",
        SourceVideoCodec.Av1 => "av1",
        _ => "vp9",
    };

    /// <summary>Returns the codec tag value.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>The value.</returns>
    public static string CodecName(SourceAudioCodec codec) => codec switch
    {
        SourceAudioCodec.Aac => "aac",
        SourceAudioCodec.Mp3 => "mp3",
        SourceAudioCodec.Opus => "opus",
        SourceAudioCodec.Flac => "flac",
        SourceAudioCodec.Ac3 => "ac3",
        _ => "eac3",
    };

    /// <summary>Returns the track tag.</summary>
    /// <param name="track">The track.</param>
    /// <returns>The tag.</returns>
    private static KeyValuePair<string, object?> TrackTag(MediaTrack track) => new("track", track == MediaTrack.Video ? "video" : "audio");

    /// <summary>Returns the tag value.</summary>
    /// <param name="reason">The value.</param>
    /// <returns>The text.</returns>
    private static string Name(TranscoderStartReason reason) => reason switch
    {
        TranscoderStartReason.Initial => "initial",
        TranscoderStartReason.Configuration => "configuration",
        TranscoderStartReason.Discontinuity => "discontinuity",
        _ => "timeline",
    };

    /// <summary>Returns the tag value.</summary>
    /// <param name="stage">The value.</param>
    /// <returns>The text.</returns>
    private static string Name(TranscodingStage stage) => stage switch
    {
        TranscodingStage.Decoder => "decoder",
        TranscodingStage.Encoder => "encoder",
        _ => "process",
    };

    /// <summary>Returns the tag value.</summary>
    /// <param name="reason">The value.</param>
    /// <returns>The text.</returns>
    private static string Name(TranscodingRejectionReason reason) => reason switch
    {
        TranscodingRejectionReason.Capacity => "capacity",
        TranscodingRejectionReason.Disabled => "disabled",
        TranscodingRejectionReason.UnsupportedCodec => "unsupported_codec",
        TranscodingRejectionReason.CodecNotAccepted => "codec_not_accepted",
        TranscodingRejectionReason.ResolutionLimit => "resolution_limit",
        _ => "frame_rate_limit",
    };

    /// <summary>Returns the tag value.</summary>
    /// <param name="reason">The value.</param>
    /// <returns>The text.</returns>
    private static string Name(TranscodingDropReason reason) => reason switch
    {
        TranscodingDropReason.QueueFull => "queue_full",
        TranscodingDropReason.BudgetExhausted => "budget_exhausted",
        TranscodingDropReason.AwaitingKeyframe => "awaiting_keyframe",
        TranscodingDropReason.Timestamp => "timestamp",
        _ => "overlap",
    };

    /// <summary>Returns the tag value.</summary>
    /// <param name="kind">The value.</param>
    /// <returns>The text.</returns>
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
