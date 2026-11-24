// Process entry point. ServiceRunner detects the hosting mode (console, Windows Service or systemd), owns the host
// lifecycle and returns the process exit code.

using RTSP.IngestService.Hosting;

return await ServiceRunner.RunAsync(args).ConfigureAwait(false);
