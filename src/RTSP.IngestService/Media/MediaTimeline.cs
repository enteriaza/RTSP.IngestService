// The per-publisher media timeline. RTMP timestamps are 32-bit milliseconds that wrap after about 49.7 days; the
// timeline unwraps them into 64-bit decode timestamps shared by audio and video, and classifies each track's steps
// as normal, regressions or jumps without changing any value.

namespace RTSP.IngestService.Media;

/// <summary>
/// Unwraps RTMP timestamps of one publishing session onto a 64-bit timeline and tracks per-track steps.
/// </summary>
/// <remarks>
/// <para>
/// Unwrapping: the first audio or video message fixes the origin (its RTMP timestamp, not zero). Each later message's
/// position is the previous message's position plus the signed 32-bit difference of their RTMP timestamps, so a
/// rollover from 0xFFFFFFFF to 0 is a +1 step and a small backward step stays a small backward step. Audio and
/// video share the reference so their relative timing is kept. A difference larger than 2^31 ms in either direction
/// cannot be told apart from rollover and is read as the shorter step.
/// </para>
/// <para>Not thread-safe; owned by one session, whose messages are processed sequentially.</para>
/// </remarks>
internal sealed class MediaTimeline
{
    /// <summary>Threshold above which a forward step is a jump, in milliseconds.</summary>
    private readonly long _jumpThreshold;

    /// <summary>Per-track state.</summary>
    private readonly TrackState _video = new();

    /// <summary>Per-track state.</summary>
    private readonly TrackState _audio = new();

    /// <summary>Whether a message has been seen.</summary>
    private bool _started;

    /// <summary>RTMP timestamp of the previous message.</summary>
    private uint _lastSource;

    /// <summary>Timeline position of the previous message.</summary>
    private long _lastPosition;

    /// <summary>Initializes a new instance of the <see cref="MediaTimeline"/> class.</summary>
    /// <param name="jumpThreshold">Forward steps above this are classified as <see cref="TimestampDiscontinuity.Jump"/>.</param>
    public MediaTimeline(TimeSpan jumpThreshold)
    {
        _jumpThreshold = (long)jumpThreshold.TotalMilliseconds;
    }

    /// <summary>Places an RTMP timestamp on the timeline. Call for every audio and video message, in arrival order.</summary>
    /// <param name="source">The RTMP timestamp.</param>
    /// <returns>The 64-bit decode timestamp.</returns>
    public long Unwrap(uint source)
    {
        if (!_started)
        {
            _started = true;
            _lastSource = source;
            _lastPosition = source;
            return source;
        }

        _lastPosition += unchecked((int)(source - _lastSource));
        _lastSource = source;
        return _lastPosition;
    }

    /// <summary>
    /// Records a delivered sample's decode timestamp for <paramref name="track"/> and classifies the step from the
    /// previous delivered sample of that track.
    /// </summary>
    /// <param name="track">The track.</param>
    /// <param name="decodeTimestamp">The sample's decode timestamp from <see cref="Unwrap"/>.</param>
    /// <param name="delta">The step from the previous sample, or <see langword="null"/> for the first.</param>
    /// <returns>The classification.</returns>
    public TimestampDiscontinuity Advance(MediaTrack track, long decodeTimestamp, out long? delta)
    {
        var state = track == MediaTrack.Video ? _video : _audio;
        delta = state.Last is { } last ? decodeTimestamp - last : null;
        state.Last = decodeTimestamp;
        return delta switch
        {
            null => TimestampDiscontinuity.None,
            < 0 => TimestampDiscontinuity.Regression,
            var step when step > _jumpThreshold => TimestampDiscontinuity.Jump,
            _ => TimestampDiscontinuity.None,
        };
    }

    /// <summary>Last decode timestamp of one track.</summary>
    private sealed class TrackState
    {
        /// <summary>Gets or sets the last delivered decode timestamp, if any.</summary>
        public long? Last { get; set; }
    }
}
