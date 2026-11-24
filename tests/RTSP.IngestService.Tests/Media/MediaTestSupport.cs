// Building blocks for media processing tests: FLV packet builders, a recording IMediaSessionHandler that copies and
// disposes samples (or retains them, or blocks), a harness that drives MediaPublishSessionHandler directly with
// pooled messages, and a minimal FLV file reader for the FFmpeg-generated fixture.

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Media;

/// <summary>Builds FLV audio and video tag bodies.</summary>
internal static class FlvPackets
{
    /// <summary>The FFmpeg AAC-LC 44.1 kHz stereo AudioSpecificConfig.</summary>
    public static readonly byte[] AacConfig = [0x12, 0x10];

    /// <summary>Builds an AVC sequence header packet.</summary>
    /// <param name="record">The AVCDecoderConfigurationRecord.</param>
    /// <returns>The packet.</returns>
    public static byte[] AvcSequenceHeader(byte[]? record = null) => [0x17, 0x00, 0, 0, 0, .. record ?? AvcDecoderConfigurationTests.Record()];

    /// <summary>Builds an AVC NALU packet.</summary>
    /// <param name="keyframe">Whether the frame type is keyframe.</param>
    /// <param name="compositionTime">The signed 24-bit composition time.</param>
    /// <param name="nalLengthSize">The length-prefix size.</param>
    /// <param name="nalUnits">The NAL units.</param>
    /// <returns>The packet.</returns>
    public static byte[] AvcNalu(bool keyframe, int compositionTime, int nalLengthSize, params byte[][] nalUnits)
    {
        var bytes = new List<byte> { (byte)(keyframe ? 0x17 : 0x27), 0x01, (byte)(compositionTime >> 16), (byte)(compositionTime >> 8), (byte)compositionTime };
        foreach (var nal in nalUnits)
        {
            for (var shift = (nalLengthSize - 1) * 8; shift >= 0; shift -= 8)
            {
                bytes.Add((byte)(nal.Length >> shift));
            }

            bytes.AddRange(nal);
        }

        return [.. bytes];
    }

    /// <summary>Builds an AAC sequence header packet.</summary>
    /// <param name="config">The AudioSpecificConfig.</param>
    /// <returns>The packet.</returns>
    public static byte[] AacSequenceHeader(byte[]? config = null) => [0xAF, 0x00, .. config ?? AacConfig];

    /// <summary>Builds an AAC raw packet.</summary>
    /// <param name="accessUnit">The raw access unit.</param>
    /// <returns>The packet.</returns>
    public static byte[] AacRaw(params byte[] accessUnit) => [0xAF, 0x01, .. accessUnit];
}

/// <summary>An event observed by <see cref="RecordingMediaHandler"/>.</summary>
/// <param name="Kind">started, info, video, audio, error or ended.</param>
/// <param name="Session">The media session.</param>
internal record MediaEvent(string Kind, MediaSession Session);

/// <summary>A delivered video sample, copied.</summary>
/// <param name="Session">The session.</param>
/// <param name="Timestamps">Its timing.</param>
/// <param name="SequenceNumber">Its sequence number.</param>
/// <param name="IsKeyframe">Its keyframe flag.</param>
/// <param name="ContainsIdr">Whether it has an IDR slice.</param>
/// <param name="Discontinuity">Its discontinuity.</param>
/// <param name="Delta">Its decode-time delta.</param>
/// <param name="Data">Its data.</param>
/// <param name="NalUnits">Its NAL units' bytes.</param>
/// <param name="Track">Its track configuration.</param>
internal sealed record VideoEvent(MediaSession Session, MediaTimestamps Timestamps, long SequenceNumber, bool IsKeyframe, bool ContainsIdr, TimestampDiscontinuity Discontinuity, long? Delta, byte[] Data, byte[][] NalUnits, VideoTrackInfo Track)
    : MediaEvent("video", Session);

/// <summary>A delivered audio sample, copied.</summary>
/// <param name="Session">The session.</param>
/// <param name="Timestamps">Its timing.</param>
/// <param name="SequenceNumber">Its sequence number.</param>
/// <param name="Discontinuity">Its discontinuity.</param>
/// <param name="Delta">Its decode-time delta.</param>
/// <param name="Data">Its data.</param>
/// <param name="Track">Its track configuration.</param>
internal sealed record AudioEvent(MediaSession Session, MediaTimestamps Timestamps, long SequenceNumber, TimestampDiscontinuity Discontinuity, long? Delta, byte[] Data, AudioTrackInfo Track)
    : MediaEvent("audio", Session);

/// <summary>A reported error.</summary>
/// <param name="Session">The session.</param>
/// <param name="Error">The error.</param>
internal sealed record ErrorEvent(MediaSession Session, MediaProcessingError Error) : MediaEvent("error", Session);

/// <summary>A stream-description change.</summary>
/// <param name="Session">The session.</param>
/// <param name="Info">The description.</param>
internal sealed record InfoEvent(MediaSession Session, MediaStreamInfo Info) : MediaEvent("info", Session);

