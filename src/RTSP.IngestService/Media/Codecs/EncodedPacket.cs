// A compressed access unit handed to a decoder. The decoder reads the data synchronously during the call and keeps
// no reference to it afterwards, so the caller may reuse or return the buffer as soon as the call returns.

namespace RTSP.IngestService.Media.Codecs;

/// <summary>One compressed access unit.</summary>
/// <param name="Data">The access unit, borrowed for the duration of the decode call.</param>
/// <param name="Timestamp">The presentation timestamp in <paramref name="TimeBase"/> ticks.</param>
/// <param name="TimeBase">The timestamp time base.</param>
internal readonly record struct EncodedPacket(ReadOnlyMemory<byte> Data, long Timestamp, MediaTimeBase TimeBase);
