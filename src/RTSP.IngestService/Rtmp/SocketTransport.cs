// Adapts an accepted TCP socket to System.IO.Pipelines. The reader pulls from the socket only when the connection
// asks for more data, so a connection that stops reading (for example while its consumer applies backpressure)
// stops consuming socket data and TCP flow control slows the publisher. Buffers come from the shared MemoryPool.

using System.IO.Pipelines;
using System.Net.Sockets;

namespace RTSP.IngestService.Rtmp;

/// <summary>
/// A duplex pipe over a connected socket. Owns the socket.
/// </summary>
/// <remarks>
/// At most one read of up to <see cref="ReceiveBufferSize"/> bytes is buffered beyond what the connection has
/// consumed. Not thread-safe beyond the usual single reader and single writer.
/// </remarks>
internal sealed class SocketTransport : IDuplexPipe, IAsyncDisposable
{
    /// <summary>Size of each socket read.</summary>
    public const int ReceiveBufferSize = 64 * 1024;

    /// <summary>The socket.</summary>
    private readonly Socket _socket;

    /// <summary>Stream over the socket, which owns it.</summary>
    private readonly NetworkStream _stream;

    /// <summary>Initializes a new instance of the <see cref="SocketTransport"/> class.</summary>
    /// <param name="socket">A connected socket; ownership passes to the transport.</param>
    public SocketTransport(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        Input = PipeReader.Create(_stream, new StreamPipeReaderOptions(bufferSize: ReceiveBufferSize, minimumReadSize: 4096, leaveOpen: true));
        Output = PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <summary>Gets the inbound pipe.</summary>
    public PipeReader Input { get; }

    /// <summary>Gets the outbound pipe.</summary>
    public PipeWriter Output { get; }

    /// <summary>Completes both pipes, shuts the socket down and closes it.</summary>
    /// <returns>A task that completes when the socket is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await Input.CompleteAsync().ConfigureAwait(false);
        try
        {
            await Output.CompleteAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
