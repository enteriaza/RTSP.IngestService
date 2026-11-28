# RTSP.IngestService — Proven Performance and Capacity

This document records the performance that RTSP.IngestService has demonstrated, the limits its current implementation
enforces, and what has not yet been demonstrated. The measured figures come from two programmes with different
workloads and methodology. The pass-through ingest figures come from the Phase 1 performance remediation programme
(November 2026). The figures in [Managed transcoding pipeline](#managed-transcoding-pipeline) come from Phase 6,
Managed Pipeline Performance and Capacity Engineering (November 2026). Targets, estimates and planning values are
labelled as such and are not benchmark results.

Source of the Phase 1 measurements: the Phase 1 remediation report and its raw evidence under
`.artifacts/performance/remediation/` (gitignored, local to the benchmark host). Section references such as "report §5"
point into that report.

## Overview

RTSP.IngestService is an ingest and HLS origin service. It accepts live H.264/AAC publications, packages them into
fragmented-MP4 HLS in memory, retains a bounded window of segments, and serves the playlists and segments from one
built-in HTTP origin (Kestrel). See [docs/architecture.md](docs/architecture.md).

When Phase 1 was measured, the service was pass-through: it packaged the publisher's encoded media without decoding
or re-encoding it, and all Phase 1 results describe that workload. The service has since added managed transcoding,
which re-encodes every publish to canonical H.264 and AAC in managed code before packaging. Its performance is
described in [Managed transcoding pipeline](#managed-transcoding-pipeline).

**Ingest protocol.** Despite the service name, the only ingest protocol implemented today is **RTMP** (default port
1935). There is no RTSP protocol implementation in `src/`. Every connection and ingest result in this document is an
RTMP result. RTSP ingest capacity has not been implemented or benchmarked.

**Intended deployment role.**

- Publishing clients connect to the ingest service.
- The service generates and retains HLS media.
- Frontend web servers consume the HLS output from the origin and distribute it.
- Viewer connections terminate at the frontend servers, not at RTSP.IngestService.

The frontend tier is an architectural intention. It is not part of this repository and has not been implemented or
benchmarked here. Viewer concurrency is therefore not the same as ingest-service connection concurrency: one origin
consumer per stream may serve any number of downstream viewers. The HTTP benchmarks below used load-generator clients
connected directly to the origin. They measure origin behaviour, not a production viewer population.

**Performance objectives of Phase 1.** Phase 1 had two targets. The first was more than 10 Gbps of HLS ingest and
origin throughput. The second was at least 2,000 concurrent RTMP connections. All memory, queues and caches had to
stay explicitly bounded.

## Benchmark environment

| Item | Value |
|---|---|
| Host | Single host; the load generator ran on the same host as the service |
| OS (Windows runs) | Windows 11 Enterprise 10.0.26200 |
| OS (Linux runs) | WSL2 on the same host: Ubuntu 26.04.1 LTS, kernel 6.18.58.1-microsoft-standard-WSL2; the WSL VM had 24 vCPUs and about 15 GiB of memory |
| CPU | Intel Core i9-12900KF, 1 socket, 16 cores, 24 logical processors |
| RAM | 32 GB (31.8 GiB reported) |
| Physical NIC | 1 Gbps Ethernet |
| Network path | **Loopback (127.0.0.1) for every throughput measurement** |
| Runtime | .NET 10 (SDK 10.0.404 on the host), Native AOT, Release, self-contained win-x64 and linux-x64 |
| GC | Default (workstation, concurrent); no GC settings changed |
| Sampling | `dotnet-counters` every 5 s (10 s in the soak); the first 15 s of each run were skipped |

Every throughput figure below was measured over loopback. The host's physical NIC is 1 Gbps, so no result here
demonstrates 10 Gbps, or any rate above 1 Gbps, over a physical network. Kernel, NIC and switching costs at those rates
are unverified. At the highest rates the load generator and the service competed for the same 24 logical processors.

The RTMP publish load, HTTP load and churn clients are benchmark tools that are not part of the repository. Unless a row
says otherwise, results are from Windows Native AOT with a storage cap (`Hls:MaxTotalStorageMegabytes`) of 4,096 MB.

Builds compared:
- **before**: commit `a05f80a`, prior to remediation.
- **after** (the report's "final" build, or "wp7f" where the final build was not re-run): all Phase 1 changes.

Matrix scenarios ran twice per build, with before and after runs interleaved. Run-to-run spread was about ±5% for
throughput and ±10–15% for CPU at high load.

## Proven connection capacity (RTMP)

| Test | before | after |
|---|---|---|
| 2,000 idle RTMP connections (10 s ramp, 30 s hold) | 2,000/2,000 in both runs; setup p99 3.2–3.6 ms; WS 202–204 MiB | 2,000/2,000 in both runs; setup p99 3.1–3.2 ms; WS 191–198 MiB |
| 2,000 mixed: 1,000 publishing at 11.2 Mbps + 1,000 idle | 1,000 + 1,000 in both runs; 10.64 Gbps | 1,000 + 1,000 in both runs; 10.64 Gbps; setup p99 21.1–23.2 ms |
| 1,000 publishing at 239 kbps (100 s) | 1,000/1,000 in both runs | 1,000/1,000 in both runs |
| 2,000 connections opened at once, idle server | failures in 3 of 8 runs (e.g. 541, 7) | 0 failures; all connected in 0.8 s |
| 2,000 at once during 4.24 Gbps ingest | 213 failures in 1 of 3 runs | 0 failures; 0.8–1.0 s |
| 4,000 at once | 362 and 254 failures | 0 failures; 1.3 s |
| 1,000 publishers from one address, ramped in 0.5 s | 4–6 rate-limited rejections | 0 rejections |

How to read these results:
- **Connected is not the same as publishing.** The largest number of streams actively publishing at once was 1,000,
  and that is the implementation ceiling (below). The 2,000-connection results combine 1,000 publishers with 1,000
  idle connections.
- **The connect bursts were connection tests.** They show that bursts of 2,000–4,000 simultaneous TCP connections
  were accepted, not that 2,000–4,000 streams can publish continuously.
- **The burst improvement** comes from the listen backlog change, described under the implementation limits below.

## Proven throughput

All figures are aggregate across streams, measured on loopback. Paced publishers send at the stated per-stream bitrate.

### Paced RTMP ingest

| Offered load | Build | Steady aggregate ingest | CPU (% of one core) | Allocated | GC pause (% of time) | WS max |
|---|---|---:|---:|---:|---:|---:|
| 100 × 9.97 Mbps (16 GB cap) | before | 0.945 Gbps | 5.8–7.5 | 110 MB/s | 0.06–0.08 | 8,612–8,625 MiB |
| | after | 0.945 Gbps | 11.4–12.5 | 93 MB/s | 0.84–0.88 | 8,117–8,118 MiB |
| 500 × 9.97 Mbps | before | 4.72 Gbps | 76.8–85.9 | 582–629 MB/s | 1.29 | 17,024–18,870 MiB |
| | after | 4.72 Gbps | 76.2–79.3 | 49 MB/s | 1.20 | 8,361–8,467 MiB |
| 1,000 × 9.97 Mbps | before | 9.44 Gbps | 205–213 | 1,194–1,291 MB/s | 3.9–4.3 | 17,821–18,980 MiB |
| | after | 9.44 Gbps | 185–190 | 95–102 MB/s | 2.8–2.9 | 12,101–12,242 MiB |
| 1,000 × 11.2 Mbps + 1,000 idle | before | 10.64 Gbps | 223–262 | 1.46–1.54 GB/s | — | 18,672–19,365 MiB |
| | after | 10.64 Gbps | 228–234 | 0.20–0.21 GB/s | — | 15,606–15,641 MiB |

Qualifications:
- **Storage cap.** At 5 Gbps and above, every run reached the 4,096 MB storage cap and dropped segments, in both
  builds. These rows exercise transport, packaging and storage up to the cap. They do not demonstrate full HLS
  availability at those rates (see the limitations below).
- **1 Gbps CPU.** At 1 Gbps the after build uses more CPU, about 5 points of one core. Pooled segment chunks are
  promoted through gen1 instead of being allocated directly on the LOH. From 5 Gbps upwards, CPU is equal or lower
  and allocation is 6–12× lower.

### Unpaced RTMP ingest ceiling (50 publishers, no pacing)

| Build | Aggregate ingest | CPU (% of one core) | Allocated | GC pause | WS max |
|---|---:|---:|---:|---:|---:|
| before | 50.4–50.5 Gbps | 1,209–1,227 | 5,186–5,220 MB/s | 5.1–5.5% | 9,165–13,994 MiB |
| after | 62.3–62.6 Gbps | 1,267–1,412 | 86–89 MB/s | 2.8–3.1% | 4,671–4,757 MiB |

The load generator shared the host, so this ceiling partly reflects the generator as well as the service.

### HTTP/HLS origin egress

See [HTTP/HLS behaviour](#httphls-behaviour). The highest measured origin egress was 59.4–61.5 Gbps, from 30 stored
streams to 120 hammer clients on loopback. With 1,000 stored streams it was 27.7–27.9 Gbps.

## Memory and runtime behaviour

The memory terms below are measured separately and must not be conflated:

- **Allocated bytes**: the allocation rate.
- **Working set (WS)**: the process's resident memory.
- **Store bytes (`hls.storage.bytes`)**: retained segment footprint.
- **Pool chunks in use**: the 64 KiB buffers that hold segment data.
- **Pending samples (`hls.pending.bytes`)**: media buffered for segments not yet cut.
- **LOH**: the .NET large object heap.

### Allocation and GC

- **Segment allocation.** Segments are now built from pooled 64 KiB chunks instead of one contiguous array.
  - Per-segment allocation fell from 525 KiB–3.8 MiB to under 1 KiB (micro-benchmark).
  - Gen2 collections per 200 segments fell from 35–200 to 1.
- **Paced 10 Gbps.** Allocation fell from 1.19–1.29 GB/s to 95–102 MB/s. GC pause fell from 3.9–4.3% to 2.8–2.9% of
  wall time.
- **LOH from segments (Windows, 40 × 4 Mbps).** The LOH fell from 1.0–1.1 GB to 2 MB.
- **Linux working set (WSL2, 100 × 10 Mbps).** It fell from 4.3 GB to 2.2 GB.

### Memory at 10.64 Gbps (mixed 2,000, run 1)

| Term | before | after (wp7f) |
|---|---:|---:|
| Store (`hls.storage.bytes`, cap 4,096 MB) | 4,095 MB | 4,095 MB |
| Pool chunks in use / free | — | 4,123 MB / 62 MB |
| Pending samples | not measured | 3,321 MB |
| gen2 heap | 373 MB | 4,815 MB (pooled chunks) |
| LOH (of which fragmentation) | 19,779 MB (10,177 MB) | 14,551 MB (8,468 MB) |
| Committed | 20,387 MB | 19,426 MB |
| Working set | 18,672 MB | 15,841 MB |

At high bitrate the remaining LOH is not segment storage. It holds pending sample buffers and RTMP message
reassembly buffers. Both are Phase 2 candidates and were not addressed.

### Store accounting and chunk footprint

- **Footprint, not payload.** The store counts each segment's footprint against the storage limits and
  `hls.storage.bytes`. A media segment's footprint is its whole 64 KiB chunks; an initialization segment's is its
  length. A media segment therefore costs at most 64 KiB more than its payload.
- **Why this changed.** It was corrected during final validation: counting payload let real memory exceed the
  configured limit with many small segments.
- **Verification.** After the correction, the store and the pool agree: 206 MB against 202 MB in the churn test, and
  2.29 GB against 2.29 GB in the soak.
- **Effect.** For megabyte-sized segments the difference is about 1–2%. A segment under 64 KiB counts as 64 KiB, so a
  cap holds fewer tiny segments than before.

### Reader memory budget and admission control

- **What it bounds.** Evicted segments that responses are still sending stay allocated. Their total is bounded by a
  reader budget equal to `Hls:MaxTotalStorageMegabytes`.
- **When it is exhausted.** A new request for a segment that no other response is sending gets `503` with
  `Retry-After: 1` and `Cache-Control: no-store`.
- **Ingest priority.** Ingest is never refused because of readers.
- **Measured effect.** With 2,000 adversarial slow viewers, the working set fell from 4,167 MiB to 3,097 MiB, with
  reader-held memory capped at the limit.

### 30-minute soak (Windows AOT, single run per build)

Load: 300 paced RTMP publishers at 938 kbps (0.28 Gbps aggregate). After 40 s the soak added 500 idle connections,
300 live HTTP players over the 300 streams, and one publisher churn worker. The storage cap was 4,096 MB.

| Metric | Audit baseline (before build, earlier tool) | after (final build) |
|---|---|---|
| Publishers connected / disconnected early | — | 300 / 0 |
| HTTP OK / failures | — | 395,436 / 6 |
| Live segment time-to-last-byte p50 / p99 | — | 0.8 / 18.5 ms |
| Idle connections held | — | 500 / 500 |
| Churn cycles | — | 227,421 (126.3/s; 26,300 client port-exhaustion failures) |
| Working set | 2.58–3.72 GB, fluctuating | 3,042 → 3,053 MiB over six 6-minute windows |
| GC pause | 11% of wall time | 1.02% |
| Allocation rate | 277–592 MB/s | 14.0 MB/s |
| Lock contentions | 590–1,950/s | 6.7/s |
| Process CPU (of one core) | 121.7% (publish phase) | 30.6% |
| Gen2 GCs | — | 16 |
| Threads | 172 → 305 → 257 | 152 → 210, growth slowing |

The final soak's gen2 heap was 2,930 MB, LOH 30–35 MB, free pool chunks 140 MB and pending samples 77 MB. The audit
baseline used an earlier revision of the load tool, so compare it in orders of magnitude only. Each soak ran once,
so variance is not measured.

### Thread count

- **Soak drift.** Threads grew from 152 to 210 in the 30-minute soak, and growth slowed over time. Working set and
  contention stayed flat. The cause is not proven; thread-pool injection under churn is the most likely explanation.
- **Heavy load on Windows AOT.** 1,000 publishers and the HTTP hammer used 336–385 threads, in both builds.

## Publisher churn and concurrency

Churn test: one worker repeatedly connects, publishes and disconnects. About 16,000 cycles complete in about 10 s,
then the client runs out of ephemeral ports (`WSAEADDRINUSE`). The rate is therefore a client limit, not a service
limit. Counters cover the whole run.

| Build | Cycles | Setup p99 | Server CPU (of one core) | Allocated | Lock contentions/s | gen2 GCs | WS max |
|---|---:|---:|---:|---:|---:|---:|---:|
| before | 16,043 and 16,046 | 2.7–2.9 ms | 50.1–50.7% | 289–306 MB/s | 616–925 | 106 | 87–90 MiB |
| after | 15,840 and 16,041 | 0.9 ms | 12.0–12.1% | 8.1–8.2 MB/s | 1.4 | 0 | 329–376 MiB |

Per cycle:
- **Allocation** fell from about 1.8 MB to about 50 KB.
- **CPU** fell by about 76%.
- **Working set** is higher after remediation. The causes are free pool chunks (up to 1/8 of the storage cap),
  retained chunks of ended streams, and log events promoted to gen2 by the asynchronous log queue. All of these are
  bounded.

Concurrency changes behind these results:
- The segment store serves readers from lock-free immutable per-stream views and an expiry index.
- The stream catalog uses persistent snapshots and coalesces reconciliation.
- Authentication keeps a verified-key memo.
- Logging writes through a bounded asynchronous queue.
- Each connection has a single read-deadline timer.

**Publisher setup latency under 10 Gbps.** Setup p99 (connect, handshake and publish) rose from 9.7–12.9 ms to
18.5–26.4 ms in the 9.44 and 10.64 Gbps scenarios. Light scenarios are unchanged. The longest individual GC pause per
5 s window rose from 0.33 s to 0.81 s, although average and total pause fell. The cause has not been conclusively
established.

## HTTP/HLS behaviour

Each scenario ran publishers plus one HTTP load and was measured for 60 s. "Hammer" clients request segments
back-to-back, so they measure the origin's ceiling. "Live players" fetch segments as they are published, so their
load is paced by the clients.

| Stored streams (per-stream bitrate) | Clients | Build | Aggregate egress | Segment TTLB p50 / p99 | Playlist p99 | GC pause |
|---|---|---|---:|---|---:|---:|
| 1 (9.3 Mbps) | 30 hammer | before | 53.2–60.2 Gbps | 15.3–17.6 / 64.8–69.8 ms | 14.6–15.0 ms | 0.14–0.15% |
| | | after | 55.8–60.9 Gbps | 15.2–16.6 / 63.3–69.5 ms | 14.9–15.1 ms | 0.21–0.22% |
| 100 (2.3 Mbps) | 200 hammer | before | 49.5–49.8 Gbps | 36.5 / 88.8–93.1 ms | 53.7–56.0 ms | 7.4–8.4% |
| | | after | 53.0–55.2 Gbps | 32.5–33.2 / 83.4–93.8 ms | 36.3–45.9 ms | 5.1–5.2% |
| 1,000 (239 kbps) | 200 hammer | before | 5.00–5.06 Gbps | 38.6–38.8 / 90.3–91.6 ms | 116.7–126.2 ms | 19.1–19.2% |
| | | after | 27.7–27.9 Gbps | 2.6 / 36.2–37.1 ms | 30.9–31.2 ms | 28.1–28.5% |
| 30 (9.3 Mbps) | 120 hammer | before | 55.5–58.9 Gbps | 76.2–77.8 / 157–191 ms | 54.9–55.7 ms | 1.0–1.3% |
| | | after | 59.4–61.5 Gbps | 72.5–75.5 / 155–160 ms | 56.8–65.1 ms | 1.8% |
| 30 (9.3 Mbps) | 2,000 live players | before | 21.66–21.67 Gbps | 32.3–34.4 / 2,648–2,870 ms | 321–330 ms | 1.6–1.7% |
| | | after | 21.69–21.70 Gbps | 24.0–34.9 / 2,436–2,742 ms | 274–317 ms | 1.8% |

Rows marked "after" for 1, 100 and 30 streams are the wp7f build; the 1,000-stream row is the final build.
TTLB is time to last byte.

Results:
- **Failures.** There were no HTTP failures, and only `200` responses, in any of these runs.
- **1,000 streams.** Before remediation, the store's per-request scans limited throughput. After, GC pause per
  request fell from 58 µs to 16 µs. Pause as a share of wall time nevertheless rose to 28%, because about 5.6× more
  requests are served (about 29,000 per second).
- **2,000 live players.** The 2.4–2.9 s segment p99 is a burst artefact of the test fixture's synchronized segment
  cuts. It is unchanged from the audit.
- **Admission control.** The reader memory budget refused new segment readers with `503` only in the
  memory-pressure shutdown test (see below). It did not refuse any in these runs.

These are origin measurements with load-generator clients on loopback. They do not show that thousands of independent
viewers can be served in a production frontend architecture. The intended design has viewers terminate at frontend
servers, and that tier has not been benchmarked.

## Shutdown behaviour

Graceful stop under load and memory pressure ran once per platform and build:
- **Load:** 30 RTMP publishers at about 5.6 Mbps, with the storage cap at 1,400 MB so the store sits at its cap.
- **Viewers:** after 30 s, 2,000 HTTP viewers spread over the 30 streams, each throttled to read at 100 kbps.
- **Stop:** after 90 s, CTRL+C on Windows or SIGTERM on Linux (WSL2).

| Platform / build | Memory before stop | Server connections (30 RTMP + 2,000 HTTP) | Stop time | Exit code | Warnings/errors after stop | `503` responses |
|---|---|---:|---:|---:|---:|---:|
| Windows AOT, before | WS 2,576 MiB | 2,030 | 14.6 s | 0 | 0 | 0 |
| Windows AOT, after | WS 2,572 MiB | 2,030 | 15.1 s | 0 | 0 | 47 |
| Linux AOT, before | RSS 2,541 MiB | 2,030 | 20.1 s | 0 | 0 | 0 |
| Linux AOT, after | RSS 2,566 MiB | 2,030 | 20.1 s | 0 | 0 | 6 |

Results:
- **RTMP closure.** In every run the RTMP side stopped within about 1 ms of the stop request: all publishers ended
  with `ServerShutdown` and the RTMP listener reported all connections closed.
- **HTTP drain.** The rest of the stop time is the HTTP origin draining in-flight responses. By design it waits up to
  the host `Lifetime:ShutdownTimeout`, which defaults to 30 s.
- **Slow readers.** With readers throttled to 100 kbps, segment responses are still in flight when the stop begins,
  so the stop takes 15–20 s. Both builds take the same time, so this is not a regression.
- **Memory pressure.** The `503` responses in the after build come from the reader memory budget refusing new
  segment readers while the store was at its cap. This is the intended bounded behaviour; the before build had no
  such budget.
- **Without slow readers.** In the 30-minute soak, CTRL+C under active HTTP, idle and churn load exited with code 0
  in 539 ms.

## Managed transcoding pipeline

The figures in this section come from Phase 6, Managed Pipeline Performance and Capacity Engineering (November 2026).
Source of the measurements: the Phase 6 report, `.artifacts/performance/managed-pipeline-phase-6-report.md`, and its
raw evidence under `.artifacts/performance/phase-6/` (gitignored, local to the benchmark host). Section references
such as "Phase 6 report §8" point into that report.

### Pass-through and managed transcoding are different workloads

The Phase 1 figures earlier in this document measure **pass-through ingest**: the service relays the publisher's
encoded H.264/AAC media into HLS without decoding or re-encoding it, and the limits are network, packaging, storage
and memory. **Managed transcoding** decodes and re-encodes every picture (H.264 decode, colour conversion, H.264
encode) and processes the audio (AAC decode, downmix, resample, encode) in managed code on the CPU, so per-session
CPU, not bandwidth, limits ingest.

The Phase 1 results remain valid for pass-through ingest. They must not be read as managed-transcoding capacity.
For example, 10.64 Gbps of ingest or 1,000 publishing streams says nothing about how many streams can be transcoded.

### Benchmark environment

| Item | Value |
|---|---|
| Host | The same single workstation as Phase 1: Intel Core i9-12900KF, 8 P-cores (logical 0–15) and 8 E-cores (logical 16–23), 24 logical processors, base 3.2 GHz; 31.8 GiB RAM |
| OS | Windows 11 Enterprise 10.0.26200, High performance power plan |
| Runtime | .NET 10 (SDK 10.0.404, runtime 10.0.15), **Windows Native AOT**, Release, win-x64; default instruction set (x86-64-v2, no AVX2) |
| GC | Default (workstation, concurrent); no GC settings changed |
| Network path | **Loopback (127.0.0.1)**; the publishers ran on the same host as the service |
| Encoder configuration | CAVLC High profile, P-frames only, 1 reference, constant quality QP 22, peak 8,000 kbit/s, VBV 2×, IDR every 4 s, search range 16, 1 slice |
| Service configuration | Raised within the valid ranges for the benchmark, including `Transcoding:MaxConcurrentSessions=256` (default 16) |

Builds compared:
- **before**: commit `08ff492`, prior to the Phase 6 optimisations.
- **after**: commit `3b09a9f`, the Phase 6 encoder motion-search optimisations. The encoded bitstream is bit-identical to the before
  build for every test clip, so output quality is unchanged.

### Per-session real-time performance

Single session, one process pinned to one P-core, Windows Native AOT; mean of 3 runs per build. The margin is the
frame interval divided by the mean time to decode, colour-convert and encode one picture. A margin above 1 means the
session keeps up with the source in real time. The colour conversion was the BT.709 identity path for these clips.

| Source | before | after | Result (after) |
|---|---:|---:|---|
| 240p25 | 2.97 | 5.10 | Demonstrated |
| 480p30 | 0.98 | 1.73 | Demonstrated |
| 720p30-clean | 0.44 | 0.72 | Not demonstrated |
| 720p30 | 0.39 | 0.66 | Not demonstrated: the first resolution and rate that fails real time |
| 720p60 | 0.21 | 0.35 | Not demonstrated |
| 1080p30 | 0.20 | 0.33 | Not demonstrated |
| 1080p60 | 0.12 | 0.18 | Not demonstrated |

Qualifications:
- **Native AOT only.** JIT figures are reported separately in the Phase 6 report (§6) and are not shown here.
- **One P-core.** On an E-core a session is about 1.6–1.8× slower (Phase 6 report §8).
- **Above real time, frames are dropped.** A session whose margin is below 1 overflows its bounded source queue and
  drops frames. Every drop is counted and logged, and other sessions are not affected (Phase 6 report §18).
- **Colour conversion.** Sources that are not BT.709 cost much more. BT.601 conversion takes 24.6 ms per 240p
  picture and 634 ms per 1080p picture (Phase 6 report §9).

### Demonstrated publisher capacity (managed transcoding)

Paced RTMP publishers with audio and video, 60 s per run, 2 runs unless noted, Windows Native AOT, on the host above.
CPU is the service process, as a percentage of one core (24 logical processors = 2,400%). Latency is the transcoding
latency p99.

| Publishers | CPU (% of one core) | Drops | Latency p99 | Result |
|---|---:|---:|---|---|
| 16 × 240p25 | 432 | 0 | — | Demonstrated |
| 24 × 240p25 | 760–798 | 0 | — | Demonstrated |
| 32 × 240p25 | 998–1,083 | 0 | 0.19 s | Demonstrated |
| 48 × 240p25 | 1,380–1,559 | 0 | 0.19 s | Demonstrated |
| 64 × 240p25 | 2,161–2,225 | 0 | 0.50 s / 0.24 s | Demonstrated with limitations: CPU near saturation (measured ceiling) |
| 8 × 480p30 | 603–607 | 0 | 0.22 s | Demonstrated |
| 16 × 480p30 | about 1,640 | — | 1.5–1.76 s | Not demonstrated as sustained |
| Above 64 × 240p25 or 8 × 480p30 | — | — | — | Not tested |

How to read these results:
- **Sustainable operating point versus measured ceiling.**
  - Up to 48 × 240p25 and 8 × 480p30, every run had 0 drops and no growing queues. These are demonstrated
    sustainable operating points on this host.
  - 64 × 240p25 also had 0 drops, but used 90–93% of all logical processors. Latency p99 doubled in one run, and the
    thread pool queued work (queue length up to 130). This is the measured ceiling on this host, not a sustainable
    operating point.
- **16 × 480p30 fell behind.** It encoded 469.9 and 478 fps of the 480 fps offered. Latency grew (p50 0.55–0.82 s),
  and the source queues held 2.2–2.7 MiB.
- **Before comparison.** The before build failed 48 × 240p25: 16,163 drops and latency p99 33.9 s, in 1 run.
- **Limiting resource.** In every failing case the limit was CPU, specifically encoding. GC pause stayed at or below
  0.62% of time, and no host throttling was observed.
- **Memory.** The working set grows by about 10–11 MiB per 240p25 session; 64 sessions peaked at 714–723 MiB.
- **One workstation.** These are measurements from one i9-12900KF workstation on loopback, not production capacity
  guarantees. The results depend on the source resolution, frame rate and colour, and on how the operating system
  schedules sessions between P-cores and E-cores. The publishers were not pinned.
- **Default concurrency limit.** The default `Transcoding:MaxConcurrentSessions` is 16. The benchmarks raised it to
  256 within the valid range.

**Not demonstrated or not tested for managed transcoding:**
- Real-time 720p30 or higher from a single session.
- Real-time non-BT.709 sources above 240p.
- Linux Native AOT capacity (Linux AOT was smoke-tested only).
- Physical-network throughput.
- More than 400 live HLS viewers of transcoded streams.

## Current implementation limits

These limits are enforced by the current code and configuration validation. A configurable maximum is not a
demonstrated capacity: the tested figures are in the sections above.

| Limit | Current value | Source | Tested up to |
|---|---|---|---|
| Configured publisher credentials | at most **1,000** (startup validation fails beyond) | `RtmpAuthenticationOptions.MaxPublishers` | 1,000 publishing at once |
| Concurrent RTMP connections | default 100; valid 1–10,000 | `Rtmp:MaxConnections` | 2,000 sustained; 4,000 in a connect burst |
| RTMP listen backlog | `max(128, Rtmp:MaxConnections)`; passed as `SOMAXCONN_HINT` on Windows; capped by `net.core.somaxconn` on Linux | `RtmpListener.GetListenBacklog` | 2,000 and 4,000 bursts |
| Concurrently packaged streams | default 100; valid 1–10,000 | `Hls:MaxSessions` | 1,000 |
| Total HLS storage (footprint) | default 2,048 MiB; valid 1–65,536 MiB | `Hls:MaxTotalStorageMegabytes` | 16 GB (1 Gbps run) |
| Per-stream HLS storage | default 128 MiB; valid 1–4,096 MiB | `Hls:MaxSessionStorageMegabytes` | — |
| Reader memory budget | equal to `Hls:MaxTotalStorageMegabytes`; not separately configurable | `InMemoryHlsSegmentStore` | exhausted in the shutdown test |
| Free pooled chunks kept | at most `MaxTotalStorageMegabytes / 8` | `HlsSegmentBufferPool` | — |
| Pending samples | at most `Hls:MaxSegmentSize` (default 32 MiB) per session | `HlsOptions` | 3,321 MB at 10.64 Gbps |
| Concurrent HTTP connections | default 1,000; valid 1–100,000 | `Http:MaxConnections` | 2,000 |
| Authentication waiters | at most 1,024 per remote address, each waiting at most 2 s | `PublisherAuthenticationRateLimiter` | 1,000 from one address |
| Log queue | at most 10,000 events; callers block when full; batches of 1,000; drained for up to 10 s at shutdown | `BoundedAsyncSink` | — |
| Catalog reconciliation | at most one pending, started at least 100 ms apart | `StreamCatalogService.CoalescingWindow` | — |
| Host shutdown timeout | default 30 s; valid 1 s–10 min | `Lifetime:ShutdownTimeout` | 15–20 s used |

Notes:
- **Defaults are conservative.** The shipped defaults (100 RTMP connections, 100 sessions, 2,048 MiB storage, 1,000
  HTTP connections) are far below the tested figures. The benchmarks used a configuration raised within the valid
  ranges.
- **Authentication lookup.** The first successful attempt per key after each credential reload scans all configured
  credentials, about 350 µs with 1,000 of them. Every rejection also scans all credentials. Later successes for the
  same key use the verified-key memo, at about 1 µs. The 1,000-credential ceiling exists because of this scan, and
  raising it requires a configuration-contract change. Phase 1 did not raise it.
- **Reader budget.** The budget and the storage limit are the same value, so in the worst case evicted-but-in-flight
  segments can hold as much memory as the store itself. The report's worst-case model is about
  `2.125 × MaxTotalStorageMegabytes + MaxSessions × MaxSegmentSize + connections × 0.1 MB + GC overhead`.

## 64 GB server capacity objectives (proposed, not proven)

> **These are proposed engineering objectives for a 64 GB server. None of them has been measured or validated.**

| Metric | Proposed objective |
|---|---:|
| Concurrent RTSP publishers | 2,000 |
| HLS origin consumers | 2,000 |
| Total ingest/origin client connections | 4,000 |
| Aggregate ingest throughput | 10 Gbps |
| Aggregate HLS origin egress | 10 Gbps |
| Direct viewer connections to ingest service | 0 |

Assumptions and gaps:
- **One consumer per stream.** The objectives assume one HLS origin consumer per active stream, with frontend
  servers distributing content to viewers. Direct viewer connections are an objective of zero, consistent with the
  intended architecture.
- **"RTSP publishers" is not implemented.** The current service ingests RTMP only. Meeting this objective requires
  either RTSP ingest to be implemented, or the objective to be restated for RTMP.
- **2,000 publishers exceeds the current ceiling.** It is above the 1,000-credential limit, so it cannot be
  configured today. The ceiling must be raised through a configuration-contract change first.
- **10 Gbps each way is theoretical.** A 10 Gbps full-duplex interface can in principle carry 10 Gbps inbound and
  10 Gbps outbound at the same time. Whether the application, NIC, CPU, memory and physical network sustain both
  together is unvalidated: neither direction has been measured on a physical 10 Gbps link.

**Proposed memory envelope (planning assumption only).**
- 48 GB application memory envelope.
- 32 GB HLS storage budget.
- 8 GB reader memory budget.
- The remainder of the 48 GB is for runtime overhead, pending buffers and connections. The remainder of the 64 GB is
  for the OS and safety headroom.

These are provisional planning values, not measured or validated settings. The HLS storage budget and the reader budget
are controls within the overall envelope, not additive independent allocations. Two gaps with the current
implementation:
- **The reader budget cannot be set separately.** The code fixes it equal to the storage budget, so an 8 GB reader
  budget beside 32 GB of storage needs a new setting. This is an open decision in the report.
- **The worst-case bounds do not fit.** 32 GB of storage is within the 65,536 MiB ceiling. But under the worst-case
  model above (2.125 × the storage cap, plus the per-session pending maximum), the current bounds would not keep the
  process inside 48 GB. The envelope is a typical-load planning figure, not a guaranteed limit.

**Retention arithmetic at 10 Gbps** (estimate). 10 Gbps is 1.25 GB of media payload per second:

| Retained media | Approximate payload volume |
|---|---:|
| 10 s | 12.5 GB |
| 20 s | 25 GB |
| 30 s | 37.5 GB |
| 60 s | 75 GB |

These are decimal payload-volume estimates. They exclude chunk allocation overhead, pending samples, indexes, reader-held
segments and other memory consumers.

Actual retention also depends on the HLS settings. The report's model is about (2 × window + 1) × segment duration per
stream. At the default settings, 4 s target segments and window 6, it measured about 65 s of media, which is about
70–81 GB at 10 Gbps. With 2 s segments, window 3 and a GOP of 2 s or less, the report estimates about 17.5 GB of store.

## Known limitations and unverified targets

- **10 Gbps with full HLS retention** cannot be sustained within the tested 32 GB host's memory. Every 10 Gbps run
  hit the 4 GB storage cap and dropped segments. At default settings the retention needed (about 70–81 GB) also
  exceeds the 65,536 MiB configuration ceiling.
- **2,000 HLS streams were not tested.** This needs more than the 1,000-credential and publisher ceiling, which was
  not raised. Streams were measured at 1, 100 and 1,000.
- **RTSP ingest is not implemented or benchmarked.** All ingest and connection results are RTMP.
- **Physical NIC throughput has not been validated.** All throughput was measured on loopback, and the host NIC is
  1 Gbps.
- **The GC pause target (below 3%) was not met** in the 1,000-stream HTTP hammer test: 28% of wall time, dominated
  by gen1 collections.
- **The Linux soak was not run.** Linux evidence covers the AOT smoke tests, a WP1 throughput run, listen-backlog
  checks and the shutdown test, all in WSL2 rather than on a dedicated Linux host.
- **Windows Service hosting and real systemd execution were not validated** under load. The Windows Service file
  sink and service stop path were not run. systemd output is covered only by unit tests and the redirected-console
  smoke test.
- **Churn testing was constrained by client ephemeral ports** (about 16,000 cycles, then `WSAEADDRINUSE`). Sustained
  churn above about 180 cycles/s is unverified.
- **Repetitions were limited.** Only one full 30-minute soak ran per build. Matrix scenarios ran twice per build.
- **Server GC and alternative GC configurations were not tried.** These include `GCConserveMemory` and gen0 budget
  settings; all runs used the default GC.
- **Slow-reader shutdown takes about 15–20 s** while the HTTP origin drains in-flight responses, up to the 30 s
  shutdown timeout.
- **The load generator shared the host**, so the highest rates (the unpaced ceiling and the hammer tests) partly
  reflect client CPU.

Other recorded risks:
- **Publisher setup p99 under 10 Gbps** rose to 18.5–26.4 ms; the cause is not proven.
- **LOH at high bitrate.** Pending samples and RTMP message reassembly produced 7.5–14.5 GB of LOH, mostly
  fragmentation, at 9.4–10.6 Gbps.
- **Higher working set in some scenarios.**
  - 1,000 publishers at 239 kbps: +27% (3.26 GB against 2.56 GB), from gen2 fragmentation.
  - Churn: 330–380 MiB against 82–90 MiB.
- **Thread count.** Drift in the soak (152 to 210), and 336–385 threads under heavy load on Windows AOT.
- **Idle-connection memory was not reduced.** 2,000 idle connections use 191–198 MiB.
- **Logging back-pressure.** If console output blocks, callers block once 10,000 events are queued. A crash loses
  up to 10,000 queued events.

## Future validation

The following tests are required before the proposed 64 GB objectives can be described as proven capacity. None of
them has been run.

1. 2,000 continuously publishing clients, reported separately for RTSP (once implemented) and RTMP. This requires
   the publisher and credential ceiling to be raised first.
2. 2,000 simultaneous HLS origin consumers, one per active stream.
3. Sustained simultaneous ingest and HLS egress at the target rates (10 Gbps each way).
4. Physical 10 Gbps network testing with the load generator on separate hosts.
5. Long-duration soak testing under representative memory pressure, repeated, on Windows and Linux.
6. Frontend ownership and failover testing.
7. Multiple frontend servers without duplicate origin subscriptions.
8. Reader and slow-client behaviour under sustained load, including reader-budget `503` rates and shutdown drain time.
9. Linux (systemd) and Windows Service hosted execution under load.
