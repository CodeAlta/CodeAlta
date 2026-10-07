---
title: Desktop and TUI
---

# Desktop and TUI

CodeAlta has two apps:

- **CodeAlta Desktop** (`alta`) is a desktop application with session tabs you can arrange, a code editor for your projects and a single Settings window. It is the most complete way to use CodeAlta.
- **CodeAlta TUI** (`altatui`) is a keyboard-first terminal UI.

Both apps share the same harness: the same agent runtime, providers, tools, agent prompts, skills, MCP servers and plugins, on the same `~/.alta` profile. This documentation applies to both. Where the two apps differ, the page says so, and screenshots have a **Desktop / TUI** switch.

{{ alta_shot "alta-desktop-home.webp" "alta-home.png" "CodeAlta main workspace" "The same workspace in both apps: projects and sessions on the left, the session timeline, and the prompt at the bottom." }}

## Install

```sh
dotnet tool install -g CodeAlta        # Desktop: alta
dotnet tool install -g CodeAlta.Tui    # TUI: altatui
```

You can install both. See [Getting Started]({{site.basepath}}/docs/getting-started/) for requirements and first launch.

## What is shared

Everything under `~/.alta/` is common to both apps:

- `config.toml`, model providers and their credentials;
- the project catalog and all sessions, with their timelines, notes and pasted images;
- agent prompts, system prompts and skills;
- MCP server configuration and plugins.

A session started in one app can be opened and continued in the other. Only one CodeAlta runs on a profile at a time, so close one app before starting the other.

Appearance is not shared. The desktop app keeps its theme, color scheme, open tabs and window layout in its own application data, and the TUI keeps its theme and navigator settings in `~/.alta/ui-state.yaml`.

## What the desktop app adds

### Arrange sessions in the window

Session tabs can be dragged along the tab strip to reorder them, to the edge of a pane to split the window, or to the center of another pane to merge. The tab menu also has **Split right** and **Split below**. Tabs from different projects stay open together, and each pane has its own prompt.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-split-three.webp" alt="CodeAlta Desktop with a parent session and its two child sessions in three panes" loading="lazy">
  <figcaption class="small text-secondary mt-2">A parent session and its two child sessions share the window.</figcaption>
</figure>

<div class="row g-3 my-4">
  <div class="col-md-6">
    <figure class="alta-figure mb-0">
      <img src="{{site.basepath}}/img/alta-desktop-split-side.webp" alt="CodeAlta Desktop with two sessions side by side" loading="lazy">
      <figcaption class="small text-secondary mt-2">Two sessions side by side.</figcaption>
    </figure>
  </div>
  <div class="col-md-6">
    <figure class="alta-figure mb-0">
      <img src="{{site.basepath}}/img/alta-desktop-split-stacked.webp" alt="CodeAlta Desktop with two sessions stacked" loading="lazy">
      <figcaption class="small text-secondary mt-2">Two sessions from different projects, stacked.</figcaption>
    </figure>
  </div>
</div>

### A code editor for each project

