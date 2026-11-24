// Building blocks for HLS packaging tests: an independent ISO BMFF box parser that checks every box size against its
// parent, an Exp-Golomb bit writer for synthetic SPS NAL units, fixture-derived codec configurations, and a harness
// that runs the real Phase 3 media processor in front of the HLS packager and an in-memory store on a fake clock.

using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>A parsed ISO BMFF box.</summary>
/// <param name="Type">The four-character type.</param>
/// <param name="Offset">Offset of the box within the parsed buffer.</param>
/// <param name="Size">The box size including its header.</param>
/// <param name="Body">The bytes after the 8-byte header.</param>
/// <param name="Children">Child boxes, for container boxes.</param>
internal sealed record Mp4Box(string Type, int Offset, int Size, byte[] Body, IReadOnlyList<Mp4Box> Children)
{
    /// <summary>Finds a descendant by slash-separated path, for example <c>moov/trak/mdia/mdhd</c>.</summary>
    /// <param name="path">The path below this box.</param>
    /// <returns>All matching boxes.</returns>
    public IEnumerable<Mp4Box> All(string path)
    {
        var parts = path.Split('/', 2);
        foreach (var child in Children.Where(c => c.Type == parts[0]))
        {
            if (parts.Length == 1)
            {
                yield return child;
            }
            else
            {
                foreach (var match in child.All(parts[1]))
                {
                    yield return match;
                }
            }
        }
    }

    /// <summary>Finds exactly one descendant.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The box.</returns>
    public Mp4Box One(string path) => Assert.Single(All(path));

    /// <summary>Reads a big-endian 32-bit value from the body.</summary>
    /// <param name="offset">Offset within the body.</param>
    /// <returns>The value.</returns>
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(Body.AsSpan(offset));

    /// <summary>Reads a big-endian 16-bit value from the body.</summary>
    /// <param name="offset">Offset within the body.</param>
    /// <returns>The value.</returns>
    public ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Body.AsSpan(offset));

    /// <summary>Reads a big-endian 64-bit value from the body.</summary>
    /// <param name="offset">Offset within the body.</param>
    /// <returns>The value.</returns>
    public ulong U64(int offset) => BinaryPrimitives.ReadUInt64BigEndian(Body.AsSpan(offset));
}

/// <summary>Parses ISO BMFF files strictly.</summary>
internal static class Mp4Parser
{
    /// <summary>Container boxes and the bytes to skip before their children.</summary>
    private static readonly Dictionary<string, int> Containers = new(StringComparer.Ordinal)
    {
        ["moov"] = 0,
        ["trak"] = 0,
        ["mdia"] = 0,
        ["minf"] = 0,
        ["stbl"] = 0,
        ["mvex"] = 0,
        ["dinf"] = 0,
        ["moof"] = 0,
        ["traf"] = 0,
        ["stsd"] = 8,
        ["dref"] = 8,
        ["avc1"] = 78,
        ["mp4a"] = 28,
    };

    /// <summary>Parses top-level boxes; every byte must belong to a box.</summary>
    /// <param name="data">The file.</param>
    /// <returns>A root box of type <c>root</c>.</returns>
    public static Mp4Box Parse(ReadOnlySpan<byte> data) => new("root", 0, data.Length, data.ToArray(), ParseChildren(data, 0));

    /// <summary>Parses a sequence of boxes filling <paramref name="data"/> exactly.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="baseOffset">Offset of <paramref name="data"/> in the file.</param>
    /// <returns>The boxes.</returns>
    private static List<Mp4Box> ParseChildren(ReadOnlySpan<byte> data, int baseOffset)
    {
        var boxes = new List<Mp4Box>();
        var position = 0;
        while (position < data.Length)
        {
            Assert.True(data.Length - position >= 8, $"Truncated box header at {baseOffset + position}.");
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            var type = System.Text.Encoding.ASCII.GetString(data.Slice(position + 4, 4));
            Assert.True(size >= 8 && size <= data.Length - position, $"Box {type} at {baseOffset + position} has size {size}, {data.Length - position} available.");
            var body = data.Slice(position + 8, size - 8);
            var children = Containers.TryGetValue(type, out var skip) ? ParseChildren(body[skip..], baseOffset + position + 8 + skip) : [];
            boxes.Add(new Mp4Box(type, baseOffset + position, size, body.ToArray(), children));
            position += size;
        }

        return boxes;
    }
}

