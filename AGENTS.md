# CodeAlta - Codex Agent Instructions

An agentic AI coding assistant developed in .NET.

Paths/commands below are relative to this directory.

## Orientation

- Frontends: CodeAlta Desktop (`src/CodeAlta`, `alta`) and CodeAlta TUI (`src/CodeAlta.Tui`, `altatui`). Follow the [frontend scope and layer rules](doc/development-guide.md#architecture-rules).
- Tests: `src/CodeAlta.Tests/` (MSTest)
- The in-process agent tool remains `alta`; do not rename neutral `CodeAlta.*` libraries or shared `.alta` state when changing frontend identities.
- Website: `site/` (Lunet end-user documentation)
- Follow the [development guide](doc/development-guide.md) for repository rules. Read the [architecture](doc/architecture.md) before changing boundaries between areas.

## Build & Test

```sh
# from the project root (this folder)
cd src
dotnet build -c Release
dotnet test -c Release

# desktop frontend tests; the build above restores its npm packages
cd CodeAlta/frontend
npm test

# from the website folder; install once with: dotnet tool install -g lunet
cd ../../../site
lunet build
```

After the build, run `dotnet test -c <Configuration> --no-build` and `npm test` in parallel as two `alta job start` jobs. Never let `dotnet test` build while `npm test` runs: the build regenerates the RPC client that the frontend tests import. Follow the [test rules](doc/development-guide.md#tests) before submitting.

The final `--logger` in `src/dotnet-releaser.toml` controls the GitHub .NET summary and replaces the `.runsettings` logger.

### Debug or Release

CodeAlta is developed with CodeAlta, so a CodeAlta that runs on this computer may be a build of this checkout: `src/CodeAlta/bin/<Configuration>/net10.0/alta` or `src/CodeAlta.Tui/bin/<Configuration>/net10.0/altatui`. Its files cannot be replaced while it runs (on Windows the build stops on a locked file), and every build of that configuration writes them: the solution, `CodeAlta.csproj`, and `dotnet test`, whose test projects build both applications.

Before the first build, look at what runs:

```powershell
Get-Process alta, altatui -ErrorAction SilentlyContinue | Select-Object Id, Path
```

```sh
# macOS, Linux
ps -eo pid,args | grep -E '/alta(tui)?( |$)'
```

| A CodeAlta runs from | Build, test and start the developer instance with |
| --- | --- |
| `bin/Release` of this checkout | `Debug` |
| `bin/Debug` of this checkout | `Release` |
| anywhere else (an installed `alta`, another checkout or worktree), or none runs | `Release` for the tests and `Debug` for the developer instance |

Use the selected configuration in `dotnet build`, `dotnet test` and executable paths. Tests that pass in `Debug` count; CI runs them in `Release`.

- Never exit that application to free its files: it is the one the user works in, and often the one you run in.
- Before building or testing, exit the developer instance you started if it runs that configuration.
- Run one build at a time, whatever the configurations: they share `src/CodeAlta/frontend/dist` and the generated RPC client.

## Working on the desktop WebApp

Follow the [developer-instance loop](src/CodeAlta/README.md#developer-instance) and check [which build is in use](#debug-or-release) first.
Always pass `--dev` when starting or exiting the test instance. Never exit the user's instance or change their real profile, which the developer instance shares.
Drive the developer window through `codealta-dev`, not `alta ui activate`.

## Documentation

Follow the [documentation rules](doc/development-guide.md#documentation). In the user guide, put CodeAlta Desktop before CodeAlta TUI, keep the page to about a dozen `##` headings at most, and follow the [screenshot style](doc/site-screenshots.md).

## Contribution Rules (Do/Don't)

- Keep diffs focused; avoid drive-by refactors/formatting and unnecessary dependencies.
- Follow existing patterns and naming; prefer clarity over cleverness.

## C# Conventions (Project Defaults)

- Naming: `PascalCase` public/types/namespaces, `camelCase` locals/params, `_camelCase` private fields, `I*` interfaces.
- Style: file-scoped namespaces; `using` outside namespace (`System` first); `var` when the type is obvious.
- Validation: use `ArgumentNullException.ThrowIfNull()` and specific exceptions with meaningful messages. Prefer `is null`/`is not null`.
- Async: `Async` suffix; no `async void` except event handlers. Consider `ValueTask<T>` on hot paths.

## Performance / AOT / Trimming

- Minimize allocations (`Span<T>`, `stackalloc`, `ArrayPool<T>`, `StringBuilder` in loops).
- Keep code AOT/trimmer-friendly: avoid reflection; prefer source generators; use `[DynamicallyAccessedMembers]` when reflection is unavoidable.
- Use `sealed` for non-inheritable classes; prefer `ReadOnlySpan<char>` for parsing.

## API Design

- Follow .NET guidelines; keep APIs small and hard to misuse.
- Prefer overloads over optional parameters (binary compatibility); consider `Try*` methods alongside throwing versions.
- Mark APIs `[Obsolete("message", error: false)]` before removal.

## Git / Pre-Submit

- Commits: commit after each self-contained logical step; imperative subject, < 72 chars; one logical change per commit; reference issues when relevant; don't delete unrelated local files.
- Commit subjects must start with a dotnet-releaser autolabel prefix: `Breaking Change`, `Add`, `Fix`/`Bugfix`, `Enhance`/`Refactor`, `Improve Perf`, `Add`/`Improve`/`Fix`/`Update` followed by `ci`/`doc`/`test`/`example`/`translation`/`accessibility`, or `Update Depend...`/`Bump <dependency>` for dependencies. Put any GitHub issue reference at the end in parentheses, e.g. `Fix parser error (#18)`.

## Local References

- This project is using several packages with source code that can be available locally for code inspection / documentation:
  - `XenoAtom` libraries (`XenoAtom.Glob`, `XenoAtom.Terminal.UI`...etc.) at `../XenoAtom/` (e.g. `../XenoAtom/XenoAtom.Glob/`).
  - `SharpYaml` at `../SharpYaml/`
  - `Tomlyn` at `../Tomlyn/`
  - `NeoAstra` at `../NeoAstra/` providing the Webview2-based framework
