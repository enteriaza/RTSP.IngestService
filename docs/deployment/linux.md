# Deploying RTSP.IngestService on Linux (systemd)

RTSP.IngestService runs as a systemd service of `Type=notify`. It detects systemd (parent process is systemd) and then
sends `READY=1` once every hosted service has started, `STOPPING=1` when shutdown begins, and `WATCHDOG=1` keep-alives
when `WatchdogSec` is set. Outside systemd it runs as a normal console application.

## Build the release (on Windows)

Linux releases are self-contained Native AOT executables for `linux-x64`, cross-compiled on Windows. The project
references the `StuDev.AotAnywhere` MSBuild SDK (`src/RTSP.IngestService/RTSP.IngestService.csproj`), which performs
the Linux link step on the Windows host with a Zig linker and sysroot restored from NuGet. No WSL, Linux machine or
Linux compiler is needed to build, and the result is not a framework-dependent deployment: the target host needs no
.NET runtime.

Build from any directory with Windows PowerShell 5.1 or PowerShell 7:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Publish-Release.ps1 -Platform linux-x64
```

`-Platform` accepts `win-x64`, `linux-x64` or `all` (the default, which builds the Windows release too). For each
target the script runs `dotnet publish -c Release -r <rid> --self-contained true -p:PublishAot=true` into
`.artifacts/release-staging/`, then validates the result before replacing the release directory. The run fails with
exit code 1, leaving the existing `Releases/linux-x64` unchanged, if:

- the publish fails or reports any warning;
- the project does not reference `StuDev.AotAnywhere`, or the publish log has no `AotAnywhere: linking` step;
- the executable is not a 64-bit x86-64 ELF binary;
- the package does not contain exactly the expected files, or the systemd unit fails validation.

Publish logs are kept in `.artifacts/release-staging/logs/`.

## Release package

The script generates `Releases/linux-x64` (generated output, not tracked in Git):

```text
Releases/
└── linux-x64/
    ├── RTSP.IngestService
    ├── appsettings.json
    └── systemd/
        └── rtsp-ingest-service.service
