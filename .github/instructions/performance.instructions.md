---
applyTo: "**/*.{cs,csproj,json,md}"
---

# Performance engineering

NMS hosts multiple independent applications and tools. Treat hot paths as performance-sensitive when the project's workload warrants it — for example high-throughput networking, packet or datagram processing, parsing/normalization, buffering and pooling, queueing, connection or socket management, durable write paths, and high-frequency logging — while preserving correctness, resource ownership, cancellation, work settlement, lifecycle, and shutdown semantics.

Not every NMS project is a high-throughput packet path. Apply these standards proportionally: establish relevance to the actual workload before optimizing. Where a project ships or uses benchmarks (for example `tools/NetFlowCapture.ReceiveBench`), prefer those verified harnesses over invented methodology.

- Avoid unnecessary allocations, copies, boxing, closures, LINQ, temporary collections, task creation, synchronization, lock contention, and retained large objects or buffers on hot paths.
- Pay particular attention to Gen 0/1/2 pressure, LOH allocations, pinned memory, buffer retention, pooled-resource lifetime, and unbounded growth.
- Make buffer and pooled-resource ownership explicit. Never trade allocation reduction for use-after-return, double-return, premature disposal, or lifetime violations.
- Prefer asynchronous, cancellable socket and stream I/O. Avoid blocking waits, synchronous I/O on high-concurrency paths, fire-and-forget work with unobserved failures, and unnecessary task or continuation overhead.
- Prefer bounded queues and explicit backpressure. Do not remove or weaken backpressure merely to increase apparent throughput. Consider queue depth, admission control, memory growth, starvation, retry amplification, and shutdown draining.
- Consider throughput, bytes/sec, work/sec, packets or records/sec, mean latency, tail latency, CPU utilization, CPU per unit of work, memory, allocation/GC behavior, connection or socket utilization, synchronization, contention, disk/WAL I/O cost, and long-running resource stability.
- Keep high-frequency logs cheap and structured. Avoid constructing expensive diagnostic data when the relevant log level is disabled. Use metrics for high-volume telemetry that would otherwise create excessive log traffic. Never remove security or operationally important diagnostics solely for performance.
- Treat network and I/O performance as an end-to-end property. Distinguish application CPU, socket behavior, protocol overhead, connection churn, peer or broker limits, remote processing, network latency, throttling, storage latency, and external bandwidth constraints.
- Establish a clean and reproducible baseline before changing performance-sensitive code. Use the same build configuration, runtime, architecture, workload, concurrency, topology, and relevant environmental conditions for before/after comparisons.
- Evaluate one meaningful performance change at a time where practical. Require credible measurements before treating an optimization as successful. Do not manufacture expected percentage improvements or accept theoretical micro-optimizations without evidence.
- Do not optimize solely because an allocation, branch, abstraction, LINQ expression, Task, ValueTask, Span, pool, lock, virtual call, or method call exists. First establish that it is relevant to the actual workload and execution path.
- Do not introduce complexity, unsafe code, custom pooling, lock-free synchronization, specialized data structures, or other optimization machinery without a credible performance justification and a clear ownership/correctness model.
- A throughput increase is not automatically an improvement if it materially increases tail latency, CPU cost, memory consumption, queue growth, connection churn, error rate, or resource retention.
- A reduction in CPU or allocations is not automatically an improvement if throughput, latency, correctness, or operational stability deteriorates.
- Preserve work-settlement and acknowledgement semantics, retry behavior, cancellation, disposal, readiness, graceful shutdown, durability contracts, and other production invariants when changing performance-sensitive code. Do not weaken Native AOT / trimming compatibility, configuration validation, or security-relevant diagnostics for speed.
- Keep production implementation, benchmark harness, fake/test peer, synthetic workload generator, and real external endpoint behavior clearly separated when evaluating measurements.
- Do not optimize production code to satisfy an unrealistic benchmark harness. If benchmark behavior is not representative of the production path, identify the benchmark limitation separately.
- Preserve established benchmark contracts, runtime identity, architecture, configuration, and CI expectations. Standalone tools under `tools/` are not solution members unless the repository explicitly includes them — do not invent solution coupling to make a bench "easier."
- Follow the repository's clean/build/run policy for benchmarks and performance tests. Do not use `--no-build` when the harness or project requires a clean build and runtime-identity verification.
- Prefer isolated, deterministic execution for potentially blocking lifecycle, concurrency, or benchmark tests. Report exact pass/fail/skip counts and environmental limitations (for example host OS when cross-compiled binaries cannot be executed locally).
- Do not invent baseline numbers. Prefer newer verified source, test, tool output, or `.artifacts/` evidence over memory. There is no repository-wide `.copilot/PERFORMANCE-CONTEXT.md` checkpoint.
- Do not reset, delete, weaken, or reinterpret an established performance baseline merely because a change performs worse. Determine whether the baseline is still representative before proposing a replacement.
- Performance changes must remain behavior-preserving unless a behavioral change is explicitly intended and reviewed separately.
- If a performance concern cannot be established from repository evidence or credible measurements, describe it as a hypothesis and identify an appropriate measurement plan rather than presenting it as a defect.
- Do not couple unrelated applications when chasing performance. Optimize within the project that owns the hot path; shared speculative frameworks are out of scope unless explicitly requested.
