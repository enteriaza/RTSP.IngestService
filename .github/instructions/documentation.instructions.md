---
applyTo: "**/*.{cs,md}"
---

# Documentation standards

## Required standard for C# source

The standard below applies to every hand-maintained `.cs` file in every project in `RTSP.slnx` — the production service and its tests. It excludes compiler/source-generated output and `bin/`/`obj/`.

### File headers

- Every `.cs` file must start with a descriptive `//` header comment, placed before `using` directives and the namespace declaration.
- The header must explain the purpose of the file, its responsibilities and, where useful, its role within the containing project or subsystem. Include important behavioural or architectural constraints when they matter to a reader who has not yet opened the types in the file.
- Headers must be concise, accurate and specific to the file. A filename-only banner, a generic statement that would fit any file, copyright boilerplate, dates, author attribution, audit history, milestone/phase/approval codes, or descriptions of intended future functionality are not acceptable. Assembly copyright is emitted from `Directory.Build.targets` when present, not from per-file headers.

### XML documentation coverage

- Every declaration that can receive XML documentation must be documented, regardless of accessibility: public, internal, protected, private, and explicit interface implementations.
- This includes classes, structs, records (including positional records), interfaces, enums, enum members, delegates, nested types, constructors and static constructors, finalizers, methods, properties, indexers, fields and constants, events, operators and conversion operators.
- Do not omit a declaration because it is private, internal, test-only, benchmark-only, or not part of the public API.
- Local variables, local functions, lambdas and top-level statements cannot receive XML documentation and are out of scope.
- Test projects are in scope: document test classes, fixtures, fakes, builders, helpers and every test method. A test method's documentation states the specific behaviour or invariant verified; for a regression test, the failure scenario it guards against; for a parameterized test, what the data cases cover.

### Accuracy and relevance

- Documentation is an engineering contract, not tag counting or warning suppression. Base claims on the actual implementation and verified repository usage (callers, lifecycle, error and cancellation paths, tests), not on names, parameter names, existing comments, or assumptions.
- Documentation must not describe intended behaviour the implementation does not provide, make assumptions about external dependencies, claim guarantees that are not enforced, repeat the declaration name without adding information, or contain generic filler.
- Where behaviour is conditional, asynchronous, concurrent, stateful or configuration-dependent, document the relevant conditions accurately.
- Explain purpose, observable behaviour, invariants, parameters, returns, exceptions, lifecycle, ownership, threading, cancellation, performance, logging, I/O and side effects where applicable and supported by the implementation or verified repository evidence.
- Be especially precise about delivery and persistence semantics: do not claim exactly-once delivery where only at-least-once exists, durable acknowledgement where only in-memory acceptance occurs, automatic recovery where state is volatile, thread safety that has not been verified, or full protocol compliance where the implementation has exclusions. State deliberate limitations explicitly.
- Distinguish implemented behaviour from planned or aspirational behaviour in both XML and Markdown documentation. Prefer stating gaps explicitly over guessing.
- If behaviour remains ambiguous after investigation, document only what can be substantiated and report the ambiguity rather than inventing an explanation. If a code defect is discovered during a documentation pass, report it; do not silently fix it in a documentation-only change.

### Tags

- Use the tags relevant to the declaration: `<summary>` for every declaration; `<param>` for every actual parameter (including positional record and primary-constructor parameters); `<typeparam>` for every generic type parameter; `<returns>` for returned values (including the result of `Task<T>`/`ValueTask<T>`); `<value>` for properties and indexers where it adds information; `<exception>` only for established thrown-exception contracts; `<remarks>` and `<seealso>` only when they add substantive information.
- Do not add meaningless tags to satisfy a checklist, do not repeat the summary in remarks, and do not use `<returns>` on properties.
- Document side effects, validation rules, cancellation behaviour, ownership and disposal responsibility, buffer/`Span<T>`/`Memory<T>` lifetimes, thread-safety characteristics, queue/channel ordering, capacity, completion and backpressure, and failure conditions where applicable.
- Treat source-generated and framework-integrated declarations according to their actual semantic contract. Distinguish thrown exceptions from exception parameters, logged exceptions, inspected exceptions, caught exceptions, and failures represented through results or return values.
- For generated logging, document declared severity, event semantics, and meaningful structured properties only when established by the declaration or repository conventions. Do not imply that logs are always emitted when filtering can suppress them.

### Inherited documentation

- Use `<inheritdoc/>` only when the inherited documentation genuinely and completely describes the implementation.
- If an override or interface implementation adds behaviour, constraints, side effects or failure conditions (for example a `Dispose` that drains work, a `ToString` with a specific format, or an `ExecuteAsync` with a specific loop), write documentation that describes them instead of relying on an incomplete inherited description.