The `</>` button of a project in the sidebar, or `Ctrl+E` `Ctrl+E`, opens the code editor of the project: its folders and files, a search in files, and the files you open as tabs. You can create, rename, move and delete files there, and pictures are shown as pictures. See [Code editor](workspace.md#code-editor).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-code-editor.webp" alt="CodeAlta Desktop code editor with the files of a project on the left and a source file on the right" loading="lazy">
  <figcaption class="small text-secondary mt-2">The files of a project, the open files as tabs, and the selected file.</figcaption>
</figure>

`Ctrl+E` opens one file with the files hidden. The editor is a tab like a session, so it can stay beside the session that works on the file.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-editor-session.webp" alt="CodeAlta Desktop with a session on the left and a source file in the code editor on the right" loading="lazy">
  <figcaption class="small text-secondary mt-2">A session and a file of the project, side by side.</figcaption>
</figure>

### A terminal next to your sessions

The terminal button of a project in the sidebar, or ``Ctrl+` ``, opens a terminal in the folder of the project. It keeps running when its tab is closed, and agents can use it too. See [Terminal](workspace.md#terminal-desktop).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-terminal.webp" alt="CodeAlta Desktop with a session at the top and a terminal under it" loading="lazy">
  <figcaption class="small text-secondary mt-2">A terminal under a session, and the terminals of the project in the sidebar.</figcaption>
</figure>

### Automations

An automation sends a prompt by itself: every morning, every Friday, or when an issue or a pull request is opened. Each run starts a new session. See [Automations](automations.md).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-automations.webp" alt="CodeAlta Desktop Automations tab with the next 24 hours and the automations of a project" loading="lazy">
  <figcaption class="small text-secondary mt-2">The automations of a project and when they run next.</figcaption>
</figure>

### Worktrees

A session can work in its own git worktree, on its own branch, so that several sessions change the same project at the same time. See [Worktrees](worktrees.md).

### UI tools and MCP server

An agent can take a screenshot of the window, read what it shows, click and type. Other applications do the same through the MCP server of CodeAlta Desktop. See [UI tools and MCP server](ui-tools.md).

### Review changes

Click the `+` / `−` numbers in the prompt bar, or the changes button of a project in the sidebar, to open the Changes tab of the project. It lists the changed files and recent commits and shows a syntax-highlighted diff of the selected file. See [Changes](workspace.md#changes-desktop).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-changes.webp" alt="CodeAlta Desktop with the Changes tab of a project and a side-by-side diff" loading="lazy">
  <figcaption class="small text-secondary mt-2">The changes of a project, with the diff of the selected file.</figcaption>
</figure>

### Projects at hand

Several projects can stay open in the sidebar, each with its sessions, and CodeAlta shows them the same way at the next start. Favorite projects are listed first. See [Projects sidebar](workspace.md#projects-sidebar-desktop).

### One Settings window

The TUI opens a dialog for each area. The desktop app groups them as pages of one window: **Appearance**, **Providers**, **Models**, **Agent prompts**, **Skills**, **Plugins**, **MCP Servers**, **Configuration file**, **Application Logs** and **About**.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-settings.webp" alt="CodeAlta Desktop Settings window showing the Appearance page with the list of pages on the left" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings pages on the left, the selected page on the right.</figcaption>
</figure>

### Windows over the session

Tool details, file diffs, context usage, session info, the session browser and the Notes of a session open as windows that you can move and resize over the workspace.

### Runs in the background

Closing the window can keep CodeAlta running in the notification area, with its sessions. CodeAlta asks whether to keep running or to exit, and remembers the answer if you tick **Remember my choice**. Use the tray icon to open the window again or to exit. **Settings > Appearance > When the window is closed** changes the choice. Exiting while sessions are running asks for confirmation first.

On macOS the menu bar has the standard items and shortcuts: **Quit CodeAlta** (⌘Q), **Hide** (⌘H), **Minimize** (⌘M), **Close** (⌘W), and **Undo**, **Redo**, **Cut**, **Copy**, **Paste** and **Select All** in the **Edit** menu. ⌘W closes the window as its close button does, with the same choice between staying in the menu bar and exiting; ⌘Q exits.

### Updates from the app

When a new version is available, the desktop app shows a notice with **Update and restart**. The TUI shows the `dotnet tool update` command to run.

## Differences at a glance

| | Desktop | TUI |
| --- | --- | --- |
| Command | `alta` | `altatui` |
| Session tabs | Drag to reorder, split and merge panes | One visible tab at a time |
| Code editor | One tab per project with its files, a search in files and file tabs | One editor tab per file |
| Git changes | Changes tab with the changed files, recent commits and a diff | Not available |
| Terminal | Terminals in tabs, listed in the sidebar; agents can use them | Not available |
| Automations | Prompts that run on a schedule, on a new issue or pull request, or on demand | Not available |
| Worktrees | Start a session in a new git worktree; list, remove and switch branches in the Changes tab | Sessions run in their worktree; not created or removed there |
| UI tools and MCP server | Agents see and drive the window; other applications connect to the MCP server of CodeAlta | Not available |
| Sessions of no project | **Chats**, first in the sidebar | **Global sessions** |
| Favorite projects | Listed first in the sidebar | Not available |
| Settings | One window with a page per area | One dialog per area |
| Appearance | Light, dark or system theme, 13 color schemes | Terminal themes |
| Session notes | Movable Notes window over the timeline | Notes panel in the sidebar |
| Pasted images | Thumbnails above the prompt | Preview dialog, in terminals with image support |
| Images read by the agent | **Image read** card with a preview | A line naming the image |
| Queue every prompt, also when idle | **Enqueue until idle** in the Send options | **AlwaysQueue** checkbox |
| Tool permissions | Always approved automatically | Approved automatically by default; can be reviewed |
| Plugin dialogs and content | The app's own components, with HTML fragments from the plugin | Terminal controls |
| Updates | **Update and restart** | Shows the command to run |
| Font | No requirement | Nerd Font required |
| Copy the UI as an image | Not available | `Ctrl+F12` |

## Current desktop limitations

Tool permission requests are always approved automatically on the desktop. To review each request, use the TUI and turn off **Auto approve commands** in Workspace Settings.

The `--no-plugins` and `--plugin-safe-mode` options belong to the TUI. Set `CODEALTA_DISABLE_PLUGINS=1` to start the desktop app without plugins.

A plugin written with terminal controls only shows its plain text or Markdown form on the desktop. See [Plugin development](plugins/developers.md#one-plugin-two-apps).

## Which one to use

Use the desktop app for daily work: it shows more at once and every feature is one click away. Use the TUI when you prefer to stay in a terminal, or when you want to review tool permission requests.
