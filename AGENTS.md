# CodeAlta - Codex Agent Instructions

An agentic AI coding assistant developed in .NET.

Paths/commands below are relative to this directory.

## Orientation

- Frontends: 
    - Webview2-based UI: `src/CodeAlta` (`alta` command)
    - TUI: `src/CodeAlta.Tui/` (`altatui` command)
- **New features are developed for CodeAlta Desktop only.** No new feature is developed for the TUI: do not add a screen, a dialog, a command or a setting to `src/CodeAlta.Tui/`, and do not build a TUI counterpart of a desktop feature, unless the task asks for it.
    - The TUI is kept working. It shares the orchestration, the catalog, the providers, the plugins and the `alta` tool with the desktop application, so a change made there for the desktop can change what the TUI does: build it, keep its tests passing, and fix what the change breaks.
    - A bug of the TUI is still fixed when the task is about it.
- Tests: `src/CodeAlta.Tests/` (MSTest)
- The in-process agent tool remains `alta`; do not rename neutral `CodeAlta.*` libraries or shared `.alta` state when changing frontend identities.
- Website: `site/` (Lunet end-user documentation)
- Development rules to keep in sync: `doc/development-guide.md`
- Docs to keep in sync with behavior: `readme.md`, the public website under `site/`, and the internal docs under `doc/` (e.g., `doc/**/*.md`)

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

All .NET tests, the frontend tests (`npm test`) and the Lunet website build must pass, and docs must be updated before submitting. `npm test` runs every `*.test.ts(x)` file under `src` and its feature folders, including the `*.browser.test.ts` files, which mount real components in headless Edge and are skipped where Edge is not installed. A frontend test that no longer matches the app is fixed or removed, never left failing.

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
| anywhere else (an installed `alta`, another checkout or worktree), or none runs | `Release` for the tests and `Debug` for the developer instance, as this file writes them |

Use that configuration in every command of this file: `-c <Configuration>` for `dotnet build` and `dotnet test`, and `bin/<Configuration>` in the path of an executable. Tests that pass in `Debug` count; CI runs them in `Release`.

- Never exit that application to free its files: it is the one the user works in, and often the one you run in.
- The developer instance you started (below) is yours: exit it before a build or a test run of its configuration.
- Run one build at a time, whatever the configurations: they share `src/CodeAlta/frontend/dist` and the generated RPC client.

## Working on the desktop WebApp

The desktop UI (`src/CodeAlta`, React frontend in `src/CodeAlta/frontend`) runs in a native window. Drive the running window through its MCP server instead of guessing from the source: look at it, click in it, read its DOM and console.

**1. Build.** `dotnet build CodeAlta/CodeAlta.csproj` from `src` builds the host and the frontend in `Debug`; add `-c Release` when `Debug` is the configuration to leave alone (see "Debug or Release"). The build restores npm packages and regenerates the typed RPC client `src/CodeAlta/obj/neoastra/neoastra.ts`. The frontend alone is checked from `src/CodeAlta/frontend` with `node node_modules/typescript/bin/tsc --noEmit` and one test file with `node node_modules/tsx/dist/cli.mjs --test src/<name>.test.ts`, or `src/<feature>/<name>.test.ts` for a feature folder (`npm test` runs them all). The `src/*.browser.test.ts` files start their own Edge; do not use them to look at the app.

**2. Launch the developer instance.** `--dev` starts a second CodeAlta beside the normal one (see "Developer instance" below). What makes an instance the normal or the developer one is `--dev`, not its build: either can be the `Debug` or the `Release` one. From the repository root, with the configuration you built (PowerShell; `src/CodeAlta/bin/Debug/net10.0/alta --dev` on macOS and Linux):

```powershell
Start-Process src\CodeAlta\bin\Debug\net10.0\alta.exe -ArgumentList "--dev" -WorkingDirectory (Get-Location)
```

The native window is titled **CodeAlta (dev)** and the page shows a **DEV** tag beside its name (`document.title` stays `CodeAlta`). Its MCP server listens at `http://127.0.0.1:2583/mcp` (the normal instance has port 2582); `~/.alta/dev/mcp_url.txt` holds that address while the instance runs, and `--mcp-port <port>` asks for another port. `alta.exe` is a windowed executable: it has no console. Started from a shell, it runs the window in a second `alta.exe` process and exits once the window is shown, with code 0; a non-zero exit code is a startup failure, and the log is under `%LOCALAPPDATA%\CodeAlta\desktop-dev\logs`. A second developer instance shows the window of the running one and exits at once. Add `--wait` to keep the window in the process you started.

**3. Connect.** Two checked-in files register that server as `codealta-dev`, over HTTP: `.mcp.json` for agents that read project MCP configuration (Claude Code and others), and `.alta/mcp.json` for CodeAlta itself. Nothing has to be installed. A client that connects when it starts needs the app running first, or a reconnection afterwards; a restart of the app needs neither, because the server keeps no session.

