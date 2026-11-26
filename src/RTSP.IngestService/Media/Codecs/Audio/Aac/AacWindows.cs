// AAC window sequences and window shapes (ISO/IEC 14496-3, 4.6.11.3.2), shared by the synthesis and analysis
// filterbanks: the sine window and the Kaiser-Bessel-derived window, alpha 4 for long and 6 for short windows. Only
// the rising halves are stored; the falling half of a window is the rising half reversed.

using RTSP.IngestService.Media.Codecs.Common.Transforms;

namespace RTSP.IngestService.Media.Codecs.Audio.Aac;

/// <summary>AAC window sequences.</summary>
internal enum AacWindowSequence
{
    /// <summary>One long window.</summary>
    OnlyLong = 0,

    /// <summary>Long-to-short transition.</summary>
    LongStart = 1,

    /// <summary>Eight short windows.</summary>
    EightShort = 2,

    /// <summary>Short-to-long transition.</summary>
    LongStop = 3,
}

/// <summary>AAC window shapes.</summary>
internal static class AacWindows
{
    /// <summary>Gets the rising halves (1024 values) of the long windows: [0] sine, [1] KBD.</summary>
    public static float[][] Long { get; } = [WindowFunctions.SineRisingHalf(2048), WindowFunctions.KbdRisingHalf(2048, 4)];

    /// <summary>Gets the rising halves (128 values) of the short windows: [0] sine, [1] KBD.</summary>
    public static float[][] Short { get; } = [WindowFunctions.SineRisingHalf(256), WindowFunctions.KbdRisingHalf(256, 6)];
}
