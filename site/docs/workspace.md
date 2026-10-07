---
title: Workspace and Dialogs
---

# Workspace and Dialogs

CodeAlta is designed for keyboard-first work, in the desktop app and in the terminal UI. Most popups close back to the prompt editor so you can keep typing without rebuilding context.

The workspace is the same in both apps. Use the **Desktop / TUI** switch on a screenshot to see each version; [Desktop and TUI]({{site.basepath}}/docs/desktop-and-tui/) lists the differences.

## Main workspace

{{ alta_shot "alta-desktop-home.webp" "alta-home.png" "CodeAlta main workspace with projects sidebar, session tab, timeline and prompt editor" "The default workspace keeps navigation, the active session, prompt drafting, provider selection, and context status visible together." }}

The main screen has four important areas:

- **Navigator/sidebar**: projects, their sessions and child sessions, running work, and navigator actions.
- **Workspace tabs**: session tabs, the code editor, the changes and the terminals of a project share the same tab strip.
- **Timeline**: user messages, assistant messages, reasoning/status updates, tool calls, results, statistics cards, compaction notices, and modified-file summaries.
- **Prompt bar**: prompt editor, queue strip, agent prompt selector, provider/model/reasoning selectors, context usage, compact button, and status text.

> [!TIP]
> Press `F1`, type `/help`, or type `?` when you are unsure where an action lives. Help and command discovery are designed to return you to the prompt quickly.

Use `Ctrl+Alt+Left` and `Ctrl+Alt+Right` to move between tabs. Use `Ctrl+T` (or `/next_prompt`) to cycle the current draft/session to the next agent prompt. Use `Ctrl+G Ctrl+G` to collapse or expand the navigator, `Ctrl+G Ctrl+S` to focus the sidebar, and `Ctrl+G Ctrl+P` to return to the prompt.