- `take_snapshot` (the elements of the page, each with a `uid`) and `take_screenshot` show the window; `click`, `fill`, `type_text` and `press_key` act on it; `evaluate_script` runs JavaScript in the page; `list_console_messages` reads its console. The tools have the names and the arguments of [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp).
- `take_screenshot` with a `filePath` saves the picture instead of returning it: in the folder of a project, or in the folder of the tools (`%LOCALAPPDATA%\CodeAlta\desktop-dev\ui`).
- `alta` runs the commands of that instance, for example `{"args": ["session", "list"]}`: the caller belongs to no session.
- The title bar's native caption buttons are not part of the page, so they are not in screenshots.
- In a CodeAlta session the server starts inactive, or unavailable while nothing listens: once the developer instance runs, `alta mcp activate codealta-dev` registers its tools (`mcp__codealta_dev__…`) in the running turn, and they stay for the next turns and across restarts of that instance. In a session of the Claude Code provider they are named `mcp__codealta__mcp__codealta_dev__…`; Claude Code may also list the server of `.mcp.json` by itself (`mcp__codealta-dev__…`), which is the same window.
- A session of CodeAlta Desktop has the same tools for the window it runs in, without any server: `alta ui activate`.
- On Windows, WebView2 still opens a DevTools port when `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222` is set before the start, for what these tools do not cover (performance traces, CSS inspection).

**4. Change, rebuild, look again.** The files of the developer instance are locked while it runs, and closing its window may only hide it. Ask it to exit, with the `alta` of either build:

```powershell
Start-Process src\CodeAlta\bin\Debug\net10.0\alta.exe -ArgumentList "--dev", "--exit"
```

It is gone within a few seconds, and `~/.alta/dev/mcp_url.txt` with it. While sessions run there or files have unsaved edits, it asks in its window first. Always pass `--dev`: `--exit` alone exits the normal instance, which may be the one you are running in, and never end every `alta` process for the same reason.

Then build and launch again. Check both themes and a narrow window (`resize_page`) when a change is visual.

**5. Mind what is shared.** The developer instance has its own sessions, so sending prompts there is safe and its sessions are disposable. Everything else is the user's real profile: do not change settings, providers, prompts or skills, and do not rename, archive or add projects, unless the task asks for it. A window started without `--dev` is the normal instance, with the user's real sessions: look and navigate freely, but do not send prompts, rename or delete sessions there unless asked, and leave it as you found it.

### Developer instance

Only one CodeAlta runs on a profile, because two processes must not write the same session files. `--dev` (`alta --dev` for the desktop app, `altatui --dev` for the terminal UI) starts a second one on the same `~/.alta`:

| Shared with the normal instance | Its own, under `~/.alta/dev/` |
| --- | --- |
| `config.toml`, providers and their credentials (`auth/`), `mcp.json`, prompts, skills, plugins and their data (`plugin-data/`), color schemes (`color-schemes/`), the project catalog (`projects/`) | `sessions/` (journals, pasted images), `data/alta.sqlite3` (the application database, its `backups/`), `ui-state.yaml`, `saved_prompts/`, `logs/`, `alta.lock` |

The desktop developer instance also has its own WebView data (`%LOCALAPPDATA%\CodeAlta\desktop-dev`: open tabs, drafts, theme, and the placement of its window) and its own MCP port. One developer instance runs at a time, terminal or desktop. It leaves the coordinator `~/.alta/AGENTS.md` as the normal instance wrote it, and on its first run takes over the normal instance's per-project provider/model preferences.

This is how CodeAlta is developed with CodeAlta: you run in the normal instance (desktop or terminal, any released or built `alta`/`altatui` that has this branch's MCP support), build the repository in a configuration that instance does not run from, start that `alta.exe --dev`, and drive that window through its MCP server (`codealta-dev`). The terminal UI is checked the same way with `altatui --dev` in a separate console.

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
- Nullability: enabled - respect annotations; use `ArgumentNullException.ThrowIfNull()`; prefer `is null`/`is not null`; don't suppress warnings without a justification comment.
- Exceptions: validate inputs early; throw specific exceptions (e.g., `ArgumentException`/`ArgumentNullException`) with meaningful messages.
- Async: `Async` suffix; no `async void` (except event handlers); follow `doc/development-guide.md` for UI-thread and `ConfigureAwait(false)` rules; consider `ValueTask<T>` on hot paths.

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
- Checklist: each self-contained step is committed; build+tests pass; docs updated if behavior changed; repository-wide development guidance updated in `doc/development-guide.md` when needed; public APIs have XML docs; changes covered by unit tests.

## Local References

- This project is using several packages with source code that can be available locally for code inspection / documentation:
  - `XenoAtom` libraries (`XenoAtom.Glob`, `XenoAtom.Terminal.UI`...etc.) at `../XenoAtom/` (e.g. `../XenoAtom/XenoAtom.Glob/`).
  - `SharpYaml` at `../SharpYaml/`
  - `Tomlyn` at `../Tomlyn/`
  - `NeoAstra` at `../NeoAstra/` providing the Webview2-based framework
