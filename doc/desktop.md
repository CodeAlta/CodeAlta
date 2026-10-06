# CodeAlta Desktop

This page describes how CodeAlta Desktop (`src/CodeAlta`, package `CodeAlta`, command `alta`) behaves:
its window, workspace, composer, timeline, Settings pages, host RPC services and limits. It is a
reference for maintainers. The user documentation is under `site/docs`, starting with
[Desktop and TUI](../site/docs/desktop-and-tui.md). Build and run instructions are in
[`src/CodeAlta/README.md`](../src/CodeAlta/README.md).

## Startup and storage

Running `alta` with no options starts the same host as `altatui`: it owns the current project and
the `~/.alta` runtime, acquires the shared runtime lock, starts the configured providers, and runs the
built-in plugins (MCP, Git, Statistics) and the source plugins of `~/.alta/plugins` and of the
launch project. Tool permissions are approved automatically and provider input forms are cancelled
unless an isolated-root launch opts in to review them. WebView-only data stays in the platform-local
application-data directory, and existing `.alta` storage is not migrated.

No-argument startup derives a stable WebView data directory from the platform's local application-data
location, composes the owned host for the current directory, and uses the current `~/.alta` catalog.
The host may update the standard project catalog, journal, cache, provider state and SQLite sidecars;
submissions may authenticate or use configured provider storage/network. It adds no desktop-specific
state to `.alta` and performs no storage migration. Help/version and rejected arguments do not initialize
native services or create storage. Explicit catalog and scoped-owned options retain stricter validation.

## Main window

The main window opens centered at 80% of the primary work area on Windows; other platforms use a
centered 1280×860 window until NeoAstra exposes display metrics.

The window has no separate title bar: the page draws it. The CodeAlta mark and name sit at the top
left, the session tabs continue the same strip, and the platform's minimize, maximize and close
buttons stay at the top right. Drag the mark or any empty part of a tab strip along the top edge to
move the window, and double-click it to maximize or restore; tabs and buttons in that strip keep
their own clicks and drags. With the Explorer hidden the tabs start right after the name. In a split
layout only the panes along the top edge are part of the title bar.

The window appears with a **start-up screen**: the title strip with the mark and name, the logo and a
progress bar, in the colors of the theme the window last had (dark on a first start). It stays until
the workspace has its first data, so the window is never shown empty. The host (catalog, providers,
plugins and their MCP servers) starts beside the view rather than before it. The window controls are
drawn for the application's theme, not the system's: dark symbols on the light theme, light ones on
the dark themes, and they follow a theme change at once. The theme and its background are kept in
`appearance.json` in the WebView data directory, which is what lets the window open in the right
colors before any page exists.

When the global `config.toml` cannot be loaded, the window opens on **configuration recovery** instead
of failing: the file in an editor with TOML highlighting, the error marked on its line and the caret
on it. The text is checked as it is typed, and the status under the editor says where it is still
wrong (select it to jump there) or that it is valid. **Save and continue** (Ctrl+S) writes a valid file
and starts the application; **Reload** reads the file again, after a confirmation when there are
edits; **Exit** closes the application without changing the file. A file that changed on disk in the
meantime is not overwritten. Recovery reads, checks and writes that one file only: no provider,
plugin or session is started before it is valid.

### The application beyond its window

CodeAlta keeps an icon in the notification area (the menu bar on macOS, the system tray on Linux)
with **Open CodeAlta** and **Exit**; selecting the icon opens the window too. Closing the window can
hide it and leave the application running there, so sessions keep running. What closing does is the
user's choice, kept in `preferences.json` in the application data directory (`onClose`: `ask`,
`keep` or `exit`) and shown by Settings → Appearance → **When the window is closed**:

- **Ask each time**, the choice of a new profile. The page asks **Keep CodeAlta running?**, saying
  where CodeAlta stays, with **Keep running**, **Exit CodeAlta**, **Cancel** and a **Remember my
  choice** check box. Keep running is the default: it has the focus, and Enter chooses it from the
  check box too. The arrow keys move between the buttons, around the ends; Escape and Cancel leave
  the window open and give the focus back to what had it. An answer given with the check box
  becomes the setting; without it the question comes back at the next close.
- **Keep running**: the window is hidden, without a question.
- **Exit CodeAlta**: closing the window exits, with the questions of an exit (see below).

Choosing **Ask each time** in the settings brings the question back. A `preferences.json` written
before the question existed (`closeToTray`) is read as the choice it was, so nothing is asked of
who had set the switch. Only closing the window is a question: **Exit** in the tray, Ctrl+Q,
`alta --exit` and Quit on macOS exit. While an exit already asks about unsaved files or running
sessions, closing the window asks nothing more. When no page can ask (it is being loaded, or it
lost its host) the window is hidden, as it was by default. Before there is a workspace (start-up,
the repair of a configuration file) and where the desktop has no status area (some Linux sessions),
closing the window exits whatever the setting says; on macOS the Dock icon also brings the window
back.

The question, like the questions of an exit, is shown in front of an open window of the application
(Settings, the session browser): those windows are modal dialogs in the browser's top layer, and a
question added to the page itself would be behind them, out of reach (`frontLayer`).

Starting `alta` while CodeAlta already runs with the same profile does not start a second one: the
running one shows its window and comes to the front. `alta --exit` asks the running one to exit, as
**Exit** in the tray does (`alta --dev --exit` for the developer instance); with none running it does
nothing. Use it before `dotnet tool update -g CodeAlta`, which cannot replace the files of a running
application.

**Exit** (the tray's, Ctrl+Q, **Quit CodeAlta** or ⌘Q on macOS, or a closed window that cannot stay
in the tray) first asks about files with unsaved edits, then, while sessions are running or terminals
run a command, says how many and that exiting stops them: **Exit CodeAlta** or **Cancel**. The end of
the user's session at sign-out or shutdown exits without a question.

On macOS the application has a menu bar, because a Mac application has no shortcut that its menu bar
does not define:

- the application's menu: **Hide CodeAlta** (⌘H), **Hide Others** (⌥⌘H), **Show All** and
  **Quit CodeAlta** (⌘Q), which is the tray's **Exit**;
- **Edit**: **Undo** (⌘Z), **Redo** (⇧⌘Z), **Cut** (⌘X), **Copy** (⌘C), **Paste** (⌘V) and
  **Select All** (⌘A), for the prompt editor and every other text field;
- **Window**: **Minimize** (⌘M) and **Close** (⌘W), which closes the window as its close button does.

Every item is a command with its shortcut (`DesktopApplicationMenu`), not a NeoAstra role item: a
role item cannot carry a shortcut, and the Quit role ends the application as the end of the session
does, without the questions above. An item other than Quit sends the standard AppKit action through
the responder chain on the window's thread. A shortcut that the page handles itself (undo and redo in
the prompt editor, the Ctrl chords) still reaches the page first. The developer instance's items say
**CodeAlta (dev)**. Windows and Linux have no menu bar: the window's title bar is the application's.

On Windows the page's files are read by path, and Windows refuses a path of 260 characters or more.
When CodeAlta is installed so deep that its files reach that length (a tool path inside a long
folder, not the usual `.dotnet\tools`), it says so in a message at start, naming the folder and the
length, and exits, instead of staying on its start-up screen with half of its files.

### Started from a terminal

`alta` typed in a terminal gives the prompt back once the window is shown. A shell waits for the
program it starts, and on Windows the launcher that `dotnet tool install` writes for a tool packed
per runtime is a script (`alta.cmd`), which waits for a windowed program as well: the terminal used
to stay busy until CodeAlta exited. Such a start now runs the application in a second process and
ends (`DesktopTerminalStart`):

- **When.** For `alta` and `alta --dev` started from a terminal: on Windows when the process that
  started `alta.exe` has a console, on macOS and Linux when the standard input, output or error is
  a terminal. The Start Menu shortcut, a taskbar pin, the Dock and a desktop entry have no
  terminal, so the process they start is the application, as before. So is a start under a
  debugger, on explicit roots, or through `dotnet alta.dll`.
- **The application.** The same executable with the same options, working directory and
  environment. On Windows it is started as the Start Menu starts it, so neither the console nor
  the pipes of a caller that reads the output reach it; it takes the application identity, so the
  taskbar still groups its window with the shortcut. On macOS and Linux it starts a session of
  its own (`setsid`) and replaces a terminal on its standard streams with `/dev/null`: closing the
  terminal or Ctrl+C does not reach it, and nothing it or the web view writes lands in the
  terminal. A stream redirected to a file or a pipe is kept.
- **The wait.** The first process creates an empty file in the temporary folder
  (`codealta-start-<token>`) and gives its token to the application in `CODEALTA_START_TOKEN`; the
  application removes the file once its window shows the start-up screen, and the first process
  ends with exit code 0. When the application ends before that, the first process ends with the
  same exit code and names the log folder on the standard error. After 30 seconds it stops waiting
  and ends with 0.
- **Already running.** The start shows the window of the running application itself; nothing is
  handed over.
- **`alta --wait`** (alone or with `--dev`) runs the application in the started process, which
  keeps the terminal until it exits: for a script that waits for CodeAlta, and on macOS and Linux
  to read what the application writes when it does not start. A windowed program writes nothing
  to a console on Windows.
- A start that cannot be handed over (no temporary folder, the process cannot be started) runs
  the application in the started process.

### Updates

Once per start the desktop asks nuget.org whether a newer `CodeAlta` package is published, as the
terminal application does for its own package (a prerelease build also considers prereleases). The
tool package only names one package per platform (`CodeAlta.win-x64`, `CodeAlta.Tui.linux-x64`), and
nuget.org can list it well before those: a version counts once the package of the running platform
lists it too, so that the update command cannot fail on a package that is not there yet. A
newer version is announced by a notice with the version, the command that installs it
(`dotnet tool update -g CodeAlta`, with `--prerelease` for a prerelease) and a button that copies
it, and **View release notes**, which opens the release page in the browser. The application has to
be exited first (**Exit** in the tray, or `alta --exit`): the tool cannot be replaced while it runs.
Settings → About keeps the result under **Updates**. A build that is not a published version, and an
instance on explicit roots, make no request.

For a tool installed with `dotnet tool install -g`, the notice and the About page also have **Update
and restart**. It hands the update to a small script in the application data directory (`update/`),
then exits as **Exit** does, with its questions; the script waits for the application to end, runs
the same `dotnet tool update` command with the .NET installation the application runs on, records
the outcome and starts CodeAlta again through the tool's launcher, which says whether it was
updated. Canceling the exit calls the update off, and the script gives up after fifteen minutes.
`update/update.log` keeps the command's output. A build output and the developer instance have no
such button.

### Desktop entry of the installed tool

The first start of a tool installed with `dotnet tool install -g CodeAlta` adds CodeAlta to the
desktop, in the user's own folders and without elevation, and refreshes the entry when the version
or the launcher's path changes. The first time, a notice in the window says where it was added:

- **Windows:** a **CodeAlta** shortcut in the Start Menu, with the application's icon. The process
  has its own application identity, so the taskbar groups the window with that shortcut and a pin
  keeps the name and icon. The shortcut starts the executable of the installed version, not the
  tool's launcher: for a tool packed per runtime the launcher is a script (`alta.cmd`), and a
  shortcut to it would show a console window. The executable's path holds the version, so the
  first start of each version writes the shortcut again, together with the copy that a pin on the
  taskbar starts.
- **macOS:** `~/Applications/CodeAlta.app`, a bundle whose executable is a shell script that becomes
  the installed tool (`exec`). It starts the tool through the user's login shell so that it gets the
  PATH of a terminal (git, node, the .NET runtime), which an application started from the Finder
  does not have. Nothing is signed or downloaded: the bundle is created locally, so it is not
  quarantined. If the tool is uninstalled, the bundle says so when opened. The bundle and the
  running application use `alta.icns` for the Dock: its tile keeps the margin of the system's icon
  grid (824 of 1024 points, over a soft shadow), because the Dock draws an icon as large as its
  canvas and the full-bleed tile of the other desktops would look bigger than its neighbours.
  `img/make-icons.py` derives it from `img/CodeAlta.png`.
- **Linux:** `codealta.desktop` in `~/.local/share/applications` (or `$XDG_DATA_HOME`).

On macOS and Linux the entry starts the tool's launcher (`alta` in the .NET tools folder), so a tool
update needs no change. On Windows, after `dotnet tool update -g CodeAlta` run by hand, start `alta`
once from a terminal: until then the shortcut still names the version that was removed. **Update and
restart** needs nothing, since it starts CodeAlta again through the launcher. The developer instance,
an instance on explicit roots, a build output and a local tool add nothing. Uninstalling the tool
leaves the entry behind; delete it by hand.

The view is an application shell rather than a browser page: the browser's own find, print, reload
and zoom shortcuts, its context menu and its status bubble are turned off, so those keys reach
CodeAlta's commands. Text-editing keys work as usual.

### When the host stops responding

Reloading the window opens a new RPC session with the same host; sessions keep running and prompt
drafts are restored, but image drafts, unsaved file edits and requests that were still waiting for an
answer are lost.

- When the host closes the RPC session of the window (NeoAstra reports the close reason
  `rpc_session_closed`), the window reloads by itself at once. It does not while a file tab holds
  unsaved edits, nor a second time within a minute of such a reload: it then shows **CodeAlta is not
  responding.** with a **Reload** button under the title bar and leaves the decision to you.
- The window also pings the host every 10 seconds; when three pings in a row get no answer it shows
  the same notice, which goes away by itself if the host answers again.

Known causes are fixed in NeoAstra: 0.3.1 no longer treats a channel closed mid-send as a transport
failure, and 0.3.2 fixes a race in its dispatcher that closed the session under ordinary RPC traffic
and tells the page when a session was closed. A `connection_closed` entry in **Application Logs**
names what the host saw.

## Workspace

Settings opens a modal window, initially about 80% of the viewport; the selected session,
composer draft and timeline remain mounted underneath but cannot be interacted with while it is
open. Drag its title bar to move it and its edges or corners to resize it; the geometry is saved in
this WebView's local storage and the title bar's restore button (or a double-click on the title bar)
returns to the default. The expanded prompt editor (F6) is the same kind of window with its own saved
geometry. Use the window's sections, Escape or Close settings to return. Configuration, Providers,
Models, Agent prompts, MCP Servers and Logs are **not** workspace tabs. Session tabs belong to the
global workspace, not the selected project: sessions from different projects can stay open together.
Drag a tab along the tab strip to reorder it, to a pane edge to create a split view, or to its center to merge
(up to 32 open sessions); drag the divider between panes to resize them. The presentation uses
stable content slots and one vertical Explorer with Projects above Sessions. Its width is locally
saved (220–720 pixels, never more than 60% of the window); the Explorer button in the title bar hides it without discarding the width.
The command palette and Settings are the two buttons next to it, right after the CodeAlta mark. Notes belong
to each session, start collapsed when empty, and open when meaningful content arrives. A small disclosure
at the top right of the timeline expands/collapses their floating panel without resizing the timeline or
composer. The panel (or its collapsed disclosure) can be dragged anywhere over the timeline and the open
panel resized; its size and position are kept across collapse/expand and saved locally, relative to the
pane's top-right corner, and its header restores the default. There is no nested notes dock or notes divider.
Project and session rows open floating action menus from their **…** button, context menu or Shift+F10; the
Projects header has a filter box, a sort/actions menu (with **Open project…**) and a **+** button that adds a
project folder with the operating system's folder dialog. A dot-matrix spinner on a
session tab, its sidebar row and its project marks an observed running session, including sessions whose
tab is closed. Existing session-keyed transitions and
draft/uncertain-action guards remain in place.

Session info, Reminders, context usage, timeline details, file changes, tool records, the raw history
source, About, project details, the saved-session browser and the model/prompt choosers open as
windows: drag the title bar to move them, drag an edge to resize them, and use the title bar's restore
button (or double-click it) to return to the default size and position. Each kind of window remembers
its own geometry. Closing Reminders does not cancel an admitted action or retry an uncertain Save.

In the explorer, a project row has one **…** menu (also on right-click): **New session**, **Search
sessions…** and **Browse saved sessions** for that project, then **Open**, **Add to favorites** (or
**Remove from favorites**), **Details**, **Rename project…** and **Archive project…**. **Chats** has
the same session actions. Session search is an inline field above the session list of
the selected project; Escape or its clear button hides it.

The **Chats** are the sessions of no project, which the TUI calls global sessions. Their row is the
first of the Explorer, above the projects, and is closed until it is opened.

Each project opens and closes on its own, and several can be open, each with its sessions. The chevron
of a row opens and closes it without selecting it. Clicking a project selects it and opens it; clicking
the selected project closes and opens it. A project that becomes the selected one in any other way (a
session tab of another project, **Open project**) is opened too. **Collapse all** in the Projects header
closes every project and the chats. What is open is kept with the favorites in this WebView's
local storage (`codealta.desktop.projectTree.v1`) and restored as it was at the next start; with nothing
stored, the selected project is the one open.

Up and Down go through the rows of the Explorer, Home and End to the first and the last. Right opens a
project and Left closes it; Left on a session goes to its project.

