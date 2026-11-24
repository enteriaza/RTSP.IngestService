# Deploying RTSP.IngestService on Linux (systemd)

RTSP.IngestService runs as a systemd service of `Type=notify`. It detects systemd (parent process is systemd) and then
sends `READY=1` once every hosted service has started, `STOPPING=1` when shutdown begins, and `WATCHDOG=1` keep-alives
when `WatchdogSec` is set. Outside systemd it runs as a normal console application.

## Build

Native AOT for Linux must be built on Linux (it cannot be cross-compiled from Windows) and needs `clang` and
`zlib1g-dev`:

```bash
dotnet publish src/RTSP.IngestService/RTSP.IngestService.csproj -c Release -r linux-x64 -o .artifacts/publish/linux-x64
```

From Windows, publish a self-contained (non-AOT) build instead:

```powershell
dotnet publish src/RTSP.IngestService/RTSP.IngestService.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=false -o .artifacts/publish/linux-x64
```

Both builds run with the same unit file. `appsettings.Development.json` and `appsettings.Production.json` are never
copied to the publish output.

## Install

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin rtsp-ingest-service
sudo mkdir -p /opt/rtsp-ingest-service
sudo cp -r .artifacts/publish/linux-x64/. /opt/rtsp-ingest-service/
sudo chmod 0755 /opt/rtsp-ingest-service/RTSP.IngestService
sudo cp deploy/linux/rtsp-ingest-service.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now rtsp-ingest-service
```

`systemctl start` returns only after the service has sent `READY=1`, so a successful start means every hosted service
started. Production overrides belong in `/opt/rtsp-ingest-service/appsettings.Production.json`. Do not put credentials
in configuration files that are committed to source control.

## Unit file

[`deploy/linux/rtsp-ingest-service.service`](../../deploy/linux/rtsp-ingest-service.service):

| Setting | Purpose |
|---------|---------|
| `Type=notify`, `NotifyAccess=main` | Start completes on `READY=1` from the main process. |
| `TimeoutStartSec=90`, `TimeoutStopSec=45` | Must exceed `Lifetime:StartupTimeout` (60 s) and `Lifetime:ShutdownTimeout` (30 s). |
| `KillSignal=SIGTERM`, `KillMode=mixed` | SIGTERM triggers graceful shutdown of the main process; remaining processes are killed after it exits. |
| `WatchdogSec=30` | The service pings every 15 s while starting, running or stopping; a missed deadline kills and restarts it. |
| `Restart=on-failure`, `RestartSec=5` | Restart after a non-zero exit, a crash or a watchdog timeout. |
| `RestartPreventExitStatus=78` | Do not restart after a configuration error. |
| `StartLimitIntervalSec=300`, `StartLimitBurst=5` | Stop restarting after five starts in five minutes. |
| Hardening (`ProtectSystem=strict`, ...) | The service needs no write access to the file system. |

If you change the timeouts in `appsettings.json`, adjust `TimeoutStartSec` and `TimeoutStopSec` to stay above them.

The RTMP listener accepts publishers on TCP 1935 by default (`Rtmp:ListenAddress`, `Rtmp:Port`). The port is above
1024, so no capability is needed. Open it in the host firewall, ideally only for encoder networks (for example
`sudo ufw allow from <encoder subnet> to any port 1935 proto tcp`). Configure publisher stream keys in
`appsettings.Production.json` or with `Rtmp__Authentication__Publishers__0__StreamKey`-style environment variables
(see [RTMP ingest](../architecture.md#rtmp-ingest)). If the port is in use the service fails to start with exit code 1
and logs event 5001.

HLS playback is served over plain HTTP on `127.0.0.1:8080` by default (`Http:ListenAddress`, `Http:Port`), so only
local clients or a reverse proxy can reach it. To serve viewers directly set `Http__ListenAddress=0.0.0.0` and open the
port (`sudo ufw allow 8080/tcp`); put a TLS-terminating proxy in front for HTTPS. If the HTTP port is in use the
service fails to start with exit code 1 (see [HTTP HLS origin](../architecture.md#http-hls-origin)). The loopback-only `/health/live` and `/health/ready` endpoints are served on the same port (see [Management endpoints](../architecture.md#management-endpoints)); a reverse proxy must forward only `/hls/`, never `/health/`.

## Operate

| Action | Command |
|--------|---------|
| Start / stop / restart | `sudo systemctl start\|stop\|restart rtsp-ingest-service` |
| Status | `systemctl status rtsp-ingest-service` |
| Exit status and result | `systemctl show rtsp-ingest-service -p Result,ExecMainStatus,NRestarts` |
| Logs | `journalctl -u rtsp-ingest-service -f` |
| Errors only | `journalctl -u rtsp-ingest-service -p err` |

### Logs

Under systemd, each event is written to standard output as one line prefixed with its syslog priority (`<3>` error,
`<4>` warning, `<6>` information, ...). journald stores it with that priority, so `journalctl -p` filtering works.
Timestamps are omitted because journald records its own; exception stack traces are folded onto the same line. File
logging is disabled by default under systemd (`FileLogging:Mode` `Auto`).

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
sudo systemctl daemon-reload
sudo rm -rf /opt/rtsp-ingest-service
sudo userdel rtsp-ingest-service
```

## Verified behaviour

Verified on Ubuntu 26.04 (WSL 2, systemd 259 as PID 1) with the self-contained non-AOT build, running this unit as a
user unit (`systemctl --user`, with `User=`, `Group=` and the hardening block removed, `WatchdogSec=4` and
`TimeoutAbortSec=5`):

- `systemctl start` returned after `READY=1` in about 250–350 ms; the unit was `active (running)`.
- Watchdog keep-alives arrived every 2 s (`WatchdogTimestamp` advanced).
- `systemctl stop` and `kill -TERM` stopped gracefully with exit status 0 and no restart.
- `systemctl restart` worked.
- Freezing the process (`kill -STOP`) produced `Watchdog timeout`, `Result=watchdog` and an automatic restart.
- A configuration error exited with status `78/CONFIG`, `Result=exit-code` and no restart (`NRestarts=0`).
- Journal entries carried the expected priorities (6 information, 3 error, 2 critical).

Not verified: a system-wide install with `User=`, `Group=` and the hardening options, and a Native AOT Linux build.
