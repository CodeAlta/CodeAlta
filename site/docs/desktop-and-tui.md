---
title: Desktop and TUI
---

# Desktop and TUI

CodeAlta has two apps:

- **CodeAlta Desktop** (`alta`) is the app we recommend. It has session tabs you can arrange or move to windows of their own, a code editor, terminals and git changes for your projects, automations, and a single Settings window.
- **CodeAlta TUI** (`altatui`) is a keyboard-first terminal UI, for when you want to stay in a terminal.

Both apps run the same agents: the same providers, tools, agent prompts, skills, MCP servers and plugins, on the same `~/.alta` profile. This documentation applies to both. A third program, **CodeAlta CLI** (`altacli`), runs these agents without a window, for scripts and editors: see [Command Line](command-line.md). Where the two apps differ, the page says so, and screenshots have a **Desktop / TUI** switch.

{{ alta_shot "alta-desktop-home.webp" "alta-home.png" "CodeAlta main workspace" "The same workspace in both apps: projects and sessions on the left, the session timeline, and the prompt at the bottom." }}

## Install

`dotnet tool install -g CodeAlta` installs CodeAlta Desktop (`alta`); `dotnet tool install -g CodeAlta.Tui` installs the TUI (`altatui`). You can install both. See [Getting Started](getting-started.md#install) for the requirements and the first launch.

## Compare the two apps

<div class="alta-compare-wrap my-4">
<table class="alta-compare">
  <thead>
    <tr><th scope="col"></th><th scope="col"><i class="bi bi-window" aria-hidden="true"></i> Desktop</th><th scope="col"><i class="bi bi-terminal" aria-hidden="true"></i> TUI</th></tr>
  </thead>
  <tbody>
    <tr class="alta-compare-group"><th scope="rowgroup" colspan="3">Agents</th></tr>
    <tr><th scope="row">Model providers, sessions, agent prompts, skills, MCP servers, plugins</th><td>{{ alta_yes }}</td><td>{{ alta_yes }}</td></tr>
    <tr><th scope="row">Delegated agents, prompt queue, steering, compaction</th><td>{{ alta_yes }}</td><td>{{ alta_yes }}</td></tr>
    <tr><th scope="row">Model tiers: Complex, Standard or Quick instead of a model, with a fallback when a provider fails</th><td>{{ alta_yes }}</td><td>{{ alta_part }} <small>Through the agent</small></td></tr>
    <tr><th scope="row">Archive sessions and restore them</th><td>{{ alta_yes }}</td><td>{{ alta_part }} <small>Through the agent</small></td></tr>
    <tr><th scope="row">Dictate a prompt with a local speech model</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Notes, reminders and asks</th><td>{{ alta_yes }}</td><td>{{ alta_yes }}</td></tr>
    <tr><th scope="row">Background jobs: commands an agent starts without waiting for them</th><td>{{ alta_yes }} <small>Listed in the session, with their output</small></td><td>{{ alta_part }} <small>Through the agent</small></td></tr>
    <tr><th scope="row">Automations: prompts on a schedule, on a new issue or pull request</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Issues and pull requests of the repository</th><td>{{ alta_yes }} <small>A tab, and <code>#</code> in a prompt</small></td><td>{{ alta_part }} <small><code>#</code> in a prompt, issues only</small></td></tr>
    <tr><th scope="row">Work items: follow-up tasks and plans</th><td>{{ alta_yes }} <small>Cards in the session and a tab</small></td><td>{{ alta_part }} <small>Through the agent</small></td></tr>
    <tr><th scope="row"><a href="{{site.basepath}}/docs/reviews/">Reviews</a>: comments on a plan, a file or the changes, sent with a verdict; review before commit</th><td>{{ alta_yes }}</td><td>{{ alta_part }} <small>A file an agent asks you to review, with line comments and a verdict; no review of changes</small></td></tr>
    <tr><th scope="row">Bookmarks: messages and sessions to find again</th><td>{{ alta_yes }} <small>In the timeline and a tab</small></td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Worktrees: a session on its own branch, in its own folder</th><td>{{ alta_yes }} <small>Created, listed and removed in the app</small></td><td>{{ alta_part }} <small>Sessions run in theirs</small></td></tr>
    <tr><th scope="row">Agents that see and drive the app (UI tools)</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">MCP server for other applications</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Review each tool permission request</th><td>{{ alta_yes }}</td><td>{{ alta_yes }}</td></tr>
    <tr><th scope="row">Claude Code sessions shared with VS Code and a terminal</th><td>{{ alta_yes }} <small>Continue them, open them there</small></td><td>{{ alta_part }} <small>Asks before continuing one open elsewhere, adds the turns made there</small></td></tr>
  </tbody>
  <tbody>
    <tr class="alta-compare-group"><th scope="rowgroup" colspan="3">Workspace</th></tr>
    <tr><th scope="row">Session tabs</th><td>{{ alta_yes }} <small>Reorder, split and merge panes</small></td><td>{{ alta_part }} <small>One visible at a time</small></td></tr>
    <tr><th scope="row"><a href="{{site.basepath}}/docs/workspace/#windows">Several windows</a>: tabs moved or dragged out of the main window, named, colored, and back on their monitor at the next start</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Several projects open in the sidebar, favorites first</th><td>{{ alta_yes }}</td><td>{{ alta_part }} <small>No favorites</small></td></tr>
    <tr><th scope="row">Spaces: a setup for each kind of work, with its projects, chats, tabs, resources and identity</th><td>{{ alta_yes }}</td><td>{{ alta_part }} <small>Through the agent</small></td></tr>
    <tr><th scope="row">One search for sessions, projects, files and commands</th><td>{{ alta_yes }}</td><td>{{ alta_part }} <small>Command palette</small></td></tr>
    <tr><th scope="row">Code editor</th><td>{{ alta_yes }} <small>Files, search in files, file tabs</small></td><td>{{ alta_part }} <small>One file per tab</small></td></tr>
    <tr><th scope="row">Open a folder in your own editor (Visual Studio Code, Cursor, Zed…) or in the file manager</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Git changes: changed files, commits, diffs, branches</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Terminals, which agents can use too</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Tool call details</th><td>{{ alta_yes }} <small>Live terminal, highlighted files and diffs</small></td><td>{{ alta_yes }} <small>Live text output</small></td></tr>
    <tr><th scope="row">Images pasted in a prompt or read by an agent</th><td>{{ alta_yes }} <small>Thumbnails and previews</small></td><td>{{ alta_part }} <small>In terminals that show images</small></td></tr>
    <tr><th scope="row">Statistics: the numbers of all your sessions as dashboards, with the <a href="{{site.basepath}}/docs/plugins/statistics/">Statistics plugin</a></th><td>{{ alta_yes }} <small>A page in a tab</small></td><td>{{ alta_part }} <small>The card of each turn</small></td></tr>
    <tr><th scope="row">The sessions on a phone, through Tailscale, with the <a href="{{site.basepath}}/docs/plugins/mobile/">Mobile plugin</a></th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">This guide in the app, with a search and a question for an agent</th><td>{{ alta_yes }} <small>A tab</small></td><td>{{ alta_part }} <small>Through the agent</small></td></tr>
    <tr><th scope="row">Notes of a session</th><td>{{ alta_yes }} <small>Movable window</small></td><td>{{ alta_yes }} <small>Sidebar panel</small></td></tr>
  </tbody>
  <tbody>
    <tr class="alta-compare-group"><th scope="rowgroup" colspan="3">Plugins</th></tr>
    <tr><th scope="row">Run plugins</th><td>{{ alta_yes }}</td><td>{{ alta_yes }}</td></tr>
    <tr><th scope="row">Create, edit and reload a plugin while the app runs</th><td>{{ alta_yes }}</td><td>{{ alta_no }} <small>Built at start</small></td></tr>
    <tr><th scope="row">Ask an agent to write a plugin and try it</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Canvases: tabs that plugins provide, listed in one page and opened by you or by an agent</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Buttons that plugins add to the window: the title bar, the navigation rail and the menus of projects and sessions</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Plugin dialogs and content</th><td>{{ alta_yes }} <small>App components, HTML, Markdown, diagrams</small></td><td>{{ alta_yes }} <small>Terminal controls</small></td></tr>
  </tbody>
  <tbody>
    <tr class="alta-compare-group"><th scope="rowgroup" colspan="3">Application</th></tr>
    <tr><th scope="row">Settings</th><td>{{ alta_yes }} <small>One window, a page per area</small></td><td>{{ alta_yes }} <small>A dialog per area</small></td></tr>
    <tr><th scope="row">Appearance</th><td>{{ alta_yes }} <small>Light, dark or system, 13 color schemes</small></td><td>{{ alta_yes }} <small>Terminal themes</small></td></tr>
    <tr><th scope="row">Keeps running in the notification area</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Update and restart from the app</th><td>{{ alta_yes }}</td><td>{{ alta_no }} <small>Shows the command to run</small></td></tr>
    <tr><th scope="row">Works without a Nerd Font</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
    <tr><th scope="row">Runs in a terminal</th><td>{{ alta_no }}</td><td>{{ alta_yes }}</td></tr>
    <tr><th scope="row">Copy the UI as an image</th><td>{{ alta_no }}</td><td>{{ alta_yes }} <small><code>Ctrl+F12</code></small></td></tr>
  </tbody>
</table>
</div>

## Which one to use

Use CodeAlta Desktop for daily work: it shows more at once, and it has features that the TUI does not have. Use CodeAlta TUI when you want to stay in a terminal.

## What is shared

Everything under `~/.alta/` is common to both apps:

- `config.toml`, model providers and their credentials;
- the project catalog, its [spaces](spaces.md) and all sessions, with their timelines, notes and pasted images;
- agent prompts, system prompts and skills;
- MCP server configuration and plugins.

A session started in one app can be continued in the other. Only one CodeAlta runs on a profile at a time: close one app before starting the other.

Appearance is not shared: the desktop app keeps its theme, color scheme, open tabs and window layout in its own application data, and the TUI keeps its theme and navigator settings in `~/.alta/ui-state.yaml`.

## What the desktop app adds

### Panes and windows

Drag a session tab along the tab strip to reorder it, to the edge of a pane to split the window, or to the center of another pane to merge; the tab menu also has **Split right** and **Split below**. Each pane has its own prompt, and tabs of different projects stay open together. A tab can also move to a window of its own, for example on a second monitor: see [Windows](workspace/readme.md#windows).

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

### Code editor

The `</>` button of a project in the sidebar, or `Ctrl+E` `Ctrl+E`, opens the code editor of the project: its files, a search in files, and the open files as tabs. You can create, rename, move and delete files there. See [Code editor](workspace/code.md#code-editor).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-code-editor.webp" alt="CodeAlta Desktop code editor with the files of a project on the left and a source file on the right" loading="lazy">
  <figcaption class="small text-secondary mt-2">The files of a project, the open files as tabs, and the selected file.</figcaption>
</figure>

`Ctrl+E` opens one file with the files hidden. The editor is a tab like a session, so it can stay beside the session that works on the file.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-editor-session.webp" alt="CodeAlta Desktop with a session on the left and a source file in the code editor on the right" loading="lazy">
  <figcaption class="small text-secondary mt-2">A session and a file of the project, side by side.</figcaption>
</figure>

### Terminals

The terminal button of a project in the sidebar, or ``Ctrl+` ``, opens a terminal in the folder of the project. It keeps running when its tab is closed, and agents can use it too. See [Terminal](workspace/code.md#terminals).

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

### UI tools

An agent can take a screenshot of the window, read what it shows, click and type. Other applications do the same through the MCP server of CodeAlta Desktop. See [UI tools and MCP server](ui-tools.md).

### Review changes

The Changes tab of a project lists the changed files and recent commits, with the diff of the selected file. Open it from the `+` / `−` numbers of the **Workspace** chip above the prompt, or the changes button of a project in the sidebar. See [Changes](workspace/code.md#changes).

**Review** in its header comments on the lines of the diffs as in a pull request and sends your comments to a session with a verdict. With [Review before commit](reviews.md#review-before-commit), agents wait for your approval before they commit. See [Reviews](reviews.md).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-changes.webp" alt="CodeAlta Desktop with the Changes tab of a project and a side-by-side diff" loading="lazy">
  <figcaption class="small text-secondary mt-2">The changes of a project, with the diff of the selected file.</figcaption>
</figure>

### Projects at hand

Several projects can stay open in the sidebar, each with its sessions, and CodeAlta shows them the same way at the next start. Favorite projects are listed first. See [Projects sidebar](workspace/readme.md#explorer).

### Spaces

A space is a setup for one kind of work, such as **Work** or **Personal**: its projects and chats, the providers, MCP servers, skills, agent prompts and plugins of its sessions, and the Git, GitHub, Jira and command line identity they work under. The window shows one space at a time and tells you when a session waits for you in another. CodeAlta TUI has no spaces: it lists every project. See [Spaces](spaces.md).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-spaces.webp" alt="CodeAlta Desktop showing the Open source space, with the list of the space switch open: Default, Work, Personal and Open source" loading="lazy">
  <figcaption class="small text-secondary mt-2">The space switch of the title bar: one space is shown, and the others say what their sessions are doing.</figcaption>
</figure>

### One Settings window

The TUI opens a dialog for each area. The desktop app has them as pages of one window: **Appearance** (the opening page), **General**, **Windows**, **Speech**, **Spaces** (a page for each space), **Sessions**, **Permissions**, **Providers**, **Models**, **Agent prompts**, **Skills**, **Plugins**, **MCP Servers**, **Configuration file**, **Application Logs** and **About**.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-settings.webp" alt="CodeAlta Desktop Settings window showing the Appearance page with the list of pages on the left" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings pages on the left, the selected page on the right.</figcaption>
</figure>

### Floating windows

Tool details, file diffs, context usage, session info, the session browser and the Notes of a session open as windows you can move and resize over the workspace. The window of a tool call follows it while it runs: a command shows its output in a terminal, a file its lines or its diff, and a patch a tab per file. See [Tool calls](workspace/prompt.md#tool-calls).

### Runs in the background

Closing the main window can keep CodeAlta running in the notification area, with its sessions. CodeAlta asks whether to keep running or to exit, and remembers the answer with **Remember my choice**; **Settings > General > Startup and closing > When the window is closed** changes it.

- The tray icon opens the window again or exits. Exiting while sessions run asks first.
- On Windows, the taskbar button shows progress while a session runs or queued prompts are sent.
- On macOS, CodeAlta started from the Applications folder, Launchpad, Spotlight or the Dock stays in the Dock, without a menu bar icon; its Dock icon opens the window again.
- While CodeAlta runs, the computer does not go to sleep on its own (the display still turns off), so automations and reminders keep going. **Keep computer awake when CodeAlta is running**, in the same place, turns it off.

On macOS the menu bar has the standard items: **Quit CodeAlta** (⌘Q), **Hide** (⌘H), **Minimize** (⌘M), **Close** (⌘W), and the **Edit** menu. ⌘W closes the window as its close button does; ⌘Q exits.

### Updates from the app

The desktop app checks for new versions at startup and every ten minutes, also from the notification area. A new version shows a notice with **Update and restart** in the bottom-right corner until you dismiss it; **Settings > About** keeps the update available afterwards. The TUI shows the `dotnet tool update` command to run.

On macOS, the desktop app warns at startup when it runs on a .NET runtime older than 10.0.13, which can make it crash hours later. **Download .NET** opens the [.NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0): install the latest .NET 10 runtime, then start CodeAlta again.

## Same feature, another name

| | Desktop | TUI |
| --- | --- | --- |
| Command | `alta` | `altatui` |
| Sessions of no project | **Chats**, first in the sidebar | **Global sessions** |
| Queue every prompt, also when idle | **Enqueue until idle** in the Send options | **AlwaysQueue** checkbox |

## Desktop limitations

The `--no-plugins` and `--plugin-safe-mode` options belong to the TUI. Set `CODEALTA_DISABLE_PLUGINS=1` to start the desktop app without plugins.

A plugin written with terminal controls only shows its plain text or Markdown form on the desktop. See [Plugin development](plugins/developers.md#one-plugin-two-apps).
