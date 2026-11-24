---
name: performance-engineering
description: Measure and improve NMS throughput and efficiency with evidence.
---

# Performance engineering

1. Establish a baseline with existing tools (for example `tools/NetFlowCapture.ReceiveBench`) before changing hot paths.
2. Prefer allocation, contention, and I/O evidence over intuition.
3. Do not sacrifice correctness, ownership, or shutdown safety for speed.
4. Report measured deltas and limitations honestly.