/// <summary>Records media events; copies then disposes samples unless told to retain them.</summary>
internal sealed class RecordingMediaHandler : IMediaSessionHandler
{
    /// <summary>Gets the events in order.</summary>
    public ConcurrentQueue<MediaEvent> Events { get; } = new();

    /// <summary>Gets or sets a gate each sample call awaits; <see langword="null"/> for none.</summary>
    public TaskCompletionSource? SampleGate { get; set; }

    /// <summary>Gets a task completed when a sample call is waiting on <see cref="SampleGate"/>.</summary>
    public TaskCompletionSource SampleBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets or sets a value indicating whether sample calls throw.</summary>
    public bool FailSamples { get; set; }

    /// <summary>Gets or sets a value indicating whether samples are kept undisposed in <see cref="Retained"/>.</summary>
    public bool RetainSamples { get; set; }

    /// <summary>Gets retained samples.</summary>
    public ConcurrentQueue<EncodedSample> Retained { get; } = new();

    /// <summary>Gets the video events.</summary>
    public IReadOnlyList<VideoEvent> Video => [.. Events.OfType<VideoEvent>()];

    /// <summary>Gets the audio events.</summary>
    public IReadOnlyList<AudioEvent> Audio => [.. Events.OfType<AudioEvent>()];

    /// <summary>Gets the error events.</summary>
    public IReadOnlyList<MediaProcessingError> Errors => [.. Events.OfType<ErrorEvent>().Select(static e => e.Error)];

    /// <summary>Gets the info events.</summary>
    public IReadOnlyList<MediaStreamInfo> Infos => [.. Events.OfType<InfoEvent>().Select(static e => e.Info)];

