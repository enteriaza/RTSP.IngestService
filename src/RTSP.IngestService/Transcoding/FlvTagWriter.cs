// Serialises validated source media as an FLV byte stream for the transcoder's input pipe. H.264, AAC and MP3 use
// legacy FLV signalling; HEVC, AV1, VP9, Opus, FLAC, AC-3 and E-AC-3 use Enhanced RTMP (v2) signalling exactly as
// FFmpeg's FLV muxer writes it, so FFmpeg's FLV demuxer reads the stream without probing guesswork. Publisher
// multitrack and ModEx signalling is never forwarded: the parser has already selected track 0.

using System.Buffers;
using System.Buffers.Binary;
using RTSP.IngestService.Media;

namespace RTSP.IngestService.Transcoding;

/// <summary>Writes an FLV stream of source media to a transcoder's input.</summary>
/// <remarks>Not thread-safe; one writer task owns it.</remarks>
internal sealed class FlvTagWriter
{
    /// <summary>FLV tag type: audio.</summary>
    private const byte AudioTag = 8;

    /// <summary>FLV tag type: video.</summary>
    private const byte VideoTag = 9;

    /// <summary>Tag header size.</summary>
    private const int TagHeaderSize = 11;

    /// <summary>Largest codec header written before the data.</summary>
    private const int MaxCodecHeaderSize = 8;

    /// <summary>The destination.</summary>
    private readonly Stream _output;

    /// <summary>Scratch for codec headers; copied into the tag buffer before any await.</summary>
    private readonly byte[] _codecHeader = new byte[MaxCodecHeaderSize];

    /// <summary>Initializes a new instance of the <see cref="FlvTagWriter"/> class.</summary>
    /// <param name="output">The destination stream.</param>
    public FlvTagWriter(Stream output)
    {
        _output = output;
    }

    /// <summary>Gets the number of bytes written.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>Writes the FLV file header.</summary>
    /// <param name="hasAudio">Whether the stream has audio.</param>
    /// <param name="hasVideo">Whether the stream has video.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when written.</returns>
    public async ValueTask WriteHeaderAsync(bool hasAudio, bool hasVideo, CancellationToken cancellationToken)
    {
        var header = new byte[13];
        "FLV"u8.CopyTo(header);
        header[3] = 1;
        header[4] = (byte)((hasAudio ? 4 : 0) | (hasVideo ? 1 : 0));
        header[8] = 9;
        await _output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        BytesWritten += header.Length;
    }