```

| File | Purpose |
|------|---------|
| `RTSP.IngestService` | The Native AOT executable (ELF 64-bit, x86-64). |
| `appsettings.json` | The shipped default configuration. It contains no credentials. |
| `systemd/rtsp-ingest-service.service` | The systemd unit, copied unchanged from [`deploy/linux/rtsp-ingest-service.service`](../../deploy/linux/rtsp-ingest-service.service). |

The package contains nothing else: no environment file, no install script, no symbols. `appsettings.Development.json`
and `appsettings.Production.json` are never packaged.

### Host requirements

- Linux on x86-64 with glibc. The executable links only against the C library (`libc`, `libm`, `libpthread`,
  `libdl`, `librt`).
- OpenSSL 3. .NET cryptography loads `libssl.so.3` and `libcrypto.so.3` at runtime: the running service and
  `publisher-key` both load them. On Ubuntu they come from the `libssl3t64` package, which is installed by default.
- No .NET runtime and no ICU (the service uses invariant globalization).

## Install

Copy the `Releases/linux-x64` directory to the host, for example with `scp -r`, and run these commands from inside
the copied directory. `install` sets explicit modes, so files copied from a Windows file system get correct
permissions:

```bash
cd linux-x64
sudo useradd --system --no-create-home --shell /usr/sbin/nologin rtsp-ingest-service
sudo install -d -m 0755 /opt/rtsp-ingest-service
sudo install -m 0755 RTSP.IngestService /opt/rtsp-ingest-service/RTSP.IngestService
sudo install -m 0644 appsettings.json /opt/rtsp-ingest-service/appsettings.json
sudo install -m 0644 systemd/rtsp-ingest-service.service /etc/systemd/system/rtsp-ingest-service.service
sudo systemctl daemon-reload
sudo systemctl enable --now rtsp-ingest-service
```

The unit is installed with `install -m 0644`, not `cp`, because files on a Windows file system (for example a WSL
`/mnt/c` mount) typically appear executable, and `cp` keeps that mode. systemd then warns that the unit
"is marked executable. Please remove executable permission bits".

The unit runs `/opt/rtsp-ingest-service/RTSP.IngestService` with that directory as its working directory, as the
`rtsp-ingest-service` user and group. The files stay owned by root; the service only needs to read them.
`systemctl start` (and `enable --now`) returns only after the service has sent `READY=1`, so a successful start means
every hosted service started.

### Configuration

The configuration directory is the executable's directory, independent of the working directory, so
`appsettings.json` must be next to the executable in `/opt/rtsp-ingest-service/`. The unit sets
`DOTNET_ENVIRONMENT=Production`, so `/opt/rtsp-ingest-service/appsettings.Production.json`, if present, overrides it.
Put host-specific settings and publisher credentials there; it is not part of the package. Create it readable by the
service, for example `sudo install -m 0640 -g rtsp-ingest-service appsettings.Production.json
/opt/rtsp-ingest-service/`. Do not put credentials in configuration files that are committed to source control.

Settings can also be supplied as environment variables in a systemd drop-in. Run
`sudo systemctl edit rtsp-ingest-service` and add a `[Service]` section with lines such as
`Environment=Http__Port=8081`. These are read at startup only.

## Update

Build a new release, copy its `linux-x64` directory to the host and run from inside it:

```bash
cd linux-x64
sudo systemctl stop rtsp-ingest-service
sudo install -m 0755 RTSP.IngestService /opt/rtsp-ingest-service/RTSP.IngestService
sudo install -m 0644 appsettings.json /opt/rtsp-ingest-service/appsettings.json
sudo install -m 0644 systemd/rtsp-ingest-service.service /etc/systemd/system/rtsp-ingest-service.service
sudo systemctl daemon-reload
sudo systemctl start rtsp-ingest-service
```

This replaces the shipped defaults and the unit. `appsettings.Production.json` and any drop-ins under
`/etc/systemd/system/rtsp-ingest-service.service.d/` are left unchanged.

## Unit file

[`deploy/linux/rtsp-ingest-service.service`](../../deploy/linux/rtsp-ingest-service.service) (packaged as
`systemd/rtsp-ingest-service.service`):

| Setting | Purpose |
|---------|---------|
| `Type=notify`, `NotifyAccess=main` | Start completes on `READY=1` from the main process. |
| `ExecStart=/opt/rtsp-ingest-service/RTSP.IngestService`, `WorkingDirectory=/opt/rtsp-ingest-service` | The install location. |
| `User=rtsp-ingest-service`, `Group=rtsp-ingest-service` | The system account created during installation. |
| `Environment=DOTNET_ENVIRONMENT=Production` | Selects `appsettings.Production.json`. |
| `TimeoutStartSec=90`, `TimeoutStopSec=45` | Must exceed `Lifetime:StartupTimeout` (60 s) and `Lifetime:ShutdownTimeout` (30 s). |
| `KillSignal=SIGTERM`, `KillMode=mixed` | SIGTERM triggers graceful shutdown of the main process; remaining processes are killed after it exits. |
| `WatchdogSec=30` | The service pings every 15 s while starting, running or stopping; a missed deadline kills and restarts it. |
| `Restart=on-failure`, `RestartSec=5` | Restart after a non-zero exit, a crash or a watchdog timeout. |
| `RestartPreventExitStatus=78` | Do not restart after a configuration error. |
| `StartLimitIntervalSec=300`, `StartLimitBurst=5` | Stop restarting after five starts in five minutes. |
| Hardening (`ProtectSystem=strict`, `ProtectHome=yes`, `NoNewPrivileges=yes`, ...) | The service needs no write access to the file system. File logging is off under systemd by default, and logs go to the journal. |

If you change the timeouts in `appsettings.json`, adjust `TimeoutStartSec` and `TimeoutStopSec` to stay above them.

### Network

The RTMP listener accepts publishers on TCP 1935 on all addresses by default (`Rtmp:ListenAddress` `0.0.0.0`,
`Rtmp:Port`). The port is above 1024, so no capability is needed. Open it in the host firewall, ideally only for
encoder networks (for example `sudo ufw allow from <encoder subnet> to any port 1935 proto tcp`).

Configure publisher credentials in `appsettings.Production.json`, which is reloaded without a restart, or with
`Rtmp__Authentication__Publishers__0__KeyHash`-style environment variables, which are read at startup only. Each
publisher needs a `Name`, an `Application` and a `KeyHash` from `RTSP.IngestService publisher-key generate`;
plaintext keys are not accepted (see [RTMP ingest](../architecture.md#rtmp-ingest)). Run the command from the install
directory: `/opt/rtsp-ingest-service/RTSP.IngestService publisher-key generate`. It prints a new stream key and its
hash; give the key to the encoder, store only the hash, and keep neither in shell history or logs.

If the RTMP port is in use, the service fails to start with exit code 1 and logs event 5001.

HLS playback is served over plain HTTP on `127.0.0.1:8080` by default (`Http:ListenAddress`, `Http:Port`), so only
local clients or a reverse proxy can reach it. To serve viewers directly, set `Http__ListenAddress=0.0.0.0` and open
the port (`sudo ufw allow 8080/tcp`); put a TLS-terminating proxy in front for HTTPS. If the HTTP port is in use, the
service fails to start with exit code 1 (see [HTTP HLS origin](../architecture.md#http-hls-origin)).

The `/health/` endpoints are served on the same port and answer only loopback clients (see
[Management endpoints](../architecture.md#management-endpoints)). A reverse proxy must forward only `/hls/`, never
`/health/`.

## Verify

Run these on the host. The commands use the default ports; substitute your configured ones.

| Check | Command | Expected result |
|-------|---------|-----------------|
| Service started | `systemctl is-active rtsp-ingest-service` | `active`. Under `Type=notify` this means `READY=1` was sent, so every hosted service started. |
| RTMP listener | `ss -ltn 'sport = :1935'` | A `LISTEN` socket on `0.0.0.0:1935`. This shows the port is bound, not that publishing works. |
| HTTP listener | `ss -ltn 'sport = :8080'` | A `LISTEN` socket on `127.0.0.1:8080`. |
| Liveness | `curl -s -w ' %{http_code}\n' http://127.0.0.1:8080/health/live` | `{"status":"live","state":"Running"} 200`. The process is up and not failed or stopping; 503 otherwise. |
| Readiness | `curl -s -w ' %{http_code}\n' http://127.0.0.1:8080/health/ready` | `{"status":"ready","state":"Running","rtmpListener":true,"httpListener":true} 200`. Running with both listeners bound; 503 otherwise. |
| Startup log | `journalctl -u rtsp-ingest-service -b --no-pager \| grep -E 'RTMP listener bound\|HTTP HLS origin listening'` | Events 5000 and 8000 with the bound endpoints. |

