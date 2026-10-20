---
title: Multiple windows in CodeAlta Desktop, and an `alta window` command
status: in-progress
created: 2026-10-20
summary: Tabs can be torn out of the main window into named, colored secondary windows, moved or dragged between windows, and restored on the right monitor at the next start. Agents see and arrange the windows with `alta window`. Needs NeoAstra 1.8.0. (#191)
---

# Multiple windows in CodeAlta Desktop, and an `alta window` command

Today CodeAlta Desktop has one window, and every tab lives in it. Once this work is done:

- A user drags a tab out of the main window to get a new window, and moves tabs between windows.
- Each window has a name, an icon and a color to tell it apart.
- At the next start, every window comes back where it was. A window never opens on a monitor that is no longer connected.

The main window stays the control center: the activity rail, the projects, the top bar and Settings. Secondary windows hold tabs only. An agent sees all of it through `alta window`: the windows, what each one shows and the monitors. It can open a window, move a tab into it, place it on a monitor and name it.

The work starts once NeoAstra 1.8.0 is on NuGet; its release candidate is NeoAstra `main` at `9dc507b`. That release brings:

- **Monitors:** `NeoApplication.GetDisplays()`, `DisplaysChanged`, `GetDisplayAt(NeoPoint)`, `NeoWindow.Display`, `DisplayChanged` and `MoveToDisplay(display[, bounds])`. A `NeoDisplay` has an id that lasts across restarts and a name.
- **Virtual desktops:** `IsOnCurrentVirtualDesktop`, `VirtualDesktopId` and `MoveToVirtualDesktop(id)`.
- **View coordinates:** `NeoAstra.ClientToScreen` and `ScreenToClient`. A NeoAstra test checks that a page capturing the pointer keeps receiving moves outside its window, at the position `ClientToScreen` reports.
- **Several windows:** the window of each view (`NeoAstra.Window`), `window-*` view patterns, and the RPC fixes for several views.

## What the user sees

```text
┌─ Main window ──────────────────────────────┐  ┌─▀▀▀▀▀▀▀▀▀▀▀▀▀▀ (window color) ▀▀▀▀▀▀▀▀▀▀▀▀▀─┐
│ ≡ CodeAlta   [Space ▾] ⌂ ? ◐       ─ □ ✕   │  │ ★ Reviews │ Fix parser ✕ │ Changes ✕ │ ─ □ ✕ │
│ rail │ projects │ Session A ✕ │ Editor ✕   │  ├────────────────────────────────────────────┤
│      │          ├─────────────────────────┤  │  the content of the active tab, with its   │
│      │          │  content                │  │  panes, exactly as in the main window      │
└────────────────────────────────────────────┘  └────────────────────────────────────────────┘
```

- **Move a tab.** The tab menu has **Move to window ▸**, which lists the open windows, the saved named windows and **New window**. The command palette has **Move tab to new window**.
- **Drag a tab.** A tab dropped outside every CodeAlta window opens a new window under the pointer, on the monitor it was dropped on. A tab dropped on another window joins it, at the pane and side the drop indicator shows, as inside one window today.
- **Last tab out closes the window.** When the last tab leaves a secondary window, by a move or a close, the window closes. A secondary window is never empty.
- **Closing a secondary window** closes its tabs, the same as closing each tab: sessions keep running, and editors with unsaved edits ask first.
- **Hiding and exiting.** The main window closes as it does today: it hides to the tray or exits. Hiding hides every window. **Open** in the tray, or a second start, shows them all and focuses the main window. Exiting saves every window, and they all come back at the next start.
- **Identity.** A window has a name, an icon and a color. They are chosen from the window badge at the left of its tab strip, or from Settings.
  - The color is a strip along the top edge.
  - The native title is `Reviews — CodeAlta`, so the taskbar and Alt+Tab show it.
  - A window the user names is *saved*: closing it keeps its name, color and place, and **Move to window ▸ Reviews** opens it again where it was. A window left unnamed (`Window 2`) is forgotten when it closes.
- **Links find their tab.** Opening a file, `alta diff show`, a canvas, a terminal, a rail button (Automations, Issues…) or a session in the rail goes to:
  1. the window that already has its tab, which is brought to the front;
  2. otherwise the window the request came from: the window of the clicked link, or the window holding the tab of the agent session that asked;
  3. otherwise the last focused CodeAlta window.
- **Spaces.** A secondary window keeps the space it was created in, and shows it in its badge. Switching space in the main window leaves it as it is. A tab of another space cannot be dropped on it, and the drop indicator says why. New sessions, terminals and canvases started from a window go to that window's space.

## What changes

### Host: a window registry

A new `DesktopWindows` service in `src/CodeAlta/Desktop/DesktopWindows.cs`, owned by `DesktopApplication`, replaces the single `window`/`view`/`binding`/`windowState` locals of `RunOwnedAsync`. It owns:

- the main window and the secondary windows. Each entry holds `{ Id, Label ("main" / "window-<id>"), Name, Icon, Color, SpaceId, Saved, PinnedDisplayId, NeoWindow, view, binding, DesktopWindowState, closeAllowed }`;
- the last focused window;
- which window holds which tab: one map, `TabRef → windowId`, written only by this service, in the single-writer style of the other runtime state.

A secondary window is created the way NeoAstra's two-window tests create theirs:

1. Create it with the main window's chrome options and a unique `Label`/`ViewLabel` (`window-<id>`), hidden, with no `Owner`, so it minimizes on its own and has its own taskbar button.
2. Restore or compute its placement.
3. Reveal it with `DesktopWindowReveal`.
4. Bind the **same** `NeoRpcHost`.
5. Navigate to `index.html`.

The page asks `windows.current()`, which answers from `NeoRpcContext.ViewLabel`. `IsApplicationDocument` keeps refusing query strings.

The capability manifest is resolved once, before the RPC host is built. It therefore grants window management to `main` and to the pattern `window-*`, with `AllowReviewedViewPatterns` turned on in `DesktopWindowChrome.ResolveCapabilities`. NeoAstra accepts only `prefix*` patterns with a prefix of at least three characters.

The single-window assumptions, and what each becomes:

| Today | Becomes |
| --- | --- |
| The close request hides or exits the app (`DesktopShell.Close`) | The main window keeps that path. A secondary window cancels its close request and asks its page to close its tabs (unsaved edits ask). The page answers, and the host then closes the window through its `closeAllowed` flag, as `allowClose` does for the main window. A close for `SessionEnd`, `System` or `Owner` goes through at once. |
| Shutdown closes one window | Shutdown first saves `windows.json` from the tab map, then closes every window without asking its page. Its disposal runs exactly where `windowState`, `view` and `binding` are disposed today, so the "shutdown not confirmed" paths (`GC.KeepAlive`, lease check) stay as they are. |
| Exit and close questions are published to every page (`DesktopShell.Publish`), and `HasWatchers()` counts any page | They go to the main page only, and `HasWatchers()` asks whether the main page watches. The macOS `close-tab` notice goes to the focused window. |
| `DesktopShell.Show`/`Hide` act on one window | They act on every window, and `Show` focuses the main one |
| `SessionRetentionProtectionService` accepts one page | It keeps one entry for each document session and protects their union. At startup, the host also protects the sessions listed in `windows.json` until their window's page has connected. |
| `DesktopSpaceView.ShownSpaceId` is one value, read through about nine closures (`DesktopApplication.cs:687–961`) | Each window has a space. The RPC methods behind those closures take `NeoRpcContext`, so they know the calling window. |
| `IAltaChangesView.Show`, `IAltaEditorView.Open`, `IAltaDocumentationView.Show` and the canvas and terminal views broadcast to every page | New overloads take the caller's session id (`context.Caller.SourceSessionId`), and the host resolves it to the window that holds `session:<id>`. `MarkdownLinksService` takes `NeoRpcContext`. The request is then routed to one window by the rule under "Links find their tab". |
| Zoom, theme, title-bar colors and the folder-picker owner apply to the main window | Zoom, theme and title-bar colors apply to every window. A folder picker opens over the window that asks. |

Taskbar progress stays on the main window, which reports the work of the whole app. The configuration-repair mode and the copy-browsing mode (`RunAsync`) stay single-window. Secondary windows open only once the RPC binding exists, and never after a close of the app has been requested.

### Tabs: who owns what

Tabs stay frontend state: each page keeps the order and panes of its own window. The host decides only membership. That keeps the one-editor-per-project and app-wide singleton tabs unique across windows, and lets requests be routed. One module, `frontend/src/tabRefs.ts`, maps the dock node ids (`session:[projectId,sessionId,path]`, `file:[…]`) to and from the `TabRef` text the host and `alta window` use (`session:<id>`, `editor:<projectId>`, `changes:<projectId>`, `terminal:<id>`, `canvas:<plugin>/<id>`, `documentation`, …).

- **Every open goes through the host.** That covers `openSessionTab`, `openFile`, the rail and top-bar buttons, and the editor, change, documentation and canvas requests. If another window holds the tab, the host focuses it there and the page does nothing. In the main window, a rail click asks before `selectProject` sets the selection. A tab moved out of the main window clears the selection, so the selection effect does not reopen it.
- **A move closes the tab in one page and opens it in the other.**
  - Sessions, terminals and canvases live on the host, so the new page attaches to them again.
  - Composer drafts are already in shared `localStorage` (`promptDraft.ts`). Ownership guarantees that two pages never mount the composer of the same session.
  - Unsaved editor text travels with the move and is opened again as unsaved, without its undo history.
  - A session tab with a prompt being sent or queued in its page cannot move until the send is done. The menu entry is disabled and says why.
- **A secondary window is `App` with a role.** `App` gets `role: "main" | "secondary"`. The secondary role:
  - hides the rail, the projects panel and the top bar, and shows the window badge;
  - takes its tabs from the host instead of `localStorage`, together with the open file of each editor (today that is the shared key `codealta.desktop.editor.v1`, which the main window keeps);
  - skips the effects only the main window may run: `adoptLegacyFiles`, the cleanup of space keys, the `storage` refresh, and the cross-space writes of `addCanvasTabToSpace`;
  - keeps its own closed-tabs list, Ctrl+Tab and Ctrl+W.

  The tab and pane rendering is moved out of `App` only if the role proves too costly.
- **Startup: the host wins.** It loads `windows.json` before any page connects. The main page then leaves out of its restored lists every tab another window holds.
- **Not saved.** Terminal, disk-folder and Claude Code preview tabs are never saved, as today. A saved window left with no tab that can be restored does not open. The preview tab is pinned before it moves; new-session draft tabs do not move.

### Dragging between windows

The tab drag keeps today's pointer-capture gesture (`useSessionTabDrag.ts`). That gesture was chosen to keep tabs out of WebView2's native drop handling, and it cannot become an HTML5 drag in the middle of the gesture. While the button is held, the captured pointer keeps reporting outside the window, so the gesture extends across windows:

```mermaid
sequenceDiagram
  participant S as Source page
  participant H as Host (DesktopWindows)
  participant T as Window under the pointer
  S->>H: dragMoved(tab, point in its window)
  H->>H: to screen units; hit-test every window's FrameBounds
  H-->>T: dragHover(tab, client point)
  Note over T: draws the same drop indicator, or a refusal (other space)
  S->>H: dragReleased(tab, point)
  alt over another CodeAlta window
    H-->>T: dropTab(tab, client point)
    T->>H: accepted(pane, side)
    H-->>S: release tab (with page state)
  else over no window
    H->>H: open window on GetDisplayAt(point), placed by Restore
    H-->>S: release tab
  else over the source window or Escape
    Note over S: handled in the page, as today
  end
  Note over H: source window empty → close it
```

The page reports points in its own client coordinates. The host converts them with `NeoAstra.ClientToScreen` on the source view, finds the window under the point from each window's `FrameBounds`, and converts back with `ScreenToClient` on the target view. A torn-out window gets the size of the window the tab left. Its top-left corner is placed at the pointer, and `DesktopWindowState.Restore` clamps it into that monitor's work area. NeoAstra's real-mouse test shows that the captured pointer reports outside the window in WebView2. The first task of this step checks the same in WebKit on macOS and Linux, through CI or the documentation. On a platform where it does not report outside the window, the drag stays inside the window and **Move to window ▸** moves tabs between windows.

### Persistence and monitors

The main window's placement stays in `window.json`, unchanged. Each secondary window's bounds and state go through NeoAstra's `NeoWindowStateController`, under the key `window-<id>`. In 1.8.0 the controller saves the window's `NeoDisplay` id in `NeoWindowPlacement.DisplayId`, so the bounds and their monitor never disagree. Step 1 checks this; if it is not the case, the store is wrapped to write that id. The rest of each window is in `windows.json` in the desktop data root. The developer instance has its own.

```json
{
  "version": 1,
  "restoreAtStartup": true,
  "windows": [
    {
      "id": "01a15a2c",
      "name": "Reviews",
      "icon": "star",
      "color": "#d97706",
      "saved": true,
      "spaceId": "default",
      "pinnedDisplayId": null,
      "virtualDesktopId": "6e1f…",
      "tabs": ["session:01a159ab-…", "changes:01a14d9c-…"],
      "editors": { "01a14d9c-…": { "file": "src/Program.cs" } },
      "layout": { "flexlayout": "…" }
    }
  ]
}
```

Forgetting a window deletes its entry and its `window-<id>.json`.

Every placement goes through `DesktopWindowState.Restore`, which already wraps `NeoWindowStateRestore.Clamp`. It is extended to take a pinned monitor first. It uses:

1. the pinned or saved monitor, when it is connected;
2. otherwise the monitor the window overlapped most;
3. otherwise the primary monitor.

It then fits the window into that monitor's work area. Restoring, tearing out, `alta window place` and Settings all use it, so no path can open a window where no monitor is. In addition:

- **Restore at startup.** Secondary windows open after the main window is shown, each hidden until it is placed.
- **A monitor disappears while CodeAlta runs** (`DisplaysChanged`). Every window whose monitor is gone is placed again on the primary monitor, unless the OS has already moved it to a connected one.
- **A pinned monitor comes back.** A window pinned to a monitor in Settings moves back to it when it is connected again. A window that is not pinned stays where it is.
- **Virtual desktops** (Windows, where NeoAstra reports support). A window returns to its saved desktop if that desktop still exists, otherwise to the current one.

### `alta window`

`alta window` is a built-in group of the live tool, gated on an `IAltaWindows` service (`src/CodeAlta.LiveTool/AltaWindows.cs`) as `alta canvas` is gated on `IAltaCanvasView`. The TUI does not register it, so it does not get the group. The desktop adapter `DesktopAltaWindows` sits over `DesktopWindows`, and `window` joins `ReservedRootCommands`.

```text
alta window list [--tabs]                      alta.window.item, alta.window.tab, alta.window.summary
alta window show <window>                      alta.window.detail
alta window monitors                           alta.window.monitor
alta window open [--name <n>] [--icon <i>] [--color <c>] [--monitor <m>] [--space <s>] [--tab <tab>...]
                                               alta.window.opened
alta window focus <window>                     alta.window.focused
alta window close <window>                     alta.window.closed   (the main window is refused)
alta window tab move <tab> --to <window|new> [--monitor <m>]
                                               alta.window.tab.moved
alta window place <window> [--monitor <m>] [--state normal|maximized] [--bounds x,y,w,h] [--desktop current]
                                               alta.window.placed
alta window set <window> [--name <n>] [--icon <i>] [--color <c>] [--saved true|false] [--pin-monitor <m>|none]
                                               alta.window.updated
```

- **`<window>`** is an id, `main`, a name, or the start of an id, resolved as `--space` is.
- **`<tab>`** is a `TabRef` as `list --tabs` prints it. `session:current` names the caller's own session.
- **`<m>`** is a monitor id, its number in `alta window monitors`, its name, or `primary`. A monitor that is not connected is an error, `window.monitorNotFound`.
- **Icons and colors** are validated as for spaces (`SpaceDescriptor.IsValidIcon/IsValidColor`).
- **Refusals** use their own error ids: a tab that cannot move (a prompt being sent, another space) gives `window.tabNotMovable`.
- **Policies:** every subcommand but `set` is `Read`, as canvas `open`/`close` are: they change only what is shown, and closing a tab never stops a session. `set` is `Mutating`, since it writes `windows.json`.

```text
> alta window list --tabs
{"type":"alta.window.item","id":"main","main":true,"name":"Main","spaceId":"default","monitor":{"id":"…","name":"DELL U2720Q","primary":true},"state":"maximized","focused":true,"tabCount":3}
{"type":"alta.window.item","id":"01a15a2c","main":false,"name":"Reviews","icon":"star","color":"#d97706","spaceId":"default","monitor":{"id":"…","name":"LG 27UK850"},"state":"normal","focused":false,"tabCount":2}
{"type":"alta.window.tab","windowId":"01a15a2c","tab":"session:01a159ab-…","kind":"session","title":"Fix parser","active":true}
{"type":"alta.window.summary","windows":2,"tabs":5}
```

So that agents know the command exists, a static Runtime Context line is contributed the way the UI tools line is (`DesktopUiPlugin.cs`): *Windows: `alta window list --tabs` shows the windows of CodeAlta Desktop, their tabs and the monitors; `alta window tab move` and `alta window place` arrange them.* It stays static so that the prompt cache holds. An entry is also added to the desktop search index (`DesktopSearchSources.cs`). A caller with no session, such as a client of `codealta-dev`, gets the last focused window when it names none.

### Settings › Windows

A new **Windows** page in Settings, built on the space editor (`frontend/src/spaces/`), with:

- **Restore windows at startup**: on by default. When off, only the main window opens.
- **Windows**: the main window and every saved window, open or not. For each one:
  - its name, icon and color;
  - **Monitor**: `Where it was last`, or a connected monitor, which pins the window to it;
  - **Open** or **Focus**;
  - **Forget**, for saved windows.

  A window pinned to a monitor that is not connected shows *Not connected: opens on the primary monitor*.
- **Monitors**: a read-only list of the connected monitors (name, resolution, scale, primary), so the user can see what the dropdown names.

The page reads and writes through the `windows` NeoRpc service, the same one the tab moves use. `alta window set` and this page share one owner, `DesktopWindows`, and its changes are pushed to every page.

## Steps

Work on a branch `feat/multi-window` from `main`. Commit after each step with the autolabel prefixes. Do not push or merge without the owner.

- [x] 1. Upgrade NeoAstra to 1.8.0 in `src/Directory.Packages.props`, adapt to its API changes, then build and run every test (`dotnet test`, `npm test`).
  - Done: `AbuseClosureThreshold` now counts seconds of refused calls, so the canvas endpoint closes a script after 5 such seconds; `DesktopWindowState` places windows with `NeoApplication.GetDisplays()` (the units of the windows), takes a key and a pinned display, and NeoAstra's controller saves the display of the window itself (no wrapped store needed).
  - Not caused by the upgrade, failing on Windows before it: two `ReviewServiceTests` compare `\n` with `Environment.NewLine` text, and `ReviewBeforeCommitRuntimeTests` cannot delete the read-only git objects of its temp folder.
- [x] 2. Add `DesktopWindows` holding the main window only. It takes over the window, view, binding and state locals of `RunOwnedAsync`, with the same objects created and disposed in the same order. Behavior is unchanged and the existing tests pass.
  - Deviation: the main window's locals stay in `RunOwnedAsync`, where its fragile shutdown paths are; `DesktopWindows` is told of them (`AttachMain`) and owns the secondary windows only, with the tab map, the routing and `windows.json`. Steps 2 to 5 landed together in `de75bc1f`.
- [x] 3. Remove the single-window assumptions of the host table above:
  - the labels and the `window-*` capability;
  - retention for each page;
  - exit questions and `HasWatchers` for the main page only, with `Show`/`Hide` on every window;
  - `NeoRpcContext` on the space-reading methods, and the caller-aware view overloads;
  - zoom, theme and the folder-picker owner for every window.

  Each comes with a test in `src/CodeAlta.Desktop.Tests`.
- [x] 4. Persistence and monitors:
  - `windows.json`, and the wrapped placement store that writes `DisplayId`;
  - `Restore` with pinned monitors, restore at startup, and the save-then-close path at exit;
  - `DisplaysChanged` handling and virtual desktops.

  Unit-test `Restore` with a missing monitor, a changed resolution, mixed DPI and a pinned monitor.
  - No wrapped store: NeoAstra 1.8.0's controller saves `DisplayId` and `DisplayWorkArea` itself. The main window's pinned monitor, from Settings › Windows, is applied at its start too.
- [x] 5. Tab ownership with the main window alone: `tabRefs.ts`, the `windows` RPC service (`current`, `watch`, `list`, `claimTabs`/`releaseTabs`), every open path asking the host, and request routing. Behavior is unchanged, and tests cover the routing order.
  - Deviation: the pages report what they hold (`report`, debounced) instead of `claimTabs`/`releaseTabs`, and `current()` sends the map, so the host wins at startup. A request that no window holds goes to the window it came from (a followed link), else the window of the session that asked, else the main window, not the last focused one: an agent's request without a session should not land in whatever window the user last clicked.
- [x] 6. Secondary windows without drag:
  - `App` with `role: "secondary"` and the window badge with its identity popover;
  - the RPCs `open`, `close`, `focus`, `moveTab` and `setIdentity`, with page state carried;
  - **Move to window ▸** in `SessionTabMenu` and **Move tab to new window** in the command palette;
  - the close flow of a secondary window, and the last tab closing it.

  Localize in the five languages; add `*.test.ts` and `*.browser.test.ts` coverage.
  - Deviation: a code editor with unsaved edits refuses to move ("Save the files of this editor before moving it.", `unsaved` for `alta window`) instead of carrying its text: Monaco models are per page, and a refusal loses nothing. Coverage is `windows/windows.test.ts` (hub, tab refs) and the existing browser mounts with the `windows` stub; no new browser test.
- [x] 7. `alta window`: add `IAltaWindows`, the group and its help, `DesktopAltaWindows`, the reserved root, the Runtime Context line and the search entry. Add `AltaWindowCommandsTests` with a fake view, and a desktop adapter test.
  - `alta window open --tab` reports in `notMoved` the tabs that did not move and why. `--space` for `open` is left out: a window takes the space of its first tab.
- [x] 8. Settings › Windows: the RPC methods, the page, localization, and a mount test in `settingsOrganization.neoastra.mount.ts`.
- [x] 9. The drag across windows and the tear-out:
  - first, the pointer-capture check on each platform;
  - then `dragMoved`/`dragReleased`, the drop indicator in the target window, the refusal across spaces, and the tear-out on `GetDisplayAt`.
  - The page calls `dragOver`/`drop` once the pointer leaves its viewport; the host converts with `ClientToScreen` and finds the window with `GetWindowAt`. Where NeoAstra reports no window placement (`NeoWindowCapability.Placement`, as on Linux), the drag stays in its window (`canDrag` off) and **Move to window** moves tabs; macOS is untested here.
- [x] 10. Update the docs:
  - `doc/live-tool.md`: a "Window commands" section and the group list;
  - `doc/desktop.md`: the window architecture;
  - `doc/development-guide.md`: no "the page" singletons; route through `DesktopWindows`;
  - `site/docs/workspace.md`: a Windows section;
  - `site/docs/ui-tools.md`: `list_pages`/`select_page` reach each window, and the selection is shared;
  - `site/docs/desktop-and-tui.md` and `readme.md`.
- [x] 11. Check it in the real app (below), then take the site screenshots of a two-window setup and of Settings › Windows at 2400×1500.
  - `site/img/alta-desktop-windows.webp` (a window named Review, a session and an editor side by side) and `alta-desktop-windows-settings.webp`.

## How it is verified

- [ ] Builds and tests pass:
  - `dotnet build -c <Configuration>` and `dotnet test -c <Configuration>`, with the configuration `AGENTS.md` asks for;
  - `npm test`;
  - `lunet build` in `site/`.
- [ ] Unit tests cover:
  - `Restore`, which never returns bounds outside the work area of a connected monitor;
  - the ownership map, which keeps editors and singleton tabs unique;
  - routing, in its order: the owning window, then the requesting window, then the last focused one;
  - the save-then-close path at exit, which keeps unnamed windows for the next start;
  - the `alta window` records and errors, including `window.monitorNotFound`, `window.tabNotMovable` and the refused close of the main window.
- [ ] In the developer instance (`alta --dev`, driven through `codealta-dev` with `list_pages`/`select_page`):
  - move a session tab to a new window and back, and check that the secondary window closes;
  - move an editor with unsaved edits, and check that the edits survive;
  - from a session in window 2, open a file link while the project's editor is in the main window, and check that the main window comes to the front on that file;
  - name, color and save a window, close it, and reopen it from **Move to window ▸**;
  - drag a tab between windows, and tear one out.
- [ ] Restart the developer instance, and check that the windows, their tabs and their monitors come back. Edit `windows.json` to pin a monitor that does not exist, restart, and check that the window opens on the primary monitor.
- [ ] From a session of the developer instance, run `alta window list --tabs`, `monitors`, `tab move session:current --to new`, `place --monitor primary` and `set --name`. Check each result in the windows.
- [ ] Check both themes, the color strip and the badge in each, and Settings › Windows in a 900×650 window.

## Decisions

| Decision | Choice | Why |
| --- | --- | --- |
| A window per page, or child windows of one page | One WebView page per window, all bound to the same RPC host | Monaco, xterm and React do not work reliably across a `window.open` realm; such windows would not restore at startup; WebKit differs |
| Source of truth | The host holds which window has which tab; each page holds its own order and panes | Uniqueness and routing need a view of every window; the layout stays where it is drawn |
| Secondary window code | `App` with a role, not a split of `App` | `renderSession`/`renderFile` close over dozens of `App` values; a split is a large, risky refactor |
| Drag | Today's pointer drag, extended across windows through the host | An HTML5 drag cannot start inside the captured gesture, and the pointer drag keeps tabs out of the native drop handling |
| Unsaved editor text on a move | Carried, without undo history | Neither saving behind the user's back nor refusing the move |
| Closing a secondary window | Closes its tabs; unsaved edits ask first | What browsers and editors do; tabs reopen from the rail |
| Hide, tray Open, second start | Act on every window; focus the main one | One application, one visibility |
| `Owner` of a secondary window | None | It minimizes on its own and has its own taskbar button, as browser windows do |
| Taskbar progress | Main window only | It reports the work of the whole app |
| Spaces | A secondary window keeps the space it was created in | Two spaces can be seen at once, and new sessions stay in the expected space |
| Named windows | Naming a window saves it; unnamed windows are forgotten on close | Gives "my setup" a home without a separate preset concept |
| Window without a session caller | The last focused window | It is the one the user looks at |
| Settings storage | `windows.json` in the desktop data root, not `config.toml` | It is desktop placement state, separate for the developer instance; one writer for Settings and `alta window` |
| Policy of `alta window` | `Read` except `set` | As `alta canvas`: it changes what is shown, never the work |
| Taskbar identity | Name in the native title; no tinted taskbar icon | NeoAstra has no overlay icon on Windows; Alt+Tab and the taskbar show the title |

## Risks

- **The NeoAstra monitor API is new.** Its names come from the release candidate, and step 1 adapts them to the published 1.8.0. Where the OS gives no stable monitor id, the saved monitor falls back to the overlap `Clamp` already uses.
- **`RunOwnedAsync` has fragile shutdown paths.** Step 2 changes ownership only, not order, and the existing shutdown tests guard it.
- **The captured pointer may not report outside the window** on some platform. The check comes first in step 9; there, tabs move with **Move to window ▸** only.
- **UI automation's selected page is shared** by every caller of `alta ui` and `codealta-dev`. This plan documents it and does not change it.

## Out of scope

- Saving the pane geometry of the main window, which is still lost on restart. Secondary windows save their tabs and the tab in front, not the geometry of their panes either.
- Moving new-session draft tabs.
- A tinted taskbar icon for each window, and taskbar progress in secondary windows.
- Moving windows between macOS Spaces, which has no public API.
- Any TUI counterpart.

<details><summary>Evidence behind the design</summary>

- **One window today:**
  - `DesktopApplication.cs:323` creates the window, `:521` the view, and `:979` binds RPC.
  - `DesktopWindowChrome.cs:20,83` hard-codes `main`.
  - Capability patterns are checked in NeoAstra's `NeoCapabilities.cs:282,378,572`.
- **The close flow:**
  - `DesktopShell.cs:187-189`; the close-request handler must not wait (`DesktopApplication.cs:352`), and `allowClose` is at `:277`.
  - Shutdown is at `:1036-1085`; `Publish`/`HasWatchers` are at `DesktopShell.cs:650-652`.
- **Single-page retention:** `SessionRetentionProtectionRpc.cs:84` (`!_watchActive`, `_pageId`); the per-window wiring is at `DesktopApplication.cs:325,533,536`.
- **Broadcast views and callers:** `DesktopEditorView.cs:16-24` and `CodeAlta.LiveTool/AltaChangesView.cs:18`; the caller's session is `context.Caller.SourceSessionId`.
- **Tabs:**
  - They are frontend state: `sessionTabs.ts` and `fileTabs.ts`, laid out by the FlexLayout model of `sessionTabLayout.ts`.
  - They are saved in `localStorage` per space (`main.tsx:1198,1201`). The selection effect is `main.tsx:1171-1188`.
  - The drag is a pointer capture that refuses other models (`useSessionTabDrag.ts:6-8,32,83-85`, `sessionTabLayout.ts:255`).
  - Drafts are kept in `promptDraft.ts:3`, the editor's open files under `codealta.desktop.editor.v1` (`ProjectEditor.tsx:97`), and unsaved text in Monaco models (`EditorSurface.tsx:171`).
- **NeoAstra:**
  - Windows and views are in `NeoApplication`/`NeoWindow`, with `NeoAstra.Window`.
  - Placement uses `NeoWindowStateRestore.Clamp` (`src/NeoAstra/Desktop/WindowState.cs:147-177`), whose controller saves `DisplayId: null` (`:342`).
  - The brief of the monitor API is in session `01a158f6`.
- **`alta` groups:** `BuiltInAltaCommandContributor.cs:122-268`, `alta canvas` in `.Canvases.cs`, and the space icon and color options in `.Spaces.cs:121-125`.

</details>
