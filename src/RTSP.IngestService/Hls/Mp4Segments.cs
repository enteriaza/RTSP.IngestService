// Writers for the two kinds of fragmented MP4 output: the initialization segment (ftyp + moov with avc1/avcC and
// mp4a/esds sample entries and mvex/trex) and media fragments (moof with one traf per track, then one mdat). Codec
// configuration records and sample bytes are copied verbatim; nothing is converted or re-encoded.

using System.Buffers;

namespace RTSP.IngestService.Hls;

/// <summary>Writes fragmented MP4 initialization segments and media fragments.</summary>
internal static class Mp4Segments
{
    /// <summary>Size of a fragment's <c>mfhd</c> box.</summary>
    private const int MfhdSize = 16;

    /// <summary>Size of a <c>tfhd</c> box with only <c>default-base-is-moof</c>.</summary>
    private const int TfhdSize = 16;

    /// <summary>Size of a version 1 <c>tfdt</c> box.</summary>
    private const int TfdtSize = 20;

    /// <summary>Size of a <c>trun</c> box before its sample entries (header, count, data offset).</summary>
    private const int TrunHeaderSize = 20;

    /// <summary>Writes an initialization segment for the given tracks.</summary>
    /// <param name="video">The video track, if any.</param>
    /// <param name="audio">The audio track, if any.</param>
    /// <param name="maxSize">The largest permitted output.</param>
    /// <returns>The segment bytes.</returns>
    /// <exception cref="Mp4SizeException">The output would exceed <paramref name="maxSize"/> or a field's range.</exception>
    public static byte[] WriteInitialization(Mp4VideoTrack? video, Mp4AudioTrack? audio, int maxSize)
    {
        if (video is null && audio is null)
        {
            throw new ArgumentException("At least one track is required.", nameof(video));
        }

        var buffer = ArrayPool<byte>.Shared.Rent(maxSize);
        try
        {
            return WriteInitialization(buffer.AsSpan(0, maxSize), video, audio);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Writes an initialization segment into a scratch buffer and copies it out.</summary>
    /// <param name="buffer">The scratch buffer, as large as the largest permitted output.</param>
    /// <param name="video">The video track, if any.</param>
    /// <param name="audio">The audio track, if any.</param>
    /// <returns>The segment bytes.</returns>
    private static byte[] WriteInitialization(Span<byte> buffer, Mp4VideoTrack? video, Mp4AudioTrack? audio)
    {
        var writer = new Mp4Writer(buffer);

        var ftyp = writer.Begin("ftyp");
        writer.FourCC("iso6");
        writer.UInt32(0);
        writer.FourCC("iso6");
        writer.FourCC("iso5");
        writer.FourCC("mp41");
        writer.End(ftyp);

        var moov = writer.Begin("moov");
        var mvhd = writer.BeginFull("mvhd", 0, 0);
        writer.UInt32(0);
        writer.UInt32(0);
        writer.UInt32(1000);
        writer.UInt32(0);
        writer.UInt32(0x00010000);
        writer.UInt16(0x0100);
        writer.Zeros(10);
        writer.UnityMatrix();
        writer.Zeros(24);
        writer.UInt32(Mp4AudioTrack.Id + 1);
        writer.End(mvhd);

        if (video is not null)
        {
            WriteVideoTrack(ref writer, video);
        }

        if (audio is not null)
        {
            WriteAudioTrack(ref writer, audio);
        }

        var mvex = writer.Begin("mvex");
        if (video is not null)
        {
            WriteTrex(ref writer, Mp4VideoTrack.Id);
        }

        if (audio is not null)
        {
            WriteTrex(ref writer, Mp4AudioTrack.Id);
        }

        writer.End(mvex);
        writer.End(moov);
        return buffer[..writer.Position].ToArray();
    }

    /// <summary>Computes the exact size of a media fragment.</summary>
    /// <param name="tracks">The non-empty track fragments.</param>
    /// <param name="moofSize">The size of the <c>moof</c> box.</param>
    /// <returns>The total size of <c>moof</c> and <c>mdat</c>.</returns>
    /// <exception cref="Mp4SizeException">The size exceeds 32-bit box limits.</exception>
    public static long MeasureFragment(IReadOnlyList<Mp4TrackFragment> tracks, out long moofSize)
    {
        moofSize = 8 + MfhdSize;
        long data = 0;
        foreach (var track in tracks)
        {
            moofSize = checked(moofSize + 8 + TfhdSize + TfdtSize + TrunHeaderSize + ((long)track.Samples.Count * (track.IsVideo ? 16 : 8)));
            foreach (var sample in track.Samples)
            {
                data = checked(data + sample.Data.Length);
            }
        }

        var total = checked(moofSize + 8 + data);
        if (total > uint.MaxValue)
        {
            throw new Mp4SizeException("The media fragment exceeds 4 GiB.");
        }

        return total;
    }

    /// <summary>Writes a media fragment: <c>moof</c> (one <c>traf</c> per track) followed by one <c>mdat</c>.</summary>
    /// <param name="sequenceNumber">The <c>mfhd</c> sequence number; at least 1 and increasing.</param>
    /// <param name="tracks">The track fragments, each with at least one sample.</param>
    /// <param name="maxSize">The largest permitted output.</param>
    /// <returns>The fragment bytes.</returns>
    /// <exception cref="Mp4SizeException">The output would exceed <paramref name="maxSize"/>.</exception>
    public static byte[] WriteFragment(uint sequenceNumber, IReadOnlyList<Mp4TrackFragment> tracks, int maxSize)
    {
        var total = CheckFragment(tracks, maxSize, out var moofSize);
        var buffer = new byte[total];
        WriteFragment(sequenceNumber, tracks, moofSize, total, [buffer], total);
        return buffer;
    }

    /// <summary>Writes a media fragment into chunks rented from <paramref name="pool"/>.</summary>
    /// <param name="sequenceNumber">The <c>mfhd</c> sequence number; at least 1 and increasing.</param>
    /// <param name="tracks">The track fragments, each with at least one sample.</param>
    /// <param name="maxSize">The largest permitted output.</param>
    /// <param name="pool">The chunk pool.</param>
    /// <param name="name">The segment URI.</param>
    /// <returns>The segment, holding its creator's reference; nothing is rented if writing fails.</returns>
    /// <exception cref="Mp4SizeException">The output would exceed <paramref name="maxSize"/>.</exception>
    public static HlsSegment WriteFragment(uint sequenceNumber, IReadOnlyList<Mp4TrackFragment> tracks, int maxSize, HlsSegmentBufferPool pool, string name)
    {
        var total = CheckFragment(tracks, maxSize, out var moofSize);
        var chunks = pool.Rent(total);
        try
        {
            WriteFragment(sequenceNumber, tracks, moofSize, total, chunks, HlsSegmentBufferPool.ChunkSize);
            return new HlsSegment(HlsSegmentKind.Media, name, chunks, total, pool);
        }
        catch
        {
            pool.Return(chunks);
            throw;
        }
    }

    /// <summary>Validates the tracks and computes the fragment size.</summary>
    /// <param name="tracks">The track fragments.</param>
    /// <param name="maxSize">The largest permitted output.</param>
    /// <param name="moofSize">The size of the <c>moof</c> box.</param>
    /// <returns>The total size.</returns>
    /// <exception cref="Mp4SizeException">The output would exceed <paramref name="maxSize"/>.</exception>
    private static int CheckFragment(IReadOnlyList<Mp4TrackFragment> tracks, int maxSize, out int moofSize)
    {
        if (tracks.Count == 0 || tracks.Any(static t => t.Samples.Count == 0))
        {
            throw new ArgumentException("Every track fragment needs at least one sample.", nameof(tracks));
        }

        var total = MeasureFragment(tracks, out var moof);
        if (total > maxSize)
        {
            throw new Mp4SizeException($"The media fragment would be {total} bytes; the limit is {maxSize}.");
        }

        moofSize = (int)moof;
        return (int)total;
    }

    /// <summary>
    /// Writes a fragment of a measured size into <paramref name="chunks"/>: the <c>moof</c> and <c>mdat</c> header are
    /// built in a pooled scratch buffer, then they and the sample bytes are copied once into the output.
    /// </summary>
    /// <param name="sequenceNumber">The <c>mfhd</c> sequence number.</param>
    /// <param name="tracks">The validated track fragments.</param>
    /// <param name="moofSize">The measured <c>moof</c> size.</param>
    /// <param name="total">The measured total size.</param>
    /// <param name="chunks">The output: consecutive chunks of <paramref name="chunkSize"/> bytes holding at least <paramref name="total"/>.</param>
    /// <param name="chunkSize">The size of every chunk but the last.</param>
    private static void WriteFragment(uint sequenceNumber, IReadOnlyList<Mp4TrackFragment> tracks, int moofSize, int total, byte[][] chunks, int chunkSize)
    {
        var headerSize = moofSize + 8;
        var scratch = ArrayPool<byte>.Shared.Rent(headerSize);
        try
        {
            var writer = new Mp4Writer(scratch.AsSpan(0, headerSize));
            WriteMoof(ref writer, sequenceNumber, tracks, moofSize);
            writer.UInt32(checked((uint)(total - moofSize)));
            writer.FourCC("mdat");
            long position = 0;
            Copy(scratch.AsSpan(0, writer.Position), chunks, chunkSize, ref position);
            foreach (var track in tracks)
            {
                foreach (var sample in track.Samples)
                {
                    Copy(sample.Data.Span, chunks, chunkSize, ref position);
                }
            }

            if (writer.Position != headerSize || position != total)
            {
                throw new InvalidOperationException($"The fragment is {position} bytes; {total} were computed.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Copies bytes to the output chunks.</summary>
    /// <param name="source">The bytes.</param>
    /// <param name="chunks">The output chunks.</param>
    /// <param name="chunkSize">The size of every chunk but the last.</param>
    /// <param name="position">The output position, advanced.</param>
    private static void Copy(ReadOnlySpan<byte> source, byte[][] chunks, int chunkSize, ref long position)
    {
        while (!source.IsEmpty)
        {
            var offset = (int)(position % chunkSize);
            var destination = chunks[(int)(position / chunkSize)].AsSpan(offset, Math.Min(chunkSize - offset, source.Length));
            source[..destination.Length].CopyTo(destination);
            source = source[destination.Length..];
            position += destination.Length;
        }
    }

    /// <summary>Writes the <c>moof</c> box.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="sequenceNumber">The <c>mfhd</c> sequence number.</param>
    /// <param name="tracks">The track fragments.</param>
    /// <param name="moofSize">The measured <c>moof</c> size.</param>
    private static void WriteMoof(ref Mp4Writer writer, uint sequenceNumber, IReadOnlyList<Mp4TrackFragment> tracks, int moofSize)
    {
        var moof = writer.Begin("moof");
        var mfhd = writer.BeginFull("mfhd", 0, 0);
        writer.UInt32(sequenceNumber);
        writer.End(mfhd);

        long dataOffset = moofSize + 8;
        foreach (var track in tracks)
        {
            var traf = writer.Begin("traf");
            var tfhd = writer.BeginFull("tfhd", 0, 0x020000);
            writer.UInt32(track.TrackId);
            writer.End(tfhd);
            var tfdt = writer.BeginFull("tfdt", 1, 0);
            writer.UInt64(track.BaseDecodeTime);
            writer.End(tfdt);
            var trun = track.IsVideo ? writer.BeginFull("trun", 1, 0x000F01) : writer.BeginFull("trun", 0, 0x000301);
            writer.UInt32((uint)track.Samples.Count);
            writer.Int32(checked((int)dataOffset));
            foreach (var sample in track.Samples)
            {
                writer.UInt32(sample.Duration);
                writer.UInt32((uint)sample.Data.Length);
                if (track.IsVideo)
                {
                    writer.UInt32(sample.Flags);
                    writer.Int32(sample.CompositionOffset);
                }

                dataOffset += sample.Data.Length;
            }

            writer.End(trun);
            writer.End(traf);
        }

        writer.End(moof);
        if (writer.Position != moofSize)
        {
            throw new InvalidOperationException($"The moof box is {writer.Position} bytes; {moofSize} were computed.");
        }
    }

    /// <summary>Writes the video <c>trak</c>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="video">The track.</param>
    private static void WriteVideoTrack(ref Mp4Writer writer, Mp4VideoTrack video)
    {
        var trak = writer.Begin("trak");
        WriteTkhd(ref writer, Mp4VideoTrack.Id, 0, video.Width, video.Height);
        var mdia = writer.Begin("mdia");
        WriteMdhd(ref writer, Mp4VideoTrack.Timescale);
        WriteHdlr(ref writer, "vide", "VideoHandler");
        var minf = writer.Begin("minf");
        var vmhd = writer.BeginFull("vmhd", 0, 1);
        writer.Zeros(8);
        writer.End(vmhd);
        WriteDinf(ref writer);
        var stbl = BeginStbl(ref writer);

        var avc1 = writer.Begin("avc1");
        writer.Zeros(6);
        writer.UInt16(1);
        writer.Zeros(16);
        writer.UInt16(checked((ushort)video.Width));
        writer.UInt16(checked((ushort)video.Height));
        writer.UInt32(0x00480000);
        writer.UInt32(0x00480000);
        writer.UInt32(0);
        writer.UInt16(1);
        writer.Zeros(32);
        writer.UInt16(0x0018);
        writer.UInt16(0xFFFF);
        var avcC = writer.Begin("avcC");
        writer.Bytes(video.ConfigurationRecord.Span);
        writer.End(avcC);
        writer.End(avc1);

        EndStbl(ref writer, stbl);
        writer.End(minf);
        writer.End(mdia);
        writer.End(trak);
    }

    /// <summary>Writes the audio <c>trak</c>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="audio">The track.</param>
    private static void WriteAudioTrack(ref Mp4Writer writer, Mp4AudioTrack audio)
    {
        if (audio.SampleRate is < 1 or > ushort.MaxValue)
        {
            throw new Mp4SizeException($"The audio sample rate {audio.SampleRate} does not fit the mp4a sample entry.");
        }

        var trak = writer.Begin("trak");
        WriteTkhd(ref writer, Mp4AudioTrack.Id, 0x0100, 0, 0);
        var mdia = writer.Begin("mdia");
        WriteMdhd(ref writer, audio.SampleRate);
        WriteHdlr(ref writer, "soun", "SoundHandler");
        var minf = writer.Begin("minf");
        var smhd = writer.BeginFull("smhd", 0, 0);
        writer.Zeros(4);
        writer.End(smhd);
        WriteDinf(ref writer);
        var stbl = BeginStbl(ref writer);

        var mp4a = writer.Begin("mp4a");
        writer.Zeros(6);
        writer.UInt16(1);
        writer.Zeros(8);
        writer.UInt16(checked((ushort)audio.ChannelCount));
        writer.UInt16(16);
        writer.Zeros(4);
        writer.UInt32((uint)audio.SampleRate << 16);

        var config = audio.AudioSpecificConfig.Span;
        const int DecoderConfigFixed = 13;
        var specificInfo = DescriptorSize(config.Length);
        var decoderConfig = DescriptorSize(DecoderConfigFixed + specificInfo);
        var slConfig = DescriptorSize(1);
        var esds = writer.BeginFull("esds", 0, 0);
        WriteDescriptorHeader(ref writer, 0x03, 3 + decoderConfig + slConfig);
        writer.UInt16(0);
        writer.UInt8(0);
        WriteDescriptorHeader(ref writer, 0x04, DecoderConfigFixed + specificInfo);
        writer.UInt8(0x40);
        writer.UInt8(0x15);
        writer.UInt24(0);
        writer.UInt32(0);
        writer.UInt32(0);
        WriteDescriptorHeader(ref writer, 0x05, config.Length);
        writer.Bytes(config);
        WriteDescriptorHeader(ref writer, 0x06, 1);
        writer.UInt8(0x02);
        writer.End(esds);
        writer.End(mp4a);

        EndStbl(ref writer, stbl);
        writer.End(minf);
        writer.End(mdia);
        writer.End(trak);
    }

    /// <summary>Returns the size of a descriptor with a payload of <paramref name="payload"/> bytes.</summary>
    /// <param name="payload">The payload length.</param>
    /// <returns>Tag, length field and payload.</returns>
    private static int DescriptorSize(int payload) => 1 + LengthFieldSize(payload) + payload;

    /// <summary>Returns the size of an expandable descriptor length field.</summary>
    /// <param name="length">The length.</param>
    /// <returns>1 to 4 bytes.</returns>
    private static int LengthFieldSize(int length) => length switch
    {
        < 0x80 => 1,
        < 0x4000 => 2,
        < 0x200000 => 3,
        < 0x10000000 => 4,
        _ => throw new Mp4SizeException($"A descriptor of {length} bytes is too large."),
    };

    /// <summary>Writes a descriptor tag and its expandable length.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="length">The payload length.</param>
    private static void WriteDescriptorHeader(ref Mp4Writer writer, byte tag, int length)
    {
        writer.UInt8(tag);
        for (var shift = (LengthFieldSize(length) - 1) * 7; shift >= 0; shift -= 7)
        {
            writer.UInt8((byte)(((length >> shift) & 0x7F) | (shift > 0 ? 0x80 : 0)));
        }
    }

    /// <summary>Writes a track header.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="trackId">The track ID.</param>
    /// <param name="volume">0x0100 for audio, 0 for video.</param>
    /// <param name="width">Display width in pixels.</param>
    /// <param name="height">Display height in pixels.</param>
    private static void WriteTkhd(ref Mp4Writer writer, uint trackId, ushort volume, int width, int height)
    {
        var tkhd = writer.BeginFull("tkhd", 0, 0x000003);
        writer.UInt32(0);
        writer.UInt32(0);
        writer.UInt32(trackId);
        writer.UInt32(0);
        writer.UInt32(0);
        writer.Zeros(8);
        writer.UInt16(0);
        writer.UInt16(0);
        writer.UInt16(volume);
        writer.UInt16(0);
        writer.UnityMatrix();
        writer.UInt32(checked((uint)width) << 16);
        writer.UInt32(checked((uint)height) << 16);
        writer.End(tkhd);
    }

    /// <summary>Writes a media header with language <c>und</c>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="timescale">The media timescale.</param>
    private static void WriteMdhd(ref Mp4Writer writer, int timescale)
    {
        var mdhd = writer.BeginFull("mdhd", 0, 0);
        writer.UInt32(0);
        writer.UInt32(0);
        writer.UInt32((uint)timescale);
        writer.UInt32(0);
        writer.UInt16(0x55C4);
        writer.UInt16(0);
        writer.End(mdhd);
    }

    /// <summary>Writes a handler reference.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="handler">The handler type.</param>
    /// <param name="name">The handler name.</param>
    private static void WriteHdlr(ref Mp4Writer writer, string handler, string name)
    {
        var hdlr = writer.BeginFull("hdlr", 0, 0);
        writer.UInt32(0);
        writer.FourCC(handler);
        writer.Zeros(12);
        foreach (var c in name)
        {
            writer.UInt8((byte)c);
        }

        writer.UInt8(0);
        writer.End(hdlr);
    }

    /// <summary>Writes a data information box with one self-contained <c>url </c> entry.</summary>
    /// <param name="writer">The writer.</param>
    private static void WriteDinf(ref Mp4Writer writer)
    {
        var dinf = writer.Begin("dinf");
        var dref = writer.BeginFull("dref", 0, 0);
        writer.UInt32(1);
        var url = writer.BeginFull("url ", 0, 1);
        writer.End(url);
        writer.End(dref);
        writer.End(dinf);
    }

    /// <summary>Opens <c>stbl</c> and <c>stsd</c> with one entry; the sample entry follows.</summary>
    /// <param name="writer">The writer.</param>
    /// <returns>The starts of <c>stbl</c> and <c>stsd</c>.</returns>
    private static (int Stbl, int Stsd) BeginStbl(ref Mp4Writer writer)
    {
        var stbl = writer.Begin("stbl");
        var stsd = writer.BeginFull("stsd", 0, 0);
        writer.UInt32(1);
        return (stbl, stsd);
    }

    /// <summary>Closes <c>stsd</c> and writes the empty sample tables of a fragmented track.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="starts">The starts from <see cref="BeginStbl"/>.</param>
    private static void EndStbl(ref Mp4Writer writer, (int Stbl, int Stsd) starts)
    {
        writer.End(starts.Stsd);
        foreach (var (type, zeros) in new[] { ("stts", 4), ("stsc", 4), ("stsz", 8), ("stco", 4) })
        {
            var box = writer.BeginFull(type, 0, 0);
            writer.Zeros(zeros);
            writer.End(box);
        }

        writer.End(starts.Stbl);
    }

    /// <summary>Writes a track extends box with no defaults.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="trackId">The track ID.</param>
    private static void WriteTrex(ref Mp4Writer writer, uint trackId)
    {
        var trex = writer.BeginFull("trex", 0, 0);
        writer.UInt32(trackId);
        writer.UInt32(1);
        writer.UInt32(0);
        writer.UInt32(0);
        writer.UInt32(0);
        writer.End(trex);
    }
}