/// <summary>A parsed <c>trun</c> sample.</summary>
/// <param name="Duration">Duration.</param>
/// <param name="Size">Size.</param>
/// <param name="Flags">Flags, when present.</param>
/// <param name="CompositionOffset">Composition offset, when present.</param>
internal readonly record struct TrunSample(uint Duration, uint Size, uint? Flags, int? CompositionOffset);

/// <summary>A parsed track fragment.</summary>
/// <param name="TrackId">Track ID.</param>
/// <param name="BaseDecodeTime">The <c>tfdt</c> time.</param>
/// <param name="DataOffset">The <c>trun</c> data offset.</param>
/// <param name="Version">The <c>trun</c> version.</param>
/// <param name="Samples">The samples.</param>
internal sealed record TrackRun(uint TrackId, ulong BaseDecodeTime, int DataOffset, int Version, IReadOnlyList<TrunSample> Samples);

/// <summary>Reads fragments.</summary>
internal static class Mp4Fragments
{
    /// <summary>Parses a media segment's track runs and checks each run's data range lies inside <c>mdat</c>.</summary>
    /// <param name="segment">The segment bytes.</param>
    /// <returns>The <c>mfhd</c> sequence number and runs.</returns>
    public static (uint Sequence, IReadOnlyList<TrackRun> Runs) Read(byte[] segment)
    {
        var root = Mp4Parser.Parse(segment);
        Assert.Equal(["moof", "mdat"], root.Children.Select(static b => b.Type));
        var moof = root.Children[0];
        var mdat = root.Children[1];
        var runs = new List<TrackRun>();
        foreach (var traf in moof.All("traf"))
        {
            var tfhd = traf.One("tfhd");
            Assert.Equal(0x020000u, tfhd.U32(0) & 0xFFFFFF);
            var tfdt = traf.One("tfdt");
            Assert.Equal(1, tfdt.Body[0]);
            var trun = traf.One("trun");
            var flags = trun.U32(0) & 0xFFFFFF;
            var count = (int)trun.U32(4);
            var offset = (int)trun.U32(8);
            var samples = new List<TrunSample>();
            var position = 12;
            long bytes = 0;
            for (var i = 0; i < count; i++)
            {
                var duration = trun.U32(position);
                var size = trun.U32(position + 4);
                position += 8;
                uint? sampleFlags = null;
                int? cto = null;
                if ((flags & 0x400) != 0)
                {
                    sampleFlags = trun.U32(position);
                    position += 4;
                }

                if ((flags & 0x800) != 0)
                {
                    cto = (int)trun.U32(position);
                    position += 4;
                }

                samples.Add(new TrunSample(duration, size, sampleFlags, cto));
                bytes += size;
            }

            Assert.Equal(trun.Body.Length, position);
            Assert.True(offset >= mdat.Offset + 8 && offset + bytes <= mdat.Offset + mdat.Size, "trun data lies outside mdat.");
            runs.Add(new TrackRun(tfhd.U32(4), tfdt.U64(4), offset, trun.Body[0], samples));
        }

        return (moof.One("mfhd").U32(4), runs);
    }
}

/// <summary>Writes bit strings, including Exp-Golomb codes.</summary>
internal sealed class BitWriter
{
    /// <summary>The bits.</summary>
    private readonly List<bool> _bits = [];

    /// <summary>Writes <paramref name="count"/> bits of <paramref name="value"/>.</summary>
    /// <param name="value">The value.</param>
    /// <param name="count">The bit count.</param>
    /// <returns>This writer.</returns>
    public BitWriter Bits(long value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            _bits.Add(((value >> i) & 1) != 0);
        }

        return this;
    }

    /// <summary>Writes <c>ue(v)</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>This writer.</returns>
    public BitWriter Ue(long value)
    {
        var code = value + 1;
        var length = 64 - long.LeadingZeroCount(code);
        return Bits(0, (int)length - 1).Bits(code, (int)length);
    }

    /// <summary>Writes <c>se(v)</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>This writer.</returns>
    public BitWriter Se(long value) => Ue(value > 0 ? (2 * value) - 1 : -2 * value);

    /// <summary>Returns the bytes with an RBSP stop bit and zero padding.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToRbsp()
    {
        var bits = new List<bool>(_bits) { true };
        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }

        var bytes = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }
}

/// <summary>Codec configurations and packets from the 4-second fixture.</summary>
internal static class HlsFixture
{
    /// <summary>Gets the fixture tags.</summary>
    public static IReadOnlyList<FlvTag> Tags { get; } = FlvFile.ReadFixture("h264-aac-4s.flv");

