# Security Policy

## Reporting a vulnerability

Do **not** open a public issue for security vulnerabilities or include secrets in bug reports.

Use GitHub Security Advisories (private) for this repository when available, or contact the repository maintainers privately.

## Configuration and credentials

RTSP.IngestService stores local Development and Production credentials (such as RTMP stream keys) in gitignored `appsettings.*.json` files. Example templates with placeholders are tracked. Do not commit real credentials.
