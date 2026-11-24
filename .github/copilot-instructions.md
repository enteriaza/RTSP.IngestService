# RTSP.IngestService Copilot instructions

- Single service: `src/RTSP.IngestService` (RTMP ingest, media processing, HLS packaging, HTTP HLS origin), tests in
  `tests/RTSP.IngestService.Tests`, solution `RTSP.slnx`, deployment assets in `deploy/`, documentation in `docs/`.
- Honor gitignored Development/Production appsettings; never commit or print secrets or stream keys.
- Development configuration must not appear in publish output.
- Do not stage/commit/push unless explicitly asked.
