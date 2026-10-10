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
launch project. The commands and the file changes of a session are approved automatically unless the
permission mode of the session, of its provider or of Settings > Permissions asks first (see "Permission
modes" below); provider input forms are cancelled unless an
isolated-root launch opts in to them. WebView-only data stays in the platform-local
application-data directory, and existing `.alta` storage is not migrated.

No-argument startup derives a stable WebView data directory from the platform's local application-data
location, composes the owned host for the current directory, and uses the current `~/.alta` catalog.
The host may update the standard project catalog, journal, cache, provider state and SQLite sidecars;
submissions may authenticate or use configured provider storage/network. It adds no desktop-specific
state to `.alta` and performs no storage migration. Help/version and rejected arguments do not initialize
native services or create storage. Explicit catalog and scoped-owned options retain stricter validation.

## Main window

The main window first opens centered at 80% of the primary work area on Windows; other platforms use a
centered 1280×860 window until NeoAstra exposes display metrics.

After that it opens **where it was** when the application last ended: at the same position and size, and
maximized or fullscreen if it was (`DesktopWindowState`). A window that was maximized goes back to the
size it had before when it is restored, and a window that was minimized opens in the state it had
before. The placement is in `window.json` of the application data directory, beside `preferences.json`,
so the normal and the developer instance each have their own. NeoAstra's `NeoWindowStateController`
writes it a moment after the window was moved, resized, maximized or restored, and once more when the
application ends. It is given back while the window is still hidden: a hidden window takes its bounds at
once and its state when it is shown, so the window never appears in its default place first. The
position is given back inside the displays that are there now (`NeoWindowStateRestore.Clamp`): a
window that was stretched over several displays stays over them, and a window that was on a display
that is gone comes back whole on one that is there, shrunk to its work area if it has to be. What is
compared with the displays is the rectangle the window shows, which the placement keeps since
NeoAstra 1.3.0, so a window whose visible edge was at the edge of a display stays there. Where the
displays are not known when the window is shown, only the size and the state are given back. A file
that is missing or malformed gives the default placement, and so does a file with that rectangle for
a CodeAlta built on NeoAstra 1.2.0 or earlier, which refuses it.

The window has no separate title bar: the page draws it. The CodeAlta mark sits at the top left
with the buttons of the workspace beside it (the name is not written, so that these buttons keep their
room in a narrow Explorer), the session tabs continue the same strip, and the platform's minimize, maximize and close
buttons stay at the top right. Drag the mark or any empty part of a tab strip along the top edge to
move the window, and double-click it to maximize or restore; tabs and buttons in that strip keep
their own clicks and drags. With the Explorer hidden the tabs start right after these buttons. In a split
layout only the panes along the top edge are part of the title bar. At the right, before the zoom and the
theme switch, is the **space switch**: the space the window shows (see "Spaces").

The window appears with a **start-up screen**: the title strip with the mark and name, the logo and a
progress bar, in the colors of the theme the window last had (dark on a first start). It stays until
the workspace has its first data, so the window is never shown empty. The host (catalog, providers,
plugins and their MCP servers) starts beside the view rather than before it. The window controls are
drawn for the application's theme, not the system's: dark symbols on the light theme, light ones on
the dark themes, and they follow a theme change at once. The theme and its background are kept in
`appearance.json` in the WebView data directory, which is what lets the window open in the right
colors before any page exists. On Windows the window is also filled in that background where the view
has not drawn yet, the part a resize uncovers: NeoAstra registers its window class with the system's
window color, white, so `DesktopWindowBackground` gives the class a brush of the theme's background
when the window is created, and a new one when the page changes its theme.

The view loads the documents of the application and no other: the start-up screen, then the
application (`DesktopApplication.DecideNavigation`). Its history still holds the start-up screen, which
nothing leaves, so the view has no history navigation (`NeoBrowserFeatures.HistoryNavigation` is off in
`DesktopWindowChrome.ViewOptions`, as `NeoBrowserFeatures.ApplicationShell()` selects it): NeoAstra
refuses the request itself, without asking the host. The back and forward buttons of a mouse, the
browser keys of a keyboard, a swipe and `history.back()` therefore leave the window on what it shows,
and the page still loads itself again. The UI tool `navigate_page` answers that history navigation is
turned off for the view when it is asked to go back or forward.

When the global `config.toml` cannot be loaded, the window opens on **configuration recovery** instead
of failing: the file in an editor with TOML highlighting, the error marked on its line and the caret
on it. The text is checked as it is typed, and the status under the editor says where it is still
wrong (select it to jump there) or that it is valid. **Save and continue** (Ctrl+S) writes a valid file
and starts the application; **Reload** reads the file again, after a confirmation when there are
edits; **Exit** closes the application without changing the file. A file that changed on disk in the
meantime is not overwritten. Recovery reads, checks and writes that one file only: no provider,
plugin or session is started before it is valid.

A file written by a newer version is the usual reason an installed one cannot read it (a value it
does not know for a setting it does), and recovery is then all that version shows. So recovery makes
the update check of the workspace too: when a newer version is published, a notice above the editor
names it, says that it may have written the file, and has the update command and, for an installed
tool, **Update and restart** (see "Updates"). The exit of that update asks nothing.

A provider whose `type` this version does not know is not such a case: it is the provider of a newer
version (1.2.0 refused the whole file for the `claude-code` type of 1.3.0). The file loads without
it. The provider is not registered or listed; the log has a line for it, Settings → Providers names
it above the list, and the configuration editor says so under the text. Every save keeps its section
as written, its key cannot be given to another provider, and a default provider that names it is
left alone. A provider without a `type`, and a setting that is wrong for a type this version knows,
are still refused.

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

`CodeAlta.app` on macOS has no icon in the menu bar: it stays in the Dock, whose icon brings the
window back, and the question and the setting say so (`trayIcon` of the shell's preferences is
false). The process of the bundle is its script, which became the tool (`exec`), and the menu bar of
macOS 26 no longer takes it for the application the system started: it refuses the icon, AppKit asks
again every second for as long as the application runs, and one of these requests can leave the
window's thread waiting forever, a frozen window in front of sessions that keep running
(`DesktopShell.HasTrayIcon`). `alta` started from a terminal keeps its icon in the menu bar.

The question, like the questions of an exit, is shown in front of an open window of the application
(Settings, the session browser): those windows are modal dialogs in the browser's top layer, and a
question added to the page itself would be behind them, out of reach (`frontLayer`).

On Windows the taskbar button of the window shows an indeterminate progress while a session runs or sends
its queued prompts (`DesktopWindowsTaskbarProgress`, `ITaskbarList3` through `NeoWindowPolishService`). The
activity is read from the runtime twice a second (`SessionRuntimeService.ListOverview`) rather than observed:
a session that only has background tasks does not count. A window that was hidden loses its taskbar button
and the progress with it, so the progress is set again once the window is shown; a state the taskbar did not
accept is set again at the next reading. The other desktops have no such progress and get nothing.

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

At its start the desktop asks nuget.org whether a newer `CodeAlta` package is published, as the
terminal application does for its own package (a prerelease build also considers prereleases). It
stays open, or in the notification area, for days, so it asks again: the page asks the host every
half hour, and when its window comes back after that long; the host looks at nuget.org again when
its last look is four hours old, or five minutes old after a look that failed or when the About page
is opened. Each newer version is announced once. Installing a version and opening its release notes
use what the last look found. The
tool package only names one package per platform (`CodeAlta.win-x64`, `CodeAlta.Tui.linux-x64`), and
nuget.org can list it well before those: a version counts once the package of the running platform
lists it too, so that the update command cannot fail on a package that is not there yet. A
newer version is announced by a notice with the version, the command that installs it
(`dotnet tool update -g CodeAlta`, with `--prerelease` for a prerelease) and a button that copies
it, and **View release notes**, which opens the release page in the browser. The application has to
be exited first (**Exit** in the tray, or `alta --exit`): the tool cannot be replaced while it runs.
Settings → About keeps the result under **Updates**. A build that is not a published version, and an
instance on explicit roots, make no request, however long they run.

For a tool installed with `dotnet tool install -g`, the notice and the About page also have **Update
and restart**. It hands the update to a small script in the application data directory (`update/`),
then exits as **Exit** does, with its questions; the script waits for the application to end, runs
the same `dotnet tool update` command with the .NET installation the application runs on, records
the outcome and starts CodeAlta again through the tool's launcher, which says whether it was
updated. Canceling the exit calls the update off, and the script gives up after fifteen minutes.
`update/update.log` keeps the command's output. A build output and the developer instance have no
such button. On macOS the script starts the application again through its bundle, which keeps its
icon and its place in the Dock, and through the launcher when the bundle does not open.

### Desktop entry of the installed tool

The first start of a tool installed with `dotnet tool install -g CodeAlta` adds CodeAlta to the
desktop, in the user's own folders and without elevation, and refreshes the entry when the version
or the launcher's path changes. The first time, the window says where it was added: a notice on
Windows and Linux, a guide on macOS (below).

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
  quarantined. If the tool is uninstalled, the bundle says so when opened.

  The icon in the Dock has three pictures, which `img/make-icons.py` derives from
  `img/CodeAlta.png`, because the Dock draws a picture in three ways:

  - From macOS 26 the system gives the icon of a bundle its own rounded shape and edge, at the size
    of its neighbours, but only when the picture is opaque up to its edges: any other is shrunk onto
    a grey tile. The bundle's icon is then `alta-full.icns`, the tile without its rounded corners and
    without an alpha channel.
  - Up to macOS 15 the icon of a bundle is drawn as it is. The bundle's icon is then `alta.icns`,
    whose tile keeps its shape and the margin of the system's icon grid (824 of 1024 points, over a
    soft shadow): a tile that fills the canvas would look bigger than its neighbours.
  - A picture that the running application sets is drawn as it is on every version. A process
    started by its executable (`alta` in a terminal, a build output) has no bundle to take an icon
    from, and sets `alta-dock.png`, the tile with the same margin. It is a PNG because the window
    services take no other file than `.ico` and `.png`.

  An application started as the bundle sets nothing: the Dock already draws the bundle's icon, and a
  picture of its own would replace the shape the system gave it. The bundle is written again when
  the version of macOS now asks for the other icon family. One start is the exception: the Dock
  keeps the picture it has of an application that runs, whatever happens to its bundle, and shows a
  new icon at the next start. So the start that gave an existing bundle another icon than it had
  (the first one after an update that changes the icon) sets `alta-dock.png` for itself.

  Few people open the Applications folder of their home folder, and the Dock has no supported way
  for an application to add itself (only an edit of the Dock's preferences followed by a restart of
  the Dock). So the first start, which is one from a terminal, shows a guide instead of a notice:
  **CodeAlta is in your Applications folder**, with a picture of the application going from that
  folder to the Dock, how it is started from now on (Launchpad, Spotlight, the folder), and
  **Reveal in Finder**, which opens the folder with `CodeAlta.app` selected, ready to be dragged.
  It waits for the provider settings of a first start to close, and it is never shown to an
  application that was started as a bundle: its user has found it. A tile in the Dock is the
  bundle, so it starts the installed version whatever it is, and the first start of each version
  writes the bundle again with its icon: an update changes neither the tile nor its place.
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

CodeAlta's own zoom commands (**Zoom In** `Ctrl+=` or `Ctrl+Plus`, **Zoom Out** `Ctrl+-`, **Reset
Zoom** `Ctrl+0`, also `/zoom_in`, `/zoom_out` and `/reset_zoom`) zoom the whole view, from 25 to 500
percent in Chrome's steps, and work whatever window of the app is open. A focused terminal keeps those
keys for its program. The host applies the zoom to the view (`desktopShell.zoom`) and keeps it in
`preferences.json` (`zoom`, written only when it is not 100), so the next start opens at the same zoom
before the page loads.

The title bar shows the zoom as a percentage, before the theme switch (`WindowZoom`). A click opens **Zoom
out**, the percentage, which goes back to 100%, and **Zoom in**; a step that does not exist is disabled (at
25 and at 500). These buttons run the same commands as the keys, and every answer of `desktopShell.zoom`
updates the percentage shown (`zoomWindow`), whoever asked. The tab strip at the end of the title bar leaves
the room of both buttons (`--window-actions-width`). A page without a shell (the browser demo) shows no zoom.

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
geometry. Use the window's sections, Escape, Close settings or a click beside the window to return.
Configuration, Providers, Models, Agent prompts, MCP Servers and Logs are **not** workspace tabs. Session tabs belong to the
global workspace, not the selected project: sessions from different projects can stay open together.
Drag a tab along the tab strip to reorder it, to a pane edge to create a split view, or to its center to merge
(up to 32 open sessions); drag the divider between panes to resize them. The presentation uses
stable content slots and one vertical Explorer with Projects above Sessions. Its width is locally
saved (220–720 pixels, never more than 60% of the window); the Explorer button in the title bar hides it without discarding the width.
The divider between the Explorer and the tabs (**Resize Explorer**) is dragged, or moved with Left and
Right by 16 pixels; Home or a double-click returns to the default, 272 pixels. The width is kept in
`codealta.desktop.ide-width.v1` (`ideWidth.ts`); when it cannot be saved, a notice says so and the
layout stays as it is. In a window of 875 pixels or less the Explorer has no column of its own: the
Explorer button shows it over the content, under the title bar, at most 320 pixels wide and without its
divider, and that reveal is not saved (it ends when the window crosses that width). The title bar is
the same with it and without it. The frame is a CSS grid
(`.workspace-shell` in `style.css`); `SessionContentLayout.tsx` gives it three slots, the Explorer, the
divider and the content, which stay mounted while the Explorer is hidden.
The search of the window, the automations and Settings are the buttons next to it, right after the CodeAlta mark. Notes belong
to each session, start collapsed when empty, and open when meaningful content arrives. A small disclosure
at the top right of the timeline expands/collapses their floating panel without resizing the timeline or
composer. The panel (or its collapsed disclosure) can be dragged anywhere over the timeline and the open
panel resized; its size and position are kept across collapse/expand and saved locally, relative to the
pane's top-right corner, and its header restores the default. There is no nested notes dock or notes divider.
Project and session rows open floating action menus from their **…** button, context menu or Shift+F10; the
Projects header has a sort/actions menu (with **Open project…**) and a **+** button that adds a
project folder with the operating system's folder dialog. The Explorer has no filter of its own: a project or
a session is looked for in the search of the window (see "Search"). A dot-matrix spinner on a
session tab, its sidebar row and its project marks an observed running session, including sessions whose
tab is closed. Existing session-keyed transitions and
draft/uncertain-action guards remain in place.

Session info, Reminders, context usage, timeline details, file changes, tool records, the raw history
source, About, project details, the saved-session browser and the model/prompt choosers open as
windows: drag the title bar to move them, drag an edge to resize them, and use the title bar's restore
button (or double-click it) to return to the default size and position. Each kind of window remembers
its own geometry. Closing Reminders does not cancel an admitted action or retry an uncertain Save.

A click outside a window closes it, as Escape does: the window gets the `cancel` event it handles for
that key (`dismissDialogsOnOutsidePress` in `modalDialogs.ts`, one pair of listeners for every `dialog`
of the page). The press and its release are both outside, so a selection or a window dragged out of its
dialog closes nothing; with a menu or a popover open in the window, the click closes that alone. A
window that asks something, or holds what is being typed, stays open and is closed by its own buttons
or Escape: the input a provider asks for, the dialog of a plugin
other than a message, the editor of an automation and **New space**. Such a `dialog` carries
`data-outside-press="keep"` (`keptOnOutsidePress`, or `keepOnOutsidePress` of `AppWindow`). The
questions that are Blueprint dialogs (unsaved changes, closing the window, exiting) already ignore a
click outside them.

In the explorer, a project row has one **…** menu (also on right-click): **New session**, **Search
sessions…** and **Browse saved sessions** for that project, then **Open**, **Add to favorites** (or
**Remove from favorites**), **Details**, **Rename project…** and **Archive project…**. **Chats** has
the same session actions. **Search sessions…** opens the search of the window on the sessions of
that project, or on the chats.

The **Chats** are the sessions of no project, which the TUI calls global sessions. Their row is the
first of the Explorer, above the projects, and is closed until it is opened.

Each project opens and closes on its own, and several can be open, each with its sessions. The chevron
of a row opens and closes it without selecting it. Clicking a project selects it and opens it; clicking
the selected project closes and opens it. A project that becomes the selected one in any other way (a
session tab of another project, **Open project**) is opened too. **Collapse all** in the Projects header
closes every project and the chats. What is open is kept with the favorites and the collapsed sessions in
this WebView's local storage (`codealta.desktop.projectTree.v1`) and restored as it was at the next start;
with nothing stored, the selected project is the one open.

Up and Down go through the rows of the Explorer, Home and End to the first and the last. Right opens a
project and Left closes it. On a session that has sub-agents, Left hides them and Right shows them; Left
on any other session goes to its project.

