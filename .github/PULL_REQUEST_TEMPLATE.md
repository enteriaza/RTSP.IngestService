## Summary
Briefly describe what changed and why.

## Component
- [ ] RTMP ingest
- [ ] Media processing / HLS packaging
- [ ] HTTP HLS origin
- [ ] Hosting, lifecycle, configuration or logging
- [ ] Repository tooling (`.github`, `deploy`, `docs`)

## Test plan
- [ ] `dotnet build RTSP.slnx -c Release`
- [ ] `dotnet test RTSP.slnx -c Release` (FFmpeg interoperability tests executed, not skipped)
- [ ] `dotnet format RTSP.slnx --verify-no-changes`
- [ ] Native AOT publish / config checks if applicable (no Development credentials in publish)

## Notes
Call out breaking changes, migration steps, or unverified areas.
