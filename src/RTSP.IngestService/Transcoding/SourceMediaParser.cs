// Codec-aware parsing of published RTMP audio and video messages, legacy FLV and Enhanced RTMP (v2): FourCC codec
// identification, ModEx, multitrack, sequence starts, coded frames with composition offsets, video metadata
// (colorInfo) and audio multichannel configuration. It runs on the connection's receive path, so it only parses and
// validates; it never decodes. Every length is checked before use, the whole message is processed, unknown codecs
// are rejected with a controlled reason and nothing unknown is ever treated as H.264 or AAC.

using System.Buffers.Binary;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Transcoding.Codecs;

namespace RTSP.IngestService.Transcoding;

/// <summary>What a parsed message produced.</summary>
internal enum SourceParseOutcome
{
    /// <summary>Nothing to deliver (command frame, sequence end, repeated configuration, other track).</summary>
    Ignored,

    /// <summary>The stream configuration changed; nothing to deliver.</summary>
    Configured,

    /// <summary>A sample was produced and owns the message.</summary>
    Sample,

    /// <summary>The packet was dropped; the session continues.</summary>
    Dropped,

    /// <summary>The publish must end.</summary>
    Rejected,
}

/// <summary>Why a publish was rejected.</summary>
internal enum TranscodingRejection
{
    /// <summary>The codec is not supported.</summary>
    UnsupportedCodec,

    /// <summary>The codec is supported but excluded by configuration.</summary>
    CodecNotAccepted,

    /// <summary>The picture exceeds the configured maximum.</summary>
    ResolutionLimit,

    /// <summary>The frame rate exceeds the configured maximum.</summary>
    FrameRateLimit,
}

/// <summary>The result of parsing one message.</summary>
/// <param name="Outcome">What the message produced.</param>
/// <param name="Track">The message's track.</param>
/// <param name="ErrorKind">For <see cref="SourceParseOutcome.Dropped"/>, why.</param>
/// <param name="Rejection">For <see cref="SourceParseOutcome.Rejected"/>, why.</param>
/// <param name="Detail">A description without payload bytes or publisher text, for drops and rejections.</param>
/// <param name="Sample">For <see cref="SourceParseOutcome.Sample"/>, the sample, which owns the message.</param>
internal readonly record struct SourceParseResult(SourceParseOutcome Outcome, MediaTrack Track, MediaErrorKind ErrorKind, TranscodingRejection Rejection, string? Detail, SourceSample? Sample)
{
    /// <summary>Gets a value indicating whether the configuration changed while parsing (also for a sample that configured an MP3 or AC-3 track).</summary>
    public bool ConfigurationChanged { get; init; }
}

/// <summary>
/// Parses one publishing session's audio and video messages into <see cref="SourceSample"/> instances on a 64-bit
/// session timeline, tracking the current <see cref="SourceStreamConfiguration"/>.
/// </summary>
/// <remarks>
/// Not thread-safe: owned by one session, whose messages arrive sequentially. Only track 0 of multitrack messages is
/// used; other tracks are counted in <see cref="IgnoredTrackPackets"/> and discarded.
/// </remarks>
internal sealed class SourceMediaParser
{
    /// <summary>Video packet type: sequence start.</summary>
    private const int SequenceStart = 0;

    /// <summary>Video/audio packet type: coded frames.</summary>
    private const int CodedFrames = 1;

    /// <summary>Video/audio packet type: sequence end.</summary>
    private const int SequenceEnd = 2;

    /// <summary>Video packet type: coded frames without composition offset.</summary>
    private const int CodedFramesX = 3;

    /// <summary>Video packet type: AMF metadata.</summary>
    private const int VideoMetadata = 4;

    /// <summary>Video packet type: MPEG-2 TS sequence start.</summary>
    private const int Mpeg2TsSequenceStart = 5;

    /// <summary>Video packet type: multitrack.</summary>
    private const int VideoMultitrack = 6;

    /// <summary>Audio packet type: multichannel configuration.</summary>
    private const int MultichannelConfig = 4;

    /// <summary>Audio packet type: multitrack.</summary>
    private const int AudioMultitrack = 5;

    /// <summary>Packet type: ModEx.</summary>
    private const int ModEx = 7;

    /// <summary>The frame-rate measurement window, in milliseconds.</summary>
    private const long FrameRateWindow = 2000;