    /// <summary>Gets the AVC sequence header packet.</summary>
    public static byte[] AvcSequenceHeader => Tags.First(static t => t.Type == 9 && t.Body[1] == 0).Body;

    /// <summary>Gets the AVCDecoderConfigurationRecord.</summary>
    public static byte[] AvcRecord => AvcSequenceHeader[5..];

    /// <summary>Gets the AAC sequence header packet.</summary>
    public static byte[] AacSequenceHeader => Tags.First(static t => t.Type == 8 && t.Body[1] == 0).Body;

    /// <summary>Gets the SPS NAL unit.</summary>
    public static byte[] Sps
    {
        get
        {
            var record = AvcRecord;
            var length = (record[6] << 8) | record[7];
            return record[8..(8 + length)];
        }
    }

    /// <summary>Wraps an SPS RBSP into an AVC sequence header packet with the fixture's PPS.</summary>
    /// <param name="spsNal">The SPS NAL unit including its header byte.</param>
    /// <returns>The packet.</returns>
    public static byte[] SequenceHeaderWithSps(byte[] spsNal)
    {
        var record = AvcRecord;
        var spsLength = (record[6] << 8) | record[7];
        var rest = record[(8 + spsLength)..];
        return [0x17, 0x00, 0, 0, 0, .. record[..6], (byte)(spsNal.Length >> 8), (byte)spsNal.Length, .. spsNal, .. rest];
    }
}

/// <summary>Runs media processing and HLS packaging over pooled messages on a fake clock.</summary>
internal sealed class HlsHarness : IDisposable
{
    /// <summary>The IDR NAL unit used by synthetic video samples.</summary>
    public static readonly byte[] Idr = [0x65, 0x88, 0x84, 0x00, 0x21];

    /// <summary>The non-IDR NAL unit used by synthetic video samples.</summary>
    public static readonly byte[] Slice = [0x41, 0x9A, 0x22, 0x10];

    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services;

    /// <summary>The logger factory.</summary>
    private readonly LoggerFactory _loggerFactory;