A real publish is the only end-to-end check of ingest and playback: publish to `rtmp://<host>:1935/<application>/<stream key>`,
then fetch `http://127.0.0.1:8080/hls/<application>/<publisher name>/index.m3u8` on the host.

Clean shutdown:

```bash
sudo systemctl stop rtsp-ingest-service
systemctl show rtsp-ingest-service -p ActiveState,Result,ExecMainStatus,NRestarts
journalctl -u rtsp-ingest-service -n 20 --no-pager
```

Expect `ActiveState=inactive`, `Result=success`, `ExecMainStatus=0` and no restart. The journal ends with
`Service host terminated in state Stopped with exit code 0`.

## Operate

| Action | Command |
|--------|---------|
| Reload unit files after installing or changing them | `sudo systemctl daemon-reload` |
| Enable or disable start at boot | `sudo systemctl enable\|disable rtsp-ingest-service` |
| Start, stop or restart | `sudo systemctl start\|stop\|restart rtsp-ingest-service` |
| Status | `systemctl status rtsp-ingest-service` |
| Exit status and result | `systemctl show rtsp-ingest-service -p Result,ExecMainStatus,NRestarts` |
| Logs | `journalctl -u rtsp-ingest-service -f` |
| Errors only | `journalctl -u rtsp-ingest-service -p err` |

### Logs

Under systemd, each event is written to standard output as one line prefixed with its syslog priority (`<3>` error,
`<4>` warning, `<6>` information, ...). journald stores it with that priority, so `journalctl -p` filtering works.
Timestamps are omitted because journald records its own, and exception stack traces are folded onto the same line.
File logging is disabled by default under systemd (`FileLogging:Mode` `Auto`).

### Exit codes

| Code | Meaning | systemd behaviour |
|------|---------|-------------------|
| 0 | Stopped normally. | No restart. |
| 1 | Startup failed, a background service faulted, or startup exceeded `Lifetime:StartupTimeout`. | Restart. |
| 78 (`EX_CONFIG`) | Invalid configuration. | No restart (`RestartPreventExitStatus=78`). |

## Uninstall

```bash
sudo systemctl disable --now rtsp-ingest-service
sudo rm /etc/systemd/system/rtsp-ingest-service.service
sudo rm -rf /etc/systemd/system/rtsp-ingest-service.service.d
sudo systemctl daemon-reload
sudo rm -rf /opt/rtsp-ingest-service
sudo userdel rtsp-ingest-service
```

## Verified behaviour

**Native AOT release under systemd.** Verified on Ubuntu 26.04 (WSL 2, systemd 259 as PID 1). The `Releases/linux-x64`
package built on Windows was installed system-wide with the packaged unit unchanged, including `User=`, `Group=` and
the hardening options. A temporary drop-in moved the listeners to test ports.

- `systemctl start` returned after `READY=1`, and the unit was `active`.
- The process ran as `rtsp-ingest-service` with `ProtectSystem=strict` and `NoNewPrivileges=yes`.
- `/health/live` and `/health/ready` returned 200.
- Watchdog keep-alives advanced.
- `systemctl stop` exited with status 0, `Result=success` and no restart.
- A configuration error exited with status 78, `Result=exit-code` and no restart.
- The same package, run as a console process with FFmpeg 9.0.2, served live and ended HLS playlists that `ffprobe`
  and a full FFmpeg decode read.

**Earlier self-contained non-AOT build.** This was run as a user unit (`systemctl --user`, with `User=`, `Group=` and
the hardening block removed, `WatchdogSec=4` and `TimeoutAbortSec=5`):

- `systemctl start` returned after `READY=1` in about 250–350 ms.
- Watchdog keep-alives arrived every 2 s.
- `systemctl stop` and `kill -TERM` stopped gracefully with exit status 0 and no restart.
- `systemctl restart` worked.
- Freezing the process (`kill -STOP`) produced `Watchdog timeout`, `Result=watchdog` and an automatic restart.
- Journal entries carried the expected priorities (6 information, 3 error, 2 critical).

Not verified with the Native AOT build: the watchdog-timeout restart, and a distribution other than Ubuntu 26.04.
