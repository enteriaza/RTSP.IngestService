// The managed H.264 video stage of one publisher: a task that decodes queued H.264 source access units with the
// managed decoder, converts each decoded picture to BT.709 limited range, encodes canonical H.264 High with the
// managed encoder and submits the FLV-framed units to the session's media multiplexer, independently of the audio
// stage. Pictures leave the decoder in presentation order and the encoder codes no B pictures, so each canonical
// unit carries the source presentation time as its decode time and a zero composition offset.
//
// Lifecycle: an output stream starts with an IDR picture and the encoder's AVC sequence header. A source
// configuration change or a timestamp discontinuity drains the decoder, ends the stream and starts a new one. A
// malformed access unit replaces the decoder, which resumes at the next IDR picture; a faulted decoder is never
// called again. Unsupported source features, decoder limits, persistent decode failures, a stream without IDR
// pictures, and any colour conversion or encoder failure end the publish. No codec configuration or payload is logged.

using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Video.Color;
using RTSP.IngestService.Media.Codecs.Video.H264;
using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Transcoding;

/// <summary>A source video access unit queued for the managed video stage.</summary>
/// <param name="Sample">The sample; ownership transfers with the input.</param>
/// <param name="Submitted">When it was submitted, as a <see cref="Stopwatch"/> timestamp.</param>
internal readonly record struct ManagedVideoInput(SourceSample Sample, long Submitted);

/// <summary>Why a managed video output stream started.</summary>
internal enum VideoStreamStartReason
{
    /// <summary>The session's first stream.</summary>
    Initial,

    /// <summary>The source video configuration changed.</summary>
    Configuration,

    /// <summary>The source timeline regressed or jumped.</summary>
    Discontinuity,
}

/// <summary>The settings of every session's managed video stage, fixed for the service's lifetime.</summary>
/// <param name="KeyframeInterval">The canonical IDR interval.</param>
/// <param name="Quantizer">The canonical P-picture quantiser.</param>
/// <param name="MaxBitrateKilobits">The canonical peak bitrate.</param>
/// <param name="MaxFrameRate">The highest accepted source frame rate, which selects the H.264 level.</param>
/// <param name="MaxSampleSize">The largest canonical FLV tag body the media stage accepts.</param>
/// <param name="Decoder">The decoder limits.</param>
internal sealed record ManagedVideoSettings(TimeSpan KeyframeInterval, int Quantizer, int MaxBitrateKilobits, int MaxFrameRate, int MaxSampleSize, H264DecoderOptions Decoder)
{
    /// <summary>Derives the settings from the configuration; picture limits are the tighter of the input limits and the managed codecs'.</summary>
    /// <param name="options">The transcoding options.</param>
    /// <param name="media">The media limits.</param>
    /// <param name="keyframeInterval">The canonical IDR interval.</param>
    /// <returns>The settings.</returns>
    public static ManagedVideoSettings FromOptions(TranscodingOptions options, MediaOptions media, TimeSpan keyframeInterval)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(media);
        var width = Math.Min(options.MaxInputWidth, H264EncoderOptions.MaxDimension);
        var height = Math.Min(options.MaxInputHeight, H264EncoderOptions.MaxDimension);
        var macroblocks = (int)Math.Min(H264EncoderOptions.MaxMacroblocks, (long)((width + 15) / 16) * ((height + 15) / 16));
        var decoder = new H264DecoderOptions
        {
            MaxWidth = width,
            MaxHeight = height,
            MaxMacroblocks = macroblocks,
            MaxAccessUnitBytes = media.MaxSampleSize,
            MaxNalUnitsPerAccessUnit = media.MaxNalUnitsPerSample,
        };
        decoder.Validate();
        return new ManagedVideoSettings(keyframeInterval, options.Video.Quantizer, options.Video.MaxBitrateKilobits, options.MaxInputFrameRate, media.MaxSampleSize, decoder);
    }
}

/// <summary>Transcodes one publisher's H.264 video with the managed codecs.</summary>
/// <remarks>
/// <see cref="TryWrite"/> is called only from the session's receive loop and <see cref="Complete"/> once, when the
/// session stops; everything else runs on the stage's own task, which disposes the codecs when it ends.
/// </remarks>
internal sealed partial class ManagedVideoStage : IDisposable
{
    /// <summary>Decode failures tolerated within <see cref="FailureWindowMilliseconds"/>; one more ends the publish.</summary>
    internal const int MaxDecodeFailures = 10;

    /// <summary>The window, in milliseconds of source time, over which decode failures are counted.</summary>
    internal const long FailureWindowMilliseconds = 60_000;

    /// <summary>The longest stretch of source time, in milliseconds, the decoder may wait for an IDR picture.</summary>
    internal const long MaxIdrWaitMilliseconds = 60_000;

