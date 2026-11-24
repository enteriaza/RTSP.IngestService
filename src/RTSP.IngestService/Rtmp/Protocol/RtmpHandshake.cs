// Server side of the plain RTMP handshake (RTMP specification 1.0, section 5.2): read C0+C1, send S0+S1+S2, read and
// validate C2. Only version 3 is accepted. S1 carries a zero version field, which tells clients to use the simple
// handshake rather than the digest ("FP9") variant; encrypted RTMPE (versions 6 and 8) is rejected explicitly.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Security.Cryptography;

namespace RTSP.IngestService.Rtmp.Protocol;

/// <summary>
/// Performs the server side of the RTMP handshake over a duplex pipe.
/// </summary>
internal static class RtmpHandshake
{
    /// <summary>The only supported RTMP version, sent in C0 and S0.</summary>
    public const byte ProtocolVersion = 3;

    /// <summary>Size of C1, C2, S1 and S2.</summary>
    public const int PacketSize = 1536;

    /// <summary>Total bytes read from the client: C0, C1 and C2.</summary>
    public const int ClientBytes = 1 + PacketSize + PacketSize;

    /// <summary>Offset of the random data in C1, C2, S1 and S2, after the time and version/time2 fields.</summary>
    private const int RandomOffset = 8;

    /// <summary>
    /// Performs the handshake. Bytes the client sends after C2 remain unread in <see cref="IDuplexPipe.Input"/>.
    /// </summary>
    /// <param name="transport">The connection transport.</param>
    /// <param name="serverTime">Value of the S1 time field (milliseconds of server uptime; any epoch is valid).</param>
    /// <param name="cancellationToken">Cancels the handshake, for example when the handshake timeout expires.</param>
    /// <returns>A task that completes when C2 has been validated.</returns>
    /// <exception cref="RtmpHandshakeException">C0 is not version 3, or C2 does not echo S1's random data.</exception>
    /// <exception cref="EndOfStreamException">The client closed the connection before the handshake completed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task PerformAsync(IDuplexPipe transport, uint serverTime, CancellationToken cancellationToken)
    {
        var c0c1 = new byte[1 + PacketSize];
        await ReadExactlyAsync(transport.Input, c0c1, cancellationToken).ConfigureAwait(false);

        var version = c0c1[0];
        if (version != ProtocolVersion)
        {
            throw new RtmpHandshakeException(version is 6 or 8
                ? $"The client requested encrypted RTMPE (version {version}), which is not supported."
                : $"The client requested RTMP version {version}; only version {ProtocolVersion} is supported.");
        }

        var response = new byte[1 + PacketSize + PacketSize];
        BuildResponse(c0c1, response, serverTime);
        await transport.Output.WriteAsync(response, cancellationToken).ConfigureAwait(false);

        var c2 = new byte[PacketSize];
        await ReadExactlyAsync(transport.Input, c2, cancellationToken).ConfigureAwait(false);

        if (!c2.AsSpan(RandomOffset).SequenceEqual(response.AsSpan(1 + RandomOffset, PacketSize - RandomOffset)))
        {
            throw new RtmpHandshakeException("C2 does not echo the random data sent in S1; digest and encrypted handshakes are not supported.");
        }
    }

    /// <summary>Writes S0, S1 (time, zero version, random) and S2 (echo of C1 with the read time) into <paramref name="response"/>.</summary>
    /// <param name="c0c1">The received C0 and C1.</param>
    /// <param name="response">Destination of S0, S1 and S2.</param>
    /// <param name="serverTime">The S1 time and S2 time2 value.</param>
    private static void BuildResponse(ReadOnlySpan<byte> c0c1, Span<byte> response, uint serverTime)
    {
        response[0] = ProtocolVersion;

        var s1 = response.Slice(1, PacketSize);
        BinaryPrimitives.WriteUInt32BigEndian(s1, serverTime);
        s1.Slice(4, 4).Clear();
        RandomNumberGenerator.Fill(s1[RandomOffset..]);

        var s2 = response.Slice(1 + PacketSize, PacketSize);
        c0c1.Slice(1, PacketSize).CopyTo(s2);
        BinaryPrimitives.WriteUInt32BigEndian(s2[4..], serverTime);
    }

    /// <summary>
    /// Reads exactly <paramref name="destination"/>.Length bytes, leaving any further bytes unconsumed. At most one
    /// transport read beyond the requested length is buffered.
    /// </summary>
    /// <param name="reader">The transport input.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the bytes have been read.</returns>
    /// <exception cref="EndOfStreamException">The input completed before enough bytes arrived.</exception>
    private static async Task ReadExactlyAsync(PipeReader reader, Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length >= destination.Length)
            {
                buffer.Slice(0, destination.Length).CopyTo(destination.Span);
                reader.AdvanceTo(buffer.GetPosition(destination.Length));
                return;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                throw new EndOfStreamException("The client closed the connection before the RTMP handshake completed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
