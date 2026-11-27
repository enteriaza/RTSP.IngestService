// The managed AAC-LC audio stage of one publisher: a task that decodes queued AAC-LC source access units with the
// managed decoder, converts them to 48 kHz stereo (downmix, resample, clip), encodes canonical AAC-LC and submits the
// FLV-framed units to the session's media multiplexer, independently of the managed video stage. Its timeline
// controller keeps canonical audio on integer 48 kHz sample positions anchored at the first source timestamp:
// differences between the source timestamps and the decoded duration of up to 100 ms are absorbed, larger gaps are
// concealed with silence and larger overlaps trimmed, and beyond one second, or at a source discontinuity, the output
// stream is ended and restarted. A malformed access unit replaces the decoder and is concealed; persistent decode
// failures, and any encoder or conversion failure, end the publish. No codec configuration or payload is logged.

using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using RTSP.IngestService.Media;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;
using RTSP.IngestService.Media.Codecs.Audio.Processing;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Transcoding;

/// <summary>Why a source audio configuration cannot be transcoded by the managed audio stage.</summary>
internal enum ManagedAudioIneligibility
{
    /// <summary>The source audio is not AAC.</summary>
    Codec,

    /// <summary>The AAC profile or configuration is not supported by the managed decoder (HE-AAC, 960-sample frames, ...).</summary>
    Profile,

    /// <summary>The AudioSpecificConfig is invalid.</summary>
    Configuration,
}

/// <summary>A source audio access unit queued for the managed audio stage.</summary>
/// <param name="Sample">The sample; ownership transfers with the input.</param>
/// <param name="DroppedBefore">Source units dropped immediately before it.</param>
/// <param name="Submitted">When it was submitted, as a <see cref="Stopwatch"/> timestamp.</param>
internal readonly record struct ManagedAudioInput(SourceSample Sample, int DroppedBefore, long Submitted);

/// <summary>Transcodes one publisher's AAC-LC audio with the managed codecs.</summary>
/// <remarks>
/// <see cref="TryWrite"/> is called only from the session's receive loop and <see cref="Complete"/> once, when the
/// session stops; everything else runs on the stage's own task.
/// </remarks>
internal sealed partial class ManagedAudioStage
{
    /// <summary>Source timestamp error, in milliseconds, absorbed without correction (FFmpeg's <c>min_hard_comp</c>).</summary>
    internal const int HardCompensationMilliseconds = 100;

    /// <summary>The longest gap, in milliseconds, concealed with silence; longer gaps restart the output stream.</summary>
    internal const int MaxConcealedGapMilliseconds = 1000;

    /// <summary>The longest overlap, in milliseconds, trimmed; longer overlaps restart the output stream.</summary>
    internal const int MaxOverlapTrimMilliseconds = 1000;

    /// <summary>Consecutive decode failures tolerated; one more ends the publish.</summary>
    internal const int MaxConsecutiveDecodeFailures = 50;

    /// <summary>The window, in milliseconds of source time, over which the decode failure rate is measured.</summary>
    internal const long FailureWindowMilliseconds = 10_000;

    /// <summary>The decode failure rate, in percent of the window's units, above which the publish ends.</summary>
    internal const int MaxFailurePercent = 10;

    /// <summary>The 48 kHz time base of every position and output timestamp.</summary>
    private static readonly MediaTimeBase Canonical = new(1, AacEncoder.SampleRate);

    /// <summary>The largest silent frame inserted at once, within the resampler's input limit.</summary>
    private const int ConcealChunk = 16384;

    /// <summary>The FLV AAC sequence header: sound format 10, 44 kHz flag, 16-bit, stereo; packet type 0.</summary>
    private static readonly byte[] SequenceHeader = [0xAF, 0x00, .. AacEncoder.AudioSpecificConfig];

    /// <summary>The publishing session.</summary>
    private readonly PublisherSession _publisher;

    /// <summary>The multiplexer's audio input.</summary>
    private readonly MediaMultiplexer.TrackInput _output;

    /// <summary>The canonical bitrate in bits per second.</summary>
    private readonly int _bitrate;

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
    private readonly Channel<ManagedAudioInput> _queue = Channel.CreateUnbounded<ManagedAudioInput>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>Encoder output of one call.</summary>
    private readonly List<AacAccessUnit> _units = [];

