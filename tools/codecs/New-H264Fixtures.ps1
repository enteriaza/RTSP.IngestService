<#
.SYNOPSIS
Regenerates the H.264 decoder test fixtures in tests/RTSP.IngestService.Tests/Media/Codecs/Fixtures.

.DESCRIPTION
Each fixture is encoded from an FFmpeg synthetic source into Matroska, so every packet is one access unit with its
presentation timestamp, then written as:
  h264-<name>.h264      Annex B elementary stream (h264_mp4toannexb)
  h264-<name>.pts       presentation timestamp of each access unit in decode order (milliseconds)
  h264-<name>.framemd5  FFmpeg's single-threaded decode of the same stream: MD5 of each yuv420p frame in output order
The software encoders (libx264, libopenh264) run single-threaded and are reproducible for a given FFmpeg build.
Hardware encoders (h264_mf, h264_nvenc) depend on the driver; their committed outputs are the reference, and
regenerating them can change the bytes and hashes together. Rejection fixtures have no .framemd5.

.PARAMETER FFmpeg
Path to ffmpeg.exe; defaults to RTSP_FFMPEG_PATH. ffprobe must be in the same directory.

.PARAMETER Hardware
Also regenerate the hardware-encoder fixtures.
#>
param(
    [string] $FFmpeg = $env:RTSP_FFMPEG_PATH,
    [switch] $Hardware
)

$ErrorActionPreference = 'Stop'
if (-not $FFmpeg -or -not (Test-Path $FFmpeg)) { throw 'Set -FFmpeg or RTSP_FFMPEG_PATH.' }
$ffprobe = Join-Path (Split-Path $FFmpeg) 'ffprobe.exe'
$out = Join-Path $PSScriptRoot '..\..\tests\RTSP.IngestService.Tests\Media\Codecs\Fixtures' | Resolve-Path
$work = Join-Path ([IO.Path]::GetTempPath()) 'h264-fixtures'
New-Item -ItemType Directory -Force $work | Out-Null

function Invoke-Tool([string] $exe, [string[]] $arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed: $($arguments -join ' ')" }
}

function New-Fixture([string] $name, [string] $source, [int] $frames, [string[]] $encoder, [switch] $NoReference) {
    $mkv = Join-Path $work "$name.mkv"
    Invoke-Tool $FFmpeg (@('-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', $source, '-frames:v', "$frames") + $encoder + @('-an', $mkv))
    $h264 = Join-Path $out "h264-$name.h264"
    Invoke-Tool $FFmpeg @('-hide_banner', '-loglevel', 'error', '-y', '-i', $mkv, '-c:v', 'copy', '-bsf:v', 'h264_mp4toannexb', '-f', 'h264', $h264)
    $pts = & $ffprobe -v error -select_streams v:0 -show_entries packet=pts -of csv=p=0 $mkv
    if ($LASTEXITCODE -ne 0) { throw "ffprobe failed for $name" }
    [IO.File]::WriteAllText((Join-Path $out "h264-$name.pts"), (($pts | ForEach-Object { $_.Trim() }) -join "`n") + "`n")
    if (-not $NoReference) {
        Invoke-Tool $FFmpeg @('-hide_banner', '-loglevel', 'error', '-y', '-threads', '1', '-i', $mkv, '-fps_mode', 'passthrough', '-pix_fmt', 'yuv420p', '-f', 'framemd5', (Join-Path $out "h264-$name.framemd5"))
    }
}

$x264 = @('-c:v', 'libx264', '-threads', '1', '-pix_fmt', 'yuv420p')
$bt709 = @('-vf', 'setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv')

New-Fixture 'baseline-cavlc-176x144' 'testsrc2=size=176x144:rate=25' 30 ($x264 + @('-profile:v', 'baseline', '-crf', '26', '-x264-params', 'keyint=15:min-keyint=15:scenecut=0:ref=3'))
New-Fixture 'constrained-baseline-openh264-320x240' 'testsrc2=size=320x240:rate=25' 24 @('-c:v', 'libopenh264', '-threads', '1', '-profile:v', 'constrained_baseline', '-b:v', '400k', '-g', '12', '-slices', '2', '-pix_fmt', 'yuv420p')
New-Fixture 'main-cabac-bpyramid-weightp-320x240' 'testsrc2=size=320x240:rate=25' 36 ($x264 + @('-profile:v', 'main', '-crf', '27', '-x264-params', 'keyint=24:min-keyint=24:scenecut=0:bframes=3:b-pyramid=normal:b-adapt=0:weightp=2:weightb=1:ref=4:direct=spatial'))
New-Fixture 'high-8x8-temporal-cqm-352x288' 'testsrc2=size=352x288:rate=25' 30 ($x264 + @('-profile:v', 'high', '-crf', '27', '-x264-params', 'keyint=20:min-keyint=20:scenecut=0:bframes=2:b-adapt=0:ref=3:8x8dct=1:direct=temporal:cqm=jvt'))
New-Fixture 'high-cavlc-8x8-256x192' 'mandelbrot=size=256x192:rate=25' 30 ($x264 + @('-profile:v', 'high', '-crf', '24', '-x264-params', 'keyint=30:min-keyint=30:scenecut=0:cabac=0:8x8dct=1:bframes=2:b-adapt=0:ref=2'))
New-Fixture 'high-cropped-slices-bt709-198x110' 'testsrc2=size=198x110:rate=25' 20 ($x264 + $bt709 + @('-profile:v', 'high', '-crf', '25', '-x264-params', 'keyint=10:min-keyint=10:scenecut=0:slices=4:deblock=-2,1:bframes=1:b-adapt=0'))
New-Fixture 'high-intra-160x96' 'testsrc2=size=160x96:rate=25' 8 ($x264 + @('-profile:v', 'high', '-crf', '22', '-x264-params', 'keyint=1:8x8dct=1'))
New-Fixture 'reject-high422' 'testsrc2=size=64x64:rate=25' 2 @('-c:v', 'libx264', '-threads', '1', '-pix_fmt', 'yuv422p', '-profile:v', 'high422') -NoReference
New-Fixture 'reject-high10' 'testsrc2=size=64x64:rate=25' 2 @('-c:v', 'libx264', '-threads', '1', '-pix_fmt', 'yuv420p10le', '-profile:v', 'high10') -NoReference
New-Fixture 'reject-interlaced' 'testsrc2=size=64x64:rate=25' 4 @('-c:v', 'libx264', '-threads', '1', '-pix_fmt', 'yuv420p', '-flags', '+ildct+ilme', '-x264-params', 'interlaced=1:tff=1') -NoReference
New-Fixture 'reject-lossless444' 'testsrc2=size=64x64:rate=25' 2 @('-c:v', 'libx264', '-threads', '1', '-pix_fmt', 'yuv444p', '-qp', '0') -NoReference

if ($Hardware) {
    New-Fixture 'mf-high-320x240' 'testsrc2=size=320x240:rate=25' 20 @('-c:v', 'h264_mf', '-profile:v', '100', '-b:v', '500k', '-g', '10', '-pix_fmt', 'nv12')
    New-Fixture 'nvenc-high-bframes-320x240' 'testsrc2=size=320x240:rate=25' 24 @('-c:v', 'h264_nvenc', '-profile:v', 'high', '-bf', '2', '-g', '12', '-b:v', '600k', '-pix_fmt', 'yuv420p')
}