### Partial declarations

- For a partial type, the compiler concatenates the XML documentation of every part into one entry. Document the type on exactly one part; additional parts must not carry their own `<summary>` (or any other single-occurrence element), and a plain `//` comment may describe what a part contains.
- For a partial method or property, the compiler uses the implementing part's documentation when it has any and otherwise the defining part's. Document one part only.

### XML correctness

- XML must be well-formed: no duplicated or unclosed tags; escape `<`, `>` and `&` in prose.
- `cref` references must resolve to real symbols. Use `{T}` syntax for generic types, and include parameter types when referencing overloaded methods (an ambiguous `cref` is a build error). A `cref` must be resolvable from the documenting assembly; use `<c>…</c>` for symbols that are not accessible there (for example private members of another assembly).
- `<param>`, `<paramref>`, `<typeparam>` and `<typeparamref>` names must match actual parameters and type parameters.
- Use `<see cref>`, `<seealso>`, and `<c>` when they materially improve understanding. Do not add references merely for decoration.

### Maintenance

- Documentation is part of the source code. Whenever a C# declaration or its behaviour changes, review and update its XML documentation and the file header in the same change.
- New C# files and new declarations must meet this standard from creation.
- Preserve existing accurate and useful XML documentation. Improve documentation when it is vague, incomplete, misleading, outdated or technically incorrect rather than deleting it. Do not rewrite good documentation merely for stylistic preference.

## Validation

Documentation changes must be validated with the repository's existing mechanisms:

- **Compiler validation.** The repository-root `Directory.Build.props` enables `GenerateDocumentationFile` for all projects, and every project sets `TreatWarningsAsErrors`. Malformed XML, unresolved or ambiguous `cref`s, and `param`/`typeparam` mismatches therefore fail the build, and `CS1591` fails the build for undocumented externally visible members. `CS1591` does not cover private or internal members, so a successful build alone does not prove complete coverage.
- **Review.** There is no mechanical source-level documentation audit in this repository. File headers, documentation of private and internal members, placeholder text and semantic accuracy must be checked by review.

Expected results before a documentation change is considered complete:

- Every C# file has a meaningful header.
- Every documentable declaration has relevant XML documentation.
- XML documentation is syntactically valid, and `cref`, `param` and `typeparam` references are valid.
- `dotnet build RTSP.slnx --configuration Release` succeeds without documentation-related warnings or errors.
- Review confirms the file-header and member-documentation requirements above for private and internal members.

Do not weaken build settings or analyzers to make documentation checks pass. Never hide documentation diagnostics with suppressions, `NoWarn`, exclusions, or analyzer configuration changes. Resolve the underlying documentation issue instead.

## Scope and change discipline

- Do not alter behaviour, API design, namespaces, signatures, accessibility, tests, source-generation declarations, logging behaviour, configuration, analyzer settings, or production logic merely to improve documentation.
- Documentation changes must remain scope-limited to the explicitly selected target file, or, for a project-wide documentation audit, the explicitly selected project tree, unless the user explicitly requests a broader documentation change (for example via `.github/prompts/project-documentation.prompt.md`). Do not modify unrelated source files merely to improve documentation consistency across applications.
- Documentation-only passes build the target project and do not run tests unless explicitly requested.
- When reviewing documentation-sensitive changes, confirm that no executable behaviour changed and that useful existing documentation was not unnecessarily removed.

## Terminology and repository context

- Follow established terminology and formatting in `.github/copilot-instructions.md`, `AGENTS.md`, `.github/instructions/csharp.instructions.md`, and the conventions of the project under documentation. Prefer project-local vocabulary (options section names, RID/release layout, RTMP/HLS/fMP4 terms) when documenting that project; do not force one application's terminology onto another.
- The repository contains one service (`src/RTSP.IngestService`), its tests (`tests/`), deployment assets (`deploy/`) and documentation (`docs/`). Markdown under `docs/`, `deploy/`, and repository-level READMEs must stay aligned with implemented layout and verified commands — do not invent paths, package names, or operational procedures.
- Never document, echo, or embed credentials, stream keys, or other secrets from local Development/Production configuration files.

## Related workflows

- The reusable single-file documentation workflow is `.github/prompts/documentation.prompt.md`.
- The reusable project-wide production documentation audit workflow is `.github/prompts/project-documentation.prompt.md`.
- Detailed documentation methodology is `.github/skills/documentation/SKILL.md`.
