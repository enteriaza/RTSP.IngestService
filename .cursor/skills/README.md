# NMS Cursor skills index

Project-local Agent Skills live under `.cursor/skills/<skill-name>/SKILL.md`.

- Core rules: [`.cursor/rules/nms-core.mdc`](../rules/nms-core.mdc)
- Layout: [`.cursor/rules/repository-layout.mdc`](../rules/repository-layout.mdc)
- Entry: [`AGENTS.md`](../../AGENTS.md)

## Skills

| Skill | Use when |
|-------|----------|
| `code-review` | Reviewing diffs for defects and evidence-backed findings without auto-editing |
| `testing` | Designing or fixing deterministic regression tests |
| `documentation` | Docs-only passes that must not change behavior |
| `implementation-and-verification` | End-to-end feature/bugfix from inspect → verify → report |
| `configuration-and-security` | Options, validation, credentials, publish exclusion |
| `performance-engineering` | Hot-path / throughput work with measured evidence |

Read the matching `SKILL.md` before substantive work. Combine skills when appropriate.
