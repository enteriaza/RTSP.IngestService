# RTSP.IngestService architecture

RTSP.IngestService is a .NET 10 Generic Host worker that runs unchanged as a console application, a Windows service and a
systemd service. This document describes the hosting foundation (lifecycle, failure handling, configuration and
logging), the RTMP ingest server, media processing, HLS packaging and the HTTP HLS origin. Deployment is covered in [deployment/windows.md](deployment/windows.md) and
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
| Logging | `Logging/SerilogPipeline`, `SystemdConsoleFormatter`, `BoundedAsyncSink`, `BufferedConsoleSink` | Serilog sinks per hosting mode, written asynchronously through a bounded queue; bootstrap logger for pre-host failures. |
| RTMP ingest | `Rtmp/*` | RTMP publishing server: listener, connections, chunk protocol, AMF0, sessions (see [RTMP ingest](#rtmp-ingest)). |
| Media processing | `Media/*` | H.264/AAC parsing into typed samples and stream descriptions (see [Media processing](#media-processing)). |
| HLS packaging | `Hls/*` | fMP4 segments and live playlists in a bounded in-memory store (see [HLS packaging](#hls-packaging)). |
| HTTP HLS origin | `Http/*` | Kestrel-based HTTP/1.1 server for playlists and segments (see [HTTP HLS origin](#http-hls-origin)). |
| Management | `Management/*` | Loopback-only liveness and readiness routes on the same HTTP listener (see [Management endpoints](#management-endpoints)). |
| Stream events | `Streams/*` | Versioned, best-effort stream lifecycle notifications over one bounded in-process channel (see [Stream lifecycle events](#stream-lifecycle-events)). |
| Stream catalog | `Streams/StreamCatalog*` | Reconciled, read-only in-memory inventory of stream incarnations, served at `/health/streams` (see [Stream catalog](#stream-catalog)). |
| Telemetry export | `Telemetry/*` | Optional OpenTelemetry OTLP export of the existing metrics, disabled by default (see [Telemetry export](#telemetry-export)). |

## Application lifecycle

`IApplicationLifecycle` exposes `State`, `IsReady` (true only in `Running`), `StateChangedAt` and `Failure`. It is the
readiness mechanism: in-process components query it, and the platform reports it to the service manager (systemd
`READY=1`, SCM `RUNNING`). Loopback clients can also query it over HTTP (see [Management endpoints](#management-endpoints)).

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
| `FileLogging` | `Mode` | `Auto` | `Auto` (Windows service only), `Enabled`, `Disabled` |
| `FileLogging` | `Path` | `logs/rtsp-ingest-service-.log` | Relative to the executable directory, or absolute |
| `FileLogging` | `RetainedFileCountLimit` | `31` | 1 – 365 |
| `FileLogging` | `FileSizeLimitMegabytes` | `100` | 1 – 10240 |
| `Management` | `Enabled` | `true` | `true` or `false` |
| `StreamEvents` | `QueueCapacity` | `1024` | 16 – 65536 |
| `StreamCatalog` | `ReconciliationInterval` | `00:00:30` | 1 s – 1 h |
| `Telemetry:Otlp` | `Enabled` | `false` | `true` or `false` |
| `Telemetry:Otlp` | `Endpoint` | Unset | Required when enabled; absolute `http`/`https` URI without user information, query or fragment |
| `Telemetry:Otlp` | `Protocol` | `grpc` | `grpc` or `http/protobuf` |
| `Telemetry:Otlp` | `ExportInterval` | `00:00:15` | 1 s – 5 min |

Sources, in increasing precedence: `appsettings.json`, `appsettings.{Environment}.json`, environment variables,
command-line arguments. Binding is strict: unknown keys and unconvertible values are configuration errors. Validators are
source-generated (`[OptionsValidator]`), which keeps the Native AOT build free of trimming warnings.

## Logging

| Mode | Sinks |
|------|-------|
| Console | Console, human-readable with timestamps. |
| Systemd | Standard output, one line per event with a `<priority>` prefix for journald. |
| WindowsService | Rolling JSON file (`FileLogging`). |

All events carry `Application`, `Environment`, `HostingMode`, `MachineName` and `ProcessId`. Log levels come from the
`Logging:LogLevel` configuration section. Configuration values are never logged wholesale; only timeouts and the
hosting environment are logged at startup.

Log calls do not write to the sinks themselves. `BoundedAsyncSink` queues each enriched event (at most 10,000) and
one dedicated thread, `Log writer`, writes them to the sinks in order, in batches of at most 1,000. When the queue
is full, the log call waits for space: no event is ever dropped, and a sustained excess slows the callers to the
sinks' rate, as synchronous logging would. Redirected console output (always under systemd) is formatted into a buffer
and written once per batch by `BufferedConsoleSink`, with the same output template or formatter; an interactive
console keeps the themed console sink, and the file sink is unchanged. On shutdown the logger is disposed after the
host: it stops accepting events, writes the queued ones (waiting at most 10 s, for example if standard output is
blocked), then flushes the sinks. Events still queued when the process terminates abnormally are lost. The bootstrap
logger writes synchronously.

| Event IDs | Source |
|-----------|--------|
| 1000–1002, 1100 | Application lifecycle |
| 2000–2003 | systemd watchdog |
| 4000–4007 | Service runner |
| 5000–5006 | RTMP listener |
| 5100–5108 | RTMP connection |
| 5200–5212 | RTMP session and publishing |
| 6000–6010 | Media processing |
| 7000–7013 | HLS packaging |
| 8000–8002 | HTTP HLS origin |
| 9000 | Management endpoints |
| 9100–9102 | Stream event dispatcher |
| 9200–9202 | Stream catalog |
| 9300–9302 | Telemetry export |

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

`ConfiguredPublisherAuthenticator` verifies the stream name (the part before any `?`; the OBS "Stream Key") against
one-way hashes of configured credentials. The service never stores, compares or logs a plaintext key, and there is no
plaintext fallback.

**Credentials.** Each entry of `Rtmp:Authentication:Publishers` has:

| Setting | Meaning |
|---------|---------|
| `Name` | Unique, non-secret identifier; also the stream identity used by sessions, the registry, HLS URLs, events and the catalog |
| `Application` | The only application the credential may publish to (ordinal). Required; there is no default application |
| `KeyHash` | `hmac-sha256$<salt>$<digest>`: unpadded base64url of a 16-byte random salt and the HMAC-SHA256 of the ASCII key keyed by that salt |
| `Enabled` | `true` (default) or `false` to revoke while keeping the entry |

HMAC-SHA256 is a fast one-way function. That is appropriate because supported keys are generated with 256 bits of
entropy, which makes offline guessing infeasible even if a hash leaks. The per-credential salt makes equal keys hash
differently. A slow password hash would add CPU cost to every attempt for every credential without strengthening
such keys. Keys chosen by people do not have that entropy and should be replaced with generated ones. Hashes are still
sensitive: keep them in the git-ignored `appsettings.{Environment}.json` files or environment variables, never in
`appsettings.json` or source control.

**Provisioning.** The service binary provisions credentials without starting the host:

```
RTSP.IngestService publisher-key generate        # prints "StreamKey: <key>" once and "KeyHash: <hash>"
RTSP.IngestService publisher-key hash < key.txt  # reads one existing key from stdin; prints only "KeyHash: <hash>"
```

Give the stream key to the encoder operator and store only the `KeyHash`. The service keeps no copy of the key, so a
lost key is replaced, not recovered. Keys must be 16 to 256 printable ASCII characters without spaces or `?`; generated
keys are 43 characters. Exit code 64 reports a usage error. Example configuration (placeholders only):

```json
"Rtmp": {
  "Authentication": {
    "Publishers": [
      { "Name": "camera1", "Application": "live", "KeyHash": "<KeyHash from publisher-key>", "Enabled": true },
      { "Name": "studio", "Application": "studio", "KeyHash": "<KeyHash from publisher-key>" }
    ]
  }
}
```

**Verification.** Steps for one `publish` command:

1. The rate limiter reserves a failure token for the TCP peer address. If none is left, the attempt is rejected
   before any hashing (`rate_limited`). The credential snapshot is read after the reservation.
2. A key that is not 16 to 256 printable ASCII characters is rejected without hashing (`malformed_key`).
3. If the snapshot's verified-key memo records that this key already authenticated, in this snapshot, as an enabled
   credential of the requested application, the key is checked against that credential's hash alone and succeeds on
   a `FixedTimeEquals` match.
4. Otherwise the key is checked against every credential in the snapshot, enabled or not, with
   `CryptographicOperations.FixedTimeEquals`, so timing does not reveal which credential matched. The attempt is
   rejected if it matches no credential (`invalid_credential`), more than one (`ambiguous`), a disabled one
   (`disabled`), or one whose `Application` differs from the application the publisher connected to
   (`application_mismatch`). A success is memoized.
5. On success the token is returned. The session is created with the credential's `Name` and `Application`, so the
   registry, HLS store, events and catalog use the authenticated application. A publisher cannot choose a different
   one.

**Verified-key memo.** Each credential snapshot keeps an in-memory map from a tag of a presented key to the
credential it authenticated as. The tag is the HMAC-SHA256 of the key under a random 32-byte pepper that is created
with the snapshot and exists only in memory. The memo stores no key and no configured hash, is never persisted or
logged, and holds at most one entry per credential, because only unambiguous successes are recorded. A reload creates
a new snapshot with an empty memo, so a revoked, disabled, rotated or reassigned credential is never served from it.
A repeated publish with a known key costs two HMACs instead of one per configured credential. Every rejection still
performs the full check, so only an attempt that succeeds, which the publisher learns anyway, can be faster. The
first attempt with each key after startup or a reload costs the full check.

Every rejection keeps the existing behaviour:
- `NetStream.Publish.Unauthorized`, then the connection closes;
- log 5205 with a fixed reason that names no key, hash or credential;
- `rtmp.authentication.failures` is incremented.

No session, registry entry, HLS stream or event is created. An authenticator exception still answers
`NetStream.Publish.Failed`.

**Rotation and revocation.** Credentials are reloaded when the configuration changes. `appsettings*.json` files are
watched by default; environment variables and command-line values are not, so they change only on restart.

| Action | How |
|--------|-----|
| Add | Append an entry |
| Remove | Delete the entry |
| Revoke | Set `Enabled` to `false` (or remove the entry) |
| Rotate | Run `publisher-key generate`, replace the entry's `KeyHash`, then reconfigure the encoder |
| Reassign | Change `Application` |

Every reload re-runs the startup binding and validation for the whole `Rtmp` section:
- A valid reload atomically replaces the snapshot. It logs 5301 and counts `rtmp.authentication.reloads{result=success}`.
- An invalid reload keeps the last known-good credentials. Examples are a malformed or duplicate hash, a duplicate
  name, a missing application, a former `StreamKey` setting, or any other invalid `Rtmp` value. It logs 5302 with the
  validation messages, which name settings but never contain values, and counts `result=failure`.

Readiness and existing sessions are unaffected by any reload. Each attempt reads the snapshot once, so it sees one
consistent set of credentials.

**Revocation and rotation affect only new publish attempts.** A session that is already publishing continues until
the publisher disconnects or the service stops; there is no session termination.

Only `Publishers` is reloaded; the other `Rtmp` settings, including `RateLimit`, are read at startup. Invalid
credentials at startup stop the service with exit code 78.

**Rate limiting.** Failed attempts are limited per TCP peer IP address, taken from the accepted socket and never from
publisher-supplied fields such as `tcUrl`, the application or the query. The limiter is a token bucket:
- Each address may fail `RateLimit:FailureLimit` times in a burst (default 10, range 1 – 1000).
- The allowance refills evenly over `RateLimit:Window` (default 1 minute, range 1 s – 1 h).
- A token is reserved before verification and returned on success, so concurrent attempts cannot exceed the
  allowance and successful publishers are never charged.
- An attempt that finds every remaining token reserved by attempts still in flight from the same address waits for
  them instead of being rejected. It waits in a FIFO queue of at most 1,024 attempts per address, for at most 2 s,
  and can be cancelled when the connection closes. A token returned by a success passes directly to the first waiter.
  Once no attempt is in flight and no token is left, the waiters are rejected (`rate_limited`). The number of
  attempts verifying at once per address therefore never exceeds the free tokens, and a burst of valid publishers
  behind one address (for example a NAT or a restarting encoder farm) is not rejected merely for being concurrent.
- Once an address is limited, even a correct key from it is rejected until tokens refill. This prevents guessing
  through the limit.

Memory and scope:
- At most 10,000 addresses are tracked. A full bucket is equivalent to an untracked address and is removed on
  release, by a sweep at most once per window, and when the table is full.
- If the table is full of addresses that have not refilled, a new address is rejected (fail closed) until space is
  reclaimed.
- IPv4-mapped IPv6 addresses count as IPv4. Each IPv6 address is limited separately.
- The limiter runs once per `publish` command under a short lock, never on the media path, and does not affect
  established sessions.

**Migration from plaintext keys.** The `StreamKey` property and the global `Authentication:Application` setting no
longer exist, so strict binding rejects them and the service exits with code 78 instead of accepting plaintext. To
migrate:

1. For each publisher, pipe the existing key into `publisher-key hash`.
2. Replace `StreamKey` with `KeyHash` and add `Application` (formerly the global value, usually `live`).
3. Delete the plaintext from configuration.

Encoders keep their keys. Plan to rotate migrated keys to generated keys.

Stream names, query strings and `tcUrl` are never logged; `PublishAuthenticationRequest` redacts them in `ToString`,
and `PublisherKeyHash.ToString` never formats a salt or digest.

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
- The listen backlog (connections the operating system has completed but the accept loop has not yet taken) is
  `max(128, MaxConnections)`, so a reconnect burst of up to `MaxConnections` encoders queues instead of being refused.
  On Windows it is requested with `SOMAXCONN_HINT`, because a plain backlog is capped at 200. The operating system
  may cap it lower; on Linux the cap is `net.core.somaxconn` (4096 by default since kernel 5.4).

### Timeouts

`HandshakeTimeout` bounds the whole handshake. `ConnectionTimeout` is an inactivity timeout armed only while the
connection waits for data or for its own output to flush; time spent in the handler does not count. Both run on
`TimeProvider` through one `ConnectionDeadline` per connection: arming and disarming record a timestamp, and a single
timer, rescheduled only when it fires or when an earlier deadline is armed (about once per timeout period rather than
four times per read), cancels the connection when an armed deadline passes. Server shutdown takes precedence over a
timeout that expires at the same time.

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
| `Authentication:Publishers:N:Name` | — | Letter or digit followed by up to 63 of `A-Z a-z 0-9 . _ -`; unique |
| `Authentication:Publishers:N:Application` | — (required) | Same pattern as `Name` |
| `Authentication:Publishers:N:KeyHash` | — (required) | `hmac-sha256$<22 chars>$<43 chars>`; unique |
| `Authentication:Publishers:N:Enabled` | `true` | `true` or `false` |
| `Authentication:RateLimit:FailureLimit` | `10` | 1 – 1000 |
| `Authentication:RateLimit:Window` | `00:01:00` | 1 s – 1 h |

At most 1000 publishers. Values are validated at startup and never clamped; an invalid value stops the service with
exit code 78. RTMP options are read once at startup, except `Authentication:Publishers`, which is reloaded (see
[Authentication](#authentication)).

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
| `rtmp.authentication.attempts` | Counter | `result`: `success`, `invalid_credential`, `disabled`, `ambiguous`, `application_mismatch`, `malformed_key`, `rate_limited` |
| `rtmp.authentication.reloads` | Counter | `result`: `success`, `failure` |
| `rtmp.protocol_errors` | Counter | — |
| `rtmp.sessions.active` | UpDownCounter | — |
| `rtmp.publisher.disconnects` | Counter | `reason`: `unpublished`, `stream_deleted`, `client_disconnected`, `connection_timeout`, `protocol_error`, `connection_error`, `server_shutdown`, `handler_failed` |
| `rtmp.bytes_received` | Counter (bytes) | — |
| `rtmp.messages_received` | Counter | `type`: `audio`, `video`, `data`, `command`, `control`, `other` |

All application meters can be exported over OTLP (see [Telemetry export](#telemetry-export), disabled by default),
or observed locally with `dotnet-counters monitor --counters RTSP.IngestService.Rtmp -n RTSP.IngestService`.

### Supported and unsupported

Supported: plain RTMP over TCP, IPv4 and IPv6; AMF0 commands and data; AMF3 command/data messages (types 17 and 15)
whose body is AMF0 after the format byte; Set Chunk Size, Abort, Acknowledgement, Window Acknowledgement Size, Set
Peer Bandwidth, User Control (ping requests answered); `@setDataFrame`/`onMetaData` capture; several publishing streams
per connection.

Not supported: RTMPE, RTMPS (TLS), RTMPT (HTTP tunnelling), the digest (FP9) handshake, AMF3-encoded values, playback
(`play`), shared objects and aggregate messages (ignored), and recording. Packaging and HTTP delivery are separate layers
([HLS packaging](#hls-packaging), [HTTP HLS origin](#http-hls-origin)); nothing is decoded or transcoded.

### Publishing from OBS and FFmpeg

Configure a publisher in `appsettings.Development.json` (git-ignored; start from
`appsettings.Development.example.json`) or with environment variables:

```json
"Rtmp": {
  "Authentication": {
    "Publishers": [ { "Name": "studio", "Application": "live", "KeyHash": "<KeyHash from publisher-key generate>" } ]
  }
}
```

OBS: Settings → Stream → Service *Custom…*, Server `rtmp://<host>:1935/live`, Stream Key `<StreamKey>` (the key
printed once by `publisher-key generate`).

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
bytes are copied verbatim into `mdat`. The store is served by the [HTTP HLS origin](#http-hls-origin).

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
by one lock with short, bounded operations. Writes touch only their own stream plus the streams with a due deadline
(an expiry index); viewer reads use immutable per-stream views republished under the lock and take the lock only when
a deadline is due.

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

Segments are immutable and owned by `HlsSegment`. Media segments are written into 64 KiB chunks from a bounded
process-wide pool (`HlsSegmentBufferPool`, keeping at most one eighth of `MaxTotalStorageMegabytes` as free chunks);
initialization segments are small exact arrays. A segment is reference-counted: the store holds one reference while it
is stored and every reader holds one through an `HlsSegmentLease` (`AcquireSegment`), so the chunks return to the pool
exactly once, after the store has released the segment and the last response using it has completed, failed or been
cancelled. Any number of readers may use a segment concurrently, before or after it is evicted. A segment the store
does not publish (rejected or failed) stays with the packager, which releases it. Appending a segment and publishing
the playlist that lists it is atomic; a rejected publication changes nothing. The storage limits and
`hls.storage.bytes` count each stored segment's footprint (whole chunks for a media segment, at most 64 KiB more than
its length), so they bound the memory stored segments hold however small the segments are.

Reader memory: the HTTP origin leases segments through `TryAcquireSegment`, against a reader budget equal to
`MaxTotalStorageMegabytes`. A segment's footprint is reserved by its first lease and returned by its last, so viewers
sharing a segment cost it once and a segment already being sent is always served. A segment that no response is
sending and whose footprint does not fit is refused (HTTP 503 with `Retry-After: 1`, counted by
`hls.readers.refused`) until responses end. Segment memory is therefore bounded by twice `MaxTotalStorageMegabytes`
(stored segments plus evicted ones still being sent), plus the pool's free chunks (one eighth) and the samples held
for segments being built (at most `MaxSegmentSize` per session, `hls.pending.bytes`). Ingest is never refused
because of readers.

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
(`reason`), `hls.errors` (`kind`), `hls.discontinuities` (`reason`), `hls.samples.dropped` (`track`, `reason`),
`hls.pending.bytes`, `hls.readers.bytes`, `hls.readers.refused`, `hls.segment_pool.bytes` (`state`: `in_use`,
`free`). Tags are fixed enumerations; no stream or session identifiers.

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
- Evicted segments still being sent to slow viewers stay allocated until those responses end; they count against the
  reader budget, so very slow viewers can make other viewers' requests for new segments 503 until they finish or
  Kestrel's minimum response data rate disconnects them.
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
| 404 / 405 / 500 / 503 | none | `no-store` | none |

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
- A stored initialization or media segment is 503 with `Retry-After: 1` while the reader memory budget is exhausted
  and no other response is sending it (see [Storage](#storage)).

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

## Management endpoints

`Management/ManagementEndpoints` adds liveness and readiness routes to the HTTP origin's request pipeline. There is
one `KestrelServer` and one listener in the application: management shares the `Http` listen address, port,
connection limit and timeouts, and has no address, port or limit of its own. `HlsHttpApplication` hands every path
under `/health/` to the management endpoints before HLS routing; HLS requests and the `http.origin.*` metrics are
unaffected. `Management:Enabled=false` stops serving these routes (they become unknown HLS paths, 404) without
affecting HLS, the lifecycle or the service-manager integration.

| Endpoint | 200 when | Otherwise |
|----------|----------|-----------|
| `GET`/`HEAD /health/live` | The lifecycle is not `Failed` or `Stopped` | 503 |
| `GET`/`HEAD /health/ready` | The lifecycle is `Running`, the RTMP listener is bound and the HTTP listener is bound | 503 |
| `GET`/`HEAD /health/streams` | The stream catalog has completed its first reconciliation | 503 `{"status":"initializing"}` |
| `GET`/`HEAD /health/streams/{application}/{stream}` | As above, and the stream is in the catalog (its current incarnation is returned) | 503 as above; 404 (empty) if unknown |

Bodies are small source-generated JSON documents, for example
`{"status":"not_ready","state":"Starting","rtmpListener":false,"httpListener":true}` or
`{"status":"live","state":"Running"}`. Every management response has `Cache-Control: no-store` and
`X-Content-Type-Options: nosniff`; `HEAD` returns the `GET` status and headers without a body; other methods are 405
(`Allow: GET, HEAD`); unknown management paths are 404.

Listener state is explicit: `RtmpListener.IsListening` is set after a successful bind and cleared when shutdown
starts closing the socket; `HlsHttpOrigin.IsListening` is set after Kestrel has bound and cleared when `StopAsync`
begins. The lifecycle enters `Stopping` on `ApplicationStopping`, before any hosted service stops, so readiness is
503 while both listeners are still bound; the origin starts before and stops after RTMP ingest, so the endpoints stay
reachable until the HTTP listener itself stops. A failure while answering a management request is logged (event
9000) and answered 500; it never stops the listener or touches the media pipeline.

### Access control

The endpoints are unauthenticated and restricted to loopback clients by the connection's transport-level remote
address: IPv4 `127.0.0.0/8`, IPv6 `::1` and IPv4-mapped loopback are accepted; any other address, or a connection
without a remote address, gets an empty 403 for every `/health/` path and method, so a rejected client cannot tell
which endpoints exist. `X-Forwarded-For`, `Forwarded` and similar headers are never read and there is no
forwarded-headers processing, so a client cannot claim to be local. The `Http` listener binds to `127.0.0.1` by
default. When viewers are served through a reverse proxy on the same host, the proxy's connections are loopback
connections: **the proxy must not forward `/health/` paths** (expose only `/hls/`), or remote clients would reach the
endpoints through it.

### Tests

`ManagementEndpointsTests` run the endpoints in a real Kestrel origin with a controllable lifecycle: liveness for
every state, readiness for every state and listener combination, the origin's real bound state, headers and bodies,
HEAD, 405 and 404, HLS and management answering on one connection, management disabled, IPv4, IPv6 and IPv4-mapped
loopback clients, a client on a non-loopback address of the machine (403 on every path with and without spoofed
forwarding headers), unknown and non-loopback remote addresses on every path and method, and a failing probe.
`ManagementHostTests` run the real host: a single origin and listening event, readiness held at 503 by startup gates
before and after the RTMP listener binds and by a shutdown gate before the listeners close, management configuration
errors (exit 78), and FFmpeg HLS playback with management disabled.

## Stream lifecycle events

**Events are notifications, not authoritative state.** They report that an authoritative transition happened in the
RTMP, media or HLS layer. Delivery is best-effort and in-process: an event can be overwritten when the channel is
full, discarded at shutdown, or skipped by a failing consumer, and nothing is persisted, retried or replayed. A
consumer that needs current state must reconcile against the authoritative source; it must never derive state from
events alone.

### Architecture and ownership

```
RtmpSession ──────────────┐  Published, Ended
MediaPublishSessionHandler ┼─► IStreamEventPublisher ─► StreamEventChannel ─► StreamEventDispatcher ─► IStreamEventConsumer
InMemoryHlsSegmentStore ───┘  MediaDescribed,           (bounded, drop-oldest,   (hosted service,        (optional, at most one)
                              PlaylistWithdrawn, Drained  single owner)            own task)
```

Producers depend only on `IStreamEventPublisher.Publish`, which calls `TryWrite` and returns; it never waits for
capacity or for the consumer, takes no lock of its own (the channel's internal lock is held only for the enqueue),
and never throws for a full or closed channel. Events are small immutable records; they never hold media payloads or
pooled buffers. `StreamEventChannel` is the single owner of the `Channel<StreamEvent>`: it creates it, counts drops,
and completes it (`Complete`, also on disposal). `StreamEventDispatcher` is the only reader and runs the consumer on
its own task, so consumer code never runs on a producer's path and cannot slow RTMP, media, HLS or viewers. There is
one channel for all streams (no per-stream queue), no bus and no multi-subscriber fan-out. The RTMP-to-media-to-HLS
pipeline is unchanged and remains synchronous; publishing an event is an additional non-blocking call at a
transition, and a publishing failure cannot affect the lifecycle.

### Envelope (schema version 1)

```json
{
  "schemaVersion": 1,
  "type": "MediaDescribed",
  "timestamp": "2026-11-24T10:30:00.1234567+00:00",
  "application": "live",
  "stream": "camera1",
  "incarnationId": "3f2a9c1e0b7d4e8f9a6b5c4d3e2f1a0b",
  "reason": "client_disconnected",
  "media": {
    "video": { "codec": "avc1.64001F", "configurationVersion": 1, "width": 1280, "height": 720, "frameRate": 30 },
    "audio": { "codec": "mp4a.40.2", "configurationVersion": 1, "sampleRate": 48000, "channelConfiguration": 2, "channels": 2 }
  }
}
```

| Field | Presence | Meaning |
|-------|----------|---------|
| `schemaVersion` | Always | `1`. |
| `type` | Always | `Published`, `MediaDescribed`, `Ended`, `PlaylistWithdrawn` or `Drained`. |
| `timestamp` | Always | When the transition happened, UTC (`+00:00`). |
| `application` | Always | RTMP application. |
| `stream` | Always | Non-secret stream identity from authentication; never the stream key. |
| `incarnationId` | Always | Public identifier of the publication (below). |
| `reason` | `Ended`, `PlaylistWithdrawn` | Snake-case reason; absent where the transition has none. |
| `media` | `MediaDescribed` | Configured tracks; codec strings, sample rate and channel configuration come from the decoder configuration, width, height and frame rate from publisher metadata (unverified). |

Absent optional fields are omitted. Serialization uses the source-generated `StreamEventJsonContext` (Native AOT
safe); the contract has no broker-specific dependency. Events never contain credentials, stream keys, authentication
data, the internal session GUID, connection IDs or client addresses.

Versioning: within schema version 1, changes are additive only. Fields, enumeration values and reason values may be
added; no field is renamed, removed, retyped or given a new meaning, and a removed field's name is never reused.
Consumers must ignore unknown fields, types and reasons. Any other change requires a new schema version.

### Event types

| Type | Emitted | Reason |
|------|---------|--------|
| `Published` | By `RtmpSession` after the publish is authenticated, registered as the key's only publisher and accepted by the media and HLS stages (`OnSessionStartedAsync` succeeded), with `NetStream.Publish.Start`. Rejected publishes (unauthorised, duplicate, stage failure) emit nothing. | None |
| `MediaDescribed` | By the media stage after the HLS stage accepted a description whose track configuration changed and that has at least one track: first when the first track is configured, again when a track is added, replaced by a different sequence header or cleared by an invalid one. Repeated identical headers and metadata-only changes emit nothing. | None |
| `Ended` | By `RtmpSession` when the session-end transition (`TryEnd`) succeeds, before the stages are notified; exactly once per publication on every path (closeStream, deleteStream, disconnect, timeout, protocol or connection error, stage failure, shutdown). | `unpublished`, `stream_deleted`, `client_disconnected`, `connection_timeout`, `protocol_error`, `connection_error`, `server_shutdown`, `handler_failed` |
| `PlaylistWithdrawn` | By the HLS store when the publication's playlist stops being served. | `aborted` (abnormal end), `retention_disabled`, `empty` (no segment), `retention_expired`, `replaced` (a new publication of the stream), `storage_pressure` |
| `Drained` | By the HLS store when the last segment of a withdrawn publication is released; nothing of it remains. Immediately after `PlaylistWithdrawn` if it had no segments. | None |

For one publication the order is `Published`, any `MediaDescribed`, `Ended`, `PlaylistWithdrawn`, `Drained`.
Events of one publication are published from the threads that perform its transitions in that order, so the channel
preserves it; events of different publications may interleave. The store raises its events under its lock and
publishes them after releasing it, in the same call. Like the rest of the store, retention expiry and segment
release are evaluated lazily on store calls (writes and viewer requests), so `PlaylistWithdrawn` (`retention_expired`)
and `Drained` are observed at the next store call after the deadline, and their timestamps are that evaluation time.
A publication whose process exits before draining never emits `Drained`. Compositions without the HLS stage emit only
`Published`, `MediaDescribed` and `Ended`.

### Incarnation identity

`PublisherSession.IncarnationId` is 128 random bits from `RandomNumberGenerator`, as 32 lowercase hexadecimal digits,
generated when the session is created. It is the same on every event of the publication, new for every publication
(including republishing the same application and stream on the same connection), and independent of the internal
session GUID, the application and the stream name. The HLS store keeps it with the stream instance so withdrawal and
drain events carry the publication's identifier even after a new publication of the same stream has started.

### Channel, drops and metrics

`StreamEventChannel` is a `Channel.CreateBounded` of `StreamEvents:QueueCapacity` events (default 1024) with
`FullMode = DropOldest`, a single reader and many writers. When it is full, a write succeeds and the oldest
undelivered event is discarded. Because `TryWrite` still returns true, drops cannot be detected from its result; they
are counted in the channel's item-dropped callback, which runs once for each overwritten event.

| Instrument (meter `RTSP.IngestService.Streams`) | Meaning |
|------------------------------------------------|---------|
| `stream_events.published` | Events accepted into the channel, including any later overwritten. |
| `stream_events.dropped` | Accepted events overwritten undelivered because the channel was full. |
| `stream_events.rejected` | Events offered after the channel closed at shutdown. |
| `stream_events.consumer_failures` | Events whose consumer threw. |

The only tag is `type` (`published`, `media_described`, `ended`, `playlist_withdrawn`, `drained`); streams,
applications and identifiers are never tags. `DroppedCount`, `RejectedCount` and `PublishedCount` on the channel are
monotonic interlocked counters with the same meaning. The drop count does not include events discarded undelivered
at shutdown, nor events a consumer received but failed to handle. Without a consumer nothing reads the channel, so
after `QueueCapacity` events every further event overwrites one and is counted as dropped; at any time
`published = delivered + buffered + dropped + discarded at shutdown`.

### Consumer lifecycle and shutdown

`IStreamEventConsumer.ConsumeAsync(event, token)` is called for one event at a time, in channel order. An exception is
logged (event 9100), counted, and the next event is delivered; it never stops the dispatcher, ingest or the host.
`StreamEventDispatcher` is registered before the HTTP origin and the RTMP listener, so the channel exists and delivery
has started before any publish is accepted, and it stops after both, still accepting the `Ended` and withdrawal events
their shutdown produces. Its `StopAsync` completes the channel (later events are rejected), cancels the consumer's
token and waits at most five seconds (`StreamEventDispatcher.StopTimeout`) or until the host's shutdown token fires;
a consumer that ignores cancellation is abandoned (event 9102). When the loop is not inside the consumer at that
point, it runs no consumer code once cancelled, so `StopAsync` waits for it to exit even if the host's token has
already fired. Undelivered events are discarded (event 9101 reports
how many). Readiness does not depend on the dispatcher or consumer. Only one consumer is supported; registering none
is valid. The service host registers the [stream catalog](#stream-catalog) as the consumer unless the composition
registers another one.

### Tests

`StreamEventContractTests` cover the exact version 1 JSON, field set, optional fields, round-trip through the
source-generated context, the absence of session, connection, address and key data, incarnation format and
uniqueness, and the end-reason mapping. `StreamEventChannelTests` cover buffering without a consumer, capacity,
drop-oldest order, exact drop accounting with concurrent producers, rejection after completion, in-order delivery
off the producer thread, a stalled consumer not blocking producers, consumer failure isolation, cancellation, the stop
budget on a fake clock, the host shutdown token and the capacity option range. `StreamLifecycleEventTests` cover
every RTMP termination path, rejected publishes, republishing, `MediaDescribed` deduplication, every withdrawal reason,
lazy expiry and drain, and publication outside the store lock. `StreamEventHostTests` run the real host with TCP
publishes: composition order, the full lifecycle delivered to a failing consumer, and a stalled consumer leaving
readiness, publishing and shutdown unaffected.

## Stream catalog

`Streams/StreamCatalog` is an in-memory, read-only operational view of the stream incarnations that are publishing or
still hold HLS resources. **Its authority is the RTMP registry and the HLS store**, read by reconciliation; lifecycle
events only accelerate updates of incarnations a reconciliation already established and request reconciliation for
anything else. Nothing is persisted, there is no second registry, and the media pipeline is untouched: the catalog
reads immutable copies and never holds media buffers.

### Sources and consistency model

`RuntimeStreamInventorySource` acquires, in this order and each as an immutable copy:

1. The HLS store's instances via `IHlsStreamInventory.GetStreams` (application, stream, incarnation, generation,
   `Live`/`Ended`/`Withdrawn`, and `PlaylistAvailable`), copied under the store lock after lazy expiry is evaluated.
   `PlaylistAvailable` is true exactly when `GetPlaylist`, which the playlist route serves, returns that instance's
   playlist: the instance is the key's current one and has published a playlist snapshot.
2. The registry's sessions (`PublishedStreamRegistry.Sessions`, an array copy), and for each the media stage's
   current immutable `MediaStreamInfo` (a volatile read), described exactly as `MediaDescribed` would describe it.

There is no global atomic snapshot. The order makes the gap interpretable because the authoritative operations are
ordered: a publication is registered before its HLS stream opens, and unregistered before it closes. So an HLS
instance that is live while its session is not registered has ended (the RTMP side is reported `ended`; the HLS close
that follows has no event, so the incarnation is *unsettled* and the service re-reconciles after
`StreamCatalogService.SettleDelay`, one second, until it is not). A registered session that is absent from the HLS copy
has either not opened yet (`pending`) or opened and been withdrawn and drained (`withdrawn`). The HLS packaging stage
sets `PublisherSession.HlsStreamOpened` once the store accepts the stream; it never reverts and lives exactly as long
as that incarnation's session, so it needs no cleanup, cannot leak to a later publication of the name, and is visible
to a newly created catalog. The source reads the flags *before* the HLS copy, so a stream that opens during acquisition
is reported `pending` (and `live` at the next reconciliation), never as drained; `withdrawn` therefore never depends on
having received `Drained`. An opened HLS stream publishes its first playlist only when its first segment closes, and
that has no event either, so a `live` incarnation without an available playlist is also *unsettled*: the settle timer
re-reconciles every second until the store reports the playlist (or the stream ends). Any remaining staleness is
bounded by the next event-triggered or periodic reconciliation.

In the current composition the store withdraws a publication's stream only after its session has been unregistered
(`CloseStream` runs from `OnSessionEndedAsync`, and a packaging failure ends the publish first), so "publishing with a
drained HLS stream" is reached only transiently or if a stage closes its stream while the publish continues; the
catalog reports it correctly either way.

### Incarnation identity and ordering

Entries are keyed by `(application, stream, incarnationId)` and looked up by incarnation identifier, so an event or
snapshot about one incarnation cannot touch another. The registry assigns every registration a strictly increasing
`PublisherSession.Generation` (an interlocked counter, set before the session becomes visible), which the HLS
packaging stage passes to the store when it opens the stream. A later publication of a name is registered only after
the previous one was removed, so it always has a larger generation. The **current** incarnation of a name is the one
with the largest generation; incarnation identifiers and timestamps are never compared.

Stale information cannot win:

- Events never create entries; only snapshots do. An event for an unknown incarnation requests reconciliation.
- Within an incarnation, states only move forward: publishing `publishing` → `ended`; HLS `pending` → `live` →
  `ended` → `withdrawn`. Every merge takes the later state, so a duplicate or late event is a no-op and an older
  snapshot cannot undo a newer event.
- Reconciliations are serialized, so each snapshot is newer than the previous one.
- Each event-driven change increments an epoch. When `Drained` removes an ended incarnation, it leaves a tombstone
  with that epoch; a snapshot whose acquisition started before the tombstone's epoch cannot reintroduce it, and later
  events for it are ignored. A reconciliation that started after the tombstone releases it, so tombstones stay
  bounded. `Drained` while still publishing keeps the entry as `withdrawn`.
- An event applied while a snapshot was being acquired keeps its media description over the snapshot's.

### Reconciliation and coalescing

`StreamCatalogService` reconciles once in `StartAsync`, before the HTTP origin and RTMP listener start; the catalog
reports not initialized (503) until a reconciliation has succeeded, and a failed startup reconciliation is retried by
the loop. Afterwards reconciliations are requested by the `StreamCatalog:ReconciliationInterval` timer (a
`TimeProvider` timer), by events it cannot apply (a new `Published`, unknown incarnations, and `Ended`, because the HLS
stream closes without an event of its own), by an increase in the event channel's dropped count, and by a one-shot
settle timer armed after any reconciliation that left an incarnation unsettled (at most one request per second, only
while that lasts). Requests set a single pending flag and release a one-slot semaphore: at most one request is
pending and one reconciliation runs at a time. A request made while a reconciliation runs makes another pending, so
it is never lost; extra requests are counted as coalesced. The loop also starts reconciliations at least
`StreamCatalogService.CoalescingWindow` (100 ms) apart: a request arriving sooner after the previous start waits for
the rest of the window and is served, together with every request made meanwhile, by one reconciliation, whose
snapshot is acquired after all of them. An isolated request is served at once. Under lifecycle churn this bounds the
full-snapshot work to at most ten reconciliations per second, instead of one per event, and delays a new publication's
first catalog entry by at most the window. Stopping cancels the loop, waits for it (it only waits for a request or the
window, or finishes an in-memory reconciliation) and disposes the timers.

### Atomic publication

Every change derives a new immutable `StreamCatalogSnapshot` (entries, lookups, version, reconciliation time) from the
published one and publishes it with one volatile reference write. Snapshots are persistent (immutable sorted set and
dictionaries): an applied event replaces or removes one entry in O(log n), and a reconciliation, which still merges
every incarnation of its source snapshot, replaces only the entries whose merged state differs and shares the rest;
only the changed streams' `current` flags are recomputed. Readers (`StreamCatalog.Current`, the HTTP routes) never lock or
wait; each response is serialized from the one snapshot it read. A short writer lock serializes publication between
the event consumer and reconciliation; it is never held while acquiring a snapshot or serializing. A failed
reconciliation (logged as event 9200, counted) keeps the previous snapshot.

### HTTP

`GET /health/streams` returns every entry, ordered by application, stream and newest generation first;
`/health/streams/{application}/{stream}` returns that stream's current incarnation. Both are served by the management
endpoints with the same loopback-only policy, headers, `HEAD`, 404 and 405 behaviour and `Management:Enabled` switch;
there is no other server or listener.

```json
{
  "version": 12,
  "reconciledAt": "2026-11-25T08:00:00+00:00",
  "streams": [
    {
      "application": "live",
      "stream": "camera1",
      "incarnationId": "3f2a9c1e0b7d4e8f9a6b5c4d3e2f1a0b",
      "current": true,
      "publishing": { "state": "ended", "startedAt": "…", "endedAt": "…", "endReason": "unpublished" },
      "hls": { "state": "ended", "playlistAvailable": true },
      "media": { "audio": { "codec": "mp4a.40.2", "configurationVersion": 1, "sampleRate": 48000, "channelConfiguration": 2, "channels": 2 } }
    }
  ]
}
```

`publishing.state` is `publishing` or `ended`; `hls.state` is `pending` (registered, HLS stream not opened yet),
`live` (HLS stream opened and being packaged), `ended` (ended playlist still served) or `withdrawn` (playlist gone,
segments draining, or drained while still publishing). `playlistAvailable` is independent of the state and reports
whether the playlist route served the playlist when the store was last read, taken from the store, never inferred
from registration, the HLS open, the entry's existence or the media description. A newly opened stream is legitimately
`live` with `playlistAvailable: false` (the playlist route returns 404) until its first segment is published, for at
least `Hls:TargetSegmentDuration` and at most `Hls:MaxSegmentDuration`, plus up to one settle delay before the catalog
observes it. It is never true for `pending` or `withdrawn`. Clients must use `playlistAvailable`, not `hls.state`,
when deciding whether to request the playlist. `media` is the latest complete description (the `MediaDescribed` shape) and is absent until one is
established; it is never inferred. `endedAt` and `endReason` are absent if the catalog has not observed them (the
session left the registry before a snapshot and its `Ended` event was not delivered). The body contains no stream
keys, credentials, addresses, connection or session identifiers, or generations. `version` increases with every
published snapshot.

### Metrics

On meter `RTSP.IngestService.Streams`: `stream_catalog.streams` (gauge), `stream_catalog.reconciliations` (tag
`result`: `success`, `failure`), `stream_catalog.reconciliation.duration` (s), `stream_catalog.reconciliation.requests`
(tag `reason`: `startup`, `periodic`, `event`, `loss`, `inconsistency`), `stream_catalog.reconciliation.coalesced` and
`stream_catalog.reconciliation.age` (s since the last success). Logs: 9200 reconciliation failed (warning), 9201
initialized (information, with the entry count), 9202 event loss detected (information).

### Tests

`StreamCatalogTests` cover initialization, startup with active streams, every RTMP/HLS state combination, unsettled
detection, failure keeping the previous catalog, replacement ordered by generation, stale, unknown, duplicate and
out-of-order events, stale snapshots after drain, events during acquisition, complete media replacement, loss
detection, coalescing and concurrent readers. `StreamCatalogServiceTests` cover startup, retry, the periodic and
settle timers on fake time, requests during a reconciliation and shutdown. `StreamCatalogSourceTests` cover registry generations, the
store inventory and a lifecycle and replacement over the real registry, media stage and store.
`StreamCatalogPlaylistAvailabilityTests` compare `playlistAvailable` with the real playlist route at every lifecycle
phase, across a replacement and under concurrent readers.
`ManagementEndpointsTests` cover the routes' schema, detail and 404s, 503, HEAD, 405, loopback policy (IPv4, IPv6,
IPv4-mapped, non-loopback, missing address, forwarding headers), management disabled and one-snapshot responses.
`StreamCatalogHostTests` run the real host with TCP and FFmpeg publishes and the interval validation (exit 78).
## Telemetry export

The service's metrics can be exported with OpenTelemetry over OTLP. Export is **disabled by default**; only metrics
are exported. There is no distributed tracing, no log export and no Prometheus endpoint.

### Architecture

`TelemetryServiceCollectionExtensions.AddTelemetryExport` (registered after logging and before every ingest component)
calls `AddOpenTelemetry().WithMetrics()` from `OpenTelemetry.Extensions.Hosting` and configures the meter provider
from the validated `Telemetry:Otlp` options when the provider is built. When enabled, it subscribes to every
application meter by name and adds the SDK's `PeriodicExportingMetricReader` around the `OtlpMetricExporter` from
`OpenTelemetry.Exporter.OpenTelemetryProtocol` (cumulative temporality, resource `service.name` = `Application:Name`).
When disabled, the provider has no meter and no reader: no exporter exists and nothing connects.

The instruments themselves are unchanged. The existing metrics classes own their instruments, created from the host's
`IMeterFactory`. The SDK observes them through its own `MeterListener` and exports their names, units, descriptions,
types, values and fixed tag vocabularies as they are. No instrument, meter or tag is added. Exported meters:

| Meter | Instruments |
|-------|-------------|
| `RTSP.IngestService.Rtmp` | 11 ([RTMP metrics](#metrics)) |
| `RTSP.IngestService.Media` | 7 ([media metrics](#metrics-1)) |
| `RTSP.IngestService.Hls` | 11 ([HLS metrics](#metrics-2)) |
| `RTSP.IngestService.Http` | 7 ([HTTP origin metrics](#metrics-3)) |
| `RTSP.IngestService.Streams` | 10: four `stream_events.*` and six `stream_catalog.*` ([stream events](#channel-drops-and-metrics), [catalog](#metrics-4)) |

### Configuration

`Telemetry:Otlp` (strict binding, so unknown keys in `Telemetry` or `Telemetry:Otlp` are errors; validated at start;
invalid values exit with code 78):

| Setting | Default | Valid values |
|---------|---------|--------------|
| `Enabled` | `false` | `true` or `false` |
| `Endpoint` | Unset | Required when enabled. An absolute `http` or `https` URI without user information, query or fragment |
| `Protocol` | `grpc` | `grpc` (OTLP/gRPC) or `http/protobuf` (OTLP/HTTP, binary protobuf) |
| `ExportInterval` | `00:00:15` | 1 s – 5 min |

`Endpoint` is the collector's base address. With `grpc` it is used as is; with `http/protobuf`, `/v1/metrics` is
appended to its path unless the path already ends with it. Each export request times out after 10 seconds. Example
for a collector on its default ports:

```json
"Telemetry": {
  "Otlp": {
    "Enabled": true,
    "Endpoint": "http://otel-collector:4317",
    "Protocol": "grpc",
    "ExportInterval": "00:00:15"
  }
}
```

(`"Endpoint": "http://otel-collector:4318", "Protocol": "http/protobuf"` for OTLP/HTTP.) The SDK still honours the
standard `OTEL_*` environment variables for settings this section does not cover, such as `OTEL_EXPORTER_OTLP_HEADERS`
and `OTEL_RESOURCE_ATTRIBUTES`; the four settings above always take precedence over their environment equivalents.

### Failure isolation and shutdown

- Export runs on the SDK's reader thread. Instruments record into the SDK's in-memory aggregation with no I/O, no
  queue and no per-measurement allocation on the RTMP, media or HLS path. Memory is bounded by the fixed
  instrument and tag cardinality.
- Building the provider does not connect, so an unavailable collector never delays or fails startup. A failed export
  drops that export's data (the next export is cumulative) and changes neither readiness nor the lifecycle state.
- The SDK reports failures only through its EventSources. While export is enabled, `OpenTelemetryDiagnosticsListener`
  logs their warnings and errors as event 9301 with only the source, event name and event id. Payloads can contain
  the endpoint and exception text and are never logged. Expected notices are skipped: no tracer or logger provider
  registered, and runtime or framework instruments the provider does not subscribe to. Native AOT disables EventSource
  by default, so the project sets `EventSourceSupport` to keep these diagnostics in the published binary.
- `OtlpMetricsExportService`, registered right after the provider, stops after every ingest component. It calls the
  SDK's `MeterProvider.Shutdown`, a final collect-and-export that includes the components' last measurements. The wait
  is bounded by 5 seconds (`FlushTimeout`); the SDK also cancels the final export at that deadline. The wait ends at
  once when the host's stop token, bounded by `Lifetime:ShutdownTimeout`, is cancelled. Event 9302 is logged when the
  SDK reports the shutdown incomplete or the wait was abandoned. An export that failed or was cancelled within the
  bound is reported by the SDK itself (9301). Disposal does not wait for an unanswered request, so an unresponsive
  collector cannot hold the process.

Event IDs: 9300 export enabled (information, with protocol, interval and meter count, never the endpoint), 9301 SDK
diagnostic (warning), 9302 final export incomplete (warning).

### Tests

`TelemetryOptionsTests` cover the defaults, every endpoint, protocol and interval rule, unknown keys, exit 78 through
the service runner and the metrics URI. `TelemetryExportTests` cover export disabled by default (no exporter, no
connection over the whole host lifetime even with an endpoint configured, and in-process instruments still recording),
OTLP/HTTP delivery of all five meters to a loopback collector, and every one of the 46 instruments exported through an
in-memory exporter. That check covers meter, name, unit, description, type, values and tag vocabulary. A real FFmpeg
publish and HTTP playback must export no identifying tag. `TelemetryHostTests` cover a refusing collector over both
protocols (listeners bound, ready, lifecycle unaffected, failure logged without the endpoint) and an unresponsive
collector (stop bounded by the flush timeout, and by a cancelled stop token; disposal not blocked). The telemetry tests
run in a non-parallel collection because the SDK subscribes to meters by name across the process.
