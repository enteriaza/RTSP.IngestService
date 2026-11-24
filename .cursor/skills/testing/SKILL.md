---
name: testing
description: Design and fix deterministic RTSP.IngestService regression tests.
---

# Testing

1. Keep tests under `tests/`.
2. Prefer deterministic unit tests; skip lab dependencies cleanly.
3. Fix races by waiting for real invariants or correcting production ownership—not by weakening asserts.
4. Report exact counts; do not claim green if a full suite run failed.
