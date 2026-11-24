// Tests for the version 1 stream event contract: the exact JSON shape produced by the source-generated context, the
// fixed field set (guarding additive-only evolution), optional fields, and that no internal identifier, credential or
// connection detail can appear in an event.

using System.Net;
using System.Reflection;
using System.Text.Json;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Tests.Streams;

/// <summary>Tests for <see cref="StreamEvent"/> and <see cref="StreamEventJsonContext"/>.</summary>
public sealed class StreamEventContractTests
{
    /// <summary>An incarnation identifier for fixtures.</summary>
    private const string Incarnation = "0123456789abcdef0123456789abcdef";

    /// <summary>Verifies the version 1 envelope serializes with exactly its documented fields, names and formats.</summary>
    [Fact]
    public void Serialize_ProducesTheVersion1Envelope()
    {
        var streamEvent = StreamEventFactory.Create(
            StreamEventType.Ended,
            "live",
            "camera1",
            Incarnation,
            new DateTimeOffset(2026, 11, 24, 12, 30, 0, TimeSpan.FromHours(2)),
            StreamEventReasons.ClientDisconnected);

        var json = JsonSerializer.Serialize(streamEvent, StreamEventJsonContext.Default.StreamEvent);

        Assert.Equal(
            """{"schemaVersion":1,"type":"Ended","timestamp":"2026-11-24T10:30:00+00:00","application":"live","stream":"camera1","incarnationId":"0123456789abcdef0123456789abcdef","reason":"client_disconnected"}""",
            json);
    }

    /// <summary>Verifies optional fields are omitted when absent and the media description serializes fully when present.</summary>
    [Fact]
    public void Serialize_MediaDescribed_IncludesTracksAndOmitsAbsentFields()
    {
        var streamEvent = StreamEventFactory.Create(
            StreamEventType.MediaDescribed,
            "live",
            "camera1",
            Incarnation,
            DateTimeOffset.UnixEpoch,
            media: new StreamMediaDescription
            {
                Video = new StreamVideoDescription { Codec = "avc1.64001F", ConfigurationVersion = 1, Width = 1280, Height = 720 },
                Audio = new StreamAudioDescription { Codec = "mp4a.40.2", ConfigurationVersion = 2, SampleRate = 48000, ChannelConfiguration = 2, Channels = 2 },
            });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(streamEvent, StreamEventJsonContext.Default.StreamEvent));
        var root = document.RootElement;

