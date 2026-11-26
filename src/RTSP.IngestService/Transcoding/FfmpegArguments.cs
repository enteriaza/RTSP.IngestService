// Builds the FFmpeg argument list for one transcoder process from the validated source configuration and the
// strongly typed transcoding options. Every argument is fixed text or a number/enum from validated options; no
// configuration or publisher-supplied text ever reaches the command line. The output is the canonical profile:
// H.264 High, yuv420p, 8-bit, BT.709 SDR limited range, closed GOPs with IDR keyframes on a fixed cadence, and
// AAC-LC 48 kHz stereo, multiplexed as FLV on standard output.

using System.Globalization;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Transcoding;

/// <summary>Builds FFmpeg argument lists.</summary>
internal static class FfmpegArguments
{
    /// <summary>
    /// The FLV timestamp written for a process's first sample, in milliseconds. It keeps encoder priming (AAC
    /// pre-roll) and earlier-interleaved samples of the other track non-negative.
    /// </summary>
    public const int TimestampOffset = 1000;

    /// <summary>
    /// How far, in microseconds, the output muxer may let one track run ahead while waiting for the other. The
    /// encoder's lookahead delays the first video frames; with a shorter bound audio is written ahead of the first
    /// keyframe, where HLS packaging cannot use it. It stays finite so a stalled track cannot hold output indefinitely.
    /// </summary>
    private const string MaxInterleaveDelta = "5000000";

    /// <summary>Builds the arguments for a transcoder process.</summary>
    /// <param name="configuration">The source configuration; at least one track is configured.</param>
    /// <param name="options">The transcoding options.</param>
    /// <param name="keyframeInterval">The IDR interval.</param>
    /// <returns>The argument list.</returns>
    public static List<string> Build(SourceStreamConfiguration configuration, TranscodingOptions options, TimeSpan keyframeInterval)
    {
        var video = configuration.Video;
        var audio = configuration.Audio;
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error" };
        if (options.ThreadsPerSession > 0)
        {
            args.AddRange(["-threads", Number(options.ThreadsPerSession)]);
        }

        args.AddRange(["-analyzeduration", "500000", "-probesize", "1048576"]);
        if (video is not null)
        {
            args.AddRange(["-c:v", VideoDecoder(video.Codec)]);
        }

        if (audio is not null)
        {
            args.AddRange(["-c:a", AudioDecoder(audio.Codec)]);
        }

        args.AddRange(["-f", "flv", "-i", "pipe:0"]);
        if (video is not null)
        {
            var v = options.Video;
            args.AddRange(["-map", "0:v:0", "-vf", VideoFilter(configuration.Color), "-fps_mode", "passthrough"]);
            args.AddRange(["-c:v", "libx264", "-profile:v", "high", "-preset", Preset(v.Preset), "-pix_fmt", "yuv420p"]);
            args.AddRange(["-crf", Number(v.ConstantRateFactor), "-maxrate", Number(v.MaxBitrateKilobits) + "k", "-bufsize", Number(v.MaxBitrateKilobits * 2) + "k"]);
            args.AddRange(["-bf", "0", "-g", "100000", "-sc_threshold", "0", "-forced-idr", "1"]);
            var interval = keyframeInterval.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            args.AddRange(["-force_key_frames", $"expr:if(isnan(prev_forced_t),1,gte(t-prev_forced_t,{interval}))"]);
            args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv"]);
            if (options.ThreadsPerSession > 0)
            {
                args.AddRange(["-threads:v", Number(options.ThreadsPerSession)]);
            }
        }

        if (audio is not null)
        {
            args.AddRange(["-map", "0:a:0", "-af", "aresample=async=1:min_hard_comp=0.1,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo"]);
            args.AddRange(["-c:a", "aac", "-profile:a", "aac_low", "-b:a", Number(options.Audio.BitrateKilobits) + "k", "-ar", "48000", "-ac", "2"]);
        }

        args.AddRange(["-copyts", "-max_interleave_delta", MaxInterleaveDelta, "-flush_packets", "1", "-flvflags", "no_duration_filesize", "-f", "flv", "pipe:1"]);
        return args;
    }

    /// <summary>Returns the FFmpeg decoder for a video codec.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>The decoder name.</returns>
    public static string VideoDecoder(SourceVideoCodec codec) => codec switch
    {
        SourceVideoCodec.H264 => "h264",
        SourceVideoCodec.Hevc => "hevc",
        SourceVideoCodec.Av1 => "libdav1d",
        _ => "vp9",
    };

    /// <summary>Returns the FFmpeg decoder for an audio codec.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>The decoder name.</returns>
    public static string AudioDecoder(SourceAudioCodec codec) => codec switch
    {
        SourceAudioCodec.Aac => "aac",
        SourceAudioCodec.Mp3 => "mp3float",
        SourceAudioCodec.Opus => "opus",
        SourceAudioCodec.Flac => "flac",
        SourceAudioCodec.Ac3 => "ac3",
        _ => "eac3",
    };

    /// <summary>
    /// Builds the video filter: crop to even dimensions (4:2:0 needs them), convert to BT.709 limited-range 8-bit
    /// 4:2:0 with perceptual intent (which tone-maps BT.2100 PQ/HLG to SDR), and, when E-RTMP metadata declared HDR,
    /// state the input colour explicitly in case the bitstream does not.
    /// </summary>
    /// <param name="color">The declared colour description.</param>
    /// <returns>The filter graph.</returns>
    public static string VideoFilter(VideoColorDescription? color)
    {
        var input = string.Empty;
        if (color is { IsHighDynamicRange: true })
        {
            input = "in_primaries=bt2020:in_color_matrix=bt2020:in_transfer=" + (color.Transfer == 16 ? "smpte2084" : "arib-std-b67") + ":";
        }

        return "crop=trunc(iw/2)*2:trunc(ih/2)*2,scale=" + input
            + "out_color_matrix=bt709:out_primaries=bt709:out_transfer=bt709:out_range=tv:intent=perceptual,format=yuv420p";
    }

    /// <summary>Returns the x264 preset name.</summary>
    /// <param name="preset">The preset.</param>
    /// <returns>The name.</returns>
    private static string Preset(TranscodingVideoPreset preset) => preset switch
    {
        TranscodingVideoPreset.Ultrafast => "ultrafast",
        TranscodingVideoPreset.Superfast => "superfast",
        TranscodingVideoPreset.Veryfast => "veryfast",
        TranscodingVideoPreset.Faster => "faster",
        TranscodingVideoPreset.Fast => "fast",
        _ => "medium",
    };

    /// <summary>Formats an integer invariantly.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