    /// <summary>The transcoding limits.</summary>
    private readonly TranscodingOptions _options;

    /// <summary>The media limits.</summary>
    private readonly MediaOptions _media;

    /// <summary>The session timeline.</summary>
    private readonly MediaTimeline _timeline;

    /// <summary>Recent video decode timestamps, for the frame-rate limit (a ring).</summary>
    private readonly long[] _recentVideo;

    /// <summary>Number of valid entries in <see cref="_recentVideo"/>.</summary>
    private int _recentCount;

    /// <summary>Index of the oldest entry in <see cref="_recentVideo"/>.</summary>
    private int _recentStart;

    /// <summary>Whether the current video configuration's picture size still has to be read from a keyframe.</summary>
    private bool _sizePending;

    /// <summary>Initializes a new instance of the <see cref="SourceMediaParser"/> class.</summary>
    /// <param name="options">The transcoding limits.</param>
    /// <param name="media">The media limits.</param>
    public SourceMediaParser(TranscodingOptions options, MediaOptions media)
    {
        _options = options;
        _media = media;
        _timeline = new MediaTimeline(media.DiscontinuityThreshold);
        _recentVideo = new long[(int)Math.Ceiling(options.MaxInputFrameRate * (FrameRateWindow / 1000.0) * 1.1) + 1];
    }

    /// <summary>Gets the current stream configuration.</summary>
    public SourceStreamConfiguration Configuration { get; private set; } = SourceStreamConfiguration.Empty;

    /// <summary>Gets the number of multitrack packets of tracks other than 0 that were discarded.</summary>
    public long IgnoredTrackPackets { get; private set; }

    /// <summary>Parses a message.</summary>
    /// <param name="message">
    /// The message. When the outcome is <see cref="SourceParseOutcome.Sample"/> the sample owns it; otherwise the
    /// caller still owns it.
    /// </param>
    /// <returns>The result.</returns>
    public SourceParseResult Parse(PublishedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Kind switch
        {
            PublishedMessageKind.Video => ParseVideo(message),
            PublishedMessageKind.Audio => ParseAudio(message),
            _ => default,
        };
    }

    /// <summary>Returns a drop result.</summary>
    /// <param name="track">The track.</param>
    /// <param name="kind">Why.</param>
    /// <param name="detail">The description.</param>
    /// <returns>The result.</returns>
    private static SourceParseResult Drop(MediaTrack track, MediaErrorKind kind, string detail) => new(SourceParseOutcome.Dropped, track, kind, default, detail, null);

    /// <summary>Returns a rejection result.</summary>
    /// <param name="track">The track.</param>
    /// <param name="rejection">Why.</param>
    /// <param name="detail">The description.</param>
    /// <returns>The result.</returns>
    private static SourceParseResult Reject(MediaTrack track, TranscodingRejection rejection, string detail) => new(SourceParseOutcome.Rejected, track, default, rejection, detail, null);

    /// <summary>Returns an ignored result.</summary>
    /// <param name="track">The track.</param>
    /// <returns>The result.</returns>
    private static SourceParseResult Ignore(MediaTrack track) => new(SourceParseOutcome.Ignored, track, default, default, null, null);

    /// <summary>Reads a FourCC as text.</summary>
    /// <param name="data">Four bytes.</param>
    /// <returns>The FourCC, with non-printable bytes replaced.</returns>
    private static string FourCcText(ReadOnlySpan<byte> data)
    {
        Span<char> text = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            text[i] = data[i] is >= 0x20 and < 0x7F ? (char)data[i] : '?';
        }

