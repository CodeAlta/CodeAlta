# CodeAlta Desktop

This project builds CodeAlta Desktop: the .NET tool package `CodeAlta`, command `alta`.

This page is for people who build and change the app. To use it, see the
[user documentation](https://codealta.github.io) and
[Desktop and TUI](https://codealta.github.io/docs/desktop-and-tui/). How the app behaves in detail
(window, workspace, composer, timeline, Settings pages, RPC services and their limits) is described
in [`doc/desktop.md`](../../doc/desktop.md).

## How it is built

- A native window from [NeoAstra](https://www.nuget.org/packages/NeoAstra) 0.3.4 hosts the web view
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

Install the .NET 10 SDK and a current Node.js, then build the solution from `src`:

```powershell
cd src
dotnet build -c Release
./CodeAlta/bin/Release/net10.0/alta.exe
```

The first build restores the npm packages of `frontend/` and generates the RPC client. To build
only this project in Debug:

```powershell
cd src
dotnet build CodeAlta/CodeAlta.csproj
./CodeAlta/bin/Debug/net10.0/alta.exe
```

Build outputs use the standard SDK layout, `bin/<Configuration>/net10.0` and
`obj/<Configuration>/net10.0`.

`alta` opens the current folder as a project on the `~/.alta` profile, like `altatui`. Only one
CodeAlta runs on a profile at a time, so exit the installed app first (`alta --exit`), or use the
developer instance.

## Developer instance

`alta --dev` starts a second window, titled **CodeAlta (dev)** with a **DEV** tag beside its name.
It runs beside the normal app on the same `~/.alta` profile:

- it shares configuration, providers, credentials, prompts, skills, the project catalog and the
  spaces;
- it keeps its own sessions, database (the list of sessions and the tables of the plugins) and lock under `~/.alta/dev/`;
- it keeps its own web view data (tabs, drafts, theme) under `CodeAlta/desktop-dev` in the local
  application data folder.

Use it to work on CodeAlta with CodeAlta: the normal app runs the agents, and the developer
instance runs the build you are testing. `alta --dev --exit` asks it to exit. The normal app can be
the installed one or a build of this repository; a build cannot be replaced while it runs, so when
the normal app runs from `bin/Release`, build, test and start the developer instance in `Debug`, and
the other way round.

Its MCP server listens at `http://127.0.0.1:2583/mcp` (the normal app has port 2582): the `.mcp.json`
and `.alta/mcp.json` of this repository register it as `codealta-dev`, which is how an agent looks at
the window and clicks in it. See `AGENTS.md`.

## Command line

| Command | Effect |
| --- | --- |
| `alta` | Starts the app for the current folder. If it is already running, shows its window. |
| `alta --dev` | Starts the developer instance. |
| `alta --wait` | Starts the app and keeps the terminal until it exits, alone or with `--dev`. |
| `alta --exit` | Asks the running app to exit. It asks first about unsaved files and running sessions. |
| `--mcp-port <port>` | With a start that opens the window: the port of the MCP server (0 takes a free one). |
| `--mcp-host <address>` | With a start that opens the window: the address the MCP server listens on, `localhost` or an IP address. |
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
[Isolated-root launches](../../doc/desktop.md#isolated-root-launches).

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
  The window is given the catalog as the shown space has it, and each space has its own tabs. See
  [Spaces](../../doc/desktop.md#spaces).
- **Tests**: `npm test` in `frontend/` runs the unit tests (`src/*.test.ts`, `src/*.test.tsx`). The
  `*.browser.test.ts` files among them mount real components in headless Edge and are skipped where
  Edge is not installed.
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
