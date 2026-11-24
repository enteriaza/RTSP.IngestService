# Deploying RTSP.IngestService on Windows

RTSP.IngestService runs as a Windows service under the Service Control Manager (SCM). The same executable also runs
interactively; it detects the SCM (parent process `services.exe`) and switches to service mode automatically.

## Build

Native AOT requires the Visual Studio C++ build tools ("Desktop development with C++").

```powershell
dotnet publish src/RTSP.IngestService/RTSP.IngestService.csproj -c Release -r win-x64 -o .artifacts/publish/win-x64
```

The output is a single native `RTSP.IngestService.exe` plus `appsettings.json`. `appsettings.Development.json` and
`appsettings.Production.json` are never copied to the publish output.

## Install

Run in an elevated PowerShell session. The example installs to `C:\Program Files\RTSP.IngestService` and runs under
the per-service virtual account `NT SERVICE\RTSP.IngestService`, which needs no password.

```powershell
$name = 'RTSP.IngestService'
$dir  = 'C:\Program Files\RTSP.IngestService'

New-Item -ItemType Directory -Path $dir, "$dir\logs" -Force | Out-Null
Copy-Item .artifacts\publish\win-x64\RTSP.IngestService.exe, .artifacts\publish\win-x64\appsettings.json $dir

sc.exe create $name binPath= "`"$dir\RTSP.IngestService.exe`"" start= delayed-auto obj= "NT SERVICE\$name" DisplayName= 'RTSP Ingest Service'
sc.exe description $name 'Streaming platform service host.'

# The service writes its log files under logs\; it needs no other write access.
icacls "$dir\logs" /grant "NT SERVICE\${name}:(OI)(CI)M"

# Restart twice after a failure, then stay stopped; reset the failure count after one day.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/30000//0
# Apply the recovery actions when the service stops with a non-zero exit code, not only when it crashes.
sc.exe failureflag $name 1

Start-Service $name
```

Production overrides belong in `appsettings.Production.json` next to the executable (the host environment is
`Production` unless `DOTNET_ENVIRONMENT` is set for the service). Do not put credentials in configuration files that are
committed to source control.

The RTMP listener accepts publishers on TCP 1935 by default (`Rtmp:ListenAddress`, `Rtmp:Port`). Allow it through the
firewall, ideally only from encoder networks:

```powershell
New-NetFirewallRule -DisplayName 'RTSP.IngestService RTMP' -Direction Inbound -Protocol TCP -LocalPort 1935 -Action Allow -RemoteAddress <encoder subnet>
```

Configure publisher stream keys in `appsettings.Production.json` (see [RTMP ingest](../architecture.md#rtmp-ingest)).

HLS playback is served over plain HTTP on `127.0.0.1:8080` by default (`Http:ListenAddress`, `Http:Port`), so only
local clients or a reverse proxy can reach it. To serve viewers directly set `Http:ListenAddress` to `0.0.0.0` and
allow the port (`New-NetFirewallRule -DisplayName 'RTSP.IngestService HLS' -Direction Inbound -Protocol TCP
-LocalPort 8080 -Action Allow`); put a TLS-terminating proxy in front for HTTPS. If the HTTP port is in use the
service fails to start with exit code 1 (see [HTTP HLS origin](../architecture.md#http-hls-origin)).
If the port is in use the service fails to start with exit code 1 and logs event 5001.

## Operate

| Action | Command |
|--------|---------|
| Start | `Start-Service RTSP.IngestService` |
| Stop | `Stop-Service RTSP.IngestService` |
| Restart | `Restart-Service RTSP.IngestService` |
| Status and last exit code | `sc.exe queryex RTSP.IngestService` |

### Logs

In service mode there is no console. Logs are written as structured JSON (one event per line) to
`logs\rtsp-ingest-service-YYYYMMDD.log` under the install directory, rolling daily and at 100 MB, keeping 31 files
(`FileLogging` section). Failures that happen before the host is built (for example an unparseable configuration value)
are written to `logs\bootstrap-YYYYMMDD.log`.

### Exit codes

`sc.exe queryex` reports the process exit code as `WIN32_EXIT_CODE`:

| Code | Meaning |
|------|---------|
| 0 | Stopped normally. |
| 1 | Startup failed, a background service faulted, or startup exceeded `Lifetime:StartupTimeout`. |
| 78 | Invalid configuration. Fix the configuration before restarting. |

Windows has no equivalent of systemd's `RestartPreventExitStatus`: with `failureflag 1`, a configuration error (78)
also triggers the recovery actions. The recovery policy above therefore limits automatic restarts to two.

### Shutdown timing

On a stop request the service asks the SCM for `Lifetime:ShutdownTimeout` plus 5 seconds of additional time, then stops
hosted services in reverse start order. During an operating-system shutdown Windows enforces its own, shorter deadline
regardless of the configured timeout.

## Uninstall

```powershell
Stop-Service RTSP.IngestService
sc.exe delete RTSP.IngestService
Remove-Item 'C:\Program Files\RTSP.IngestService' -Recurse
```

## Verified behaviour

Verified on Windows 11 (build 26100) with the Native AOT build installed as above under `NT SERVICE\RTSP.IngestService`:
start reaches `RUNNING` in under 2 s; `sc stop` completes a graceful shutdown in about 30 ms with exit code 0;
`Restart-Service` works; a configuration error stops the service with `WIN32_EXIT_CODE 78`; JSON log files are written
under `logs\`.