    /// <summary>Initializes a new instance of the <see cref="HlsHarness"/> class.</summary>
    /// <param name="options">HLS options; defaults with a 1 s target and 2 s maximum when <see langword="null"/>.</param>
    public HlsHarness(HlsOptions? options = null)
    {
        Options = options ?? new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(1), MaxSegmentDuration = TimeSpan.FromSeconds(2) };
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        MeterFactory = _services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        _loggerFactory = new LoggerFactory([new FakeLoggerProvider(Logs)]);
        var metrics = new HlsMetrics(MeterFactory);
        Store = new InMemoryHlsSegmentStore(Microsoft.Extensions.Options.Options.Create(Options), Time, metrics);
        Packaging = new HlsPackagingHandler(Microsoft.Extensions.Options.Options.Create(Options), Store, metrics, _loggerFactory.CreateLogger<HlsPackagingHandler>());
        Processor = new MediaPublishSessionHandler(
            Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
            Packaging,
            new MediaMetrics(MeterFactory),
            _loggerFactory.CreateLogger<MediaPublishSessionHandler>());
    }

    /// <summary>Gets the options.</summary>
    public HlsOptions Options { get; }

    /// <summary>Gets the clock.</summary>
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UnixEpoch);

    /// <summary>Gets the meter factory.</summary>
    public System.Diagnostics.Metrics.IMeterFactory MeterFactory { get; }

    /// <summary>Gets the logs.</summary>
    public FakeLogCollector Logs { get; } = new();

    /// <summary>Gets the store.</summary>
    public InMemoryHlsSegmentStore Store { get; }

    /// <summary>Gets the packaging handler.</summary>
    public HlsPackagingHandler Packaging { get; }

    /// <summary>Gets the media processor.</summary>
    public MediaPublishSessionHandler Processor { get; }

    /// <summary>Gets the payload pool.</summary>
    public CountingArrayPool Pool { get; } = new();

    /// <summary>Starts a session for <c>live/camera1</c>.</summary>
    /// <param name="identity">The stream identity.</param>
    /// <returns>The session.</returns>
    public async Task<PublisherSession> StartAsync(string identity = "camera1")
    {
        var session = new PublisherSession(1, null, "live", identity, "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await Processor.OnSessionStartedAsync(session, TestContext.Current.CancellationToken);
        return session;
    }

    /// <summary>Sends the fixture's AVC and AAC sequence headers.</summary>
    /// <param name="session">The session.</param>
    /// <param name="video">Whether to configure video.</param>
    /// <param name="audio">Whether to configure audio.</param>
    /// <returns>A task.</returns>
    public async Task ConfigureAsync(PublisherSession session, bool video = true, bool audio = true)
    {
        if (video)
        {
            await SendAsync(session, PublishedMessageKind.Video, 0, HlsFixture.AvcSequenceHeader);
        }

        if (audio)
        {
            await SendAsync(session, PublishedMessageKind.Audio, 0, HlsFixture.AacSequenceHeader);
        }
    }

    /// <summary>Sends a synthetic video sample.</summary>
    /// <param name="session">The session.</param>
    /// <param name="timestamp">The RTMP timestamp.</param>
    /// <param name="idr">Whether it is an IDR keyframe.</param>
    /// <param name="compositionTime">Its composition time.</param>
    /// <returns>A task.</returns>
    public Task VideoAsync(PublisherSession session, uint timestamp, bool idr, int compositionTime = 0) =>
        SendAsync(session, PublishedMessageKind.Video, timestamp, FlvPackets.AvcNalu(idr, compositionTime, 4, idr ? Idr : Slice));

    /// <summary>Sends a synthetic AAC access unit.</summary>
    /// <param name="session">The session.</param>
    /// <param name="timestamp">The RTMP timestamp.</param>
    /// <returns>A task.</returns>
    public Task AudioAsync(PublisherSession session, uint timestamp) =>
        SendAsync(session, PublishedMessageKind.Audio, timestamp, FlvPackets.AacRaw(0x21, 0x10, 0x05, (byte)timestamp));

    /// <summary>Sends a message.</summary>
    /// <param name="session">The session.</param>
    /// <param name="kind">Its kind.</param>
    /// <param name="timestamp">Its RTMP timestamp.</param>
    /// <param name="payload">Its payload.</param>
    /// <returns>A task.</returns>
    public async Task SendAsync(PublisherSession session, PublishedMessageKind kind, uint timestamp, byte[] payload)
    {
        var buffer = new PooledBuffer(Pool);
        buffer.EnsureCapacity(Math.Max(payload.Length, 1));
        buffer.Append(new ReadOnlySequence<byte>(payload));
        var type = kind switch { PublishedMessageKind.Audio => (byte)8, PublishedMessageKind.Video => (byte)9, _ => (byte)18 };
        await Processor.OnMessageAsync(session, new PublishedMessage(kind, type, timestamp, buffer), TestContext.Current.CancellationToken);
    }

    /// <summary>Replays FLV tags.</summary>
    /// <param name="session">The session.</param>
    /// <param name="tags">The tags.</param>
    /// <returns>A task.</returns>
    public async Task ReplayAsync(PublisherSession session, IEnumerable<FlvTag> tags)
    {
        foreach (var tag in tags)
        {
            var kind = tag.Type switch { 8 => PublishedMessageKind.Audio, 9 => PublishedMessageKind.Video, _ => PublishedMessageKind.Data };
            if (kind != PublishedMessageKind.Data)
            {
                await SendAsync(session, kind, tag.Timestamp, tag.Body);
            }
        }
    }

    /// <summary>Ends a session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="reason">The end reason.</param>
    /// <returns>A task.</returns>
    public async Task EndAsync(PublisherSession session, PublishEndReason reason = PublishEndReason.Unpublished)
    {
        session.TryEnd(reason, Time.GetUtcNow());
        await Processor.OnSessionEndedAsync(session, TestContext.Current.CancellationToken);
    }

    /// <summary>Gets the playlist of <c>live/camera1</c>.</summary>
    /// <param name="identity">The stream identity.</param>
    /// <returns>The playlist.</returns>
    public HlsPlaylistSnapshot? Playlist(string identity = "camera1") => Store.GetPlaylist("live", identity);

    /// <summary>Gets a segment of <c>live/camera1</c>.</summary>
    /// <param name="name">The segment name.</param>
    /// <param name="identity">The stream identity.</param>
    /// <returns>The bytes.</returns>
    public byte[] Segment(string name, string identity = "camera1") =>
        (Store.GetSegment("live", identity, name) ?? throw new InvalidOperationException($"{name} is not stored.")).Data.ToArray();

    /// <summary>Disposes the factories.</summary>
    public void Dispose()
    {
        _loggerFactory.Dispose();
        _services.Dispose();
    }
}