    /// <summary>Entries in the latency ring.</summary>
    private const int LatencyEntries = 128;

    /// <summary>The bytes of an FLV AVC video tag header: frame type and codec, packet type, composition time.</summary>
    private const int TagHeaderSize = 5;

    /// <summary>
    /// The colour policies: unspecified H.273 fields are taken as BT.709 (the canonical output; logged once per
    /// session), and BT.2100 PQ/HLG is tone mapped to SDR, as the canonical profile always did.
    /// </summary>
    internal static readonly VideoColorConversionOptions ColorPolicy = new() { Unspecified = UnspecifiedColorPolicy.AssumeBt709, Hdr = HdrPolicy.ToneMap };

    /// <summary>The FLV AVC sequence header prefix: keyframe and AVC, packet type 0, composition time 0.</summary>
    private static readonly byte[] SequenceHeaderPrefix = [0x17, 0x00, 0, 0, 0];

    /// <summary>The publishing session.</summary>
    private readonly PublisherSession _publisher;

    /// <summary>The multiplexer's video input.</summary>
    private readonly MediaMultiplexer.TrackInput _output;

    /// <summary>The settings.</summary>
    private readonly ManagedVideoSettings _settings;

    /// <summary>Metrics.</summary>
    private readonly TranscodingMetrics _metrics;

    /// <summary>Logger.</summary>
    private readonly ILogger _logger;

    /// <summary>Releases a source sample's queue accounting.</summary>
    private readonly Action<SourceSample> _release;

    /// <summary>Told about a failure that ends the publish.</summary>
    private readonly Action<TranscodingFailedException> _onFailure;

    /// <summary>The pool output payloads are rented from.</summary>
    private readonly ArrayPool<byte> _pool;

    /// <summary>Cancelled when the pipeline is aborted or out of drain time.</summary>
    private readonly CancellationToken _token;

    /// <summary>The queue from the receive loop.</summary>
    private readonly Channel<ManagedVideoInput> _queue = Channel.CreateUnbounded<ManagedVideoInput>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>Decoder output of one call; every frame in it is owned by the stage until disposed.</summary>
    private readonly List<VideoFrame> _frames = [];

    /// <summary>Encoder output of one call.</summary>
    private readonly List<H264EncodedAccessUnit> _units = [];

    /// <summary>The FLV header of the coded frame being submitted: frame type and AVC, packet type 1, composition time 0.</summary>
    private readonly byte[] _tagHeader = [0x27, 0x01, 0, 0, 0];

    /// <summary>Source timestamps of recent decode failures, oldest first.</summary>
    private readonly Queue<long> _failures = new();

    /// <summary>Submission times of recent source pictures by presentation time in milliseconds, for latency.</summary>
    private readonly (long Pts, long Ticks)[] _latency = new (long, long)[LatencyEntries];

    /// <summary>1 once a drain was requested; set before the queue completes.</summary>
    private int _drain;

    /// <summary>The stage task.</summary>
    private Task? _task;

    /// <summary>The configuration of the current decoder.</summary>
    private SourceVideoConfiguration? _configuration;

    /// <summary>The decoder.</summary>
    private H264Decoder? _decoder;

    /// <summary>The encoder, created for the first picture.</summary>
    private H264Encoder? _encoder;

    /// <summary>The last AVC sequence header submitted, or null.</summary>
    private byte[]? _announced;

    /// <summary>Whether an output stream is open (its first unit was submitted).</summary>
    private bool _streaming;

    /// <summary>Why the next output stream starts.</summary>
    private VideoStreamStartReason _nextReason = VideoStreamStartReason.Initial;

    /// <summary>The presentation time in milliseconds of the last picture encoded in the current stream.</summary>
    private long? _lastPts;

    /// <summary>The source decode time in milliseconds of the first unit decoded for the current stream.</summary>
    private long? _streamSourceStart;

    /// <summary>The 90 kHz timestamp of the last unit submitted.</summary>
    private long _lastSubmitted = long.MinValue;

    /// <summary>The source time at which the decoder started waiting for an IDR picture, while it waits.</summary>
    private long? _idrWaitStart;

    /// <summary>The colour description declared for the current source pictures, if any.</summary>
    private VideoColorDescription? _color;

    /// <summary>Whether the unspecified-colour assumption was logged.</summary>
    private bool _loggedColorAssumption;

    /// <summary>Whether a decoder recreation was logged at Warning.</summary>
    private bool _loggedRecreation;

    /// <summary>Next write index in <see cref="_latency"/>.</summary>
    private int _latencyNext;

    /// <summary>Source units decoded.</summary>
    private long _decoded;

