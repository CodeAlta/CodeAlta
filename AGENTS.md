# CodeAlta — Codex Agent Instructions

An agentic AI coding CLI assistant developed in .NET.

Pre-release project. Remove this line at stable release.

Paths/commands below are relative to this directory.

## Orientation

- Library: `src/CodeAlta/`
- Tests: `src/CodeAlta.Tests/` (MSTest)
- Website: `site/` (Lunet end-user documentation)
- Development rules to keep in sync: `doc/development-guide.md`
- Docs to keep in sync with behavior: `readme.md`, the public website under `site/`, and the internal docs under `doc/` (e.g., `doc/**/*.md`)

## Build & Test

```sh
# from the project root (this folder)
cd src
dotnet build -c Release
dotnet test -c Release

# from the website folder; install once with: dotnet tool install -g lunet
cd ../site
lunet build
```

All tests and the Lunet website build must pass, and docs must be updated before submitting.

## Contribution Rules (Do/Don't)

- Keep diffs focused; avoid drive-by refactors/formatting and unnecessary dependencies.
- Follow existing patterns and naming; prefer clarity over cleverness.
- New/changed behavior requires tests; bug fix = regression test first, then fix.
- All public APIs require XML docs (avoid CS1591) and should document thrown exceptions.
- Do not add static mutable data anywhere in the codebase. Prefer instance-owned state, DI-managed services, or immutable/frozen static data for true constants; never use static mutable collections or process-wide lock maps for runtime/session state.
- Keep frontend, orchestration, plugin, catalog, and hosting boundaries aligned with `doc/development-guide.md`; reusable runtime orchestration should not move into the TUI project.
- Runtime thread/session state should use explicit command/event contracts and single-writer mailbox/actor-style ownership where practical; do not add Akka.NET or another actor framework without a documented spike/decision.

## C# Conventions (Project Defaults)

- Naming: `PascalCase` public/types/namespaces, `camelCase` locals/params, `_camelCase` private fields, `I*` interfaces.
- Style: file-scoped namespaces; `using` outside namespace (`System` first); `var` when the type is obvious.
- Nullability: enabled — respect annotations; use `ArgumentNullException.ThrowIfNull()`; prefer `is null`/`is not null`; don't suppress warnings without a justification comment.
- Exceptions: validate inputs early; throw specific exceptions (e.g., `ArgumentException`/`ArgumentNullException`) with meaningful messages.
- Async: `Async` suffix; no `async void` (except event handlers); follow `doc/development-guide.md` for UI-thread and `ConfigureAwait(false)` rules; consider `ValueTask<T>` on hot paths.

## Performance / AOT / Trimming

- Minimize allocations (`Span<T>`, `stackalloc`, `ArrayPool<T>`, `StringBuilder` in loops).
- Keep code AOT/trimmer-friendly: avoid reflection; prefer source generators; use `[DynamicallyAccessedMembers]` when reflection is unavoidable.
- Use `sealed` for non-inheritable classes; prefer `ReadOnlySpan<char>` for parsing.

## API Design

- Follow .NET guidelines; keep APIs small and hard to misuse.
- Prefer overloads over optional parameters (binary compatibility); consider `Try*` methods alongside throwing versions.
- Mark APIs `[Obsolete("message", error: false)]` before removal once stable (can be skipped while pre-release).

## Git / Pre-Submit

- Commits: commit after each self-contained logical step; imperative subject, < 72 chars; one logical change per commit; reference issues when relevant; don't delete unrelated local files.
- Commit subjects must start with a dotnet-releaser autolabel prefix: `Breaking Change`, `Add`, `Fix`/`Bugfix`, `Enhance`/`Refactor`, `Improve Perf`, `Add`/`Improve`/`Fix`/`Update` followed by `ci`/`doc`/`test`/`example`/`translation`/`accessibility`, or `Update Depend...`/`Bump <dependency>` for dependencies. Put any GitHub issue reference at the end in parentheses, e.g. `Fix parser error (#18)`.
- Checklist: each self-contained step is committed; build+tests pass; docs updated if behavior changed; repository-wide development guidance updated in `doc/development-guide.md` when needed; public APIs have XML docs; changes covered by unit tests.

## Local References

- This project is using several packages with source code that can be available locally for code inspection / documentation:
  - `XenoAtom` libraries (`XenoAtom.Glob`, `XenoAtom.Terminal.UI`...etc.) at `../XenoAtom/` (e.g. `../XenoAtom/XenoAtom.Glob/`).
  - `SharpYaml` at `../SharpYaml/`
  - `Tomlyn` at `../Tomlyn/`
