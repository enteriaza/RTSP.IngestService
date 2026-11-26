// Streaming polyphase sample-rate converter. Output sample j is the input signal evaluated at t_j = j M / L input
// samples, y[j] = sum_k x[n0 + k] h_p[k] with n0 = floor(t_j) and phase p = (j M) mod L, using ResamplerFilter. The
// filter is zero-phase, so output sample j has exactly the presentation time of input time t_j: timestamps are the
// first frame's timestamp plus j / outputRate, computed from absolute sample counts so that no rounding accumulates.
// A stream of N input samples yields exactly ceil(N L / M) output samples: W samples are held back until enough
// look-ahead arrives, and Flush() releases them against zero padding. Frames may be split at arbitrary boundaries;
// the output is identical. Equal rates bypass filtering and copy samples.

using System.Runtime.Intrinsics;

namespace RTSP.IngestService.Media.Codecs.Audio.Processing;

/// <summary>Converts planar float audio of any channel count from one sample rate to another. Not thread-safe.</summary>
internal sealed class AudioResampler
{
    /// <summary>The largest accepted input frame in samples per channel.</summary>
    public const int MaxInputSamples = 65536;

    /// <summary>The filter, or <see langword="null"/> when the rates are equal.</summary>
    private readonly ResamplerFilter? _filter;

    /// <summary>Input history per channel; element 0 holds absolute input sample <see cref="_bufferStart"/>.</summary>
    private readonly float[][] _history;

    /// <summary>Valid samples in each history buffer.</summary>
    private int _historyLength;

    /// <summary>The absolute input index of history element 0.</summary>
    private long _bufferStart;

    /// <summary>Input samples received since the stream started.</summary>
    private long _inputCount;

    /// <summary>The index j of the next output sample.</summary>
    private long _outputIndex;

    /// <summary>floor(t_j) for the next output sample.</summary>
    private long _position;

    /// <summary>(j M) mod L for the next output sample.</summary>
    private int _phase;

    /// <summary>Whether a stream is in progress.</summary>
    private bool _started;

    /// <summary>The timestamp of the stream's first input sample.</summary>
    private long _anchor;

    /// <summary>The stream's time base.</summary>
    private MediaTimeBase _timeBase;

    /// <summary>The stream's channel layout.</summary>
    private ReadOnlyMemory<AudioChannel> _layout;

