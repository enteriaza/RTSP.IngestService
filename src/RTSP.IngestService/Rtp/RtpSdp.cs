// Session descriptions (RFC 8866) for an RTP output's current publication, which receivers such as FFmpeg and VLC need
// to decode the streams: H.264 with the RFC 6184 media type parameters (packetization-mode=1, profile-level-id and the
// parameter sets) and AAC with the RFC 3640 mpeg4-generic AAC-hbr parameters and the AudioSpecificConfig. Only tracks
// that are both configured for the output and present in the stream are described.

using System.Globalization;
using System.Net.Sockets;
using System.Text;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;

namespace RTSP.IngestService.Rtp;

/// <summary>Renders SDP for an RTP output session.</summary>
internal static class RtpSdp
{
    /// <summary>Media type of session descriptions.</summary>
    public const string ContentType = "application/sdp";

    /// <summary>The <c>a=tool</c> attribute, naming the service.</summary>
    private static readonly string ToolAttribute = "a=tool:" + ApplicationIdentity.ServiceAssemblyName;

    /// <summary>Renders the description of a session for a stream description.</summary>
    /// <param name="session">The output session.</param>
    /// <param name="info">The publication's current stream description.</param>
    /// <returns>The SDP text (CRLF line endings), or <see langword="null"/> if no configured track is present yet.</returns>
    public static string? Render(RtpOutputSession session, MediaStreamInfo info)
    {
        var output = session.Output;
        var video = output.VideoPort is not null && info.Video is not null && session.Video is not null;
        var audio = output.AudioPort is not null && info.Audio is not null;
        if (!video && !audio)
        {
            return null;
        }

        var ipv6 = session.Destination.AddressFamily == AddressFamily.InterNetworkV6;
        var family = ipv6 ? "IP6" : "IP4";
        var origin = output.SourceAddress ?? (ipv6 ? "::" : "0.0.0.0");
        var connection = output.Mode == RtpOutputMode.Multicast && !ipv6
            ? $"{output.Destination}/{output.EffectiveTimeToLive.ToString(CultureInfo.InvariantCulture)}"
            : output.Destination;
        var sessionId = BitConverter.ToUInt64(session.SessionId.ToByteArray(), 8) & long.MaxValue;

        var sdp = new StringBuilder(512);
        Line(sdp, "v=0");
        Line(sdp, $"o=- {sessionId.ToString(CultureInfo.InvariantCulture)} {info.Version.ToString(CultureInfo.InvariantCulture)} IN {family} {origin}");
        Line(sdp, $"s={output.Name}");
        Line(sdp, $"c=IN {family} {connection}");
        Line(sdp, "t=0 0");
        Line(sdp, ToolAttribute);

        if (video)
        {
            var stream = session.Video!.Stream;
            var configuration = info.Video!.Configuration;
            var pt = stream.PayloadType.ToString(CultureInfo.InvariantCulture);
            Line(sdp, $"m=video {output.VideoPort!.Value.ToString(CultureInfo.InvariantCulture)} RTP/AVP {pt}");
            Line(sdp, $"a=rtpmap:{pt} H264/{H264RtpPacketizer.ClockRate.ToString(CultureInfo.InvariantCulture)}");
            Line(sdp, $"a=fmtp:{pt} packetization-mode=1;profile-level-id={ProfileLevelId(configuration)};sprop-parameter-sets={ParameterSets(configuration)}");
            Line(sdp, $"a=ssrc:{stream.Ssrc.ToString(CultureInfo.InvariantCulture)} cname:{session.CanonicalName}");
        }

        if (audio)
        {
            var configuration = info.Audio!.Configuration;
            var clock = session.AudioStream?.ClockRate ?? output.AudioClockRate ?? configuration.SampleRate;
            var pt = output.AudioPayloadType.ToString(CultureInfo.InvariantCulture);
            var channels = configuration.ChannelCount is { } count ? "/" + count.ToString(CultureInfo.InvariantCulture) : string.Empty;
            Line(sdp, $"m=audio {output.AudioPort!.Value.ToString(CultureInfo.InvariantCulture)} RTP/AVP {pt}");
            Line(sdp, $"a=rtpmap:{pt} MPEG4-GENERIC/{clock.ToString(CultureInfo.InvariantCulture)}{channels}");
            Line(sdp, $"a=fmtp:{pt} streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config={Convert.ToHexStringLower(configuration.Record.Span)}");
            if (session.AudioStream is { } stream)
            {
                Line(sdp, $"a=ssrc:{stream.Ssrc.ToString(CultureInfo.InvariantCulture)} cname:{session.CanonicalName}");
            }
        }

        return sdp.ToString();
    }

    /// <summary>Returns the RFC 6184 <c>profile-level-id</c>: profile_idc, constraint flags and level_idc as hex.</summary>
    /// <param name="configuration">The decoder configuration.</param>
    /// <returns>Six hexadecimal digits.</returns>
    internal static string ProfileLevelId(AvcDecoderConfiguration configuration) =>
        string.Create(CultureInfo.InvariantCulture, $"{configuration.Profile:x2}{configuration.ProfileCompatibility:x2}{configuration.Level:x2}");

    /// <summary>Returns the RFC 6184 <c>sprop-parameter-sets</c>: base64 SPS and PPS NAL units, comma separated.</summary>
    /// <param name="configuration">The decoder configuration.</param>
    /// <returns>The value.</returns>
    internal static string ParameterSets(AvcDecoderConfiguration configuration) =>
        string.Join(',', configuration.SequenceParameterSets.Concat(configuration.PictureParameterSets).Select(static p => Convert.ToBase64String(p.Span)));

    /// <summary>Appends a line with CRLF.</summary>
    /// <param name="sdp">The builder.</param>
    /// <param name="line">The line.</param>
    private static void Line(StringBuilder sdp, string line) => sdp.Append(line).Append("\r\n");
}
