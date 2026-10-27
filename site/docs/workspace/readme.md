---
title: Workspace
---

# Workspace

The CodeAlta window holds your projects, your sessions and the tools around them. It is keyboard-first: most popups close back to the prompt. The **Desktop / TUI** switch on a screenshot shows each app; [Desktop and TUI](../desktop-and-tui.md) lists the differences.

See also [the prompt and the timeline](prompt.md), [code, changes and terminals](code.md) and [Settings](settings.md).

## The window

{{ alta_shot "alta-desktop-home.webp" "alta-home.png" "CodeAlta main workspace with projects sidebar, session tab, timeline and prompt editor" "The default workspace keeps navigation, the active session, prompt drafting, provider selection, and context status visible together." }}

The window has four areas:

- **Explorer** (the sidebar): projects, their sessions and sub-agents, and running work.
- **Tabs**: sessions, code editors, changes and terminals share one tab strip.
- **Timeline**: messages, reasoning, tool calls, results and statistics of the session. See [Timeline](prompt.md#timeline).
- **Prompt**: the editor, with the session status above it and the agent, model, context and Send below it. See [The prompt](prompt.md).

> [!TIP]
> Press `F1`, type `/help` or type `?` when you are unsure where an action lives.

In CodeAlta Desktop, the title bar has the buttons of the Explorer, the search, **Automations**, **Work items**, **Issues**, **Canvases**, **Bookmarks** and **Settings**; those that do not fit go in the **…** menu. It also has the space switch, the zoom and the theme switch. A [space](../spaces.md) is a group of projects with its own setup: the window shows one space at a time.

| Shortcut | Action |
| --- | --- |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` (also `Ctrl+PageDown` / `Ctrl+PageUp`) | Next / previous open tab, in the order of the tab strip (desktop). In a code editor, they go through its files instead |
| `Ctrl+Alt+Right` / `Ctrl+Alt+Left` | Next / previous tab |
| `Ctrl+T` or `/next_prompt` | Switch the session or draft to the next agent prompt |
| `Ctrl+G Ctrl+G` | Collapse or expand the Explorer |
| `Ctrl+G Ctrl+S` / `Ctrl+G Ctrl+P` | Go to the Explorer / back to the prompt |

> [!TIP]
> If these shortcuts do not work in Windows Terminal, see [Troubleshooting](../troubleshooting.md#windows-terminal-shortcuts).

## Explorer

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-explorer.webp" alt="CodeAlta Desktop sidebar with the chats, favorite projects first, three open projects with their sessions, the buttons of a project row, and the spaces at its foot" loading="lazy">
  <figcaption class="small text-secondary mt-2">The chats, then favorite projects first, several projects open, the buttons of the project under the pointer, and the spaces at the foot.</figcaption>
</figure>

The Explorer lists the projects of the space the window shows. **Chats**, at the top, are sessions of no project. With several spaces, its foot has a button for each space and says what its sessions are doing.

- **Filter sessions**, at the top, keeps the sessions, chats, projects and Claude Code conversations whose title has every word you type. `Esc` or **×** clears it.
- Click the arrow of a project to show or hide its sessions; **Collapse all** closes them all. Open projects stay open at the next start.
- A project or a [label](../labels.md) shows a spinner while sessions run in it, with their number when there are several, and a yellow mark with the number of sessions that wait for you: a folded one tells it without being opened. A project with no session or terminal has no arrow.
- Click a project to write the prompt of a new session in it. Click a session to look at it; double-click it to keep its tab (see [Tabs](#tabs)).
- Point at a project for its buttons: the star (adds it to **Favorites**), its [changes](code.md#changes), its [code editor](code.md#code-editor) and a [terminal](code.md#terminals).
- The `…` button or a right-click (`Ctrl`+click on macOS) opens the menu of a project or a session.
- The `+` button adds a folder as a project (see [Open a project](#open-a-project)).

On the row of a session, `F2` renames it, `F` forks it, `A` archives it, and `Delete` or `Backspace` deletes it, as **Rename…**, **Fork…**, **Archive** and **Delete…** of its menu do.

The menu of a session also has:

- **Delete…**: deletes the history of the session, not the files of the project, after a small question beside the row (`Enter` deletes, `Esc` keeps). **Also remove the worktree** removes the [worktree](../worktrees.md) of the session too; a session with child sessions asks about them (see [Delete a session and its child sessions](../sessions/delegation.md#child-sessions)).
- **Move to project…** (also drag the row onto a project) and **Move to space…**: see [Move a session to another project](../sessions/readme.md#move-a-session) and [to another space](../spaces.md#move-a-session).
- **Archive**: takes the session out of the Explorer; the search and the session browser restore it. See [Archive a session](../sessions/readme.md#archive-a-session).

The menu of a project has **Open in** your [external editor](code.md#external-editor), **Open in File Explorer** (**Open in Finder** on macOS), **Search sessions…**, **Archive project…** (read-only until you unarchive it) and **Delete project…** (deletes its sessions for good, keeps its folder; stop its running sessions first).

A session lists its [sub-agents](../sessions/delegation.md#sub-agents) under it. A bolt marks a session an [automation](../automations.md) started, a tree one in a [worktree](../worktrees.md).

**From Claude Code (N)**, at the end of a project, and **From Claude Code, no project (N)**, after the projects, list the Claude Code conversations started in VS Code or a terminal. Drag one onto a project to continue it there. See [Claude Code sessions](../sessions/claude-code.md).

## Search

Press `Ctrl+P` to search and run any command. In CodeAlta Desktop, `Ctrl+P` or the search icon of the title bar searches:

- the **sessions** of every project and your chats, then the Claude Code conversations not in CodeAlta yet;
- the **projects**, and the **files** of the current project;
- the **commands**, each with its slash command and its shortcut;
- the **skills** of the current project.

`Tab` switches between **All**, **Sessions**, **Conversations**, **Projects**, **Files**, **Commands**, **Skills**, **Docs**, **Tools** and **Archived** (`Enter` restores an archived session). **Conversations**, **Docs** and **Tools** come from the [search index](../search.md). Start with `/` for a command or a skill.

{{ alta_shot "alta-desktop-search.webp" "alta-command-palette.png" "Search listing sessions, projects and files that match, or the command palette of the TUI" "One search for sessions, projects, files and commands in the desktop app. The TUI has a command palette." }}

### Use a skill

Type `/` and the name of a [skill](settings.md#skills-management) in the empty prompt of a session, such as `/handoff for the review session`, and press `Enter`: the agent is asked to follow that skill, with your text. A skill is offered when it is enabled, valid and trusted for the project.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-skill-search.webp" alt="The search window of CodeAlta Desktop after a slash: the first commands, then the skills of the project, each with its slash name, its description and where it comes from" loading="lazy">
  <figcaption class="small text-secondary mt-2">After a slash, the search lists the first commands, then every skill of the project.</figcaption>
</figure>

### Session browser

In CodeAlta Desktop, `Ctrl+Alt+B` opens the session browser: a searchable table of the sessions of a project or of the global scope, with their provider, last update and message count. **Archived** shows the [archived sessions](../sessions/readme.md#archive-a-session) instead: **Restore** (or `Enter`, or a double-click) restores and opens one, **Info…** shows what is recorded of it, and **Delete** deletes it.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-session-browser.webp" alt="CodeAlta Desktop session browser listing saved sessions of a project, with its Archived button" loading="lazy">
  <figcaption class="small text-secondary mt-2">The session browser finds and opens any saved session; <strong>Archived</strong> shows the archived ones.</figcaption>
</figure>

## Tabs

In CodeAlta Desktop, a session you click opens in one tab whose title is in *italics*: the next session you click takes its place.

{.table}
| To | Do this |
|---|---|
| Look at a session | Click it |
| Keep a session open | Double-click it, or the title of its tab, or type in its prompt |
| Start a new session in a project | Click the project: its new-session prompt takes the italic tab |
| Keep that prompt open | Double-click the project or the tab. A prompt you typed in is kept too, in a tab named after its project |

<figure class="alta-figure my-4" style="max-width: 52rem;">
  <img src="{{site.basepath}}/img/alta-desktop-tabs.webp" alt="The tab strip of CodeAlta Desktop: a kept session, the Bookmarks and Issues tabs, and a tab with an italic title" loading="lazy">
  <figcaption class="small text-secondary mt-2">Kept tabs, and the tab with the italic title that the next session or project you click takes over.</figcaption>
</figure>

A session you create is kept from the start. Closing a tab never deletes a session. `Ctrl+W` (`Cmd+W` on macOS) or a middle click closes a tab, and `Ctrl+Shift+T` reopens the last closed one.

Right-click a tab, or press `Shift+F10` on it, for **Close**, **Close others**, **Close to the right** and **Close all** (in the same pane), **Keep open** on an italic tab, and **Move to window** (see [Windows](#windows)). The `…` menu of a pane has **Close others**, **Close all** and **Reopen closed tab**. A code editor with unsaved changes asks before it closes.

### Single tab for sessions

Turn on **Single tab for sessions** in **Settings > Sessions** (off by default) to show every session you open, and the new-session prompt of a project, in one tab. A session that leaves the tab keeps working; other tabs, such as editors, are not concerned.

## Split panes

In CodeAlta Desktop, tabs can be arranged in panes:

- drag a tab along the tab strip to reorder it;
- drag it to an edge of a pane to split side by side or stacked, or to the center of another pane to move it there;
- or use **Split right** and **Split below** in the `…` menu of a pane.

Each pane has its own timeline and prompt. Sessions of different projects stay open together, and the layout comes back at the next start. Each [space](../spaces.md) has its own tabs and layout.

<div class="row g-3 my-4">
  <div class="col-md-6">
    <figure class="alta-figure mb-0">
      <img src="{{site.basepath}}/img/alta-desktop-split-side.webp" alt="CodeAlta Desktop with a parent session and a child session side by side" loading="lazy">
      <figcaption class="small text-secondary mt-2">A parent session and one of its child sessions, side by side.</figcaption>
    </figure>
  </div>
  <div class="col-md-6">
    <figure class="alta-figure mb-0">
      <img src="{{site.basepath}}/img/alta-desktop-split-stacked.webp" alt="CodeAlta Desktop with two sessions from different projects stacked" loading="lazy">
      <figcaption class="small text-secondary mt-2">Sessions from two projects, stacked.</figcaption>
    </figure>
  </div>
</div>

## Windows

In CodeAlta Desktop, you can move tabs out of the main window into windows of their own, for example to keep a review on a second monitor. Such a window has the same tabs and panes, but no Explorer and no title-bar buttons.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-windows.webp" alt="Two CodeAlta Desktop windows side by side on Windows: the main window with a new session above Statistics, and Window 2, whose badge names it and its Default space, with a session" loading="lazy">
  <figcaption class="small text-secondary mt-2">The main window, with a new session and Statistics, beside Window 2, which holds a session: its badge, at the left of its tabs, shows its name and its space.</figcaption>
</figure>

Move a tab to another window:

- right-click it and choose a window under **Move to window**, or **New window**;
- use **Move tab to new window** in the search, or `/move_tab_to_window`;
- drag it out of its window: onto another window to join the pane under the pointer, or anywhere else for a new window (on Linux, a tab is dragged only inside its window);
- ask an agent, which uses `alta window tab move`.

A tab is in one window at a time: opening something another window shows brings that window to the front. The badge at the left of the tabs names the window and its space; click it to change its name, icon and color, keep it or close it.

- A window closes with its last tab. Closing a window closes its tabs; the sessions keep running.
- A window you name, or mark **Keep this window** in its badge, is kept: once closed, it is listed *closed* in **Move to window** and in **Settings > Windows** (**Open**, **Forget**), and opens again where it was.
- Open windows come back at the next start, unless you turn off **Restore windows at startup** in **Settings > Windows**.
- A window keeps the [space](../spaces.md) of its first tab: sessions and project tabs do not go to a window of another space.
- A window opens again on its monitor (on Windows, on its virtual desktop too), or on a connected one. **Monitor**, in **Settings > Windows**, pins it to one.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-windows-settings.webp" alt="Settings, Windows page of CodeAlta Desktop with Restore windows at startup, the main window and the Review window with their name, icon, color and monitor" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings &gt; Windows: the main window and a kept window, each with its name, icon, color and monitor.</figcaption>
</figure>

## Welcome page

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-landing.webp" alt="Welcome tab with recent sessions, a Statistics card and links to explore CodeAlta" loading="lazy">
  <figcaption class="small text-secondary mt-2">Start a session, return to recent work or explore CodeAlta from the Welcome tab.</figcaption>
</figure>

CodeAlta Desktop starts on a **Welcome** tab with:

- **New session**, **Open a project** and **Documentation**;
- **Get started**, while a model provider or a first project is missing;
- **Recent sessions** and **Recent projects** of the space;
- the cards of your plugins, such as the [Statistics](../plugins/statistics.md) of the last seven days;
- **Explore**: the documentation, Canvases, Plugins, Model Providers, Settings and the keyboard shortcuts.

Open it with the **Home** icon of the title bar, **Welcome** in the search or `/landing`; an agent uses `alta landing open`. The switches at its bottom, also in **Settings > General > Welcome page**, choose whether it opens at startup and whether it is animated (it never moves when your system asks for reduced motion). To remove it, turn off the **Landing page** plugin in **Settings > Plugins**. A plugin can add [its own card](../plugins/ui.md#welcome-page-cards).

## Open a project

Open the **Open Project** dialog with `Ctrl+O` or `/open`. Type a name to filter known projects, or a path to open a folder: rooted paths such as `/`, `C:` and `~` complete folders. In CodeAlta TUI, **Include hidden** includes archived projects. The project opens on its prompt, and a folder you add joins the space the window shows.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-open-project.webp" alt="CodeAlta Desktop Open project dialog filtering projects by name" loading="lazy">
  <figcaption class="small text-secondary mt-2">Type a name to filter known projects, or a path to open a folder.</figcaption>
</figure>

In CodeAlta Desktop, the folder dialog of your system opens with the `+` button of the Explorer, the button at the end of the field, or `Ctrl+O` `Ctrl+O`. Then **Trust and open folder** (`Enter`) adds the folder as a project.

## Documentation tab

CodeAlta Desktop ships this guide and shows it in a tab, without a connection. Open it with the book icon at the top right, or `/documentation`.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-documentation.webp" alt="Shipped documentation open to Agent Prompts, with page navigation, an outline and Ask an agent" loading="lazy">
  <figcaption class="small text-secondary mt-2">Read the guide beside your work, follow its headings or ask an agent about the page.</figcaption>
</figure>

- The pages are on the left and the headings of the page on the right.
- **Search the guide** (`/`) finds a text in every page.
- `Ctrl+F` (`Cmd+F` on macOS) finds a text in the page: `Enter` / `Shift+Enter` go to the next / previous match, and `Alt+C`, `Alt+W` and `Alt+R` match the case, whole words and regular expressions.
- `Alt+Left` and `Alt+Right` go back and forward. Click a picture to enlarge it.

**Ask an agent**, above the page, sends a question about the page or the whole guide to a new chat with your default provider. The agent answers with links to the pages it used. Agents in any session read the guide with `alta documentation list`, `search`, `read` and `open`.

## About and updates

Open **About** with `Ctrl+G Ctrl+A` or `/about`: it shows the version and whether a newer one exists.

CodeAlta checks NuGet for a newer package at startup; CodeAlta Desktop checks again every ten minutes and when you open About. A notice gives the new version and its release notes:

- CodeAlta Desktop: **Update and restart** runs `dotnet tool update -g CodeAlta` and starts again.
- CodeAlta TUI: the notice shows `dotnet tool update -g CodeAlta.Tui` to copy, and prints it again when you exit.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-release-toast.png" alt="CodeAlta TUI update-available toast showing a newer package version and a dotnet tool update command" loading="lazy">
  <figcaption class="small text-secondary mt-2">The update notice appears once per session when a newer CodeAlta package is available.</figcaption>
</figure>

## Close or keep running

CodeAlta Desktop opens its windows where you left them: same position and size, maximized or full screen, on a connected display.

Closing the main window asks whether CodeAlta keeps running or exits. **Keep running** hides the windows and keeps the sessions running, with an icon in the notification area (in the Dock on macOS) to show them again or exit. **Remember my choice** stops the question; **Settings > General > Startup and closing** changes it later. `Ctrl+Q` exits, after a question when sessions are running.

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-close-question.webp" alt="CodeAlta Desktop asking Keep CodeAlta running? with Cancel, Exit CodeAlta and Keep running buttons and a Remember my choice check box" loading="lazy">
  <figcaption class="small text-secondary mt-2">Enter keeps CodeAlta running, Escape leaves the window open.</figcaption>
</figure>

While CodeAlta Desktop runs, your computer does not go to sleep on its own, so sessions, automations and reminders keep going; the display still turns off and the lid still sleeps. Turn off **Keep computer awake when CodeAlta is running** in **Settings > General > Startup and closing** to change that. On Linux this needs `systemd-inhibit`.
