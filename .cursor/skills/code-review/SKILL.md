---
name: code-review
description: Review RTSP.IngestService diffs for defects, risks, and evidence-backed findings without auto-editing.
---

# Code review

1. Inspect the diff and surrounding ownership/lifecycle.
2. Prefer findings backed by code paths or failing tests.
3. Do not auto-edit unless the user asked for fixes.
4. Call out secrets, publish leakage of Development config, and cross-project coupling.
5. Distinguish confirmed defects from speculative risks.
