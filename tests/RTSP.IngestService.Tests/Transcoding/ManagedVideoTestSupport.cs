// Helpers for tests of the managed video path: turning the Annex B decoder fixtures into the FLV video tags an RTMP
// publisher sends (decode timestamps and composition offsets derived from the fixtures' presentation times), and
// decoding the canonical video the transcoding stage delivers with the Phase 3 managed decoder, so a test checks the
// actual pictures rather than only the tag framing.

using RTSP.IngestService.Media;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Video.H264;
using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media.Codecs.H264;
using FixtureTag = RTSP.IngestService.Tests.Media.FlvTag;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>A decoded picture of the canonical output.</summary>
/// <param name="Timestamp">Its presentation time in milliseconds.</param>
/// <param name="Width">Its width.</param>
/// <param name="Height">Its height.</param>
/// <param name="IsKeyframe">Whether it is an IDR picture.</param>
/// <param name="Color">The colour description the decoder reported.</param>
/// <param name="MeanLuma">The mean of its luma samples.</param>
internal readonly record struct CanonicalPicture(long Timestamp, int Width, int Height, bool IsKeyframe, VideoColorInfo Color, double MeanLuma);

/// <summary>The canonical video delivered for one session, decoded.</summary>
/// <param name="SequenceHeaders">The AVC decoder configuration records announced, in order.</param>
/// <param name="Pictures">The decoded pictures in output order.</param>
/// <param name="TagTimestamps">The FLV timestamps of the coded-frame tags, in delivery order.</param>
/// <param name="KeyframeTags">The FLV frame-type keyframe flag of each coded-frame tag.</param>
internal sealed record CanonicalVideo(IReadOnlyList<byte[]> SequenceHeaders, IReadOnlyList<CanonicalPicture> Pictures, IReadOnlyList<uint> TagTimestamps, IReadOnlyList<bool> KeyframeTags);

/// <summary>Managed video test helpers.</summary>
internal static class ManagedVideoTestSupport
{
    /// <summary>
    /// Builds the FLV video tags of an Annex B decoder fixture: an AVC sequence header, then one NALU tag per access
    /// unit. Presentation times come from the fixture's <c>.pts</c> file when present, else one frame every
    /// <paramref name="frameMilliseconds"/>; decode times are the sorted presentation times delayed just enough that
    /// no composition offset is negative.
    /// </summary>
    /// <param name="name">The fixture name without the <c>h264-</c> prefix.</param>
    /// <param name="frameMilliseconds">The frame duration when the fixture has no timestamps.</param>
    /// <param name="start">Added to every timestamp.</param>
    /// <returns>The tags.</returns>
    public static List<FixtureTag> AnnexBFixtureTags(string name, int frameMilliseconds = 40, uint start = 0)
    {
        var units = H264TestSupport.ReadAccessUnits(name);
        var ptsFile = Path.Combine(H264TestSupport.FixtureDirectory, $"h264-{name}.pts");
        var pts = File.Exists(ptsFile) ? H264TestSupport.ReadTimestamps(name) : [.. Enumerable.Range(0, units.Count).Select(i => (long)i * frameMilliseconds)];
        var sorted = pts.Order().ToArray();
        var delay = Math.Max(0, Enumerable.Range(0, units.Count).Max(i => sorted[i] - pts[i]));
        var tags = new List<FixtureTag> { new(9, start, [0x17, 0, 0, 0, 0, .. H264TestSupport.BuildAvcC(units[0])]) };
        for (var i = 0; i < units.Count; i++)
        {
            var keyframe = H264TestSupport.SplitNalUnits(units[i]).Any(static n => (n[0] & 0x1F) == 5);
            var cto = (int)(pts[i] - sorted[i] + delay);
            tags.Add(new FixtureTag(9, (uint)(start + sorted[i] - sorted[0]), [keyframe ? (byte)0x17 : (byte)0x27, 1, (byte)(cto >> 16), (byte)(cto >> 8), (byte)cto, .. H264TestSupport.ToLengthPrefixed(units[i])]));
        }

        return tags;
    }

    /// <summary>
    /// Builds the FLV video tags of a synthetic BT.709 source encoded by the managed encoder: an AVC sequence header,
    /// then one NALU tag per picture, without B-frames.
    /// </summary>
    /// <param name="pattern">The picture content.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="count">The picture count.</param>
    /// <param name="frameMilliseconds">The frame duration.</param>
    /// <param name="qp">The source quantiser.</param>
    /// <param name="start">The first timestamp.</param>
    /// <returns>The tags.</returns>
    public static List<FixtureTag> SyntheticTags(SyntheticPattern pattern, int width, int height, int count, int frameMilliseconds = 40, int qp = 26, uint start = 0)
    {
        using var encoder = new H264Encoder(H264EncoderTestSupport.Options(width, height, qp));
        var tags = new List<FixtureTag> { new(9, start, [0x17, 0, 0, 0, 0, .. encoder.DecoderConfigurationRecord.Span]) };
        var units = new List<H264EncodedAccessUnit>();
        for (var i = 0; i < count; i++)
        {
            using var frame = H264EncoderTestSupport.CreateFrame(pattern, width, height, i, frameMilliseconds * 90L);
            encoder.Encode(frame, units);
            foreach (var unit in units)
            {
                tags.Add(new FixtureTag(9, (uint)(start + (unit.Timestamp / 90)), [unit.IsKeyframe ? (byte)0x17 : (byte)0x27, 1, 0, 0, 0, .. unit.Data.Span]));
                unit.Dispose();
            }

            units.Clear();
        }

        return tags;
    }

