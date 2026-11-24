// Tests for the fragmented MP4 writers, checked with an independent box parser: initialization segment nesting,
// sizes, track IDs, timescales, sample entries and verbatim avcC/esds configuration; media fragment sequence numbers,
// tfdt, trun durations, sizes, flags, signed composition offsets and data offsets into mdat; and size limits.

using System.Buffers.Binary;
using RTSP.IngestService.Hls;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>
/// Tests for <see cref="Mp4Segments"/>.
/// </summary>
public sealed class Mp4SegmentsTests
{
    /// <summary>The fixture's AAC AudioSpecificConfig.</summary>
    private static readonly byte[] Asc = [0x12, 0x10];

    /// <summary>Verifies the box structure and fields of an audio/video initialization segment.</summary>
    [Fact]
    public void Initialization_AudioVideo_HasCompleteStructure()
    {
        var bytes = Mp4Segments.WriteInitialization(Video(), Audio(), 128 * 1024);
        var root = Mp4Parser.Parse(bytes);

        Assert.Equal(["ftyp", "moov"], root.Children.Select(static b => b.Type));
        var ftyp = root.One("ftyp");
        Assert.Equal("iso6", Ascii(ftyp.Body, 0));
        Assert.Equal(["iso6", "iso5", "mp41"], new[] { Ascii(ftyp.Body, 8), Ascii(ftyp.Body, 12), Ascii(ftyp.Body, 16) });

        var moov = root.One("moov");
        Assert.Equal(["mvhd", "trak", "trak", "mvex"], moov.Children.Select(static b => b.Type));
        Assert.Equal(1000u, moov.One("mvhd").U32(12));
        Assert.Equal(3u, moov.One("mvhd").U32(96));

        var (video, audio) = (moov.Children[1], moov.Children[2]);
        Assert.Equal(1u, video.One("tkhd").U32(12));
        Assert.Equal(3u, video.One("tkhd").U32(0) & 0xFFFFFF);
        Assert.Equal((160u << 16, 120u << 16), (video.One("tkhd").U32(76), video.One("tkhd").U32(80)));
        Assert.Equal(90000u, video.One("mdia/mdhd").U32(12));
        Assert.Equal(0x55C4, video.One("mdia/mdhd").U16(20));
        Assert.Equal("vide", Ascii(video.One("mdia/hdlr").Body, 8));
        Assert.Single(video.All("mdia/minf/vmhd"));
        Assert.Single(video.All("mdia/minf/dinf/dref/url "));
        var stbl = video.One("mdia/minf/stbl");
        Assert.Equal(["stsd", "stts", "stsc", "stsz", "stco"], stbl.Children.Select(static b => b.Type));
        Assert.Equal(1u, stbl.One("stsd").U32(4));
        var avc1 = stbl.One("stsd/avc1");
        Assert.Equal((160, 120), (avc1.U16(24), avc1.U16(26)));
        Assert.Equal(HlsFixture.AvcRecord, avc1.One("avcC").Body);

        Assert.Equal(2u, audio.One("tkhd").U32(12));
        Assert.Equal(0x0100, audio.One("tkhd").U16(36));
        Assert.Equal(44100u, audio.One("mdia/mdhd").U32(12));
        Assert.Equal("soun", Ascii(audio.One("mdia/hdlr").Body, 8));
        Assert.Single(audio.All("mdia/minf/smhd"));
        var mp4a = audio.One("mdia/minf/stbl/stsd/mp4a");
        Assert.Equal((2, 16), (mp4a.U16(16), mp4a.U16(18)));
        Assert.Equal(44100u << 16, mp4a.U32(24));
        Assert.Equal(Asc, ReadEsds(mp4a.One("esds")));

        var trex = moov.All("mvex/trex").ToList();
        Assert.Equal([1u, 2u], trex.Select(static t => t.U32(4)));
        Assert.All(trex, static t => Assert.Equal(1u, t.U32(8)));
    }