    /// <summary>Builds one FLV tag body.</summary>
    private readonly byte[] _body = new byte[2 + (AacEncoder.MaxFrameBits / 8)];

    /// <summary>Source timestamps of the units in the failure window, oldest first, with whether each failed.</summary>
    private readonly Queue<(long Timestamp, bool Failed)> _window = new();

    /// <summary>Concealment reasons already logged at Warning.</summary>
    private readonly HashSet<AudioConcealmentReason> _loggedConcealments = [];

    /// <summary>1 once a drain was requested; set before the queue completes.</summary>
    private int _drain;

    /// <summary>The stage task.</summary>
    private Task? _task;

    /// <summary>The configuration of the current decoder.</summary>
    private SourceAudioConfiguration? _configuration;

    /// <summary>The decoder.</summary>
    private AacDecoder? _decoder;

    /// <summary>The decoder's channel layout.</summary>
    private ReadOnlyMemory<AudioChannel> _layout;

    /// <summary>The source sample rate.</summary>
    private int _rate;

    /// <summary>The converter of the current segment.</summary>
    private CanonicalAudioConverter? _converter;

    /// <summary>The encoder, created with the first stream.</summary>
    private AacEncoder? _encoder;

    /// <summary>Whether an output stream is open.</summary>
    private bool _streaming;

    /// <summary>Whether the sequence header was submitted.</summary>
    private bool _announced;

    /// <summary>The 48 kHz position of the current stream's first sample.</summary>
    private long _streamStart;

    /// <summary>Canonical samples fed to the encoder in the current stream.</summary>
    private long _canonicalFed;

    /// <summary>The 48 kHz position of the current segment's first sample; a segment is one stream under one configuration.</summary>
    private long _segmentStart;

    /// <summary>Source samples fed to the converter in the current segment, including silence.</summary>
    private long _consumed;

    /// <summary>Clipped samples already counted, of the current converter.</summary>
    private long _clippedReported;

    /// <summary>Consecutive decode failures.</summary>
    private int _consecutiveFailures;

    /// <summary>Failed units in <see cref="_window"/>.</summary>
    private int _windowFailures;

    /// <summary>Source units decoded.</summary>
    private long _decoded;

    /// <summary>Canonical units submitted.</summary>
    private long _encoded;

    /// <summary>Silent source samples inserted.</summary>
    private long _concealed;

    /// <summary>Source samples trimmed.</summary>
    private long _trimmed;

    /// <summary>Output streams restarted.</summary>
    private int _resets;

    /// <summary>Decoders replaced.</summary>
    private int _recreations;

    /// <summary>Whether a decoder recreation was logged at Warning.</summary>
    private bool _loggedRecreation;

    /// <summary>Initializes a new instance of the <see cref="ManagedAudioStage"/> class.</summary>
    /// <param name="publisher">The publishing session.</param>
    /// <param name="output">The multiplexer's audio input.</param>
    /// <param name="bitrate">The canonical bitrate in bits per second.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="release">Releases a source sample's queue accounting.</param>
    /// <param name="onFailure">Told about a failure that ends the publish.</param>
    /// <param name="pool">The pool output payloads are rented from.</param>
    /// <param name="token">Cancelled when the pipeline is aborted.</param>
    public ManagedAudioStage(PublisherSession publisher, MediaMultiplexer.TrackInput output, int bitrate, TranscodingMetrics metrics, ILogger logger, Action<SourceSample> release, Action<TranscodingFailedException> onFailure, ArrayPool<byte> pool, CancellationToken token)
    {
        _publisher = publisher;
        _output = output;
        _bitrate = bitrate;
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

    /// <summary>Gets the silent source samples inserted; for tests.</summary>
    internal long ConcealedSamples => Interlocked.Read(ref _concealed);

    /// <summary>Gets the source samples trimmed; for tests.</summary>
    internal long TrimmedSamples => Interlocked.Read(ref _trimmed);

    /// <summary>Gets the output streams restarted; for tests.</summary>
    internal int Resets => Volatile.Read(ref _resets);

    /// <summary>Gets the decoders replaced; for tests.</summary>
    internal int Recreations => Volatile.Read(ref _recreations);

    /// <summary>Whether a source audio configuration can be transcoded by the managed engine.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="reason">Why not, when it cannot.</param>
    /// <returns><see langword="true"/> for AAC-LC that the managed decoder accepts.</returns>
    public static bool IsEligible(SourceAudioConfiguration configuration, out ManagedAudioIneligibility reason)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        reason = default;
        if (configuration.Codec != SourceAudioCodec.Aac)
        {
            reason = ManagedAudioIneligibility.Codec;
            return false;
        }

        try
        {
            AacDecoder.Create(configuration.Record).Dispose();
            return true;
        }
        catch (CodecException ex)
        {
            reason = ex.Kind == CodecErrorKind.Unsupported ? ManagedAudioIneligibility.Profile : ManagedAudioIneligibility.Configuration;
            return false;
        }
    }

