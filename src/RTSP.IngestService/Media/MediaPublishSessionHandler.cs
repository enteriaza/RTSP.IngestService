// The media processing stage: an IPublishSessionHandler that turns each published stream's opaque RTMP audio and
// video messages into validated H.264 and AAC samples on a per-publisher timeline and hands them to an
// IMediaSessionHandler. It keeps one state object per session, processes a session's messages strictly in order and
// awaits the downstream handler for each, so Phase 2 backpressure applies unchanged. Malformed media is dropped and
// reported without affecting the session or any other publisher.

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Media;

/// <summary>
/// Parses published audio and video into <see cref="EncodedVideoSample"/> and <see cref="EncodedAudioSample"/>
/// instances and delivers them, with stream-description changes and dropped-packet reports, to an
/// <see cref="IMediaSessionHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ownership: each sample takes over the <see cref="PublishedMessage"/> that carried it, so payloads are not copied.
/// Messages that do not become samples (sequence headers, data messages, dropped packets) are disposed here before
/// the call returns. Codec configuration records are copied because they outlive their message.
/// </para>
/// <para>
/// Errors: a packet that cannot be delivered is counted, logged (Warning for the first of each kind per track and
/// session, Debug afterwards) and reported through <see cref="IMediaSessionHandler.OnMediaErrorAsync"/>; the session
/// continues. An invalid sequence header clears the track's configuration, so later samples of that track are
/// reported as <see cref="MediaErrorKind.MissingConfiguration"/> until a valid one arrives. Only an exception from
/// the downstream handler ends the session.
/// </para>
/// </remarks>
public sealed partial class MediaPublishSessionHandler : IPublishSessionHandler
{
    /// <summary>The media limits.</summary>
    private readonly MediaOptions _options;

    /// <summary>The downstream consumer.</summary>
    private readonly IMediaSessionHandler _downstream;

    /// <summary>Metrics.</summary>
    private readonly MediaMetrics _metrics;

    /// <summary>Logger.</summary>
    private readonly ILogger<MediaPublishSessionHandler> _logger;

    /// <summary>Stream lifecycle event publisher, if any.</summary>
    private readonly IStreamEventPublisher? _events;

    /// <summary>Clock for event timestamps.</summary>
    private readonly TimeProvider _time;

    /// <summary>State of the active sessions by session ID.</summary>
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();

