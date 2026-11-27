// The exceptions with which the transcoding stage ends a publish: a refusal by admission or codec policy, and a
// failure of the session's managed transcoding pipeline. Their messages never contain publisher data.

namespace RTSP.IngestService.Transcoding;

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

/// <summary>The transcoding pipeline of a session failed.</summary>
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
