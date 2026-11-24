# NMS agent instructions

Routing entry point for Cursor agents (including JetBrains Rider ACP). Presence of this file does not prove automatic loading.

NMS is a multi-application .NET repository: production apps under `src/`, tests under `tests/`, standalone tools under `tools/`, deployment/local tooling under `deploy/`.

## Before substantive work

1. Read [`.cursor/skills/README.md`](.cursor/skills/README.md) when present.
2. Follow always-on rules under [`.cursor/rules/`](.cursor/rules/), especially `nms-core.mdc`, `repository-layout.mdc`, `configuration-security.mdc`, and `git-discipline.mdc`.
3. Explicitly open relevant skill files before implementing, reviewing, or documenting.
4. Prefer the smallest correct change; do not couple unrelated applications.

## Discovery honesty

- Treat the skill index as a routing guide, not proof of automatic invocation.
- Never claim a rule or skill was automatically loaded unless verified in this session.
