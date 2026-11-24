# NMS Copilot instructions

- Multi-app repository: apps in `src/`, tests in `tests/`, tools in `tools/`, scripts in `deploy/`.
- Do not couple unrelated applications.
- Honor gitignored Development/Production appsettings; never commit or print secrets.
- Development configuration must not appear in publish output.
- Generated evidence belongs under `.artifacts/`; release packages under `release/`.
- Do not stage/commit/push unless explicitly asked.