The sessions of an open project are the same rows whether it is selected or not: the most recent ones
(the recent-session count of **Settings → Appearance**), **Show more…** for the others. A click opens
the session, which selects its project. **Rename…** and **Delete…** act on the selected session, so in
another project they open the session first. The search field, the form of a new session and the
notices of an unconfirmed action belong to the selected project.

Favorite projects are listed first, under **Favorites**, in the chosen order; the others follow under
**Other projects**. The star of a row and the menu of the row add and remove a favorite. A favorite is
a preference of the window: nothing is written to the project or to the catalog.

A row also has an icon for the changes of its project, one for its code editor and one that opens a
terminal in its folder. These four icons and the **…** button take no room until the pointer or the
keyboard is on the row; the icon of an open Changes tab or code editor stays visible and tinted, and so
does the terminal icon of a project that has terminals. A row that shows such an icon keeps the room of its
other buttons while they are hidden: the icon is in the same place with the pointer on the row or away
from it. A row that shows none gives its whole width to the name. `explorer/projectRowButtons.browser.test.ts`
lays the rows out in a browser and checks where the buttons are in both cases. Icons have the color of
what they stand for: a project is a folder, open or closed (an archive box once archived), a session a
robot head, a session started by another one an arrow under its parent, a session started by an
automation a bolt, and the chats two speech bubbles.
**Rename project…** and a session's **Rename…** open a small popover beside the row, with the current
name selected: Enter or **Rename** saves it; Escape, **Cancel** or a click elsewhere leaves the name
as it is. A rename that is refused says why under the field. A session keeps the name it was created
or renamed with, also when a later Send attaches it again (another model, a restart); a session that
was never named shows the first line of its summary.

Clicking a project opens one temporary **New session** tab, reused when selecting another project
before creation. Selecting an existing session tab or sidebar session removes it. Real session
panes, drafts and split geometry are retained, rather than replaced with a project placeholder.
The welcome view renders the same 3-D FIGlet/ASCII logo asset as the TUI, with the project name,
folder and launch guidance below it. Its composer uses the same surface, status line, editor,
toolbar, expansion and keyboard handling as existing sessions. **Start session** (or Enter) creates
the session, moves the prompt into it and sends it in one step; a text-only prompt needs no second
Enter. Agent/model/reasoning choices are available before creation in the same shared
selector rows as an existing session. Agent choices use the exact project/global prompt catalog;
model choices load for the selected enabled provider. These are local preferences, revalidated
against the created session before transfer, not authority to send or switch a running provider.
Unavailable model catalogs do not remove independently available Agent choices; use the labelled
Refresh composer choices button to retry. The prompt's divider
reserves space within its own pane, independently of session dividers. Sizing observes the
actual portal DOM mount, including late-mounted panes. Session tab dragging uses pointer capture
and public FlexLayout move actions, avoiding native HTML5 drag/drop handling. A highlighted target
previews the split/merge/insertion, and Escape or loss of capture cancels. The tab menu's **Split right** and
**Split below** actions provide a keyboard alternative when a pane contains multiple tabs.

Real FlexLayout session tabs project
existing bounded identities, with compact status/close chrome and secondary Reopen/Refresh menus.
The tab menu uses themed Blueprint controls with an opaque popup and keyboard navigation.
Open sessions retain independent display/runtime/review/notes owners and drafts. Hidden panes
pause automatic history, live, runtime, receipt, Ask, notes and choice reads until visible again;
visible split panes continue observing. A pane that becomes visible again keeps its loaded timeline
without reading history again; only an interrupted page chain continues with its next page.
Hiding a pane does not cancel admitted commands. The focused pane
owns global composer shortcuts. Running indicators in the project/session sidebar refresh in bounded
batches, prioritizing visible and open sessions (at most 32 verified sessions per batch). Missing or
stale observations never imply idle or authorize mutations. Timeline loading supports records up to
8 MiB, with bounded previews and an explicit raw-source inspector showing at most 16 KiB per
chunk. Previously loaded rows survive later page errors with a partial-state notice. Temporary history-read
failures can resume the existing live-revision refresh, with at most two retries for an unchanged live
revision. Explicitly older views and source inspection are not replaced by this recovery. The spinner
uses independent runtime observations; if the live display channel fails, use **Reconnect live activity**
when offered, not Send retry. A spinner alone does not prove that transcript updates are arriving. The first
window is bounded to 1,000 events, 2 Mi text units or 32 automatic pages; **Load previous messages** and
**Load all previous messages** extend it (see "Persisted event history").
Larger records produce an explicit error, not a silent skip.

### Session info

Session Info's **Recorded creation time** is the instant the session's journal header records; a
session whose header cannot be read shows the catalog summary's (possibly cached) timestamp. It is
not runtime start, elapsed time or last activity.

A project whose folder no longer exists is left out of the workspace, with the sessions recorded in
that folder; nothing is removed from the catalog, so they are back when the folder is. A listed
session that was recorded for another project or folder than the one it is listed under opens as
**Session unavailable**.
Year-1/default values are unavailable; no header, update-time, clock or filesystem
fallback is used. The dialog preserves the supplied ISO representation, whose offset
may already be UTC after cache projection. Missing/null/malformed client fields show
unavailable; this tolerance does not promise cross-version RPC contract compatibility.
Opening the dialog adds no metadata reads, and Copy remains session ID only. Existing
snapshot identity/scope checks, catalog freshness and epoch/clipboard limits remain.

### Running sessions

While a session works, an activity spinner shows in three places: before the title of its tab, on its
row and on its project's row in the Explorer, and in the status line above the prompt. For a session
open in a visible pane the three follow the run itself and start and stop together. Sessions that are
not open are checked every five seconds, so their Explorer spinner can lag by that much.

### Projects and saved sessions

**Open project** (`Ctrl+O`) is a resizable window with one field for a saved project's name or a
folder path. Saved projects that match by name or path are listed first; for an absolute path, the
folders below it are suggested as you type (at most 16, never at a filesystem root). Up/Down move, Enter
opens the selected project, Tab or Enter on a suggested folder completes it into the field, and Enter
on the typed folder itself checks it and offers **Trust and open folder**, which adds it as a project.
Escape closes the window. Saved projects use deterministic name order, **not** last-active or recent order.
Opening a project or trusting a folder focuses its prompt; canceling restores focus to the control
that opened the window.

A folder can also be chosen with the operating system's folder dialog (`desktopShell.pickFolder`):

- The button at the end of the field, or `Ctrl+O` pressed again inside the window, opens the dialog in the
  folder the field names. The chosen folder replaces the text of the field and is checked like a typed path
  submitted with Enter; a folder that is a saved project is opened.
- **+** in the Projects header goes straight to the dialog, since it adds a folder. A folder that is already
  a project is opened. Any other folder is shown in the Open project window, checked, with **Trust and open
  folder** focused. Canceling the dialog opens nothing. Where there is no folder dialog (and in a
  catalog-only launch) **+** opens the window, so that a path can be typed.

Choosing a folder never trusts it: the page gets a path, and adding the project still goes through the
check and **Trust and open folder**. One folder dialog is open at a time and it may stay open ten minutes,
the longest an invocation lasts.

On Windows the dialog is the Explorer file dialog in folder mode (`IFileOpenDialog` with
`FOS_PICKFOLDERS`, in `DesktopFolderPicker`): it has the address bar, the search box and a **Folder** field
a path can be pasted into, and it reopens where it was last used. NeoAstra's own Windows folder dialog is
the folder tree of `SHBrowseForFolder`, which takes no typed path; it is only the fallback when the
Explorer dialog cannot be created. On macOS and Linux NeoAstra's folder dialog is the system one
(`choose folder`, the desktop portal or zenity).
Navigation requires the row's unique, unchanged ID/path/name/archive state in the current bounded
snapshot. It selects existing sessions without importing, creating a runtime, or discarding their
drafts. Archived projects are labeled and their sessions open read-only; a catalog-only launch
can also navigate saved projects read-only. **Details** in a project's **…** menu
opens a read-only window with the display name, full recorded path, ID and archive flag, each value
with a copy button at the end of its row. The Chats root has no project details;
missing, duplicated or changed ID/path rows cannot open it. A failed/pending catalog refresh
disables inspection until a successful fresh snapshot, and project, session or host changes
dismiss stale details. It has no total session count, tags, description or source metadata. A copy
button shows whether the copy succeeded; no catalog/filesystem reads, metadata writes or project
actions occur on opening.