    /// <summary>Canonical units submitted.</summary>
    private long _encoded;

    /// <summary>Decoded pictures not encoded (overlapping timestamps) and source pictures skipped awaiting an IDR picture.</summary>
    private long _dropped;

    /// <summary>Output streams started.</summary>
    private int _streams;

    /// <summary>Decoders replaced.</summary>
    private int _recreations;

    /// <summary>1 once the codecs were disposed.</summary>
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="ManagedVideoStage"/> class.</summary>
    /// <param name="publisher">The publishing session.</param>
    /// <param name="output">The multiplexer's video input.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="release">Releases a source sample's queue accounting.</param>
    /// <param name="onFailure">Told about a failure that ends the publish.</param>
    /// <param name="pool">The pool output payloads are rented from.</param>
    /// <param name="token">Cancelled when the pipeline is aborted.</param>
    public ManagedVideoStage(PublisherSession publisher, MediaMultiplexer.TrackInput output, ManagedVideoSettings settings, TranscodingMetrics metrics, ILogger logger, Action<SourceSample> release, Action<TranscodingFailedException> onFailure, ArrayPool<byte> pool, CancellationToken token)
    {
        _publisher = publisher;
        _output = output;
        _settings = settings;
        _metrics = metrics;
        _logger = logger;
        _release = release;
        _onFailure = onFailure;
        _pool = pool;
        _token = token;
    }

    /// <summary>Gets a task that completes when the stage has stopped; it never faults.</summary>
    public Task Completion => _task ?? Task.CompletedTask;

    /// <summary>Gets the source units decoded; for tests.</summary>
    internal long DecodedUnits => Interlocked.Read(ref _decoded);

    /// <summary>Gets the canonical units submitted; for tests.</summary>
    internal long EncodedUnits => Interlocked.Read(ref _encoded);

    /// <summary>Gets the pictures dropped; for tests.</summary>
    internal long DroppedPictures => Interlocked.Read(ref _dropped);

    /// <summary>Gets the output streams started; for tests.</summary>
    internal int Streams => Volatile.Read(ref _streams);

    /// <summary>Gets the decoders replaced; for tests.</summary>
    internal int Recreations => Volatile.Read(ref _recreations);

    /// <summary>
    /// Whether a source video configuration can be transcoded: H.264 whose every parameter set the managed decoder
    /// supports (8-bit, 4:2:0, progressive, Baseline/Main/High, no FMO) and whose pictures fit the managed codecs.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="settings">The settings.</param>
    /// <returns><see langword="null"/> if eligible; otherwise the rejection and a description without publisher data.</returns>
    public static (TranscodingRejectionReason Reason, string Detail)? CheckEligibility(SourceVideoConfiguration configuration, ManagedVideoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(settings);
        if (configuration.Codec != SourceVideoCodec.H264)
        {
            return (TranscodingRejectionReason.UnsupportedCodec, $"Source video {configuration.Description} is not supported: only H.264 video is transcoded.");
        }

        if (!AvcDecoderConfiguration.TryParse(configuration.Record, out var record, out _) || record.NalLengthSize == 3)
        {
            return (TranscodingRejectionReason.UnsupportedCodec, "The AVC decoder configuration record is not supported by the managed decoder.");
        }

        try
        {
            foreach (var nal in record.SequenceParameterSets)
            {
                var sps = H264Sps.Parse(Rbsp(nal.Span));
                if (sps.UnsupportedReason is { } reason)
                {
                    return (TranscodingRejectionReason.UnsupportedCodec, $"Source video {configuration.Description} is not supported: {reason}");
                }

                if (sps.DisplayWidth < 16 || sps.DisplayHeight < 16)
                {
                    return (TranscodingRejectionReason.ResolutionLimit, $"The video picture is {sps.DisplayWidth}x{sps.DisplayHeight}; at least 16x16 is required.");
                }

                var decoder = settings.Decoder;
                if (sps.DisplayWidth > decoder.MaxWidth || sps.DisplayHeight > decoder.MaxHeight || sps.MbCount > decoder.MaxMacroblocks)
                {
                    return (TranscodingRejectionReason.ResolutionLimit, $"The video picture is {sps.DisplayWidth}x{sps.DisplayHeight}; at most {decoder.MaxWidth}x{decoder.MaxHeight} and {decoder.MaxMacroblocks} macroblocks are accepted.");
                }
            }

            foreach (var nal in record.PictureParameterSets)
            {
                if (H264Pps.Parse(Rbsp(nal.Span)).UnsupportedReason is { } reason)
                {
                    return (TranscodingRejectionReason.UnsupportedCodec, $"Source video {configuration.Description} is not supported: {reason}");
                }
            }
        }
        catch (CodecException ex)
        {
            return (TranscodingRejectionReason.UnsupportedCodec, $"Source video {configuration.Description} is not supported: a parameter set is not accepted by the managed decoder ({ex.Kind}).");
        }

        return null;
    }