    /// <summary>Verifies single-track initialization segments.</summary>
    [Fact]
    public void Initialization_SingleTrack_HasOneTrak()
    {
        var videoOnly = Mp4Parser.Parse(Mp4Segments.WriteInitialization(Video(), null, 128 * 1024)).One("moov");
        Assert.Equal(1u, videoOnly.One("trak/tkhd").U32(12));
        Assert.Equal(1u, videoOnly.One("mvex/trex").U32(4));

        var audioOnly = Mp4Parser.Parse(Mp4Segments.WriteInitialization(null, Audio(), 128 * 1024)).One("moov");
        Assert.Equal(2u, audioOnly.One("trak/tkhd").U32(12));
        Assert.Equal(2u, audioOnly.One("mvex/trex").U32(4));
        Assert.Throws<ArgumentException>(static () => Mp4Segments.WriteInitialization(null, null, 128 * 1024));
    }

    /// <summary>Verifies a long AudioSpecificConfig uses multi-byte descriptor lengths and round-trips.</summary>
    [Fact]
    public void Esds_LongConfig_UsesExpandableLengths()
    {
        var config = Enumerable.Range(0, 200).Select(static i => (byte)i).ToArray();
        var audio = new Mp4AudioTrack(48000, 2, config, 1024);
        var esds = Mp4Parser.Parse(Mp4Segments.WriteInitialization(null, audio, 128 * 1024)).One("moov/trak/mdia/minf/stbl/stsd/mp4a/esds");

        Assert.Equal(config, ReadEsds(esds));
        Assert.Equal(48000u << 16, Mp4Parser.Parse(Mp4Segments.WriteInitialization(null, audio, 128 * 1024)).One("moov/trak/mdia/minf/stbl/stsd/mp4a").U32(24));
    }

    /// <summary>Verifies size limits and field ranges are enforced.</summary>
    [Fact]
    public void Limits_AreEnforced()
    {
        Assert.Throws<Mp4SizeException>(() => Mp4Segments.WriteInitialization(Video(), Audio(), 300));
        Assert.Throws<Mp4SizeException>(() => Mp4Segments.WriteInitialization(null, new Mp4AudioTrack(96000, 2, Asc, 1024), 128 * 1024));
        var fragment = new[] { new Mp4TrackFragment(1, 0, true, [new Mp4Sample(3000, Mp4Sample.SyncFlags, 0, new byte[1000])]) };
        Assert.Throws<Mp4SizeException>(() => Mp4Segments.WriteFragment(1, fragment, 1000));
        // moof 8 + mfhd 16 + traf 8 + tfhd 16 + tfdt 20 + trun 20 + one 16-byte entry, then mdat 8 + data.
        Assert.Equal(8 + 16 + 8 + 16 + 20 + 20 + 16 + 8 + 1000, Mp4Segments.WriteFragment(1, fragment, 2000).Length);
    }