**Browse saved sessions** (`Ctrl+Alt+B`, or a project's **…** menu) lists the sessions of the project
(or the global ones) in a table: title, provider, last update and message count. Click a column to
sort, type to filter by title or ID, use Up/Down and Enter or double-click to open. A session that is
running shows a spinner. Exact deletion of several sessions stays in the collapsed panel under the table.

Owned project/global session creation is effectful even while the new session is labeled
**Draft**: it starts a provider runtime, creates a provider session and persists its identity,
without sending a prompt. The host selects its default enabled provider (or first enabled
descriptor); this is not a provider-free draft or a readiness/reservation guarantee. Creation
is single-flight: a concurrent request is `busy`, and canceling a caller's wait does not cancel
the admitted original, which shutdown drains. After settlement, an identical request creates
another session with a new identity; there is no exact receipt-replay guarantee.
`create_unconfirmed` does not establish that no effects occurred. Inspect uncertain outcomes;
neither refresh nor repeating the request proves that an earlier creation did not complete.
Create-and-open completion is bound to its original host, exact scope/path, selection and
view/input lifetime. Leaving and returning, opening Settings or another modal, changing
selection/title/search, closing the form, or publishing another catalog snapshot ends that
navigation authority. A stale completion leaves an inspection notice without clearing newer
input or closing Settings. Its late catalog read cannot publish over the newer context; an
unchanged valid completion still opens the confirmed new session. This is presentation fencing,
not a retained-create receipt, retry permission or proof that an uncertain create had no effects.

In an owned host, if the *current* project becomes
archived after an action was captured, its exact pending/uncertain Send, Steer, host-only Queue,
compaction, observed-run cancellation and Abort/cancel-Queue intents remain visible in a
read-only recovery card for the same unique project path, session and host epoch. The Reminders
view likewise retains exact pending/uncertain Save evidence while disabling Create, Save,
Delete and panel shortcuts. Neither card sends, retries or retargets an operation; a late
  definite outcome can remove an owner intent. Already-admitted Ask answer/cancel, nonsecret
  provider input and command-permission decisions also show their original host, session, handle,
  decision, captured text and pending/late result in a read-only archived card. It never lists,
  observes or acknowledges those owners: in particular, explicitly observing a terminal
  command-permission decision in the mutable review would enable a fresh review, but this card
  does not. No archived controls can resolve, cancel or retry an interaction. These records are
  bounded app-instance evidence, not host execution/completion proof. The archived and catalog-only
  composer is a compact, auto-growing **draft-only** editor: ordinary Enter makes a new line, Send
  is unavailable; Settings opens only from the project rail, shortcut or palette (Ctrl+G, Ctrl+U
  opens Settings when no owned context refresh is available).
  Model/prompt/reasoning are not inferred from unavailable runtime state. Draft restoration uses
  WebView-local storage when available; a failed write does not certify an off-session draft badge.
  Recovery evidence does not persist across app reloads. Missing, changed, duplicated, omitted
  and unreadable entries cannot be opened from a stale list. The separate absolute-folder field
  still requires an owned host, preview and explicit trust confirmation to import an arbitrary
  existing directory.
  Selecting a folder suggestion returns keyboard focus to the absolute-folder field before the
  focused option disappears, so Escape can close the dialog and cancel its pending suggestion
  wait. A canceled waiter does not establish that a blocking host enumeration has stopped.
If Refresh projects fails, saved selection pauses until a successful refresh.
During an admitted import, its original host epoch, requested/verified paths and pending or
uncertain outcome remain visible when Open project is closed and reopened. Saved selection and
new folder requests remain blocked across dialog remount and project-list refresh; refresh only
observes the catalog and never retries the import. A definite refusal unlocks the dialog; a
confirmed import only navigates after exact catalog verification without changing the selected
project in the meantime. An uncertain outcome requires app reload after external inspection.
The shared catalog caches its snapshot, and closing/reloading the document is not a refresh
or run-abort contract. Relaunch with a fresh browser data directory to load another snapshot.
Canceling an RPC waiter does not stop the shared catalog's background load. Existing RPC
teardown waits only a bounded time, so cache work can outlive bridge teardown.

## Composer

A prompt sent while the session works is never refused. `Enter` during a running turn, or behind
prompts that already wait, adds the prompt to the queue; `Ctrl+Enter` sends it to the running turn as
steering, and sends it normally when no turn runs. Both wait in compact rows above the prompt card:
steering first, then the queue in the order it is sent. A queued row has a repeat count (stepper or
typed number), copy, edit, **Steer now** and delete; **Clear queue** or `F10` removes the prompts
that have not left, and `Ctrl+Enter` with an empty prompt steers with the first queued prompt.

When the session is idle the first queued prompt leaves as a normal Send, with the selection, the
project references and the images of that moment, also while its tab is hidden. A Send the host
refuses because the session started working puts the prompt in the queue instead of failing. A
steering row reads **Steer pending** until its message appears in the timeline or its turn ends;
deleting it then only removes the row, since the agent may already hold it. Steering that cannot
reach the turn becomes the first queued prompt, and a prompt with images is queued rather than
steered. The Send button is a split button: its caret chooses **Send now** or **Enqueue until
idle**, which queues every prompt. A request the host did not confirm keeps its key and offers
**Try again**; nothing is sent twice or retried by itself. The rows are app memory only, not kept
across reloads. A Send that is not accepted for another reason leaves the prompt in the composer
and says why in the status line: a prompt is never answered with a toast. The bottom bar shows the
next-Send agent prompt, provider, model and reasoning effort as one clickable summary; it opens a
popover to change them (reasoning is a stepped slider over the model's supported efforts) and to
browse the agent-prompt and model catalogs. Enabled-provider readiness appears in the same bar.
The expanded prompt editor retains file insertion without reference-inspection diagnostics.

The model list holds the models the provider reports and the slider the efforts of the selected
model: neither has a "default" entry, and a model without reasoning shows **None**. A draft or a
session that has no model yet starts as in the TUI: with the provider's configured model when the
provider lists it, otherwise with the first model listed, and with the provider's configured effort
when the model supports it, otherwise High, the model's own default or its first effort. The host
applies the same rule (`AgentModelDefaults`) to what it reports for a session and to a Send that
selects nothing, so a Send never reaches a provider without a model while the provider lists one.
Choosing another model starts it with its own effort. A saved model the provider does not list is
kept and shown as **Unverified**; settings change again once a listed model is chosen.

The provider indicator is a compact active-provider count, green when ready and orange when
providers fail or are unsupported. Owned startup initializes the configured providers, as in
the TUI; inventory reads themselves do not probe. Compaction has a persistent icon, disabled
until an idle attachment is verified. Send and Stop occupy one slot, not two adjacent buttons.
Missed background reads retain receipts and ask drafts and recover on the next observation;
prolonged unavailability shows a small status indicator rather than raw timeline diagnostics.

Runtime and receipt observation errors appear inside the selected session timeline, never below the
prompt. An Ask read failure is an error, not a pending question. "Pending asks" is reserved for a real
pending backend ask; retained answers and recovery drafts remain available.

### File and issue references

Typing `@` at a word start in a prompt opens the **Project files** window. It lists the project's
files and folders from the same index as the TUI (`.gitignore`-aware, recently used first, fuzzy-ranked
as the query grows, at most 64 rows) with a colored icon per file type, the name and its folder.
Up/Down, PageUp/PageDown and Home/End move the selection, Enter replaces the `@query` with a
Markdown link (`[name](relative/path)`) and Escape leaves the text as typed. Typing `#` the same
way opens the issues of the project's hosted repository (**GitHub issues**, **GitLab issues** or
**Azure DevOps work items**, from the project's git remote): number, title, state and last update,
most recently updated first, 50 at most. The search field matches a number or title words,
**Include closed** (`Ctrl+I`) filters closed issues, and Enter inserts `[#123](url)`. The credentials
come from the provider's environment variable or CLI (`GITHUB_TOKEN`/`GH_TOKEN` or `gh auth token`;
`GITLAB_TOKEN` or `glab config get token`; `AZURE_DEVOPS_EXT_PAT` or `az account get-access-token`);
pull and merge requests are not listed.
Both windows are resizable and remember their size. Catalog-only and unverified inputs have no picker.

### Images of a prompt

Clipboard PNG, JPEG, WebP, GIF and BMP files pass through the browser decoder and are normalized to
PNG before wire validation, so editor-generated PNG metadata, palettes and interlacing are accepted.
Paint.NET clipboard data must be exposed as an image file by Chrome/WebView2; there is no direct native
clipboard reader. A conversion error does not mean that the selected model lacks image support.
Animation is reduced to one frame and source metadata is not retained. There is no per-image size,
count or dimension limit beyond the inbound RPC frame allowance of 128 MiB, which includes the
base64/JSON overhead. Image-bearing prompts use the normal 32,768-character text limit. PNG integrity,
observed model capability, exact draft revisions and host/session ownership are validated.

A prompt sent with pasted images shows them in its **You** card, as a row of thumbnails under the text
(or alone, for a prompt of images only). The card's text is what was typed: the lines that name the
image files are left out, and the card's **Details** list the images without their paths. Thumbnails
appear as soon as the prompt is sent, from the images the page still holds, and stay when the persisted
message replaces the pending card. Images of earlier messages, including those sent from the terminal
UI and those of an archived project's sessions, are read from the host when their card scrolls into
view; the 32 most recently shown (48 MiB of text at most) are kept, so a card drawn again does not
read them again. A tile is neutral while its image is read and shows a
crossed-out picture when the image cannot be shown (the file is gone, is not a supported image, or is
over 8 MiB); clicking that tile reads the image again.

Clicking a thumbnail opens the image in a window titled with the image's title. The window opens at
the image's own size when it fits and otherwise at no more than 80% of the main window's width and
height, with the image scaled down to fit and never enlarged or distorted. It moves and resizes like
the other windows, and its title bar restores the default size. `Left`/`Right` or the two arrows in
the title bar go to the previous or next image of the same message (past the last comes the first),
and `Escape` closes the window.

### Images a tool gives the model

When a tool result has images (`view_image` on a file, a screenshot tool of an MCP server), the timeline
shows an **Image read** card after the tile of the tool call. The card names the images and shows them as
previews, larger than the thumbnails of a prompt; a click opens the same image window. The tile of the call
keeps the text of the result. The card ends the group of tool calls it follows, so it sits where the image
was read.

The history row of the tool output lists its images by index, title and media type
(`HistoryImageProjection`), and `promptImages.read` serves them by the journal offset of that row. The
rules are those of the images of a prompt: the path comes from the journal record and never from the page,
the file must be inside the session's attachment folder, and neither the output row nor the details of the
tool call name the file.

### Asks and plan review

An agent asks the user with **`alta ask --stdin`**: one or more questions, and optionally a file to
review. This is how the **Plan** agent prompt ends a planning turn: it saves the plan under
`.alta/plans/` and asks for its review together with the questions that remain. The ask opens once the
run that asked has ended, as in the terminal UI:

- **The questions take the place of the prompt.** Each question is a tab (`Title →`, the last one
  `Title ✓`) with its text, its description, its choices as a single selection (numbered, the first one
  selected) and, when the ask allows one, a text answer. `Enter` and the button go to the next
  question that was not shown yet (**Next**) and send once all were shown (**Submit**); `Shift+Enter`
  is a new line in a text answer. `Ctrl+N` / `Ctrl+P` and, outside text, `Left` / `Right` change the
  question; `Up` / `Down` change the choice. A question may be left without an answer.
- **A file to review takes the place of the timeline.** It is the file's source in an editor, with
  line numbers, the highlighting of its type and wrapped lines, under a header `File context: <path>`
  (` *` while it has unsaved edits). `Ctrl+K`, the **Comment** button or a click in the margin beside a
  line adds a comment on that line: a **User Comment** box under the line, one per line, marked in the
  margin. In a comment `Esc` finishes it (a check mark shows it is done), `Ctrl+D` deletes it and
  `Ctrl+N` / `Ctrl+P` go to the next and previous comment; **Clear comments** removes them all. The
  file can be edited and saved (`Ctrl+S`, **Save**); comments stay on their lines while it is edited,
  and a file that changed on disk asks before it is overwritten.
- `Ctrl+G Ctrl+E` (**Go to Ask File**) moves to the file and `Ctrl+G Ctrl+P` (**Go to Prompt**), or
  `Esc` in the editor, back to the questions.
- **Submit** sends one prompt to the session that asked: the file, its comments by line (every comment
  with text, finished or not), whether the file was edited and saved, and the answers. It is an
  ordinary **You** card in the timeline. With unsaved edits it first asks **Save and submit**,
  **Submit without saving** or **Keep answering**.
- **Cancel** (`Esc`) asks before it withdraws the ask (**Exit without responding**); nothing is sent
  and nothing is stopped. With unsaved edits it offers to save them first.

The timeline and the prompt return when the ask is answered or cancelled. A session without a project
has no folder to read the file from: its questions are shown and the file is reported as unavailable.

An ask holds at most 8,192 UTF-16 units of text and a file path of 1,000; an answer holds 8,192 units
of text, and its review at most 200 comments of 4,000 units each, 16,384 in all. A run can ask once,
and only a run started from the window can ask; asks do not survive closing the application. When the
ask an answer was written for changes or disappears before it is sent, the text stays in the timeline
as an **Unsent answer** that can be copied or discarded, never sent to another ask. When the host does
not confirm an answer or a cancel within eight seconds, the timeline says so with a **Check** button
that reads what the host recorded, without sending again.

### Reminders

The **Reminders** session tool is available only with a selected owned session. Its window lists that
session's reminders on the left (state, attempts sent, interval) and shows either the **New reminder**
form or the selected reminder on the right. A new reminder has a Markdown prompt, a delay (`5m`,
`1h 30m`, `90s`, whole seconds or `HH:mm:ss`, at most 24 hours; quick buttons for 5m, 15m, 1h, 4h)
and 1–20 attempts. **Delete reminder** asks for one confirmation. At most 32 reminders per session and
256 per host are kept; they live in memory and are lost when CodeAlta stops. The composer's timer
button shows the session's active count, and the explorer marks every session and project that has
active reminders; both follow reminder changes made in the window and are re-read every 20 seconds.
The timer icon and `Ctrl+G`, `Ctrl+D` open Reminders.
Select a schedule to inspect its full stored message, beyond the list preview. **Use as new reminder**
copies that message, delay and repeat count into the local Create form (with confirmation before
discarding an edited draft); it does not change the selected schedule or create a new one until
Create is explicitly submitted. **Save message** edits only the full message of an active selected
reminder, using the exact host, owning session, reminder ID and the detail snapshot's edit revision.
It is separate from Create/Use as new and the exact-ID Delete confirmation. Unsaved edits require
confirmation before switching reminders or discarding the edit draft; a concurrent edit (including
one later changed back to the original message), missing reminder or completed schedule refuses Save
without overwriting it. Save preserves the delay, due time, repeat count and attempt counts: it only
affects future firing captures, never an already captured or admitted send. Pending or uncertain Save
admissions remain held for their original host/session, with no automatic retry; refresh observes
state but cannot certify an uncertain response. The exact pending/uncertain Save's host epoch,
session, reminder ID, original revision and full submitted message remain visible for that target
even if its list/detail read fails or the reminder disappears; switching targets does not show
another session's retained message. While viewing that target, a local unsaved edit remains
selectable for recovery after conflict, failed detail/list refresh or deletion, even with an empty
list; discarding that draft requires confirmation and never clears an outstanding Save admission.
A refreshed detail does not silently rebase an old draft or retry it.
Within the eligible panel, `Ctrl+R` refreshes from non-editor focus, `Ctrl+E` focuses the selected
active message editor, and `Ctrl+S` saves a changed message from that editor or a non-editor panel
control. `Delete` from non-editor focus only focuses the **Delete reminder** button; it
never submits deletion, and Delete in a text field remains text editing. Shortcuts do not run during
inline discard confirmations, modal/IME input or without current host authority. Pending or uncertain
Save blocks mutation/focus shortcuts; `Ctrl+R` remains read-only observation, never a retry. Create
still requires its explicit button; `Ctrl+Enter` is not mapped in this panel.
Schedules and results are **in memory, not persisted**:
closing/restarting the host loses them. The host
owns the timers and attempts one normal owned Send per firing, with the same permission policy
and host drain as other sends. Busy, unavailable or failed sends count as failed attempts; there
is no automatic retry beyond the requested repeats. A completed reminder means all attempts
finished, not that the agent responded. A deletion cannot retract an already captured delivery
or an admitted run. Counts are point-in-time as of Refresh; a lost mutation response is held as
uncertain for that host/session, with no automatic retry. Catalog-only mode cannot schedule work.

### Context usage meter and window

The composer of an owned session shows a compact context meter: a small bar, the used percentage and
the token counts (`46% 125k / 272k`), green below 75%, amber from 75% and red from 90%, as in the TUI.
It reads `sessionUsage.read` when the session is shown and again, at most every four seconds, while a
run produces events; until the host has observed usage (for example right after opening a saved
session) it shows the last usage record of the loaded timeline. Clicking it opens the **Context usage**
window with what the TUI popup shows: provider and model, the context window (percentage, used and
limit, messages, active context against input headroom, indicative model limits), the last operation
(input, output, cache read, cache write, cached input and reasoning tokens as a breakdown bar, with
effort, initiator, duration and cost), rate limits (plan, primary and secondary windows with used
percentage, window length and reset time) and the provider's session totals. **Copy as Markdown**
copies the same content; **Refresh usage** reads again.

`sessionUsage.read` is an owned-only generated Desktop RPC for an existing actor and the actual host
epoch. It requires explicit project ID/path or global scope, checks the exact persisted session header,
confirms unarchived project ownership when applicable, and rechecks the original attachment after
asynchronous reads. It returns the last admitted usage event: numeric counters as decimal strings,
bounded single-line text (model, effort, initiator, labels, plan; anything longer than 128 characters
is dropped), rate-limit windows and the provider's cumulative session totals. Providers split usage
across events, so the window keeps the newest value of each field seen on the same attachment and
starts over when the attachment changes. Copilot quota snapshots, named Codex limits and compaction
details are not shown.

## Timeline

### Timeline cards

The timeline is compact: every card has the same small padding and the rows are spaced by the list
alone. Consecutive tool calls of one run share a single **Tool calls** card, their tiles side by side
(up to 60 per card); only something the timeline shows between two calls (an assistant message, a file
change, a status row) starts a new card. Records that show nothing, such as usage updates or reasoning
without text, do not.

A **Compaction completed** row's **Details** shows what the terminal shows for a local compaction:
context before and after with the ratio and the target, the messages summarized and kept, the
summarizer's calls and budgets, what fed it (messages, tool calls and outputs, reasoning, files) and
the checkpoint summary. The host sends those figures as a small record and the summary as text, because
the stored details are larger than a history row may carry.

Nothing is written above the prompt. Message navigation (`F3`/`F4`) is announced to assistive
technology only, and a Send that was not accepted is reported in a toast at the top right of the
window.

### Turn statistics

When a turn ends, the built-in Statistics plugin adds a **Turn statistics** row after it, as in the
terminal UI: duration, input and output tokens (the provider's totals when it reports them, an
estimate otherwise), tool calls with their total time, and compactions. The row's **Details** button
opens the full tables: sizes of the prompt, answer, reasoning and tool traffic, latencies and speeds,
the provider's usage figures, and one line per tool bucket. The rows are not stored in the session;
they are computed from its events each time, so earlier turns get theirs when **Load previous
messages** brings them into view, and a failed turn has one too.

The page asks for these rows through the `sessionPluginEvents.read` RPC. The host derives them from
the journal: the Statistics rows with a projection of its own, and the timeline cards of the other
plugins with the projections the plugin runtime holds. It reads
the journal backwards from its end until the oldest turn shown is complete (at most 64 pages of 100
records; a turn that begins further back gets no row) and returns at most 32 rows, newest kept.
With the Statistics plugin turned off in **Settings > Plugins**, the next read (the next turn, or
reopening the session) returns no rows.

### Persisted event history

Selecting a session shows its latest turn: bounded pages of its persisted canonical events, up to 1,000
events. One button above the first message brings in what came before, with a caret for its second
action, like Send:

- **Load previous messages**, the button itself, reads one chunk: whole turns, at least 300 records.
- **Load all previous messages**, in the caret's menu, reads back to the start of the session, page after page. The timeline
  takes what was read in small batches while a counter shows the records loaded; **Stop** keeps what
  was read and ends at a whole turn. A session of tens of thousands of records takes about a minute.

Either way the reader stays on the messages in view, and nothing newer is dropped: the window grows (up
to 200,000 records or 128 Mi text units, after which it slides) and later refreshes keep it. Rows away
from the view are not laid out until they are scrolled to. The chronological timeline folds duplicate stream completions and tool lifecycle/output
rows for presentation; this does not change persisted records. Tool cards identify the tool and primary
command/input. Prompt, usage, model and secondary event data use compact summaries and disclosures, and
internal raw persistence records duplicated by typed events are hidden rather than shown as empty cards.
Permissions and other stored requests are historical records, not actionable approvals. Previews can be
shortened explicitly; arbitrary provider payloads are not sent to the frontend.

Persisted user and assistant message bodies longer than 1,200 UTF-16 units initially show a plain-text,
inert excerpt of at most 240 units (not sliced/rendered Markdown). **Show full message** renders the entire
already retained body through the normal sanitized Markdown renderer; **Collapse message** restores the excerpt.
Both controls work with keyboard or pointer. Copy Markdown still copies all retained text while collapsed;
shortening/omission notices stay outside the disclosure. Copy success/failure feedback belongs only to the
current retained record and latest click; changing source or leaving the row clears it. An already dispatched
clipboard write cannot be undone. This is window-local per-record presentation state,
not a read for omitted text, a stored preference or a reconstruction of live output.
Equivalent refreshes and retained older-page records preserve intentional expansion. A changed body,
displayed record identity or omission evidence clears it, including a short-body interlude or A→B→A
replacement; returning to an earlier source does not restore its previous expansion.

### Markdown and useful HTML boundary

Timeline, live text and Notes use markdown-it **15.0.2** with the CommonMark preset,
raw HTML enabled, explicit pipe-table/strikethrough/linkify rules and hard line breaks.
This is not full GFM or a CommonMark-conformance claim. Task markers remain literal;
strike renders as `s` rather than `del`. Grid tables, footnotes and task-list plugins
are not implemented. Footnote-like
syntax may parse as an ordinary reference link instead. Mixed inline HTML/Markdown
renders; Markdown inside block HTML follows CommonMark blank-line boundaries, not
arbitrary nested Markdown interpretation.

The browser-independent parser returns **untrusted HTML**, never directly injected.
Each mounted `MarkdownContent` owns its parser and DOMPurify **3.4.14** instance/hooks.
The explicit boundary retains paragraphs, breaks, headings, blockquotes, lists and
definition lists, pre/code, ordinary text formatting, links, span/div, authored
tables/captions/sections/cells, and details/summary. The precise tag list is
`p br hr h1 h2 h3 h4 h5 h6 blockquote pre code ul ol li dl dt dd strong em s del b i u
sub sup kbd samp var abbr a span div table caption thead tbody tfoot tr th td details summary`.
Attributes are limited to title, safe anchor href, bounded code language class,
details open, th scope, cell alignment/spans (1–100), and bounded list start/value/reversed.
Table alignment uses `align`, never authored CSS. Authored IDs, app classes, handlers,
ARIA/data/role/tabindex authority, scripts, styles, forms, frames, objects, embeds,
images/media and SVG/MathML are not accepted. Sanitized pre/code can receive only the
renderer-owned harmless timeline region attributes; no markup can create app controls.

**Fenced code blocks.** A fence that names a language is colored with highlight.js **11.12.0**: its
common set (C#, C/C++, JavaScript, TypeScript, Python, Go, Rust, Java, Kotlin, Swift, JSON, YAML,
XML/HTML, CSS, SQL, shell, diff, Markdown and more) plus CMake, Dart, Dockerfile, batch, Elixir, F#,
Haskell, HTTP, nginx, PowerShell, properties, Protocol Buffers and Scala, under their usual short
names (`cs`, `js`, `ts`, `sh`, `ps1`, `yml`, `jsonc`, `csproj`, ...). The block shows its language in
its top right corner. A fence without a language, with an unknown one, or longer than 200,000
characters stays plain. The colors follow the theme and the color scheme.

A `mermaid` fence is drawn as a diagram by Mermaid **11.17.2**, in the colors of the theme and color
scheme, and drawn again when either changes. Text that is not a valid diagram, or is longer than
20,000 characters, stays a code block labelled `mermaid`. A diagram is drawn shortly after its text
stops changing, so a message still being written shows the code first. Mermaid is loaded when the
first diagram is drawn. Copying a message still copies its Markdown source, fences included.

Both happen after sanitization, from the block's text alone: the sanitizer still refuses authored
classes, styles and SVG, and highlighted code contains only `span` elements with highlight.js classes.
Diagrams are drawn at Mermaid's `strict` security level, which encodes markup in labels, disables
click handlers and sanitizes the SVG.

Markdown images become **escaped alt text**; authored image/fetch nodes are removed.
Only credential-free absolute HTTP(S) anchor href survives; relative, mailto, custom,
executable and credentialed URLs do not. Fuzzy www/email/IP linkification is disabled.
Trusted container click, auxiliary-click and Enter handling suppresses link navigation;
there is no external opener or bridge action. Not every native
gesture is suppressed. Raw source Copy, including CRLF and fences, remains independent of the
rendered display. Parser/sanitizer errors display inert original source, never exception
details. Equivalent source preserves rendered DOM/focus/selection/inner scroll.

Production CSP is unchanged and **`base-uri 'none'` remains required**: hostile base
markup can produce blocked base-assignment diagnostics during parsing/removal even
when final DOM is clean. Mounted production-component tests use fake in-memory HTTPS
assets, pre-navigation interception and 600 ms deferred observation; unexpected requests,
navigation or execution fail even if intercepted. This finite browser evidence is not
a security audit or equivalence to `app://codealta`/native WebView2. No CSP relaxation,
iframe workaround or blanket HTML escaping is used.

Code blocks in persisted timeline Markdown (including Markdown details) wrap within the card and
use at most 14 rendered text lines plus padding/border; short blocks keep their natural height.
All retained code remains selectable. Tab enters the labelled code region; arrows, Page Up/Down,
Home and End navigate it, and Tab/Shift+Tab leave it. A visible focus ring identifies the region.
Wheel/touch input scrolls code while it can move; at a boundary it may scroll the timeline and change
follow normally. Whole-message Copy still captures the full retained Markdown, not the code viewport.
Equivalent history refreshes preserve code focus, selection and position. This does not change
Notes, live Markdown, or the separate diagnostic Wrap/details control.

Tool and file cards keep available persisted diagnostic/output detail text behind their collapsed **Details**
disclosure. Inside it, **Wrap lines** is on by default and can be switched per card with a pointer or keyboard;
turning it off scrolls long lines inside the detail pane. This only changes how the already loaded plain text
is displayed: Markdown and copied content remain unchanged, and omitted or shortened details remain marked.
There is no request for missing output, additional history, or live provider data when opening or wrapping a card.
Persisted FileChange activities label this disclosure **File change record details**, regardless of phase
or command presence. This identifies the supplied record, not applied changes, a complete diff or file-navigation authority.
Persisted ToolCall messages hidden by a primary command/query/path/prompt summary remain available inside
that disclosure as **Supplied activity message**, and in full retained-source Copy. The exact bounded message
is not interpreted as an output or verified outcome; existing phase labels and omission/shortening warnings
remain unchanged. Failed/no-summary messages still appear in the body without duplication. Live tool cards
continue to show only their supplied name, reported phase and identity.
Following the loaded timeline stays at the latest visible bottom across layout-only changes. Wheel, scroll-key,
scrollbar (including a held drag after a pause) and touch-drag navigation can still unfollow when it coincides
with a detail layout change; merely
clicking a disclosure/Wrap control or scrolling inside its detail text does not opt out. An explicitly unfollowed
reader is not moved to the newest journal window.

### Message navigation and history reads

In the focused session workspace, `F3`/`F4` move among **persisted user and assistant
messages in the currently retained window** (not live projection, tool, reasoning,
unknown-kind or status cards); a live-only window does not enable these keys. `Ctrl+F3` moves
to its first retained message, which may not be the journal's first message. These
keys unfollow the timeline. They do not fetch
older pages or infer that a running session has finished persisting events. Use
**Load previous messages** or **Load all previous messages** to bring in older history. **Refresh newest history** and `Ctrl+F4`
explicitly read the newest persisted window (up to 1,000 events), including when
browsing an older page; only `Ctrl+F4` opts into follow after all matching pages
have settled. A failed or superseded read cannot claim success or move to an older
window's bottom. Scrolling, changing selection/host or another navigation action
cancels the pending keyboard follow intent; it does not cancel a completed read or
automatically retry. The bottom button follows the displayed window only and labels
older windows accordingly. This is a snapshot of the last successful explicit read,
not proof that a running provider has finished writing its journal.

Loaded-message navigation keeps keyboard focus and uses the current viewport or
retained DOM-row anchor. Unlike TUI index navigation, next-at-last remains paused
at the retained-window boundary; `Ctrl+F4` is an explicit read, not a loaded-last
jump. Composer/editor and modal shortcuts retain ownership.

The shared reader limits page input to 256 KiB, individual records to 128 KiB and work to
100 physical records, plus bounded framing probes. Blank and metadata-only records count,
so an empty visible page can still offer continuation. UTF-8 journals with optional BOM
and LF/CRLF framing are supported; other legacy encodings/framing and oversized records
surface explicit limitations instead of unbounded reads. A malformed final record may be
omitted with a notice; interior corruption is an error. Existing complete-history readers
are unchanged.

A failed history, tail, timeline or source read answers only a stable status code, never
exception text. The host logs the exception it omitted as a `CodeAlta.Desktop.History`
warning naming the route, the session and the code (plus the reader's own code when it was
mapped to `read_failed`); `history_changed` is routine during a live turn and is not logged.
A request that never reached the host or never answered logs only its RPC failure code to the
WebView console (`[CodeAlta History]`). Over-long provider item identities are compacted, not
rejected (see "Committed live display window" in `runtime.md`).

Cursors belong to one session and journal length/time stamp. A detected change requires
restarting history from the beginning; it does not refresh the shared session catalog.
Length/time detects ordinary changes, not same-stamp rewrites or every external-writer
race. Selecting another session, changing page or closing the view cancels the waiter and
suppresses late results. These checks are not reparse
isolation or live recovery.

### Notes

Each open owned-host session has a read-only **Notes** overlay at the top right above its timeline.
It travels with that session pane, starts collapsed when empty, and can be expanded/collapsed with its
labelled buttons or the existing notes shortcut. It does not consume composer space. It reads current durable notes
automatically, refreshes every ten seconds after the previous read completes, and supports explicit
**Refresh notes**. It displays the latest stored notes as sanitized Markdown. Complete notes up to
262,144 UTF-16 units are shown without truncation; larger or invalid text produces an error. Empty
notes, cleared notes and no notes event share the same empty result; a failed read is distinct.
There is no editing, automatic mutation retry or browser persistence. This feature does not require ask opt-in.

Selection/remount changes detach presentation but retain the original read. While it is pending,
another local refresh is refused; after failure, the next refresh is a new read, not a recovered write
outcome. Host identity change requires reload before further operations. Notes shares the host's
eight actual workspace/history reads, and a cancelled/timed-out wait does not release a still-running
backend read. The display limit does not bound journal scanning or latency: the first notes read of a
session after host start scans its whole journal. Later reads reuse that scan, so the ten-second refresh
of an unchanged journal does not open it and a grown journal is read only past the records already
scanned. A notes scan does not hold the journal gate, so it does not delay timeline pages or event
appends for the same session. Reload can perform a
fresh durable read without restoring run/queue/interaction authority.

## Settings

### Appearance

**Settings → Appearance** manages the language, the theme (Dark, Light, or Auto, which follows the
operating system), a darker dark theme, the color scheme, project sorting, the recent-session count and
desktop project-rail collapse. The button before the window controls at the top right switches between the
three themes. The rail's Sort projects selector and
Show/Hide projects button use the same live preferences; changes apply immediately and are
saved only to this WebView's local storage (theme, darker, colorScheme, projectSort, projectRail and projectTree v1 keys).
The user's own color schemes are files of the profile (see below).

The rows of the page follow the width of their card, not of the window: the Settings window has a size of
its own. A control is beside the name of its row while there is room for both, and goes below it, over the
width of the row, when the card is narrow (a container query on the card, at 480 px). A switch stays beside
its name, a group of buttons breaks into lines rather than leaving the card, and at the smallest size of
the Settings window the theme buttons drop their icons. The colors of the scheme editor do the same: the
well and the text of a color go below its name. `appearanceLayout.browser.test.ts` lays the card and the
editor out from the smallest width the Settings window gives them to a wide one, in every language, and
fails when a control covers its name or leaves the card.

**Darker dark theme** deepens the dark theme of every scheme, wherever the dark theme is shown (Dark, or
Auto on a dark system). It is not a dimmer: `darkerPalette` in `frontend/src/colorPalette.ts` lowers only
the surfaces (black and the five dark grays, which are the inset color, the window background, the panels
and what is raised above them) by 0.06 of Oklab lightness, each keeping its tint, and leaves text, mid
grays and accents as they are. On Blueprint's own palette the window background goes from `#1c2127` to
`#101317` and the panels from `#252a31` to `#181c21`: clearly darker, and still a dark gray rather than
black. The darker theme has no palette of its own: it is always made from the dark one.

The color scheme is chosen in a dropdown that shows every scheme with its swatch for the theme on screen.
Choosing one applies it at once and leaves the list open, so that several can be tried in a row; Up, Down,
Home and End move through the list. It is **Blueprint** (Blueprint's own palette, the default) or one of the RootLoops
schemes that the TUI gets from XenoAtom.Terminal.UI: Cherry, Tomato, Orange, Pineapple, Apple, Kiwi,
Kale, Blueberry, Plum, Elderberry, Blackberry and Raspberry. Each has two variants that follow the
theme: its *Dark Soft* recipe for the dark theme and its *Light Soft* recipe for the light one. A scheme
recolors the whole window, Blueprint components and the code editors included.

A scheme is nothing but a redefinition of Blueprint's palette variables (`--bp-palette-*`). The page sets
the ones that differ from Blueprint's own as custom properties of the document root (`applyPalette` in
`frontend/src/colorSchemes.ts`) and removes them again for another scheme. The root's `data-color-scheme`
attribute names the scheme and `data-palette` carries a signature of what is set: the code editor and the
diagrams, which draw with the window's colors outside CSS, follow `data-theme` and `data-palette`.

- `frontend/src/colorSchemes.gen.ts` is generated by
  `dotnet run --project src/CodeAlta.ColorSchemes.Generator`: Blueprint's own palette and, for each
  scheme, the whole palette of its dark and light themes. Run it again when the scheme recipes of
  XenoAtom.Terminal.UI or Blueprint's palette change, and commit the result. It reads Blueprint's palette
  from `frontend/node_modules`, so the frontend must have been built once.
- Every palette step keeps Blueprint's lightness, because Blueprint derives more colors from its
  palette by lightness arithmetic and tuned its contrast for those steps. The grays take the hue and
  chroma of the scheme's neutral ramp (background, black, bright black, white, bright white, foreground)
  at their relative place between window background and text; each color family (blue, green, red,
  orange, …) takes the hue and the share of chroma of the nearest scheme accent.
- Blueprint compiles most component colors to literals. The Vite build (`blueprintPalette` in
  `vite.config.ts`) rewrites every literal of Blueprint's stylesheet that equals a palette color into the
  palette variable, so a scheme reaches all components and the default renders exactly as before.
- Blueprint computes the background of cards and inputs from a mid gray (`--bp-surface-background-color-default-rest`),
  which a palette with other surfaces would leave where it was. `style.css` binds it to the palette's own
  inset step (black in the dark themes, white in the light one).
- The app's own colors (`--bg`, `--panel`, `--text`, `--accent`, … in `style.css`) are defined from the
  palette variables and follow the scheme. Monaco takes its surface colors from them while a palette is
  set (`data-palette`: any scheme but Blueprint's, the darker theme, a custom scheme) and keeps its own
  with Blueprint's palette.

#### Custom color schemes

**Customize**, beside the dropdown, makes a scheme of the user's own from the selected one; **Edit** opens
one of the user's. A custom scheme is a built-in scheme (its *base*) with some colors chosen, for each of
the three themes:

| Color | What follows it |
| --- | --- |
| Background | The window background. The other surfaces (panels, what is raised, the inset of cards and inputs) keep their distance from it and take its tint; a background with a clear tint also turns the grays and the text to its hue. |
| Text | The text, and the steps of gray near it. |
| Muted text | Muted text; icons and borders (the mid grays) follow it. |
| Accent | Buttons, links, selections: the blue and indigo families. |
| Success, Warning, Danger | The green, the orange and gold, and the red families. |

The editor is a panel of the Appearance page: a name, the theme whose colors are edited (Light, Dark or
Darker, which the window then shows), and for each color a well that opens the color picker of the system,
the color as `#rrggbb` text, and a reset button once the color is chosen. The window shows the scheme while
it is edited (`ShellAppearance` listens to an `AppearancePreviewStore`, so that a color being dragged
repaints the palette and not the whole application) and nothing is written before **Save**; **Cancel**,
leaving the page or closing Settings gives the window its own appearance back. A saved scheme can be
duplicated, shown in the file manager and removed; removing the selected one selects its base.

A color that is not chosen follows the base. The darker theme follows the scheme's own dark theme (made
darker as for any scheme) and then takes the colors chosen for it, so an accent chosen for Dark is the
accent of Darker too. The page makes the palette (`adjustPalette` in `frontend/src/colorPalette.ts`): every
chosen color is shown exactly as given, and the steps around it keep their structure. Dark surfaces move
with the background in display-encoded lightness, where a black background still leaves the panels above
it visible (`#080808` gives panels at `#111111` and `#1b1b1b`). Where a chosen accent would leave the
white text of a filled button unreadable, the page sets Blueprint's `--bp-intent-*-foreground` to the
palette's black.

The schemes are files, one per scheme, in `~/.alta/color-schemes/` (the developer instance shares them).
The file name without `.json` is the scheme's id; the file holds only what the scheme chooses:

```json
{
  "name": "Deep Sea",
  "base": "plum",
  "dark": {
    "background": "#0b1d2a",
    "accent": "#33ccff"
  },
  "darker": {
    "background": "#05101a"
  }
}
```

`base` is the id of a built-in scheme (`blueprint` when absent; one that this version does not have starts
from Blueprint's); `light`, `dark` and `darker` hold `background`, `text`, `muted`, `accent`, `success`,
`warning` and `danger` as `#rgb` or `#rrggbb`. A file can be written by hand: comments and trailing commas
are accepted, unknown values are ignored, and a file that is not a scheme is listed under the dropdown
with the reason instead of being dropped silently. The page reads the folder when it starts, when the
Appearance page or the dropdown opens and when the window comes back to the front, so a file edited in
another application shows on return.

The `colorSchemes` RPC service (`ColorSchemesService`) only reads, checks, writes and removes these files
(`list`, `save`, `delete`) and shows one in the file manager (`reveal`). It needs no host, so it also
answers while a configuration file is repaired; a window that browses a copy of a catalog has none
(`unavailable`). It lists at most 64 schemes of at most 16 KB each, writes a scheme through a staged file,
names a new file after the scheme's name (`deep-sea.json`, then `deep-sea-2.json`), and refuses ids that
are not plain file names. The selection stays in the WebView's local storage (`custom:<id>` for a custom
scheme), together with a copy of the selected custom scheme, so that the next start shows it before the
host has answered and the configuration repair screen shows it without a host. The copy follows the file.
While the file of the selected scheme is missing or is not a scheme (a syntax error while it is edited by
hand), the window keeps showing the copy and the selection is not rewritten; saving the scheme from its
editor writes the file again.
If storage is invalid or unavailable, the screen reports the fallback; if a write fails,
the change applies in this window but is **not** reported as saved. Recent visible updates
uses only verified saved session timestamps in the visible snapshot, **not** the TUI's
complete last-active order: missing, unverified or truncated evidence cannot establish
recency, and undated projects follow name/ID order. Narrow-screen Show projects is a
temporary reveal independent of the desktop collapse preference. These controls do not
change the selected project/session, draft, requests, pane widths or timeline position.
The command approval policy is edited in **Settings → Configuration file**.

### About

**Settings → About** is an inline page with the product, version, build metadata (only for a recognized
version `+metadata` suffix) and the mode of the running app (desktop app, catalog only, browser demo or
unavailable host). The command palette opens the same facts as a window. The result of this
run's update check is shown under **Updates** (see "Updates" above).

### Application Logs

**Settings → Application Logs** is a live log view of this desktop process: it reads the captured
messages when the page opens and every two seconds after, one line per message with its local time,
level, logger and text, coloured by level (warnings in gold, errors in red). The view stays at its end
unless you scroll up. A filter box and **All / Info / Warnings / Errors** narrow the lines, **Wrap
lines** wraps long ones, and **Clear** (one confirmation) removes the captured messages. The capture
holds the newest 1,000 messages (1 MiB, 2,048 characters per message, an exception appended to its
message); one read returns at most the newest 400 (256 KiB). Logging keeps its existing file writer,
levels, rotation and lifetime, but this screen **never opens log files**: it cannot show earlier
processes. If another owner initialized logging first, in-memory capture is unavailable; the browser
demo also has no desktop capture. A clear is tied to this capture's identity and the boundary of the
snapshot it was asked from: messages appended after it survive. Pending or
uncertain clear requests retain their original identity and boundary across screen
switches; a failed/mismatched reply cannot be retried or unlocked by Refresh. The
application must be restarted to discard uncertain in-process evidence. Clear never
alters persisted log files, the rolling writer, configuration, or logging lifetime;
it cannot clear another process's capture or an unavailable/external logger. No
export, file deletion or logging-policy controls are provided.
Logs may contain sensitive content; this screen renders them as inert text locally,
without link activation or external requests.

### Models

The **Models** Settings section (or `Ctrl+G`, then `Ctrl+O`) opens a model catalog in owned
mode. It requests the actual host-reported models of every enabled provider and shows them in one grid
with resizable columns (provider, name, ID, token limits, capabilities, default effort), like the TUI's
Models window; a provider whose read fails is named above the grid. Search provider and model names,
IDs and descriptions and open a model for supported efforts, capabilities and token limits. Provider
reads are capped at 32 and model reads at 128 and 96 KiB serialized, with omitted results identified; availability and
missing metadata, including pricing not reported by this inventory, remain explicit. Loading a
provider can start its configured provider runtime/probe and may access its normal configured
storage or network. The catalog-only copy and browser demo cannot discover model inventory from
saved defaults and show an unavailable state instead. From a model's details, an owned session
can explicitly select that provider's available model and supported reasoning effort for its
**next Send**. This uses the existing per-session composer choices and Send validation, retains
the selected agent prompt, and cannot change an in-flight/uncertain exact Send; it does not change
the running turn or queued work. A different provider, missing choices or changed host/session
cannot apply the selection.

### Providers

The **Providers** Settings section (or `Ctrl+G`, then `Ctrl+R`)
lists at most 32 configured providers with adapter type, enabled/default settings, configured
default model and **cached** host initialization availability. Opening the section or selecting a row
does not probe. **Test selected provider** explicitly starts only that enabled provider through
the shared initialization service; it can use configured provider storage or network in an owned
launch. The result is a completed initialization/probe, not proof of authentication or a live
connection, and arbitrary provider error messages/URLs/categories are not shown. An abandoned
probe is still joined by the host before disposal; do not assume its outcome from a timed-out
browser request. Catalog-only mode lists saved descriptors read-only without runtime tests.
When the application starts without any enabled model provider, which is how a new profile starts,
Settings opens on **Providers**, as the TUI opens its Model Providers dialog. The
first time, a **setup guide** runs over the page in three stops, each lighting the control it is
about: the providers that sign in with a subscription you already have (Codex with ChatGPT, Copilot
with GitHub Copilot; the guide continues with Codex and selects it), its **Sign in with the
browser** button, and the other providers, which take an API key through an environment variable.
**Next** (Enter or →), **Back** (←) and **Skip** (Escape) move through it; the page stays usable
underneath, so the sign-in can be started from its stop. It starts by itself once per window
profile; the question-mark button beside **Reload** shows it again at any time.

In an owned launch the **Providers** section is an editor: the configured definitions (including
disabled ones) are listed on the left with their cached availability and the configured default, and
the selected one has a form for its key, adapter type, enabled state, display name, default model,
reasoning effort, API URL, API key environment variable and API key, plus **Use as the default
provider**. **Add provider** starts a new definition and the trash button removes one after
confirmation. **Save and apply** writes the global `config.toml` and re-registers the providers in
the running host; it is refused as a conflict when the file changed on disk since the page read it.
A stored API key is never sent to the page: leaving the field blank keeps it, and **Remove the
stored key** clears it. Settings the form does not show (timeouts, request overrides, compaction and
so on) are preserved, but this structured save rewrites the file without its comments and blank
lines, exactly like the TUI's provider dialog; use **Configuration file** to keep hand formatting.
A field left blank uses its default: the field shows that default as its placeholder with a
**Default** tag (the provider's built-in value, otherwise the one of its adapter type), and a field
with a value has a button that returns it to the default. A blank model reads **First model
listed** and a blank reasoning effort **High when supported**, which is what a session of the
provider then starts with: the model and the effort of the built-in template are not applied to a
provider that leaves them out. Codex, Copilot and xAI sign in with their
account from the form's **Account** block: it shows whether the stored sign-in is valid and for which
account, a button per sign-in method (**Sign in with the browser**, **Sign in with a device code**)
and **Sign out**. A sign-in opens the provider's page in the system browser and shows the address,
and the code to enter for a device flow, each with a copy button, until it completes or is canceled;
a provider that was disabled is enabled when its sign-in succeeds. Save a new or edited provider
before signing in. The default provider shown here is `[chat] default_provider`; the host's own inventory marks every registered provider as
default, so it is not used for that.

**Provider defaults.** `globalConfig.providers` also reports what a blank field falls back to. Each provider carries
`defaults` (`displayName`, `model`, `reasoningEffort`, `apiUrl`, `apiKeyEnv`; null when nothing is
known): the built-in template entry with the same key and adapter type, then what the adapter type
fills in (the Codex, Copilot and xAI display names, the Codex API URL), and finally the provider key
as the display name. `model` and `reasoningEffort` only hold what the adapter type fills in, which is
nothing today. `typeDefaults` lists the same record per offered adapter type, in the order of
`providerTypes`, for a provider that has no definition yet.

**The `providerLogin` RPC.** The owned host's `providerLogin` service signs the Codex, Copilot and xAI providers in to their
accounts with the flows and credential storage of the TUI. Requests name the host epoch and a
provider key.

- `status` reads the stored sign-in state without network access: `supported` (false for an adapter
  type without account sign-in), the `modes` (`browser` for Codex, `device` for Copilot, both for
  xAI), `signedIn`, an account label, a short detail and a token expiry when known. Status codes are
  `ok`, `unavailable`, `stale_epoch`, `invalid`, `unknown_provider`, `config_invalid` and
  `read_failed`. Copilot's state follows its short-lived cached token.
- `login` takes an optional mode (the provider's first mode when blank) and returns a channel of
  events. A `prompt` event carries the address to open, for a device flow the user code and its
  expiry, and `browserOpened`: the host hands `https` addresses to the system browser. Exactly one
  terminal event follows: `completed` with the signed-in state, or `failed` with a code
  (`unavailable`, `stale_epoch`, `invalid`, `unknown_provider`, `unsupported`, `busy`, `timeout`,
  `canceled`, `login_failed`) and at most an exception type name as detail. Only one sign-in runs at
  a time in the host; a second one fails with `busy`. Closing the channel cancels the sign-in, and
  closing the window cancels and joins it before the host is disposed. A completed sign-in enables a
  disabled provider and re-registers the providers like **Save and apply**, so the configuration
  revision changes; a ChatGPT sign-in that declined plan usage (detail `No plan usage permission`)
  does not.
- `logout` removes the stored credential and reports `removed` (false when the provider was not
  signed in). It is refused with `busy` while a sign-in runs; other codes are those of `status` plus
  `unsupported` and `logout_failed`.

### Configuration file

**Settings → Configuration file** edits the global `config.toml` (providers and their credentials,
the default provider, plugin and skill policy) in an owned launch. The text is validated shortly
after typing stops, with the first diagnostic marked on its line; Save is offered only for changed,
valid text of at most 256 Ki UTF-16 units. A save is refused as a conflict when the file on disk no
longer has the revision the editor read (for example after an edit in the TUI or a text editor):
reload, then reapply the edit. **Save** only writes the file. **Save and apply providers** also
re-registers the enabled provider definitions in the running host and unregisters providers that
are no longer configured or enabled, like the TUI's Save and Apply; replacing a provider discards
its cached runtime. The file is written as typed, without reformatting, and not atomically. The
editor shows the file's content, including any credentials stored in it. Catalog-only launches and
the browser demo report the editor as unavailable.

ChatGPT subscription providers use the same token-sharing implementation and global credential
store as the TUI. Sign in from **Settings → Providers** (or with **Continue with ChatGPT** in `altatui`),
then use that configured provider in the owned desktop. Existing legacy Codex credentials require a new sign-in; device
login and credential import are no longer supported. No desktop-only OAuth flow or storage is added.

### Agent prompts

The **Agent prompts** Settings section (or `Ctrl+G`, then `Ctrl+H`) lists agent prompts and system
prompts from the built-in, global and (when an unarchived project is selected) project scopes, and
edits them: display name, description, system prompt, whether the text is added to the system prompt
or replaces it, and the prompt text in a Markdown editor. **New prompt** creates an agent or system
prompt in the global or project scope; **Customize a copy** on a built-in prompt creates a global
prompt with the same name, which then overrides it; **Remove** deletes a global or project prompt
file. Built-in prompts are read-only. A save is refused, without overwriting, when the file changed
on disk since it was read. A session's prompt for the next Send is chosen from the prompt bar.

### MCP Servers

The **MCP Servers** Settings section lists the servers defined in the global and selected project
`mcp.json` and edits them: name, local command (command, arguments, working directory, environment
variables) or HTTP (URL, headers), where it is stored, and whether it is enabled. The switch in the
list enables or disables a server without opening it. Stored environment and header values are never
sent to the page; leaving a value blank keeps the stored one. Connection tests, sign-in and per-tool
switches are done in the TUI. Enabling or disabling rewrites `config.toml` without its comments.

A session uses MCP servers as in the terminal: the model activates one with `alta mcp activate <id>`,
and the server's tools (`mcp__<server>__<tool>`) are part of the session from the model's next step
of the same turn. A server stays active for that session while the app runs.

The end of the status line above every prompt shows the status items of plugins. The MCP plugin shows
`MCP {enabled}/{configured}`, `· {n} unavailable` in the warning colour when servers are disabled or
invalid, and the state of their tools for that session: `tools not loaded` until the session activates
a server, then `active tools {n}` in the success colour. It is absent when no MCP configuration exists
or the plugin is disabled. Clicking it opens **Settings → MCP Servers**. Session status items of source
plugins follow it. The items are read again every ten seconds and when the window gets the focus back.

### Skills and Plugins

The **Skills** Settings section lists discovered skills (project, user, plugin and built-in) with a
switch per skill, a filter, **Enable all** / **Disable all** for the shown skills, and **New skill**,
which creates `<name>/SKILL.md` under the global or project skills folder. Selecting a skill shows its
details beside the list: source and state, the path of its `SKILL.md`, the skill that overrides it,
license, compatibility and allowed tools when declared, related files, validation diagnostics, and the
instructions of the `SKILL.md` rendered as Markdown (the first 64 Ki characters of a file up to 256 KiB).
The **Models** section's table fills the page height. The **Plugins** Settings
section has a switch per plugin, including the built-in MCP, Git and Statistics plugins; a change
applies the next time CodeAlta starts, except for the Statistics rows described below, which follow
the switch from their next read on. With an unarchived project selected, both pages can store a
change globally or for that project. Every Settings section has its own icon in the sidebar.
The Plugins page also shows the state of each plugin in the running host: running, failed with the
first compiler error, not supported in this application, or stopped.

## Plugins

The normal launch (`alta`, `alta --dev`) starts the plugin runtime like the terminal UI, before the
window's page loads:

- The built-in plugins are the terminal's, under the same ids (`mcp`, `git`, `statistics`), so one
  `[plugins.<id>]` configuration applies to both. They run as backends: the MCP plugin gives sessions
  the `alta mcp` commands, its prompt guidance and the tools of activated servers; the Git plugin
  gives sessions of CodeAlta-managed providers the `gh`, `glab` and `az` tools (each when its CLI is
  installed), run in the session's project; the Statistics plugin gives `alta statistics`. The MCP
  and Git plugins contribute no command to the window, which has its own MCP page and issue picker.
- Source plugins under `~/.alta/plugins` and the launch project's `.alta/plugins` are built while the
  start-up screen is shown, which names the plugin being built. They are compiled once for both
  applications, so a plugin that references terminal controls loads here too; the window never
  shows a terminal visual. A plugin made for the terminal only (`PluginFrontends.Terminal`) is not
  started and is marked as not supported in **Settings > Plugins**.
- Plugin commands, key bindings, dialogs, prompt pickers, status items, content around the prompt
  and timeline cards are shown by the window, with HTML fragments from the plugin where it gives
  them. The host side, the `pluginUi` RPC and the limits are described in
  [Plugins](plugins.md#two-applications-desktop-and-terminal).
- A build or load failure raises one notice at start and shows the error under the plugin in
  **Settings > Plugins**.
- Every Send asks the plugins what they add to the run, exactly as `alta session send` does, so a
  session gets the same tools and instructions from the window and from another session. When that
  changes between two sends (a server was activated), the next Send replaces the session's provider
  attachment; the timeline shows **System prompt changed**.
- `CODEALTA_DISABLE_PLUGINS=1` starts without any plugin. The explicit-root launch never starts them.

Sessions of the desktop host have the same `alta` tool as in the terminal: notes, sessions and
sub-sessions, reminders, skills, projects, providers, models and prompts, and the commands of the
active plugins (`alta mcp`, `alta statistics`).

- `alta notes set` writes the session's notes; the **Notes** window at the top right of the session
  opens with them.
- `alta ask` puts its questions in the place of the prompt and, with a file to review, that file in the
  place of the timeline (see "Asks and plan review" below).
- `alta reminder create` creates reminders the **Reminders** view lists and delivers.
- `alta session create` creates a sub-session. It appears in the Explorer under its parent within ten
  seconds while a run is live, and at the latest when the run ends. A sub-session is an ordinary
  session: it opens in a tab and takes prompts from the window. Sub-sessions created before their
  journal and their provider record agreed on the creation time stay at the root of their project.

What a sub-session reports to its parent, and what one session writes to another, is recorded as a
prompt with a routing envelope. The timeline shows it as an **Agent message** card with the kind of
message and the start of the sending session's id, and only the text that was sent; **Details** names
the sending session.

## Files

### Changes

The counts beside the branch in the composer are a button: it opens the **Changes** tab of the project,
which is also what `alta diff show` does for an agent. There is one such tab per project. It is a tab
of the same strip as the sessions and the code editors (`view: "changes"` in `fileTabs`), so it closes,
reopens, cycles and is restored like an editor tab, and several can be open. It opens in a pane on the right of the
tab it was asked from, or in the pane that already holds a changes tab; from there it is dragged, split
and merged like any tab.

- The header names the project, the work tree and the branch, what is compared when it is not the
  uncommitted changes, the number of files and the lines added and removed. **Auto-refresh** reads the
  lists every five seconds while the tab is shown; the refresh button reads them now. A list that did
  not change is answered with `unchanged` and nothing is drawn again; a file is read again only when its
  `revision` changed, and the diff keeps its scroll position.
- The files are a tree (folders first, a chain of single folders on one row, each folder with its
  number of files) or a flat list, with the status letter and the `+`/`−` lines of each file. The filter
  keeps the paths that hold every word. Up/Down, PageUp/PageDown, Home and End move through the files,
  Enter goes to the diff. The width of the list and every choice of the tab are kept in
  `codealta.desktop.changes.v1`.
- **History**, under the files and behind a splitter, chooses what is compared: **Uncommitted changes**,
  everything since the base of the branch when it has commits of its own, or one of the recent commits
  (**Load more** adds twenty, up to 200).
- The diff is Monaco's diff editor, read-only, with the highlighting of the file's language and the
  changed words marked inside a changed line. It is side by side or inline (inline by itself when the
  pane is narrower than 780 pixels), with the unchanged regions folded behind expanders. The arrows and
  `Alt+Up` / `Alt+Down` go through the changes. **Open file** opens the file in the code editor of the
  project when it is inside the project folder. The `…` menu has **Hide unchanged lines**, **Ignore whitespace
  changes**, **Wrap lines** and **Copy path**.
- A file that is new or deleted is shown whole, tinted green or red, instead of beside an empty side.
  A binary file, a file over 1 MB and a file that cannot be read say so instead of a diff.
- In a pane narrower than 760 pixels the files and the history sit above the diff.

### Edits in the timeline

The tile of a tool call that edited files (`write_file`, `apply_patch`, `replace_in_file` and the like)
shows the lines it added and removed, and its details open with the diff: a row per line with the line
numbers of both sides, a header per file and a rule per hunk. The host takes both from the `diff` of the
call's record (`HistoryToolProjection`): the lines are counted over the whole diff, and up to 16 Ki
UTF-16 units of it are sent, cut at the end of a line. The files of a **Modified files** card open in
the same view.

### Code editor

A project has one **code editor**: a tab of the same strip as the sessions (`view: "editor"` in `fileTabs`),
labeled **Editor** and the name of the project. It holds the files of the project on a side and the open files
as tabs of its own, so that several files of one project share one tab. Its sources are in
`frontend/src/editor/`.

It is opened in three ways:

- `Ctrl+E` (`/edit`) opens **Open file** for a project: the `@` search limited to files, recently used first.
  Enter opens the file in the project's editor. An editor that was not open yet opens on that file alone,
  without its side.
- `Ctrl+E` again in that window (`Ctrl+E Ctrl+E`), `/editor`, or the `</>` icon of a project in the
  Explorer opens the editor with the files of the project and the keyboard in them. The icon shows while the
  pointer is over the project, stays visible and tinted while the project's editor is open, and has a dot while
  one of its files holds unsaved edits.
- `alta editor open` does the same for an agent (see `doc/live-tool.md`), and **Open file** in a Changes tab
  opens the file there.

The project is the one of the code editor or the Changes tab in front, otherwise the selected project. The
editor needs an owned host and a project that is not archived. Editor tabs close, reopen, cycle, drag and split
like session tabs; the open editors are restored with the window (`codealta.desktop.fileTabs.v1`), and a
stored tab of one file, from before the editor had tabs of its own, becomes the editor of its project with
that file. While an editor is in front, the commands that act on a session are unavailable.

**Side.** Two views, **Files** and **Search** (`Ctrl+Shift+E`, `Ctrl+Shift+F`); `Ctrl+B` or the button at
the left of the file tabs hides and shows the side, and its splitter sets its width. In a pane narrower than
560 pixels the side and the file take turns.

- **Files** is a tree read one folder at a time: `projectFiles.list` is asked for the project folder and for
  each folder as it is opened, never for what is below a closed folder. What git ignores is left out;
  **Show ignored files** (the `…` menu) lists it dimmed. The open folders are listed again every four
  seconds while the files are shown, when the window gets the focus and after each change made in the tree;
  a folder that did not change is answered with `unchanged` and redraws nothing. Only the rows in view are
  drawn.
- A file changed since the last commit has the color and the letter of its status (as in the Changes tab), and
  a folder that holds one has a dot; they come from `projectGit.changes`, read every five seconds.
- A click previews a file (its tab is in italics and is taken over by the next preview); a double click, Enter
  or an edit keeps it open. Arrows, Home, End, PageUp and PageDown move through the rows, Left and Right close
  and open a folder, and the letters of a name go to the next row that starts with them.
- **New file**, **New folder** (in the selected folder, or beside the selected file), `F2` to rename and
  `Delete` to delete, also in the menu of a row with **Copy path**, **Copy relative path**, **Reveal in File
  Explorer** (Finder, or the containing folder on Linux) and **Find in folder**. A name is typed in the tree
  itself and checked as it is typed; a new name may hold folders (`a/b/c.ts`). An entry dragged onto a folder
  moves there. The open files of a renamed or moved entry keep their tabs, their text and their unsaved edits.
- A deletion asks first. The entry goes to the Recycle Bin (the Trash on macOS and Linux) when the system has
  one the host can use; otherwise, or when the trash refuses, the question becomes the one of a deletion that
  cannot be undone. The question says how many open files with unsaved edits are closed with it.
- **Search** looks for a text through the files of the project, with **Match case**, **Match whole word**
  and **Use regular expression**, and globs for the files to include and to exclude. It runs a moment after
  the typing stops, or at once on Enter, and lists the matches by file as `projectFiles.search` finds them.
  A row shows the line of a match from a few words before it, so that the match itself shows in a narrow
  side. A match opens its file on its line with the match selected. **Find in folder** fills the files to include.

**Open files.** A tab per file with the icon of its type; a name that several tabs share is followed by its
folder. A tab shows a dot while its file has unsaved edits and is struck through when the file is gone from
the disk. Tabs are dragged to another place of the strip, closed with the middle button or `Ctrl+W`, and
`Ctrl+Tab` / `Ctrl+Shift+Tab` go to the next and the previous one. The menu of a tab has **Close**, **Close
others**, **Close saved**, **Close all**, **Keep open**, the two paths and **Show in the files**. `Ctrl+W`
closes the file shown; with no file left it closes the editor. The open files, the one shown, the side and the
open folders of each project are kept in `codealta.desktop.editor.v1`, with the width of the side and the
view options. A file is read the first time it is shown.

**Text.** The editor is Monaco, with line numbers, folding, matching brackets, several carets, the other
places of the word under the caret, and:

- `Ctrl+F` and `Ctrl+H` find and replace, with regular expressions, case and whole words; `F3` and
  `Shift+F3` go to the next and the previous match.
- `Ctrl+G` goes to a line (and a column, `120:8`), as in the terminal UI's editor: in the text of the code
  editor that key is not the first stroke of a `Ctrl+G` chord.
- `Alt+Z` and the `…` menu turn **Wrap lines** on and off (on at first); the menu also has **Minimap**,
  **Reload from disk**, **Copy path** and the three commands above.
- The status bar shows the state (**Saved**, **Modified**, **Saving…**, **Read-only**, **Changed on disk**,
  **Deleted on disk**), the path, the line and column (a click goes to a line), the indentation, the line
  endings, the encoding, the language, and **Save**.

The file name or extension chooses the highlighting; anything unknown is plain text. Every grammar Monaco
ships is available (about eighty), and the app adds its own small grammars for what Monaco lacks: JSON, TOML,
Makefile and diff/patch (`frontend/src/monaco/monacoGrammars.ts`). A language is one lazily loaded chunk,
fetched the first time a file needs it; `frontend/src/monaco/fileLanguage.ts` maps file names and extensions
to languages. To add a language, add its Monarch grammar to `monacoGrammars.ts` and its extensions to
`fileLanguage.ts`.

**Pictures and previews.** A PNG, JPEG, GIF, WebP, BMP, ICO or AVIF file (read through `projectFiles.image`)
is shown on a checkered background, fitted to the pane, with zoom buttons, `Ctrl` and the wheel, its size in
pixels and in bytes. An SVG file is text that opens as its drawing, and a Markdown file is text that can be
shown as a page: two buttons at the right of the tabs switch between **Preview** and **Text**, and a file
opens in the view last chosen for its kind. A preview is made from the text as the editor holds it, unsaved
edits included.

**Saving and changes from elsewhere.**

- `Ctrl+S` saves the file shown through `projectFiles.write` with the revision that was read, and
  `Ctrl+Shift+S` saves every file with unsaved edits. Text is written with the line endings the editor
  shows: a file with mixed line endings gets its dominant one once it is edited and saved.
- The open files are compared with the disk every two and a half seconds while the editor is shown
  (`projectFiles.stat`). A file that something else changed and that holds no edit is read again: the
  text is replaced where it differs, the caret and the scroll position stay, and one undo brings the
  previous text back. This is how an agent's edits appear in an open file.
- A file with unsaved edits that changed on the disk is not replaced: the editor offers **Reload** (replace
  the edits with the file on disk), **Overwrite** (write the edits anyway) or **Cancel** (keep editing; the
  next save asks again). The same question answers a save that finds another revision on the disk.
- Closing files with unsaved edits asks **Save**, **Discard** or **Cancel**, and so does closing the editor;
  a save that is refused keeps the file open.
- A file with the read-only attribute opens read-only. A binary file, a file over 1 MiB, a missing file, a
  path outside the project folder and an archived or unavailable project show the reason in place of the
  text, with **Reload** to read again.
- Unsaved edits live in the open editor only and are not stored. **Exit** (`Ctrl+Q`, `/exit`) asks **Save
  all**, **Exit without saving** or **Cancel** while files hold unsaved edits; the window's own close button
  and `Alt+F4` close without asking and drop them.

### Project files and git status

Two host RPC services give the page a project's files and repository state. Both take a project id,
never a folder, refuse another host epoch (`stale_epoch`) and answer `unavailable` in catalog-only
mode. Other refusals shared by both are `invalid` (no project id, or a malformed request),
`unknown_project`, `project_unavailable` (the folder is gone) and `read_failed`.

`projectFiles` gives the code editor the files of a project. An entry is addressed by a path relative to
the project folder (forward or back slashes, at most 1024 characters; responses use forward slashes). The
service refuses archived projects (`archived_project`), and never renames or deletes the project folder
itself.

- A path that is rooted, names a drive or a stream (`:`), has a `..` segment or crosses a link
  (a symbolic link or junction below the project folder) is refused as `outside_root`. Empty or `.`
  segments, control characters and names ending in a dot or a space are `invalid`.
- `list` returns the entries of up to 256 folders, each on its own and nothing below it (an empty path is
  the project folder): the name, whether it is a folder and whether git ignores it, folders first, then
  files, by name without regard to case and with numbers by value. It is `ProjectFileTree.ListFolder` of
  `CodeAlta.Catalog`: the rules of the nearest git work tree at or above the folder (`.gitignore` files
  down to the folder, `.git/info/exclude`, the global excludes file), or the `.gitignore` files between
  the project folder and the folder outside a work tree, evaluated with XenoAtom.Glob. The folders of
  version control systems and links are never listed. Each folder has a `revision`; a request that names
  it as `knownRevision` gets `unchanged`. A folder holds at most 5000 entries (`truncated`). The answer
  also says whether the host can move entries to the trash and show one in the file manager.
- `read` returns the text of a file with its newlines unchanged, its size in bytes, its encoding, whether
  it has the read-only attribute, a revision (SHA-256 of the file's bytes) and a `stamp` (size and last
  write time). A file larger than 1 MiB on disk is `too_large` and is not read. A file that is not UTF-8,
  or UTF-16/UTF-32 with a BOM, or that contains a NUL character, is `binary`. A missing file, and a
  folder, are `not_found`. A read records the file among the project's recent files, unless it is a
  `reload`.
- `write` keeps the file's encoding and BOM and writes the newlines it is given. It must name the
  revision that was read: when the file on disk has another one the answer is `conflict` with the
  current revision and nothing is written. With `overwrite` the revision is not compared. A
  read-only file is `read_only`; a failure while replacing the file is `write_failed`. The 1 MiB limit
  applies to the bytes a write would produce.
- `stat` returns the stamp of up to 128 files without reading them: another stamp than the one of the
  read means that the file changed.
- `create` makes an empty file or a folder, with the folders above it that do not exist yet; `rename`
  renames or moves a file or a folder (not into itself). Neither replaces anything: a name that is
  taken is `exists`, except a rename to the same name in another case.
- `delete` moves a file or a folder with all it holds to the trash of the system (`DesktopFileTrash`:
  the Recycle Bin through the shell on Windows, `/usr/bin/trash` on macOS 14 and later, `gio trash` on
  Linux), or with `permanent` removes it for good. Without a usable trash the first is
  `trash_unavailable`, and a trash that refuses is `trash_failed`: nothing is removed for good that was
  not asked to be.
- `image` returns a picture of at most 16 MiB as base64 with the media type found in its content
  (`unsupported_type` otherwise); `reveal` shows an entry, or the project folder, in the file manager.
- `search` is a stream: one `file` event for each file with matches, then one `done` event with the
  totals or with why the search did not run (`invalid_pattern`, `invalid_glob`, `timeout`…). The files
  are those the XenoAtom.Glob scanner walks (`ProjectFileTree.EnumerateFiles`: what git ignores is left
  out), narrowed by the include and exclude globs (`ProjectFilePathFilter`), where a name or a path
  matches anywhere in the project and a folder matches what it holds. Both types are in
  `CodeAlta.Catalog`: the desktop assembly does not reference XenoAtom.Glob. A page that stops reading
  stops the search. A text or a .NET regular expression is matched line by
  line, in text files of at most 1 MiB; a search reports at most 2000 matches in 500 files, 200 in one
  file, and gives up a line that takes more than a second.
- `watch` is the channel through which `alta editor open` reaches the page.

`composerStatus.read` returns the plugin status items of a composer for a project id (or none) and
a session id (or none): each has the plugin id, a name, a label, a text, a tone (`info`, `success`,
`warning`, `error`, `muted`) and the Settings page it opens, which is what a plugin's session status
contribution carries. The MCP item comes from the configuration and from what the running MCP plugin
knows of the session (the servers it activated and their tool counts); the read itself contacts no
server. Without a running MCP plugin (explicit roots, `CODEALTA_DISABLE_PLUGINS=1`) the item describes
the configuration alone and its tools read `tools not loaded`.

`projectGit.status` returns the branch of the repository containing the project folder and how much
its work tree differs from the last commit. Archived projects are answered too.

- The repository is the nearest `.git` at or above the project folder; a `.git` file (a linked
  worktree or a submodule) is followed to the directory it names. Without one the answer is
  `not_repository`.
- The branch is read from the `HEAD` file, without starting git. On a detached `HEAD` the branch is
  the first seven digits of the commit and `detached` is set.
- `insertions`, `deletions` and `changedFiles` are the totals of the list `projectGit.changes` returns
  for the comparison with `HEAD`: staged and unstaged changes of tracked files in the whole repository,
  and the files git neither tracks nor ignores.
- Git runs without a shell or prompts and is stopped after 10 seconds. When it is not installed, fails
  or is stopped, the answer is still `ok` with the branch and the three counts unset.
- The composer asks every two seconds while it is on screen. A list is reused for four times as long as
  git took to produce it, at least one second and at most thirty, so a slow repository is not read all
  the time; a failure is reused for five seconds. One list is read at a time.

`projectGit.changes` lists the changed files of that repository. `comparison` chooses what is compared:

- `head` (the default): the work tree against the last commit. It is one
  `git diff --raw --numstat -z -M HEAD` (against the empty tree in a repository without a commit) and one
  `git ls-files --others --exclude-standard -z`, run together. The lines of an untracked file are counted
  by reading it, once per content (length and last write time), for files up to 2 MB and up to 500 new
  files per list.
- `branch`: the work tree against the commit the branch left its base at (`git merge-base`), so the
  commits of the branch are included. The base is the upstream of the branch, or `origin/main`,
  `origin/master`, `main` or `master` when it has none; it is looked up at most every ten seconds. The
  answer always names it (`baseReference`, `baseAhead`), and a branch without a base or without a commit
  of its own is answered as `head`.
- `commit` with the full id of a commit in `commit`: that commit against its first parent (against the
  empty tree for a first commit). Its list is kept ten minutes.

Each file has its path relative to the work tree (`root`), the path it had before a rename, a status
(`modified`, `added`, `deleted`, `renamed`, `copied`, `conflicted`, `untracked`), its added and removed
lines (unset for a binary file) and a `revision` that changes when one of its two contents does. The
answer has a `revision` of its own: a page that sends it back as `knownRevision` gets `unchanged` and
nothing else. `prefix` is the project folder inside the work tree, which is how the page knows the
project-relative path of a file. A list holds at most 3000 files and 4 MB of git output; `truncated`
says that there were more.

`projectGit.file` returns both contents of one file of that list, as text: the one of the commit
(`git cat-file blob` of the object the list named) and the one of the work tree, or of the commit for
the comparison `commit`. A side is `text`, `absent`, `binary`, `too_large` (more than 1 MB) or
`unreadable`. Only a path of the current list is read (`not_changed` otherwise), a link is never
followed, and UTF-16 with a byte order mark is decoded.

`projectGit.commits` returns the newest commits of the current branch (id, short id, author, date,
subject), 20 by default and at most 200, with `more` when older ones exist; it has the same
`knownRevision` answer. `projectGit.watch` is the channel through which `alta diff show` reaches the page.

`promptImages.read` returns one image of a persisted user message as base64 with its media type. It
takes the host epoch, a session id, the journal offset of the message (the `offset` of its history
row) and the index of the image in the row's `images` list (`index`, `title`, `mediaType`); it takes
no path and no project id, so the sessions of archived projects are answered too.

- The host reads that journal record again and takes the path the record holds. The record must be a
  user message (`missing_record` otherwise, `missing_session` for an unknown session) and have an
  image at that index (`missing_image`).
- The file must be in the session's prompt-image folder (`<catalog>/sessions/yyyy/MM/dd/<session
  id>.attachments`, where sent images are saved). A path elsewhere, a relative path, and a path that
  crosses a symbolic link or junction at or below that folder are `outside_store`; a file that is gone
  is `missing_file`.
- The content decides the type: PNG, JPEG, GIF, WebP and BMP are served, anything else (SVG included)
  is `unsupported_type`. A file over 8 MiB is `too_large`.
- Other refusals are `stale_epoch`, `unavailable` (catalog-only mode), `invalid` (a malformed
  request), `capacity` (eight host reads are already running; the page asks again), `closed` and
  `read_failed`.

The page shows the image through a `data:` URL: the content security policy allows `data:` images and
no `blob:` URL, and is unchanged.

## Terminals

A project has **terminals**: shells that run in the application, each shown in a tab of the same strip as
the sessions (`view: "terminal"` in `fileTabs`). The terminal icon of a project row opens one in the folder
of the project, the terminal button of the composer opens one in the folder the session works in, and
**New Terminal** (``Ctrl+` ``, `Ctrl+G` `Ctrl+J`, `/terminal`) opens one from the tab in front. The first
terminal opens in a pane under the tab it was asked from, a third of the height; the next ones are tabs
of that pane. From there a terminal tab is dragged, split and merged like any tab. The pane of the
terminals stays theirs: a session or a code editor opened while the keyboard is in a terminal opens in a
pane of the sessions. The sources are in `Desktop/Terminals/` (host) and `frontend/src/terminal/` (page).

- **A terminal belongs to the application, not to its tab.** Closing the tab leaves the shell running.
  The terminals of a project are listed under its sessions in the Explorer (**Terminals**): a row opens
  the tab again with everything the terminal wrote, renames it (the pencil, `F2`, a double-click; an
  empty title gives back the folder) and ends it. A row shows the title or the folder the shell is in,
  the shell, a spinner while a command runs, the exit code once the program has ended, a dot when the
  program rang the bell and a mark when a session created the terminal. A terminal goes away when its
  shell exits; one a session created stays until it is closed, so that what it wrote can still be read,
  and so does a shell that fails within three seconds of starting. Terminals keep running while the
  application stays in the notification area, and exiting ends them, after a question when a shell says
  it runs a command. Terminal tabs are not restored at the next start. At most 64 terminals run at once.
- **The host runs the shell and keeps its text.** The shell runs behind a pseudo-terminal of the system:
  ConPTY on Windows (Windows 10 1809 and later), a pty on macOS and Linux. The host reads what it writes
  into a text model of the screen (`TerminalScreen`: the screen, 10,000 lines that scrolled off it, the
  screen of full-screen programs, long lines and their wrapping when the width changes) and keeps the
  last two million characters as they were written. `alta terminal` reads the model, and a tab that
  opens later is replayed the characters: neither needs the page, which the system may stop while the
  window is hidden. The page's terminal is xterm.js and is a view only. One `terminals.watch` channel
  carries the list of terminals and what the shown ones write; the page acknowledges what it took in, a
  terminal is sent at most 512 Ki characters ahead of that, and a tab that falls further behind than the
  host keeps is started again from what is left.
- **Shells.** The default shell on Windows is PowerShell 7 when it is installed, then Windows PowerShell,
  then the Command Prompt; Git Bash and the WSL distributions are found too. On macOS and Linux it is
  `$SHELL`, then the shells of `/etc/shells`, started as a login shell on macOS. A shell starts with the
  environment of the application, read again from the registry on Windows so that a new terminal sees
  what was installed since, plus `TERM_PROGRAM=CodeAlta`, `TERM_PROGRAM_VERSION`, `COLORTERM=truecolor`,
  `CODEALTA_TERMINAL` (the id of the terminal) and, outside Windows, `TERM=xterm-256color` and a UTF-8
  `LANG` when none is set.
- **Shell integration.** PowerShell, bash (Git Bash included), zsh and fish are started with a script
  that makes them say where their prompt is, what command runs, how it ended and which folder they are
  in (the `OSC 633` sequences of Visual Studio Code, and `OSC 133`); the Command Prompt gets a `PROMPT`
  that says where its prompt is and its folder. This is what the spinner, the folder of the title and
  the exit codes of `alta terminal` come from. The scripts are written to `terminal/` under the data
  root (`TerminalIntegration`): PowerShell dot-sources its script, so an execution policy that forbids
  scripts leaves the shell without it, and a prompt that a theme installs later is wrapped in its turn;
  bash reads it with `--init-file` and the script reads the files
  of the user; zsh reads four small files through `ZDOTDIR`, each of which reads the file of the user it
  stands for; fish takes it with `--init-command`. A program started through `wsl.exe` is started as it
  is. **Shell integration** in the options of a terminal turns it off for new terminals.
- **Display.** Text is drawn with WebGL, in 24-bit color, with the Unicode 11 widths. The font is
  CaskaydiaCove Nerd Font (Cascadia Code with the Nerd Fonts icons, SIL Open Font License 1.1), shipped
  with the application with its license, so that prompts with icons show as they do in a terminal set
  up for them. The sixteen colors a program names come from the theme and the color scheme, and a color
  too close to the background is nudged until it is read. Links (`http`, `https`, and those a program
  marks with `OSC 8`) open in the browser of the system; a program can write to the clipboard
  (`OSC 52`) but not read it, and can show a progress bar in the header (`OSC 9;4`). Pictures (sixel,
  iTerm, kitty) need WebAssembly, which the content security policy of the page does not allow yet.
- **The header** of a terminal names the shell and the folder, shows the command that runs, and has
  buttons for a new terminal in the same folder, **Find** (matches are counted and marked; case, whole
  word, regular expression), the options and a menu (**Rename…**, **Clear**, **End terminal**). The
  options are a popover: the cursor (bar, block, underline) and whether it blinks, the text size (8 to
  24), **Copy on select**, the scrollback (1,000, 10,000 or 100,000 lines), the **Shell** new terminals
  start when the system has several, and **Shell integration**. They apply to every terminal of the
  window and are kept in `codealta.desktop.terminal.v1`.
- **Keys.** A terminal takes the keyboard: `Ctrl` with a letter, the function keys and `Alt` with an
  arrow go to the shell. The application keeps `Ctrl+P`, the `Ctrl+G` chords, `Ctrl+,`, ``Ctrl+` ``,
  `Ctrl+PageUp` / `Ctrl+PageDown`, `Ctrl+Alt+Left` / `Ctrl+Alt+Right`, `Ctrl+Alt+B`, `Ctrl+Shift+T`,
  `Ctrl+Shift+W` and `Ctrl+Shift+N`. `Ctrl+C` copies when text is selected and interrupts the program
  when none is; `Ctrl+V` and `Shift+Insert` paste; `Ctrl+Shift+C` / `Ctrl+Shift+V` copy and paste too;
  `Ctrl+F` finds; `Ctrl+Home` / `Ctrl+End` go to the top and the bottom. On macOS the Command key does
  these and `Ctrl` is all for the program. A right click opens **Copy**, **Paste**, **Select all**,
  **Find…** and **Clear**.

Agents use the same terminals with `alta terminal` (see `doc/live-tool.md`): they list them, create one,
read its screen or its last lines, type in it and wait for the command to end, rename it, show its tab
and close it. A host started with `--review-owned-command-permissions` lets no session type in a
terminal, since what is typed in a shell is a command nobody reviewed.

## Automations

An **automation** is a prompt that starts a session by itself: on a schedule, when something happens in the
repository of its project, or when it is asked to. Each run creates a new session, named after the
automation (and after the issue or the pull request that started it), in its project or as a chat, and
sends it the prompt. A reminder is for the session that sets
it and is lost when the application stops; an automation is written in a configuration file and starts
sessions of its own. Automations exist in CodeAlta Desktop only. The sources are in `Desktop/Automations/`
(host), `Desktop/Rpc/AutomationsRpc.cs` and `frontend/src/automations/` (page).

- **Where they are kept.** An automation is one table of the configuration of the user
  (`~/.alta/config.toml`) or of a project (`<project>/.alta/config.toml`, which a repository shares with
  everyone who opens it). The key of the table is the identifier of the automation, a GUID.

  ```toml
  [automations.0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77]
  name = "Issue triage"
  enabled = true
  project = "C:/code/app"            # configuration of the user only: the folder of the project it runs in
  model = "codex:gpt-6.1-sol@high"   # provider[:model][@effort]
  agent = "default"
  catch_up = false
  triggers = [
    { type = "daily", at = ["09:00", "17:30"] },
    { type = "weekly", days = ["mon", "thu"], at = ["08:30"] },
    { type = "hourly", minute = 15, every = 2 },
    { type = "cron", expression = "0 9 * * 1-5" },
    { type = "issue", event = "opened" },
    { type = "pull_request", event = "updated", authors = "anyone" },
  ]
  prompt = '''
  Triage the issues opened since yesterday.
  '''
  ```

  Only `name` and `prompt` are required. In the configuration of the user, an automation without `project`
  runs as a chat; in the file of a project it runs in that project. Without `model` it takes the default
  provider and the first model it lists; an effort it does not name is the one a new session gets. A name
  has at most 120 characters, a prompt 32,768, and an automation 8 triggers. The files are read apart from
  the rest of the configuration (`AutomationConfig.Read`): a table that is not an automation is listed in
  the tab with the reason and breaks nothing else. Saving writes that one table and leaves every other
  byte of the file as it was (`AutomationConfig.Write` finds the table with the Tomlyn syntax tree). The
  files of all the projects are read away from the window, eight at a time, again every minute and when
  the tab asks: an automation written by hand, or pulled with the repository, shows up by itself.
- **What comes with a project is allowed first.** The configuration of a project comes with its
  repository: other people write it, and a pull can change it. The triggers of an automation kept there
  start it only once the user allowed it in this instance, as it is: the card says **Waits for you to
  allow it** and has an **Allow** button. What is allowed is the definition (name, prompt, model, agent
  prompt, triggers): a change of any of them asks again, and its switch does not answer. An automation
  saved from the editor or by `alta automation create` is allowed by that, and one kept with the user
  needs no allowance. Running an automation by hand never needs one. The allowance is for the file the
  automation is in: the same table in another folder asks again, and when two files hold the same
  identifier, the one that was allowed is the automation. A configuration file of a project that is a
  link, or whose `.alta` folder is one, is neither read for automations nor written: the repository
  gives that link, and it may name any file. The allowances are in `automations.json`.
- **Schedules** are read on the clock of the machine. `hourly` is a minute of the hour, every 1 to 12
  hours counted from midnight; `daily` one or more times of day; `weekly` days and times; `cron` five
  fields (minute, hour, day of the month, month, day of the week) with `*`, lists, ranges, steps and the
  names of months and days. When both the day of the month and the day of the week are given, either one
  is enough, as in cron. A time the clock skips when it moves forward is due when it would have been
  reached, and a time it shows twice is due once. An automation without trigger is run by hand.
- **Events of the repository.** `issue` (opened) and `pull_request` (opened, or `updated` when it gets
  commits) watch the repository of the project on GitHub, GitLab or Azure DevOps (work items and pull
  requests there). A desktop application cannot be called by a provider, so it asks: every five minutes,
  only for the repositories an enabled trigger watches, with the credentials of the issue picker, and
  with the entity tag of its last reading on GitHub, where an unchanged list then costs nothing
  (`GitRepositoryFeed` in `CodeAlta.Plugin.Git`). The first look of a trigger starts nothing: what is
  there already is not an event. An event starts the automation only when its author is one of the
  people of the repository (on GitHub its owner, a member of its organization or a collaborator, as
  GitHub reports the association; on GitLab a developer or more; on Azure DevOps anyone of the
  organization), since what a stranger writes would otherwise reach a session; `authors = "anyone"` asks
  for every author. The prompt is followed by what happened: the repository, the number of the issue or
  pull request, then its title, its author and its link, each on a line of its own and cleaned of what
  is not seen (control characters, marks that turn the text around), and a line that says they are
  someone else's words. The events of one automation run one at a time, in their order: an event that
  comes during a run waits for it to end. The card of the automation names the repository it watches,
  or says why it sees nothing (no repository, no sign-in, no access). A reading lists the 30 newest
  issues, or the 100 pull requests that changed last: the commits of a pull request are noticed from
  the second time it is listed.
- **What is missed.** An automation runs while CodeAlta is open. A time that passed more than two
  minutes ago (the application was closed, the computer slept) is not run later, and what happened in a
  repository meanwhile starts nothing. With `catch_up = true` a missed schedule runs once at the next
  start, and each issue or pull request that came meanwhile starts its run. A schedule that is due while
  the previous run of the same automation is still in progress is recorded as skipped.
- **Runs.** A run checks what the automation names (project, provider, model, effort, agent prompt),
  and that the host still accepts a command, before it creates anything, so that neither a wrong name
  nor a full host leaves an empty session behind; it then creates the session and sends the prompt once. The session records the automation that created it in its journal
  (`created_by`, kind `automation`), which is how the Explorer and the session itself know. A run is
  `running`, `completed` (the session answered), `failed` (with the reason), `cancelled`, `interrupted`
  (the application stopped meanwhile) or `skipped`.
- **State.** What an instance remembers is in `automations.json` under its state root, apart from the
  definitions: whether the automations are paused, when the schedules were last looked at (written once
  a minute and when the application closes), the last 100 runs of each automation (2,000 in all), what
  the event triggers have seen and which automations of a project were allowed. A state file that is
  there and cannot be read starts an empty state with the automations paused. **Running / Paused** in the tab stops every trigger at once;
  running an automation by hand still works. The developer instance (`--dev`) reads the same
  definitions as the installed application and starts paused, so that it does not run them a second
  time.
- **The tab.** The bolt of the activity bar, **Automations** in the command palette and `Ctrl+G`
  `Ctrl+M` open one tab (`view: "automations"` in `fileTabs`), kept for the next start. It shows the
  next 24 hours on a line (a mark for each time a schedule is due, a band for an automation that runs
  all along), a card for each automation (what starts it, where it runs, when it is next due, how its
  last run ended, a switch, **Run now**, and **Edit…**, **Duplicate** and **Delete…** in its menu), the
  runs of the selected automation or the recent runs of all, and templates a new one starts from. A run
  opens its session. The editor is a window: name, where it runs, where it is stored (**My
  configuration** or **The project**), the triggers with the next times of each schedule, the prompt,
  the provider, model, effort and agent prompt, and **Save and run**.
- **Sessions.** A session started by an automation has a bolt for icon in the Explorer and, above its
  timeline, a line that names the automation and what triggered it, with a link to the automation in
  the tab. Deleting an automation leaves its runs and their sessions.
- **Agents** use the same automations with `alta automation` (see `doc/live-tool.md`): they list them,
  create one, run one, read its runs, and a session started by an automation finds it with
  `alta automation current`. A session that an automation started reads the automations and changes
  none: it cannot run, create, enable, disable or delete one, so that nothing it was told by an issue
  can keep itself going.
- **Writing a file.** A change reads the file, replaces the text of one table and writes the file back
  under a name of its own beside it, then moves it over the file; changes are made one at a time. The
  file keeps its line endings and its UTF-8 mark. Nothing is written unless the new text reads back with
  the automation in it: a file that holds `automations = { … }` on one line, or already holds the 64
  automations that are read from one file, is left as it is, with the reason. The switch of an
  automation changes the file as it is on disk, not as it was last read.

Each run takes one of the commands the host accepts in one run of the application, with the sends of the
page and the reminders: the desktop keeps 4,096 of them.

## Commands, help and keyboard shortcuts

The desktop app uses the TUI's key map. `Ctrl+P` (or `/` in an empty prompt, or the search icon on
the activity rail) opens the **command palette**: every command with its slash name, description and
shortcut, grouped by category and searchable by any of them; Enter runs the selected command and
commands that cannot run right now are dimmed. The palette is a window like Settings: drag its title
bar to move it and its edges to resize it. `F1` (or `?` in an empty prompt) opens **Commands and
shortcuts**, a filterable window listing the same commands by category.

| Keys | Command |
|---|---|
| `Ctrl+P`, `F1` | Command palette, help |
| `Ctrl+Q` | Exit (`/exit`); works from any window |
| `Ctrl+O` | Open project |
| `Ctrl+E`, `Ctrl+E` `Ctrl+E` | Open a project file in the code editor (`/edit`), open the code editor with the files of the project (`/editor`) |
| In a code editor: `Ctrl+S`, `Ctrl+Shift+S`, `Ctrl+W`, `Ctrl+Tab` | Save the file, save every file, close the file shown, next file |
| In a code editor: `Ctrl+B`, `Ctrl+Shift+E`, `Ctrl+Shift+F` | Show or hide the side, go to the files, search in files |
| In the text of a code editor: `Ctrl+G`, `Ctrl+F`, `Ctrl+H`, `F3`, `Alt+Z` | Go to line, find, replace, next match, wrap lines |
| In the files of a code editor: `F2`, `Delete`, `Enter`, `Space` | Rename, delete, open, preview |
| `Alt+Up`, `Alt+Down` in a Changes tab | Go to the previous or next change of the shown file |
| ``Ctrl+` ``, `Ctrl+G` then `Ctrl+J` | New terminal (`/terminal`) in the folder of the session or of the project |
| In a terminal: `Ctrl+C`, `Ctrl+V`, `Ctrl+F`, `Ctrl+Home` / `Ctrl+End` | Copy the selection (or interrupt the program), paste, find, top / bottom |
| `Ctrl+G` then `Ctrl+M` | Automations (`/automations`) |
| `Ctrl+Alt+Left` / `Ctrl+Alt+Right` (also `Ctrl+PageUp` / `Ctrl+PageDown`) | Previous / next tab |
| `Ctrl+W` (also `Ctrl+Shift+W`, which a terminal leaves to the application), `Ctrl+Shift+T` | Close tab, reopen the last closed tab |
| `Enter`, `Ctrl+Enter`, `Shift+Enter` | Send (queued while a turn runs), steer the running turn, new line |
| `Alt+Up` / `Alt+Down` in the prompt | Previous / next prompt sent from this window |
| `F6`, `Ctrl+T` | Full prompt editor, next agent prompt |
| `F8`, `F10`, `Ctrl+F11` | Abort the running turn, clear the queue, compact |
| `F3` / `F4`, `Ctrl+F3` / `Ctrl+F4` | Previous / next message, first / latest message |
| `Ctrl+Alt+B`, `Ctrl+F` | Browse sessions, filter the project's sessions |
| `Alt+Up` / `Alt+Down`, `Alt+Left` / `Alt+Right` outside text | Previous / next session, previous / next project |
| `Ctrl+Shift+N` | Show or hide the session notes |
| `Ctrl+G` then `Ctrl+P` / `Ctrl+S` / `Ctrl+G` | Go to prompt, go to sidebar, toggle the navigator |
| `Ctrl+G` then `Ctrl+T` / `Ctrl+U` / `Ctrl+D` | Session info, context usage, reminders |
| `Ctrl+G` then `Ctrl+W` / `Ctrl+R` / `Ctrl+O` / `Ctrl+H` | Settings, providers, models, agent prompts |
| `Ctrl+G` then `Ctrl+K` / `Ctrl+N` / `Ctrl+Y` / `Ctrl+L` / `Ctrl+A` | Skills, plugins, MCP servers, logs, about |

Switching to a session tab, by clicking it or with the tab keys, and creating, closing or reopening one
puts the keyboard focus in that session's prompt, so typing can start at once. A code editor takes it
in its text, or in its files when it shows no text. The window's own icon (Alt+Tab, task switcher) is `alta.ico`, shipped next to the executable.

The second stroke of a `Ctrl+G` chord works with or without `Ctrl` held. Shortcuts work while the
prompt editor has focus; the ones marked "outside text" stay ordinary caret keys in text fields. An
open window keeps the keyboard, except that the Settings window follows the commands that move to
another Settings page.

## Send diagnostics

For a Send lockup, turn on the Send diagnostics from DevTools
(`localStorage.setItem("codealta.debug.send", "1")`, then reload; they are off otherwise) and preserve
the developer-console entries prefixed `[CodeAlta Send]` and `[CodeAlta RPC]`. They report composer guards, dispatch, safe framework error codes, elapsed time
and UUID request keys, never prompt text or provider credentials. Match a dispatch key with
`Send reached backend (<key>)` in application logs. With NeoAstra 0.3.2, `duplicate_request`
means an active or retained completed request identity was reused;
`too_many_requests` means admission/rate/channel pressure; `connection_closed` means transport
loss. An uncancelled eight-second `operation_canceled` wait is consistent with the client timeout.
Completed request identities use bounded fingerprint history rather than accumulating ID strings
for the document lifetime. `request_id_capacity_exhausted` separately identifies capacity pinned by
active work/subscriptions; it is not a duplicate. No automatic
reload, capability reset or mutation retry is performed. Inspect receipts before deliberate retry.

## Isolated-root launches

### Browse a task-owned catalog copy

To display persisted projects and sessions, supply an existing **trusted, task-owned catalog
copy** separately from the new browser data directory:

```text
alta --data-root <new-absolute-browser-directory> --catalog-root <existing-absolute-catalog-copy> --allow-catalog-cache
```

Both catalog options are required together. The roots must not overlap, and neither may be
under a `.alta` path component. Path spelling does not prove ownership or protect against
symlinks/reparse points: do not point this mode at a production profile or an untrusted tree.
Preparing the copy is an explicit operator action; the application does not copy a profile.
Prepare it without copying `cache/cache.sqlite3` or its sidecars: existing cache rows can
contain absolute paths into the original tree. Normal shared cache creation then indexes
the copy. The desktop does not silently repair copied cache paths or switch discovery
strategies after an error; history refuses resolved paths outside the copy's sessions root.
Existing cache lookup can still probe whether a stored path exists before that refusal.

**This is not read-only filesystem access.** The opt-in permits the shared durable catalog
to create, rebuild and update `cache/cache.sqlite3` and SQLite sidecars in the copy. It does
not enable config migration, provider/plugin startup, agent execution or journal editing.
Cache/read failures are surfaced; the desktop does not substitute a header scan.

The screen shows a bounded persisted snapshot, project/session selection and metadata, not
live run status. Wire responses are capped at 200 projects and 500 sessions with a truncation
notice; these limits do not bound the underlying catalog load or implement paging/history.

### Explicit owned text submission

The default launch borrows command and cached-read services from one shared host. The following
form provides the same ownership route with isolated explicit roots, for tests and development:

```text
alta --data-root <new-absolute-browser-directory> --catalog-root <existing-absolute-trusted-copy> --allow-catalog-cache --allow-owned-host --project-root <existing-absolute-project-directory> --discovery-home <existing-absolute-discovery-directory> --instruction-root <existing-absolute-project-ancestor> --builtin-skill-root <existing-absolute-directory>
```

All flags are required together. The added roots must exist, be absolute and stay outside
`.alta`; browser data must not overlap project/discovery/builtin roots. The instruction root
is an inclusive ancestor of the project. No profile or HOME inference is performed. These
lexical checks and the copy's `alta.lock` are not a reparse sandbox or race-free ownership proof.

**This consent is broader than browsing:** lock/project/journal/cache/provider-state writes,
configuration/instruction/skill reads and configured-provider registration are permitted.
Registration can read declared credential environment variables and shipped defaults; later
submissions can authenticate and use provider storage/network. In this explicit-root launch plugins and automatic provider probes remain off; explicit Models reads and Providers tests may probe.
Use only trusted task-owned roots, never a production profile or an untrusted copied cache.

Select an existing session to send text (32,768 UTF-16 units maximum). Tool permissions are automatically
approved by default, matching TUI AutoApprove. Commands and file writes run with the host's privileges;
project/discovery roots are not a sandbox. Explicit command-review mode below disables this default.
User input is cancelled unless separately opted in below. Sessions get the `alta` tool; in the explicit-root launch no plugin adds tools.
Receipts are observed automatically; **Abort original Send operation** targets one pending send,
not a later run. Submitted means dispatch completed, not that the conversation/run completed.
Send and Abort retain up to 256 local intents combined, including their original live waiters,
across selection changes and remounts. Uncertainty keeps the exact epoch/key/session/text or
original Abort target for deliberate retry, never automatic resend. Receipt refresh cannot clear
an intent while its original waiter is live; Abort-only recovery does not erase unrelated composer
text. Late valid epoch mismatch disables mutations even after the old selection is cancelled.
Reload permits manual receipt browsing, not reconstruction of lost text/keys. Abort control
settlement is not rollback, decision retraction or run termination. The host's separate shared
receipt capacity is 4,096 for one run of the application, paged 64 at a time.

The composer visibly emphasizes **Cancel observed run** when an eligible point-in-time target
is available, keeping **Send** as a separate secondary button. This is not a live-running indicator.
A retained original Send keeps **Retry exact request** primary; retained run cancellation instead
reads **Retry exact cancellation**, preserving its original target and manual recovery. Neither
presentation changes nor cancellation clear the draft or initiate runtime reads. The two controls
keep separate identities and handlers: editor Enter still sends, Ctrl+Enter steers, and in the expanded
editor Enter inserts a new line while Escape or Ctrl+Enter closes without sending. Cancellation
signalling does not establish run completion.
On narrow screens the labelled controls wrap rather than clip; exact-target evidence and the
separate **Abort original Send operation** remain available.

Add **`--review-owned-command-permissions`** to the complete owned-mode command above to opt
into manual review of supported plain command requests. The selected-session review shows the
complete command, working directory and optional reason, with **Allow once / Deny / Cancel**.
After a manual refresh, deliberately choose **Review command permission** to open the native
HTML dialog for that exact observed entry. Opening or dismissing it performs no read or decision;
Close/Escape dismisses presentation, unlike the explicit **Cancel** permission decision.
Pending and uncertain original outcomes remain retained when the dialog closes.
Refresh pending commands manually; this is not a notification stream. Unsupported permission
payloads remain denied, this review flag alone leaves user input cancelled, and there is no Allow for Session option.
Approval can execute a command with the host's privileges: discovery roots are not a sandbox.
Changing selection does not cancel a pending permission or its original decision-response wait.
Use **Observe retained decision** to check that response locally, labelled with its original session
and complete handle; it does not contact the host or resend a decision. A pending observation does
not unlock another decision. Explicitly observe a terminal response, then refresh for a fresh review
before deciding again; merely displaying the result does not acknowledge it. Acceptance is not proof
of execution, and rejection does not identify an earlier decision. Genuine uncertainty or epoch
invalidation disables review across selections until renderer reload. Reload loses the local record;
then manually refresh still-pending requests under the existing opt-in. An empty list cannot recover
a lost decision, and host restart restores no old permission authority. No decision is replayed.
Aborting the owning submission or closing the application invalidates still-pending requests,
but cannot revoke a decision already accepted by the backend.

Add **`--enable-owned-user-input`** to the complete owned-mode command for **Nonsecret provider input**.
It is off by default, requires owned mode, and is independent of command review and restricted asks.
Command permissions follow the host's auto-approval or explicit review policy. **Never enter passwords, tokens or other secrets**:
literal answers may persist in provider tool results and history. Unsupported/secret/oversized forms are
cancelled as a whole; this provides no file review or credential-entry workflow.

Use **Refresh input** to list up to four pending forms, then deliberately choose **Review provider input**
for one exact observed form. The native HTML dialog shows complete literal questions/options and handle
provenance. Select an offered option or explicitly enter freeform text for every prompt, then
**Submit literal answers**, or **Cancel this attempt only**. Close/Escape dismisses presentation only,
without reading, answering or cancelling. Local edits survive ordinary close/reopen only for the same
validated form and lifetime; refresh, another form, scope changes and stale modal controls discard them.
They are not persisted or retained across sessions.
No answer is silently filled in. Answers are limited to 2,048 UTF-16 units each and 8,192 in aggregate.
An accepted response is an owner decision, not proof of provider continuation or persistence success.

Selection changes and panel remounts retain the original action and prevent competing actions. Use
**Observe original locally (no RPC)**, then **Acknowledge observed terminal original**; a fresh explicit
list is required before acting again. Genuine uncertainty cannot be acknowledged away or replayed.
Renderer reload can re-list still-pending host attempts but loses local action records; an absent attempt
does not reveal a lost outcome. Closing the application or cancelling the original operation/run
invalidates pending attempts; host restart restores no old input authority.

## Session commands and recovery

With a provider supporting run-bound review, cancellation of the actual owning run also invalidates
its pending requests, even when individual requests omit a run ID. A request naming another run is
denied. Previously accepted decisions remain accepted; trusted TUI approval policy is unchanged.

The compact prompt toolbar offers **Cancel observed run** (square icon with an accessible label)
only for an eligible point-in-time runtime observation or a retained exact cancellation intent.
Send and exact Send recovery remain separate. This action is distinct from **Abort original Send
operation**, which controls its receipt, not the running provider. The observed target names the
exact epoch, session, runtime, attachment and run; it is not inferred from host availability or
display state. Unsupported, stale, retiring, transitioning and draining targets fail closed
without fallback. Refresh submissions
for the result: **Cancellation signalled; run completion is not confirmed.** Failure can occur
after signalling; neither failure nor success promises rollback of accepted decisions or effects.
An uncertain request retains its original target/key across selection changes and remounts.
Reconcile manually or deliberately **Retry exact cancellation request** from the toolbar after the
previous wait settles; its exact target and key remain visible next to the composer and in advanced
diagnostics. New observations never retarget it, and replay never repeats cancellation. Closing the
panel cancels only its wait; host shutdown retains and joins original work. No automatic retry,
refresh or polling is added.

The selected session also has a **live status/text window**, separate from submission receipts.
Unmatched retained text and reported tools appear after the persisted journal rows in the same
timeline, with a compact status/coverage indicator instead of an empty live panel. A run-scoped
completion with an unambiguous matching provider replaces its live copy after manual history
refresh; uncertain identities stay visible rather than being guessed away. The live projection
has no timestamps or shared text/tool ordering, so its tail is **not** an exact chronological
interleaving, a history watermark or automatic journal refresh. The window shows retained
lifecycle, queue count, configuration labels
and up to eight text items, plus two most recently updated **reported plain ToolCall activities**.
Tool names may be shortened; arguments/results are not shown. Reported phases can regress, and
missing/evicted activity is unknown—not idle or complete. Started may precede permission resolution
and proves neither approval nor process launch; other phases are not run-completion or exactly-once
effect acknowledgments. This is not a complete transcript, tool-results/usage view or interaction UI.
No retained state means not yet observed or evicted—not idle or completed. Text and labels may
be shortened, and replacement/eviction indicators make omissions explicit. **Reconnect live
display** explicitly starts a new observation; it never resends a prompt. A stale host epoch
explicitly requires reloading the UI, not reconnecting with the old identity. Selection changes,
renderer detach and cancellation close only the observation, not a run or the host. Owned mode registers this channel, including the normal no-argument startup; the explicit catalog-only route does not.
Same-host reload obtains a new baseline of retained partial values only, not omitted history,
tool results, permission decisions or effects. Host restart restores no prior authority.

Live text **Copy Markdown** captures the full currently retained text at the explicit click,
including text marked as truncated or missing an earlier prefix; copying does not recover those
omissions. **Copied** / **Copy failed** feedback belongs only to the latest copy request for the
current row identity, text and completion/omission flags. Streaming changes and row removal clear
feedback timers; late clipboard results or queued old resets cannot overwrite newer feedback.
Failures are announced without exposing clipboard exception details. This fences presentation,
not the clipboard write itself: an already dispatched write cannot be undone by these guards.

Rapid selection/reconnect requests retain only the latest desired observation. Reopening waits for
the previous iterator's successful cleanup. If cleanup fails, **Reconnect live display** is blocked
and the failed owner is retained; renderer reload does not prove that old backend work terminated.
Valid late host/runtime identity changes disable shared mutation controls even after selection changes.

Live file-change/diff notifications invalidate the host's shared file-search cache even without a
terminal reader or when its event stream drops a notification. History replay and Display reconnect
do not repeat that cache effect. This best-effort dirty mark does not scan files or confirm a write.
Live plugin-effect observation belongs to the shared runtime rather than a terminal event
reader.

**Refresh runtime state** is a separate manual, point-in-time observation of the actual runtime
entry, coordinator transition, recorded run, Shutdown, retirement and queue-drain facts. It does
not poll or automatically refresh; steering uses its captured target, subject to host revalidation. No entry/no recorded run is not
idle or completed, and queue depth is explicitly unknown. Configuration is runtime-captured, not
verified provider-effective settings; pending prompt selection is shown separately. This query
does not read catalogs/history or start/discover providers. Its runtime instance/attachment
identity is not a Display revision, effect acknowledgement or history-recovery handshake.
Host epoch and selection are checked; obsolete presentation is discarded, but valid late identity
changes still require reload rather than retrying the old epoch. Repeated explicit refreshes retain
one frontend waiter and the latest pending refresh, not an unlimited set of overlapping requests.
This is not a bound on actual backend work. Malformed/oversized output is refused
without truncating authoritative fields (32 KiB transport budget, 256 UTF-16 units per identity/
configuration string, decimal-string attachment generation). Canceling a wait does not cancel
admitted runtime-owned work. This unary RPC is registered only in explicit owned mode.

After **Refresh runtime state**, **Steer observed run** submits text to that exact runtime,
attachment and recorded non-null run. It never creates or replaces a runtime, falls back to a
send/queue, or silently targets a later run. The host rechecks ownership and target identity;
stale, retiring, transitioning, terminated or unsupported targets fail rather than retarget.
While a turn runs, the compact composer offers a **Steer the running turn** icon beside Send; it is
the same action as `Ctrl+Enter` (see Composer) and is disabled for empty text with nothing queued.
The composer captures the run to steer when the prompt leaves, not when the key is pressed. A pending
Send is not an editable steering draft.
Steering preserves the existing run's permission authority and cannot reopen a closed review
window. Text is limited to 32,768 UTF-16 units. Success means input submitted, not run completed.
An uncertain steering request retains its immutable target, key and text across selection changes.
Use **Refresh submissions** to reconcile it or **Retry exact steering request** deliberately;
there is no automatic retry. A fresh runtime observation does not change that retained target.
Only one steering dispatch per session is in flight, independently of an owned send; steering
shares the host's 4,096-receipt limit. Closing presentation cancels only the waiter, while host
shutdown retains and joins accepted steering and cancellation work.

The compact prompt toolbar offers **Compact observed idle attachment** (or `Ctrl+F11` from the
selected owned workspace/prompt) only for an eligible point-in-time runtime observation or a
retained exact compaction intent. Send, exact Send recovery and observed-run cancellation remain
separate. For an uncertain intent, deliberate **Retry exact compaction request** never retargets after
refresh or session switch and waits for the original waiter to settle. No polling or automatic
retry occurs. The action requires no recorded run or queue drain. That is only eligibility: the
host and supported provider must admit compaction without waiting for active work. Unsupported
or stale targets fail without fallback, discovery or replacement. Compaction summarizes the
attachment's context **at provider admission**, not a history snapshot captured by the UI, and
may use the configured model/network and persist context changes. It creates no new permission
authority. One compaction per session
may be in flight and shares the 4,096-receipt limit. Refresh submissions for the settled outcome;
busy, unsupported and unsuccessful compaction are not success. Uncertain requests survive
selection changes for manual receipt reconciliation. Replaying a busy receipt does not try again:
a new explicit action uses a fresh key. Closing the panel cancels only the wait; shutdown retains
and joins accepted compaction and cancellation work.

**Queue text — this host only** uses an explicitly refreshed runtime/attachment observation, including
busy/draining attachments. `sessions.queue` reserves exact text for that attachment only; the receipt
separately reports reservation, host-only insertion and execution/cleanup. `queue_accepted` is not
durable or executed, and `queue_dispatched` is not proof of run completion. The composer does not
use this operation: its queue is kept in the window and each prompt leaves as a normal Send, which
resolves project references and applies the selection (see Composer). The
secondary queue editor keeps its own volatile draft per host epoch/session across panel selection
changes. While a queue request is retained, that disabled editor shows the exact request text;
after manual retry or receipt reconciliation of a composer-originated request, its separate draft
returns unchanged. A successful direct secondary-editor submission clears only that editor's
submitted draft if it has not been edited since capture, never a distinct later edit. These drafts
are not persisted across document reload or host-owner replacement. Refresh receipts manually to
inspect distinct phases.
**Cancel this queued operation** uses `sessions.cancelQueue` with the original operation ID,
never a later/current run.
Cancellation signalling is not rollback or target cleanup completion. Uncertain queue/cancel intents
retain exact keys and targets in App memory across selection/remount, with manual refresh/retry only
and a combined 256-intent bound. Cancellation-only recovery preserves unrelated queue draft text.
After document reload, receipts can be browsed manually, but lost local text/keys are not reconstructed.
No durable/restart recovery is provided. Existing terminal/live-tool queues and permission policy remain separate.

Actual cached-store reads are host-owned (eight active reads, excess rejected); cancelling an
RPC wait does not stop them. Shutdown joins command/read work before runtime dependencies.
Five seconds triggers a pending diagnostic, not termination. Unconfirmed host/native cleanup
keeps the lease; external termination is not confirmed cleanup. See [runtime contracts](runtime.md#explicit-desktop-submissions-and-owned-reads).

## Platforms

The tool package has launchers for Windows, macOS and glibc Linux, x64 and ARM64 (six RIDs, no musl).
See [native qualification](desktop-native-qualification.md) for the platform evidence and the
[test instructions](../src/CodeAlta.Desktop.Tests/README.md).