    /// <summary>Starts the stage task.</summary>
    public void Start() => _task = Task.Run(RunAsync, CancellationToken.None);

    /// <summary>Queues a source unit.</summary>
    /// <param name="sample">The sample; ownership transfers when queued.</param>
    /// <returns><see langword="true"/> if queued; <see langword="false"/> if the stage has stopped.</returns>
    public bool TryWrite(SourceSample sample) => _queue.Writer.TryWrite(new ManagedVideoInput(sample, Stopwatch.GetTimestamp()));

    /// <summary>Ends the input. When draining, queued units are transcoded and the decoder drained before the track completes.</summary>
    /// <param name="drain">Whether to drain.</param>
    public void Complete(bool drain)
    {
        if (drain)
        {
            Volatile.Write(ref _drain, 1);
        }

        _queue.Writer.TryComplete();
    }

    /// <summary>Disposes the codecs; call only after <see cref="Completion"/> has completed or before <see cref="Start"/>.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DisposeFrames();
        DisposeUnits();
        _decoder?.Dispose();
        _encoder?.Dispose();
    }

    /// <summary>Returns the RBSP of a parameter set NAL unit.</summary>
    /// <param name="nal">The NAL unit including its header byte.</param>
    /// <returns>The RBSP.</returns>
    /// <exception cref="CodecException">The NAL unit is malformed.</exception>
    private static byte[] Rbsp(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 2)
        {
            throw new CodecException(CodecErrorKind.InvalidData, "A parameter set NAL unit is truncated.");
        }

        var rbsp = new byte[nal.Length - 1];
        return rbsp[..H264NalUnits.Unescape(nal[1..], rbsp)];
    }

    /// <summary>The stage task.</summary>
    /// <returns>A task that completes when stopped.</returns>
    private async Task RunAsync()
    {
        try
        {
            await foreach (var input in _queue.Reader.ReadAllAsync(_token).ConfigureAwait(false))
            {
                await ProcessAsync(input).ConfigureAwait(false);
            }

            if (Volatile.Read(ref _drain) == 0 || _token.IsCancellationRequested)
            {
                return;
            }

            await EndStreamAsync().ConfigureAwait(false);
            _output.Complete();
        }
        catch (OperationCanceledException)
        {
            // Aborted, out of drain time, or the multiplexer stopped.
        }
        catch (Exception ex)
        {
            if (!_token.IsCancellationRequested)
            {
                _onFailure(ex as TranscodingFailedException ?? new TranscodingFailedException(TranscodingStage.Pipeline, Interlocked.Read(ref _encoded) == 0, "The managed video stage failed.", ex));
            }
        }
        finally
        {
            // Completing the queue first makes a racing TryWrite fail rather than strand its sample.
            _queue.Writer.TryComplete();
            Dispose();
            while (_queue.Reader.TryRead(out var remaining))
            {
                _release(remaining.Sample);
                remaining.Sample.Dispose();
            }

            LogSummary(_publisher.SessionId, DecodedUnits, EncodedUnits, DroppedPictures, Streams, Recreations);
        }
    }

    /// <summary>Transcodes one source unit.</summary>
    /// <param name="input">The input; its sample is released here.</param>
    /// <returns>A task that completes when its output was submitted.</returns>
    private async ValueTask ProcessAsync(ManagedVideoInput input)
    {
        var sample = input.Sample;
        var released = false;
        try
        {
            var configuration = sample.Configuration.Video ?? throw new InvalidOperationException("A managed video sample has no video configuration.");
            if (_decoder is null || !ReferenceEquals(configuration, _configuration))
            {
                await ReconfigureAsync(configuration).ConfigureAwait(false);
            }
            else if (sample.Discontinuity != TimestampDiscontinuity.None)
            {
                await EndStreamAsync().ConfigureAwait(false);
                _nextReason = VideoStreamStartReason.Discontinuity;
            }

            _color = sample.Configuration.Color;
            _streamSourceStart ??= sample.DecodeTimestamp;
            if (_announced is null)
            {
                await AnnounceAsync(configuration, sample.DecodeTimestamp).ConfigureAwait(false);
            }

            var pts = sample.DecodeTimestamp + sample.CompositionTimeOffset;
            RecordSubmission(pts, input.Submitted);
            var skipped = _decoder!.SkippedPictures;
            try
            {
                _decoder.Decode(new EncodedPacket(sample.Data, pts, MediaTimeBase.Milliseconds), _frames, _token);
            }
            catch (CodecException ex) when (ex.Kind == CodecErrorKind.InvalidData)
            {
                Release(sample, ref released);
                RecordFailure(sample.DecodeTimestamp);
                RecreateDecoder();
                Interlocked.Increment(ref _dropped);
                _metrics.Dropped(MediaTrack.Video, TranscodingDropReason.DecodeError);
                await EncodeFramesAsync().ConfigureAwait(false);
                return;
            }
            catch (CodecException ex)
            {
                throw new TranscodingFailedException(
                    TranscodingStage.Decoder,
                    Interlocked.Read(ref _encoded) == 0,
                    ex.Kind == CodecErrorKind.Unsupported
                        ? "The source video uses an H.264 feature the managed decoder does not support: " + ex.Message
                        : "The source video exceeds a managed decoder limit: " + ex.Message,
                    ex);
            }

            Release(sample, ref released);
            Interlocked.Increment(ref _decoded);
            _metrics.InputFrame(MediaTrack.Video, TranscodingMetrics.CodecName(SourceVideoCodec.H264));
            if (_decoder.SkippedPictures > skipped)
            {
                Interlocked.Increment(ref _dropped);
                _metrics.Dropped(MediaTrack.Video, TranscodingDropReason.AwaitingKeyframe);
            }

            CheckIdrWait(sample.DecodeTimestamp);
            await EncodeFramesAsync().ConfigureAwait(false);
        }
        finally
        {
            Release(sample, ref released);
        }
    }

    /// <summary>
    /// Creates the encoder for the source picture size and submits its sequence header before the first picture is
    /// decoded. The multiplexer releases a leading sequence header while it still holds audio for the first video
    /// keyframe, so the media stage has both configurations before any sample, as from an FLV source.
    /// </summary>
    /// <param name="configuration">The source configuration.</param>
    /// <param name="timestamp">The first source unit's decode time in milliseconds.</param>
    /// <returns>A task that completes when submitted.</returns>
    private async ValueTask AnnounceAsync(SourceVideoConfiguration configuration, long timestamp)
    {
        if (!AvcDecoderConfiguration.TryParse(configuration.Record, out var record, out _) || record.SequenceParameterSets.Count == 0)
        {
            return;
        }

        H264Sps sps;
        try
        {
            sps = H264Sps.Parse(Rbsp(record.SequenceParameterSets[0].Span));
        }
        catch (CodecException)
        {
            return;
        }

        EnsureEncoder(sps.DisplayWidth, sps.DisplayHeight);
        _announced = _encoder!.DecoderConfigurationRecord.ToArray();
        _lastSubmitted = Math.Max(_lastSubmitted, timestamp * 90);
        await SubmitAsync(EncodedUnitKind.SequenceStart, _lastSubmitted, true, SequenceHeaderPrefix, _announced).ConfigureAwait(false);
    }

    /// <summary>Releases a sample once.</summary>
    /// <param name="sample">The sample.</param>
    /// <param name="released">Whether it was released.</param>
    private void Release(SourceSample sample, ref bool released)
    {
        if (!released)
        {
            released = true;
            _release(sample);
            sample.Dispose();
        }
    }

    /// <summary>Ends the current stream, if any, and replaces the decoder for a new configuration.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>A task that completes when the old stream's output was submitted.</returns>
    private async ValueTask ReconfigureAsync(SourceVideoConfiguration configuration)
    {
        if (_decoder is not null)
        {
            await EndStreamAsync().ConfigureAwait(false);
            _nextReason = VideoStreamStartReason.Configuration;
            _decoder.Dispose();
            _decoder = null;
        }

        _decoder = CreateDecoder(configuration);
        _configuration = configuration;
        _idrWaitStart = null;
    }

    /// <summary>Creates a decoder for a configuration.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The configured decoder.</returns>
    private H264Decoder CreateDecoder(SourceVideoConfiguration configuration)
    {
        var decoder = new H264Decoder(_settings.Decoder);
        try
        {
            decoder.Configure(H264DecoderConfiguration.FromAvcC(configuration.Record));
            return decoder;
        }
        catch (CodecException ex)
        {
            decoder.Dispose();
            throw new TranscodingFailedException(TranscodingStage.Decoder, Interlocked.Read(ref _encoded) == 0, "The managed video decoder does not accept the source video configuration.", ex);
        }
    }

    /// <summary>Replaces a decoder that failed with one for the same configuration; the new one resumes at the next IDR picture.</summary>
    private void RecreateDecoder()
    {
        _decoder!.Dispose();
        _decoder = null;
        _decoder = CreateDecoder(_configuration!);
        _idrWaitStart = null;
        Interlocked.Increment(ref _recreations);
        _metrics.VideoDecoderRecreated();
        if (!_loggedRecreation)
        {
            _loggedRecreation = true;
            LogFirstDecoderRecreated(_publisher.SessionId);
        }
        else
        {
            LogDecoderRecreated(_publisher.SessionId);
        }
    }

    /// <summary>Records a decode failure and ends the publish when failures persist.</summary>
    /// <param name="timestamp">The unit's source decode timestamp in milliseconds.</param>
    private void RecordFailure(long timestamp)
    {
        _failures.Enqueue(timestamp);
        while (_failures.Count > 0 && (timestamp - _failures.Peek() >= FailureWindowMilliseconds || _failures.Peek() > timestamp))
        {
            _failures.Dequeue();
        }

        if (_failures.Count > MaxDecodeFailures)
        {
            throw new TranscodingFailedException(TranscodingStage.Decoder, Interlocked.Read(ref _encoded) == 0, "The managed video decoder rejected too many source video frames.");
        }
    }

    /// <summary>Ends the publish when the decoder has waited too long for an IDR picture.</summary>
    /// <param name="timestamp">The unit's source decode timestamp in milliseconds.</param>
    private void CheckIdrWait(long timestamp)
    {
        if (!_decoder!.AwaitingIdr)
        {
            _idrWaitStart = null;
            return;
        }

        if (_idrWaitStart is not { } start || timestamp < start)
        {
            _idrWaitStart = timestamp;
            return;
        }

        if (timestamp - start > MaxIdrWaitMilliseconds)
        {
            throw new TranscodingFailedException(TranscodingStage.Decoder, Interlocked.Read(ref _encoded) == 0, $"The source video sent no decodable IDR picture for {MaxIdrWaitMilliseconds / 1000} seconds; H.264 streams without IDR pictures are not supported.");
        }
    }

    /// <summary>Ends the output stream: drains the decoder, encodes what it held and lets the next picture start a new stream.</summary>
    /// <returns>A task that completes when submitted.</returns>
    private async ValueTask EndStreamAsync()
    {
        if (_decoder is not null)
        {
            try
            {
                _decoder.Drain(_frames);
            }
            catch (CodecException ex)
            {
                DisposeFrames();
                throw new TranscodingFailedException(TranscodingStage.Decoder, Interlocked.Read(ref _encoded) == 0, "The managed video decoder could not be drained.", ex);
            }

            await EncodeFramesAsync().ConfigureAwait(false);
        }

        if (_encoder is not null && _streaming)
        {
            _encoder.Flush(_units);
            await SubmitUnitsAsync().ConfigureAwait(false);
        }

        _streaming = false;
        _lastPts = null;
        _streamSourceStart = null;
    }

    /// <summary>Encodes and disposes the frames the decoder output.</summary>
    /// <returns>A task that completes when their units were submitted.</returns>
    private async ValueTask EncodeFramesAsync()
    {
        try
        {
            for (var i = 0; i < _frames.Count; i++)
            {
                await EncodeFrameAsync(_frames[i], _color).ConfigureAwait(false);
                _frames[i].Dispose();
            }
        }
        finally
        {
            DisposeFrames();
        }
    }

    /// <summary>Converts and encodes one decoded picture and submits its unit.</summary>
    /// <param name="frame">The picture; the caller keeps ownership.</param>
    /// <param name="color">The declared colour description of the source, if any.</param>
    /// <returns>A task that completes when submitted.</returns>
    private async ValueTask EncodeFrameAsync(VideoFrame frame, VideoColorDescription? color)
    {
        var pts = frame.Timestamp;
        if (_lastPts is { } last && pts <= last)
        {
            Interlocked.Increment(ref _dropped);
            _metrics.Dropped(MediaTrack.Video, TranscodingDropReason.Overlap);
            return;
        }

        EnsureEncoder(frame.Width, frame.Height);
        VideoFrame? converted = null;
        VideoFrame? relabelled = null;
        try
        {
            var input = frame;
            if (color is { IsHighDynamicRange: true })
            {
                // E-RTMP colorInfo declared BT.2100; it describes the source in place of the bitstream's tags.
                relabelled = new VideoFrame(frame.Width, frame.Height, frame.Timestamp, frame.TimeBase, new VideoColorInfo((byte)color.Primaries, (byte)color.Transfer, (byte)color.Matrix, frame.Color.FullRange));
                for (var plane = 0; plane < 3; plane++)
                {
                    frame.GetPlane(plane).CopyTo(relabelled.GetPlane(plane));
                }

                input = relabelled;
            }

            if (input.Color != VideoColorInfo.Bt709)
            {
                if (!_loggedColorAssumption && (input.Color.Primaries == 2 || input.Color.Transfer == 2 || input.Color.Matrix == 2))
                {
                    _loggedColorAssumption = true;
                    LogColorAssumed(_publisher.SessionId);
                }

                converted = new VideoFrame(frame.Width, frame.Height, frame.Timestamp, frame.TimeBase, VideoColorInfo.Bt709);
                try
                {
                    VideoColorConverter.Convert(VideoImage.FromFrame(input), converted, ColorPolicy, _token);
                }
                catch (CodecException ex)
                {
                    throw new TranscodingFailedException(TranscodingStage.Encoder, Interlocked.Read(ref _encoded) == 0, "The source video colour cannot be converted to BT.709: " + ex.Message, ex);
                }

                input = converted;
            }

            try
            {
                _encoder!.Encode(input, _units, forceKeyframe: false, _token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new TranscodingFailedException(TranscodingStage.Encoder, Interlocked.Read(ref _encoded) == 0, "The managed video encoder failed.", ex);
            }
        }
        finally
        {
            converted?.Dispose();
            relabelled?.Dispose();
        }

        _lastPts = pts;
        await SubmitUnitsAsync().ConfigureAwait(false);
    }

    /// <summary>Creates the encoder, or reconfigures it for a new picture size (which starts a new stream).</summary>
    /// <param name="width">The picture width.</param>
    /// <param name="height">The picture height.</param>
    private void EnsureEncoder(int width, int height)
    {
        if (_encoder is { } current && current.Options.Width == width && current.Options.Height == height)
        {
            return;
        }

        var options = new H264EncoderOptions
        {
            Width = width,
            Height = height,
            KeyframeInterval = _settings.KeyframeInterval,
            RateControl = H264RateControlMode.ConstantQuality,
            BaseQp = _settings.Quantizer,
            MaxBitrateKilobits = _settings.MaxBitrateKilobits,
            MaxFrameRate = _settings.MaxFrameRate,
            Format = H264OutputFormat.LengthPrefixed,
        };
        try
        {
            if (_encoder is null)
            {
                _encoder = new H264Encoder(options);
            }
            else
            {
                _encoder.Reconfigure(options);
                _streaming = false;
                _nextReason = VideoStreamStartReason.Configuration;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or CodecException)
        {
            throw new TranscodingFailedException(TranscodingStage.Encoder, Interlocked.Read(ref _encoded) == 0, $"The managed video encoder does not accept a {width}x{height} picture.", ex);
        }
    }

    /// <summary>Submits the encoder's output units in order, preceded by a sequence header when a stream starts or its parameter sets change.</summary>
    /// <returns>A task that completes when queued.</returns>
    private async ValueTask SubmitUnitsAsync()
    {
        try
        {
            for (var i = 0; i < _units.Count; i++)
            {
                var unit = _units[i];
                if (!_streaming)
                {
                    _streaming = true;
                    Interlocked.Increment(ref _streams);
                    _metrics.VideoStreamStarted(_nextReason);
                    LogStreamStarted(_publisher.SessionId, _nextReason, _encoder!.Options.Width, _encoder.Options.Height, _encoder.LevelIdc);
                    _output.Declare(true);
                }

                var record = _encoder!.DecoderConfigurationRecord.Span;
                if (_announced is null || !record.SequenceEqual(_announced))
                {
                    _announced = record.ToArray();

                    // Like an FLV source's, the sequence header is stamped at the stream's first decode time, so the
                    // media stage has it before audio the multiplexer orders ahead of the first picture.
                    var start = Math.Max(Math.Min(unit.Timestamp, (_streamSourceStart ?? long.MaxValue / 90) * 90), _lastSubmitted);
                    await SubmitAsync(EncodedUnitKind.SequenceStart, start, true, SequenceHeaderPrefix, _announced).ConfigureAwait(false);
                }

                if (TagHeaderSize + unit.Length > _settings.MaxSampleSize)
                {
                    throw new TranscodingFailedException(TranscodingStage.Encoder, Interlocked.Read(ref _encoded) == 0, $"An encoded picture of {unit.Length} bytes exceeds Media:MaxSampleSize.");
                }

                _tagHeader[0] = unit.IsKeyframe ? (byte)0x17 : (byte)0x27;
                await SubmitAsync(EncodedUnitKind.Sample, unit.Timestamp, unit.IsKeyframe, _tagHeader, unit.Data).ConfigureAwait(false);
                _lastSubmitted = unit.Timestamp;
                RecordLatency(H264Encoder.TimeBase.Rescale(unit.Timestamp, MediaTimeBase.Milliseconds));
                unit.Dispose();
            }
        }
        finally
        {
            DisposeUnits();
        }
    }

    /// <summary>Submits one FLV tag body to the multiplexer.</summary>
    /// <param name="kind">What it carries.</param>
    /// <param name="timestamp">Its 1/90000 timestamp.</param>
    /// <param name="sync">Whether it is a sync point.</param>
    /// <param name="header">The tag header bytes.</param>
    /// <param name="data">The tag data.</param>
    /// <returns>A task that completes when queued.</returns>
    /// <exception cref="OperationCanceledException">The multiplexer stopped or failed; its owner already knows why.</exception>
    private async ValueTask SubmitAsync(EncodedUnitKind kind, long timestamp, bool sync, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> data)
    {
        var payload = new PooledBuffer(_pool);
        payload.EnsureCapacity(checked(header.Length + data.Length));
        payload.Append(new ReadOnlySequence<byte>(header));
        payload.Append(new ReadOnlySequence<byte>(data));
        try
        {
            await _output.SubmitAsync(new EncodedAccessUnit(kind, timestamp, H264Encoder.TimeBase, sync, payload), _token).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new OperationCanceledException("The media multiplexer is no longer accepting video.", ex);
        }

        if (kind == EncodedUnitKind.Sample)
        {
            Interlocked.Increment(ref _encoded);
            _metrics.OutputFrame(MediaTrack.Video);
        }
    }

    /// <summary>Disposes every frame still held.</summary>
    private void DisposeFrames()
    {
        foreach (var frame in _frames)
        {
            frame.Dispose();
        }

        _frames.Clear();
    }

    /// <summary>Disposes every unit still held.</summary>
    private void DisposeUnits()
    {
        foreach (var unit in _units)
        {
            unit.Dispose();
        }

        _units.Clear();
    }

    /// <summary>Records when a source picture with a presentation time was submitted.</summary>
    /// <param name="pts">The presentation time in milliseconds.</param>
    /// <param name="submitted">The submission time.</param>
    private void RecordSubmission(long pts, long submitted)
    {
        _latency[_latencyNext] = (pts, submitted);
        _latencyNext = (_latencyNext + 1) % LatencyEntries;
    }

    /// <summary>Records the latency of an encoded picture, if its submission is still in the ring.</summary>
    /// <param name="pts">The presentation time in milliseconds.</param>
    private void RecordLatency(long pts)
    {
        for (var i = 0; i < LatencyEntries; i++)
        {
            if (_latency[i].Ticks != 0 && _latency[i].Pts == pts)
            {
                _metrics.Latency(Stopwatch.GetElapsedTime(_latency[i].Ticks).TotalSeconds);
                _latency[i] = default;
                return;
            }
        }
    }

    /// <summary>Logs a started output stream.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="reason">Why it started.</param>
    /// <param name="width">The canonical width.</param>
    /// <param name="height">The canonical height.</param>
    /// <param name="level">The H.264 level_idc.</param>
    [LoggerMessage(EventId = 10000, Level = LogLevel.Information, Message = "Session {SessionId} managed video stream started ({Reason}): canonical H.264 High {Width}x{Height}, level_idc {Level}")]
    private partial void LogStreamStarted(Guid sessionId, VideoStreamStartReason reason, int width, int height, int level);

    /// <summary>Logs that unspecified source colour fields are taken as BT.709.</summary>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 10032, Level = LogLevel.Information, Message = "Session {SessionId} source video does not specify every colour field; BT.709 is assumed for the unspecified fields")]
    private partial void LogColorAssumed(Guid sessionId);

    /// <summary>Logs the first replaced decoder.</summary>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 10033, Level = LogLevel.Warning, Message = "Session {SessionId} managed video decoder rejected a source frame and was recreated; video resumes at the next IDR picture. Further recreations are logged at Debug")]
    private partial void LogFirstDecoderRecreated(Guid sessionId);

    /// <summary>Logs a replaced decoder.</summary>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 10034, Level = LogLevel.Debug, Message = "Session {SessionId} managed video decoder rejected a source frame and was recreated")]
    private partial void LogDecoderRecreated(Guid sessionId);

    /// <summary>Logs the stage summary.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="decoded">Source units decoded.</param>
    /// <param name="encoded">Canonical units submitted.</param>
    /// <param name="dropped">Pictures dropped.</param>
    /// <param name="streams">Output streams started.</param>
    /// <param name="recreations">Decoders replaced.</param>
    [LoggerMessage(EventId = 10035, Level = LogLevel.Information, Message = "Managed video for session {SessionId} ended: {Decoded} source frames decoded, {Encoded} canonical frames, {Dropped} pictures dropped, {Streams} output streams, {Recreations} decoder recreations")]
    private partial void LogSummary(Guid sessionId, long decoded, long encoded, long dropped, int streams, int recreations);
}