    /// <summary>Gets the presentation times, in milliseconds, of a source's coded video tags in presentation order.</summary>
    /// <param name="tags">The source tags.</param>
    /// <returns>The presentation times.</returns>
    public static List<long> SourcePresentationTimes(IEnumerable<FixtureTag> tags) =>
        [.. tags.Where(static t => t.Type == 9 && t.Body.Length > 5 && t.Body[1] == 1)
            .Select(static t => t.Timestamp + (((t.Body[2] << 24) | (t.Body[3] << 16) | (t.Body[4] << 8)) >> 8))
            .Order()];

    /// <summary>Decodes the canonical video delivered downstream with the managed decoder.</summary>
    /// <param name="delivered">The delivered video messages of one session, in order.</param>
    /// <returns>The decoded output.</returns>
    public static CanonicalVideo Decode(IReadOnlyList<HandlerEvent> delivered)
    {
        var headers = new List<byte[]>();
        var pictures = new List<CanonicalPicture>();
        var timestamps = new List<uint>();
        var keyframes = new List<bool>();
        var frames = new List<VideoFrame>();
        H264Decoder? decoder = null;
        try
        {
            foreach (var message in delivered)
            {
                var body = message.Payload!;
                Assert.True(body.Length >= 5, "A canonical video tag has the five-byte AVC header.");
                Assert.True(body[0] is 0x17 or 0x27, $"Canonical video is legacy AVC; frame type and codec byte was 0x{body[0]:x2}.");
                Assert.True(body[2] == 0 && body[3] == 0 && body[4] == 0, "Canonical video has no B-frames, so every composition offset is 0.");
                if (body[1] == 0)
                {
                    if (decoder is not null)
                    {
                        decoder.Drain(frames);
                        Collect(frames, pictures);
                        decoder.Dispose();
                    }

                    headers.Add(body[5..]);
                    decoder = new H264Decoder();
                    decoder.Configure(H264DecoderConfiguration.FromAvcC(body.AsMemory(5)));
                    continue;
                }

                Assert.Equal(1, body[1]);
                Assert.NotNull(decoder);
                timestamps.Add(message.Timestamp);
                keyframes.Add(body[0] == 0x17);
                decoder.Decode(new EncodedPacket(body.AsMemory(5), message.Timestamp, MediaTimeBase.Milliseconds), frames, TestContext.Current.CancellationToken);
                Collect(frames, pictures);
            }

            if (decoder is not null)
            {
                decoder.Drain(frames);
                Collect(frames, pictures);
            }
        }
        finally
        {
            frames.ForEach(static f => f.Dispose());
            decoder?.Dispose();
        }

        return new CanonicalVideo(headers, pictures, timestamps, keyframes);
    }

    /// <summary>
    /// Asserts the canonical profile of every announced configuration record: H.264 High, 8-bit 4:2:0 progressive,
    /// BT.709 limited range declared in the VUI, at most one reference frame and a picture of the given size.
    /// </summary>
    /// <param name="video">The decoded output.</param>
    /// <param name="width">The expected width.</param>
    /// <param name="height">The expected height.</param>
    public static void AssertCanonical(CanonicalVideo video, int width, int height)
    {
        Assert.NotEmpty(video.SequenceHeaders);
        foreach (var record in video.SequenceHeaders)
        {
            Assert.True(AvcDecoderConfiguration.TryParse(record, out var parsed, out _));
            Assert.Equal(4, parsed.NalLengthSize);
            var sps = H264Sps.Parse(Rbsp(Assert.Single(parsed.SequenceParameterSets).ToArray()));
            Assert.Null(sps.UnsupportedReason);
            Assert.Equal(100, sps.ProfileIdc);
            Assert.Equal(VideoColorInfo.Bt709, sps.Color);
            Assert.Equal(1, sps.MaxNumRefFrames);
            Assert.Equal((width, height), (sps.DisplayWidth, sps.DisplayHeight));
        }

        Assert.NotEmpty(video.Pictures);
        Assert.All(video.Pictures, p =>
        {
            Assert.Equal((width, height), (p.Width, p.Height));
            Assert.Equal(VideoColorInfo.Bt709, p.Color);
        });
        Assert.True(video.KeyframeTags[0], "The first canonical frame is an IDR picture.");
        Assert.True(video.Pictures[0].IsKeyframe);
        Assert.Equal(video.TagTimestamps.Count, video.Pictures.Count);
        Assert.Equal(video.TagTimestamps.Select(static t => (long)t), video.Pictures.Select(static p => p.Timestamp));
        Assert.Equal(video.KeyframeTags, video.Pictures.Select(static p => p.IsKeyframe));
    }