    /// <inheritdoc />
    public ValueTask OnSessionStartedAsync(MediaSession session, CancellationToken cancellationToken)
    {
        Events.Enqueue(new MediaEvent("started", session));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnStreamInfoChangedAsync(MediaSession session, MediaStreamInfo streamInfo, CancellationToken cancellationToken)
    {
        Events.Enqueue(new InfoEvent(session, streamInfo));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask OnVideoSampleAsync(MediaSession session, EncodedVideoSample sample, CancellationToken cancellationToken)
    {
        await GateAsync(cancellationToken);
        var nals = Enumerable.Range(0, sample.NalUnits.Count).Select(i => sample.GetNalUnitData(i).ToArray()).ToArray();
        Events.Enqueue(new VideoEvent(session, sample.Timestamps, sample.SequenceNumber, sample.IsKeyframe, sample.ContainsIdr, sample.Discontinuity, sample.DecodeTimestampDelta, sample.Data.ToArray(), nals, sample.Track));
        Finish(sample);
    }

    /// <inheritdoc />
    public async ValueTask OnAudioSampleAsync(MediaSession session, EncodedAudioSample sample, CancellationToken cancellationToken)
    {
        await GateAsync(cancellationToken);
        Events.Enqueue(new AudioEvent(session, sample.Timestamps, sample.SequenceNumber, sample.Discontinuity, sample.DecodeTimestampDelta, sample.Data.ToArray(), sample.Track));
        Finish(sample);
    }

    /// <inheritdoc />
    public ValueTask OnMediaErrorAsync(MediaSession session, MediaProcessingError mediaError, CancellationToken cancellationToken)
    {
        Events.Enqueue(new ErrorEvent(session, mediaError));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnSessionEndedAsync(MediaSession session, CancellationToken cancellationToken)
    {
        Events.Enqueue(new MediaEvent("ended", session));
        return ValueTask.CompletedTask;
    }

    /// <summary>Waits on the gate and applies the failure switch.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    private async Task GateAsync(CancellationToken cancellationToken)
    {
        if (SampleGate is { } gate)
        {
            SampleBlocked.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (FailSamples)
        {
            throw new InvalidOperationException("Test media handler failure.");
        }
    }

    /// <summary>Disposes or retains a sample.</summary>
    /// <param name="sample">The sample.</param>
    private void Finish(EncodedSample sample)
    {
        if (RetainSamples)
        {
            Retained.Enqueue(sample);
        }
        else
        {
            sample.Dispose();
        }
    }
}

/// <summary>Drives a <see cref="MediaPublishSessionHandler"/> directly with pooled messages.</summary>
internal sealed class MediaHarness : IDisposable
{
    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services;

    /// <summary>The logger factory.</summary>
    private readonly LoggerFactory _loggerFactory;

    /// <summary>Initializes a new instance of the <see cref="MediaHarness"/> class.</summary>
    /// <param name="options">Media options; defaults when <see langword="null"/>.</param>
    public MediaHarness(MediaOptions? options = null)
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        MeterFactory = _services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        _loggerFactory = new LoggerFactory([new FakeLoggerProvider(Logs)]);
        Processor = new MediaPublishSessionHandler(
            Microsoft.Extensions.Options.Options.Create(options ?? new MediaOptions()),
            Downstream,
            new MediaMetrics(MeterFactory),
            _loggerFactory.CreateLogger<MediaPublishSessionHandler>());
    }

    /// <summary>Gets the meter factory.</summary>
    public System.Diagnostics.Metrics.IMeterFactory MeterFactory { get; }

    /// <summary>Gets the captured logs.</summary>
    public FakeLogCollector Logs { get; } = new();

    /// <summary>Gets the downstream recorder.</summary>
    public RecordingMediaHandler Downstream { get; } = new();

    /// <summary>Gets the processor under test.</summary>
    public MediaPublishSessionHandler Processor { get; }

    /// <summary>Gets the pool payloads are rented from.</summary>
    public CountingArrayPool Pool { get; } = new();

    /// <summary>Creates a publisher session.</summary>
    /// <returns>The session.</returns>
    public static PublisherSession NewSession() =>
        new(1, null, "live", "camera1", "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    /// <summary>Creates a pooled message.</summary>
    /// <param name="kind">Its kind.</param>
    /// <param name="timestamp">Its RTMP timestamp.</param>
    /// <param name="payload">Its payload.</param>
    /// <returns>The message.</returns>
    public PublishedMessage Message(PublishedMessageKind kind, uint timestamp, byte[] payload)
    {
        var buffer = new PooledBuffer(Pool);
        buffer.EnsureCapacity(Math.Max(payload.Length, 1));
        buffer.Append(new ReadOnlySequence<byte>(payload));
        var type = kind switch
        {
            PublishedMessageKind.Audio => (byte)8,
            PublishedMessageKind.Video => (byte)9,
            _ => (byte)18,
        };
        return new PublishedMessage(kind, type, timestamp, buffer);
    }

    /// <summary>Starts a session.</summary>
    /// <returns>The session.</returns>
    public async Task<PublisherSession> StartAsync()
    {
        var session = NewSession();
        await Processor.OnSessionStartedAsync(session, TestContext.Current.CancellationToken);
        return session;
    }

    /// <summary>Sends a video packet.</summary>
    /// <param name="session">The session.</param>
    /// <param name="timestamp">The RTMP timestamp.</param>
    /// <param name="payload">The packet.</param>
    /// <returns>A task.</returns>
    public Task VideoAsync(PublisherSession session, uint timestamp, byte[] payload) =>
        Processor.OnMessageAsync(session, Message(PublishedMessageKind.Video, timestamp, payload), TestContext.Current.CancellationToken).AsTask();

    /// <summary>Sends an audio packet.</summary>
    /// <param name="session">The session.</param>
    /// <param name="timestamp">The RTMP timestamp.</param>
    /// <param name="payload">The packet.</param>
    /// <returns>A task.</returns>
    public Task AudioAsync(PublisherSession session, uint timestamp, byte[] payload) =>
        Processor.OnMessageAsync(session, Message(PublishedMessageKind.Audio, timestamp, payload), TestContext.Current.CancellationToken).AsTask();

    /// <summary>Sends a data message.</summary>
    /// <param name="session">The session.</param>
    /// <returns>A task.</returns>
    public Task DataAsync(PublisherSession session) =>
        Processor.OnMessageAsync(session, Message(PublishedMessageKind.Data, 0, [0x02, 0x00, 0x00]), TestContext.Current.CancellationToken).AsTask();

    /// <summary>Configures both tracks of a session with the default AVC (4-byte NAL lengths) and AAC configs.</summary>
    /// <param name="session">The session.</param>
    /// <returns>A task.</returns>
    public async Task ConfigureAsync(PublisherSession session)
    {
        await VideoAsync(session, 0, FlvPackets.AvcSequenceHeader());
        await AudioAsync(session, 0, FlvPackets.AacSequenceHeader());
    }

    /// <summary>Disposes the logger and meter factories.</summary>
    public void Dispose()
    {
        _loggerFactory.Dispose();
        _services.Dispose();
    }
}

/// <summary>A tag of an FLV file.</summary>
/// <param name="Type">8 audio, 9 video, 18 script.</param>
/// <param name="Timestamp">The 32-bit timestamp.</param>
/// <param name="Body">The tag body.</param>
internal sealed record FlvTag(byte Type, uint Timestamp, byte[] Body);

/// <summary>Reads FLV files.</summary>
internal static class FlvFile
{
    /// <summary>Reads a fixture from the test output's <c>Media/Fixtures</c> directory.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>The tags.</returns>
    public static IReadOnlyList<FlvTag> ReadFixture(string name) =>
        Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Media", "Fixtures", name)));

    /// <summary>Parses an FLV file.</summary>
    /// <param name="data">The file.</param>
    /// <returns>The tags.</returns>
    public static IReadOnlyList<FlvTag> Read(ReadOnlySpan<byte> data)
    {
        Assert.True(data[..3].SequenceEqual("FLV"u8), "Not an FLV file.");
        var position = (int)BinaryPrimitives.ReadUInt32BigEndian(data[5..]) + 4;
        var tags = new List<FlvTag>();
        while (data.Length - position >= 11)
        {
            var header = data.Slice(position, 11);
            var size = (header[1] << 16) | (header[2] << 8) | header[3];
            var timestamp = (uint)((header[7] << 24) | (header[4] << 16) | (header[5] << 8) | header[6]);
            tags.Add(new FlvTag(header[0], timestamp, data.Slice(position + 11, size).ToArray()));
            position += 11 + size + 4;
        }

        return tags;
    }
}
