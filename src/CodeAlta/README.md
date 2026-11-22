# CodeAlta Desktop

This project builds CodeAlta Desktop: the .NET tool package `CodeAlta`, command `alta`.

This page is for people who build and change the app. To use it, see the
[user documentation](https://codealta.io) and
[Desktop and TUI](https://codealta.io/docs/desktop-and-tui/). How the app behaves in detail
(the host, the RPC between host and page, the page, windows, security boundaries) is described
in [`doc/desktop.md`](../../doc/desktop.md).

## How it is built

- A native window from [NeoAstra](https://www.nuget.org/packages/NeoAstra) (the version pinned in
  `src/Directory.Packages.props`) hosts the web view
  of the operating system: WebView2 on Windows, the system web view on macOS, WebKitGTK on Linux.
- The page is React with strict TypeScript, built by Vite from `frontend/`.
- The page calls the .NET host through generated RPC. The services are in `Desktop/Rpc`; the build
  generates the typed client under `obj/neoastra`.
- The host is the same as the one of CodeAlta TUI: `CodeAlta.Orchestration`, `CodeAlta.Agent`,
  `CodeAlta.Catalog`, the plugin runtime and the built-in MCP, Git and Statistics plugins.
- The window can be seen and driven with tools (`Desktop/Ui`, over the browser automation of NeoAstra):
  by its own sessions, and by other applications through its MCP server (`Desktop/Mcp`).
- Terminals run behind the pseudo-terminal of the system (ConPTY on Windows, a pty on macOS and
  Linux), started by the host in `Desktop/Terminals`. The page shows them with
  [xterm.js](https://xtermjs.org) and the CaskaydiaCove Nerd Font (SIL Open Font License 1.1), both
  packaged in the tool.

The built page is packaged in the tool. The installed app has no UI server and loads nothing from
the network. Node and npm are needed only to build.

## Build and run

Install the .NET 10 SDK and a current Node.js. Before building, check
[Debug or Release](../../AGENTS.md#debug-or-release): never build over a running copy from this
checkout. Build the solution from `src`:

```powershell
cd src
dotnet build -c Release
```

The build restores the npm packages of `frontend/` and generates the RPC client under
`obj/neoastra/`. To build only Desktop from `src` (choose `-c Release` instead if Debug is in use):

```powershell
cd src
dotnet build CodeAlta/CodeAlta.csproj -c Debug
```

Build outputs use the standard SDK layout, `bin/<Configuration>/net10.0` and
`obj/<Configuration>/net10.0`.

To check just the frontend after the build, run these from `src/CodeAlta/frontend`:

```sh
node node_modules/typescript/bin/tsc --noEmit
node node_modules/tsx/dist/cli.mjs --test src/<feature>/<name>.test.ts
```

Use the actual test path (`src/<name>.test.ts` for a top-level test). Browser tests launch their
own Edge, not the app; use the developer instance to check the real window.

## Developer instance

[Build the project](#build-and-run) first, then test it in a second window, beside the normal
app that runs the agents. Use `--dev` to start and exit it; the flag, not the build configuration,
selects the instance.

### Start

From the repository root, use the executable of the configuration you built. On Windows, for
a Debug build:

```powershell
Start-Process src\CodeAlta\bin\Debug\net10.0\alta.exe -ArgumentList "--dev" -WorkingDirectory (Get-Location)
```

On macOS and Linux, run `src/CodeAlta/bin/Debug/net10.0/alta --dev`.
The native window is titled **CodeAlta (dev)** and the page shows a **DEV** tag
(`document.title` stays `CodeAlta`). The default MCP port is 2583, not the normal app's 2582.
`~/.alta/dev/mcp_url.txt` holds its address while it runs. Starting it again shows the running
developer window, rather than opening another one.

- On Windows, omit `-Wait`, `-NoNewWindow` and `--wait` from the detached launch. Avoid
  changing its environment or redirecting output: the window can inherit the shell tool's
  capture handles and keep it waiting after the launcher exits.
- For an isolated environment with logs, run a bounded job:
  `alta job start --command "pwsh -NoProfile -File <launcher.ps1>" --title "Launch isolated desktop" --timeout 60`.
  In the launcher, make each captured standard handle non-inheritable
  (`SetHandleInformation(GetStdHandle(n), 1, 0)` for `n` = -10, -11, -12, through `Add-Type`).
  Then `Start-Process` with the allowlisted environment and distinct `-RedirectStandardOutput`
  and `-RedirectStandardError` files, without `-Wait`. Redirection and a background job alone do
  not fix a pipe leak. The launcher ends in seconds; diagnose a job that stays live rather than
  starting another.
- In a separate short shell call, confirm the process and path (`Get-Process alta | Select-Object Id, Path`)
  and that `(Get-NetTCPConnection -LocalPort 2583 -State Listen).OwningProcess` is its PID.
  The port can take a few seconds to listen. Check the end of the log under
  `%LOCALAPPDATA%\CodeAlta\desktop-dev\logs` when diagnosing startup.
- A fresh or isolated profile opens Settings on **Providers**. Use **Skip**, not a provider
  configuration change, to leave the setup guide.

### Connect

The repository's `.mcp.json` (Claude Code and other clients) and `.alta/mcp.json` (CodeAlta)
register `http://127.0.0.1:2583/mcp` as `codealta-dev`. A client that connects at startup needs
the app running or a reconnection afterwards. The server is stateless, so later app restarts
need neither.

- In CodeAlta, run `alta mcp activate codealta-dev` after launch. Tools named
  `mcp__codealta_dev__…` join the current turn and stay available across turns and app restarts.
  In the Claude Code provider the prefix is `mcp__codealta__mcp__codealta_dev__…`; Claude Code
  can also list `mcp__codealta-dev__…` from `.mcp.json` for the same window.
- Drive this window through `codealta-dev` only. `alta ui activate` would drive the window that
  hosts the session, not the test window. The MCP server's `alta` tool runs commands in the test
  instance without a calling session, for example `{"args": ["session", "list"]}`.

### Inspect the window

Use `take_snapshot` to find elements by `uid`, `take_screenshot` to see the layout, `click`,
`fill`, `type_text` and `press_key` to exercise controls, `evaluate_script` for the DOM, and
`list_console_messages` for errors. These use the
[Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp) tool arguments.
On Windows, set `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222` before
launch only if you need DevTools features such as performance traces or CSS inspection.

- For a visual change, check both themes and a narrow layout. Use `resize_page` with 900×650
  CSS pixels for the narrow check (Settings has about 430 px of content width there). Restore a
  large page for ordinary inspection and screenshots: the desktop site Settings and Statistics
  images are 2400×1500 device pixels, from a 1600×1000 CSS-pixel page at 150% scale. Check the
  actual scale and image dimensions. Native caption buttons are not in page screenshots.
- To save a screenshot, pass `filePath` in the project or the tools' folder
  (`%LOCALAPPDATA%\CodeAlta\desktop-dev\ui`). In an isolated fixture save under its own
  `<browser-root>/ui` instead, and record that path with the results.
- Use `evaluate_script` to navigate (e.g. `nav[aria-label="Settings pages"] button`), wait for the
  control and measure layout (`scrollWidth > clientWidth` for horizontal overflow). Use
  `scrollIntoView({ block: "nearest" })` to avoid scrolling the dialog itself. Use `fill` for
  native selects and check keyboard interaction with `press_key` or `type_text`. Verify changes
  on disk only with an isolated profile.

### Exit before rebuilding

Closing the window may only hide it. From the repository root, ask the developer instance you
started to exit, with the `alta` of either build:

```powershell
Start-Process src\CodeAlta\bin\Debug\net10.0\alta.exe -ArgumentList "--dev", "--exit"
```

On macOS and Linux, run `src/CodeAlta/bin/Debug/net10.0/alta --dev --exit`.
Never omit `--dev` or stop all `alta` processes: that would exit the user's app. After checking
the port owner, `press_key` with `Control+q` through `codealta-dev` is another way to exit the
test window. It asks first about running sessions and unsaved files. Confirm the port is no
longer listening before rebuilding, then launch and inspect again.

### Profile and safety

One developer instance runs at a time: `alta --dev` or, to check the TUI in a separate console,
`altatui --dev`. Both use `~/.alta` with a separate state root:

| Shared with the normal instance | Separate, under `~/.alta/dev/` |
| --- | --- |
| `config.toml`, providers and credentials (`auth/`), `mcp.json`, prompts, skills, plugins and their `plugin-data/`, color schemes (`color-schemes/`), projects (`projects/`) and spaces (`spaces/`) | `sessions/` (journals and pasted images), `scratchpad/`, `saved_statistics_after_deleted_sessions/`, `data/alta.sqlite3` and backups, `ui-state.yaml`, `saved_prompts/`, `logs/`, `alta.lock` |

Desktop keeps separate WebView data (tabs, drafts, theme and placement) in
`%LOCALAPPDATA%\CodeAlta\desktop-dev` on Windows. The developer instance leaves the coordinator's
`~/.alta/AGENTS.md` alone and takes over the per-project provider/model preferences on its first
run. Startup cleanup removes only the owning instance's scratchpad contents after safety checks.

Use developer sessions, not the user's real sessions, for test prompts. Do not change shared
settings, providers, prompts, skills or projects unless the task asks for it. For destructive
checks use isolated temporary roots and synthetic data, never either real profile.

## Command line

| Command | Effect |
| --- | --- |
| `alta` | Starts the app for the current folder. If it is already running, shows its window. |
| `alta --dev` | Starts the developer instance. |
| `alta --wait` | Starts the app and keeps the terminal until it exits, alone or with `--dev`. |
| `alta --exit` | Asks the running app to exit. It asks first about unsaved files and running sessions. |
| `--mcp-port <port>` | With a start that opens the window: the port of the MCP server (0 takes a free one). |
| `--mcp-host <address>` | With a start that opens the window: the address the MCP server listens on, `localhost`, an IP address or a host name of this computer (its Tailscale name, for example). |
| `alta --version` | Prints the version. |
| `alta --help` | Prints the options. |

`--help` and `--version` do not start the host or create any storage.

Started from a terminal, `alta` and `alta --dev` run the window in a second process and give the
prompt back once it is shown: exit code 0 then, or the code the app failed to start with. `--wait`
keeps the app in the process you started; so does a start under a debugger. See
[Started from a terminal](../../doc/desktop.md#started-from-a-terminal).

Set `CODEALTA_DISABLE_PLUGINS=1` to start without plugins.

`alta --help` also lists the options that start the app on explicit directories instead of
`~/.alta` (`--data-root`, `--catalog-root`, `--allow-owned-host` and others). They are for tests and
for running on a copy of a profile. See
[One instance per profile](../../doc/desktop.md#one-instance-per-profile).

## Browser demo

The frontend has an in-memory demo that runs in a browser. It needs no .NET host, credentials or
profile, and it never sends a provider request:

```powershell
cd src/CodeAlta/frontend
npm ci          # first checkout only
npm run demo    # opens http://127.0.0.1:5173
```

The demo needs the generated client. If `../obj/neoastra` does not exist, build the project once
(see above), then run `npm ci`.

Select projects and sessions, send messages, open Settings and switch themes. Demo messages
disappear on refresh. `npm run build:demo` writes the same demo as static files under `dist/`.
`npm run build` builds the page the app uses. The packaged app never includes the demo backend.

## Frontend notes

- **Spaces**: `frontend/src/spaces/` holds the groups of projects the window shows one at a time.
  The window is given the catalog as the shown space has it, and each space has its own tabs.
- **Tests**: `npm test` in `frontend/` runs every `*.test.ts` and `*.test.tsx` under `src/`,
  reporting failures, totals and diagnostics; `npm run test:verbose` shows every test name.
  Both cap file concurrency at two. The `*.browser.test.ts` files mount components in headless
  Edge (or another Chromium selected with `CODEALTA_TEST_BROWSER`) and skip if no browser is
  available. `waitForBrowserPort` has a five-second wall-clock deadline, scaled by `testLimit`,
  and handles partial port files, spawn errors and child exit. Keep stderr piped, clean up only
  your browser with `endBrowser`, and diagnose startup failures rather than retrying or skipping.
- **Color schemes**: `frontend/src/colorSchemes.gen.css` and `colorSchemes.gen.ts` are generated by
  `dotnet run --project src/CodeAlta.ColorSchemes.Generator`. Run it again when the scheme recipes
  of XenoAtom.Terminal.UI or the Blueprint palette change, and commit the result. It reads the
  Blueprint palette from `frontend/node_modules`, so build the frontend once before.
- **Brand icons**: `frontend/src/brandIcons.gen.ts` holds the logos the page draws (model providers,
  models, coding agents, issue trackers). `npm run icons` in `frontend/` writes it from the list in
  `frontend/scripts/generate-brand-icons.mjs` and from two development dependencies that hold only SVG
  files and have no dependency: `@lobehub/icons-static-svg` (MIT) and `simple-icons` (CC0). Run it again
  after adding a brand to the list or changing the version of a package, and commit the result.
- **Editor languages**: `frontend/src/fileLanguage.ts` maps file names and extensions to Monaco
  languages. To add a language that Monaco does not ship, add its Monarch grammar to
  `frontend/src/monacoGrammars.ts` and its extensions to `fileLanguage.ts`.
- **DevTools**: `F12` opens them on Windows. On macOS (13.3 and later), turn on the developer
  features of Safari (**Settings > Advanced > Show features for web developers**), then choose the
  CodeAlta page under the name of this computer in Safari's **Develop** menu.
- **Send diagnostics**: in DevTools, run `localStorage.setItem("codealta.debug.send", "1")` and
  reload. The console then logs entries prefixed `[CodeAlta Send]` and `[CodeAlta RPC]`. They contain
  request keys and error codes, never prompt text or credentials.

## Where the app keeps its data

- `~/.alta/`: configuration, providers, sessions, prompts, skills and plugins, shared with CodeAlta
  TUI. The app adds no desktop-specific state there. The spaces are files of `~/.alta/spaces/`, and
  each project file names the spaces of its project.
- `CodeAlta/desktop` in the local application data folder: `webview/` (web view data: the shown
  space, the open tabs of each space, drafts, window geometry), `appearance.json` (theme of the start-up screen), `preferences.json`
  (what closing the window does) and `update/` (the script and log of **Update and restart**).

## Tests

`src/CodeAlta.Desktop.Tests` holds the .NET tests of the host and its RPC services. They run with
the solution (`dotnet test -c Release` from `src`). See its
[README](../CodeAlta.Desktop.Tests/README.md) for the opt-in native tests.

## Platforms

The tool package has launchers for Windows, macOS and glibc Linux, on x64 and ARM64. Linux musl is
not supported.