    /// <summary>Verifies a two-track fragment's moof fields and that every trun entry addresses its own bytes in mdat.</summary>
    [Fact]
    public void Fragment_DescribesEverySampleExactly()
    {
        byte[][] videoData = [[0, 0, 0, 1, 0x65], [0, 0, 0, 2, 0x41, 0x9A], [0, 0, 0, 1, 0x01]];
        byte[][] audioData = [[0x21, 1], [0x21, 2, 3], [0x21, 4]];
        var tracks = new[]
        {
            new Mp4TrackFragment(1, 900_000, true, [
                new Mp4Sample(3000, Mp4Sample.SyncFlags, 6000, videoData[0]),
                new Mp4Sample(3003, Mp4Sample.NonSyncFlags, -3000, videoData[1]),
                new Mp4Sample(2997, Mp4Sample.NonSyncFlags, 0, videoData[2])]),
            new Mp4TrackFragment(2, 441_000, false, [
                new Mp4Sample(1024, 0, 0, audioData[0]),
                new Mp4Sample(1024, 0, 0, audioData[1]),
                new Mp4Sample(1023, 0, 0, audioData[2])]),
        };

        var bytes = Mp4Segments.WriteFragment(7, tracks, 1 << 20);
        var (sequence, runs) = Mp4Fragments.Read(bytes);

        Assert.Equal(7u, sequence);
        Assert.Equal(Mp4Segments.MeasureFragment(tracks, out _), bytes.Length);
        var video = runs[0];
        Assert.Equal((1u, 900_000ul, 1), (video.TrackId, video.BaseDecodeTime, video.Version));
        Assert.Equal([3000u, 3003u, 2997u], video.Samples.Select(static s => s.Duration));
        Assert.Equal([Mp4Sample.SyncFlags, Mp4Sample.NonSyncFlags, Mp4Sample.NonSyncFlags], video.Samples.Select(static s => s.Flags!.Value));
        Assert.Equal([6000, -3000, 0], video.Samples.Select(static s => s.CompositionOffset!.Value));
        var audio = runs[1];
        Assert.Equal((2u, 441_000ul, 0), (audio.TrackId, audio.BaseDecodeTime, audio.Version));
        Assert.Equal([1024u, 1024u, 1023u], audio.Samples.Select(static s => s.Duration));
        Assert.All(audio.Samples, static s => Assert.Null(s.Flags));
        Assert.Equal(video.DataOffset + videoData.Sum(static d => d.Length), audio.DataOffset);

        foreach (var (run, data) in new[] { (video, videoData), (audio, audioData) })
        {
            var offset = run.DataOffset;
            for (var i = 0; i < data.Length; i++)
            {
                Assert.Equal((uint)data[i].Length, run.Samples[i].Size);
                Assert.Equal(data[i], bytes[offset..(offset + data[i].Length)]);
                offset += data[i].Length;
            }
        }

        Assert.Equal(bytes.Length, audio.DataOffset + audioData.Sum(static d => d.Length));
        Assert.Throws<ArgumentException>(() => Mp4Segments.WriteFragment(1, [new Mp4TrackFragment(1, 0, true, [])], 1 << 20));
    }

    /// <summary>Returns the fixture's video track.</summary>
    /// <returns>The track.</returns>
    private static Mp4VideoTrack Video() => new(160, 120, HlsFixture.AvcRecord);

    /// <summary>Returns the fixture's audio track.</summary>
    /// <returns>The track.</returns>
    private static Mp4AudioTrack Audio() => new(44100, 2, Asc, 1024);

    /// <summary>Reads four ASCII characters.</summary>
    /// <param name="body">The bytes.</param>
    /// <param name="offset">The offset.</param>
    /// <returns>The text.</returns>
    private static string Ascii(byte[] body, int offset) => System.Text.Encoding.ASCII.GetString(body, offset, 4);

    /// <summary>Parses an <c>esds</c> box strictly and returns its DecoderSpecificInfo.</summary>
    /// <param name="esds">The box.</param>
    /// <returns>The AudioSpecificConfig.</returns>
    private static byte[] ReadEsds(Mp4Box esds)
    {
        var body = esds.Body[4..];
        var (tag, es) = Descriptor(ref body);
        Assert.Equal(0x03, tag);
        Assert.Empty(body);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(es));
        Assert.Equal(0, es[2]);
        es = es[3..];
        var (decoderTag, decoder) = Descriptor(ref es);
        Assert.Equal(0x04, decoderTag);
        Assert.Equal((0x40, 0x15), (decoder[0], decoder[1]));
        decoder = decoder[13..];
        var (infoTag, info) = Descriptor(ref decoder);
        Assert.Equal(0x05, infoTag);
        Assert.Empty(decoder);
        var (slTag, sl) = Descriptor(ref es);
        Assert.Equal(0x06, slTag);
        Assert.Equal([0x02], sl);
        Assert.Empty(es);
        return info;
    }

    /// <summary>Reads a descriptor with an expandable length.</summary>
    /// <param name="data">The bytes, replaced by those after the descriptor.</param>
    /// <returns>The tag and payload.</returns>
    private static (byte Tag, byte[] Payload) Descriptor(ref byte[] data)
    {
        var tag = data[0];
        var length = 0;
        var position = 1;
        byte next;
        do
        {
            next = data[position++];
            length = (length << 7) | (next & 0x7F);
        }
        while ((next & 0x80) != 0);

        var payload = data[position..(position + length)];
        data = data[(position + length)..];
        return (tag, payload);
    }
}