    /// <summary>Writes the sequence start tags for the configured tracks.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="timestamp">The FLV timestamp to write them at.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when written.</returns>
    public async ValueTask WriteSequenceStartsAsync(SourceStreamConfiguration configuration, int timestamp, CancellationToken cancellationToken)
    {
        if (configuration.Video is { } video)
        {
            var header = _codecHeader;
            var length = WriteVideoHeader(header, video.Codec, packetType: 0, keyframe: true, compositionTimeOffset: 0);
            await WriteTagAsync(VideoTag, timestamp, header.AsMemory(0, length), video.Record, cancellationToken).ConfigureAwait(false);
        }

        if (configuration.Audio is { } audio && audio.Codec != SourceAudioCodec.Mp3)
        {
            var header = _codecHeader;
            var length = WriteAudioHeader(header, audio, sequenceStart: true);
            await WriteTagAsync(AudioTag, timestamp, header.AsMemory(0, length), audio.Record, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes one sample.</summary>
    /// <param name="sample">The sample.</param>
    /// <param name="timestamp">The FLV decode timestamp.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when written.</returns>
    public ValueTask WriteSampleAsync(SourceSample sample, int timestamp, CancellationToken cancellationToken)
    {
        var header = _codecHeader;
        int length;
        byte type;
        if (sample.Track == MediaTrack.Video)
        {
            var codec = sample.Configuration.Video!.Codec;
            length = WriteVideoHeader(header, codec, packetType: 1, sample.IsKeyframe, sample.CompositionTimeOffset);
            type = VideoTag;
        }
        else
        {
            length = WriteAudioHeader(header, sample.Configuration.Audio!, sequenceStart: false);
            type = AudioTag;
        }

        return WriteTagAsync(type, timestamp, header.AsMemory(0, length), sample.Data, cancellationToken);
    }

    /// <summary>Writes a video tag header.</summary>
    /// <param name="destination">The destination, at least <see cref="MaxCodecHeaderSize"/> bytes.</param>
    /// <param name="codec">The codec.</param>
    /// <param name="packetType">0 for sequence start, 1 for coded frames.</param>
    /// <param name="keyframe">Whether the frame is a keyframe.</param>
    /// <param name="compositionTimeOffset">The composition time offset.</param>
    /// <returns>The header length.</returns>
    private static int WriteVideoHeader(Span<byte> destination, SourceVideoCodec codec, int packetType, bool keyframe, int compositionTimeOffset)
    {
        var frameType = keyframe ? 1 : 2;
        if (codec == SourceVideoCodec.H264)
        {
            destination[0] = (byte)((frameType << 4) | 7);
            destination[1] = (byte)packetType;
            WriteSi24(destination[2..], compositionTimeOffset);
            return 5;
        }

        destination[0] = (byte)(0x80 | (frameType << 4) | packetType);
        var fourCc = codec switch
        {
            SourceVideoCodec.Hevc => "hvc1"u8,
            SourceVideoCodec.Av1 => "av01"u8,
            _ => "vp09"u8,
        };
        fourCc.CopyTo(destination[1..]);
        if (codec == SourceVideoCodec.Hevc && packetType == 1)
        {
            WriteSi24(destination[5..], compositionTimeOffset);
            return 8;
        }

        return 5;
    }

    /// <summary>Writes an audio tag header.</summary>
    /// <param name="destination">The destination, at least <see cref="MaxCodecHeaderSize"/> bytes.</param>
    /// <param name="audio">The configuration.</param>
    /// <param name="sequenceStart">Whether the tag is a sequence start.</param>
    /// <returns>The header length.</returns>
    private static int WriteAudioHeader(Span<byte> destination, SourceAudioConfiguration audio, bool sequenceStart)
    {
        switch (audio.Codec)
        {
            case SourceAudioCodec.Aac:
                destination[0] = 0xAF;
                destination[1] = sequenceStart ? (byte)0 : (byte)1;
                return 2;
            case SourceAudioCodec.Mp3:
                destination[0] = (byte)((2 << 4) | (3 << 2) | (1 << 1) | (audio.Channels > 1 ? 1 : 0));
                return 1;
            default:
                destination[0] = (byte)(0x90 | (sequenceStart ? 0 : 1));
                var fourCc = audio.Codec switch
                {
                    SourceAudioCodec.Opus => "Opus"u8,
                    SourceAudioCodec.Flac => "fLaC"u8,
                    SourceAudioCodec.Ac3 => "ac-3"u8,
                    _ => "ec-3"u8,
                };
                fourCc.CopyTo(destination[1..]);
                return 5;
        }
    }

    /// <summary>Writes a signed 24-bit big-endian value.</summary>
    /// <param name="destination">Three bytes.</param>
    /// <param name="value">The value.</param>
    private static void WriteSi24(Span<byte> destination, int value)
    {
        destination[0] = (byte)(value >> 16);
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)value;
    }

    /// <summary>Writes one tag, assembled in a single pooled buffer so it reaches the pipe in one write.</summary>
    /// <param name="type">The tag type.</param>
    /// <param name="timestamp">The timestamp in milliseconds; non-negative.</param>
    /// <param name="codecHeader">The codec header.</param>
    /// <param name="data">The data.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when written.</returns>
    private async ValueTask WriteTagAsync(byte type, int timestamp, ReadOnlyMemory<byte> codecHeader, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var bodyLength = codecHeader.Length + data.Length;
        var total = TagHeaderSize + bodyLength + 4;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var span = buffer.AsSpan(0, total);
            span[0] = type;
            span[1] = (byte)(bodyLength >> 16);
            span[2] = (byte)(bodyLength >> 8);
            span[3] = (byte)bodyLength;
            span[4] = (byte)(timestamp >> 16);
            span[5] = (byte)(timestamp >> 8);
            span[6] = (byte)timestamp;
            span[7] = (byte)(timestamp >> 24);
            span[8] = 0;
            span[9] = 0;
            span[10] = 0;
            codecHeader.Span.CopyTo(span[TagHeaderSize..]);
            data.Span.CopyTo(span[(TagHeaderSize + codecHeader.Length)..]);
            BinaryPrimitives.WriteUInt32BigEndian(span[(TagHeaderSize + bodyLength)..], (uint)(TagHeaderSize + bodyLength));
            await _output.WriteAsync(buffer.AsMemory(0, total), cancellationToken).ConfigureAwait(false);
            BytesWritten += total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