    /// <summary>Initializes a new instance of the <see cref="MediaPublishSessionHandler"/> class.</summary>
    /// <param name="options">The media limits.</param>
    /// <param name="downstream">The consumer of typed media.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="events">Receives <see cref="StreamEventType.MediaDescribed"/> events, or <see langword="null"/> for none.</param>
    /// <param name="time">Clock for event timestamps; the system clock when <see langword="null"/>.</param>
    internal MediaPublishSessionHandler(
        IOptions<MediaOptions> options,
        IMediaSessionHandler downstream,
        MediaMetrics metrics,
        ILogger<MediaPublishSessionHandler> logger,
        IStreamEventPublisher? events = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _downstream = downstream;
        _metrics = metrics;
        _logger = logger;
        _events = events;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets the number of sessions with media state; for diagnostics and tests.</summary>
    internal int ActiveSessionCount => _sessions.Count;

    /// <summary>Returns the current immutable stream description of an active session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The description, or <see langword="null"/> if the session has no media state.</returns>
    internal MediaStreamInfo? FindStreamInfo(Guid sessionId) => _sessions.TryGetValue(sessionId, out var state) ? state.Media.StreamInfo : null;

    /// <inheritdoc />
    public async ValueTask OnSessionStartedAsync(PublisherSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = new SessionState(new MediaSession(session), new MediaTimeline(_options.DiscontinuityThreshold));
        if (!_sessions.TryAdd(session.SessionId, state))
        {
            throw new InvalidOperationException($"Session {session.SessionId} has already started.");
        }

        _metrics.SessionStarted();
        try
        {
            LogStarted(session.SessionId);
            await _downstream.OnSessionStartedAsync(state.Media, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(session.SessionId);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask OnMessageAsync(PublisherSession session, PublishedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        if (!_sessions.TryGetValue(session.SessionId, out var state))
        {
            throw new InvalidOperationException($"Session {session.SessionId} has not started.");
        }

        var transferred = false;
        try
        {
            switch (message.Kind)
            {
                case PublishedMessageKind.Video:
                    transferred = await ProcessVideoAsync(state, message, cancellationToken).ConfigureAwait(false);
                    break;
                case PublishedMessageKind.Audio:
                    transferred = await ProcessAudioAsync(state, message, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await ProcessDataAsync(state, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            if (!transferred)
            {
                message.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask OnSessionEndedAsync(PublisherSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_sessions.TryGetValue(session.SessionId, out var state))
        {
            return;
        }

        try
        {
            var media = state.Media;
            LogEnded(session.SessionId, media.VideoSampleCount, media.KeyframeCount, media.AudioSampleCount, media.ConfigurationCount, media.ErrorCount, media.DiscontinuityCount);
            await _downstream.OnSessionEndedAsync(media, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Release(session.SessionId);
        }
    }

    /// <summary>Removes a session's state.</summary>
    /// <param name="sessionId">The session.</param>
    private void Release(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _))
        {
            _metrics.SessionEnded();
        }
    }

    /// <summary>Processes a video message.</summary>
    /// <param name="state">The session state.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see langword="true"/> if ownership of <paramref name="message"/> passed to a sample.</returns>
    private async ValueTask<bool> ProcessVideoAsync(SessionState state, PublishedMessage message, CancellationToken cancellationToken)
    {
        var source = message.Timestamp;
        var decodeTimestamp = state.Timeline.Unwrap(source);
        if (!FlvMediaParser.TryParseVideoHeader(message.Payload.Span, out var header, out var errorKind, out var error))
        {
            await ReportAsync(state, MediaTrack.Video, errorKind, source, error, cancellationToken).ConfigureAwait(false);
            return false;
        }

        switch (header.Kind)
        {
            case FlvPacketKind.Command:
                return false;
            case FlvPacketKind.EndOfSequence:
                LogEndOfSequence(state.Media.SessionId);
                return false;
            case FlvPacketKind.SequenceHeader:
                await ConfigureVideoAsync(state, message, cancellationToken).ConfigureAwait(false);
                return false;
        }

        var length = message.Length - FlvMediaParser.VideoHeaderLength;
        var track = state.Media.StreamInfo.Video;
        NalUnit[] nalUnits;
        if (length > _options.MaxSampleSize)
        {
            errorKind = MediaErrorKind.SampleTooLarge;
            error = $"The video sample is {length} bytes; the limit is {_options.MaxSampleSize}.";
        }
        else if (track is null)
        {
            errorKind = MediaErrorKind.MissingConfiguration;
            error = "A video sample arrived before a valid AVC sequence header.";
        }
        else if (FlvMediaParser.TryReadNalUnits(message.Payload.Span[FlvMediaParser.VideoHeaderLength..], track.NalLengthSize, _options.MaxNalUnitsPerSample, out nalUnits, out errorKind, out error))
        {
            var discontinuity = Advance(state, MediaTrack.Video, decodeTimestamp, out var delta);
            var sample = new EncodedVideoSample(message, track, header.IsKeyframe, nalUnits, new MediaTimestamps(source, decodeTimestamp, header.CompositionTimeOffset), state.VideoSequence++, discontinuity, delta);
            state.Media.RecordSample(MediaTrack.Video, header.IsKeyframe, discontinuity);
            _metrics.Sample(MediaTrack.Video, length, header.IsKeyframe);
            try
            {
                await _downstream.OnVideoSampleAsync(state.Media, sample, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                sample.Dispose();
                throw;
            }

            return true;
        }

        await ReportAsync(state, MediaTrack.Video, errorKind, source, error!, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>Processes an audio message.</summary>
    /// <param name="state">The session state.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see langword="true"/> if ownership of <paramref name="message"/> passed to a sample.</returns>
    private async ValueTask<bool> ProcessAudioAsync(SessionState state, PublishedMessage message, CancellationToken cancellationToken)
    {
        var source = message.Timestamp;
        var decodeTimestamp = state.Timeline.Unwrap(source);
        if (!FlvMediaParser.TryParseAudioHeader(message.Payload.Span, out var header, out var errorKind, out var error))
        {
            await ReportAsync(state, MediaTrack.Audio, errorKind, source, error, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (header.Kind == FlvPacketKind.SequenceHeader)
        {
            await ConfigureAudioAsync(state, message, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var length = message.Length - FlvMediaParser.AudioHeaderLength;
        var track = state.Media.StreamInfo.Audio;
        if (length == 0)
        {
            errorKind = MediaErrorKind.MalformedPacket;
            error = "The AAC access unit is empty.";
        }
        else if (length > _options.MaxSampleSize)
        {
            errorKind = MediaErrorKind.SampleTooLarge;
            error = $"The audio sample is {length} bytes; the limit is {_options.MaxSampleSize}.";
        }
        else if (track is null)
        {
            errorKind = MediaErrorKind.MissingConfiguration;
            error = "An audio sample arrived before a valid AAC sequence header.";
        }
        else
        {
            var discontinuity = Advance(state, MediaTrack.Audio, decodeTimestamp, out var delta);
            var sample = new EncodedAudioSample(message, track, new MediaTimestamps(source, decodeTimestamp, 0), state.AudioSequence++, discontinuity, delta);
            state.Media.RecordSample(MediaTrack.Audio, false, discontinuity);
            _metrics.Sample(MediaTrack.Audio, length, false);
            try
            {
                await _downstream.OnAudioSampleAsync(state.Media, sample, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                sample.Dispose();
                throw;
            }

            return true;
        }

        await ReportAsync(state, MediaTrack.Audio, errorKind, source, error, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>Applies an AVC sequence header.</summary>
    /// <param name="state">The session state.</param>
    /// <param name="message">The message carrying it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when downstream has been notified.</returns>
    private async ValueTask ConfigureVideoAsync(SessionState state, PublishedMessage message, CancellationToken cancellationToken)
    {
        var info = state.Media.StreamInfo;
        var record = message.Payload[FlvMediaParser.VideoHeaderLength..];
        if (info.Video is { } current && current.Configuration.HasRecord(record.Span))
        {
            return;
        }

        if (!AvcDecoderConfiguration.TryParse(record.Span, out var configuration, out var error))
        {
            if (info.Video is not null)
            {
                await PublishAsync(state, info with { Video = null }, cancellationToken).ConfigureAwait(false);
            }

            await ReportAsync(state, MediaTrack.Video, MediaErrorKind.InvalidConfiguration, message.Timestamp, error, cancellationToken).ConfigureAwait(false);
            return;
        }

        var track = new VideoTrackInfo
        {
            Configuration = configuration,
            ConfigurationVersion = ++state.VideoConfigurationVersion,
            Width = info.Metadata.Width,
            Height = info.Metadata.Height,
            FrameRate = info.Metadata.FrameRate,
        };
        state.Media.RecordConfiguration();
        _metrics.Configuration(MediaTrack.Video);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            LogVideoConfiguration(state.Media.SessionId, configuration.CodecString, configuration.NalLengthSize, configuration.SequenceParameterSets.Count, configuration.PictureParameterSets.Count, track.ConfigurationVersion);
        }

        await PublishAsync(state, info with { Video = track }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies an AAC sequence header.</summary>
    /// <param name="state">The session state.</param>
    /// <param name="message">The message carrying it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when downstream has been notified.</returns>
    private async ValueTask ConfigureAudioAsync(SessionState state, PublishedMessage message, CancellationToken cancellationToken)
    {
        var info = state.Media.StreamInfo;
        var record = message.Payload[FlvMediaParser.AudioHeaderLength..];
        if (info.Audio is { } current && current.Configuration.HasRecord(record.Span))
        {
            return;
        }

        if (!AacAudioConfiguration.TryParse(record.Span, out var configuration, out var error))
        {
            if (info.Audio is not null)
            {
                await PublishAsync(state, info with { Audio = null }, cancellationToken).ConfigureAwait(false);
            }

            await ReportAsync(state, MediaTrack.Audio, MediaErrorKind.InvalidConfiguration, message.Timestamp, error, cancellationToken).ConfigureAwait(false);
            return;
        }

        var track = new AudioTrackInfo { Configuration = configuration, ConfigurationVersion = ++state.AudioConfigurationVersion };
        state.Media.RecordConfiguration();
        _metrics.Configuration(MediaTrack.Audio);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            LogAudioConfiguration(state.Media.SessionId, configuration.CodecString, configuration.SampleRate, configuration.ChannelConfiguration, track.ConfigurationVersion);
        }

        await PublishAsync(state, info with { Audio = track }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies the session's latest <c>onMetaData</c>, if it changed.</summary>
    /// <param name="state">The session state.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when downstream has been notified.</returns>
    private async ValueTask ProcessDataAsync(SessionState state, CancellationToken cancellationToken)
    {
        var properties = state.Media.Publisher.Metadata;
        if (ReferenceEquals(properties, state.LastMetadataProperties))
        {
            return;
        }

        state.LastMetadataProperties = properties;
        var metadata = PublisherMetadata.FromProperties(properties);
        var info = state.Media.StreamInfo;
        if (metadata == info.Metadata)
        {
            return;
        }

        LogMetadata(state.Media.SessionId, metadata.Width, metadata.Height, metadata.FrameRate, metadata.AudioSampleRate, metadata.AudioChannels);
        var video = info.Video is { } current
            ? current with { Width = metadata.Width, Height = metadata.Height, FrameRate = metadata.FrameRate }
            : null;
        await PublishAsync(state, info with { Metadata = metadata, Video = video }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the stream description and notifies downstream. Once downstream has accepted a description whose
    /// track configurations differ from the previous one and that has at least one track, a
    /// <see cref="StreamEventType.MediaDescribed"/> event is published; metadata-only changes publish nothing.
    /// </summary>
    /// <param name="state">The session state.</param>
    /// <param name="info">The new description; its version is assigned here.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when downstream has been notified.</returns>
    private async ValueTask PublishAsync(SessionState state, MediaStreamInfo info, CancellationToken cancellationToken)
    {
        var previous = state.Media.StreamInfo;
        var next = info with { Version = info.Version + 1 };
        state.Media.SetStreamInfo(next);
        await _downstream.OnStreamInfoChangedAsync(state.Media, next, cancellationToken).ConfigureAwait(false);
        if (_events is not null
            && (next.Video is not null || next.Audio is not null)
            && (next.Video?.ConfigurationVersion != previous.Video?.ConfigurationVersion || next.Audio?.ConfigurationVersion != previous.Audio?.ConfigurationVersion))
        {
            _events.Publish(StreamEventFactory.Create(StreamEventType.MediaDescribed, state.Media.Publisher, _time.GetUtcNow(), media: StreamEventFactory.Describe(next)));
        }
    }

    /// <summary>Classifies a delivered sample's timestamp step and records any discontinuity.</summary>
    /// <param name="state">The session state.</param>
    /// <param name="track">The track.</param>
    /// <param name="decodeTimestamp">The sample's decode timestamp.</param>
    /// <param name="delta">The step from the previous sample of the track.</param>
    /// <returns>The classification.</returns>
    private TimestampDiscontinuity Advance(SessionState state, MediaTrack track, long decodeTimestamp, out long? delta)
    {
        var discontinuity = state.Timeline.Advance(track, decodeTimestamp, out delta);
        if (discontinuity != TimestampDiscontinuity.None)
        {
            _metrics.Discontinuity(track, discontinuity);
            var step = delta ?? 0;
            if (state.LoggedDiscontinuities.Add(track))
            {
                LogDiscontinuity(state.Media.SessionId, track, discontinuity, step);
            }
            else
            {
                LogRepeatedDiscontinuity(state.Media.SessionId, track, discontinuity, step);
            }
        }

        return discontinuity;
    }

    /// <summary>Counts, logs and reports a dropped packet.</summary>
    /// <param name="state">The session state.</param>
    /// <param name="track">The packet's track.</param>
    /// <param name="kind">Why it was dropped.</param>
    /// <param name="source">Its RTMP timestamp.</param>
    /// <param name="detail">A description without payload bytes or publisher text.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when downstream has been notified.</returns>
    private ValueTask ReportAsync(SessionState state, MediaTrack track, MediaErrorKind kind, uint source, string detail, CancellationToken cancellationToken)
    {
        state.Media.RecordError();
        _metrics.Error(track, kind);
        if (!state.LoggedErrors.Add((track, kind)))
        {
            LogRepeatedError(state.Media.SessionId, track, kind, detail);
        }
        else if (kind == MediaErrorKind.UnsupportedCodec)
        {
            LogUnsupportedCodec(state.Media.SessionId, track, detail);
        }
        else
        {
            LogError(state.Media.SessionId, track, kind, detail);
        }

        return _downstream.OnMediaErrorAsync(state.Media, new MediaProcessingError(kind, track, source, detail), cancellationToken);
    }

    /// <summary>Logs a started session.</summary>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 6000, Level = LogLevel.Debug, Message = "Media processing started for session {SessionId}")]
    private partial void LogStarted(Guid sessionId);

    /// <summary>Logs an accepted AVC configuration.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="codec">The RFC 6381 codec string.</param>
    /// <param name="nalLengthSize">The NAL length-prefix size.</param>
    /// <param name="spsCount">Number of SPS.</param>
    /// <param name="ppsCount">Number of PPS.</param>
    /// <param name="version">The track configuration version.</param>
    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Session {SessionId} video configuration {Codec}: NAL length size {NalLengthSize}, {SpsCount} SPS, {PpsCount} PPS (version {Version})")]
    private partial void LogVideoConfiguration(Guid sessionId, string codec, int nalLengthSize, int spsCount, int ppsCount, int version);

    /// <summary>Logs an accepted AAC configuration.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="codec">The RFC 6381 codec string.</param>
    /// <param name="sampleRate">The sample rate.</param>
    /// <param name="channelConfiguration">The channel configuration.</param>
    /// <param name="version">The track configuration version.</param>
    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Session {SessionId} audio configuration {Codec}: {SampleRate} Hz, channel configuration {ChannelConfiguration} (version {Version})")]
    private partial void LogAudioConfiguration(Guid sessionId, string codec, int sampleRate, int channelConfiguration, int version);

    /// <summary>Logs the first unsupported-codec packet of a track.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="detail">The reason.</param>
    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning, Message = "Session {SessionId} {Track} uses an unsupported codec; its packets are dropped: {Detail}")]
    private partial void LogUnsupportedCodec(Guid sessionId, MediaTrack track, string detail);

    /// <summary>Logs the first dropped packet of a kind and track.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="kind">Why it was dropped.</param>
    /// <param name="detail">The reason.</param>
    [LoggerMessage(EventId = 6004, Level = LogLevel.Warning, Message = "Session {SessionId} {Track} packet dropped ({Kind}): {Detail}; further packets of this kind are logged at Debug")]
    private partial void LogError(Guid sessionId, MediaTrack track, MediaErrorKind kind, string detail);

    /// <summary>Logs a repeated dropped packet.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="kind">Why it was dropped.</param>
    /// <param name="detail">The reason.</param>
    [LoggerMessage(EventId = 6005, Level = LogLevel.Debug, Message = "Session {SessionId} {Track} packet dropped ({Kind}): {Detail}")]
    private partial void LogRepeatedError(Guid sessionId, MediaTrack track, MediaErrorKind kind, string detail);

    /// <summary>Logs the first timestamp discontinuity of a track.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="kind">The discontinuity.</param>
    /// <param name="delta">The decode-time step in milliseconds.</param>
    [LoggerMessage(EventId = 6006, Level = LogLevel.Warning, Message = "Session {SessionId} {Track} timestamp {Kind} of {Delta} ms; timestamps are passed through unchanged and further discontinuities are logged at Debug")]
    private partial void LogDiscontinuity(Guid sessionId, MediaTrack track, TimestampDiscontinuity kind, long delta);

    /// <summary>Logs a repeated timestamp discontinuity.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="kind">The discontinuity.</param>
    /// <param name="delta">The decode-time step in milliseconds.</param>
    [LoggerMessage(EventId = 6007, Level = LogLevel.Debug, Message = "Session {SessionId} {Track} timestamp {Kind} of {Delta} ms")]
    private partial void LogRepeatedDiscontinuity(Guid sessionId, MediaTrack track, TimestampDiscontinuity kind, long delta);

    /// <summary>Logs the media summary of an ended session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="videoSamples">Video samples delivered.</param>
    /// <param name="keyframes">Keyframes delivered.</param>
    /// <param name="audioSamples">Audio samples delivered.</param>
    /// <param name="configurations">Configurations accepted.</param>
    /// <param name="errors">Packets dropped.</param>
    /// <param name="discontinuities">Timestamp discontinuities.</param>
    [LoggerMessage(EventId = 6008, Level = LogLevel.Information, Message = "Media processing ended for session {SessionId}: {VideoSamples} video samples ({Keyframes} keyframes), {AudioSamples} audio samples, {Configurations} configurations, {Errors} dropped packets, {Discontinuities} timestamp discontinuities")]
    private partial void LogEnded(Guid sessionId, long videoSamples, long keyframes, long audioSamples, long configurations, long errors, long discontinuities);

    /// <summary>Logs changed publisher metadata.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="width">Declared width.</param>
    /// <param name="height">Declared height.</param>
    /// <param name="frameRate">Declared frame rate.</param>
    /// <param name="sampleRate">Declared audio sample rate.</param>
    /// <param name="channels">Declared audio channels.</param>
    [LoggerMessage(EventId = 6009, Level = LogLevel.Debug, Message = "Session {SessionId} metadata: {Width}x{Height} at {FrameRate} fps, audio {SampleRate} Hz with {Channels} channels (declared by the publisher)")]
    private partial void LogMetadata(Guid sessionId, int? width, int? height, double? frameRate, int? sampleRate, int? channels);

    /// <summary>Logs an AVC end-of-sequence packet.</summary>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 6010, Level = LogLevel.Debug, Message = "Session {SessionId} sent an AVC end-of-sequence packet")]
    private partial void LogEndOfSequence(Guid sessionId);

    /// <summary>Mutable per-session processing state; accessed only by the session's sequential message calls.</summary>
    /// <param name="media">The public session view.</param>
    /// <param name="timeline">The session's timeline.</param>
    private sealed class SessionState(MediaSession media, MediaTimeline timeline)
    {
        /// <summary>Gets the public session view.</summary>
        public MediaSession Media { get; } = media;

        /// <summary>Gets the session's timeline.</summary>
        public MediaTimeline Timeline { get; } = timeline;

        /// <summary>Gets the (track, kind) pairs already logged at Warning.</summary>
        public HashSet<(MediaTrack Track, MediaErrorKind Kind)> LoggedErrors { get; } = [];

        /// <summary>Gets the tracks whose first discontinuity has been logged at Warning.</summary>
        public HashSet<MediaTrack> LoggedDiscontinuities { get; } = [];

        /// <summary>Gets or sets the metadata property list last applied.</summary>
        public IReadOnlyList<KeyValuePair<string, AmfValue>>? LastMetadataProperties { get; set; }

        /// <summary>Gets or sets the next video sequence number.</summary>
        public long VideoSequence { get; set; }

        /// <summary>Gets or sets the next audio sequence number.</summary>
        public long AudioSequence { get; set; }

        /// <summary>Gets or sets the number of video configurations accepted.</summary>
        public int VideoConfigurationVersion { get; set; }

        /// <summary>Gets or sets the number of audio configurations accepted.</summary>
        public int AudioConfigurationVersion { get; set; }
    }
}