        Assert.Equal("MediaDescribed", root.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("reason", out _));
        var video = root.GetProperty("media").GetProperty("video");
        Assert.Equal(["codec", "configurationVersion", "width", "height"], video.EnumerateObject().Select(static p => p.Name));
        var audio = root.GetProperty("media").GetProperty("audio");
        Assert.Equal(["codec", "configurationVersion", "sampleRate", "channelConfiguration", "channels"], audio.EnumerateObject().Select(static p => p.Name));
        Assert.Equal(48000, audio.GetProperty("sampleRate").GetInt32());
    }

    /// <summary>Verifies an event round-trips through the source-generated context unchanged.</summary>
    [Fact]
    public void Deserialize_RoundTrips()
    {
        var original = StreamEventFactory.Create(StreamEventType.PlaylistWithdrawn, "live", "camera1", Incarnation, DateTimeOffset.UnixEpoch, StreamEventReasons.Aborted);

        var json = JsonSerializer.Serialize(original, StreamEventJsonContext.Default.StreamEvent);
        var copy = JsonSerializer.Deserialize(json, StreamEventJsonContext.Default.StreamEvent);

        Assert.Equal(original, copy);
    }

    /// <summary>
    /// Verifies the version 1 field set exactly. A failure here means the contract changed: additions are allowed
    /// within version 1 (update this list), but a rename, removal or retyping needs a new schema version.
    /// </summary>
    [Fact]
    public void Envelope_HasExactlyTheVersion1Fields()
    {
        Assert.Equal(1, StreamEvent.CurrentSchemaVersion);
        Assert.Equal(
            [
                ("SchemaVersion", typeof(int)),
                ("Type", typeof(StreamEventType)),
                ("Timestamp", typeof(DateTimeOffset)),
                ("Application", typeof(string)),
                ("Stream", typeof(string)),
                ("IncarnationId", typeof(string)),
                ("Reason", typeof(string)),
                ("Media", typeof(StreamMediaDescription)),
            ],
            typeof(StreamEvent).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(static p => (p.Name, p.PropertyType)));
        Assert.Equal(
            ["Published", "MediaDescribed", "Ended", "PlaylistWithdrawn", "Drained"],
            Enum.GetNames<StreamEventType>());
    }

    /// <summary>Verifies no contract type has a member that could carry an internal identifier, an address or media bytes.</summary>
    [Fact]
    public void Contract_HasNoSensitiveOrPayloadMembers()
    {
        Type[] types = [typeof(StreamEvent), typeof(StreamMediaDescription), typeof(StreamVideoDescription), typeof(StreamAudioDescription)];
        foreach (var property in types.SelectMany(static t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
        {
            Assert.NotEqual(typeof(Guid), property.PropertyType);
            Assert.False(typeof(EndPoint).IsAssignableFrom(property.PropertyType), property.Name);
            Assert.NotEqual(typeof(byte[]), property.PropertyType);
            Assert.NotEqual(typeof(ReadOnlyMemory<byte>), property.PropertyType);
            Assert.DoesNotContain("Key", property.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Session", property.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Connection", property.Name, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Verifies an event built from a session carries the session's non-secret identity and incarnation and none of
    /// its session ID, connection ID, address or the stream key.
    /// </summary>
    [Fact]
    public void Create_FromSession_ExposesNoInternalIdentifiers()
    {
        const string streamKey = "secret-key-0001";
        var session = new PublisherSession(42, new IPEndPoint(IPAddress.Parse("192.0.2.7"), 50123), "live", "camera1", "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(StreamEventFactory.Create(StreamEventType.Published, session, DateTimeOffset.UnixEpoch), StreamEventJsonContext.Default.StreamEvent);

        Assert.Contains(session.IncarnationId, json, StringComparison.Ordinal);
        Assert.Contains("\"stream\":\"camera1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(session.SessionId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(session.SessionId.ToString("N"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.0.2.7", json, StringComparison.Ordinal);
        Assert.DoesNotContain("50123", json, StringComparison.Ordinal);
        Assert.DoesNotContain(streamKey, json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies incarnation identifiers: 32 lowercase hex digits, distinct per session even for the same application
    /// and stream, and unrelated to the session ID.
    /// </summary>
    [Fact]
    public void IncarnationId_IsRandomPerSession()
    {
        var sessions = Enumerable.Range(0, 1000)
            .Select(static _ => new PublisherSession(1, null, "live", "camera1", "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
            .ToList();

        Assert.All(sessions, static s => Assert.Matches("^[0-9a-f]{32}$", s.IncarnationId));
        Assert.Equal(sessions.Count, sessions.Select(static s => s.IncarnationId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(sessions, static s => Assert.NotEqual(s.SessionId.ToString("N"), s.IncarnationId));
        Assert.All(sessions, static s => Assert.Same(s.IncarnationId, s.IncarnationId));
    }

    /// <summary>Verifies every session end reason maps to a documented snake-case reason.</summary>
    [Fact]
    public void EndReason_MapsEveryPublishEndReason()
    {
        var reasons = Enum.GetValues<PublishEndReason>().Select(StreamEventFactory.EndReason).ToList();

        Assert.Equal(
            ["unpublished", "stream_deleted", "client_disconnected", "connection_timeout", "protocol_error", "connection_error", "server_shutdown", "handler_failed"],
            reasons);
    }
}
