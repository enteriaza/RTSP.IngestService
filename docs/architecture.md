# RTSP.IngestService architecture

RTSP.IngestService is a .NET 10 Generic Host worker that runs unchanged as a console application, a Windows service and a
systemd service. This document describes the hosting foundation (lifecycle, failure handling, configuration and
logging) and the RTMP ingest server. Deployment is covered in [deployment/windows.md](deployment/windows.md) and
[deployment/linux.md](deployment/linux.md).

## Components

| Area | Type | Responsibility |
|------|------|----------------|
| Entry point | `Program`, `Hosting/ServiceRunner` | Build, start, wait, stop, dispose; map the outcome to an exit code. |
| Host composition | `Hosting/IngestServiceHost` | Register options, host options, lifecycle, platform integration, logging and hosted services. |
| Platform | `Hosting/HostingModeDetector`, `PlatformHostingExtensions` | Detect Console, WindowsService or Systemd and register `AddWindowsService` / `AddSystemd`. |
| Windows | `Hosting/WindowsServiceControlLifetime` | Request SCM stop time and report the exit code to the SCM. |
| systemd | `Hosting/SystemdWatchdogService`, `SystemdWatchdogSettings` | Send `WATCHDOG=1` at half of `WATCHDOG_USEC`. |
| Lifecycle | `Lifecycle/ApplicationLifecycle`, `ApplicationLifecycleCoordinator` | Application state machine and readiness, driven by host events. |
| Configuration | `Configuration/*Options`, `IngestServiceOptionsExtensions` | Strict binding, source-generated validation, fail fast at startup. |
| Logging | `Logging/SerilogPipeline`, `SystemdConsoleFormatter` | Serilog sinks per hosting mode; bootstrap logger for pre-host failures. |
| Sample workload | `Services/HeartbeatService` | Periodic heartbeat; demonstrates options reload and `TimeProvider`. |
| RTMP ingest | `Rtmp/*` | RTMP publishing server: listener, connections, chunk protocol, AMF0, sessions (see [RTMP ingest](#rtmp-ingest)). |

## Application lifecycle

`IApplicationLifecycle` exposes `State`, `IsReady` (true only in `Running`), `StateChangedAt` and `Failure`. It is the
readiness mechanism: in-process components query it, and the platform reports it to the service manager (systemd
`READY=1`, SCM `RUNNING`). There is no HTTP endpoint.

```
NotStarted ──► Starting ──► Running ──► Stopping ──► Stopped
     │            │            │           │
     └────────────┴────────────┴───────────┴──────► Failed
NotStarted ──► Stopped   (shutdown requested before startup began)
Starting   ──► Stopping  (shutdown requested during startup)
```

`Stopped` and `Failed` are terminal. Transitions are serialised by a lock, invalid transitions are ignored and logged at
Debug, and `Failed` can only be entered through `TryFail(exception)`, which records the cause.

`ApplicationLifecycleCoordinator` is inserted as the first hosted service, so it starts before and stops after every
other service:

| Host event | Transition |
|------------|------------|
| `StartingAsync` (first service) | NotStarted → Starting |
| `ApplicationStarted` (all services started) | Starting → Running |
| `ApplicationStopping` / `StoppingAsync` | → Stopping |
| `StoppedAsync` (last service) | → Stopped, or → Failed if any `BackgroundService.ExecuteTask` faulted |

Background services still running when the shutdown timeout expires are logged as abandoned (event 1100).

## Startup and shutdown sequence

1. Create the bootstrap logger (stderr; `logs/bootstrap-*.log` for Windows services).
2. Build the host. Logging and host options are resolved here, so invalid `Lifetime` or `FileLogging` values fail the
   build; the error is logged through the bootstrap logger and the process exits with 78.
3. `StartAsync`: the platform lifetime waits for the service manager; `ValidateOnStart` validates every options type
   (exit 78 on failure); hosted services start sequentially in registration order. `ServiceRunner` enforces
   `Lifetime:StartupTimeout`.
4. All services started: `ApplicationStarted` fires, the state becomes `Running`, systemd receives `READY=1`.
5. Wait for a stop request: Ctrl+C, SIGTERM, SCM stop or shutdown, systemd stop, a faulted background service
   (`BackgroundServiceExceptionBehavior.StopHost`) or the caller's cancellation token.
6. `StopAsync`: `ApplicationStopping` fires, systemd receives `STOPPING=1`, services stop in reverse order within
   `Lifetime:ShutdownTimeout`.
7. Log the final state and exit code (event 4005), dispose the host and flush the log sinks.

Failure and cancellation handling:

| Situation | Result |
|-----------|--------|
| Stop requested during startup | Startup is cancelled, started services are stopped, state `Stopped`, exit 0 (event 4002). |
| A service throws from `StartAsync` | Already-started services are stopped, state `Failed`, exit 1 (event 4003). |
| Startup exceeds `StartupTimeout` | Treated as failure with `TimeoutException`, exit 1. |
| Invalid configuration | State `Failed`, exit 78. |
| Background service faults while running | Host stops, state `Failed`, exit 1. |
| Shutdown exceeds `ShutdownTimeout` | Remaining work is abandoned and logged; exit code reflects the lifecycle state. |
| Exception while stopping | Logged (event 4004); the exit code is unchanged because the service did stop. |

The runner owns the startup timeout (rather than `HostOptions.StartupTimeout`) because, when startup is cancelled, the
host also calls `StopApplication` for background services cancelled by that token. The runner records whichever cause
fires first so that a timeout is not misreported as a requested shutdown.

## Configuration

RTMP settings are listed under [RTMP ingest](#configuration-1).

| Section | Key | Default | Valid range |
|---------|-----|---------|-------------|
| `Application` | `Name` | Required (`RTSP.IngestService` in `appsettings.json`) | Letter followed by up to 63 of `A-Z a-z 0-9 . _ -` |
| `Lifetime` | `StartupTimeout` | `00:01:00` | 1 s – 1 h |
| `Lifetime` | `ShutdownTimeout` | `00:00:30` | 1 s – 10 min |
| `Heartbeat` | `Interval` | `00:01:00` | 1 s – 1 h (reloadable) |
| `FileLogging` | `Mode` | `Auto` | `Auto` (Windows service only), `Enabled`, `Disabled` |
| `FileLogging` | `Path` | `logs/rtsp-ingest-service-.log` | Relative to the executable directory, or absolute |
| `FileLogging` | `RetainedFileCountLimit` | `31` | 1 – 365 |
| `FileLogging` | `FileSizeLimitMegabytes` | `100` | 1 – 10240 |

Sources, in increasing precedence: `appsettings.json`, `appsettings.{Environment}.json`, environment variables,
command-line arguments. Binding is strict: unknown keys and unconvertible values are configuration errors. Validators are
source-generated (`[OptionsValidator]`), which keeps the Native AOT build free of trimming warnings. An invalid reload of
the heartbeat interval is logged and the previous interval is kept.

## Logging

| Mode | Sinks |
|------|-------|
| Console | Console, human-readable with timestamps. |
| Systemd | Standard output, one line per event with a `<priority>` prefix for journald. |
| WindowsService | Rolling JSON file (`FileLogging`). |

All events carry `Application`, `Environment`, `HostingMode`, `MachineName` and `ProcessId`. Log levels come from the
`Logging:LogLevel` configuration section. Configuration values are never logged wholesale; only timeouts and the
hosting environment are logged at startup.

| Event IDs | Source |
|-----------|--------|
| 1000–1002, 1100 | Application lifecycle |
| 2000–2003 | systemd watchdog |
| 3000–3003 | Heartbeat |
| 4000–4007 | Service runner |
| 5000–5006 | RTMP listener |
| 5100–5108 | RTMP connection |
| 5200–5212 | RTMP session and publishing |
| 6000–6010 | Media processing |
| 7000–7013 | HLS packaging |

## RTMP ingest

The service accepts RTMP publishers (OBS, FFmpeg, hardware encoders) on TCP port 1935 and turns each publish into a
`PublisherSession` whose audio, video and data messages are handed to an `IPublishSessionHandler`. Payloads are opaque
to this layer: there is no codec parsing, decoding, transcoding, packaging (MPEG-TS, fMP4, HLS) or playback. The host
registers [media processing](#media-processing) as the handler; `AddRtmpIngest()` alone falls back to a handler that
discards messages.

### Components

| Type | Responsibility |
|------|----------------|
| `RtmpListener` | `BackgroundService`. Binds in `StartAsync` (so readiness implies the port is open), runs the accept loop, enforces `MaxConnections`, cancels and awaits every connection on stop. |
| `RtmpConnection` | One TCP connection: handshake under `HandshakeTimeout`, receive loop, protocol control messages, acknowledgements, inactivity timeout, close classification and cleanup. Never throws. |
| `SocketTransport` | `IDuplexPipe` over the socket (`System.IO.Pipelines`). |
| `Protocol/RtmpHandshake` | Plain RTMP handshake (C0/C1/C2 ↔ S0/S1/S2), version 3 only, C2 echo verified. |
| `Protocol/ChunkDecoder`, `ChunkEncoder` | Chunk stream: basic headers of 1–3 bytes, header types 0–3, extended timestamps, chunk size changes, interleaving, Abort. |
| `Amf/Amf0Reader`, `Amf0Writer`, `AmfValue` | Bounded AMF0 codec for commands and metadata. |
| `RtmpSession` | Command workflow: `connect`, `createStream`, `releaseStream`/`FCPublish`/`FCUnpublish`, `publish`, `closeStream`, `deleteStream`; metadata capture; media delivery. |
| `Publishing/IPublisherAuthenticator` | Authorises each publish; `ConfiguredPublisherAuthenticator` is the default. |
| `Publishing/IPublishSessionHandler` | Receives session start, messages and end; `MediaPublishSessionHandler` in the host, `DiscardingPublishSessionHandler` as the RTMP-only fallback. |
| `Publishing/PublishedStreamRegistry` | Active sessions by application and stream identity; prevents two publishers of one stream. |
| `RtmpMetrics` | Meter `RTSP.IngestService.Rtmp`. |

All services are registered by `AddRtmpIngest()` with `TryAdd`, so the authenticator and handler can be replaced by
registering an implementation before it.

### Connection lifecycle

```
Accepted ──► Handshaking ──► Connected ◄──► Publishing ──► Closing ──► Closed | Failed
```

`Closed` follows a client disconnect, a rejection (bad `connect`, unauthorised publish, command before `connect`) or
server shutdown; `Failed` follows a handshake failure or timeout, idle timeout, protocol error, transport error or
handler failure. Every path ends the connection's sessions (with a `PublishEndReason`), removes them from the registry,
returns pooled buffers and completes the transport.

Publishing flow: `connect` (app validated, query stripped) → Window Acknowledgement Size, Set Peer Bandwidth, Set Chunk
Size (`OutboundChunkSize`) and `_result NetConnection.Connect.Success` → `createStream` → `publish` → authenticate →
registry → `OnSessionStartedAsync` → StreamBegin and `onStatus NetStream.Publish.Start`. Rejections answer with
`NetConnection.Connect.Rejected`, `NetStream.Publish.BadName`, `NetStream.Publish.Unauthorized` (connection closed) or
`NetStream.Publish.Failed`.

### Authentication

`ConfiguredPublisherAuthenticator` accepts a publish when the application equals `Rtmp:Authentication:Application`
(ordinal) and the stream name (the part before any `?`) equals a configured `StreamKey`. Keys are compared as SHA-256
digests with `CryptographicOperations.FixedTimeEquals` across every entry. The session's stream identity is the
publisher's `Name`, so the key itself is never used as an identifier, logged or exported. With no publishers configured
every publish is rejected. Stream names, query strings and `tcUrl` are never logged; `PublishAuthenticationRequest`
redacts them in `ToString`. Real keys belong in the git-ignored `appsettings.{Environment}.json`, environment variables
(`Rtmp__Authentication__Publishers__0__StreamKey`) or a secret store, never in `appsettings.json`.

### Memory and backpressure

- The receive loop awaits each message's handler before decoding the next. A slow handler therefore stops the
  connection reading, the socket receive buffer fills and TCP flow control slows the publisher. Nothing is queued
  per connection beyond the pipe's bounded buffer (64 KiB pause threshold).
- Chunk payloads are copied into pooled buffers (`ArrayPool<byte>.Shared`) as they arrive, so at most an 18-byte
  partial header stays in the pipe whatever chunk size the client chooses.
- A message longer than `MaxMessageSize` is rejected from its header, before any allocation. Partial messages across
  all chunk streams may hold at most `MaxBufferedBytes`; memory is reserved as data arrives (doubling from 4 KiB), not
  from declared lengths. At most 64 chunk streams and 8 message streams per connection.
- AMF0 decoding limits nesting to 16 levels, collections to 1024 entries and a payload to 4096 values.
- `PublishedMessage` owns its pooled payload; the handler must dispose it. If the handler throws, the connection
  disposes it.
- Worst-case memory is roughly `MaxConnections × (MaxBufferedBytes + MaxMessageSize + 128 KiB of pipe buffers)`; size
  the limits for the host.

### Timeouts

`HandshakeTimeout` bounds the whole handshake. `ConnectionTimeout` is an inactivity timeout armed only while the
connection waits for data or for its own output to flush; time spent in the handler does not count. Both run on
`TimeProvider`.

### Configuration

| Key (`Rtmp:`) | Default | Valid range |
|---------------|---------|-------------|
| `ListenAddress` | `0.0.0.0` | IPv4 or IPv6 literal; `::` listens dual-stack |
| `Port` | `1935` | 0 – 65535 (0 = OS-assigned, for tests) |
| `MaxConnections` | `100` | 1 – 10000 |
| `HandshakeTimeout` | `00:00:10` | 1 s – 5 min |
| `ConnectionTimeout` | `00:00:30` | 1 s – 1 h |
| `MaxMessageSize` | `8388608` | 128 – 16777215 bytes |
| `InboundChunkSize` | `128` | 128 – 65536 (the size assumed before the client's Set Chunk Size) |
| `OutboundChunkSize` | `4096` | 128 – 65536 |
| `MaxBufferedBytes` | `16777216` | 65536 – 268435456, and at least `MaxMessageSize` |
| `Authentication:Application` | `live` | Letter or digit followed by up to 63 of `A-Z a-z 0-9 . _ -` |
| `Authentication:Publishers:N:Name` | — | Same pattern as `Application`; unique |
| `Authentication:Publishers:N:StreamKey` | — | 16 – 256 characters; unique |

Values are validated at startup and never clamped; an invalid value stops the service with exit code 78. RTMP options
are read once at startup.

### Metrics

Meter `RTSP.IngestService.Rtmp`. Tags take only the fixed values listed; stream names, keys, addresses and
connection IDs are never tags.

| Instrument | Type | Tags |
|------------|------|------|
| `rtmp.connections.accepted` | Counter | — |
| `rtmp.connections.rejected` | Counter | `reason`: `connection_limit` |
| `rtmp.connections.active` | UpDownCounter | — |
| `rtmp.connection.duration` | Histogram (s) | `close_reason`: `client_disconnected`, `rejected`, `server_shutdown`, `handshake_timeout`, `handshake_failed`, `idle_timeout`, `protocol_error`, `transport_error`, `handler_failed`, `internal_error` |
| `rtmp.handshakes` | Counter | `result`: `success`, `failure` |
| `rtmp.authentication.failures` | Counter | — |
| `rtmp.protocol_errors` | Counter | — |
| `rtmp.sessions.active` | UpDownCounter | — |
| `rtmp.publisher.disconnects` | Counter | `reason`: `unpublished`, `stream_deleted`, `client_disconnected`, `connection_timeout`, `protocol_error`, `connection_error`, `server_shutdown`, `handler_failed` |
| `rtmp.bytes_received` | Counter (bytes) | — |
| `rtmp.messages_received` | Counter | `type`: `audio`, `video`, `data`, `command`, `control`, `other` |

No exporter or HTTP endpoint is configured; use `dotnet-counters monitor --counters RTSP.IngestService.Rtmp -n
RTSP.IngestService` or add an exporter in a later phase.

### Supported and unsupported

Supported: plain RTMP over TCP, IPv4 and IPv6; AMF0 commands and data; AMF3 command/data messages (types 17 and 15)
whose body is AMF0 after the format byte; Set Chunk Size, Abort, Acknowledgement, Window Acknowledgement Size, Set
Peer Bandwidth, User Control (ping requests answered); `@setDataFrame`/`onMetaData` capture; several publishing streams
per connection.

Not supported: RTMPE, RTMPS (TLS), RTMPT (HTTP tunnelling), the digest (FP9) handshake, AMF3-encoded values, playback
(`play`), shared objects and aggregate messages (ignored), and anything after ingest (decoding, packaging, HTTP
delivery, recording).

### Publishing from OBS and FFmpeg

Configure a publisher in `appsettings.Development.json` (git-ignored; start from
`appsettings.Development.example.json`) or with environment variables:

```json
"Rtmp": {
  "Authentication": {
    "Application": "live",
    "Publishers": [ { "Name": "studio", "StreamKey": "<at least 16 random characters>" } ]
  }
}
```

OBS: Settings → Stream → Service *Custom…*, Server `rtmp://<host>:1935/live`, Stream Key `<StreamKey>`.

FFmpeg (synthetic test stream; no media files needed):

```
ffmpeg -re -f lavfi -i testsrc=size=1280x720:rate=30 -f lavfi -i sine=frequency=440 \
  -c:v libx264 -preset veryfast -tune zerolatency -g 60 -c:a aac -f flv rtmp://127.0.0.1:1935/live/<StreamKey>
```

A successful publish logs event 5204 (`Publishing started … stream studio`) and, when the client stops, event 5207 with
the message counts. A wrong key logs 5205 and the client reports `NetStream.Publish.Unauthorized`.

The automated interoperability tests in `FfmpegInteropTests` (an FLV1/ADPCM publish, and an H.264/AAC publish checked
through media processing) run when FFmpeg is on the `PATH` or `RTSP_FFMPEG_PATH` points to it, and are skipped
otherwise. FFmpeg is used only by tests and to generate the checked-in fixture, never by the service.

## Media processing

`MediaPublishSessionHandler` is the host's `IPublishSessionHandler`. It turns each session's RTMP audio and video
messages into validated, typed encoded samples and hands them to an `IMediaSessionHandler`. Bitstreams are delivered
byte-for-byte: no decoding, transcoding, re-encoding, Annex B or ADTS conversion. The default `IMediaSessionHandler`
(`DiscardingMediaSessionHandler`) disposes every sample; a later phase registers its own with `TryAdd` semantics.

### Flow

```
RtmpSession ──► MediaPublishSessionHandler ──► IMediaSessionHandler
                 per session: MediaTimeline, MediaStreamInfo, counters
```

An RTMP audio/video message body is an FLV tag body (no tag header; the timestamp comes from the RTMP header).

| Packet | Handling |
|--------|----------|
| Video, codec ID 7 (AVC), packet type 0 | `AVCDecoderConfigurationRecord` parsed into `AvcDecoderConfiguration` (profile, level, NAL length size 1/2/4, SPS/PPS). An identical repeat is ignored; a change creates a new `VideoTrackInfo` version. |
| Video, AVC, packet type 1 | `EncodedVideoSample`: frame type (keyframe), signed 24-bit composition time, NAL units split on the configured length size with every length checked. |
| Video, AVC, packet type 2; frame type 5 | End of sequence (logged at Debug) and info/command frames; ignored. |
| Audio, sound format 10 (AAC), packet type 0 | `AudioSpecificConfig` parsed into `AacAudioConfiguration` (object type incl. escape and explicit SBR/PS, sampling frequency incl. explicit 24-bit, channel configuration, channel count when derivable). |
| Audio, AAC, packet type 1 | `EncodedAudioSample`: the raw access unit, unchanged. |
| Data (`onMetaData`) | `PublisherMetadata`: untrusted, range-checked values (width, height, frame rate, codec IDs, sample rate, channels, duration, data rates, encoder). Width, height and frame rate are copied into `VideoTrackInfo` as declared values; codec properties always come from the configuration records. |
| Any other codec, enhanced RTMP (FourCC) | `UnsupportedCodec` error. |

Each change of configuration or metadata publishes a new immutable `MediaStreamInfo` (with an incremented `Version`)
through `OnStreamInfoChangedAsync` before any sample that depends on it. Samples carry the `VideoTrackInfo` or
`AudioTrackInfo` they were coded with.

### Ownership

A sample takes ownership of the `PublishedMessage` that carried it, so the pooled payload is not copied: `Data` and
`GetNalUnitData` are slices of it. The `IMediaSessionHandler` must dispose each sample exactly once; afterwards the
accessors throw `ObjectDisposedException`, and memory obtained earlier must not be read. If the handler throws, the
processor disposes the sample. Messages that do not become samples (configurations, metadata, ignored and dropped
packets) are disposed before `OnMessageAsync` returns. Configuration records are copied (at most 64 KiB for AVC, 1 KiB
for AAC) because they outlive their message.

### Timestamps

`MediaTimestamps` keeps `SourceTimestamp` (the 32-bit RTMP value as received), `DecodeTimestamp` and
`CompositionTimeOffset`; `PresentationTimestamp` is DTS + CTO (CTO is 0 for audio). All values are milliseconds.

- One timeline per session, shared by audio and video. The first audio or video message sets the origin at its own
  RTMP timestamp; timestamps are never rebased, smoothed or rewritten.
- Each message advances the timeline by the signed 32-bit difference from the previous message, so 2^32 rollover
  continues forward. A step of more than 2^31 ms in one message cannot be told apart from rollover.
- Per track, each delivered sample has a `DecodeTimestampDelta` and a `Discontinuity`: `Regression` if negative,
  `Jump` if greater than `Media:DiscontinuityThreshold`, otherwise `None`. Flagged samples are still delivered.
- Samples also carry a per-track `SequenceNumber` (0-based, delivered samples only).

### Errors

A packet that cannot be delivered is dropped, disposed, counted and reported through `OnMediaErrorAsync` as a
`MediaProcessingError` (kind, track, RTMP timestamp, detail without payload bytes or publisher text). The session
continues. Kinds: `UnsupportedCodec`, `MalformedPacket` (bad FLV header, zero/overrunning/truncated NAL lengths,
forbidden bit, empty access unit), `InvalidConfiguration` (also clears that track's configuration), `MissingConfiguration`
(sample before a valid sequence header), `SampleTooLarge` and `TooManyNalUnits`. Only an exception from the
`IMediaSessionHandler` ends the session (`HandlerFailed`, connection closed). State is per session and released when the
session ends or its start fails, so one publisher's media cannot affect another's.

### Backpressure

Every downstream call is awaited before the next message is processed; there are no queues. A slow
`IMediaSessionHandler` therefore applies the same TCP backpressure as described under
[Memory and backpressure](#memory-and-backpressure).

### Configuration

| Key (`Media:`) | Default | Valid range |
|----------------|---------|-------------|
| `MaxSampleSize` | `8388608` | 1024 – 16777215 bytes (after the FLV packet header) |
| `MaxNalUnitsPerSample` | `256` | 1 – 4096 |
| `DiscontinuityThreshold` | `00:00:10` | 1 s – 1 h |

Validated at startup and never clamped; an invalid value exits with code 78.

### Metrics

Meter `RTSP.IngestService.Media`. Tags take only the fixed values listed.

| Instrument | Type | Tags |
|------------|------|------|
| `media.samples` | Counter | `track`: `video`, `audio` |
| `media.keyframes` | Counter | — |
| `media.sample_bytes` | Counter (bytes) | `track` |
| `media.configurations` | Counter | `track` |
| `media.errors` | Counter | `track`; `kind`: `unsupported_codec`, `malformed_packet`, `invalid_configuration`, `missing_configuration`, `sample_too_large`, `too_many_nal_units` |
| `media.timestamp_discontinuities` | Counter | `track`; `kind`: `regression`, `jump` |
| `media.sessions.active` | UpDownCounter | — |

### Events

| ID | Level | Event |
|----|-------|-------|
| 6000 | Debug | Media processing started for a session |
| 6001 | Information | Video configuration accepted (codec string, NAL length size, SPS/PPS counts, version) |
| 6002 | Information | Audio configuration accepted (codec string, sample rate, channel configuration, version) |
| 6003 | Warning | First unsupported-codec packet of a track in a session |
| 6004 | Warning | First dropped packet of each kind and track in a session |
| 6005 | Debug | Further dropped packets |
| 6006 | Warning | First timestamp discontinuity of a track in a session |
| 6007 | Debug | Further discontinuities |
| 6008 | Information | Session summary on end (samples, keyframes, configurations, errors, discontinuities) |
| 6009 | Debug | Publisher metadata changed (declared values) |
| 6010 | Debug | AVC end of sequence |

Events identify sessions by `SessionId` only; stream keys, stream names and publisher-supplied strings are not logged.

### Test fixture

`tests/RTSP.IngestService.Tests/Media/Fixtures/h264-aac.flv` (14 KB, 1 s, 160×120 at 10 fps, H.264 Main with B-frames,
AAC-LC stereo 44.1 kHz) is checked in, so the tests never need FFmpeg. It was generated with:

```
ffmpeg -f lavfi -i testsrc=size=160x120:rate=10 -f lavfi -i sine=frequency=440:sample_rate=44100 -t 1 \
  -c:v libx264 -profile:v main -bf 2 -g 10 -pix_fmt yuv420p -c:a aac -ac 2 -b:a 64k -f flv h264-aac.flv
```

## HLS packaging

`Hls/` packages the typed H.264/AAC samples of media processing into fragmented MP4 (ISO BMFF, CMAF-style) and live
HLS media playlists (RFC 8216, version 7) held in a bounded in-memory store. Nothing is decoded or re-encoded; sample
bytes are copied verbatim into `mdat`. There is no HTTP delivery yet: the store is the boundary a later phase serves.

### Components

| Type | Role |
|------|------|
| `HlsPackagingHandler` | The host's `IMediaSessionHandler`: one `HlsPackager` per media session, opened in the store at session start |
| `HlsPackager` | Per-stream state machine: periods, segment boundaries, timestamp mapping, ownership of pending samples |
| `Mp4Segments`, `Mp4Writer` | Exact-size initialization (`ftyp`+`moov`) and media (`moof`+`mdat`) segment writers with checked sizes |
| `AvcSequenceParameterSet` | Minimal SPS reader for the cropped coded dimensions (VUI is not read) |
| `HlsPlaylist` | Sliding-window playlist state and deterministic rendering; no I/O, no clock |
| `IHlsSegmentStore`, `InMemoryHlsSegmentStore` | Storage boundary and its bounded in-memory implementation; owns each stream's playlist |

Calls are sequential and awaited per session (no queues or workers); sessions share only the store, which is guarded
by one lock with short, bounded operations.

### MP4 structure

- Initialization: `ftyp` (`iso6`; `iso6`, `iso5`, `mp41`), `moov` with `mvhd` (timescale 1000), one `trak` per
  packaged track (`tkhd`, `mdia`/`mdhd`/`hdlr`, `minf` with `vmhd`/`smhd`, `dinf`, `stbl` with one sample entry and
  empty `stts`/`stsc`/`stsz`/`stco`) and `mvex` with one `trex` per track.
- Video is track 1, timescale 90 kHz, sample entry `avc1` with the AVCDecoderConfigurationRecord verbatim as `avcC`;
  dimensions come from the first SPS, not from metadata. Samples keep their AVCC length-prefixed NAL units.
- Audio is track 2, timescale = AAC core sample rate, sample entry `mp4a` with `esds` (object type 0x40) carrying the
  AudioSpecificConfig verbatim. AAC Main, LC and LTP (with SBR/PS signalling), frame length 1024 or 960, 1–8 channels
  are packaged; other configurations are rejected (event 7003) and that track's samples are dropped.
- Media segment: one `moof` (`mfhd`, then a `traf` per track with `tfhd` default-base-is-moof, `tfdt` version 1 and
  `trun`) followed by one `mdat`. Video `trun` is version 1 with per-sample duration, size, flags and signed
  composition offset; audio `trun` has duration and size. Sync samples are those containing an IDR NAL unit; a
  disagreeing FLV keyframe flag is logged once (7009).

### Timing

Every track maps the shared Phase 3 decode timeline onto its timescale from one base (the first period's first
sample), from absolute timestamps, so rounding never accumulates. Video ticks are milliseconds × 90 exactly. Audio
sample starts continue the previous sample by exactly one frame when the timestamp agrees within a quarter frame. A
later timestamp starts the frame there (a source gap, kept: the previous sample's duration spans it, as `trun`
requires). An earlier one would overlap the previous frame's decoded samples, so that frame is dropped
(`timeline_overlap`), which also keeps audio within a quarter frame of its timestamps (no drift). A video sample lasts
until the next video sample; when its segment closes before that sample is known (stream end, a cut made by audio),
it lasts the last step observed between two samples of one segment. No timestamp or duration is ever altered to fit
the playlist. Composition offsets are written unchanged. RTMP rollover is continuous (handled by Phase 3's unwrapping).

Each segment starts where the previous one ended: at the video sample that closed it, at the audio frame that closed
it, or at the end of its media (stream end). `#EXTINF` is the distance between consecutive boundaries, each boundary
converted from 90 kHz on its own, so the playlist durations add up exactly to the media timeline with no drift. A
segment's audio may start or end up to one AAC frame (plus RTMP interleaving) either side of its video boundary;
that is codec frame granularity, and the audio track itself stays contiguous across segments.

A period is a run of segments sharing one initialization segment and timeline. A new period, whose first segment
carries `#EXT-X-DISCONTINUITY`, starts on a codec configuration change, a track appearing, a Phase 3 regression or
jump, or dropped media. The initialization segment is reused when the tracks are unchanged; otherwise `init-N.mp4`
follows. With video configured, a period starts at an IDR and earlier audio is dropped.

### Segment boundaries

- At the first sync sample once the segment spans `TargetSegmentDuration` (audio-only: at the target).
- Forced at `MaxSegmentDuration` without a sync sample (7008, once); the next segment then does not start with an IDR.
- Forced before a video sample whose observed step would carry the segment past TARGETDURATION + 0.499 s, and on an
  audio frame that would (video stalled while audio continues).
- A pause no segment can span (more than TARGETDURATION + 0.499 s past the segment start), or video that would
  overlap written video, ends the period (`timeline_gap`, `timeline_overlap`); the next IDR starts a new one.
- Forced when the next sample would exceed `MaxSegmentSize`; a single sample larger than that is dropped and starts a
  new period.
- On a new period, and at a normal session end (the final, possibly short, segment).

### Playlist

`#EXTM3U`, `#EXT-X-VERSION:7`, `#EXT-X-TARGETDURATION`, `#EXT-X-MEDIA-SEQUENCE`,
`#EXT-X-DISCONTINUITY-SEQUENCE` once a discontinuity has rolled out, `#EXT-X-MAP` before the first segment and on each
initialization change, and `#EXTINF` as integer milliseconds rendered `s.mmm` (rounded half up, culture-independent).
Only completed, stored segments are listed. The oldest segment is retired while more than `PlaylistWindowSize` remain
and the rest still span three target durations (RFC 8216, 6.2.2). A live playlist is capped at 1000 entries; at the
cap a segment is accepted only if removing the oldest still leaves three target durations, otherwise the store
returns `PlaylistFull` with nothing changed. The packager then logs 7013 and stops, and its next sample throws
`HlsPackagingStoppedException`, ending the publish (`HandlerFailed`); the playlist is withdrawn and its segments
drain normally. This needs 1000 segments averaging under 3 × TD / 1000 (for example `MaxSegmentSize` far below the
bitrate). An ended playlist is never shortened and accepts nothing more, so the live minimum does not apply to it.

TARGETDURATION is `MaxSegmentDuration` rounded up and is fixed for the playlist's lifetime (RFC 8216, 6.2.1). Every
`#EXTINF` rounded to the nearest integer is at most that value (4.3.3.1): the playlist rejects any entry rendering
above TD + 0.499 s, and the packager's boundary rules (see [Segment boundaries](#segment-boundaries)) never produce
one.

`#EXT-X-ENDLIST` is added only when the publisher ends normally (unpublish, delete stream or disconnect); the ended
playlist stays readable for `EndedStreamRetention`. Any other end (timeout, protocol error, shutdown, handler
failure), or retention 0, discards pending samples and withdraws the playlist immediately; its segments still drain
as below. A new stream on the same key continues the key's media sequence and initialization numbering, even after
the previous stream has fully drained (the store keeps two counters per key for the process lifetime), so a URI never
names two different segments within a process. Playlists reference segments as `{generation}/{name}`, with a random
generation per store instance, so URIs from different processes never collide (see
[HTTP HLS origin](#http-hls-origin)). Each published snapshot has a store-wide increasing `Version`.

### Storage

Segments are immutable `byte[]` owned by `HlsSegment`, safe for any number of concurrent readers. Appending a segment
and publishing the playlist that lists it is atomic; a rejected publication changes nothing.

Availability (RFC 8216, 6.2.2): a listed segment is never released. A segment removed from the playlist, by
rollover or by withdrawal of the whole playlist, stays readable until
`removed at + its duration + the longest published playlist that contained it`; the store records that longest
duration on every publish. Unreferenced initialization segments are released once no listed or retained segment uses
them. Expiry is evaluated lazily on each call; there is no background worker. Eviction reasons: `retired`,
`withdrawn`, `unreferenced`.

Storage pressure never releases anything early and never shrinks a playlist:

- Stream limit (`MaxSessionStorageMegabytes`) reached while the stream has retained segments: the segment is rejected
  with `StorageFull` (transient; space returns as they expire).
- Stream limit reached with nothing retained, so space can never return: `StreamLimitExceeded`. The packager logs
  7012, stops packaging, and its next sample throws `HlsPackagingStoppedException`; Phase 3 ends the publish with
  `HandlerFailed` and the playlist is withdrawn and drains. Size the session limit for the listed window plus the
  retired segments' retention (about twice the window).
- Global limit (`MaxTotalStorageMegabytes`): ended streams are withdrawn early (their segments drain normally) and,
  if still short, the segment is rejected with `StorageFull`. Other live streams are never touched.

After a `StorageFull` the packager drops the pending segment's samples, ends the period, and resumes at the next sample
that can start one (an IDR when video is packaged) with `#EXT-X-DISCONTINUITY` and no sequence gap; the published
playlist is unchanged meanwhile. If another session is packaging the key, or `MaxSessions` is reached, the publish
is rejected.

### Configuration

`Hls` section (strict binding, validated at start; invalid values exit with code 78):

| Setting | Default | Range |
|---------|---------|-------|
| `TargetSegmentDuration` | 4 s | 1 s–1 min |
| `MaxSegmentDuration` | 10 s | 1 s–2 min, ≥ target |
| `PlaylistWindowSize` | 6 | 3–100 |
| `MaxSessionStorageMegabytes` | 128 | 1–4096, ≥ `MaxSegmentSize` |
| `MaxTotalStorageMegabytes` | 2048 | 1–65536, ≥ session storage |
| `MaxSessions` | 100 | 1–10000 |
| `MaxInitializationSegmentSize` | 128 KiB | 4 KiB–1 MiB |
| `MaxSegmentSize` | 32 MiB | 64 KiB–256 MiB |
| `EndedStreamRetention` | 30 s | 0–10 min |

### Metrics

Meter `RTSP.IngestService.Hls`: `hls.sessions.active`, `hls.initialization_segments`, `hls.segments` (`boundary`),
`hls.segment_bytes`, `hls.segment.duration`, `hls.keyframe_wait`, `hls.storage.bytes`, `hls.storage.evictions`
(`reason`), `hls.errors` (`kind`), `hls.discontinuities` (`reason`), `hls.samples.dropped` (`track`, `reason`). Tags
are fixed enumerations; no stream or session identifiers.

### Events

| ID | Level | Event |
|----|-------|-------|
| 7000 | Debug | HLS packaging started for a session |
| 7001 | Information | Initialization segment published (dimensions, sample rate, channels, size) |
| 7002 | Debug | Media segment published (sequence, duration, size, boundary) |
| 7003 | Warning | A track configuration cannot be packaged |
| 7004 | Warning | First segment problem of a kind (too large, storage full) |
| 7005 | Debug | Further segment problems |
| 7006 | Information | First new period of a reason (discontinuity) |
| 7007 | Debug | Further new periods |
| 7008 | Warning | First forced boundary of a cause (duration or size) |
| 7009 | Warning | Keyframe flag disagrees with the IDR NAL unit |
| 7010 | Warning | Packaging refused (stream limit or busy stream); the publish is rejected |
| 7011 | Information | Session summary on end (completed, segments, bytes, samples not packaged) |
| 7012 | Error | Stream storage exhausted with nothing releasable; packaging stops and the publish ends |
| 7013 | Error | Live playlist at the 1000-entry cap cannot shrink below 3 × TD; packaging stops and the publish ends |

### Tests and fixture

`Media/Fixtures/h264-aac-4s.flv` (53 KB, 4 s, IDR every second; otherwise as above, generated with
`-t 4 -g 10 -keyint_min 10 -sc_threshold 0` plus `-fflags +bitexact`) drives the segmentation and integration tests.
`HlsFfmpegInteropTests` reconstruct the presentation from the store into a temporary directory and validate it with
ffprobe (streams, frame counts, duration) and a full `ffmpeg -xerror` decode, for the fixture and for a live FFmpeg
publish (B-frames, AAC 48 kHz). They also compare timing exactly: the packaged MP4 timeline is contiguous on both
tracks, `#EXTINF` adds up to it, ffprobe's packet timestamps and durations equal the packaged ones, decoded frame
times equal the packaged presentation times, every AAC frame decodes to its full 1024 samples, and the decoded A/V
offset equals the packaged one. They are skipped when FFmpeg or ffprobe is not found. `HlsTimingTests` cover AAC at
44.1/48 kHz with 1024/960-sample frames, early and late AAC frames, B-frames with AAC over ten segments without
drift, a video stall under continuous audio, and the entry cap through the packager.

### Limitations

- A pause in all tracks longer than TARGETDURATION + 0.499 s past a segment start, or overlapping video timestamps,
  becomes a discontinuity; video then resumes at the next IDR (earlier frames are dropped).
- A video sample that ends a segment before its successor is known (stream end, video stall) lasts the last observed
  frame step; under variable frame rate the real gap to the next frame can differ by the jitter.
- AAC frames timestamped more than a quarter frame early are dropped rather than overlapped.
- `StorageFull` drops media (a discontinuity); a session limit too small for the window, or a live playlist at the
  1000-entry cap that cannot shrink, ends the publish.
- SPS VUI (sample aspect ratio, colour) is not parsed; no `pasp`/`colr` boxes are written.
- Segments are single contiguous arrays, so large segments are large-object-heap allocations.
- A track that appears later, and audio before the first video IDR, cause a new period or are dropped.

## HTTP HLS origin

`Http/` serves the HLS store over plain HTTP/1.1. `HlsHttpOrigin` is a hosted service in the existing Generic Host
that owns a `KestrelServer` (shared framework `Microsoft.AspNetCore.App`; no web application builder, no second
host, no middleware or routing framework). `HlsHttpApplication` is its `IHttpApplication<HttpContext>`.

### Lifecycle

- Registered before RTMP ingest: it starts first and stops last, so viewers keep reading while publishers are
  disconnected at shutdown.
- `StartAsync` binds; a bind failure throws `InvalidOperationException`, startup fails with exit code 1, and
  readiness (`Running`) is never reported. Event 8000 logs the bound endpoint before readiness.
- `StopAsync` stops accepting connections, lets in-flight responses finish until the host shutdown token
  (`Lifetime:ShutdownTimeout`) fires, then aborts the rest. Windows Service, systemd and console modes are unchanged.

### URLs and responses

`/hls/{application}/{stream}/index.m3u8`, `.../{generation}/init-{n}.mp4` and `.../{generation}/segment-{n}.m4s`.
`{stream}` is the publisher's configured non-secret `Name` (the stream identity), never the stream key or a session
GUID; a request using a key as the path is just an unknown stream. Path segments must match the validated name
pattern; anything else is 404.

Generation: segment numbering restarts with the process, so names alone would collide across restarts. The store
draws a random 128-bit generation (32 lowercase hex digits; not derived from any key or name) when it is created,
once per process, and renders every playlist URI as `{generation}/{name}` (`#EXT-X-MAP` included). The playlist URL
stays stable and always lists the current process's generation; numbering still continues per key within a process,
so a generation-qualified URL names exactly one piece of media ever. A segment request whose generation is not the
store's (an earlier process) is 404 `segment_unavailable`, even if a segment of the same name exists now; an
unqualified segment path is 404 `invalid_path`. Nothing is persisted.

| Resource | Content-Type | Cache-Control | ETag |
|----------|--------------|---------------|------|
| Playlist | `application/vnd.apple.mpegurl` | `no-cache` | process epoch + snapshot `Version` |
| Initialization | `video/mp4` | `public, max-age=L` | process epoch + name |
| Segment | `video/iso.segment` | `public, max-age=L` | process epoch + name |
| 404 / 405 / 500 | none | `no-store` | none |

- Playlist text is served byte-for-byte as published. `no-cache` lets caches store it but forces revalidation, so a
  refresh is never stale; an unchanged playlist costs a 304.
- `L` = `EndedStreamRetention` + `MaxSegmentDuration` × (2 × max(`PlaylistWindowSize`, 4) + 1), an upper bound on how
  long the origin serves a segment (listed time plus its RFC 8216 availability period). Defaults: 160 s. A segment
  URL never names different media, so a fresh cached copy is always correct; URL identity does not depend on ETags.
- `If-None-Match` (weak comparison, `*` supported) returns 304, evaluated only after the resource is found, so a
  withdrawn or expired resource is 404, never 304. The random per-process epoch prevents a match across restarts.
- Ranges are not supported: `Accept-Ranges: none`, and `Range` is ignored (full 200). HLS segments are fetched whole
  and are bounded by `MaxSegmentSize`; byte-range playlists are not produced.
- `GET` and `HEAD` only (HEAD sends the headers and `Content-Length`); other methods on a valid URL are 405 with
  `Allow: GET, HEAD`. `X-Content-Type-Options: nosniff`; no `Server` header; request bodies are refused.
- Missing resources are 404: invalid path, unknown stream, withdrawn playlist, or a segment that is not (or no longer)
  stored. Unexpected failures are 500 if the response has not started, otherwise the connection is aborted.

Lifecycle as seen over HTTP: an active stream serves a live playlist; an ended stream serves the `#EXT-X-ENDLIST`
playlist for `EndedStreamRetention`; an aborted or withdrawn stream's playlist is 404 at once while its segments are
served until their availability deadlines. A replacement publish continues the numbering, so old URLs keep naming
the old media until they expire.

### Ownership and backpressure

The store lock is held only while looking up a snapshot or segment, never during network I/O. Responses write
directly from the segment's immutable array (no copy; the playlist text is UTF-8 encoded per request) in awaited
64 KiB chunks, so Kestrel's output backpressure applies and a slow viewer only delays its own response; the publisher
and other viewers never wait on it. Eviction only drops the store's reference, so expiry during a response cannot
break it. Client disconnect and shutdown end the write: either the awaited write observes `RequestAborted`, or
Kestrel reports the output as completed (it discards writes after an abort and signals `RequestAborted` later), and
the request is counted as cancelled. Nothing per viewer is retained.
Kestrel's minimum response data rate (240 B/s after a 5 s grace) disconnects viewers that stop reading.

### Configuration

`Http` section (strict binding, validated at start; invalid values exit with code 78). Plain HTTP only.

| Setting | Default | Range |
|---------|---------|-------|
| `ListenAddress` | `127.0.0.1` | IP address (use `0.0.0.0` or `::` to expose) |
| `Port` | 8080 | 0–65535 (0 = ephemeral) |
| `MaxConnections` | 1000 | 1–100000 |
| `RequestHeadersTimeout` | 30 s | 1 s–5 min |
| `KeepAliveTimeout` | 2 min | 1 s–1 h |

### Metrics

Meter `RTSP.IngestService.Http`: `http.origin.requests` (`resource`, `status_class`), `http.origin.requests.active`,
`http.origin.request.duration` (`resource`), `http.origin.response_bytes` (`resource`), `http.origin.cancellations`
(`resource`), `http.origin.missing` (`resource`, `reason`: `invalid_path`, `unknown_stream`, `playlist_withdrawn`,
`segment_unavailable`), `http.origin.failures` (`resource`). `resource` is `playlist`, `initialization`, `segment` or
`other`. No application, stream, segment name or client address is ever a tag.

### Events

| ID | Level | Event |
|----|-------|-------|
| 8000 | Information | HTTP origin listening (bound endpoint) |
| 8001 | Error | Unexpected request failure (resource kind only) |
| 8002 | Information | HTTP origin stopped |

### Tests

`HlsHttpOriginTests` run a real Kestrel listener on an ephemeral loopback port over an `InMemoryHlsSegmentStore` on a
`FakeTimeProvider`: headers, bytes, HEAD, ranges, conditional requests, 404 reasons, 405, the ended and aborted
lifecycles to the exact deadline, replacement without URL reuse, a simulated restart (two store and origin lifetimes
on the same port with the same names and numbers, where cached first-lifetime URLs are 404 on the second and never
return its media), foreign, malformed and unqualified generations, concurrent viewers while publishing and expiring,
expiry during a 24 MiB response, disconnected and stalled viewers (raw sockets with a 4 KiB receive window so the
responses stay in progress), low-cardinality tags, bind failure and graceful drain on stop. `HlsHttpPlaybackTests` run the real service host: binding before readiness, HTTP port conflict
(exit 1), and real-time FFmpeg publishes over RTMP played over HTTP with ffprobe and `ffmpeg -xerror` (live playlist
refresh while publishing, ENDLIST, streams, frame counts, duration equal to the `#EXTINF` total), with A/V and
audio only.

### Limitations

- No HTTPS, authentication or CORS headers; expose it through a reverse proxy for TLS or cross-origin browser players.
- After a restart, pre-restart segment URLs are 404 (their media is gone); players must reload the playlist, which
  then lists the new generation. Cached pre-restart segments are only ever served for their own URLs.
- No byte ranges, no master playlist, HTTP/1.1 only.