        return new string(text);
    }

    /// <summary>Reads a signed 24-bit big-endian value.</summary>
    /// <param name="data">Three bytes.</param>
    /// <returns>The value.</returns>
    private static int ReadSi24(ReadOnlySpan<byte> data) => ((data[0] << 16) | (data[1] << 8) | data[2]) << 8 >> 8;

    /// <summary>Skips ModEx extensions starting at <paramref name="position"/>.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="position">Read position; advanced past the extensions.</param>
    /// <param name="packetType">The packet type; updated by each extension.</param>
    /// <returns><see langword="null"/> on success; otherwise why the extensions are malformed.</returns>
    private static string? SkipModEx(ReadOnlySpan<byte> payload, ref int position, ref int packetType)
    {
        var count = 0;
        while (packetType == ModEx)
        {
            if (++count > 16)
            {
                return "The packet chains more than 16 ModEx extensions.";
            }

            if (position >= payload.Length)
            {
                return "A ModEx extension is truncated.";
            }

            var size = payload[position++] + 1;
            if (size == 256)
            {
                if (payload.Length - position < 2)
                {
                    return "A ModEx extension size is truncated.";
                }

                size = BinaryPrimitives.ReadUInt16BigEndian(payload[position..]) + 1;
                position += 2;
            }

            if (payload.Length - position < size + 1)
            {
                return "A ModEx extension is truncated.";
            }

            position += size;
            var modExType = payload[position] >> 4;
            packetType = payload[position] & 0x0F;
            position++;
            if (modExType == 0 && size < 3)
            {
                return "A TimestampOffsetNano ModEx extension has fewer than 3 bytes.";
            }
        }

        return null;
    }

    /// <summary>
    /// Selects track 0 from a multitrack body and reports where its FourCC and data are.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <param name="position">Position after the multitrack type/packet type byte.</param>
    /// <param name="multitrackType">The multitrack type.</param>
    /// <param name="fourCc">The FourCC of track 0.</param>
    /// <param name="bodyOffset">Offset of track 0's body.</param>
    /// <param name="bodyLength">Length of track 0's body.</param>
    /// <param name="ignored">Number of other tracks.</param>
    /// <param name="error">Why the body is malformed.</param>
    /// <returns><see langword="true"/> if track 0 was found.</returns>
    private static bool TrySelectTrackZero(ReadOnlySpan<byte> payload, int position, int multitrackType, out ReadOnlySpan<byte> fourCc, out int bodyOffset, out int bodyLength, out int ignored, out string? error)
    {
        fourCc = default;
        bodyOffset = 0;
        bodyLength = 0;
        ignored = 0;
        error = null;
        if (multitrackType > 2)
        {
            error = $"Multitrack type {multitrackType} is not defined.";
            return false;
        }

        ReadOnlySpan<byte> common = default;
        if (multitrackType != 2)
        {
            if (payload.Length - position < 4)
            {
                error = "The multitrack FourCC is truncated.";
                return false;
            }

            common = payload.Slice(position, 4);
            position += 4;
        }

        var found = false;
        var tracks = 0;
        while (position < payload.Length)
        {
            if (++tracks > 256)
            {
                error = "The multitrack message has more than 256 tracks.";
                return false;
            }

            var trackFourCc = common;
            if (multitrackType == 2)
            {
                if (payload.Length - position < 4)
                {
                    error = "A multitrack FourCC is truncated.";
                    return false;
                }

                trackFourCc = payload.Slice(position, 4);
                position += 4;
            }

            if (position >= payload.Length)
            {
                error = "A multitrack track ID is truncated.";
                return false;
            }

            var trackId = payload[position++];
            int length;
            if (multitrackType == 0)
            {
                length = payload.Length - position;
            }
            else
            {
                if (payload.Length - position < 3)
                {
                    error = "A multitrack track size is truncated.";
                    return false;
                }

                length = (payload[position] << 16) | (payload[position + 1] << 8) | payload[position + 2];
                position += 3;
                if (length > payload.Length - position)
                {
                    error = $"A multitrack track declares {length} bytes; {payload.Length - position} remain.";
                    return false;
                }
            }

            if (trackId == 0 && !found)
            {
                found = true;
                fourCc = trackFourCc;
                bodyOffset = position;
                bodyLength = length;
            }
            else
            {
                ignored++;
            }

            position += length;
            if (multitrackType == 0)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>Parses a video message.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The result.</returns>
    private SourceParseResult ParseVideo(PublishedMessage message)
    {
        const MediaTrack Track = MediaTrack.Video;
        var payload = message.Payload.Span;
        var decodeTimestamp = _timeline.Unwrap(message.Timestamp);
        if (payload.IsEmpty)
        {
            return Drop(Track, MediaErrorKind.MalformedPacket, "The video packet is empty.");
        }

        var first = payload[0];
        if ((first & 0x80) == 0)
        {
            var legacyFrameType = first >> 4;
            var codecId = first & 0x0F;
            if (legacyFrameType == 5)
            {
                return Ignore(Track);
            }

            if (codecId != 7)
            {
                var name = codecId switch { 2 => "Sorenson H.263", 3 => "Screen video", 4 => "On2 VP6", 5 => "On2 VP6 with alpha", 6 => "Screen video v2", _ => "unknown" };
                return Reject(Track, TranscodingRejection.UnsupportedCodec, $"FLV video codec ID {codecId} ({name}) is not supported.");
            }

            if (legacyFrameType is < 1 or > 4 || payload.Length < 5 || payload[1] > 2)
            {
                return Drop(Track, MediaErrorKind.MalformedPacket, "The AVC packet header is truncated or has an undefined frame or packet type.");
            }

            var legacyPacketType = payload[1] == 2 ? SequenceEnd : payload[1];
            return HandleVideo(message, SourceVideoCodec.H264, legacyPacketType, legacyFrameType, 5, payload.Length - 5, ReadSi24(payload[2..]), decodeTimestamp);
        }

        var frameType = (first >> 4) & 0x07;
        var packetType = first & 0x0F;
        var position = 1;
        if (SkipModEx(payload, ref position, ref packetType) is { } modExError)
        {
            return Drop(Track, MediaErrorKind.MalformedPacket, modExError);
        }

        if (frameType == 5 && packetType != VideoMetadata)
        {
            return position < payload.Length ? Ignore(Track) : Drop(Track, MediaErrorKind.MalformedPacket, "The video command frame has no command.");
        }

        ReadOnlySpan<byte> fourCc;
        int bodyOffset;
        int bodyLength;
        if (packetType == VideoMultitrack)
        {
            if (position >= payload.Length)
            {
                return Drop(Track, MediaErrorKind.MalformedPacket, "The video multitrack header is truncated.");
            }

            var multitrackType = payload[position] >> 4;
            packetType = payload[position] & 0x0F;
            if (packetType is VideoMultitrack or ModEx)
            {
                return Drop(Track, MediaErrorKind.MalformedPacket, "A video multitrack packet nests multitrack or ModEx signalling.");
            }

            if (!TrySelectTrackZero(payload, position + 1, multitrackType, out fourCc, out bodyOffset, out bodyLength, out var ignored, out var error))
            {
                IgnoredTrackPackets += ignored;
                return error is null ? Ignore(Track) : Drop(Track, MediaErrorKind.MalformedPacket, error);
            }

            IgnoredTrackPackets += ignored;
        }
        else
        {
            if (payload.Length - position < 4)
            {
                return Drop(Track, MediaErrorKind.MalformedPacket, "The video FourCC is truncated.");
            }

            fourCc = payload.Slice(position, 4);
            bodyOffset = position + 4;
            bodyLength = payload.Length - bodyOffset;
        }

        SourceVideoCodec codec;
        if (fourCc.SequenceEqual("avc1"u8))
        {
            codec = SourceVideoCodec.H264;
        }
        else if (fourCc.SequenceEqual("hvc1"u8))
        {
            codec = SourceVideoCodec.Hevc;
        }
        else if (fourCc.SequenceEqual("av01"u8))
        {
            codec = SourceVideoCodec.Av1;
        }
        else if (fourCc.SequenceEqual("vp09"u8))
        {
            codec = SourceVideoCodec.Vp9;
        }
        else
        {
            var known = fourCc.SequenceEqual("vp08"u8) ? " (VP8)" : fourCc.SequenceEqual("vvc1"u8) ? " (VVC)" : string.Empty;
            return Reject(Track, TranscodingRejection.UnsupportedCodec, $"Enhanced RTMP video FourCC '{FourCcText(fourCc)}'{known} is not supported.");
        }

        var composition = 0;
        switch (packetType)
        {
            case SequenceStart:
            case SequenceEnd:
                break;
            case VideoMetadata:
                return ApplyColorInfo(payload.Slice(bodyOffset, bodyLength));
            case CodedFrames when codec is SourceVideoCodec.H264 or SourceVideoCodec.Hevc:
                if (bodyLength < 3)
                {
                    return Drop(Track, MediaErrorKind.MalformedPacket, "The coded video frame has no composition time offset.");
                }

                composition = ReadSi24(payload[bodyOffset..]);
                bodyOffset += 3;
                bodyLength -= 3;
                break;
            case CodedFrames:
            case CodedFramesX:
                packetType = CodedFrames;
                break;
            case Mpeg2TsSequenceStart:
                return Reject(Track, TranscodingRejection.UnsupportedCodec, "Enhanced RTMP MPEG-2 TS sequence starts are not supported.");
            default:
                return Drop(Track, MediaErrorKind.MalformedPacket, $"Enhanced RTMP video packet type {packetType} is not defined.");
        }

        if (packetType == CodedFrames && frameType is < 1 or > 4)
        {
            return Drop(Track, MediaErrorKind.MalformedPacket, $"Video frame type {frameType} is not valid for a coded frame.");
        }

        return HandleVideo(message, codec, packetType, frameType, bodyOffset, bodyLength, composition, decodeTimestamp);
    }

    /// <summary>Handles a video sequence start, sequence end or coded frame.</summary>
    /// <param name="message">The message.</param>
    /// <param name="codec">The codec.</param>
    /// <param name="packetType">Sequence start, sequence end or coded frames.</param>
    /// <param name="frameType">The frame type.</param>
    /// <param name="offset">Offset of the body.</param>
    /// <param name="length">Length of the body.</param>
    /// <param name="composition">The composition time offset.</param>
    /// <param name="decodeTimestamp">The decode timestamp.</param>
    /// <returns>The result.</returns>
    private SourceParseResult HandleVideo(PublishedMessage message, SourceVideoCodec codec, int packetType, int frameType, int offset, int length, int composition, long decodeTimestamp)
    {
        const MediaTrack Track = MediaTrack.Video;
        if (!_options.Accepts(codec))
        {
            return Reject(Track, TranscodingRejection.CodecNotAccepted, $"Video codec {codec} is not accepted by Transcoding:AcceptedVideoCodecs.");
        }

        if (packetType == SequenceEnd)
        {
            return Ignore(Track);
        }

        var data = message.Payload.Span.Slice(offset, length);
        if (packetType == SequenceStart)
        {
            var current = Configuration.Video;
            if (current is not null && current.IsSame(codec, data))
            {
                return Ignore(Track);
            }

            if (!SourceVideoConfigurations.TryParse(codec, data, out var parsed, out var error))
            {
                if (current is not null)
                {
                    Configuration = Configuration with { Video = null, Version = Configuration.Version + 1 };
                }

                return Drop(Track, MediaErrorKind.InvalidConfiguration, error) with { ConfigurationChanged = current is not null };
            }

            if (parsed.Width is { } width && parsed.Height is { } height && ExceedsSize(width, height) is { } sizeError)
            {
                return Reject(Track, TranscodingRejection.ResolutionLimit, sizeError);
            }

            _sizePending = parsed.Width is null;
            Configuration = Configuration with { Video = parsed, Version = Configuration.Version + 1 };
            return new SourceParseResult(SourceParseOutcome.Configured, Track, default, default, null, null) { ConfigurationChanged = true };
        }

        var configuration = Configuration.Video;
        if (configuration is null || configuration.Codec != codec)
        {
            return Drop(Track, MediaErrorKind.MissingConfiguration, $"A {codec} video frame arrived before a valid {codec} sequence start.");
        }

        if (length > _media.MaxSampleSize)
        {
            return Drop(Track, MediaErrorKind.SampleTooLarge, $"The video sample is {length} bytes; the limit is {_media.MaxSampleSize}.");
        }

        var keyframe = frameType is 1 or 4;
        if (!TryValidateFrame(codec, configuration, data, out var kind, out var frameError))
        {
            return Drop(Track, kind, frameError);
        }

        if (keyframe && _sizePending && SourceVideoConfigurations.TryReadKeyframeSize(codec, data, out var keyWidth, out var keyHeight))
        {
            if (ExceedsSize(keyWidth, keyHeight) is { } sizeError)
            {
                return Reject(Track, TranscodingRejection.ResolutionLimit, sizeError);
            }

            _sizePending = false;
        }

        var discontinuity = _timeline.Advance(Track, decodeTimestamp, out _);
        if (discontinuity != TimestampDiscontinuity.None)
        {
            _recentCount = 0;
        }

        if (ExceedsFrameRate(decodeTimestamp) is { } rateError)
        {
            return Reject(Track, TranscodingRejection.FrameRateLimit, rateError);
        }

        var sample = new SourceSample(message, Track, offset, length, decodeTimestamp, composition, keyframe, discontinuity, Configuration);
        return new SourceParseResult(SourceParseOutcome.Sample, Track, default, default, null, sample);
    }

    /// <summary>Validates the framing of a coded video frame.</summary>
    /// <param name="codec">The codec.</param>
    /// <param name="configuration">The track configuration.</param>
    /// <param name="data">The frame.</param>
    /// <param name="kind">Why it is invalid.</param>
    /// <param name="error">The description.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    private bool TryValidateFrame(SourceVideoCodec codec, SourceVideoConfiguration configuration, ReadOnlySpan<byte> data, out MediaErrorKind kind, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        kind = MediaErrorKind.MalformedPacket;
        switch (codec)
        {
            case SourceVideoCodec.H264:
            case SourceVideoCodec.Hevc:
                return FlvMediaParser.TryReadNalUnits(data, configuration.NalLengthSize, _media.MaxNalUnitsPerSample, out _, out kind, out error);
            case SourceVideoCodec.Av1:
                if (data.IsEmpty)
                {
                    error = "The AV1 temporal unit is empty.";
                    return false;
                }

                return SourceVideoConfigurations.TryValidateObus(data, out error);
            default:
                error = data.IsEmpty ? "The VP9 frame is empty." : null;
                return error is null;
        }
    }

    /// <summary>Checks the picture size limit.</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns><see langword="null"/> if within the limit; otherwise the reason.</returns>
    private string? ExceedsSize(int width, int height) =>
        width > _options.MaxInputWidth || height > _options.MaxInputHeight
            ? $"The video picture is {width}x{height}; at most {_options.MaxInputWidth}x{_options.MaxInputHeight} is accepted."
            : null;

    /// <summary>Records a video frame for the frame-rate limit.</summary>
    /// <param name="decodeTimestamp">The frame's decode timestamp.</param>
    /// <returns><see langword="null"/> if within the limit; otherwise the reason.</returns>
    private string? ExceedsFrameRate(long decodeTimestamp)
    {
        var capacity = _recentVideo.Length;
        if (_recentCount == capacity)
        {
            var oldest = _recentVideo[_recentStart];
            if (decodeTimestamp - oldest < FrameRateWindow)
            {
                return $"The video frame rate exceeds {_options.MaxInputFrameRate} fps ({capacity + 1} frames within {FrameRateWindow} ms).";
            }

            _recentVideo[_recentStart] = decodeTimestamp;
            _recentStart = (_recentStart + 1) % capacity;
            return null;
        }

        _recentVideo[(_recentStart + _recentCount) % capacity] = decodeTimestamp;
        _recentCount++;
        return null;
    }

    /// <summary>Applies an E-RTMP video metadata packet (<c>colorInfo</c>).</summary>
    /// <param name="body">The AMF0 body.</param>
    /// <returns>The result.</returns>
    private SourceParseResult ApplyColorInfo(ReadOnlySpan<byte> body)
    {
        const MediaTrack Track = MediaTrack.Video;
        List<AmfValue> values;
        try
        {
            values = Amf0Reader.ReadAll(body);
        }
        catch (RtmpProtocolException)
        {
            return Drop(Track, MediaErrorKind.MalformedPacket, "The video metadata packet is not valid AMF0.");
        }

        VideoColorDescription? color = null;
        for (var i = 0; i + 1 < values.Count; i += 2)
        {
            if (values[i] is not { Kind: AmfValueKind.Text } name || name.StringValue != "colorInfo")
            {
                continue;
            }

            color = values[i + 1].GetProperty("colorConfig") is { } config ? MapColor(config) : null;
        }

        if (color == Configuration.Color)
        {
            return Ignore(Track);
        }

        Configuration = Configuration with { Color = color, Version = Configuration.Version + 1 };
        return new SourceParseResult(SourceParseOutcome.Configured, Track, default, default, null, null) { ConfigurationChanged = true };
    }

    /// <summary>Maps a <c>colorConfig</c> object to a supported HDR colour description.</summary>
    /// <param name="config">The object.</param>
    /// <returns>The description for BT.2020 PQ or HLG; otherwise <see langword="null"/>, which leaves the bitstream's tags in charge.</returns>
    private static VideoColorDescription? MapColor(AmfValue config)
    {
        if (config.Kind is not (AmfValueKind.AnonymousObject or AmfValueKind.EcmaArray))
        {
            return null;
        }

        var primaries = config.GetNumberProperty("colorPrimaries");
        var transfer = config.GetNumberProperty("transferCharacteristics");
        var matrix = config.GetNumberProperty("matrixCoefficients");
        return primaries == 9 && transfer is 16 or 18 && matrix is 9 or 10
            ? new VideoColorDescription(9, (int)transfer.Value, 9)
            : null;
    }

    /// <summary>Parses an audio message.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The result.</returns>
    private SourceParseResult ParseAudio(PublishedMessage message)
    {
        const MediaTrack Track = MediaTrack.Audio;
        var payload = message.Payload.Span;
        var decodeTimestamp = _timeline.Unwrap(message.Timestamp);
        if (payload.IsEmpty)
        {
            return Ignore(Track);
        }

        var format = payload[0] >> 4;
        if (format != 9)
        {
            switch (format)
            {
                case 10:
                    if (payload.Length < 2 || payload[1] > 1)
                    {
                        return Drop(Track, MediaErrorKind.MalformedPacket, "The AAC packet header is truncated or has an undefined packet type.");
                    }

                    return HandleAudio(message, SourceAudioCodec.Aac, payload[1] == 0 ? SequenceStart : CodedFrames, 2, payload.Length - 2, decodeTimestamp);
                case 2:
                case 14:
                    return HandleAudio(message, SourceAudioCodec.Mp3, CodedFrames, 1, payload.Length - 1, decodeTimestamp);
                default:
                    var name = format switch
                    {
                        0 or 3 => "linear PCM",
                        1 => "ADPCM",
                        4 or 5 or 6 => "Nellymoser",
                        7 or 8 => "G.711",
                        11 => "Speex",
                        15 => "device-specific",
                        _ => "reserved",
                    };
                    return Reject(Track, TranscodingRejection.UnsupportedCodec, $"FLV sound format {format} ({name}) is not supported.");
            }
        }

        var packetType = payload[0] & 0x0F;
        var position = 1;
        if (SkipModEx(payload, ref position, ref packetType) is { } modExError)
        {
            return Drop(Track, MediaErrorKind.MalformedPacket, modExError);
        }

        ReadOnlySpan<byte> fourCc;
        int bodyOffset;
        int bodyLength;
        if (packetType == AudioMultitrack)
        {
            if (position >= payload.Length)
            {
                return Drop(Track, MediaErrorKind.MalformedPacket, "The audio multitrack header is truncated.");
            }

            var multitrackType = payload[position] >> 4;
            packetType = payload[position] & 0x0F;
            if (packetType is AudioMultitrack or ModEx)
            {
                return Drop(Track, MediaErrorKind.MalformedPacket, "An audio multitrack packet nests multitrack or ModEx signalling.");
            }

            if (!TrySelectTrackZero(payload, position + 1, multitrackType, out fourCc, out bodyOffset, out bodyLength, out var ignored, out var error))
            {
                IgnoredTrackPackets += ignored;
                return error is null ? Ignore(Track) : Drop(Track, MediaErrorKind.MalformedPacket, error);
            }

            IgnoredTrackPackets += ignored;
        }
        else
        {
            if (payload.Length - position < 4)
            {
                return Drop(Track, MediaErrorKind.MalformedPacket, "The audio FourCC is truncated.");
            }

            fourCc = payload.Slice(position, 4);
            bodyOffset = position + 4;
            bodyLength = payload.Length - bodyOffset;
        }

        SourceAudioCodec codec;
        if (fourCc.SequenceEqual("mp4a"u8))
        {
            codec = SourceAudioCodec.Aac;
        }
        else if (fourCc.SequenceEqual(".mp3"u8))
        {
            codec = SourceAudioCodec.Mp3;
        }
        else if (fourCc.SequenceEqual("Opus"u8))
        {
            codec = SourceAudioCodec.Opus;
        }
        else if (fourCc.SequenceEqual("fLaC"u8))
        {
            codec = SourceAudioCodec.Flac;
        }
        else if (fourCc.SequenceEqual("ac-3"u8))
        {
            codec = SourceAudioCodec.Ac3;
        }
        else if (fourCc.SequenceEqual("ec-3"u8))
        {
            codec = SourceAudioCodec.Eac3;
        }
        else
        {
            return Reject(Track, TranscodingRejection.UnsupportedCodec, $"Enhanced RTMP audio FourCC '{FourCcText(fourCc)}' is not supported.");
        }

        switch (packetType)
        {
            case SequenceStart:
            case CodedFrames:
            case SequenceEnd:
                return HandleAudio(message, codec, packetType, bodyOffset, bodyLength, decodeTimestamp);
            case MultichannelConfig:
                return ValidateMultichannelConfig(payload.Slice(bodyOffset, bodyLength));
            default:
                return Drop(Track, MediaErrorKind.MalformedPacket, $"Enhanced RTMP audio packet type {packetType} is not defined.");
        }
    }

    /// <summary>Validates an audio multichannel configuration; channel layout is taken from the codec itself.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The result.</returns>
    private static SourceParseResult ValidateMultichannelConfig(ReadOnlySpan<byte> body)
    {
        const MediaTrack Track = MediaTrack.Audio;
        if (body.Length < 2 || body[0] > 2)
        {
            return Drop(Track, MediaErrorKind.MalformedPacket, "The audio multichannel configuration is truncated or has an undefined channel order.");
        }

        var required = body[0] switch { 1 => 6, 2 => 2 + body[1], _ => 2 };
        return body.Length < required
            ? Drop(Track, MediaErrorKind.MalformedPacket, "The audio multichannel configuration is truncated.")
            : Ignore(Track);
    }

    /// <summary>Handles an audio sequence start, sequence end or coded frame.</summary>
    /// <param name="message">The message.</param>
    /// <param name="codec">The codec.</param>
    /// <param name="packetType">The packet type.</param>
    /// <param name="offset">Offset of the body.</param>
    /// <param name="length">Length of the body.</param>
    /// <param name="decodeTimestamp">The decode timestamp.</param>
    /// <returns>The result.</returns>
    private SourceParseResult HandleAudio(PublishedMessage message, SourceAudioCodec codec, int packetType, int offset, int length, long decodeTimestamp)
    {
        const MediaTrack Track = MediaTrack.Audio;
        if (!_options.Accepts(codec))
        {
            return Reject(Track, TranscodingRejection.CodecNotAccepted, $"Audio codec {codec} is not accepted by Transcoding:AcceptedAudioCodecs.");
        }

        if (packetType == SequenceEnd)
        {
            return Ignore(Track);
        }

        var data = message.Payload.Span.Slice(offset, length);
        var current = Configuration.Audio;
        var changed = false;
        if (packetType == SequenceStart)
        {
            if (!SourceAudioConfigurations.HasSequenceHeader(codec))
            {
                return Ignore(Track);
            }

            if (!SourceAudioConfigurations.TryParseSequenceHeader(codec, data, out var parsed, out var error))
            {
                if (current is not null)
                {
                    Configuration = Configuration with { Audio = null, Version = Configuration.Version + 1 };
                }

                return Drop(Track, MediaErrorKind.InvalidConfiguration, error) with { ConfigurationChanged = current is not null };
            }

            if (current is not null && current.IsSame(parsed))
            {
                return Ignore(Track);
            }

            Configuration = Configuration with { Audio = parsed, Version = Configuration.Version + 1 };
            return new SourceParseResult(SourceParseOutcome.Configured, Track, default, default, null, null) { ConfigurationChanged = true };
        }

        if (length == 0)
        {
            return Ignore(Track);
        }

        if (length > _media.MaxSampleSize)
        {
            return Drop(Track, MediaErrorKind.SampleTooLarge, $"The audio sample is {length} bytes; the limit is {_media.MaxSampleSize}.");
        }

        if (SourceAudioConfigurations.HasSequenceHeader(codec))
        {
            if (current is null || current.Codec != codec)
            {
                return Drop(Track, MediaErrorKind.MissingConfiguration, $"A {codec} audio frame arrived before a valid {codec} sequence start.");
            }
        }
        else
        {
            if (!SourceAudioConfigurations.TryParseFrame(codec, data, out var parsed, out var error))
            {
                return Drop(Track, MediaErrorKind.MalformedPacket, error);
            }

            if (current is null || !current.IsSame(parsed))
            {
                Configuration = Configuration with { Audio = parsed, Version = Configuration.Version + 1 };
                changed = true;
            }
        }

        var discontinuity = _timeline.Advance(Track, decodeTimestamp, out _);
        var sample = new SourceSample(message, Track, offset, length, decodeTimestamp, 0, false, discontinuity, Configuration);
        return new SourceParseResult(SourceParseOutcome.Sample, Track, default, default, null, sample) { ConfigurationChanged = changed };
    }
}
