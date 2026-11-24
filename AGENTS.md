# RTSP.IngestService agent instructions

Routing entry point for Cursor agents (including JetBrains Rider ACP). Presence of this file does not prove automatic loading.

RTSP.IngestService is a single-service .NET repository: the service under `src/`, tests under `tests/`, deployment assets under `deploy/`, documentation under `docs/`.

## Before substantive work

1. Read [`.cursor/skills/README.md`](.cursor/skills/README.md) when present.
2. Follow always-on rules under [`.cursor/rules/`](.cursor/rules/), especially `rtsp-core.mdc`, `repository-layout.mdc`, `configuration-security.mdc`, and `git-discipline.mdc`.
3. Explicitly open relevant skill files before implementing, reviewing, or documenting.
4. Prefer the smallest correct change; avoid unnecessary dependencies.

## Discovery honesty

- Treat the skill index as a routing guide, not proof of automatic invocation.
- Never claim a rule or skill was automatically loaded unless verified in this session.