> [!TIP]
> If these shortcuts do not work in Windows Terminal, see [Troubleshooting: Windows Terminal shortcuts do not reach CodeAlta]({{site.basepath}}/docs/troubleshooting/#windows-terminal-shortcuts-do-not-reach-codealta).

### Projects sidebar (desktop)

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-explorer.webp" alt="CodeAlta Desktop sidebar with the chats, favorite projects first, three open projects with their sessions, and the buttons of a project row" loading="lazy">
  <figcaption class="small text-secondary mt-2">The chats, then favorite projects first, several projects open, and the buttons of the project under the pointer.</figcaption>
</figure>

- **Chats**, at the top, are the sessions that belong to no project.
- Click the arrow of a project to show or hide its sessions. Several projects can stay open, and CodeAlta shows them the same way at the next start. **Collapse all** at the top closes them all.
- Point at a project to see its buttons. The star adds it to **Favorites**, at the top of the list. The next two open its [changes](#changes-desktop) and its [code editor](#code-editor), and stay visible while that tab is open. The last one opens a [terminal](#terminal-desktop).
- The `…` button of a project or a session, or a right-click, opens its menu.
- A session with a bolt was started by an [automation](automations.md).
- A session with a tree mark works in a [worktree](worktrees.md).

### Command palette

Press `Ctrl+P`, or type `/` in an empty prompt, to search and run any command. Each entry shows its slash command and its shortcut.

{{ alta_shot "alta-desktop-command-palette.webp" "alta-command-palette.png" "Command palette listing commands with their shortcuts" "The command palette lists every command with its slash name and shortcut." }}

### Split panes (desktop)

In the desktop app, session tabs, the code editor and the Changes tab can be arranged in panes:

- drag a tab along the tab strip to reorder it;
- drag a tab to an edge of a pane to split the window side by side or stacked;
- drag a tab to the center of another pane to move it there;
- or use **Split right** and **Split below** in the tab menu (`…`) of a pane.

Each pane has its own timeline and prompt. Tabs are workspace-wide: sessions from different projects stay open together, and the layout is restored at the next start. `Ctrl+W` closes the current tab and `Ctrl+Shift+T` reopens the last closed one.

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

## Timeline cards

{{ alta_shot "alta-desktop-modified-files.webp" "alta-modified-files.png" "Diff of a modified file opened from the timeline" "Modified-file cards summarize changed files and diff totals. Open a file to read its diff." }}

Timeline entries are grouped so the important parts stay visible:

- assistant messages render as Markdown;
- reasoning and status messages explain what the agent is doing;
- tool calls appear as compact tiles, side by side;
- the modified-files card summarizes per-file `+/-` diff totals and can show diff details;
- statistics cards summarize timing, tools, usage, and other plugin-projected details.

Use `F3` / `F4` to jump between previous and next user or assistant messages. Use `Ctrl+F3` to jump to the first message and `Ctrl+F4` to return to the bottom.

The desktop timeline opens on the latest messages of a session. Use **Load previous messages** at the top to read earlier turns.

### Tool calls

{{ alta_shot "alta-desktop-tool-details.webp" "alta-tool-input-output-dialog.png" "Details of a tool call opened from the timeline" "Open a tool call to follow a command while it runs, or to read the file or the diff it worked on." }}

A tool call has one tile, from its start to its end. The tile shows the tool, what it works on (a command, a file, a search), its state and what it wrote. While a command runs, its tile shows the last line it wrote.

In CodeAlta Desktop, click a tile to open the window of the call. The window follows the call while it runs:

- **A command** shows its command line and a terminal with its output, as it comes.
- **A file that was read** shows its lines with their numbers, in the colors of its language.
- **An edit** shows the diff of the file. A patch that changes several files has a tab per file.
- **A search** shows its matches by file, and **any other tool** its arguments and its result.

The line under the title says how long the call took, the exit code of a command, and the lines added and removed. The **Details** tab has the arguments and the result as the tool received and returned them.

In the TUI, a tile opens a dialog with the details of the call and its output.

## Prompt editor and prompt queue

{{ alta_shot "alta-desktop-prompt.webp" "alta-prompt.png" "Inline prompt editor with model controls, context usage and send actions" "The inline prompt editor keeps model controls, context usage, queue state, and send actions close to your draft." }}

Press `Enter` to send and `Shift+Enter` for a new line. If the selected session is busy, `Enter` adds the prompt to the waiting list instead of dropping it. Queued prompts can be edited, repeated, steered immediately when supported, deleted, or cleared with `F10`.

The agent prompt selector chooses the agent prompt profile for the current draft/session. Built-in prompts appear first, followed by global `~/.alta/prompts/agents` prompts and project `.alta/prompts/agents` prompts. Global/project prompts with the same file id override lower-precedence prompts; see [Agent Prompts]({{site.basepath}}/docs/prompts/).

In the desktop app, the agent prompt, provider, model and reasoning effort are in one picker at the left of the prompt bar. It lists the models of the provider and the reasoning efforts of the selected model. The prompt bar also shows the project folder, the git branch and the lines added and removed since the last commit. These numbers follow the changes made outside CodeAlta too. Click them to open the [changes of the project](#changes-desktop).

<figure class="alta-figure my-4" style="max-width: 38rem;">
  <img src="{{site.basepath}}/img/alta-desktop-session-config.webp" alt="CodeAlta Desktop picker for agent prompt, provider, model and reasoning effort" loading="lazy">
  <figcaption class="small text-secondary mt-2">Agent prompt, provider, model and reasoning effort for the next send.</figcaption>
</figure>

`Ctrl+Enter` steers a running provider session. If the provider cannot steer live, CodeAlta re-queues the prompt for the next normal turn. `F8` aborts the running turn.

Use `F6` or the **Full Prompt** action to open a larger prompt editor. `Esc` or `Ctrl+Enter` closes it and keeps your draft.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-system-prompt-and-user-prompt.png" alt="CodeAlta timeline showing system prompt details and an agent prompt" loading="lazy">
  <figcaption class="small text-secondary mt-2">Prompt and system-prompt details are visible in the timeline, including the selected agent prompt and source path, so you can review what context was sent.</figcaption>
</figure>

## Session notes

Each session has sticky Markdown notes that agents keep up to date with a checklist, a status or next actions. The TUI shows them in the **Notes** panel of the sidebar. The desktop app shows them in a **Notes** window over the timeline, which you can move, resize, collapse, copy and clear; `Ctrl+Shift+N` shows or hides it.

{{ alta_shot "alta-desktop-notes.webp" "alta-notes.png" "Session notes with a Markdown checklist" "Notes belong to the session and stay visible while the agent works." }}

## Open Project dialog

Open it with `Ctrl+O` or `/open`.
Opening a project puts the cursor in its prompt, so you can start typing.

The dialog supports project-name and directory completion. Rooted paths such as `/`, `C:`, `D:`, and `~` open folders. In the TUI, the **Include hidden** toggle includes archived/hidden projects in completion.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-open-project.webp" alt="CodeAlta Desktop Open project dialog filtering projects by name" loading="lazy">
  <figcaption class="small text-secondary mt-2">Type a name to filter known projects, or a path to open a folder.</figcaption>
</figure>

### Choose a folder with the system dialog

In CodeAlta Desktop you can also pick the folder with the folder dialog of your operating system:

- The `+` button of the Projects sidebar opens the folder dialog directly, to add a folder as a project.
- In the Open Project dialog, the button at the end of the field opens it too. So does pressing `Ctrl+O` again, which makes `Ctrl+O` `Ctrl+O` the quick way to browse for a folder.

On Windows the dialog accepts a pasted path in its **Folder** field or its address bar. After you choose a folder, CodeAlta shows it with **Trust and open folder**: press `Enter` to add it as a project. A folder that is already a project is opened right away.

In the CodeAlta TUI, the `+` action of the Projects sidebar opens the Open Project dialog.

## File/folder picker and prompt attachments

{{ alta_shot "alta-desktop-file-selection.webp" "alta-file-selection.gif" "File picker for attaching project files to a prompt" "The <code>@</code> picker searches project files and folders, then inserts accepted entries as structured prompt attachments." }}

Type `@` in the prompt to search project files and folders. Accepted entries become Markdown links and are sent as structured attachments. You can also type raw references such as:

> [!NOTE]
> Attach only the files or folders that are relevant to the task. Smaller, focused context usually makes provider responses easier to review and keeps compaction pressure lower.

```text
@src/CodeAlta.Tui/Program.cs
@"src/path with spaces/file.cs":10-40
```

Use `Ctrl+E` or `/edit` to open the same picker and edit the selected file in the [code editor](#code-editor).

## Code editor

{{ alta_shot "alta-desktop-code-editor.webp" "alta-code-editor.png" "Code editor of a project with its files, the open files as tabs and syntax-highlighted source code" "The code editor of a project: its files on the left, the open files as tabs, and the selected file." }}

CodeAlta Desktop has a code editor for each project: one tab that holds the files of the project and every file you open in it.

- Click the `</>` button of a project in the sidebar, press `Ctrl+E` `Ctrl+E`, or run `/editor` to open it with the files of the project.
- Press `Ctrl+E`, or run `/edit`, to open a single file. `Ctrl+B` shows or hides the files.
- **Edit in the code editor** on a plugin of **Settings > Plugins** opens the same editor on the folder of that plugin. See [Plugin development]({{site.basepath}}/docs/plugins/developers/#edit-build-and-reload).

### Files

The **Files** pane lists the folders and files of the project, without those excluded by `.gitignore`. Changed files have the color and the letter of their git status.

- Click a file to preview it. Double-click it, or edit it, to keep its tab open.
- **New file** and **New folder** are at the top of the pane. `F2` renames, and `Delete` moves to the Recycle Bin or the Trash.
- Drag a file or a folder onto another folder to move it.

### Editing

| Action | Shortcut |
| --- | --- |
| Save the file / all files | `Ctrl+S` / `Ctrl+Shift+S` |
| Find / replace in the file, also with a regular expression | `Ctrl+F` / `Ctrl+H` |
| Go to a line | `Ctrl+G` |
| Next file | `Ctrl+Tab` |
| Close the file | `Ctrl+W` |
| Show the files / search in files | `Ctrl+Shift+E` / `Ctrl+Shift+F` |
| Wrap lines | `Alt+Z` |

The editor asks before it closes a file with unsaved changes. A file changed by an agent or by another program is reloaded, unless you have edited it: the editor then lets you choose.

### Search in files

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-editor-search.webp" alt="CodeAlta Desktop code editor with the results of a regular expression search in the files of a project" loading="lazy">
  <figcaption class="small text-secondary mt-2">The results of a search, file by file. A click opens the file on the match.</figcaption>
</figure>

The **Search** pane (`Ctrl+Shift+F`) searches the text of the project files. It can match the case, whole words or a regular expression, and include or exclude files such as `src, *.ts`.

### Pictures and previews

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-editor-preview.webp" alt="CodeAlta Desktop code editor showing an SVG file as a drawing, with zoom controls" loading="lazy">
  <figcaption class="small text-secondary mt-2">An SVG file shown as a drawing. The buttons at the right of the tabs switch to its text.</figcaption>
</figure>

Pictures are shown as pictures. An SVG file opens as a drawing, and a Markdown file can be shown as a page: the buttons at the right of the file tabs switch between the preview and the text.

### Beside a session

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-editor-session.webp" alt="CodeAlta Desktop with a session on the left and a source file in the code editor on the right" loading="lazy">
  <figcaption class="small text-secondary mt-2">A session and the file it talks about, side by side.</figcaption>
</figure>

Drag the editor tab to an edge of the window to keep a file beside the session that works on it. An agent can open a file for you with `alta editor open --file <path> --line <n>`.

### Editor tabs in the TUI

In CodeAlta TUI, `Ctrl+E` opens a file in an editor tab with syntax highlighting, `Ctrl+S` to save, and a confirmation before closing unsaved edits.

## Changes (desktop)

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-changes.webp" alt="CodeAlta Desktop with the Changes tab of a project: the tree of changed files, the history and a side-by-side diff" loading="lazy">
  <figcaption class="small text-secondary mt-2">The changed files of a project, its recent commits, and the diff of the selected file.</figcaption>
</figure>

The Changes tab shows what changed in the git repository of a project. Open it by clicking the `+` / `−` numbers in the prompt bar, or the changes button of a project in the sidebar. It opens beside the current tab, and you can move, split or close it like any other tab. Each project has its own Changes tab, so several can be open.

- **Worktrees**: when the project has git [worktrees](worktrees.md), they are listed above the files. Click one to see its changes.
- **Files**: the changed files as a tree or a flat list, with the lines added and removed in each file. Type in the filter to narrow the list.
- **History**: under the files. Choose **Uncommitted changes**, all the changes of the branch since its base branch, or one of the recent commits. **Load more** shows older commits.
- **Diff**: the selected file with syntax highlighting, side by side or inline. Unchanged regions are folded, and you can expand them. `Alt+Down` and `Alt+Up` jump to the next and previous change.
- **Refresh**: the tab refreshes every five seconds while **Auto-refresh** is on. The refresh button reads the changes right away.

The `…` menu of the diff has **Hide unchanged lines**, **Ignore whitespace changes**, **Wrap lines** and **Copy path**. **Open file** opens the file in the [code editor](#code-editor).

An agent can open this tab for you with `alta diff show`, for example when it has finished a change and wants you to review it.

## Terminal (desktop)

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-terminal.webp" alt="CodeAlta Desktop with a session at the top, a terminal under it and the terminals of the project listed in the sidebar" loading="lazy">
  <figcaption class="small text-secondary mt-2">A terminal under a session, and the terminals of the project in the sidebar.</figcaption>
</figure>

CodeAlta Desktop has terminals: your own shell, in a tab under your sessions.

- Click the terminal button of a project in the sidebar, or press ``Ctrl+` ``, to open a terminal in the folder of the project. The terminal button of the prompt bar opens one in the folder of the session.
- A terminal keeps running when you close its tab. The terminals of a project are listed under its sessions: click one to show it again, rename it, or end it.
- The list shows the folder the shell is in, and a spinner while a command runs.
- `Ctrl+C` copies the selection, or interrupts the program when nothing is selected. `Ctrl+V` pastes and `Ctrl+F` finds text.
- The options button of a terminal sets the cursor, the text size, **Copy on select**, the number of lines kept, and the shell new terminals start.

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-terminal-options.webp" alt="Options of a terminal in CodeAlta Desktop: cursor, blink, text size, copy on select, scrollback and shell integration" loading="lazy">
  <figcaption class="small text-secondary mt-2">The options of the terminals.</figcaption>
</figure>

The default shell is PowerShell on Windows and your login shell on macOS and Linux. On Windows you can also choose the Command Prompt, Git Bash or a WSL distribution. CodeAlta comes with a font that has the icons many prompts use, so there is nothing to install.

### Terminals for agents

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-terminal-agent.webp" alt="CodeAlta Desktop with a session that ran a command in a terminal it created, and that terminal under the session" loading="lazy">
  <figcaption class="small text-secondary mt-2">A session runs a command in a terminal it created, and reads the result.</figcaption>
</figure>

An agent can use the same terminals: create one, type in it, wait for a command and read what it printed. Ask for it in your own words, for example:

```text
Start the dev server in a terminal and tell me when it is ready.
```

You see what the agent types, and you can type in the terminal yourself. A terminal an agent created has a small robot mark in the list.

## Model Providers

{{ alta_shot "alta-desktop-model-providers.webp" "alta-model-providers.png" "Model provider configuration with validation controls" "Provider setup, endpoint details, credential options, and tests live in one place." }}

Open it with `Ctrl+G Ctrl+R`, `/model_providers`, or the provider summary. Use it to enable providers, enter credentials, test endpoints, sign in to Codex or Copilot, and edit advanced TOML safely. In the desktop app this is the **Providers** page of Settings.

## Prompt manager

Open it with `Ctrl+G Ctrl+H` or `/prompt`. It lists built-in modes such as Default and Plan plus global/project custom prompts, shows which prompts are shadowed by overrides, and edits the selected prompt on the right. Agent prompt properties are `name`, `description`, `system`, and the Markdown body. System prompt files are listed with the same override rules, and only global/project override bodies can be edited. Built-in prompts are displayed for inspection but are read-only; create a global or project prompt/system prompt with the same id to override one. Advanced prompt workflows can combine prompts with sessions, notes, reminders, asks, MCP, and skills; see [Advanced Agent Workflows]({{site.basepath}}/docs/advanced-agent-workflows/).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-prompts.webp" alt="CodeAlta Desktop Agent prompts page listing built-in prompts with the selected prompt on the right" loading="lazy">
  <figcaption class="small text-secondary mt-2">The <strong>Agent prompts</strong> page of the desktop Settings. <strong>Customize a copy</strong> creates your own version of a built-in prompt.</figcaption>
</figure>

## Model browser

{{ alta_shot "alta-desktop-models.webp" "alta-models.png" "Model browser showing provider models and capability metadata" "The model browser lists the models of every provider with their context size and capabilities." }}

Open it with `Ctrl+G Ctrl+O` or `/models`. It shows provider/model metadata and model refs such as `codex:gpt-5.5@high`. Use it to verify whether reasoning/tool-call/image capabilities are available.

## Context usage popup

{{ alta_shot "alta-desktop-context-usage.webp" "alta-context-usage.png" "Context usage with token sections and provider usage details" "The usage popup explains active context, recent usage, compaction pressure, and provider-reported limits." }}

Open it with `Ctrl+G Ctrl+U` or the context indicator of the prompt bar. The popup explains the current context denominator, active usage, compaction pressure, recent operation usage, and provider-specific usage details when available.

## Session report

Open it with `Ctrl+G Ctrl+T` or the session info icon. The report summarizes selected-session scope, provider/model state, run status, queue state, and history/session details useful for troubleshooting or handoff.

In the desktop app, `Ctrl+Alt+B` opens the session browser: a searchable table of the sessions of a project or of the global scope, with their provider, last update and message count.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-session-browser.webp" alt="CodeAlta Desktop session browser listing saved sessions of a project" loading="lazy">
  <figcaption class="small text-secondary mt-2">The session browser finds and opens any saved session.</figcaption>
</figure>

## Workspace settings

Open settings with `Ctrl+G Ctrl+W` or `/settings`.

### Desktop

The desktop app has one Settings window with a page per area: **Appearance**, **Providers**, **Models**, **Agent prompts**, **Skills**, **Plugins**, **MCP Servers**, **Configuration file**, **Application Logs** and **About**. `Ctrl+,` and the gear button of the title bar open it too.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-settings.webp" alt="CodeAlta Desktop Settings window showing the Appearance page" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Appearance page: language, theme, color scheme, project sorting and window behavior.</figcaption>
</figure>

The **Appearance** page sets:

- the UI language: **Auto**, English, Spanish, French, German, Japanese, or Simplified Chinese;
- the theme: **Dark**, **Light**, or **Auto**, which follows the operating system;
- **Darker dark theme**, for deeper backgrounds in the dark theme with the same text and accents;
- one of 13 color schemes, each with a dark, a darker and a light variant, or a color scheme of your own;
- how projects are sorted and how many recent sessions are listed per project;
- what closing the window does: ask, keep CodeAlta running in the notification area, or exit.

<div class="row g-3 my-4">
  <div class="col-md-6">
    <figure class="alta-figure mb-0">
      <img src="{{site.basepath}}/img/alta-desktop-theme-dark.webp" alt="CodeAlta Desktop in the dark theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Dark</figcaption>
    </figure>
  </div>
  <div class="col-md-6">
    <figure class="alta-figure mb-0">
      <img src="{{site.basepath}}/img/alta-desktop-theme-light.webp" alt="CodeAlta Desktop in the light theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Light</figcaption>
    </figure>
  </div>
</div>

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-themes.webp" alt="CodeAlta Desktop in six color schemes, dark and light" loading="lazy">
  <figcaption class="small text-secondary mt-2">Blueprint, Cherry, Kiwi, Plum and Orange in dark mode, and Blueberry in light mode.</figcaption>
</figure>

#### Your own color scheme

**Customize**, beside the color scheme, makes a scheme of your own from the selected one. Give it a name,
pick the theme to edit (**Light**, **Dark** or **Darker**) and choose the colors you want to change:

| Color | What it changes |
| --- | --- |
| **Background** | The window background. Panels and raised surfaces follow it and take its tint. |
| **Text** | The text. |
| **Muted text** | Secondary text, icons and borders. |
| **Accent** | Buttons, links and selections. |
| **Success**, **Warning**, **Danger** | The colors of what succeeded, needs attention or failed. |

Click a color to open the color picker of your system, or type it as `#rrggbb`. The window shows the scheme
while you edit it, and nothing is kept until you choose **Save**. Every color you leave alone keeps
following the scheme you started from, and the darker theme follows your dark theme unless you choose a
color for it. **Edit** opens a scheme of yours again, to change, duplicate or remove it.

For example, for a dark theme with a window at `#080808` and panels just above it, customize **Blueprint**,
edit **Dark** and set **Background** to `#080808`.

Your schemes are files in `~/.alta/color-schemes/`, one per scheme, that you can copy to another machine,
share, or write by hand:

```json
{
  "name": "Deep Sea",
  "base": "plum",
  "dark": {
    "background": "#0b1d2a",
    "accent": "#33ccff"
  }
}
```

`base` is the scheme it starts from (`blueprint`, `cherry`, `tomato`, `orange`, `pineapple`, `apple`,
`kiwi`, `kale`, `blueberry`, `plum`, `elderberry`, `blackberry` or `raspberry`), and `light`, `dark` and
`darker` hold the colors it changes: `background`, `text`, `muted`, `accent`, `success`, `warning` and
`danger`. A file you change appears in CodeAlta when you come back to its window; one that cannot be used
is listed on the Appearance page with the reason.

### TUI

<div class="row g-3 my-4">
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-default.png" alt="CodeAlta TUI default dark theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Default</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-blue.png" alt="CodeAlta TUI blue theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Blue</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-green.png" alt="CodeAlta TUI green theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Green</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-cherry.png" alt="CodeAlta TUI cherry theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Cherry</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-light.png" alt="CodeAlta TUI light theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Light</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-multi.png" alt="CodeAlta TUI theme selector showing multiple available themes" loading="lazy">
      <figcaption class="small text-secondary mt-2">Theme selector</figcaption>
    </figure>
  </div>
</div>

The Workspace Settings dialog covers the navigator and the UI theme, and is separate from the model-provider editor. You can also choose the UI language from **Auto**, English, Spanish, French, German, Japanese, and Simplified Chinese; language changes are saved with the workspace settings and fully apply after restarting CodeAlta.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-workspace-settings-with-language.png" alt="CodeAlta Workspace Settings dialog showing theme, recent sessions, language, and auto-approve options" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Workspace Settings dialog includes the language selector and a restart notice for fully applying language changes.</figcaption>
</figure>

### Tool permissions

Agents ask for permission before running commands or changing files. **Auto approve commands** is enabled by default in the TUI Workspace Settings: requests are approved automatically. Turn it off to review each request with **Allow Once**, **Allow for Session**, or **Deny**.

The desktop app always approves requests automatically.

> [!CAUTION]
> With automatic approval, commands and file changes run with your user privileges. They are not limited to the project folder.

## About and updates

Open it with `Ctrl+G Ctrl+A` or `/about`. It shows the current version and whether the startup update check found a newer .NET tool package.

### New version notice

When the startup update check finds a newer package on NuGet, CodeAlta shows a notice with the new version and a link to the release notes.

- **Desktop**: the notice has an **Update and restart** button. CodeAlta exits, runs `dotnet tool update -g CodeAlta`, and starts again.
- **TUI**: the notice shows the `dotnet tool update -g CodeAlta.Tui` command with a copy action. After you exit, the same command is printed so you can run it in your shell.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-release-toast.png" alt="CodeAlta TUI update-available toast showing a newer package version and a dotnet tool update command" loading="lazy">
  <figcaption class="small text-secondary mt-2">The update notice appears once per session when a newer CodeAlta package is available.</figcaption>
</figure>

### Notification area (desktop)

Closing the desktop window asks whether CodeAlta keeps running or exits. **Keep running** hides the window and leaves CodeAlta and its sessions running, with an icon in the notification area: click the icon to show the window, or use its menu to exit. Tick **Remember my choice** to stop being asked.

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-close-question.webp" alt="CodeAlta Desktop asking Keep CodeAlta running? with Cancel, Exit CodeAlta and Keep running buttons and a Remember my choice check box" loading="lazy">
  <figcaption class="small text-secondary mt-2">Enter keeps CodeAlta running, Escape leaves the window open.</figcaption>
</figure>

**Settings > Appearance > When the window is closed** changes the choice later. `Ctrl+Q` exits directly. If sessions are still running, CodeAlta asks before exiting because exiting stops them.

## Plugin management

{{ alta_shot "alta-desktop-plugins.webp" "alta-plugins.png" "Plugin management" "Plugin management lists built-in and source plugins and lets you enable or disable them." }}

Open it with `Ctrl+G Ctrl+N`, `/plugins`, or `/plugin`. The TUI dialog shows discovered global and project plugins, state, diagnostics, contributions, and actions for source or README files. The desktop **Plugins** page lists the plugins of the global and project scopes with a switch to enable or disable each one. A source plugin has a button to build and reload it while CodeAlta runs and one to edit it in the code editor; **New plugin** creates one. See [Plugins]({{site.basepath}}/docs/plugins/).

## Skills management

{{ alta_shot "alta-desktop-skills.webp" "alta-skills.png" "Skills management showing discovered skills" "Skills management shows discovered skill packages, their descriptions, and whether they are enabled." }}

Open it with `Ctrl+G Ctrl+K` or `/skills`. CodeAlta discovers Agent Skills-compatible `SKILL.md` packages from user and project locations. Skills management lets you inspect skills and their files, create a new skill, and enable or disable skills for the global `~/.alta/config.toml` or for the selected project's `.alta/config.toml`. Disabled skills remain inspectable but are not advertised to models and cannot be activated.

In the TUI, compact `G` and `P` checkboxes set the global and project state of a skill, and bulk actions can enable, disable, or invert the currently shown skills. Enabled skills can also be activated for the session when the selected provider supports injected skill context.

## MCP servers

Open it with `/mcp`, or `Ctrl+G Ctrl+Y` in the desktop app. See the [MCP plugin]({{site.basepath}}/docs/plugins/mcp/) page.

{{ alta_shot "alta-desktop-mcp.webp" "alta-plugin-mcp.png" "MCP server configuration" "MCP servers are defined globally or per project, with a local command or an HTTP endpoint." }}

## Logs viewer

{{ alta_shot "alta-desktop-logs.webp" "alta-logs.png" "Logs viewer with diagnostic output and search controls" "The logs viewer keeps startup, provider, credential, and plugin diagnostics available inside CodeAlta." }}

Open logs with `Ctrl+G Ctrl+L` or `/logs`. The log viewer shows diagnostic output from startup, supports search and level filters, and can clear the retained log buffer.

## Config recovery editor

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-config-recovery.png" alt="CodeAlta TUI configuration recovery editor with TOML validation feedback" loading="lazy">
  <figcaption class="small text-secondary mt-2">The recovery editor opens before normal startup continues, highlights TOML issues, and lets you save once the configuration is valid.</figcaption>
</figure>

If `~/.alta/config.toml` cannot be loaded at startup, CodeAlta opens a recovery editor with TOML highlighting, an error marker, and live parse feedback. Fix the file, then save to continue. Both apps have this editor.

The desktop Settings also has a **Configuration file** page to edit `~/.alta/config.toml` at any time.