The sessions of an open project are the same rows whether it is selected or not: the most recent ones
(the recent-session count of **Settings → Appearance**), **Show more…** for the others. That count is of
the sessions of the project itself: the sub-agents of a session are listed under it and have a count of
their own (the sub-agent count of the same page, 4 by default), at every level, with a **Show more…** of
their own under them. A session that has sub-agents starts with a twist that hides them and shows them
again (`explorer/sessionTree.ts` makes the list). A collapsed session hides the selected session too; a
selected session beyond a count stays listed, with the sessions it is under. A click opens
the session, which selects its project. **Rename…** and **Delete…** act on the selected session, so in
another project they open the session first. `Delete` on the row that has the focus does what
**Delete…** does. The form of a new session and the
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
or renamed with, also when a later Send attaches it again (another model, a restart) and when it is
continued from the terminal UI, which lists a session by the first line of its summary; a session that
was never named shows the first line of its summary, 80 characters at most
(`SessionRuntimeService.ListedTitle`). That line follows
what the session says, also after a restart or another model: a Send that attaches a session again
leaves its saved title as it is, so the line never becomes the name of the session.

**Delete…** of a session, **Archive project…** and **Unarchive project…** ask in the same kind of
popover beside the row (`ConfirmPopover.tsx`): the question, the name it is about, what it does, and
**Cancel** and the button that does it, which has the focus. Enter answers yes; Escape, **Cancel** or a
click elsewhere leaves everything as it is and gives the focus back to the row. What is refused is said
in the popover. **Do not ask again** makes the next ones run at once, and their menu entries lose their
ellipsis; what is refused is then said in a notice. The two answers are kept in this WebView's local
storage (`codealta.desktop.confirm.sessionDelete.v1`, `codealta.desktop.confirm.projectArchive.v1`) and
taken back with **Ask before deleting a session** and **Ask before archiving a project** of
**Settings → Appearance**. Nothing is typed to delete a session: the request still names the session by
its id and the title it is listed with (`confirmedTitle`), and the host refuses a session whose title
is no longer that one. After a deletion the row that takes the place of the deleted one has the focus,
so several sessions are deleted from the keyboard. A question goes away with its row when the Explorer
is hidden.

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

**Background tasks.** A provider can go on working for a session after the tool call that started the
work returned, and after the run ended: Claude Code with a command in the background, a watch, an
agent of its own. Such a session is not running (it takes a prompt) and is not idle either:

- its row in the Explorer and its tab show a dot that breathes, quieter than the spinner of a run
  (`BackgroundMark`), where the spinner would be;
- the status line above the prompt says **N background tasks** after its usual text, also while a run
  thinks. It opens the list of the tasks, each with what it does, its kind, how long it has been going
  on and a button that stops it (`BackgroundTasksStatus`);
- the tile of the tool call that started a task says **Running in the background** in the place of
  **Completed**, with the spinner of a call that runs, and **Stopped in the background** or **Failed in
  the background** once the task ended that way. The window of the call says the same with a tag. A task
  that ran to its end leaves the tile as **Completed**.

How it reaches the page:

- The provider tells its tasks with `AgentBackgroundTasksEvent`: every task that goes on (identity, kind,
  description, the tool call that started it) and the tasks that just ended. The event names no run,
  because an event with a run marks its session as running, and it is given to those who listen without
  being kept in the history or recorded: it is the state of a process that runs now, and a session read
  again later shows no task.
- `RuntimeSessionEntry` keeps the tasks that go on and the last ones that failed or were stopped (16 at
  most), which `SessionRuntimeCurrentEntry.BackgroundTasks` and then `runtimeState` carry
  (`SessionRuntimeStateEntry.backgroundTasks`, each text bounded, the tool call named as the timeline
  names it). No new channel: the open panel reads it every second with the rest of the runtime state,
  and the Explorer every five seconds, as for a run.
- In the page, `backgroundTasks.ts` reads the list, `runtimeObservations` counts the tasks that go on
  for the marks (`sessionBackground`; the open panel wins over the polled row, as for the run), and
  `BackgroundCallsContext` gives the tiles of the session the state of the task of each call.
- **Stop** calls `sessions.stopBackgroundTask` (epoch, session, task), which reaches the provider through
  `OwnedSessionCommandService.StopBackgroundTaskAsync`, `SessionRuntimeService` (only a task the entry
  lists as going on is asked to stop), `AgentHub` and `IAgentBackgroundTaskProvider`. It is not a command
  of a run and leaves no receipt: its effect is the task leaving the list.
- After a restart of the application nothing is shown: the tasks ended with the process of the provider.

**Background jobs.** A session of any provider can also start a command in the background itself, with
`alta job start` (see "Background jobs" in [live-tool.md](live-tool.md)): CodeAlta runs the command and
gives the session a prompt when it ends. A job is listed with the background tasks of its session, and
shows more than a task of a provider, because CodeAlta owns its process:

- `SessionRuntimeService` adds the jobs of the session to `SessionRuntimeCurrentEntry.BackgroundTasks`
  (`WithJobTasks`: kind `command`, the title of the job or its command as the description, `IsJob`,
  `ExitCode`, `EndedAt`) and counts the ones that run in `ListOverview`, so the dot, the count and the
  spaces follow them as they follow a task of Claude Code. The wire says `isJob`, `exitCode` and
  `endedAt`, and the state `completed` for a job that succeeded.
- The list keeps a job for ten minutes after it ended (`listedBackgroundTasks`), with **Succeeded**,
  **Failed** and its exit code, or **Stopped**; the status line then says **Background tasks** without a
  count while nothing runs.
- A job has a button that opens what it writes in a window (`BackgroundJobDialog`): the output as it
  comes, in the read-only terminal of a tool call, with the time it has run, its exit code and **Stop**.
  The output is followed through `toolCalls.observe` with the identity of the job in the place of a
  call (`ToolCallsService.Jobs`), so the page keeps one store for both; a job of another session is not
  served.
- **Stop** goes the same way as for a task of a provider; `SessionRuntimeService.StopBackgroundTaskAsync`
  sends an identity that starts with `job-` to the jobs. A job the user stops tells its session so,
  unless the job was started with `--notify success` or `never`.
- The prompt that tells the end of a job is a row of the timeline titled **Background job**, with the
  result as its subtitle, the command and the end of the output (`jobResult.ts`), not a prompt of the
  user.