    /// <summary>Initializes a new instance of the <see cref="AudioResampler"/> class.</summary>
    /// <param name="inputRate">The input rate in hertz, 1 to 384000.</param>
    /// <param name="outputRate">The output rate in hertz, 1 to 384000.</param>
    /// <param name="channels">The channel count, 1 to 8.</param>
    public AudioResampler(int inputRate, int outputRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inputRate, 384000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(outputRate, 384000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);
        InputRate = inputRate;
        OutputRate = outputRate;
        Channels = channels;
        if (inputRate != outputRate)
        {
            _filter = ResamplerFilter.Get(inputRate, outputRate);
        }

        var capacity = (3 * Lookahead) + MaxInputSamples;
        _history = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            _history[c] = new float[_filter is null ? 0 : capacity];
        }
    }

    /// <summary>Gets the input rate in hertz.</summary>
    public int InputRate { get; }

    /// <summary>Gets the output rate in hertz.</summary>
    public int OutputRate { get; }

    /// <summary>Gets the channel count.</summary>
    public int Channels { get; }

    /// <summary>Gets the input samples held back for look-ahead (W); zero when the rates are equal.</summary>
    public int Lookahead => _filter?.HalfLength ?? 0;

    /// <summary>Gets the total output samples a stream of a given length produces once flushed: ceil(N L / M).</summary>
    /// <param name="inputSamples">The input samples per channel.</param>
    /// <returns>The output samples per channel.</returns>
    public long GetOutputLength(long inputSamples) =>
        _filter is null ? inputSamples : CeilingDivide(inputSamples * _filter.Interpolation, _filter.Decimation);

    /// <summary>Converts one input frame. The first frame after construction, <see cref="Flush"/> or <see cref="Reset"/> anchors the timeline.</summary>
    /// <param name="input">The input frame; the caller keeps ownership.</param>
    /// <returns>The output frame, owned by the caller, or <see langword="null"/> when all samples are held for look-ahead.</returns>
    public AudioFrame? Process(AudioFrame input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.SampleRate != InputRate || input.ChannelCount != Channels)
        {
            throw new ArgumentException($"The frame must be {Channels} channel(s) at {InputRate} Hz.", nameof(input));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.SampleCount, MaxInputSamples, nameof(input));
        if (!_started)
        {
            Start(input);
        }

        if (_filter is null)
        {
            var copy = CreateFrame(input.SampleCount);
            for (var c = 0; c < Channels; c++)
            {
                input.GetChannel(c).CopyTo(copy.GetChannel(c));
            }

            _inputCount += input.SampleCount;
            _outputIndex += input.SampleCount;
            return copy;
        }

        for (var c = 0; c < Channels; c++)
        {
            input.GetChannel(c).CopyTo(_history[c].AsSpan(_historyLength));
        }

        _historyLength += input.SampleCount;
        _inputCount += input.SampleCount;
        var limit = (_inputCount - _filter.HalfLength) * _filter.Interpolation;
        return Produce(limit <= 0 ? 0 : CeilingDivide(limit, _filter.Decimation));
    }

    /// <summary>Ends the stream: releases the held samples against zero padding, then resets.</summary>
    /// <returns>The remaining output, or <see langword="null"/> if there is none.</returns>
    public AudioFrame? Flush()
    {
        AudioFrame? output = null;
        if (_started && _filter is not null)
        {
            for (var c = 0; c < Channels; c++)
            {
                _history[c].AsSpan(_historyLength, _filter.HalfLength).Clear();
            }

            _historyLength += _filter.HalfLength;
            output = Produce(CeilingDivide(_inputCount * _filter.Interpolation, _filter.Decimation));
        }

        Reset();
        return output;
    }

    /// <summary>Discards all state, for example after a timestamp discontinuity; the next frame starts a new stream.</summary>
    public void Reset()
    {
        _started = false;
        _historyLength = 0;
        _bufferStart = 0;
        _inputCount = 0;
        _outputIndex = 0;
        _position = 0;
        _phase = 0;
    }

    /// <summary>Starts a stream with W samples of leading silence before input sample 0.</summary>
    /// <param name="first">The first frame.</param>
    private void Start(AudioFrame first)
    {
        _started = true;
        _anchor = first.Timestamp;
        _timeBase = first.TimeBase;
        _layout = first.Layout;
        if (_filter is not null)
        {
            for (var c = 0; c < Channels; c++)
            {
                _history[c].AsSpan(0, _filter.HalfLength).Clear();
            }

            _historyLength = _filter.HalfLength;
            _bufferStart = -_filter.HalfLength;
        }
    }

    /// <summary>Produces output samples up to (excluding) index <paramref name="end"/> and discards history no longer needed.</summary>
    /// <param name="end">The exclusive end output index.</param>
    /// <returns>The frame, or <see langword="null"/> if no samples are due.</returns>
    private AudioFrame? Produce(long end)
    {
        var filter = _filter!;
        var count = (int)Math.Max(0, end - _outputIndex);
        AudioFrame? frame = count == 0 ? null : CreateFrame(count);
        var taps = filter.TapCount;
        for (var i = 0; i < count; i++)
        {
            var coefficients = filter.GetPhase(_phase);
            var start = (int)(_position - _bufferStart - filter.HalfLength + 1);
            for (var c = 0; c < Channels; c++)
            {
                frame!.GetChannel(c)[i] = Dot(_history[c].AsSpan(start, taps), coefficients);
            }

            _phase += filter.Decimation;
            _position += _phase / filter.Interpolation;
            _phase %= filter.Interpolation;
        }

        _outputIndex += count;
        var discard = (int)Math.Clamp(_position - filter.HalfLength + 1 - _bufferStart, 0, _historyLength);
        if (discard > 0)
        {
            for (var c = 0; c < Channels; c++)
            {
                _history[c].AsSpan(discard, _historyLength - discard).CopyTo(_history[c]);
            }

            _historyLength -= discard;
            _bufferStart += discard;
        }

        return frame;
    }

    /// <summary>Creates an output frame for the next <paramref name="count"/> output samples.</summary>
    /// <param name="count">Samples per channel.</param>
    /// <returns>The frame.</returns>
    private AudioFrame CreateFrame(int count) =>
        new(_layout, count, OutputRate, _anchor + new MediaTimeBase(1, OutputRate).Rescale(_outputIndex, _timeBase), _timeBase);

    /// <summary>
    /// The dot product of two equal-length spans. The vector width is fixed at 128 bits, so the summation order and
    /// therefore the result are the same on every machine, under JIT and Native AOT.
    /// </summary>
    /// <param name="a">The first span.</param>
    /// <param name="b">The second span.</param>
    /// <returns>sum a[i] b[i].</returns>
    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector128<float>.Zero;
        var i = 0;
        for (; i <= a.Length - Vector128<float>.Count; i += Vector128<float>.Count)
        {
            sum += Vector128.Create(a[i..]) * Vector128.Create(b[i..]);
        }

        var total = Vector128.Sum(sum);
        for (; i < a.Length; i++)
        {
            total += a[i] * b[i];
        }

        return total;
    }

    /// <summary>Divides rounding towards positive infinity, for non-negative operands.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor.</param>
    /// <returns>ceil(value / divisor).</returns>
    private static long CeilingDivide(long value, long divisor) => (value + divisor - 1) / divisor;
}