    /// <summary>Starts the stage task.</summary>
    public void Start() => _task = Task.Run(RunAsync, CancellationToken.None);

    /// <summary>Queues a source unit.</summary>
    /// <param name="sample">The sample; ownership transfers when queued.</param>
    /// <param name="droppedBefore">Source units dropped immediately before it.</param>
    /// <returns><see langword="true"/> if queued; <see langword="false"/> if the stage has stopped.</returns>
    public bool TryWrite(SourceSample sample, int droppedBefore) =>
        _queue.Writer.TryWrite(new ManagedAudioInput(sample, droppedBefore, Stopwatch.GetTimestamp()));

    /// <summary>Ends the input. When draining, queued units are transcoded and the encoder flushed before the track completes.</summary>
    /// <param name="drain">Whether to drain.</param>
    public void Complete(bool drain)
    {
        if (drain)
        {
            Volatile.Write(ref _drain, 1);
        }

        _queue.Writer.TryComplete();
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
                _onFailure(ex as TranscodingFailedException ?? new TranscodingFailedException(TranscodingStage.Pipeline, Interlocked.Read(ref _encoded) == 0, "The managed audio stage failed.", ex));
            }
        }
        finally
        {
            _decoder?.Dispose();
            _encoder?.Dispose();
            while (_queue.Reader.TryRead(out var remaining))
            {
                _release(remaining.Sample);
                remaining.Sample.Dispose();
            }

            LogSummary(_publisher.SessionId, DecodedUnits, EncodedUnits, ConcealedSamples, TrimmedSamples, Resets, Recreations);
        }
    }

    /// <summary>Transcodes one source unit.</summary>
    /// <param name="input">The input; its sample is released here.</param>
    /// <returns>A task that completes when its output was submitted.</returns>
    private async ValueTask ProcessAsync(ManagedAudioInput input)
    {
        var sample = input.Sample;
        var released = false;
        try
        {
            var configuration = sample.Configuration.Audio ?? throw new InvalidOperationException("A managed audio sample has no audio configuration.");
            var timestamp = sample.DecodeTimestamp;
            long trim = 0;
            if (!_streaming)
            {
                if (_configuration is null || !SameConfiguration(configuration))
                {
                    Reconfigure(configuration);
                }

                await StartStreamAsync(timestamp).ConfigureAwait(false);
            }
            else
            {
                if (!SameConfiguration(configuration))
                {
                    await EndSegmentAsync().ConfigureAwait(false);
                    Reconfigure(configuration);
                }

                if (sample.Discontinuity != TimestampDiscontinuity.None)
                {
                    await RestartAsync(timestamp, AudioResetReason.Discontinuity).ConfigureAwait(false);
                }
                else
                {
                    if (input.DroppedBefore > 0)
                    {
                        await ConcealAsync((long)input.DroppedBefore * AacDecoder.FrameLength, AudioConcealmentReason.Drop, input.Submitted).ConfigureAwait(false);
                    }

                    var error = Error(timestamp);
                    if (error > (long)_rate * MaxConcealedGapMilliseconds / 1000)
                    {
                        await RestartAsync(timestamp, AudioResetReason.Gap).ConfigureAwait(false);
                    }
                    else if (error < -(long)_rate * MaxOverlapTrimMilliseconds / 1000)
                    {
                        await RestartAsync(timestamp, AudioResetReason.Overlap).ConfigureAwait(false);
                    }
                    else if (error > (long)_rate * HardCompensationMilliseconds / 1000)
                    {
                        await ConcealAsync(error, AudioConcealmentReason.Gap, input.Submitted).ConfigureAwait(false);
                    }
                    else if (error < -(long)_rate * HardCompensationMilliseconds / 1000)
                    {
                        trim = -error;
                    }
                }
            }

            AudioFrame frame;
            try
            {
                frame = _decoder!.Decode(new EncodedPacket(sample.Data, Position(), Canonical));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordUnit(timestamp, failed: true);
                Release(sample, ref released);
                RecreateDecoder();
                _metrics.Dropped(MediaTrack.Audio, TranscodingDropReason.DecodeError);
                await ConcealAsync(AacDecoder.FrameLength, AudioConcealmentReason.DecodeError, input.Submitted).ConfigureAwait(false);
                return;
            }

            RecordUnit(timestamp, failed: false);
            Release(sample, ref released);
            using (frame)
            {
                Interlocked.Increment(ref _decoded);
                _metrics.InputFrame(MediaTrack.Audio, TranscodingMetrics.CodecName(SourceAudioCodec.Aac));
                if (trim >= frame.SampleCount)
                {
                    Interlocked.Add(ref _trimmed, frame.SampleCount);
                    _metrics.AudioTrimmed(frame.SampleCount);
                    _metrics.Dropped(MediaTrack.Audio, TranscodingDropReason.Overlap);
                    return;
                }

                if (trim == 0)
                {
                    await FeedAsync(frame, input.Submitted).ConfigureAwait(false);
                    return;
                }

                using var rest = new AudioFrame(frame.Layout, frame.SampleCount - (int)trim, frame.SampleRate, frame.Timestamp, frame.TimeBase);
                for (var c = 0; c < frame.ChannelCount; c++)
                {
                    frame.GetChannel(c)[(int)trim..].CopyTo(rest.GetChannel(c));
                }

                Interlocked.Add(ref _trimmed, trim);
                _metrics.AudioTrimmed(trim);
                await FeedAsync(rest, input.Submitted).ConfigureAwait(false);
            }
        }
        finally
        {
            Release(sample, ref released);
        }
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

    /// <summary>Whether a configuration equals the current decoder's.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns><see langword="true"/> if the same.</returns>
    private bool SameConfiguration(SourceAudioConfiguration configuration) =>
        ReferenceEquals(configuration, _configuration) || (_configuration is not null && configuration.IsSame(_configuration));

    /// <summary>Replaces the decoder and converter for a configuration; called between segments.</summary>
    /// <param name="configuration">The configuration.</param>
    private void Reconfigure(SourceAudioConfiguration configuration)
    {
        AacDecoder decoder;
        try
        {
            decoder = AacDecoder.Create(configuration.Record);
        }
        catch (CodecException ex)
        {
            throw new TranscodingFailedException(TranscodingStage.Decoder, Interlocked.Read(ref _encoded) == 0, "The managed audio decoder does not accept the source audio configuration.", ex);
        }

        _decoder?.Dispose();
        _decoder = decoder;
        _configuration = configuration;
        _layout = decoder.Layout;
        _rate = decoder.SampleRate;
        _converter = new CanonicalAudioConverter(_layout.Span, _rate);
        _clippedReported = 0;
        _consumed = 0;
        _segmentStart = _streamStart + _canonicalFed;
    }

    /// <summary>Replaces a faulted decoder with one for the same configuration.</summary>
    private void RecreateDecoder()
    {
        _decoder!.Dispose();
        try
        {
            _decoder = AacDecoder.Create(_configuration!.Record);
        }
        catch (CodecException ex)
        {
            throw new TranscodingFailedException(TranscodingStage.Decoder, Interlocked.Read(ref _encoded) == 0, "The managed audio decoder could not be recreated.", ex);
        }

        Interlocked.Increment(ref _recreations);
        _metrics.AudioDecoderRecreated();
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

    /// <summary>Records a decoded or failed unit and ends the publish when failures persist.</summary>
    /// <param name="timestamp">The unit's source timestamp in milliseconds.</param>
    /// <param name="failed">Whether it failed to decode.</param>
    private void RecordUnit(long timestamp, bool failed)
    {
        _consecutiveFailures = failed ? _consecutiveFailures + 1 : 0;
        _window.Enqueue((timestamp, failed));
        _windowFailures += failed ? 1 : 0;
        while (_window.Count > 0 && (timestamp - _window.Peek().Timestamp >= FailureWindowMilliseconds || _window.Peek().Timestamp > timestamp))
        {
            _windowFailures -= _window.Dequeue().Failed ? 1 : 0;
        }

        if (!failed)
        {
            return;
        }

        if (ExceedsFailureThresholds(_consecutiveFailures, _windowFailures, _window.Count, _rate))
        {
            throw new TranscodingFailedException(TranscodingStage.Decoder, Interlocked.Read(ref _encoded) == 0, "The managed audio decoder rejected too many source audio frames.");
        }
    }

    /// <summary>
    /// Whether decode failures are persistent: more than <see cref="MaxConsecutiveDecodeFailures"/> in a row, or more
    /// than <see cref="MaxFailurePercent"/> of the units in the last <see cref="FailureWindowMilliseconds"/>. The rate is
    /// taken over at least a full window of units at the source rate, so a few failures at the start of a stream do
    /// not end the publish.
    /// </summary>
    /// <param name="consecutive">Consecutive failures.</param>
    /// <param name="windowFailures">Failures in the window.</param>
    /// <param name="windowUnits">Units in the window.</param>
    /// <param name="rate">The source sample rate.</param>
    /// <returns><see langword="true"/> if the publish must end.</returns>
    internal static bool ExceedsFailureThresholds(int consecutive, int windowFailures, int windowUnits, int rate)
    {
        var units = Math.Max(windowUnits, FailureWindowMilliseconds * rate / 1000 / AacDecoder.FrameLength);
        return consecutive > MaxConsecutiveDecodeFailures || windowFailures * 100L > units * MaxFailurePercent;
    }

    /// <summary>The difference, in source samples, between a source timestamp and the decoded position.</summary>
    /// <param name="timestamp">The source timestamp in milliseconds.</param>
    /// <returns>Positive when the source is ahead (a gap), negative when behind (an overlap).</returns>
    private long Error(long timestamp) =>
        Canonical.Rescale(MediaTimeBase.Milliseconds.Rescale(timestamp, Canonical) - _segmentStart, new MediaTimeBase(1, _rate)) - _consumed;

    /// <summary>The 48 kHz position of the next source sample.</summary>
    /// <returns>The position.</returns>
    private long Position() => _segmentStart + new MediaTimeBase(1, _rate).Rescale(_consumed, Canonical);

    /// <summary>Opens an output stream anchored at a source timestamp.</summary>
    /// <param name="timestamp">The source timestamp in milliseconds.</param>
    /// <returns>A task that completes when the stream is open.</returns>
    private async ValueTask StartStreamAsync(long timestamp)
    {
        _encoder ??= new AacEncoder(_bitrate);
        _streamStart = MediaTimeBase.Milliseconds.Rescale(timestamp, Canonical);
        _segmentStart = _streamStart;
        _canonicalFed = 0;
        _consumed = 0;
        _streaming = true;
        _output.Declare(true);
        if (!_announced)
        {
            _announced = true;
            await SubmitAsync(EncodedUnitKind.SequenceStart, _streamStart - AacEncoder.Delay, SequenceHeader, 0).ConfigureAwait(false);
        }
    }

    /// <summary>Ends the output stream and opens a new one at a source timestamp.</summary>
    /// <param name="timestamp">The source timestamp in milliseconds.</param>
    /// <param name="reason">Why.</param>
    /// <returns>A task that completes when restarted.</returns>
    private async ValueTask RestartAsync(long timestamp, AudioResetReason reason)
    {
        await EndStreamAsync().ConfigureAwait(false);
        _decoder!.Flush();
        Interlocked.Increment(ref _resets);
        _metrics.AudioReset(reason);
        LogRestarted(_publisher.SessionId, reason);
        await StartStreamAsync(timestamp).ConfigureAwait(false);
    }

    /// <summary>Flushes the converter into the encoder at the end of a segment.</summary>
    /// <returns>A task that completes when submitted.</returns>
    private async ValueTask EndSegmentAsync()
    {
        AudioFrame? tail;
        try
        {
            tail = _converter!.Flush();
        }
        catch (Exception ex)
        {
            throw new TranscodingFailedException(TranscodingStage.Pipeline, Interlocked.Read(ref _encoded) == 0, "The managed audio converter failed.", ex);
        }

        ReportClipped();
        if (tail is not null)
        {
            using (tail)
            {
                await EncodeAsync(tail, 0).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Ends the output stream: flushes the converter and the encoder and submits the remaining units.</summary>
    /// <returns>A task that completes when submitted.</returns>
    private async ValueTask EndStreamAsync()
    {
        if (!_streaming)
        {
            return;
        }

        await EndSegmentAsync().ConfigureAwait(false);
        _units.Clear();
        try
        {
            _encoder!.Flush(_units);
        }
        catch (Exception ex)
        {
            throw new TranscodingFailedException(TranscodingStage.Encoder, Interlocked.Read(ref _encoded) == 0, "The managed audio encoder failed.", ex);
        }

        _streaming = false;
        _consumed = 0;
        await SubmitUnitsAsync(0).ConfigureAwait(false);
    }

    /// <summary>Feeds silence in the source layout and rate.</summary>
    /// <param name="samples">The samples per channel.</param>
    /// <param name="reason">Why.</param>
    /// <param name="submitted">When the unit that revealed the gap was submitted.</param>
    /// <returns>A task that completes when fed.</returns>
    private async ValueTask ConcealAsync(long samples, AudioConcealmentReason reason, long submitted)
    {
        Interlocked.Add(ref _concealed, samples);
        _metrics.AudioConcealed(reason, samples);
        if (_loggedConcealments.Add(reason))
        {
            LogFirstConcealment(_publisher.SessionId, reason, samples, _rate);
        }
        else
        {
            LogConcealment(_publisher.SessionId, reason, samples, _rate);
        }

        for (var remaining = samples; remaining > 0;)
        {
            var count = (int)Math.Min(remaining, ConcealChunk);
            using var silence = new AudioFrame(_layout, count, _rate, Position(), Canonical);
            await FeedAsync(silence, submitted).ConfigureAwait(false);
            remaining -= count;
        }
    }

    /// <summary>Converts a source-rate frame and encodes the result.</summary>
    /// <param name="frame">The frame; the caller keeps ownership.</param>
    /// <param name="submitted">When its source unit was submitted.</param>
    /// <returns>A task that completes when the output was submitted.</returns>
    private async ValueTask FeedAsync(AudioFrame frame, long submitted)
    {
        _consumed += frame.SampleCount;
        AudioFrame? canonical;
        try
        {
            canonical = _converter!.Process(frame);
        }
        catch (Exception ex)
        {
            throw new TranscodingFailedException(TranscodingStage.Pipeline, Interlocked.Read(ref _encoded) == 0, "The managed audio converter failed.", ex);
        }

        ReportClipped();
        if (canonical is not null)
        {
            using (canonical)
            {
                await EncodeAsync(canonical, submitted).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Encodes a canonical frame and submits the units it completes.</summary>
    /// <param name="frame">The frame; the caller keeps ownership.</param>
    /// <param name="submitted">When its source unit was submitted, or 0.</param>
    /// <returns>A task that completes when submitted.</returns>
    private async ValueTask EncodeAsync(AudioFrame frame, long submitted)
    {
        _units.Clear();
        try
        {
            _encoder!.Encode(frame, _units);
        }
        catch (Exception ex)
        {
            throw new TranscodingFailedException(TranscodingStage.Encoder, Interlocked.Read(ref _encoded) == 0, "The managed audio encoder failed.", ex);
        }

        _canonicalFed += frame.SampleCount;
        await SubmitUnitsAsync(submitted).ConfigureAwait(false);
    }

    /// <summary>Submits the encoder's output units in order.</summary>
    /// <param name="submitted">When the source unit that completed them was submitted, or 0.</param>
    /// <returns>A task that completes when queued.</returns>
    private async ValueTask SubmitUnitsAsync(long submitted)
    {
        foreach (var unit in _units)
        {
            _body[0] = 0xAF;
            _body[1] = 0x01;
            unit.Data.Span.CopyTo(_body.AsSpan(2));
            var timestamp = unit.TimeBase.Rescale(unit.Timestamp, Canonical);
            await SubmitAsync(EncodedUnitKind.Sample, timestamp, _body.AsMemory(0, 2 + unit.Data.Length), submitted).ConfigureAwait(false);
        }

        _units.Clear();
    }

    /// <summary>Submits one FLV tag body to the multiplexer.</summary>
    /// <param name="kind">What it carries.</param>
    /// <param name="timestamp">Its 48 kHz timestamp.</param>
    /// <param name="body">The body, copied into a pooled payload.</param>
    /// <param name="submitted">When the source unit was submitted, or 0.</param>
    /// <returns>A task that completes when queued.</returns>
    /// <exception cref="OperationCanceledException">The multiplexer stopped or failed; its owner already knows why.</exception>
    private async ValueTask SubmitAsync(EncodedUnitKind kind, long timestamp, ReadOnlyMemory<byte> body, long submitted)
    {
        var payload = new PooledBuffer(_pool);
        payload.EnsureCapacity(body.Length);
        payload.Append(new ReadOnlySequence<byte>(body));
        try
        {
            await _output.SubmitAsync(new EncodedAccessUnit(kind, timestamp, Canonical, true, payload), _token).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new OperationCanceledException("The media multiplexer is no longer accepting audio.", ex);
        }

        if (kind != EncodedUnitKind.Sample)
        {
            return;
        }

        Interlocked.Increment(ref _encoded);
        _metrics.OutputFrame(MediaTrack.Audio);
        if (submitted != 0)
        {
            _metrics.AudioLatency(Stopwatch.GetElapsedTime(submitted).TotalSeconds);
        }
    }

    /// <summary>Counts newly clipped samples.</summary>
    private void ReportClipped()
    {
        var clipped = _converter!.ClippedSamples;
        if (clipped > _clippedReported)
        {
            _metrics.AudioClipped(clipped - _clippedReported);
            _clippedReported = clipped;
        }
    }

    /// <summary>Logs the first concealment of a reason.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="reason">Why.</param>
    /// <param name="samples">Samples inserted.</param>
    /// <param name="rate">Their sample rate.</param>
    [LoggerMessage(EventId = 10026, Level = LogLevel.Warning, Message = "Session {SessionId} managed audio inserted {Samples} samples of silence at {Rate} Hz ({Reason}); further concealment for this reason is logged at Debug")]
    private partial void LogFirstConcealment(Guid sessionId, AudioConcealmentReason reason, long samples, int rate);

    /// <summary>Logs a repeated concealment.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="reason">Why.</param>
    /// <param name="samples">Samples inserted.</param>
    /// <param name="rate">Their sample rate.</param>
    [LoggerMessage(EventId = 10027, Level = LogLevel.Debug, Message = "Session {SessionId} managed audio inserted {Samples} samples of silence at {Rate} Hz ({Reason})")]
    private partial void LogConcealment(Guid sessionId, AudioConcealmentReason reason, long samples, int rate);

    /// <summary>Logs a restarted output stream.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="reason">Why.</param>
    [LoggerMessage(EventId = 10028, Level = LogLevel.Information, Message = "Session {SessionId} managed audio ended its output stream and started a new one ({Reason})")]
    private partial void LogRestarted(Guid sessionId, AudioResetReason reason);

    /// <summary>Logs the first replaced decoder.</summary>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 10029, Level = LogLevel.Warning, Message = "Session {SessionId} managed audio decoder rejected a source frame and was recreated; the frame is concealed with silence. Further recreations are logged at Debug")]
    private partial void LogFirstDecoderRecreated(Guid sessionId);

    /// <summary>Logs a replaced decoder.</summary>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 10030, Level = LogLevel.Debug, Message = "Session {SessionId} managed audio decoder rejected a source frame and was recreated")]
    private partial void LogDecoderRecreated(Guid sessionId);

    /// <summary>Logs the stage summary.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="decoded">Source units decoded.</param>
    /// <param name="encoded">Canonical units submitted.</param>
    /// <param name="concealed">Silent samples inserted.</param>
    /// <param name="trimmed">Samples trimmed.</param>
    /// <param name="resets">Output streams restarted.</param>
    /// <param name="recreations">Decoders replaced.</param>
    [LoggerMessage(EventId = 10031, Level = LogLevel.Information, Message = "Managed audio for session {SessionId} ended: {Decoded} source frames decoded, {Encoded} canonical frames, {Concealed} samples concealed, {Trimmed} samples trimmed, {Resets} stream restarts, {Recreations} decoder recreations")]
    private partial void LogSummary(Guid sessionId, long decoded, long encoded, long concealed, long trimmed, int resets, int recreations);
}