- The question before the application exits counts a session whose job runs with the sessions that
  run (`CountSessionsAtWork`): exiting ends the commands.

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
  is unavailable; Settings opens only from the project rail, shortcut or search (Ctrl+G, Ctrl+U
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

## Spaces

A **space** is a named group of projects the user works on together. The window shows one space at a
time. The word is *space* in the interface, the files, the `alta` tool and the code; it is never called
a workspace, which here is the catalog of projects and sessions and the main area of the window.

- The **Default** space (`default`) holds every project, always exists and cannot be deleted. The user
  creates the others; a project can be in several. A catalog holds at most 32 spaces
  (`SpaceCatalog.MaximumSpaces`).
- A space has an id, a name (1 to 64 characters on one line, unique without regard to case), an optional
  description of at most 2000 characters ("What it is for", read by the user and by the agents through
  `alta space list`), an icon (a general icon or a brand logo, as a provider has) and a color (`#rgb` or
  `#rrggbb`). The id is worked out from the first name (`SpaceDescriptor.IdFromName`: lower case, one `-`
  for what is neither a letter nor a digit, `space` when nothing usable is left, a number after it when
  the id is taken) and a rename keeps it.
- On a profile that never had spaces, the first start of CodeAlta Desktop creates two empty ones,
  **Work** and **Personal** (`SpaceCatalog.SeedAsync`, called in `DesktopApplication` before the page
  reads the spaces). It does nothing once `~/.alta/spaces/` exists, so what the user deleted does not
  come back.
- Chats, the sessions of no project, are shown in every space.
- Deleting a space deletes its file, then its id in the file of each of its projects. The projects,
  their folders and their sessions stay, in the Default space and in their other spaces.

**What follows the shown space.** Everything the window lists: the Explorer (projects, their sessions,
terminals and work item badges), the search (projects, sessions, files), Open project, the browser of
saved sessions, the Work items tab ("All projects" is the projects of the space), the project list of the
Issues tab, and previous/next project. Automations are the application's: their tab lists every project.
A session opened from a link, a work item or an automation whose project is not in the shown space
shows the Default space first (`revealSession`). The code editor or the Changes tab that an agent asks
for (`alta editor open`, `alta diff show`), or that a link to a file opens, is shown at once only for a
project of the shown space. For a project of another space the window stays where the user is and a
toast says where it opens ("The editor of CodeAlta opens in Work"), with **Show**: the button shows
that space, the first of the spaces of the project, else Default, selects the project and opens what was
asked (`placeProject` in `spaces.ts`, `showInSpace` in `main.tsx`). A request repeated for the same
project takes the place of its toast. The command is answered `project.notInShownSpace`, so the agent
knows that nothing was opened (see "A project that the shown space does not have" in
`doc/live-tool.md`). The tab of a terminal (`alta terminal show`) opens in the shown space whatever its
project. A folder added with **+** or Open project while another
space than Default is shown joins that space; a folder that is already a project of another space joins
the shown one (`joinShownSpace`) instead of being opened elsewhere.

**Tabs.** Each space has its own tabs (sessions, code editors, Changes, terminals, Work items, Issues,
Automations) and its own layout. Showing a space swaps the Explorer and the tabs; the tabs of a space
come back when it is shown again, also after a restart. The panes of a space that is not shown are not
in the page; its sessions keep running in the host, and a prompt being typed is kept. When code editors
hold unsaved edits, leaving the space asks first (Save, Discard, Cancel). A space that is gone (removed
here, by a command or by the other instance) gives its place to the Default space, and the tabs kept for
it are forgotten.

**Space switch.** In the title bar (`SpaceSwitch`). With the Default space alone it is an icon; with
several it shows the icon, in its color, and the name of the shown space. It opens the list of the
spaces, each with what its sessions do and its `Ctrl+G 1`…`9` hint, then **New space…** and **Organize
spaces…** (in a window with a host of its own). A dot on the switch says that a session waits for the
user or failed in a space that is not shown.

**Foot of the Explorer.** With more than the Default space (`SpaceActivityBar`), one button for each
space, as its icon, the shown one marked. A mark says what the sessions of the space do, the first that
applies: a session waits for the user (a question, a command to review, a form), a run failed, sessions
run, a provider works in the background. Above the buttons, up to three lines name a session that waits
or failed in a space that is not shown (`spaceCalls`: one for each space, a session that waits before one
that failed, named for the first space of its project); a click shows that space and opens that session.
When a session starts to wait in another space, a toast says so once, with **Show**; what already waited
when the window started or left a space is not said.

**New space.** The window (`SpaceDialog`) has a row of ready-made spaces (`spaceTemplates`: Work,
Personal, Open source, Experiments, Learning, Clients; a name, an icon and a color in one click, the
names already taken are not offered), the name, the icon, the color, "What it is for" and a checklist of
the projects. A space created from the title bar is shown at once. An empty space says "No project in
this space yet." with **Add projects…**, which opens Settings → Spaces.

**Implementation.**

- *Catalog* (`src/CodeAlta.Catalog`): `SpaceDescriptor` (the values and their validation),
  `SpaceCatalog` (`LoadAsync`, `LoadMembersAsync`, `CreateAsync`, `UpdateAsync`, `DeleteAsync`,
  `ReorderAsync`, `AssignAsync`, `SeedAsync`; the changes of one instance run one after the other, and a
  file that cannot be read as a space is left out so that it never hides the others),
  `ProjectCatalog.UpdateSpacesAsync` in `ProjectCatalog.Spaces.cs` (the in-place edit of the `spaces`
  entry of a project file, under the project edit gate, see `doc/catalog-and-config.md`),
  `CatalogOptions.SpacesRoot` and the space front matter of `CatalogYamlSerializer`. Tests:
  `src/CodeAlta.Catalog.Tests/SpaceCatalogTests.cs`.
- *Host*: `CodeAltaHost.SpaceCatalog`. The RPC service `spaces` (`Desktop/Rpc/SpacesRpc.cs`) has `list`,
  `create`, `update`, `delete`, `assign`, `reorder`, `shown` (the page says which space it shows),
  `activity` and `watch`. `DesktopSpaceView` implements `IAltaSpaceView`: it holds the space the page
  says it shows, which the `alta` commands read as the current space, and sends the page the requests
  `show` (`alta space switch`) and `changed` (a command changed the spaces or the projects) over
  `watch`. It is no setting of the user. `activity` lists the sessions that run, work in the background,
  whose last run failed (`SessionRuntimeService.ListOverview()`) or that wait for the user
  (`OwnedSessionAskService.ListWaitingSessions()` and
  `SessionPermissionService.ListWaitingSessionsAsync()`), the ones that wait first, at most 128. Tests:
  `src/CodeAlta.Desktop.Tests/SpacesRpcTests.cs`.
- *Page* (`frontend/src/spaces/`): `spaces.ts` holds the rules without the DOM (`readSpaces`,
  `scopeSnapshot`, `spaceActivities`, `spaceCalls`, `spaceStorageKey`), `spacesHub.ts` the link to the
  host, `SpaceViews.tsx` the switch and the bar, `SpaceDialog.tsx` and `SpaceSettings.tsx` the two
  windows, with `spaces.css` and the tests in `spaces.test.tsx`. In `main.tsx`, the catalog snapshot is
  kept whole (the `catalog` ref) and the window is given what the shown space has of it: the
  `workspace.snapshot` wrapper applies `scopeSnapshot`, so everything that reads the snapshot follows
  the space without knowing of it. `showSpace` publishes the snapshot of the other space and swaps the
  tabs, kept for each space in memory and under storage keys of the space. `SessionTabStrip` is given
  one FlexLayout model for each space (`layout`) and is keyed by the id of the space. The hub lists the
  spaces when it connects, when the host says they changed and after each of its own changes; it asks
  `spaces.activity` every 4 s, only while there is more than one space and the window is visible.
- *Storage of the page* (local storage of the window): `codealta.desktop.space.v1` is the shown space;
  `codealta.desktop.sessionTabs.v1.<id>` and `codealta.desktop.fileTabs.v1.<id>` are the tabs of a
  space. The Default space keeps the keys without a suffix, which are the ones the window had before.

The `alta space` commands, and the `alta project` commands that follow the shown space, are described
in `doc/live-tool.md` ("Spaces" and "Project commands"). CodeAlta TUI shows no spaces and lists every
project; its `alta` tool has the `alta space` commands except `switch`.

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

Beside the chip, a small button (`PermissionModeMenu`) names the permission mode the session runs in and
opens the modes it can be given (`SessionChoicesResponse.PermissionModes`; empty hides the button, as in
a host that forces the review). Each mode has a name and a line that says what it does; the one the
session runs in has a check. The mode a session without one runs in
(`SessionChoicesResponse.DefaultPermissionMode`: the provider's `permission_mode`, else the default of the
application) is marked **Default** in the list, and choosing it gives the session no mode of its own; a
default a session cannot be given (`plan`, which stays provider-wide) has an entry of its own. The button
is quiet while the session runs in the default mode and stands out when it has a mode of its own. A
saved mode that is no longer offered stays shown as **Unverified**. The choice travels with the next Send
(`SessionSelection.PermissionMode`: an id, or `"provider"` to go back to the default; absent keeps the
session's); the host keeps it in the session's local state (`permission_mode`), reads the policy of the
send from it (see "Permission modes"), and for Claude Code switches the running CLI with
`set_permission_mode`, restarting it only when the CLI refuses. A provider switch clears it. New-session
drafts have no button: the choice appears once the session exists.

The provider indicator is a compact active-provider count, green when ready and orange when
providers fail or are unsupported. Owned startup initializes the configured providers, as in
the TUI; inventory reads themselves do not probe. Compaction has a persistent icon, disabled
until an idle attachment is verified. While a compaction runs, the status line above the prompt
shows **Compacting…** with the activity spinner and the icon is disabled: a compaction is not a
run, so the status follows its receipt, which stays pending until the provider ends it. The context
meter is read again when it ends. Send and Stop occupy one slot, not two adjacent buttons.
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
way opens the issues and the pull requests of the project's hosted repository (**GitHub issues**, **GitLab
issues**, **Azure DevOps work items** or **Bitbucket issues**, from the project's git remote): an icon that
says which it is, number, title, state and last update, most recently updated first, 50 at most. The search field matches a number or title words,
**Include closed** (`Ctrl+I`) filters closed issues, and Enter inserts `[#123](url)`. The credentials
come from the provider's environment variable or CLI (`GITHUB_TOKEN`/`GH_TOKEN` or `gh auth token`;
`GITLAB_TOKEN` or `glab config get token`; `AZURE_DEVOPS_EXT_PAT` or `az account get-access-token`;
`BITBUCKET_ACCESS_TOKEN`). The pull requests are found beside the issues from one listing of the recent ones,
kept a minute; a provider that refuses them still shows its issues.
Both windows are resizable and remember their size. Catalog-only and unverified inputs have no picker.

### Images of a prompt

Clipboard PNG, JPEG, WebP, GIF and BMP files pass through the browser decoder and are normalized to
PNG before wire validation, so editor-generated PNG metadata, palettes and interlacing are accepted.
The PNG the canvas writes is not always the narrow one the host takes (`OwnedPromptImages` refuses
compressed metadata, animation and unknown chunks): WebKit, the web view of the macOS application, adds
an `eXIf` chunk. `stripPngMetadata` leaves out the chunks a decoder may skip and the host does not take
before the validation; a PNG without such a chunk is sent as the canvas wrote it.
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

When a tool result has images (`view_image` on a file, `take_screenshot` of the UI tools, a screenshot tool
of an MCP server), the timeline
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

- **The questions take the place of the prompt.** With several questions, a row of numbered steps
  names them: the one that is shown, the ones that were seen (a check mark) and the ones to come; a
  step is a button that goes to its question. A question shows its text, its description and its
  choices as cards of a single selection (a number, the label, the description; the first one
  selected) and, when the ask allows one, a text answer. `Enter` and the button go to the next
  question that was not shown yet (**Next**) and send once all were shown (**Submit**); **Back**
  returns to the previous one; `Shift+Enter` is a new line in a text answer. Outside text, a digit
  `1`–`9` selects the choice of that number, `Up` / `Down` change the choice and `Left` / `Right` the
  question, as `Ctrl+N` / `Ctrl+P` do everywhere. A question may be left without an answer. The
  sources are `frontend/src/AskPanel.tsx` and `AskFileReview.tsx`.
- **A file to review takes the place of the timeline.** Under a header `File context: <path>` (` *`
  while it has unsaved edits), a Markdown file opens as it reads: **Read** renders it with the
  Markdown of the timeline (headings, tables, highlighted code, `mermaid` diagrams), which is how a
  plan is reviewed. **Source and comments** shows the source of the file in an editor, with line
  numbers, the highlighting of its type and wrapped lines; a file that is not Markdown has only that
  view. The comments and the unsaved edits are kept when the view changes, and **Read** shows the
  text as it is edited. In the source, `Ctrl+K`, the **Comment** button or a click in the margin beside a
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
owns the timers. A firing whose session runs a turn gives the reminder to that turn, as a steering
message, or leaves it in the queue of the session for the end of the turn when the provider takes
nothing during a turn; it does not attempt a Send, which a running session refuses. A firing whose
session is idle attempts one normal owned Send, with the same permission policy and host drain as
other sends. When that Send is refused as busy without a running turn (another Send of the session
is being admitted), the firing asks again every second, at most 10 times, and the turn that runs by
then is given the reminder. Unavailable or failed sends, and a session that stays busy, count as
failed attempts; there is no other automatic retry beyond the requested repeats. A completed reminder means all attempts
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
(a breakdown bar in which each token is in one slice: uncached input, cache write, cached input,
output without its reasoning, and reasoning; with the whole input, effort, initiator, duration and
cost beside the title), rate limits (plan, primary and secondary windows with used
percentage, window length and reset time) and the provider's session totals. **Copy as Markdown**
copies the same content; **Refresh usage** reads again.

`sessionUsage.read` is an owned-only generated Desktop RPC for an existing actor and the actual host
epoch. It requires explicit project ID/path or global scope, checks the exact persisted session header,
confirms unarchived project ownership when applicable, and rechecks the original attachment after
asynchronous reads. It returns the last admitted usage event: numeric counters as decimal strings,
bounded single-line text (model, effort, initiator, labels, plan; anything longer than 128 characters
is dropped), rate-limit windows and the provider's cumulative session totals. Providers split usage
across events, so the window keeps the newest value of each field seen on the same attachment and
starts over when the attachment changes. The last operation is the exception: an operation that
reports tokens is another request and replaces the one before it whole, so that its initiator, its
cost and its cache counts are not shown on the next request (the TUI and the usage recovered from a
journal do the same). Copilot quota snapshots, named Codex limits and compaction details are not shown.

**Subscription usage.** The window shows the limits of the subscription as meters (`UsageLimitList`,
`subscriptionUsage.ts`): the two rate-limit windows of the last observation while that observation is
at most 15 minutes old (`liveLimitsAreCurrent`), and otherwise the answer of `providerUsage.read` for
the provider of the session, which is asked when the window opens and on **Refresh usage** only. A
limit is named after the length of its window (5 hours, a day, a week, a month) or after what the
provider counts; the mark on a meter is the share of the period that has gone by.

**The `providerUsage` RPC.** `read(expectedEpoch, key, refresh)` returns the usage of the subscription
behind a configured provider: `plan`, `observedAt` and at most 16 `limits` (`id`, `name`,
`usedPercent`, `resetsAt`, `windowMinutes`, `used`, `total`, `unit`, `unlimited`, `remaining`, in
whole numbers). `supported` is false, with status `ok`, for a provider type that is not Codex, Copilot
or Claude Code. Other statuses: `signed_out`, `tool_missing` and `tool_signed_out` (with `tool`, the
command-line program that is asked: it is not installed, or not signed in with the account of the
provider), `not_available`, `failed`, and the codes of a provider that cannot be resolved. An answer
is kept for a minute per provider, a failure and a refreshed answer for five seconds, and concurrent
requests for one provider ask it once. `ConfiguredProviderUsage` (Hosting) reads it: Codex through
`codex app-server` (`account/read`, then `account/rateLimits/read`, for the same e-mail address as the
sign-in of CodeAlta), Copilot from `copilot_internal/user` with the stored GitHub token, Claude Code
through the `get_usage` control request of its CLI. No model turn is sent, and the credentials of the
two CLIs are never read. The Providers page shows the answer in the **Usage** section of a saved
provider (`ProviderUsagePanel`).

## Timeline

### Timeline cards

The timeline is compact: every card has the same small padding and the rows are spaced by the list
alone. Consecutive tool calls of one run share a single **Tool calls** card, their tiles side by side
(up to 60 per card); only something the timeline shows between two calls (an assistant message, a file
change, a status row) starts a new card. Records that show nothing, such as usage updates or reasoning
without text, do not. A call that starts is not in the journal yet: its tile joins the card of the calls
of its run shown just before it, and stays there once the journal has it (`groupTimelineTools`).

### Tool calls

A tool call has one tile from the moment it is requested to its end. The tile names the tool and what it
works on (the command of a shell call or of an `alta` call, a path, a query, the first file of a patch, or
its arguments on one line), then its state, and what it wrote. While the call runs, the tile turns its
dots, counts the lines and the size of its output and shows its last line. A shell command that ended
shows its exit code when it is not zero, and the first line it wrote rather than the header of its result.

Clicking a tile opens the window of the call, which follows the call like the tile does: opened on a
running call, it shows the output as it comes and the result when the call ends. Its title has the name of
the tool and its state. Under it a line of facts: the time the call took, the exit code, the size of the
output, the files and the lines changed, the timeout and the working folder. Then two tabs:

- The first tab depends on the call. **Output**, for a command: the command as it is typed, with the colors
  of its shell, and a terminal that shows what the command wrote (xterm, read-only: colors, carriage
  returns, selection, `Ctrl+C` to copy, the scrolling keys; it opens on the end of the output and keeps its
  scroll bar in view when there is more above). The standard error follows the standard output
  under a rule. **File**, for a file that was read: its lines with their numbers, in the colors of its
  language. **Changes**, for an edit: the diff of the file with the line numbers of both sides, highlighted;
  a new file is shown as the file it is; a call that changed several files (`apply_patch`) has a tab per
  file on the left, each with its counts. Before the record of an edit has a diff (the call is running, was
  refused or failed), the changes are read from its arguments: the patch of `apply_patch`, the old and new
  text of `replace_in_file`, the content of `write_file`. **Result**, for any other call: its arguments by
  name (a short value on the line of its name, a long text or a structure in a block), the images it gave
  the model, and its result. The result of `grep` is shown by file with what matched marked, the one of
  `list_dir` as entries with their icons, JSON is formatted (the records an `alta` command writes, one per
  line, each under its type), a text with fenced code blocks is rendered as Markdown, and any other text is
  shown as it is. A failed call says its error once, above the tabs. The tabs take the height the window
  has left: a long file, diff or result scrolls in its view, and nothing is cut at the bottom of the window.
- **Details** has what the records hold as they are: the tool, the kind of call, the provider, the run and
  the call identity, when it started and ended, its arguments as JSON and the text of its result, each
  with a copy button, and the files the call read and modified.

Where the data comes from:

- **The row.** The timeline folds the records of a call (requested, started, ended) into one row, at the
  time of the first record, with the key `tool:[provider, run, activity]` (`reconcileTimeline`). The live
  view (`display.observe`) learns of a phase before the journal is read again: the row shows the later of
  the two. The host adds to the row of a call a summary (`HistoryToolProjection`): the primary text, the
  lines and bytes of the output, the lines added and removed, and the exit code of a shell command.
- **The record.** A history row is bounded, so its texts can be cut. The window reads the whole record
  with `toolCalls.read(expectedEpoch, sessionId, offset, outputOffset)`: the journal offsets of the call's
  newest activity record and of its output record, as the row has them. The reply has the command, the
  working folder, the exit code and the error when the record states them, and three texts (the arguments
  as JSON, the output, the diff), each as a part of at most 256 Ki UTF-16 units with its whole length; the
  page continues a longer text with `part` and `position`, up to 4 Mi units, beyond which it says that the
  rest is too long to show. The page keeps the last 24 records it read. Reading is not tied to a project.
- **The live output.** A tool reports its output while it runs as content deltas that no journal keeps
  (`shell_command` sends each line). The runtime retains them per running call
  (`RuntimeToolOutputProjection`, see `doc/runtime.md`) and `toolCalls.observe(expectedEpoch, sessionId,
  activityId)` streams them: what the call wrote so far, then each addition, in items of at most 64 Ki
  units, until the call ends. The page opens one channel per running call that a tile or a window shows
  and closes it with them. When the call ends, the window shows the output of its record.

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
the provider's usage figures, and one line per tool bucket. The provider's totals add up the requests
of the turn: the session repeats the usage of its last request on later updates (its idle update, a
compaction), and a request is counted once. The rows are not stored in the session;
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
This is not full GFM or a CommonMark-conformance claim: strike renders as `s` rather than
`del`, and grid tables, footnotes, math, emoji short codes and heading anchors are not
implemented. Footnote-like
syntax may parse as an ordinary reference link instead. Mixed inline HTML/Markdown
renders; Markdown inside block HTML follows CommonMark blank-line boundaries, not
arbitrary nested Markdown interpretation.

**Messages and documents.** A message breaks its lines where its text does. A document is
wrapped in its source at any width, so its lines follow each other and only a blank line, two
spaces or a backslash end one: the preview of a Markdown file in the code editor and the
instructions of a skill are documents (`MarkdownContent` with `document`).

**Front matter, tasks and alerts.** Three things that Markdown files and messages hold are
shown as what they are (`markdown.ts`, `markdownBoundary.ts`):

- **Front matter.** The lines between a first line `---` and the next `---` or `...` are taken off
  the text when they start with a key, as a YAML mapping does (at most 16 Ki characters); a text
  that only starts with a rule keeps it. They are shown as a table of names and values: a value on
  one line or a block of text as text, a list of plain items as a list, anything nested as the YAML
  it is written in. A front matter that is no list of keys is shown whole, as YAML. Nothing of it is
  read as Markdown or HTML.
- **Tasks.** An item of a list that starts with `[ ]` or `[x]` shows a box, empty or checked, in the
  place of its bullet. The box is a `span` of the renderer with the role of a checkbox that takes
  no input: no form control is ever created.
- **Alerts.** A quote whose first line is `[!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!WARNING]` or
  `[!CAUTION]` is shown as on GitHub, with its title in the language of the window and the color of
  its kind.

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
The table of a front matter, the box and the classes of a task, and the title, the class
and the kind of an alert are added after sanitization, by the renderer and from text alone
(`markdown-front-matter`, `markdown-task`, `markdown-task-item`, `markdown-task-list`,
`markdown-alert`, `markdown-alert-title`, `data-alert`): authored HTML that names them
loses them like any other class, role or data attribute.

**Fenced code blocks.** A fence that names a language is colored with highlight.js **11.12.0**: its
common set (C#, C/C++, JavaScript, TypeScript, Python, Go, Rust, Java, Kotlin, Swift, JSON, YAML,
XML/HTML, CSS, SQL, shell, diff, Markdown and more) plus CMake, Dart, Dockerfile, batch, Elixir, F#,
Haskell, HTTP, nginx, PowerShell, properties, Protocol Buffers and Scala, under their usual short
names (`cs`, `js`, `ts`, `sh`, `ps1`, `yml`, `jsonc`, `csproj`, ...). The block shows its language in
its top right corner. A fence without a language, with an unknown one, or longer than 200,000
characters stays plain. The colors follow the theme and the color scheme.

**Copy button.** Every code block (fenced, indented, or an authored `pre` with a `code`) has a button
in its top right corner that copies its text as it is written, without the last line end. It is drawn
beside the language, stays in the corner while a long block scrolls, and shows a check for a moment
once it copied. It is the one control inside rendered Markdown: `button.markdown-copy`, added by the
renderer after sanitization with constant attributes and no content (`dressCopy` in
`markdownBoundary.ts`); an authored button or class never passes the sanitizer. `MarkdownContent`
handles its click. The values of a front matter have none.

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
An anchor href survives when it is a credential-free absolute HTTP(S) address or, on a link that the Markdown
wrote, a file of this computer (see "Links to files" below); mailto, custom, script, network-path and
credentialed URLs do not. Fuzzy www/email/IP linkification is disabled.
Trusted container click, auxiliary-click and Enter handling always suppresses WebView navigation.
In an owned host every rendered Markdown has the opener of the window (`MarkdownLinksContext`): the
messages of a session whatever their author, the notes, the details and tool windows, the issues, the work
items, the instructions of a skill and the Markdown preview of the code editor. The inline preview of a
compact timeline row is the exception: a link is not followed there.
Normal click, Cmd/Ctrl/Shift-click, middle click and keyboard Enter follow the link once.
Scripted, already handled, Alt, right-click and repeated/composing Enter gestures do not invoke it, and a link
of the page under a modal dialog is not followed while a link of the dialog is.
Rendering/streaming never opens links. Detached contexts without the opener remain inert. The dedicated
`markdownLinks.open` bridge checks the host epoch and independently validates credential-free absolute
HTTP(S) addresses within 2,048 UTF-16 units, rejecting whitespace, controls, backslashes and malformed
escapes/authority; such an address opens in the system browser.
Stale grants cannot invoke the opener; a failure is a generic notice, not private diagnostics.
Native external navigation and new-window cancellation, `app://` restrictions and CSP remain unchanged.

**Links to files.** Any other target that the boundary keeps is a link to a file of this computer, which opens
the code editor:

- **What is a link to a file.** A link that the Markdown wrote (`[Program.cs](src/Program.cs#L42)`, a
  `<file:///…>` autolink, a `file:///…` address alone in the text) whose target is a relative path, a full path
  (`C:/code/a.cs`, `/home/me/a.cs`, `~/notes.md`) or a `file:` address without a host. The parser lets `file:`
  through and marks the file links it writes with a value made for the renderer; the sanitizer keeps a file
  target only on a marked link. An `<a href>` written in HTML therefore opens a page of the web and nothing
  else. A path of another computer (`//server/share`, a device path, a `file:` address with a host), another
  scheme (`mailto:`, `vscode:`) and a fragment alone are no link: the host never looks at such a path, which
  would already send the credentials of the user to that computer.
- **The place in the file.** `#L42`, `#L42C7` and `#L42-L50` as a fragment, or `:42`, `:42:7` and `:42-50`
  after the path, as the `@` references of a prompt write it. A range goes to its start.
- **Where a relative path starts from.** The folder the session works in for a message, the notes and the
  windows of that session (its git worktree while it has one, the folder of its project otherwise), and the
  folder of the document for the Markdown preview of the code editor (`MarkdownLinkScopeContext`). Elsewhere
  a relative path names nothing. A path that starts with `/` and is not a file is tried from that folder too,
  as a site writes a path from its root.
- **What opens.** The host reads the target again (`DesktopFileLink`), finds the file (`DesktopFileLinks`) and
  has the window show it through the requests of `projectFiles.watch`, as `alta editor open` does. A file
  inside the folder of a project opens in the code editor of that project, the innermost one. Any other file
  opens the code editor on a folder that is no project (see "Code editor"): the folder the session works in
  when the file is in it, a worktree for example, and the folder of the file otherwise, so that its
  neighbours can be looked at. A folder opens with its files shown.
- **What does not.** A file that is not text is not opened, and the window says so (`binary`): the first
  8 KiB hold a NUL and no byte order mark. A picture is opened, because the code editor shows it. A file that
  is not there is `not_found`.
- **Documents.** A `file:` address of an HTML page or a PDF is handed to the system, which shows it in the
  browser, as an address of the web is. No other kind of file is handed over: the system would start a
  program for it. The same page named by its path opens in the code editor, like any file.

The runtime context of every session tells the model how to write such a link (`SystemPromptBuilder`): a
Markdown link whose target is the path relative to the project root, with `#L<line>` for a place in the file.
Not every native gesture is suppressed. Raw source Copy, including CRLF and fences, remains independent of the
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

File cards keep available persisted diagnostic/output detail text behind their **Details** button. This
only shows the already loaded plain text: there is no request for missing output, additional history, or
live provider data when opening it. Persisted FileChange activities label it **File change record
details**, regardless of phase or command presence. This identifies the supplied record, not applied
changes, a complete diff or file-navigation authority. Tool calls have their own window (see "Tool calls").
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

## Brand icons

The logos of brands (model providers, models, coding agents, issue trackers) are not part
of the icon set of the app (`lucide-react`, through `AppIcon`), which draws outlines of things and has no
logo. They come from two packages that hold only SVG files and have no dependency, taken as development
dependencies: `@lobehub/icons-static-svg` (MIT, the AI brands, each as a one-color mark and often as a
colored drawing) and `simple-icons` (CC0, Jira, GitLab, Git and others it lacks). The React package
`@lobehub/icons` is not used: it depends on `antd` and `@lobehub/ui`. `npm run icons`
(`frontend/scripts/generate-brand-icons.mjs`) copies the drawings of the brands listed in the script into
`frontend/src/brandIcons.gen.ts`, which is committed: the page ships that text and nothing else of the two
packages. The script refuses a file that holds anything but a drawing.

- `BrandIcon` draws a logo: its colored drawing, else its one-color mark in the color of the brand
  (`--brand-tint`, and `--brand-tint-dark` on a dark background), else in the color of the text, which is
  how a black mark (OpenAI, GitHub, Anthropic) stays visible in both themes. The gradients of a drawing get
  names of their own for each logo drawn.
- `brands.ts` chooses the logo. For a provider: the `icon` of its definition, then a brand named by its
  key, its name or the host of its API URL, then its adapter type; `openai-chat`, the protocol of many,
  names none. For a model: the family its id names (`claude`, `gpt`, `gemini`...), else the logo of its
  provider. For an issue tracker: its service (`github`, `gitlab`, `azure_devops`, `bitbucket`, `jira`).
  For a plugin: its id. `icon` can also name one of the general icons of `symbolIcons.ts`, a list of
  `lucide-react` icons by their names in that set, for a provider of no brand.
- `ProviderIcon` and `ModelIcon` take the key of a provider and find the rest in
  `ProviderBrandsContext`, filled from `configuration.snapshot`: its `providerBrands` lists every
  definition of the file (key, type, name, icon, color), the disabled ones and the ones without credentials
  included, so that a session keeps the logo of a provider that cannot run now. A provider of no known
  brand keeps the icon the place had before.
- Where they show: the session rows of the sidebar (the logo replaces the icon of a session; a session
  started by another one or by an automation keeps that icon before it), the provider and the model of
  the prompt, the Providers page (list, form, **Add provider** menu, and the **Icon** and **Icon color**
  fields that write `icon` and `color`), the Models page, Session info, the usage window, the saved
  sessions, the automations and their Jira trigger, the work items, the plugin and MCP server lists, the
  source of the Issues tab and of the issue picker, and the Copilot tag. The provider and model lists of
  the prompt are native selects, which cannot hold a picture: the logo is beside them.

## Settings

### Appearance

**Settings → Appearance** manages the language, the theme (Dark, Light, or Auto, which follows the
operating system), a darker dark theme, the color scheme, project sorting, the recent-session count and
desktop project-rail collapse. The button before the window controls at the top right switches between the
three themes; the percentage before it is the zoom of the window. The rail's Sort projects selector and
Show/Hide projects button use the same live preferences; changes apply immediately and are
saved only to this WebView's local storage (theme, darker, colorScheme, projectSort, projectRail and projectTree v1 keys).
The user's own color schemes are files of the profile (see below).

**Width of the conversation.** The timeline and the prompt of a session take a percentage of its space, in
the middle (`--session-width`, read by `.session-workspace` as an inset of the timeline and of the composer;
a narrow pane keeps at least 520 px, or all of it). The user's setting is one value for every session, from 40
to 100, kept by the host in `preferences.json` (`sessionWidth`, written only when it is not 100): the host
keeps it because `alta appearance` reads it. It is changed by dragging an edge of the prompt
(`SessionWidthGrips`: both edges move, the pointer sets the width to the percent, a double click or Home
resets it, the arrow keys change it by 5), or with the slider of **Settings → Appearance**, which also resets
it. Blueprint's slider measures its track once, when it mounts, and a page of Settings mounts before its
dialog is shown: `SessionWidthSlider` makes the slider again whenever the room it has changes, so a press is
read on the track that is shown. `desktopShell.setSessionWidth` writes the setting and every page is told with a `session-width` notice. A session
can be shown with a width of its own, which `alta appearance set --session-width` gives it: the host holds
these in memory (`DesktopShell.SetSessionWidthOf`, 256 sessions at most), lists them in
`desktopShell.preferences` and tells the pages with a `session-width` notice that names the session. Such a
session has `--session-width` on its own workspace. Resizing it by its grips changes the user's setting and
releases the session.

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

### Spaces

**Settings → Spaces** (group Personalization, after Appearance; `/spaces`, or **Organize spaces…** in the
space switch) is where the spaces are organized (`frontend/src/spaces/SpaceSettings.tsx`, see "Spaces").

- On the left, the Default space with the list of every project, each with the icons of the other spaces
  it is in. Its name, icon, color and description can be changed like those of the others.
- On the right, one card for each space: icon, name, color swatches, "What it is for", its projects,
  **Projects** (a checklist of all the projects, to add or remove them with the keyboard), **Show**, move
  before and after, and remove, which asks to confirm. The `×` of a project removes it from that space.
  **New space** opens the window that creates one.
- Drag a project from the list onto a card to add it; from a card onto another card to move it, or to
  keep it in both with `Ctrl` or `Alt` held; from a card back to the list to remove it from that space.
- Every change is saved at once, through the `spaces` RPC service. Nothing is kept by the page.

### About

**Settings → About** is an inline page with the product, version, build metadata (only for a recognized
version `+metadata` suffix) and the mode of the running app (desktop app, catalog only, browser demo or
unavailable host). The **About** command opens the same facts as a window. The result of this
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
connection, and arbitrary provider error messages/URLs/categories are not shown. A failed test
whose cause is one of a fixed list (`reason` of the `probe` response: `claude-code-signed-out`,
`claude-code-not-found`, `claude-code-unavailable`) shows the page's own text for it, which says what to do. An abandoned
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
confirmation. When the configuration lacks some of the providers CodeAlta ships a definition for (the bundled
`DefaultConfig/config.toml`), **Add provider** first opens a menu of them, by name with their adapter type, and
**Custom provider…** for a blank definition; choosing one adds it as it is shipped (`globalConfig.addBuiltInProvider`:
its key, adapter type, endpoint, the variable of its key and what its service needs), disabled, and selects it so
that its credential can be given. A configuration that has them all goes straight to the blank definition. **Save and apply** writes the global `config.toml` and re-registers the providers in
the running host; it is refused as a conflict when the file changed on disk since the page read it.
Each registration of a provider has a version (`ModelProviderRegistry.GetRegistrationVersion`), which an attachment
records: an idle session whose provider was registered again since is attached again at its next send, as for a
change of model, and runs with the saved settings (`SessionRuntimeService`, `UsesCurrentProvider`). A running turn,
a draining queue, or an attachment another caller configured keeps the runtime it started with.
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
before signing in. A `claude-code` provider has no API field and no **Account** block; its form has
**ANTHROPIC_API_KEY** instead (`anthropic_api_key` of `GlobalConfigProvider`/`GlobalConfigProviderEdit`): follow the
answer Claude Code saved for the key (blank), ignore it and use the Claude login (`ignore`), or use it (`use`). See
"`ANTHROPIC_API_KEY`" in [providers.md](providers.md). Another adapter type does not keep the setting.

**The default provider.** `ModelProviderDescriptor.IsDefault` is true for every configured provider (it is
the default option of its own definition), so it never says which provider a new session starts with.
`DesktopDefaultProvider` does: `[chat] default_provider` (of the project, then of the user) when that
provider is enabled, otherwise the first enabled provider in the order of the registry (by name). The
`configuration` snapshot and `modelCatalog.providers` mark that one provider `isDefault`, and
`workspace.createSession` without a provider, `SessionStarter` (work items, issues) and
`AutomationRunner` take the same, so the composer of a new session, a work item and an automation agree.
`alta session create` and `alta model resolve` follow the same rule (in `CodeAlta.LiveTool`, for both
frontends) when the command names no provider and inherits none from a calling session.
`globalConfig.providers` reports both what the file says (`defaultProvider`, which the checkbox **Use as
the default provider for new sessions** shows) and the provider that results (`startingProvider`, which
carries the **Default** tag in the list). Saving a provider with the box cleared removes
`default_provider` when it named that provider.

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

### Files of the settings pages

Every page that is backed by files says where they are, in rows of one shared component
(`SettingsFileLocation`): the path on one line, cut at its start when it is too long with the whole path
as its tooltip, then three quiet buttons, **Edit in the code editor** (**Open in the code editor** for
what is only read), **Copy path**, and the action that shows it in the file manager, named after the
system. A file that is not there yet is shown in the muted colour without the buttons that need it; a
folder CodeAlta owns is created when it is opened. Opening leaves Settings, as **Edit in the code editor**
of a plugin does.

| Page | Rows above the list | Rows of the selected item |
| --- | --- | --- |
| Configuration file | `~/.alta/config.toml`; the `.alta/config.toml` of the selected project | |
| Providers, Worktrees, Work items | `~/.alta/config.toml`, which keeps their choices | |
| MCP Servers | `~/.alta/mcp.json`, the `.alta/mcp.json` of the project, and the files of other tools that exist | |
| Agent prompts | `~/.alta/prompts`, the `.alta/prompts` of the project, the agents folders of GitHub Copilot that exist, the shipped prompts (only read) | The file of the prompt |
| Skills | `~/.alta/skills`, the `.alta/skills` of the project, and the common and GitHub Copilot folders that exist | The folder of the skill and its `SKILL.md` |
| Plugins | `~/.alta/plugins`; the `.alta/plugins` of the project | The folder of each source plugin, in its row; a configuration file that does not parse, in its message |
| Appearance | The folder of the color schemes | |
| Pull requests | `~/.alta/prompts/pull-requests`; the one of the project | The file of the kind |

The `settingsFiles` RPC is behind the rows (`SettingsFilesService`, `useSettingsFiles`). `list` returns
the rows of a page (`config`, `mcp`, `prompts`, `skills`, `plugins`, `colorSchemes`, `pullRequests`), each
with its kind, scope (`Global`, `Project`, `BuiltIn`), id, path, whether it exists and whether it can be
opened, and the platform that names the file manager. `open` and `reveal` take the kind, the scope and the
id of a row, or name a prompt (`prompt`, with its kind as `part`) or a kind of pull request
(`pullRequest`) by its id. **The page never sends a path**: the host finds it again as the service of that
page does, so nothing that a page does not list can be opened through this RPC. `open` answers `ok`,
`not_found`, `invalid` (no page lists it) or `failed` (no window showed it).

Where `open` shows a file:

- a file inside the folder of a project of the catalog: in the code editor of that project;
- a folder: in a tab of its own (`folder:<key>`);
- a prompt or a kind of pull request of the user: in the tab of its folder of prompts;
- a file whose folder holds more than settings (`config.toml` and `mcp.json` of `~/.alta`,
  `~/.copilot/mcp-config.json`): in a tab that has that file alone (`folder:file:<key>`);
- a prompt that ships with the application: in a tab that is only read (`folder:view:<key>`).

The folder of a skill and the folder of a plugin keep their own ids (`skill:`, `plugin:`) and are shown
in the file manager through `projectFiles.reveal`. The global `config.toml` keeps its editor on the
Configuration file page; its row opens the same file in the code editor.

### Removing what a page creates

A page that creates something removes it too, with one red button (`RemoveButton` in
`SettingsPage.tsx`): a trash icon in a row, or **Remove** with its label at the foot of a form or in the
details, and a popover that names what goes (**Remove {name}?**) before anything happens.

| Page | What is removed | How |
| --- | --- | --- |
| Agent prompts | A global or project prompt | `agentPrompts.delete`: the file, when it still has the revision that was read |
| Skills | A skill of `UserAlta`, `ProjectAlta`, `UserCommon` or `ProjectCommon` | `skills.delete`: the folder of the skill goes to the trash of the system |
| Plugins | A source plugin, global or of a project | `plugins.delete`: stopped, then its folder goes to the trash, then its switch leaves the configuration |
| MCP Servers | A server of a file of CodeAlta | `mcpServers.remove` |
| Appearance | A color scheme of the user | `colorSchemes.delete` |
| Pull requests | A kind of the user or of a project | `pullRequestPrompts.delete` |

What ships with the application (a built-in prompt, skill or plugin), what a plugin brings and what
another tool owns (a skill or an agent of GitHub Copilot, a server of `.mcp.json`) has no such button.

`skills.delete` names a skill by its name and source, as `detail` does. It answers `read_only` for any
other source, and only moves a folder that is directly inside the folder its source reads and holds a
`SKILL.md`: a name is compared with the names of the skills and never made into a path. A code editor
tab that was open on the folder finds it gone, as for a plugin.

`plugins.delete` names a package by scope and id, as `reload` does. In order: the plugins of the package
are stopped in the running host (`PluginRuntimeManager.StopPackageAsync`, which does not look at the
configuration), the folder is moved to the trash (`IDesktopFileTrash`), and the `enabled` entry of
`[plugins.<id>]` is dropped from the configuration of the scope of the package
(`RemoveGlobalPluginEnabled`, `RemoveProjectPluginEnabled`), which keeps every other setting of that
table. The entry stays when a built-in plugin has the same id: it is the switch of that plugin. When the
folder cannot be moved (`trash_failed`, or `trash_unavailable` on a system without a trash) the plugin
is started again and nothing else changes. Nothing is ever deleted for good from these pages.

### Agent prompts

The **Agent prompts** Settings section (or `Ctrl+G`, then `Ctrl+H`) lists agent prompts and system
prompts from the built-in, global and (when an unarchived project is selected) project scopes, and
edits them: display name, description, system prompt, whether the text is added to the system prompt
or replaces it, and the prompt text in a Markdown editor. **New prompt** creates an agent or system
prompt in the global or project scope; **Customize a copy** on a built-in prompt creates a global
prompt with the same name, which then overrides it; **Remove** deletes a global or project prompt
file. Built-in prompts are read-only. A save is refused, without overwriting, when the file changed
on disk since it was read. The form shows the path of the file of the selected prompt
(`AgentPromptDocument.File`) with the buttons that open, copy and show it. Each row of the list
(`PromptRows`) has the same `</>` button as the row of a plugin: **Edit in the code editor** opens the
file of the prompt, in the code editor of its project or in the tab of its folder of prompts, and
**Open in the code editor** shows a built-in prompt read-only. The form stays the way to edit the values
of a prompt. A session's prompt for the next Send is chosen from the prompt bar.

The custom agents of GitHub Copilot (`.github/agents` of the selected project, `~/.copilot/agents`)
are listed after the agent prompts with their scope and the Copilot mark (`PromptTags`). They are
read-only like a built-in prompt, and **Customize a copy** creates a global prompt with the same
name, which is then the one that is used.

### MCP Servers

The **MCP Servers** Settings section lists the servers defined in the global and selected project
`mcp.json` and edits them: name, local command (command, arguments, working directory, environment
variables) or HTTP (URL, headers), where it is stored, and whether it is enabled. The switch in the
list enables or disables a server without opening it. Stored environment and header values are never
sent to the page; leaving a value blank keeps the stored one. Connection tests, sign-in and per-tool
switches are done in the TUI. Enabling or disabling rewrites `config.toml` without its comments.

The list also holds the servers of the files of other tools (`.mcp.json`, `.github/mcp.json`,
`.vscode/mcp.json`, `~/.copilot/mcp-config.json`; see `mcp.md`). Each card has the name and the
switch on its first row and its tags below: the scope, the file for a server of another tool (the
Copilot mark for the two files of GitHub Copilot, the name of the file for the others), and
**Overridden** for a definition that is not the one in effect. Such a server has no **Remove**:
its switch and its authorization work as for any server, and **Save** writes a server of the same
name to the file of CodeAlta of the chosen scope, which then comes first. The `mcpServers` RPC
names a definition by key, scope and origin (`CodeAlta`, `Common`, `Copilot`, `Vscode`); a request
without an origin names a file of CodeAlta. A server that is left out (it needs `${input:...}`,
an `envFile` or an unknown variable) is listed after the others as **Not supported** with the
reason, by its name only.

The MCP server of CodeAlta itself, which other applications connect to, has its own page (see "MCP
server").

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
A skill is a folder that can hold several files: the button of the details opens that folder in the code
editor, on its `SKILL.md` and with its files (see "The folder of a plugin or of a skill"). It is **Edit** for
a skill of the user or of a project, and **View files** for a built-in skill and for a skill that a plugin
brings, whose folder is only read. **New skill** opens the folder it created the same way. Each row of the
list (`SkillRows`) has the same `</>` button as the row of a plugin, which opens that folder without
selecting the skill first: `skills.list` returns the id of the folder of each skill (`folder`) and its
path. The folders the skills are read from are listed above the list (see "Files of the settings pages"):
`~/.alta/skills`, which holds the global skills, opens in the code editor as one folder, and is created
when it does not exist yet.
A configuration file that cannot be read or parsed (the `config.toml` of the user or of the project, which
holds the disabled skills) does not blank the page: `skills.list` answers `ok` with every skill, listed as if
that file disabled none (`SkillManagementService.LoadListingAsync`), and names the file in `problems` (kind
`config`, with its scope and what the parser said). The page shows it in red above the list with the row that
opens it in the code editor, as the Plugins page does. `skills.detail`, `skills.delete` and the folder of a
skill for the code editor do not depend on that file either. A change of enablement is still refused
(`config_invalid`): the file is not written over. Only this listing for display guesses nothing was disabled;
what decides the skills a model is offered (`LoadAsync`, the bounded readers of the configuration) still
refuses a file it cannot read.
The **Models** section's table fills the page height. The **Plugins** Settings
section has a switch per plugin, including the built-in MCP, Git, Statistics and UI tools plugins. The
switch of a source plugin applies at once: the plugin is built and started, or stopped. The switch of a
built-in plugin applies the next time CodeAlta starts, except for the Statistics rows described below,
which follow the switch from their next read on. With an unarchived project selected, both pages can
store a change globally or for that project. Every Settings section has its own icon in the sidebar.

The row of a source plugin (`PluginRows` in `PluginSettings.tsx`) also has:

- what the running host did with it: nothing for a plugin that runs, **Failed** with the reason,
  **Not supported in the desktop application**, or **Not started**;
- the errors of the compiler from its last build, each with its file, line and column, while the version
  that was built before keeps running;
- **Source changed**, when the `plugin.cs` on disk is not the one that runs;
- **Build and reload**, while the plugin is turned on and is one this host loads;
- **Edit in the code editor**, which opens the folder of the plugin in the code editor (see "Code editor");
- the path of its folder, with **Copy path** and the action that shows it in the file manager;
- the red **Remove** button, which asks for a confirmation (see "Removing what a page creates").

**New plugin** asks for an id and a description, writes a first `plugin.cs` and a `README.md` in the
global plugin folder or in the one of the selected project, starts the plugin and opens its folder in
the code editor. The page reads its list again when the host starts, replaces or stops a plugin.

The `plugins` RPC behind the page: `list` returns each plugin with `folder` (the id of its folder for
the code editor), `path`, `loadable` (this host loads it: a plugin of a project the application was
not started in is listed and edited, not loaded), `changed`, `errors`, `runtime` and
`runtimeMessage`; `setEnabled` answers `applied` when the running host followed; `reload` answers
`ok`, `build_failed` (with the first error), `start_failed`, `disabled`, `not_loaded`, `unknown` or
`unavailable`; `create` answers the folder id, the path and the name of the new plugin, or `exists`
and `invalid`; `delete` removes a source package and answers `ok`, `unknown`, `trash_unavailable` or
`trash_failed`.

`list` answers `ok` with every plugin it could read, and names what it could not read in `problems`
(`PluginProblems` shows them above the list, in red, each with its path and what the parser or the
system said):

| `kind` | What could not be read | What the list does |
| --- | --- | --- |
| `config` | The global `config.toml` or the `.alta/config.toml` of the project: it does not parse, or another program is writing it | The plugins are listed as if that file said nothing of them |
| `folder` | A plugin folder that cannot be listed | The packages of the other folder are listed |
| `name` | A package whose folder name is no plugin id | It is named with its folder and not listed |
| `runtime` | What the running host did with the packages | The plugins are listed without their running state |

`omitted` counts what is neither listed nor named: plugins past the limit of one listing, and an id of
configuration that is no plugin id. An exception the listing does not expect answers `read_failed`
and is written to the log (`CodeAlta.Desktop.Rpc`). A source plugin that has the id of a built-in one
(`git`, `mcp`...) has a row of its own after the built-in rows.

The plugin runtime has one plugin folder when the application is started in the folder that holds
the global root (the home folder, whose `.alta/plugins` is `~/.alta/plugins`): the packages are the
global ones, and none is built or listed twice (`PluginRuntimeManager.Roots`).

## Plugins

The normal launch (`alta`, `alta --dev`) starts the plugin runtime like the terminal UI, before the
window's page loads:

- The built-in plugins are the terminal's, under the same ids (`mcp`, `git`, `statistics`), so one
  `[plugins.<id>]` configuration applies to both, and one of the window's own, `ui`, which gives a
  session the tools that see and drive the window (see "UI tools"). They run as backends: the MCP plugin gives sessions
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
- A source plugin is built again and replaced while the application runs, from **Settings > Plugins**
  or by a session (`alta plugin reload`, see "Plugins written by a session" below). The host tells the
  page (`plugins-changed`, a notice of the shell): the page reads the commands, shortcuts and pickers of
  the plugins again, and what they show around the prompt. A command or a dialog action of a plugin that
  ends does the same for what the plugins show (`refresh`, an event of `pluginUi.watch`), so a status
  item follows the command that changed it.
- Every Send asks the plugins what they add to the run, exactly as `alta session send` does, so a
  session gets the same tools and instructions from the window and from another session. When that
  changes between two sends (a server was activated), the next Send replaces the session's provider
  attachment; the timeline shows **System prompt changed**.
- `CODEALTA_DISABLE_PLUGINS=1` starts without any plugin. The explicit-root launch never starts them.

Sessions of the desktop host have the same `alta` tool as in the terminal: notes, sessions and
sub-sessions, reminders, skills, projects, providers, models and prompts, and the commands of the
active plugins (`alta mcp`, `alta statistics`).

### Plugins written by a session

A session of the window writes a plugin and tries it without a restart. `DesktopPlugins.Workshop` gives
the `alta` tool an `AltaPluginWorkshop` over the plugin runtime of the host, which adds
`alta plugin create`, `build`, `reload`, `refresh` and `open` to `list`, `status` and `api` (see
`doc/live-tool.md`, Plugin commands):

- `create` writes the first `plugin.cs` and starts it, for the user (`~/.alta/plugins`) unless `--project`
  asks for a plugin of the project; `reload` builds the source again and replaces the
  running plugin. A build that fails returns the errors of the compiler with file, line and column, and
  leaves the running version in place.
- The agent tools of the plugin are registered in the turn that built it (`AgentRunTools.Set`), so the
  session calls them at once. A session that already had a tool of the plugin calls the new version: a
  plugin tool is bound when it is called.
- `open` shows the folder of the plugin to the user in the code editor.
- With the UI tools (see "UI tools") the session looks at what its plugin shows in the window: it runs
  the command, reads the dialog in a snapshot and clicks in it.

Building a plugin runs its code in the application, so these commands are not given to the sessions of a
host that reviews their commands (`--review-owned-command-permissions`), and not to the explicit-root
launch, which starts no plugin. The built-in `codealta-plugin-runtime` skill is what tells an agent how to do all this; a
session reads it with `alta skill activate codealta-plugin-runtime`.

`Services.State` of a plugin stores its data as JSON files: `~/.alta/plugin-data/<plugin key>/` for the
user, `<project>/.alta/plugin-data/<plugin key>/` for a project (see `doc/plugins.md`, Plugin data).

- `alta notes set` writes the session's notes; the **Notes** window at the top right of the session
  opens with them.
- `alta ask` puts its questions in the place of the prompt and, with a file to review, that file in the
  place of the timeline (see "Asks and plan review" below).
- `alta reminder create` creates reminders the **Reminders** view lists and delivers.
- `alta session create` creates a sub-session. It appears in the Explorer under its parent within ten
  seconds while a run is live, and at the latest when the run ends. A sub-session is an ordinary
  session: it opens in a tab and takes prompts from the window. Sub-sessions created before their
  journal and their provider record agreed on the creation time stay at the root of their project.

A session that started sessions shows how many beside its title (`SubAgentBadge`; the count is
`subAgents` of its row in `sessionHierarchy.ts`), in the color of sub-agents (`--agent-accent`), which is
also the color of the rows under it.

What agents send each other is shown as an **Agent message** card in that color (`data-delegated` on the
row), with only the text that was sent:

- What a sub-session reports to its parent, and what one session writes to another with
  `alta session message` or `request`, is recorded as a prompt with a routing envelope, which names the
  sending session and the kind of message (**Answer**, **Note**, **Request**).
- The prompt a session sends another with `alta session send` (a parent that gives work to a sub-agent)
  is sent as it is written. The host records the session that sent it with the user message
  (`AgentSendOptions.SourceSessionId`, `source_session_id` in the journal, `sourceSessionId` of the
  history entry), and the card says **Prompt**.

The card names the sending session by its title (`SessionReference`): a button that opens that session.
A session the window does not list is named by the start of its id. **Details** gives the full id.

## Files

### Changes

The counts beside the branch in the composer are a button: it opens the **Changes** tab of the project,
which is also what `alta diff show` does for an agent. There is one such tab per project. It is a tab
of the same strip as the sessions and the code editors (`view: "changes"` in `fileTabs`), so it closes,
reopens, cycles and is restored like an editor tab, and several can be open. It opens in a pane on the right of the
tab it was asked from, or in the pane that already holds a changes tab; from there it is dragged, split
and merged like any tab.

- A repository that has git worktrees lists its checkouts above the files, and the tab shows the one
  that is selected; the branch of the header opens the branches the checkout can move to (see
  [Worktrees](#worktrees)).
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
  `Alt+Up` / `Alt+Down` go through the changes, from the list of files as well as from the diff: inside a
  Changes tab (`data-change-keys`) the window leaves these two keys to the tab, where they select another
  session anywhere else (`changeKeyKept`). **Open file** opens the file in the code editor of the
  project when it is inside the project folder. The `…` menu has **Hide unchanged lines**, **Ignore whitespace
  changes**, **Wrap lines** and **Copy path**.
- A file that is new or deleted is shown whole, tinted green or red, instead of beside an empty side.
  A binary file, a file over 1 MB and a file that cannot be read say so instead of a diff.
- The diff side shows **One file at a time** (the default) or **All files in one view**: the two
  buttons before **Side by side** / **Inline** in its header, and Settings → Appearance → **Changes are
  shown**, set the same preference (`view` in `codealta.desktop.changes.v1`). The preferences are one
  set for the window (`useChangesPreferences`): a change in a tab or in the settings is sent to the
  others with a `codealta:changes-preferences` event.
- In a pane narrower than 760 pixels the files and the history sit above the diff.

**All files in one view** (`AllChanges`) puts the diffs of the listed files (the filter applies) one
under the other in one view that scrolls, each under a header that stays at the top while its diff is
crossed: the status, the name and the folder, the lines added and removed, and **Open file**. A click on
the header folds the file; **Collapse all files** / **Expand all files** is in the header of the view.

- Each diff is the same Monaco editor as for one file, as tall as what it shows, so the wheel moves the
  view and not the diff (`alwaysConsumeMouseWheel` is off); a diff that scrolls sideways still does.
  **Side by side** / **Inline** and the options of the `…` menu apply to every file.
- A diff is read and built when its section comes within one view of what is shown, four files at a
  time, and let go when it is farther: its section keeps the height it had. Before it is read, a
  section has the height of the changed lines the list counts (`estimatedDiffHeight`).
- A diff of more than 1000 lines (`changeDiffLimit`) is not shown whole, as an editor draws every line
  it is tall enough for: it takes the height of the view under its header and scrolls by itself.
- The file at the top of the view is the selected one of the list, and the header shows its place
  (**3 of 12**). Selecting a file in the list, the arrows of the header, `alta diff show --file` and
  `Alt+Up` / `Alt+Down` bring a file to the top; the view holds it there while the diffs
  around it take their size. A diff that takes its size above what is read moves the view by as much:
  the view does this itself (`overflow-anchor: none`), since WebKit has no scroll anchoring.

### Edits in the timeline

The tile of a tool call that edited files (`write_file`, `apply_patch`, `replace_in_file` and the like)
shows the lines it added and removed, and its window opens on the changes (see "Tool calls"): a row per
line with the line numbers of both sides and a rule per hunk, in the colors of the language of the file.
The host takes the counts from the `diff` of the call's record (`HistoryToolProjection`): the lines are
counted over the whole diff, and up to 16 Ki UTF-16 units of it are sent with the row, cut at the end of
a line; the window reads the whole diff. The files of a **Modified files** card open in the same rows.

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

**The folder of a plugin or of a skill.** The editor also opens on the folder of a source plugin, which is
no project of the catalog: **Edit in the code editor** in **Settings > Plugins**, the plugin a **New plugin**
just created, and `alta plugin open` for an agent. Its
tab is labeled **Plugin** and the id of the plugin, with the plugin icon. The tab names the folder by an
id the host gives and resolves itself, `plugin:global:<package>` or `plugin:project:<project id>:<package>`
(`PluginFolder`), where a project tab has the id of its project: every `projectFiles` call of the editor
works unchanged inside that folder, and nowhere else. A package name is one folder name, so an id cannot
name a path outside the plugin folder. Such a tab has no git status and no Changes, and it is kept across
restarts like the editors of projects; when the folder is gone its editor says so.

The folder of a skill opens the same way from **Settings > Skills**, in a tab labeled **Skill** and the
name of the skill, with the skill icon. Its id is `skill:global:<source>:<name>`, or
`skill:project:<project id>:<source>:<name>` for a skill that a project brings (`SkillFolder`): the source is
the one the Skills page shows (`UserAlta`, `UserCommon`, `ProjectAlta`, `ProjectCommon`, `Plugin`,
`Builtin`). The host finds the folder among the skills it discovers (`SkillFolders`), by that name and
source: a name is compared with the names of the skills and is never made into a path. The folder of a skill
of the user or of a project is edited like a project. The folder of a built-in skill, or of a skill of a
plugin, is only read: the host reports its files as read-only and answers a write, a creation, a rename and
a deletion with `read_only`, and the editor offers none of them.

A link to a file that no project has opens the editor on a folder of the disk the same way (see "Links to
files" under the Markdown boundary): the folder of the file, or the folder a session works in. Its tab is
labeled **Editor** and the name of the folder. Its id is `folder:<key>`, a key the host makes from the path
(`DiskFolders`): the host gives it when a link is followed and knows the folder while it runs, the last 128
of them, so an id never names a folder that the user did not open. The folder is edited like a project, has
no git status and no Changes, and its tab is not kept across restarts.

The pages of Settings open their files in such tabs too (see "Files of the settings pages"), in two more
forms that the id names:

| Id | What the tab reaches | Used for |
| --- | --- | --- |
| `folder:<key>` | The folder, edited like a project | A folder of skills, plugins, prompts, color schemes or pull request instructions; a file a link names |
| `folder:file:<key>` | One file of the folder, and nothing else of it | `~/.alta/config.toml`, `~/.alta/mcp.json`, `~/.copilot/mcp-config.json` |
| `folder:view:<key>` | The folder, only read | The prompts that ship with the application |

A tab of one file exists because the folder of that file holds more than settings: `~/.alta` has the stored
credentials (`auth/`) beside `config.toml`. `DiskFolders.GiveFile` remembers the one file of the id, and
`ProjectFilesService` answers every other path of the folder with `not_found`: `list` returns that file
alone, `read`, `write`, `stat`, `image` and `reveal` reach it alone, `search` finds nothing, and `create`,
`rename` and `delete` answer `read_only`, the file itself included. A folder that is only read answers a
write, a creation, a rename and a deletion with `read_only`, as the folder of a built-in skill does. The
editor offers no **New file**, **New folder**, rename or delete in either (`isReadOnlyTab`).

The project is the one of the code editor or the Changes tab in front, otherwise the selected project. The
editor needs an owned host and a project that is not archived. Editor tabs close, reopen, cycle, drag and split
like session tabs; the open editors are restored with the window (`codealta.desktop.fileTabs.v1`), and a
stored tab of one file, from before the editor had tabs of its own, becomes the editor of its project with
that file. While an editor is in front, the commands that act on a session are unavailable.

**Side.** Two views, **Files** and **Search** (`Ctrl+Shift+E`, `Ctrl+Shift+F`); `Ctrl+B` or the button at
the left of the file tabs hides and shows the side, and its splitter sets its width. In a pane narrower than
560 pixels the side and the file take turns.

- The header of **Files** has the name of the project and, under it, the full path of its folder on one line,
  cut at its start when the side is too narrow so that the end of the path stays in view (the whole path is
  its tooltip). The `…` menu of the header has **Copy path** for that folder.
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
the disk; its tooltip is the full path of the file. Tabs are dragged to another place of the strip, closed with the middle button or `Ctrl+W`, and
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
  **Deleted on disk**), the path of the file in the project (its tooltip is the full path, and a click copies
  the full path), the line and column (a click goes to a line), the indentation, the line endings, the
  encoding, the language, and **Save**.

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

**New files.** `Ctrl+N`, or the **+** button of the tab strip, opens a new file that is not on the disk
(`newUntitledDocument` in `editorDocuments.ts`). Its place among the open files is `untitled:<n>`, a path no
file of a project can have (the host refuses a colon), shown as **Untitled-n**. It is ready at once and empty;
it is not written to the saved list of open files, not compared with the disk and not shown in the tree, and
its text is lost when the window goes, which asks first as for any unsaved file. Saving it (`Ctrl+S`, the
**Save** button, **Save** in the question of a close or of an exit) asks for its path in the project
(`SaveAsDialog`), proposed from its name and the usual extension of its language (`languageExtension`). The
host creates the empty file with the folders above it (`projectFiles.create`), it is read for its revision,
and the text is written as an edit of it; the tab is then that file, with the same text model. A name that
exists, or is no name, is said in the dialog, which stays open.

**Language.** The language of a text is the one of the name of its file. `Ctrl+K` `M`, the language in the
status bar, or **Select the language** in the editor options, chooses another one for the document shown
(`LanguageDialog`: the languages the editor colors, filtered by what is typed; the first entry gives the
choice back to the name of the file). The choice lasts while the file is open and is dropped when a new file
is given a name. The first key of the chord is left to the text editor, which has chords of its own on
`Ctrl+K`.

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
- `watch` is the channel through which `alta editor open` and `alta plugin open` reach the page. Its
  `show` event has the project id, the file, the line and the column; for the folder of a plugin the id
  is the folder id, with the name of the plugin and its path, which the page needs to open a tab for it.

`composerStatus.read` returns the plugin status items of a composer for a project id (or none) and
a session id (or none): each has the plugin id, a name, a label, a text, a tone (`info`, `success`,
`warning`, `error`, `muted`) and the Settings page it opens, which is what a plugin's session status
contribution carries. The MCP item comes from the configuration and from what the running MCP plugin
knows of the session (the servers it activated and their tool counts); the read itself contacts no
server. Without a running MCP plugin (explicit roots, `CODEALTA_DISABLE_PLUGINS=1`) the item describes
the configuration alone and its tools read `tools not loaded`.

Every request of `projectGit` can name a `worktree`: the folder of the project in a git worktree of its
repository. It is then answered for that checkout, when the folder is one of the project's own repository
(`worktree_missing` otherwise; see [Worktrees](#worktrees)).

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
and close it. A session whose permission mode asks before commands does not type in a terminal, since
what is typed in a shell is a command nobody reviewed (see "Permission modes"); a host started with
`--review-owned-command-permissions` lets no session do it.

## Worktrees

A session of a project works in the folder of the project, or in a **git worktree** of it: a second
checkout of the same repository, in a folder of its own and on a branch of its own. Several sessions
then change the same project at the same time without stepping on each other, and the folder of the
project stays as it is. The sources are in `CodeAlta.Catalog/Worktrees/` (names, location, git),
`Desktop/Rpc/WorktreesRpc.cs` (host) and `frontend/src/worktrees/` (page).

- **A session stays one of its project.** The folder a session records as its working directory is the
  folder of its project, whatever checkout it works in: that folder is what ties a session to its
  project everywhere (the lists, the tabs, the scope of a send). The worktree is recorded beside it
  (`worktreeDirectory` in the summary of the session, `worktree_dir` in a session view), and is where
  the tools of the agent run, where its `alta` commands and its file references resolve paths, where its
  terminal opens, and what its composer reads the branch and the changes from. Prompts, skills, MCP
  servers and the configuration of the project are still those of the project.
- **Where a new session works.** The composer of a new session, for a project that is in a git
  repository, has a chip beside the folder: **Project folder** or **New worktree**. It opens the two
  places, what a worktree starts from (the commit the folder of the project is on, or a branch) and the
  folder it is created in. The choice is kept for each project in `codealta.desktop.workPlaces.v1`. A
  new worktree is created when the first message is sent, before the session:
  `git worktree add --no-track -b alta/<name> <folder> <commit>`. It starts from a commit, so what is
  not committed in the folder of the project is not in it. When git creates none (no repository, no
  commit, a branch that is gone) no session is created and the draft says why; where none can ever be
  made (no repository, no git) the choice goes back to the folder of the project. The chip is offered
  where the folder is in a repository, and stays visible as long as a worktree is chosen.
- **Names.** A worktree gets a name of two words and four random characters, such as
  `quiet-heron-7k2m`, `amber-denali-x4pq` or `brisk-zephyr-9bdt` (`WorktreeNames`: 275 adjectives and
  604 nouns, 166,100 pairs; the characters are digits and consonants, 531,441 endings). A name that is
  taken, as a folder or as a branch, is drawn again, and then numbered. Its branch is `alta/<name>`.
  The characters are for what the check cannot see: a worktree made in another clone or on another
  machine, whose branch of the same two words would otherwise be pushed under the same name.
- **One branch, one checkout.** A worktree is always created on a new branch, never on the branch of
  the project folder, and git refuses to move a checkout to a branch another checkout has: two
  sessions never work on `main`, or on any one branch, in two folders at once.
- **Where worktrees go.** Settings > **Worktrees** chooses it, in the configuration of the user:

  ```toml
  [worktrees]
  location = "custom"        # "global" (the default, not written), "project" or "custom"
  folder = "D:/worktrees"    # for "custom": an absolute folder; a leading `~` is the folder of the user
  ```

  `global` is `~/.alta/worktrees/<project>/<name>`, `project` is `<repository>/.alta/worktrees/<name>`
  and `custom` is `<folder>/<project>/<name>`. Inside a repository the folder holds a `.gitignore` of
  one line (`*`), written with the first worktree: git ignores the folder, and no tracked file is
  touched. The setting is for the worktrees to come: the ones that exist stay where they are. It is read
  from the file of the user only, never from the file of a project.
- **A project below the root of its repository.** A worktree is a checkout of the whole repository; the
  session works in the folder of its project inside it (`<worktree>/src/app` for a project `src/app`).
- **How it shows.** A worktree has a color of its own (turquoise) wherever it appears. The composer
  shows the name of the project, then the worktree with its folder, then its branch; a row of the
  session lists has a tree mark, with the name and the folder as its tooltip; Session info has a **Git
  worktree** row; the Changes tab names the worktree in its header. The agent is told the same: the
  runtime context of its instructions names the worktree as its working directory and as the root of the
  project, and says that the main checkout of the project is to be left as it is. The instruction files
  of the repository (`AGENTS.md` and the like) are read from the worktree; a file that only the main
  checkout has (one that git does not track) and the files above the repository apply as before.
- **A worktree that is gone.** A worktree can be removed at any time, from the Changes tab or from a
  terminal. The composer asks for the branch every two seconds: when the folder is gone the list of
  sessions is read again, and the session shows its worktree struck through, as **removed**. The next
  message is then answered in the folder of the project: the session no longer records the worktree,
  and is told that the folder it worked in is gone. What waits for the session is answered the same
  way, without a message of the user: the answer that a child session forwards to it, or a queued
  prompt, first attaches the session again in the folder of the project
  (`TryMarkNextQueuedPromptSubmittingAsync`). Whether the folder exists is looked at when a
  session is listed and each time it is about to work; it is never remembered. A run that would still
  start in a worktree that is gone is refused before any provider is asked, with an error that names
  the folder (`AgentSession.ExecuteRunAsync`), where a provider that starts a process would report
  that its executable could not be started.
  The `alta` tool of a session is made once for an attachment and can outlive the worktree: it knows
  the folder of the project too (`OwnedSessionToolRequest.ProjectDirectory`), and its commands start
  from that folder once the worktree is gone.
- **Changes tab.** A repository with more than one checkout lists them above the files, under
  **Worktrees**: the folder of the project first, then each worktree with its branch and the number of
  sessions that work in it. A row shows the changes and the commits of its checkout. The changes button
  and the worktree chip of a session open the tab on its worktree, and so does `alta diff show` called
  by a session that works in one. The trash button of a row removes the worktree
  (`git worktree remove`), after a confirmation that names its folder. A worktree with changes that are
  not committed asks a second time before it is removed with them. The branch `alta/<name>` goes with
  its worktree unless it holds commits that no other branch has: it is then kept, and the tab says so.
  The folder that held the worktrees of the project goes with the last one, and so does
  `~/.alta/worktrees` once it is empty; a folder the user chose stays. The folder of the project is
  never removed. In a pane narrower than 760 pixels the checkouts are above the files, beside the
  history.
- **Branches.** The branch in the composer and in the header of the Changes tab is a button: it lists
  the local branches, the ones with the newest commits first, then the branches of the remotes that
  have no local branch yet, and offers to create the branch that is typed. A branch another checkout is
  on is shown with that checkout and cannot be chosen: git gives a branch to one checkout at a time.
  What git refuses (changes that would be overwritten) is shown as git said it.
- **While a session works.** A checkout a session is at work in is neither removed nor moved to another
  branch: its row has no trash button, its branches are disabled, and the host answers `in_use`
  whatever the page asks. "At work" is a run in progress or a queued message being sent; an idle session
  does not keep its worktree.
- **Code editor.** The code editor of a project shows the folder of the project, not a worktree: for a
  worktree the Changes tab has no **Open file**, and `alta editor open` refuses a session that works in
  one (`editor.worktree`) instead of showing the project's copy of a file under the same name.
- **Agents.** `alta session create --worktree` creates a session in a new worktree, and a session that
  works in a worktree gives it to the sessions it creates (see `doc/live-tool.md`).
- **Host API.** `workspace.createSession` takes `worktree` and `baseBranch`, and answers
  `worktree_failed` with a `reason` (`not_repository`, `no_commit`, `invalid`, `git_unavailable`,
  `timeout`, `failed`) and the message of git. A session of `workspace.snapshot` has `worktreePath`,
  `worktreeRoot`, `worktreeName` and `worktreeMissing`. The `worktrees` service has `list`, `remove`,
  `branches`, `switch`, `settings` and `saveSettings`. The requests of `projectGit` take a `worktree`:
  only a folder of the project's own repository is read (two checkouts of one repository share their
  git folder), anything else is `worktree_missing`.

## Automations

An **automation** is a prompt that starts a session by itself: on a schedule, when something happens in the
repository of its project, when a command it keeps running succeeds, or when it is asked to. Each run creates
a new session, named after the automation (and after the issue or the pull request that started it, or the
last line its command wrote), in its project or as a chat, and
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
    { type = "jira", event = "created" },
    { type = "command", command = "gh run watch 123 --exit-status", cwd = "tools" },
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
  prompt, triggers, with the command and the folder of a command trigger): a change of any of them asks
  again, and its switch does not answer. An automation
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
- **Events of Jira.** `jira` (`created`, or `updated`) watches the Jira project of the project of the
  automation, through the plugin that knows it (`IIssueEventSource`, the Jira plugin). The trigger starts from
  the moment it is first looked at; each later look asks what was created, or updated, since the last one, a
  day back at most, and starts the automation once for each issue created and once for each change of an
  issue. Its authors are the people of the Jira project, so every event starts it. The prompt is followed by
  the service and the project, the key of the issue, its title, type, status, author and link. The card names
  the Jira project, or says why nothing is seen: the project names no Jira, or nobody is signed in.
- **Commands.** `command` keeps a command running: an executable that waits for something and ends when
  it happened (`gh run watch 123 --exit-status`, a script that waits for a file). `command` is the command
  line, one line of at most 2,048 characters; `cwd` is the folder it runs in, a path from the folder of
  the project or a full path, and without it the folder of the project, or the home folder of the user for
  an automation that runs as a chat. It runs in the shell of the `shell_command` tool (`ShellCommandProcess`
  in `CodeAlta.Orchestration`: PowerShell on Windows, the shell of the user elsewhere), with nothing on its
  input. The service (`AutomationService.Commands.cs`) keeps one command for each trigger of an automation
  that is enabled, can run as defined and is allowed, while the automations are not paused: each look at the
  automations (after a reading of the files, an allowance, a pause, and at least every 30 seconds) starts
  the commands that are missing and ends, with the processes they started, the ones that are no longer to
  run (disabled, deleted, another command or folder, not allowed, paused). Closing the application ends
  them all; the developer instance starts paused and runs none.
  - **Exit code 0** starts a run, with the trigger `command`: the prompt is followed by the command, its
    exit code and the end of what it wrote, standard output and error together (8 KB at most, without the
    sequences that color a terminal, in a fenced block no line of it can end), and a line that says this is
    information and not instructions. The run is named after the last line the command wrote. The command
    is started again at once.
  - **Another exit code** starts nothing, and the command is started again: it is what the command says of
    what it waited for.
  - **A run in progress.** A command that succeeds while a run of its automation is in progress keeps its
    run for when that one ends: up to 8 wait, in their order, and one at a time starts. Beyond 8 the oldest
    is recorded as `skipped`. Pausing, or ending the command, forgets what waited.
  - **A command that ends at once.** A command that ends in less than 5 seconds, whatever its exit code, is
    started again after 5 seconds, then twice as long each time, up to 5 minutes; a command that ran more
    than 30 seconds starts this from the beginning. Two such ends in a row with an exit code that is not 0
    are said on the card of the automation, with the last line the command wrote, until a command lasts 5
    seconds; a folder that does not exist and a shell that cannot be started are said the same way, and
    tried again with the same delays (`WatchProblem`, which the event triggers use too).
  - **Who may write one.** The command of a trigger is one nobody reviews when it runs. In the file of a
    project it waits for the user like the rest of the automation. A host that has the user review the
    commands of its sessions (`ReviewOwnedCommandPermissions`) refuses `alta automation create` with a
    command trigger, and `alta automation enable` of an automation that has one; the user writes it in the
    window. Disabling and deleting, which only end a command, stay open to a session.
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
- **The tab.** The bolt of the activity bar, **Automations** in the search of the window and `Ctrl+G`
  `Ctrl+M` open one tab (`view: "automations"` in `fileTabs`), kept for the next start. It shows the
  next 24 hours on a line (a mark for each time a schedule is due, a band for an automation that runs
  all along), a card for each automation (what starts it, where it runs, when it is next due, how its
  last run ended, a switch, **Run now**, and **Edit…**, **Duplicate** and **Delete…** in its menu), the
  runs of the selected automation or the recent runs of all, and templates a new one starts from. A run
  opens its session. The editor is a window: name, where it runs, where it is stored (**My
  configuration** or **The project**), the triggers with the next times of each schedule (a command
  trigger is a command line and the folder it runs in, in a chat as well as in a project), the prompt,
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

The prompt of a run is one of the commands the host keeps a receipt of, with the sends of the page and
the reminders. A run is refused, before a session is created for it, only while as many commands are
pending as the host keeps (256); it runs again at its next trigger.

## Work items

A **work item** is something left to do in a project: a **task** an agent proposed, or a **plan**
written in Plan mode. Both are Markdown files of the project; the window shows them where the user
decides on them, and lists them in one tab. The sources are `CodeAlta.Catalog/WorkItems/` (files,
settings, the store of the links), `Desktop/WorkItems/WorkItemRunner.cs` and
`Desktop/Rpc/WorkItemsRpc.cs` (host), `frontend/src/workItems/` (page), and
`BuiltInAltaCommandContributor.WorkItems.cs` (`alta task`, `alta plan`; see `doc/live-tool.md`).

- **Tasks.** A task is one specific piece of work beside what a session was asked: a gap, a problem
  or an improvement the agent found and verified. The **Default** agent prompt tells the agent when to
  propose one (before it ends its turn, never for what belongs to the request, never as a list of
  ideas) and to leave the decision to the user. A task is `<project>/.alta/tasks/yyyy-mm-dd-<slug>-<xxxx>.md` (four random characters end the name, so that two checkouts of a project never create the same file):

  ```markdown
  ---
  title: "Report why the model list is empty"
  kind: gap            # gap | problem | improvement
  status: pending      # pending | later | done | dismissed
  created: 2026-10-07
  summary: "The Models page shows nothing for a provider that failed. It should say why."
  ---

  ## Why
  ...
  ```

  A title holds 160 characters, a summary 600, a description 16,384. A session has at most five open
  proposals and a project 300 tasks; the command says so when it refuses.
- **Plans.** A plan is `<project>/.alta/plans/yyyy-mm-dd-<slug>.md`, with a front matter of `title`,
  `status` (`draft`, `approved`, `in-progress`, `done`, `blocked`), `created` and `summary`. A plan
  written without a front matter is read as before: its title is its first heading, and its status,
  date and summary come from its `- Status:`, `- Created:` and `- Task:` lines (a status that is a
  sentence is shown as written, beside the status it stands for). Changing the status of such a plan
  rewrites that one line. Only the first 16 KiB of a plan are read for the list.
- **What is not in the files.** Which session proposed an item, which one carries it out, and whether
  the user put its card away are facts of this machine: they are kept in `work_items.json` of the
  state folder, at most 2,000 of them, never in the repository. A link to a session that no longer
  exists counts for nothing.
- **What an item runs with.** The link also keeps the provider, the model and the reasoning effort of
  the session that proposed the item (`WorkItemLink.RunsWith`): `alta task create` and `alta plan
  status <id> approved` record those of the calling session, resolved as `alta session create` resolves
  them for a child. A provider key, like a session id, means nothing on another computer, which is why
  it is not in the file. A session started for the item is given, in this order
  (`SessionStartChoice.ChooseAsync`): what the user chose in the Work items tab (`providerId`,
  `modelId`, `reasoningEffort` of `workItems.act`), which is used as it is or refused with the reason;
  what the session that shows the card runs with; what is recorded with the item; then the default
  provider (see "The default provider"). The last three are passed over when their provider is no
  longer enabled, and keep their provider with its own model when their model is no longer offered.
  The Work items tab shows the result as **Runs with** (provider, model, effort), pre-filled the same
  way from the row (`runsWith`) and the defaults of the provider (`workItems/runsWith.ts`), and always
  sends what it shows.
- **Cards of a session.** A session shows, in its top right corner, what it proposed and the user has
  not decided on: its pending tasks, and its plans once they are approved (`alta plan status <id>
  approved`, which the Plan prompt runs after the review). One card is shown at a time, the plan
  first: what it is, its title, the kind of finding and the summary, **Details** (the whole text in a
  window, rendered), and with several cards a count with previous and next. The card can be folded
  into a chip. The choices are **Start in a new worktree**, **Start in a new session**, **Do it in
  this session**, **Later** and, for a task, **Dismiss**; the way of starting chosen in the settings
  is the first, primary button. **Later** on a plan only puts the card away.
- **Starting.** *A new worktree* and *a new session* are started by the host (`WorkItemRunner`, like
  an automation run): it creates the worktree from the head of the project when asked, creates a
  session named after the item with the provider, model and effort of the session that showed it,
  and sends it the prompt with the Default agent. *This session* goes through the composer of the
  session: the host returns the prompt and the page sends it, or queues it when the session is busy,
  so it runs after the current work; a plan switches the session to the Default agent for that
  prompt. If the session does not take the prompt, nobody is recorded as carrying the item out. The
  prompt of a task carries its description and ends with `alta task complete <id>`; the prompt of a
  plan names its file, or tells a session whose worktree does not have the file to read it with
  `alta plan show`.
- **The tab.** The checklist of the activity bar (with a dot while something waits), **Work items**
  in the search of the window, `Ctrl+G` then `Ctrl+I`, `/work_items`, and the mark of a project open
  one tab. Its toolbar filters by kind, by project and by text; **To do**, **In progress**, **Later**
  and **Closed** are tabs with their counts. The list is grouped by project, plans first; the item
  that is selected is read on the right, rendered, with its status, the session that carries it out
  (a link to it), the ways of starting it, **Mark done**, **Later** / **Put back**, **Dismiss**,
  **Open file** and **Remove** (which asks first). A plan whose file says `in-progress` is in
  progress even with no session recorded.
- **In the Explorer.** A project shows the number of its work items to do, with a pulsing dot while
  a session that is working carries one out; the session that carries an item out has a mark too.
- **Reading does not hold the window.** The projects and the sessions are shown without the work
  items. The page then asks for them a few projects at a time (3, then 6, 12, 24…), the selected
  project first and then the ones whose sessions were used last, and shows what it has after each
  answer. An empty list is said to be empty only once every project was read. The host tells the page
  when a file or a link changed (`workItems.watch`), and a turn that ends reads again.
- **Settings.** **Settings > Work items** edits the `[work_items]` table of the configuration of the
  user: whether agents propose tasks (`propose`; off, `alta task create` from a session is refused),
  whether sessions show the cards (`notify`), what happens to the file of a task that is completed or
  dismissed (`completed_tasks`, `dismissed_tasks`: `delete`, the default, or `keep`, which writes the
  status in the file and lists it under **Closed**), what happens to a plan once it is done
  (`completed_plans`: `keep`, the default, or `delete`), and the way of starting that comes first
  (`start`: `worktree`, `session` or `here`). A value that is the default is not written. A plan that
  is deleted when done is the copy the session works on: in a worktree, the copy of the project goes
  when the branch is merged, and until then the plan is listed without a card.
- **Limits.** A reading names at most 32 projects and returns at most 100 tasks and 100 plans for
  each; the text of an item is cut at 200 KiB. CodeAlta TUI has the two commands and no cards or tab.

## Issues and pull requests

The **Issues** tab shows the issues and the pull requests of a project, from where they are kept: the service
that hosts its repository, and the trackers plugins add. There is one tab (`view: "issues"`), opened by the
issue icon of the activity bar, **Issues** in the search of the window, `Ctrl+G` then `Ctrl+B`, or `/issues`.
The sources are `CodeAlta.Plugins.Abstractions/PluginIssueTracking.cs` (the model), `CodeAlta.Plugin.Git/GitHostTracker.cs`
(the hosting services), `Desktop/Rpc/IssuesRpc.cs` (host) and `frontend/src/issues/` (page).

- **No project.** The tab shows **No projects in this snapshot.** instead of waiting for tracker sources.
  Sources are used only when both their state and the selected project exist and their project IDs match.
- **Jira** is a tracker of the projects that name one (`[plugins.jira]`, see `doc/plugins.md`): its issues have keys
  (`ALTA-12`), a type, a priority and the status of their workflow, shown beside the state. Its rows have no date,
  which the search of the Atlassian CLI does not give. When the CLI is being downloaded or nobody is signed in, the
  list says so in place of the items.
- **Trackers.** A tracker is an `IIssueTracker`: a service name (`github`, `gitlab`, `azure_devops`, `bitbucket`,
  `jira`), what it is of (`owner/repository`, or the key of a project), the kinds of items it has, a listing and
  the reading of one item. A plugin that knows trackers implements `IIssueTrackerSource`
  (`GetTrackersAsync(projectPath)`); the host asks every active plugin that implements it, and a plugin that
  fails hides no other. The Git plugin gives the tracker of the hosted repository of the folder; the Jira plugin
  gives the Jira project a project names in its configuration. With several trackers the tab shows a choice
  beside the project; one service appears once.
- **The hosting services** are read through their REST APIs with the credentials of the user (see
  `doc/plugins.md`). GitHub: the issues and pulls listings, and the search for words and for merged or unmerged
  pull requests. GitLab: issues and merge requests of the project, with `search`. Azure DevOps: the work items of
  the project by WIQL, and the pull requests of the repository (no search there: the titles of the first hundred
  are matched). Bitbucket Cloud: issues and pull requests with its query language; a repository without an issue
  tracker says so. A refusal is a sentence the tab shows, with how to sign in when signing in is what is missing
  (a private repository answers "not found" to a visitor).
- **Lists.** The toolbar has the project, the tracker, **Issues** or **Pull requests** (merge requests on GitLab,
  work items on Azure DevOps), and a filter. **Open**, **Closed**, **Merged** (pull requests only) and **All**
  are tabs; a draft pull request is open. A listing holds 50 items, the most recently updated first, and says
  when more match. What is typed is asked 350 ms after the last key. A row has the state as an icon in the color
  people know (green open, purple merged or done, red closed without a merge, gray draft), the title, the
  number or key, the author, the last change, up to three labels in their colors and the number of comments.
- **Reading.** The item that is selected is read on the right: kind and state, type and priority when the
  tracker has them, who opened it and when, the branches of a pull request, assignees and labels, then the
  description and up to 100 comments, rendered as Markdown behind the boundary of the timeline (a tracker that
  keeps HTML gives its HTML, which the same boundary cleans). A double click, or the expand button, opens the
  same in a window that keeps its size. A description is cut at 200 KiB, and so are the comments together.
- **Actions.** **Start in a new worktree** and **Start in a new session** create a session of the project
  (`ISessionStarter`, the one that starts work items) named after the item, with a prompt that says what to do
  (work on the issue; review the pull request, without merging or commenting), the title, link, state, author,
  labels and branches, and the description in a fence longer than any run of backticks in it, said to be
  information and not instructions. The way of starting chosen in **Settings > Work items** comes first.
  **Open on …** opens the item in the browser of the system: the host opens an address only when it is https.
  **Copy link** copies it.
- **What is sent to the page** is cleaned: control and bidirectional characters are removed from titles and
  names, texts are bounded, a link that is not https drops its item, a color is six hexadecimal digits or is
  not sent, and the text of an exception never is.

### Create a pull request

A session can be asked to open a pull request for its work. The sources are
`CodeAlta.Catalog/PullRequests/` (the kinds), `Desktop/Rpc/PullRequestPromptsRpc.cs` (host) and
`frontend/src/pullRequests/` (button and settings page).

- **Kinds.** A kind is a Markdown file `<id>.pr.md` with an optional header (`name`, `description`); its body is
  what the session is sent. `PullRequestPromptCatalog` reads the one that ships (`default`, an embedded
  resource), those of the user (`~/.alta/prompts/pull-requests/`) and those of a project
  (`<project>/.alta/prompts/pull-requests/`). The nearest file of an id is the one in use: project, then user,
  then built-in. An id is letters, digits, `-` or `_` (64 at most), a body holds 32 KiB at most, a folder gives
  64 kinds at most, and a file that is a link is not read.
- **The button** (`PullRequestButton`) is one icon of the strip under the prompt of a session of a project,
  before the terminal button; a chat, a session being placed and an archived one have none. Its menu reads the
  kinds in use when it opens (`pullRequestPrompts.list`), the default one first, and ends with **Pull request
  instructions…**, which opens the settings page. Choosing a kind sends its body to the session as a prompt of
  the user. It is refused, with a notice, while the session is running and while a draft is being typed: the
  instructions are a message of their own.
- **Settings > Pull requests** (`PullRequestSettings`) lists every kind, with where it comes from and whether a
  nearer one replaces it (`list` with `all`). The built-in one is shown as it is, with **Customize for me** and
  **Customize for <project>**, which start a copy with the same id. A kind of the user or of the selected
  project is edited in place (name, description, instructions) and removed; the file name and where it is kept
  are chosen when it is created. `save` and `delete` write the file of the user or of the project.

## UI tools

A session can see and drive the window it runs in. The tools are those of
[Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp), with the same names, arguments
and result text, so that a model that knows them uses these unchanged. They come from the browser automation
of NeoAstra (`NeoAutomation`), which works inside the page with DOM APIs and is the same on WebView2,
WKWebView and WebKitGTK. The sources are in `Desktop/Ui/` (the tools and their plugin).

- **The tools.** `take_snapshot` (the elements of the page as text, each with a `uid`), `take_screenshot`,
  `click`, `click_at`, `hover`, `drag`, `fill`, `fill_form`, `type_text`, `press_key`, `upload_file`,
  `handle_dialog`, `wait_for`, `evaluate_script`, `list_console_messages`, `get_console_message`,
  `list_network_requests`, `get_network_request`, `list_pages`, `select_page`, `navigate_page` and
  `resize_page`. `new_page` and `close_page` are left out: the pages are the windows of the application,
  not those of a browser. The view only loads the application's own document, so `navigate_page` is of use
  with the type `reload`.
- **They are tools, not `alta` commands.** A result holds a text and images, which an `alta` command cannot
  return.
- **On request.** The tools are two dozen, so a session does not carry them until it asks for them.
  `alta ui activate` registers them in the running turn (`toolsAvailable: now`); the later runs of that
  session start with them, until `alta ui deactivate` or the end of the application. `alta ui status` says
  whether the session has them. A line of the developer instructions of every session says so too, and how to
  get them ("UI tools: inactive. `alta ui activate` gives this session…"): this is how an agent that is asked
  to look at the window finds them. A caller that is no session cannot turn them on (`ui.noSession`).
- **A built-in plugin.** The tools, the `alta ui` commands and the line of instructions come from the plugin
  `ui` ("UI tools"), which only the window has. A plugin reaches every session the same way: the ones the
  window sends to, and the ones another session creates with `alta session`. `[plugins.ui]` with
  `enabled = false` turns it off, and so does everything that turns plugins off. A host that has the user
  review the commands of its sessions (`--review-owned-command-permissions`) does not have the plugin: a
  session that drives the window could answer the review itself.
- **Not for a session that asks.** For the same reason a session whose permission mode has the user review
  its commands (any policy but `Approve`) does not have the tools, whatever it activated:
  `alta ui activate` answers `ui.activateDenied`, its runs start without the tools, a tool it still holds
  answers with an error, and its instructions say "UI tools: unavailable". What it activated is kept: it has
  the tools again on the first run after its mode stops asking. The plugin asks `DesktopUiSessions.Reviewed`,
  which the application sets over `SessionRuntimeService.GetPermissionPolicy`.
- **Files.** `take_screenshot` and `take_snapshot` take a `filePath` to save their result instead of
  returning it. A relative path starts from the folder the session works in (its git worktree, or the folder
  of its project), and the file has to be in that folder or in the folder of the tools (`ui` in the
  application data directory). The automation only writes into the folder of the tools: the file is saved
  there, then moved. The files the other tools read (`upload_file`, the `sourcePath` of `evaluate_script`)
  are in the folder of the tools.
- **Images.** A screenshot goes to the model with the text of the result, as any image a tool returns: the
  timeline shows it in an **Image read** card (see "Images a tool gives the model").
- **Editors.** The prompt and the code editors are Monaco editors, which take their text from the browser's
  own text input (EditContext) where the engine has one. The automation writes there as the engine does.
  `type_text` and `press_key` go through the editor as typing does, key by key: the editor closes the bracket
  or the quote that is opened and indents a new line. `fill` replaces the whole text of an editor in one
  change and leaves it as given, with the line endings of the editor: it is the tool for a text of several
  lines or for code.
- **A window in the background.** The tools work on a window that is not in front. The browser announces no
  change of focus there, so the automation tells the page which element has it before it sends keys, and the
  editors keep the text focus with one of them (see "Commands, help and keyboard shortcuts").
- **Window size.** `resize_page` gives the page the size it is asked for, in CSS pixels, also on a display
  that scales. A window that cannot take the size, because its screen ends or because it has a least size,
  leaves the page as near to it as it gets, and the result says which size the page has.
- **Limits.** Those of the automation: input is dispatched as DOM events (`isTrusted` is false), so CSS
  `:hover` does not apply and a native popup does not open; the caption buttons of the title bar are not part
  of the page; a screenshot needs a visible window on Windows. Sessions run at the same time against one
  window: two sessions that drive it get in each other's way.

## MCP server

CodeAlta Desktop is an MCP server: another application sees and drives the window, and runs `alta`
commands, through it. It is what drives the developer instance when CodeAlta is developed (see `AGENTS.md`).
The sources are in `Desktop/Mcp/`, `Desktop/Rpc/McpHostRpc.cs` and `frontend/src/mcpHost/`.

- **Transport.** Streamable HTTP, with the official C# SDK (`ModelContextProtocol.AspNetCore`) on a Kestrel
  server of its own: nothing is read from the folder the application was started in (no settings file, no
  environment variable), and the server listens neither to the console nor to the signals of the process.
  Requests share no state: a client has nothing to set up again after the application restarts. The
  package brings the ASP.NET Core shared framework, which the .NET SDK a tool is installed with has.
- **Address.** `http://127.0.0.1:2582/mcp`. The developer instance (`--dev`) has port 2583, so that both run
  side by side; an instance on explicit roots takes a free port, as several of them run at once. When the
  port of the application's own choice is taken, it takes a free one. `--mcp-port <port>` (0 for a free
  port) and `--mcp-host <address>` (`localhost` or an IP address), with any start that opens the window, say
  where the server listens; a port named this way is not replaced when it is taken: the server then does not
  listen, and Settings says why. The address of the running server is in `mcp_url.txt` beside the lock of the
  instance (`~/.alta/mcp_url.txt`, `~/.alta/dev/mcp_url.txt`); the file is removed when the server stops.
- **Tools.** The UI tools, under the names above, and `alta`: the tool the sessions have (same description
  and arguments), for a caller of the kind `mcp` that belongs to no session. A relative path starts from the
  folder the application was started in. The tools an agent works on a project with (files, shell) are not
  offered: a client is an application that has its own. A client saves a file (`filePath`) in the folder of a
  project of the catalog or in the folder of the tools. The server listens before the host runs; `alta`
  joins the tools once it does. A client creates sessions and sends to them without the window asking: after
  each of its `alta` commands the page is told (`sessions-changed`, a notice of the shell) and reads its
  sessions again.
- **Who is answered.** A client has the control of the application that its user has. On the loopback address
  the server answers the programs of this computer, and no page of a browser: a request whose `Host` is not
  the loopback address or `localhost` (a name an attacker resolves to this computer), and a request with the
  `Origin` of a page that is not served from this computer, are refused (403). A server other computers can
  reach (`--mcp-host` with another address) asks every request for an access token
  (`Authorization: Bearer <token>`, else 401); the token is created once and kept in `mcp_token.txt` beside
  the address.
- **Settings > CodeAlta MCP server** has the switch (**Run the MCP server**), the address, the configuration
  to paste into a client (`mcpServers` with `"type": "http"` and the address, and the access token as a
  header when there is one; the server is named `codealta`, or `codealta-dev` in the developer instance) and
  the tools. The switch starts and stops the server at once. It is kept in `preferences.json` of the
  application data directory (`"mcpServer": false`; the default, on, is not written), so the developer
  instance has its own. A server that is on and cannot listen shows why.
- **Host API.** `mcpHost.status` and `mcpHost.setEnabled` answer `state` (`running`, `stopped`, `failed`),
  `enabled`, `url`, `token`, `error` and `tools`.

## Commands, help and keyboard shortcuts

The desktop app uses the TUI's key map. `Ctrl+P` (or the search icon on the activity rail) opens the
**search** of the window, which also runs every command: see "Search" below. `F1` (or `?` in an empty
prompt) opens **Commands and shortcuts**, a filterable window listing the commands by category.

### Search

One window looks through everything (`frontend/src/search/GlobalSearch.tsx`, with the ranking in
`searchResults.ts`). It takes the place of the command palette and of the two filters the Explorer had.

- **What it finds.** In the space the window shows (see "Spaces"): the **sessions** of every project and the chats, the **projects**, the **files** of
  the project in front (the one of the code editor or the Changes tab shown, otherwise the selected
  project), and the **commands**, those of plugins included. Enter opens what is selected: a session in
  its tab, a project with its new-session tab and its sessions shown in the Explorer, a file in the code
  editor, and a command runs. What was chosen is opened once the window of the search has closed, with
  the keyboard in the prompt of a session or of a project.
- **Categories.** **All** shows the first results of each group, the group with the best match first,
  and **Show all** goes to its category. `Tab` and `Shift+Tab` go through **All**, **Sessions**,
  **Projects**, **Files** and **Commands**, with the number each one found. A text that starts with `/`
  looks for a command, as `/` typed in an empty prompt does.
- **Nothing typed.** The sessions that were updated last, the projects (the favorite ones first) and the
  commands that start something (new session, open, new terminal, project editor, settings, help).
- **Ranking.** Every word has to be found. A word counts most as the whole text, then at its start, then
  at the start of one of its words, then anywhere; a session is found by its title, then by the name of
  its project, then by its id, and the one updated last comes first among matches as good; a project by
  its name, then by its folder, then by the rest of its path, a favorite first and an archived one last.
  The files are searched and ranked by the host (`sessions.searchReferences`, the search of the `@`
  picker), a moment after the last key. What a word found is marked in the names.
- **The sessions of one project.** **Search sessions…** in the menu of a project or of **Chats** opens
  the search on its sessions only, with the name of the project before the field; Backspace in the empty
  field, or the button of that name, searches every project again. `Ctrl+F` outside a text
  (**Search Sessions**) opens the search on the sessions of every project.
- **Limits.** The projects and the sessions are those of the workspace snapshot (up to 200 projects and
  500 sessions); when the host shortened it, the window says so under the results. A session is found by
  what its row shows, not by the text of its conversation.

The window is one like Settings: drag its title bar to move it and its edges to resize it.

| Keys | Command |
|---|---|
| `Ctrl+P`, `F1` | Search (sessions, projects, files, commands), help |
| `Ctrl+Q` | Exit (`/exit`); works from any window |
| `Ctrl+=` (or `Ctrl+Plus`), `Ctrl+-`, `Ctrl+0` | Zoom in, zoom out, reset the zoom; kept for the next start |
| `Ctrl+O` | Open project |
| `Ctrl+E`, `Ctrl+E` `Ctrl+E` | Open a project file in the code editor (`/edit`), open the code editor with the files of the project (`/editor`) |
| In a code editor: `Ctrl+S`, `Ctrl+Shift+S`, `Ctrl+W`, `Ctrl+Tab` | Save the file, save every file, close the file shown, next file |
| In a code editor: `Ctrl+N`, `Ctrl+K` `M` | Start a new file, select the language of the text |
| In a code editor: `Ctrl+B`, `Ctrl+Shift+E`, `Ctrl+Shift+F` | Show or hide the side, go to the files, search in files |
| In the text of a code editor: `Ctrl+G`, `Ctrl+F`, `Ctrl+H`, `F3`, `Alt+Z` | Go to line, find, replace, next match, wrap lines |
| In the files of a code editor: `F2`, `Delete`, `Enter`, `Space` | Rename, delete, open, preview |
| On a session of the Explorer: `Delete` | Delete the session, after its question unless it is not asked any more |
| `Alt+Up`, `Alt+Down` in a Changes tab (its files or a diff) | Go to the previous or next change of the shown file, or to the previous or next file when all files are in one view |
| ``Ctrl+` ``, `Ctrl+G` then `Ctrl+J` | New terminal (`/terminal`) in the folder of the session or of the project |
| In a terminal: `Ctrl+C`, `Ctrl+V`, `Ctrl+F`, `Ctrl+Home` / `Ctrl+End` | Copy the selection (or interrupt the program), paste, find, top / bottom |
| `Ctrl+G` then `Ctrl+M` | Automations (`/automations`) |
| `Ctrl+G` then `Ctrl+I` | Work items (`/work_items`) |
| `Ctrl+G` then `Ctrl+B` | Issues and pull requests (`/issues`) |
| `Ctrl+G` then `Ctrl+V`, `Ctrl+G` then `1`…`9` | Go to Space (`/space`, opens the space switch), show the space at that place of the list (Default is 1) |
| `Ctrl+Alt+PageUp` / `Ctrl+Alt+PageDown` (also from a terminal) | Previous / next space (`/space_prev`, `/space_next`) |
| No key | New Space (`/new_space`), Spaces (`/spaces`, opens Settings → Spaces) |
| `Ctrl+Alt+Left` / `Ctrl+Alt+Right` (also `Ctrl+PageUp` / `Ctrl+PageDown`) | Previous / next tab |
| `Ctrl+W` (also `Ctrl+Shift+W`, which a terminal leaves to the application), `Ctrl+Shift+T` | Close tab, reopen the last closed tab |
| `Enter`, `Ctrl+Enter`, `Shift+Enter` | Send (queued while a turn runs), steer the running turn, new line |
| `Alt+Up` / `Alt+Down` in the prompt | Previous / next prompt sent from this window |
| `F6`, `Ctrl+T` | Full prompt editor, next agent prompt |
| `F8`, `F10`, `Ctrl+F11` | Abort the running turn, clear the queue, compact |
| `F3` / `F4`, `Ctrl+F3` / `Ctrl+F4` | Previous / next message, first / latest message |
| `Ctrl+Alt+B`, `Ctrl+F` | Browse sessions, search the sessions of every project |
| `Alt+Up` / `Alt+Down`, `Alt+Left` / `Alt+Right` outside text | Previous / next session (not in a Changes tab, which keeps these two keys), previous / next project |
| `Ctrl+Shift+N` | Show or hide the session notes |
| `Ctrl+G` then `Ctrl+P` / `Ctrl+S` / `Ctrl+G` | Go to prompt, go to sidebar, toggle the navigator |
| `Ctrl+G` then `Ctrl+T` / `Ctrl+U` / `Ctrl+D` | Session info, context usage, reminders |
| `Ctrl+G` then `Ctrl+W` / `Ctrl+R` / `Ctrl+O` / `Ctrl+H` | Settings, providers, models, agent prompts |
| `Ctrl+G` then `Ctrl+K` / `Ctrl+N` / `Ctrl+Y` / `Ctrl+L` / `Ctrl+A` | Skills, plugins, MCP servers, logs, about |

Switching to a session tab, by clicking it or with the tab keys, and creating, closing or reopening one
puts the keyboard focus in that session's prompt, so typing can start at once. A code editor takes it
in its text, or in its files when it shows no text. The window's own icon (Alt+Tab, task switcher) is `alta.ico`, shipped next to the executable.

One editor of the window has the text focus. An editor writes what is typed, and runs the keys, in the
editor that says it has the text focus, and it learns that it lost it from an event that the browser does
not send while the window is in the background: the prompt of a tab that opened there would go on taking
the text typed later in a file. When an editor takes the text focus, the others that still claim it are
told that they lost it (`frontend/src/monaco/editorFocus.ts`), on every engine.

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
settlement is not rollback, decision retraction or run termination.

The host keeps the receipts of 256 commands: those of the commands that are pending, and the most
recent of those that have settled. When it is full, the oldest settled receipt makes room for the
next command, so the host accepts commands for as long as the application runs; it answers `capacity`
only while 256 commands are pending at once. A retry with the key of a receipt that made room is
answered `expired` and is not run a second time: the page tells the user that the prompt was already
sent, and keeps no intent for it. `sessions.receipts` pages what the host still keeps, 64 at a time:
the pending commands first, which are the ones that can still be aborted or cancelled, then the
settled ones, the most recent first.

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

#### Permission modes

What the host does with the permission requests of a session follows from one mode
(`SessionPermissionModes`, `SessionPermissionPolicy` in the orchestration):

| Mode | Policy | The host |
| --- | --- | --- |
| `bypassPermissions` | `Approve` | answers every request with Allow once |
| `acceptEdits` | `AcceptEdits` | approves file changes and has commands reviewed |
| any other (`default`, and the `auto`, `dontAsk`, `plan` of Claude Code) | `Review` | has every request it receives reviewed |

The mode of a session is, in this order: the one chosen for it (the permission button of the composer,
saved with the session), the one its provider is configured with (`permission_mode` of a Claude Code
provider), and the default of the application. **Settings > Permissions > Default mode** sets that
default: **Bypass permissions** unless the user chose **Ask first**. It is kept in `preferences.json`
(`reviewPermissions`, written only as `true`) and read by `SessionRuntimeService.GetPermissionPolicy`.
Every provider has the three modes of the host, because the host answers the requests of its sessions:
the providers that run the tools of CodeAlta are offered `default`, `acceptEdits` and
`bypassPermissions`, and Claude Code its own list, which has them. For Claude Code the mode is also
given to the CLI, which decides what it asks at all.

One send reads the policy once, after its session is prepared (the mode a send chooses is saved by
then), so its setup and its cleanup agree: a change applies to what the sessions do next rather than to
what is already running. A send whose policy is `Approve` has no permission execution, and its requests
reach the default handler of the host, which answers with the policy of their session. That handler
also answers the requests outside a send of the window (a prompt the host queued, a session another
session drives with `alta session send`, a turn the provider starts): it approves what the policy
approves, and has the rest reviewed like a send of the window does. It opens a permission execution for
that one request, bound to the live attachment of the session
(`SessionPermissionService.SessionReviewTarget`), and closes it with the answer, so the request is listed
by `sessionPermissions`, shown on the card of its session, and counted among the sessions that wait. It
waits as long as the user does not answer, and ends as cancelled with the run. A session that has no live
attachment is denied. The handler reads the policy at each request, so a change of the default mode
reaches a run that has no permission execution at its next request. The window can always answer: the boot status reports the review available
(`commandReviewEnabled`) for every owned host, and `sessionPermissions` lists and resolves requests
whatever the modes are now.

`--review-owned-command-permissions` forces the review on for every session: the host then ignores the
modes of the sessions (`CodeAltaHostOptions.SessionPermissionModes` is off) and offers none but those
of Claude Code.

What a session does without a request must not go round its mode. The `alta` commands that run a command
nobody reviews are refused for a caller whose policy is not `Approve` (`AltaCommandReviewPolicy`, which the
host registers with `AcceptsCommandsOf` over `GetPermissionPolicy`): `alta job start` (`job.startDenied`),
typing in a terminal with `alta terminal send` or `create --command` (`terminal.inputDenied`), creating or
enabling an automation that has a command trigger (`automation.commandDenied`), and creating, building,
reloading or refreshing a plugin, which runs its code (`plugin.buildDenied`). The session that calls is
the one that counts; a caller that is no session (a client of the MCP server) has the default mode of the
application. A host started with the flag refuses them all. The UI tools are withheld the same way (see
"UI tools").

A session that another session creates with `alta session create` is given its mode by the host
(`SessionRuntimeService.GetCreatedSessionPermissionMode`, passed as `SessionExecutionOptions.PermissionMode`
and saved with the session like a mode chosen in the composer). **Settings > Permissions > Sessions created
by agents** decides, kept in `preferences.json` (`inheritPermissions`, written only as `true`) and read at
each creation through `CodeAltaHostOptions.InheritPermissionModePolicy`:

| Setting | A created session | A prompt to another session |
| --- | --- | --- |
| **Bypass permissions** (the default) | does not ask: it is given `bypassPermissions` where the default mode asks, and no mode where the default mode bypasses or its provider is configured with a mode | any session |
| **Same as the session that creates them** | has the mode of the policy of its creator: `default`, `acceptEdits`, or `bypassPermissions` (no mode where it bypasses anyway) | refused (`session.promptDenied`) for a session that asks less than the caller |

The default lets a session that asks hand a command to a session that does not: this is the user's choice,
since a sub-agent that waits for an answer stops the work of its creator. The second value closes that
route: a session then neither creates nor reaches (`alta session send`, `queue`, `steer`, a peer message, a
reminder for another session) a session whose policy is looser than its own
(`SessionRuntimeService.AcceptsPromptFrom`; a session that is not attached has the mode saved with it). A
caller that is no session is not concerned.

A session that waits for an answer counts among the sessions that wait for the user in the activity of
its space (`SpaceSessionActivity.Waiting`). The page marks it with `WaitingBadge` in the Explorer (its
row and its project) and on its tab, and says it once in a toast with **Show** when it starts to wait
while another session is on screen; a session of another space has the toast of its space.

Add **`--review-owned-command-permissions`** to the complete owned-mode command above to opt
into manual review of supported command and file-change requests. The selected-session review shows a
command with its complete command line, working directory and optional reason, and a file change with
the complete root it asks to write under and its optional reason, with **Allow once / Deny** (the RPC still takes **Cancel**).
Each kind is held to its own whole shape: a command that carries parsed actions, network access or a
policy amendment is refused rather than shown as less than it is, and a request that arrives with the
fields of the other kind, or without its own, is refused with the window it came in.
The panel (`CommandPermissionPanel`, on top of the composer of the session) is shown only while a
request waits, a decision is being sent or reading the requests failed. It reads the pending requests of
its session by itself: at once when the session is selected, then every 1.5 seconds while the session runs
and no request is shown. A request that is shown is not read again while the run goes on, so the entry
being answered is not replaced under the user; the end of the run reads once more what is left. It shows
the first waiting entry, its command line and working directory or the root of the file change, and its
reason, then its choices as one list: **Allow once**, **Deny** and a text field. It offers no **Cancel**:
its effect depends on the provider (Claude Code stops the turn, the tools of CodeAlta only fail the call),
while **Deny** means the same everywhere and the Stop button of the composer stops a run.
The choices are disabled for 400 ms after an entry appears (and after a selection shows it again), so a
click aimed at what was there does not answer it. The arrow keys move along the list (Left and Right
move the caret in the text field), Enter answers with the focused choice, 1-2 answer directly and
Escape denies (in the text field, Escape first clears its text; Escape that ends an IME composition
answers nothing); a held key answers nothing, and neither does a space. When the entry arms, or when its
session is shown while it waits, the first choice (**Allow once**) takes the focus if the focus is
nowhere, in the composer region or in the card, and the composer has no draft text or image: Enter then
allows. Focusing the prompt of a session (a tab, **Show** of a toast) goes to that choice too while a
request waits. Once the user answered, the focus goes back to the prompt (`onAnswered`). The card
announces a waiting request to screen readers (a polite status), and the list is labelled by its
question.
Text in the field and Enter send **Deny** and, once the host has taken that denial, the text as a steer
of the run through the composer queue; otherwise the text is queued for the next turn. Text that cannot
be staged (a request of the composer is pending, the queue is full) goes back to the composer draft. This works the
same for every provider: none needs a deny message of its own.
Unsupported permission payloads remain denied, this review flag alone leaves user input cancelled, and
there is no Allow for Session option.
Approval can execute a command with the host's privileges: discovery roots are not a sandbox.
Changing selection does not cancel a pending permission or its original decision-response wait.
The answer to a decision is acknowledged by the panel, which then reads the requests again; while
the response is pending, no other decision can be sent. Acceptance is not proof of execution, and
rejection does not identify an earlier decision. Genuine uncertainty or epoch invalidation disables
review and the reads across selections until renderer reload, and the panel says so. Reload loses the
local record; an empty list cannot recover a lost decision, and host restart restores no old
permission authority. No decision is replayed.
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
display state. Unsupported, stale, retiring and transitioning targets fail closed
without fallback. A run the host started from its queue, such as the answer of a child session, is
cancelled like a run of a prompt sent from the composer, although its queue drain lasts as long as
the run. Refresh submissions
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
shares the receipts the host keeps. Closing presentation cancels only the waiter, while host
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
may be in flight and shares the receipts the host keeps. Refresh submissions for the settled outcome;
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
