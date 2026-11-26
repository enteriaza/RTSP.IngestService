// Incremental FLV reader for the transcoder's output pipe. It validates the file header and every tag header,
// bounds each tag by the configured maximum sample size before allocating, and hands each audio and video tag body
// to the caller as a pooled buffer, so canonical output enters the existing media pipeline exactly as RTMP messages
// from a publisher would.

using System.Buffers;
using System.Buffers.Binary;
using RTSP.IngestService.Rtmp.Protocol;

namespace RTSP.IngestService.Transcoding;

/// <summary>One FLV tag read from the transcoder.</summary>
/// <param name="Type">The tag type (8 audio, 9 video, 18 script data).</param>
/// <param name="Timestamp">The 32-bit FLV timestamp in milliseconds.</param>
/// <param name="Body">The tag body, owned by the caller.</param>
internal readonly record struct FlvTag(byte Type, int Timestamp, PooledBuffer Body);

/// <summary>Reads FLV tags from a stream.</summary>
/// <remarks>Not thread-safe; one reader task owns it.</remarks>
internal sealed class FlvTagReader : IDisposable
{
    /// <summary>Tag header size.</summary>
    private const int TagHeaderSize = 11;

    /// <summary>The source.</summary>
    private readonly Stream _input;

    /// <summary>The largest accepted tag body.</summary>
    private readonly int _maxTagSize;

    /// <summary>Scratch for headers.</summary>
    private readonly byte[] _header = new byte[TagHeaderSize + 4];

    /// <summary>Scratch for bodies; grows up to <see cref="_maxTagSize"/>.</summary>
    private byte[] _body;

    /// <summary>Whether the file header has been read.</summary>
    private bool _started;

    /// <summary>Initializes a new instance of the <see cref="FlvTagReader"/> class.</summary>
    /// <param name="input">The source stream.</param>
    /// <param name="maxTagSize">The largest accepted tag body.</param>
    public FlvTagReader(Stream input, int maxTagSize)
    {
        _input = input;
        _maxTagSize = maxTagSize;
        _body = ArrayPool<byte>.Shared.Rent(64 * 1024);
    }

    /// <summary>Reads the next tag.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The tag, or <see langword="null"/> at the end of the stream.</returns>
    /// <exception cref="InvalidDataException">The stream is not valid FLV or a tag exceeds the limit.</exception>
    public async ValueTask<FlvTag?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            if (!await TryFillAsync(_header.AsMemory(0, 13), cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (!_header.AsSpan(0, 3).SequenceEqual("FLV"u8) || _header[3] != 1)
            {
                throw new InvalidDataException("The transcoder output is not FLV.");
            }

            var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(5));
            if (dataOffset != 9)
            {
                throw new InvalidDataException("The transcoder output has an unexpected FLV header size.");
            }

            _started = true;
        }

        if (!await TryFillAsync(_header.AsMemory(0, TagHeaderSize), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var type = (byte)(_header[0] & 0x1F);
        if ((_header[0] & 0x20) != 0)
        {
            throw new InvalidDataException("The transcoder output contains an encrypted FLV tag.");
        }

        var size = (_header[1] << 16) | (_header[2] << 8) | _header[3];
        var timestamp = (_header[7] << 24) | (_header[4] << 16) | (_header[5] << 8) | _header[6];
        if (size > _maxTagSize)
        {
            throw new InvalidDataException($"A transcoder output tag is {size} bytes; the limit is {_maxTagSize}.");
        }

        if (_body.Length < size)
        {
            ArrayPool<byte>.Shared.Return(_body);
            _body = ArrayPool<byte>.Shared.Rent(size);
        }

        if (!await TryFillAsync(_body.AsMemory(0, size), cancellationToken).ConfigureAwait(false)
            || !await TryFillAsync(_header.AsMemory(TagHeaderSize, 4), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The transcoder output ended inside an FLV tag.");
        }

        var body = new PooledBuffer(ArrayPool<byte>.Shared);
        try
        {
            if (size > 0)
            {
                body.EnsureCapacity(size);
                body.Append(new ReadOnlySequence<byte>(_body, 0, size));
            }
        }
        catch
        {
            body.Dispose();
            throw;
        }

        return new FlvTag(type, timestamp, body);
    }

    /// <summary>Returns the scratch buffer to the pool.</summary>
    public void Dispose()
    {
        var body = _body;
        _body = [];
        if (body.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    /// <summary>Fills <paramref name="buffer"/> completely.</summary>
    /// <param name="buffer">The destination.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="false"/> if the stream ended before the first byte.</returns>
    /// <exception cref="InvalidDataException">The stream ended part-way.</exception>
    private async ValueTask<bool> TryFillAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await _input.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new InvalidDataException("The transcoder output ended inside an FLV header.");
            }

            read += count;
        }

        return true;
    }
}
