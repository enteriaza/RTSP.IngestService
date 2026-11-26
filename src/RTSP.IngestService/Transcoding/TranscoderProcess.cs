// The transcoder process boundary. Each publisher's media is transcoded by its own FFmpeg child process, fed FLV on
// standard input and read as FLV from standard output, so a decoder crash or hang ends only that publisher's session
// and no native code runs inside the service. The executable is the one deployed beside the service (or configured
// explicitly); the PATH is never searched. Standard error is kept in a small bounded ring for failure diagnostics.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Transcoding;

/// <summary>A running transcoder.</summary>
internal interface ITranscoderProcess : IAsyncDisposable
{
    /// <summary>Gets the stream the source FLV is written to.</summary>
    Stream Input { get; }

    /// <summary>Gets the stream the canonical FLV is read from.</summary>
    Stream Output { get; }

    /// <summary>Gets a task that completes with the exit code when the process has exited.</summary>
    Task<int> Exited { get; }

    /// <summary>Gets the most recent diagnostic output, bounded in size.</summary>
    string Diagnostics { get; }

    /// <summary>Closes the input, which tells the transcoder to flush and exit.</summary>
    void CompleteInput();

    /// <summary>Terminates the process immediately.</summary>
    void Kill();
}

/// <summary>Starts transcoders.</summary>
internal interface ITranscoderProcessFactory
{
    /// <summary>Starts a transcoder.</summary>
    /// <param name="arguments">The argument list.</param>
    /// <returns>The running transcoder.</returns>
    /// <exception cref="TranscodingFailedException">The process could not be started.</exception>
    ITranscoderProcess Start(IReadOnlyList<string> arguments);
}

/// <summary>Starts FFmpeg child processes.</summary>
internal sealed class FfmpegProcessFactory : ITranscoderProcessFactory
{
    /// <summary>The executable.</summary>
    private readonly string _path;

    /// <summary>Initializes a new instance of the <see cref="FfmpegProcessFactory"/> class.</summary>
    /// <param name="options">The transcoding options.</param>
    public FfmpegProcessFactory(IOptions<TranscodingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = options.Value.ResolveFfmpegPath(AppContext.BaseDirectory);
    }

    /// <inheritdoc />
    public ITranscoderProcess Start(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(_path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new TranscodingFailedException(TranscodingStage.Process, true, "The transcoder process did not start.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            process.Dispose();
            throw new TranscodingFailedException(TranscodingStage.Process, true, "The transcoder process could not be started: " + ex.Message, ex);
        }

        return new FfmpegProcess(process);
    }

    /// <summary>A running FFmpeg process.</summary>
    private sealed class FfmpegProcess : ITranscoderProcess
    {
        /// <summary>Lines of standard error kept.</summary>
        private const int MaxLines = 16;

        /// <summary>Characters kept per line.</summary>
        private const int MaxLineLength = 512;

        /// <summary>The process.</summary>
        private readonly Process _process;

        /// <summary>Recent standard error lines.</summary>
        private readonly Queue<string> _lines = new(MaxLines);

        /// <summary>Guards <see cref="_lines"/>.</summary>
        private readonly Lock _gate = new();

        /// <summary>1 once disposed.</summary>
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="FfmpegProcess"/> class.</summary>
        /// <param name="process">The started process.</param>
        public FfmpegProcess(Process process)
        {
            _process = process;
            Input = process.StandardInput.BaseStream;
            Output = process.StandardOutput.BaseStream;
            process.ErrorDataReceived += OnErrorData;
            process.BeginErrorReadLine();
            Exited = WaitAsync(process);
        }

        /// <inheritdoc />
        public Stream Input { get; }

        /// <inheritdoc />
        public Stream Output { get; }

        /// <inheritdoc />
        public Task<int> Exited { get; }

        /// <inheritdoc />
        public string Diagnostics
        {
            get
            {
                lock (_gate)
                {
                    return string.Join(" | ", _lines);
                }
            }
        }

        /// <inheritdoc />
        public void CompleteInput()
        {
            try
            {
                Input.Dispose();
            }
            catch (IOException)
            {
                // The process already closed its end.
            }
        }

        /// <inheritdoc />
        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Already exited.
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (!Exited.IsCompleted)
            {
                Kill();
            }

            try
            {
                await Exited.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The process could not be reaped; disposing releases our handles regardless.
            }

            CompleteInput();
            try
            {
                Output.Dispose();
            }
            catch (IOException)
            {
                // Ignore pipe errors on close.
            }

            _process.ErrorDataReceived -= OnErrorData;
            _process.Dispose();
        }

        /// <summary>Waits for the process to exit.</summary>
        /// <param name="process">The process.</param>
        /// <returns>The exit code.</returns>
        private static async Task<int> WaitAsync(Process process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }

        /// <summary>Keeps a standard error line.</summary>
        /// <param name="sender">The process.</param>
        /// <param name="e">The line.</param>
        private void OnErrorData(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            var line = e.Data.Length > MaxLineLength ? e.Data[..MaxLineLength] : e.Data;
            lock (_gate)
            {
                if (_lines.Count == MaxLines)
                {
                    _lines.Dequeue();
                }

                _lines.Enqueue(line.Trim());
            }
        }
    }
}

/// <summary>A publish was refused by transcoding admission or codec policy.</summary>
internal sealed class TranscodingRejectedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TranscodingRejectedException"/> class.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="message">A description without publisher data.</param>
    public TranscodingRejectedException(TranscodingRejectionReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Gets why the publish was refused.</summary>
    public TranscodingRejectionReason Reason { get; }
}

/// <summary>The transcoder for a session failed.</summary>
internal sealed class TranscodingFailedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TranscodingFailedException"/> class.</summary>
    /// <param name="stage">The failing stage.</param>
    /// <param name="initialization">Whether it failed before producing output.</param>
    /// <param name="message">A description without publisher data.</param>
    /// <param name="innerException">The cause, if any.</param>
    public TranscodingFailedException(TranscodingStage stage, bool initialization, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        Initialization = initialization;
    }

    /// <summary>Gets the failing stage.</summary>
    public TranscodingStage Stage { get; }

    /// <summary>Gets a value indicating whether the failure happened before any output.</summary>
    public bool Initialization { get; }
}
