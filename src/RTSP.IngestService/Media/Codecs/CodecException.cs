// The single failure type of the managed codecs. A codec that throws it has stopped at an unknown point in its state
// and is faulted: the owner disposes it and creates a new one rather than continuing to feed it.

namespace RTSP.IngestService.Media.Codecs;

/// <summary>Why a codec operation failed.</summary>
internal enum CodecErrorKind
{
    /// <summary>The bitstream or configuration violates the codec specification or is truncated.</summary>
    InvalidData,

    /// <summary>The input is valid but uses a profile, tool or layout this implementation does not decode.</summary>
    Unsupported,

    /// <summary>The input exceeds a configured or structural resource limit.</summary>
    ResourceLimit,
}

/// <summary>A managed codec failure. The message never contains payload bytes.</summary>
internal sealed class CodecException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CodecException"/> class.</summary>
    /// <param name="kind">The failure kind.</param>
    /// <param name="message">The message.</param>
    public CodecException(CodecErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>Gets the failure kind.</summary>
    public CodecErrorKind Kind { get; }

    /// <summary>Throws an <see cref="CodecErrorKind.InvalidData"/> failure.</summary>
    /// <param name="message">The message.</param>
    /// <exception cref="CodecException">Always.</exception>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void InvalidData(string message) => throw new CodecException(CodecErrorKind.InvalidData, message);

    /// <summary>Throws an <see cref="CodecErrorKind.Unsupported"/> failure.</summary>
    /// <param name="message">The message.</param>
    /// <exception cref="CodecException">Always.</exception>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void Unsupported(string message) => throw new CodecException(CodecErrorKind.Unsupported, message);
}