    /// <summary>
    /// Builds an AVCDecoderConfigurationRecord of a minimal High-profile 8-bit 4:2:0 progressive SPS and PPS for a
    /// picture of whole macroblocks, without VUI; for limits no real encoder here can produce.
    /// </summary>
    /// <param name="width">The width, a multiple of 16.</param>
    /// <param name="height">The height, a multiple of 16.</param>
    /// <returns>The record.</returns>
    public static byte[] RecordWithSize(int width, int height)
    {
        var sps = new BitWriter();
        sps.Bits(100, 8);
        sps.Bits(0, 8);
        sps.Bits(51, 8);
        sps.Ue(0);
        sps.Ue(1);
        sps.Ue(0);
        sps.Ue(0);
        sps.Bits(0, 1);
        sps.Bits(0, 1);
        sps.Ue(0);
        sps.Ue(2);
        sps.Ue(1);
        sps.Bits(0, 1);
        sps.Ue((uint)(width / 16) - 1);
        sps.Ue((uint)(height / 16) - 1);
        sps.Bits(1, 1);
        sps.Bits(1, 1);
        sps.Bits(0, 1);
        sps.Bits(0, 1);
        var spsNal = Nal(0x67, sps.Finish());

        var pps = new BitWriter();
        pps.Ue(0);
        pps.Ue(0);
        pps.Bits(0, 1);
        pps.Bits(0, 1);
        pps.Ue(0);
        pps.Ue(0);
        pps.Ue(0);
        pps.Bits(0, 1);
        pps.Bits(0, 2);
        pps.Ue(0);
        pps.Ue(0);
        pps.Ue(0);
        pps.Bits(1, 1);
        pps.Bits(0, 1);
        pps.Bits(0, 1);
        var ppsNal = Nal(0x68, pps.Finish());

        return [1, spsNal[1], spsNal[2], spsNal[3], 0xFF, 0xE1, (byte)(spsNal.Length >> 8), (byte)spsNal.Length, .. spsNal, 1, (byte)(ppsNal.Length >> 8), (byte)ppsNal.Length, .. ppsNal];
    }

    /// <summary>Adds a NAL header and emulation prevention to an RBSP.</summary>
    /// <param name="header">The NAL header byte.</param>
    /// <param name="rbsp">The RBSP.</param>
    /// <returns>The NAL unit.</returns>
    private static byte[] Nal(byte header, byte[] rbsp)
    {
        var nal = new List<byte> { header };
        var zeros = 0;
        foreach (var value in rbsp)
        {
            if (zeros >= 2 && value <= 3)
            {
                nal.Add(3);
                zeros = 0;
            }

            nal.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }

        return [.. nal];
    }

    /// <summary>Returns the RBSP of a parameter set NAL unit.</summary>
    /// <param name="nal">The NAL unit including its header.</param>
    /// <returns>The RBSP.</returns>
    private static byte[] Rbsp(byte[] nal)
    {
        var rbsp = new byte[nal.Length - 1];
        return rbsp[..H264NalUnits.Unescape(nal.AsSpan(1), rbsp)];
    }

    /// <summary>Records and disposes decoded frames.</summary>
    /// <param name="frames">The frames; cleared.</param>
    /// <param name="pictures">Receives the records.</param>
    internal static void Collect(List<VideoFrame> frames, List<CanonicalPicture> pictures)
    {
        foreach (var frame in frames)
        {
            var luma = frame.GetPlane(0);
            long sum = 0;
            foreach (var sample in luma)
            {
                sum += sample;
            }

            pictures.Add(new CanonicalPicture(frame.Timestamp, frame.Width, frame.Height, frame.IsKeyframe, frame.Color, (double)sum / luma.Length));
            frame.Dispose();
        }

        frames.Clear();
    }

    /// <summary>Writes an RBSP bit by bit.</summary>
    private sealed class BitWriter
    {
        /// <summary>The bits written, most significant first.</summary>
        private readonly List<bool> _bits = [];

        /// <summary>Writes the low <paramref name="count"/> bits of a value.</summary>
        /// <param name="value">The value.</param>
        /// <param name="count">The bit count.</param>
        public void Bits(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }
        }

        /// <summary>Writes an unsigned Exp-Golomb code.</summary>
        /// <param name="value">The value.</param>
        public void Ue(uint value)
        {
            var code = value + 1;
            var length = 32 - System.Numerics.BitOperations.LeadingZeroCount(code);
            Bits(0, length - 1);
            Bits(code, length);
        }

        /// <summary>Appends the RBSP trailing bits and returns the bytes.</summary>
        /// <returns>The RBSP.</returns>
        public byte[] Finish()
        {
            _bits.Add(true);
            while (_bits.Count % 8 != 0)
            {
                _bits.Add(false);
            }

            var bytes = new byte[_bits.Count / 8];
            for (var i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    bytes[i / 8] |= (byte)(0x80 >> (i % 8));
                }
            }

            return bytes;
        }
    }
}
