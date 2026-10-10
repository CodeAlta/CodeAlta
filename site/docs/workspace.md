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

In CodeAlta Desktop, the title bar has the space switch before the zoom and the theme switch. A [space](spaces.md) is a group of projects: the window shows the projects, the sessions and the tabs of one space at a time.

Use `Ctrl+Alt+Left` and `Ctrl+Alt+Right` to move between tabs. Use `Ctrl+T` (or `/next_prompt`) to cycle the current draft/session to the next agent prompt. Use `Ctrl+G Ctrl+G` to collapse or expand the navigator, `Ctrl+G Ctrl+S` to focus the sidebar, and `Ctrl+G Ctrl+P` to return to the prompt.

> [!TIP]
> If these shortcuts do not work in Windows Terminal, see [Troubleshooting: Windows Terminal shortcuts do not reach CodeAlta]({{site.basepath}}/docs/troubleshooting/#windows-terminal-shortcuts-do-not-reach-codealta).

### Projects sidebar (desktop)

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-explorer.webp" alt="CodeAlta Desktop sidebar with the chats, favorite projects first, three open projects with their sessions, the buttons of a project row, and the spaces at its foot" loading="lazy">
  <figcaption class="small text-secondary mt-2">The chats, then favorite projects first, several projects open, the buttons of the project under the pointer, and the spaces at the foot.</figcaption>
</figure>

- **Chats**, at the top, are the sessions that belong to no project.
- Click the arrow of a project to show or hide its sessions. Several projects can stay open, and CodeAlta shows them the same way at the next start. **Collapse all** at the top closes them all.
- Point at a project to see its buttons. The star adds it to **Favorites**, at the top of the list. The next two open its [changes](#changes-desktop) and its [code editor](#code-editor), and stay visible while that tab is open. The last one opens a [terminal](#terminal-desktop).
- The `…` button of a project or a session, or a right-click, opens its menu.
- **Delete…** in the menu of a session, or `Delete` on its row, deletes the session: its history, not the files of the project. A small question beside the row asks first; press `Enter` to delete or `Esc` to keep the session.
- **Archive project…** in the menu of a project makes it read-only until you unarchive it. Its files and its sessions are kept.
- A session that has [sub-agents](sessions.md#sub-agents) lists them under it, and has an arrow of its own to show or hide them.
- A session with a bolt was started by an [automation](automations.md).
- A session with a tree mark works in a [worktree](worktrees.md).

The sidebar lists the projects of the [space](spaces.md) the window shows; the Default space lists them all. With several spaces, the foot of the sidebar has a button for each space and says what its sessions are doing.

### Search

Press `Ctrl+P` to search and run any command. In the desktop app, `Ctrl+P` or the search icon of the title bar opens one search for everything:

- the **sessions** of every project and your chats,
- the **projects**,
- the **files** of the current project,
- the **commands**, each with its slash command and its shortcut.

Type a few words and press `Enter` to open what is selected. `Tab` switches between **All**, **Sessions**, **Projects**, **Files** and **Commands**. Start with `/` to look for a command, as when you type `/` in an empty prompt. With nothing typed, the search lists your recent sessions, your projects and the most useful commands. The sessions, the projects and the files are those of the [space](spaces.md) the window shows.

{{ alta_shot "alta-desktop-search.webp" "alta-command-palette.png" "Search listing sessions, projects and files that match, or the command palette of the TUI" "One search for sessions, projects, files and commands in the desktop app. The TUI has a command palette." }}

**Search sessions…** in the menu of a project searches the sessions of that project only.

### Split panes (desktop)

In the desktop app, session tabs, the code editor and the Changes tab can be arranged in panes:

- drag a tab along the tab strip to reorder it;
- drag a tab to an edge of a pane to split the window side by side or stacked;
- drag a tab to the center of another pane to move it there;
- or use **Split right** and **Split below** in the tab menu (`…`) of a pane.

Each pane has its own timeline and prompt. Tabs are workspace-wide: sessions from different projects stay open together, and the layout is restored at the next start. Each [space](spaces.md) has its own tabs and its own layout. `Ctrl+W` closes the current tab and `Ctrl+Shift+T` reopens the last closed one.

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

### Welcome page (desktop)

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-landing.webp" alt="Welcome tab with recent sessions, a Statistics card and links to explore CodeAlta" loading="lazy">
  <figcaption class="small text-secondary mt-2">Start a session, return to recent work or explore CodeAlta from the Welcome tab.</figcaption>
</figure>

CodeAlta Desktop has a start page, in a tab titled **Welcome**. It shows:

- three buttons: **New session**, **Open a project** and **Documentation**;
- **Get started**, while something is left to set up: a model provider when none is ready, a first project when you have none;
- **Recent sessions** and **Recent projects** of the [space](spaces.md) the window shows. A click on a session opens it; a click on a project shows its new-session prompt;
- the cards of your plugins, such as the **Statistics** overview of the last seven days with a button that opens the [Statistics](plugins/statistics.md) page;
- **Explore**: the documentation, the Canvases page, Plugins, Model Providers, Settings and the keyboard shortcuts.

Open it with **Welcome** in the search, or `/landing`. It is a tab like the others: close it with `Ctrl+W`, and it is restored with your tabs when it was left open. An agent opens it with `alta landing open`.

**Documentation** opens the documentation that ships with CodeAlta, in the window.

Two switches at the bottom of the page are kept on your computer. The same two are in **Settings > Appearance**, as **Show the welcome page at startup** and **Animate the welcome page**:

{.table}
| Switch | What it does |
| --- | --- |
| **Show at startup** | The page opens in front each time CodeAlta Desktop starts. On by default. Turn it off to start on your last tab |
| **Animation** | The colored pixels at the right of the welcome move slowly. On by default. They never move when your system asks for reduced motion, or while the tab is not in front |

A plugin can pin its own card on the page: see [Cards on the welcome page](plugins/developers.md#cards-on-the-welcome-page). To remove the page, turn the **Landing page** plugin off in Settings > Plugins. The change applies the next time CodeAlta Desktop starts.

## Timeline cards

{{ alta_shot "alta-desktop-modified-files.webp" "alta-modified-files.png" "Diff of a modified file opened from the timeline" "Modified-file cards summarize changed files and diff totals. Open a file to read its diff." }}

Timeline entries are grouped so the important parts stay visible:

- assistant messages render as Markdown; in CodeAlta Desktop, a code block has a copy button in its corner;
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

### Width of the conversation (desktop)

Drag the left or the right edge of the prompt to make the conversation narrower or wider. Both edges move together, so the prompt and the timeline stay centered. Double-click an edge to take the whole width again.

The width is the same for every session. **Settings > Appearance > Width of the conversation** sets it with a slider, shows it as a percentage and resets it.

An agent can change the width of its own session when you ask it to, for example "show this conversation at 70%". That only changes how that session is shown, not your setting.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-conversation-width.webp" alt="A session whose timeline and prompt take 70% of the width, centered" loading="lazy">
  <figcaption class="small text-secondary mt-2">A conversation at 70% of the width: the timeline and the prompt stay centered.</figcaption>
</figure>

### Agent prompt, model and permissions

The agent prompt selector chooses the agent prompt profile for the current draft/session. Built-in prompts appear first, followed by global `~/.alta/prompts/agents` prompts and project `.alta/prompts/agents` prompts. Global/project prompts with the same file id override lower-precedence prompts; see [Agent Prompts]({{site.basepath}}/docs/prompts/).

In the desktop app, the agent prompt, provider, model and reasoning effort are in one picker at the left of the prompt bar. It lists the models of the provider and the reasoning efforts of the selected model. The prompt bar also shows the project folder, the git branch and the lines added and removed since the last commit. These numbers follow the changes made outside CodeAlta too. Click them to open the [changes of the project](#changes-desktop).

<figure class="alta-figure my-4" style="max-width: 38rem;">
  <img src="{{site.basepath}}/img/alta-desktop-session-config.webp" alt="CodeAlta Desktop picker for agent prompt, provider, model and reasoning effort" loading="lazy">
  <figcaption class="small text-secondary mt-2">Agent prompt, provider, model and reasoning effort for the next send.</figcaption>
</figure>

Beside that picker, a small button names the permission mode of the session and opens the list of modes:

| Mode | What the session does |
| --- | --- |
| **Ask first** | Asks you before it runs a command or changes a file. |
| **Accept edits** | Changes files without asking, and asks before it runs a command. |
| **Bypass permissions** | Runs commands and changes files without asking. |
| **Auto** ([Claude Code](model-providers.md#claude-code) only) | Claude Code decides for each request. |
| **Don't ask** (Claude Code only) | Never asks: Claude Code runs what its own settings allow and refuses the rest. |

The mode marked **Default** in the list is the one a session runs in when you choose none: the mode of
its provider when it has one, otherwise the default mode of the application, which is **Bypass
permissions** unless you change it in Settings > Permissions (see [Tool permissions](#tool-permissions)).
A mode you choose applies from the next prompt you send, without restarting the session, and stays with
the session when you reopen it. Switching the session to another provider takes it back to the default.
A new session gets the choice once it has started.

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

In CodeAlta Desktop the dialog lists the projects of the [space](spaces.md) the window shows, and a folder you add joins that space.

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
- The same button beside a path in Settings opens that file or folder. See [Files of the settings](#files-of-the-settings).

### Files

The **Files** pane lists the folders and files of the project, without those excluded by `.gitignore`. Changed files have the color and the letter of their git status.

The full path of the project folder is written under the name of the project, at the top of the pane. **Copy path** in the `…` menu beside it copies it.

- Click a file to preview it. Double-click it, or edit it, to keep its tab open.
- **New file** and **New folder** are at the top of the pane. `F2` renames, and `Delete` moves to the Recycle Bin or the Trash.
- Drag a file or a folder onto another folder to move it.

### Editing

| Action | Shortcut |
| --- | --- |
| New file | `Ctrl+N` |
| Save the file / all files | `Ctrl+S` / `Ctrl+Shift+S` |
| Select the language of the text | `Ctrl+K` `M` |
| Find / replace in the file, also with a regular expression | `Ctrl+F` / `Ctrl+H` |
| Go to a line | `Ctrl+G` |
| Next file | `Ctrl+Tab` |
| Close the file | `Ctrl+W` |
| Show the files / search in files | `Ctrl+Shift+E` / `Ctrl+Shift+F` |
| Wrap lines | `Alt+Z` |

`Ctrl+N`, or the **+** button beside the tabs, starts a new file. It is named **Untitled-1** and stays in the editor only: write in it at once, and save it when you want to keep it. `Ctrl+S` then asks for its path in the project, and creates the folders that do not exist yet.

A new file is plain text. Choose its language with `Ctrl+K` `M`, or click the language in the status bar. The same choice changes the language of any open file.

The status bar shows the path of the open file in the project. Hover over it, or over the tab of a file, to see the full path; click it to copy the full path.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-editor-new-file.webp" alt="A new untitled file in the code editor with the dialog that selects its language" loading="lazy">
  <figcaption class="small text-secondary mt-2">A new file, not saved yet, and the choice of its language.</figcaption>
</figure>

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

Pictures are shown as pictures. An SVG file opens as a drawing, and a Markdown file can be shown as a page: the buttons at the right of the file tabs switch between the preview and the text. The page shows the front matter of the file as a table, task lists (`- [ ]`, `- [x]`) as check boxes, GitHub alerts (`> [!NOTE]`) with their color, code with syntax highlighting and `mermaid` blocks as diagrams.

### Beside a session

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-editor-session.webp" alt="CodeAlta Desktop with a session on the left and a source file in the code editor on the right" loading="lazy">
  <figcaption class="small text-secondary mt-2">A session and the file it talks about, side by side.</figcaption>
</figure>

Drag the editor tab to an edge of the window to keep a file beside the session that works on it. An agent can open a file for you with `alta editor open --file <path> --line <n>`.

A link to a file in a message opens that file in the editor too (see [Sessions](sessions.md)). When the file belongs to no project, the editor opens on the folder of the file, in a tab named **Editor** and the name of that folder: you can read and edit its files as in a project, without git status. Such a tab is not restored when CodeAlta starts again.

### Editor tabs in the TUI

In CodeAlta TUI, `Ctrl+E` opens a file in an editor tab with syntax highlighting, `Ctrl+S` to save, and a confirmation before closing unsaved edits.

## Changes (desktop)

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-changes.webp" alt="CodeAlta Desktop with the Changes tab of a project: the tree of changed files, the history and a side-by-side diff" loading="lazy">
  <figcaption class="small text-secondary mt-2">The changed files of a project, its recent commits, and the diff of the selected file.</figcaption>
</figure>

The Changes tab shows what changed in the git repository of a project. Open it by clicking the `+` / `−` numbers in the prompt bar, or the changes button of a project in the sidebar. It opens beside the current tab, and you can move, split or close it like any other tab. Each project has its own Changes tab, so several can be open.

- **Worktrees**: when the project has git [worktrees](worktrees.md), they are listed above the files. Click one to see its changes. Run `/worktree` to see them all and remove the ones you no longer need.
- **Files**: the changed files as a tree or a flat list, with the lines added and removed in each file. Type in the filter to narrow the list.
- **History**: under the files. Choose **Uncommitted changes**, all the changes of the branch since its base branch, or one of the recent commits. **Load more** shows older commits.
- **Diff**: the selected file with syntax highlighting, side by side or inline. Unchanged regions are folded, and you can expand them. `Alt+Down` and `Alt+Up` jump to the next and previous change, from the list of files as well as from the diff.
- **One file or all files**: the two buttons in the header of the diff choose between **One file at a time** and **All files in one view**. See [All files in one view](#all-files-in-one-view).
- **Refresh**: the tab refreshes every five seconds while **Auto-refresh** is on. The refresh button reads the changes right away.

The `…` menu of the diff has **Hide unchanged lines**, **Ignore whitespace changes**, **Wrap lines** and **Copy path**. **Open file** opens the file in the [code editor](#code-editor).

#### All files in one view

By default the Changes tab shows the diff of one file at a time. Click **All files in one view** in the header of the diff to read all the changes in one view that scrolls, as in a pull request:

- each file has a header with its name and the lines added and removed. The header stays at the top while you scroll through the file;
- click a header to fold a file you have read. **Collapse all files** and **Expand all files** fold and unfold every file;
- click a file in the list to go to it. While you scroll, the list selects the file at the top of the view;
- the arrows in the header go to the previous and next file, and so do `Alt+Up` and `Alt+Down`;
- **Side by side**, **Inline** and the options of the `…` menu apply to every file.

A diff of more than 1000 lines is not shown whole: it takes the height of the view and scrolls by itself.

Click **One file at a time** to go back. CodeAlta remembers your choice, and **Settings > Appearance > Changes are shown** sets the same choice.

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

Open it with `Ctrl+G Ctrl+H` or `/prompt`. It lists built-in modes such as Default and Plan plus global/project custom prompts, shows which prompts are shadowed by overrides, and edits the selected prompt on the right. Agent prompt properties are `name`, `description`, `system`, and the Markdown body. System prompt files are listed with the same override rules, and only global/project override bodies can be edited. Built-in prompts are displayed for inspection but are read-only; create a global or project prompt/system prompt with the same id to override one. In the desktop app, the `</>` button of a prompt opens its file in the [code editor](#code-editor), and the path of the selected prompt is shown above its form; a built-in prompt opens read-only. Advanced prompt workflows can combine prompts with sessions, notes, reminders, asks, MCP, and skills; see [Advanced Agent Workflows]({{site.basepath}}/docs/advanced-agent-workflows/).

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

The last operation shows the tokens of the last request to the model as a bar in which each token is counted once: the input that came from the prompt cache, the input that was written to it, the rest of the input, the output and the reasoning. The whole input is given beside the title. For a Copilot session it also gives the cost of that request in AI credits. See [Prompt caching and the cost of a request](model-providers.md#prompt-caching-and-the-cost-of-a-request).

In CodeAlta Desktop, a session of Codex, Copilot or Claude Code also shows the **Subscription usage**: the limits of the plan, how much of each is used and when it starts over. See [Usage of a subscription](model-providers.md#usage-of-a-subscription).

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

The desktop app has one Settings window with a page per area: **Appearance**, **Spaces**, **Providers**, **Models**, **Agent prompts**, **Skills**, **Plugins**, **MCP Servers**, **Configuration file**, **Application Logs** and **About**. `Ctrl+,` and the gear button of the title bar open it too.

Like the other windows of the desktop app, Settings closes with `Esc`, with its close button, or with a click outside it. A window that asks you something stays open until you answer.

#### Files of the settings

Most settings are files on your disk. Each page shows where its files are, with three buttons beside every path:

| Button | What it does |
| --- | --- |
| **Edit in the code editor** | Opens the file or the folder in the [code editor](#code-editor) and leaves Settings. |
| **Copy path** | Copies the full path. |
| **Reveal in File Explorer** | Shows it in the file manager of your system (**Reveal in Finder** on macOS). |

A path that is too long is cut at its start; point at it to see it whole.

| Page | Files and folders |
| --- | --- |
| **Configuration file** | `~/.alta/config.toml`, and `.alta/config.toml` of the selected project |
| **Providers**, **Worktrees**, **Work items** | `~/.alta/config.toml`, where their choices are saved |
| **MCP Servers** | `~/.alta/mcp.json`, `.alta/mcp.json` of the project, and the files of other tools CodeAlta reads |
| **Agent prompts** | `~/.alta/prompts`, `.alta/prompts` of the project, and the file of the selected prompt |
| **Skills** | `~/.alta/skills`, `.alta/skills` of the project, and the folder of the selected skill |
| **Plugins** | `~/.alta/plugins`, `.alta/plugins` of the project, and the folder of each source plugin |
| **Appearance** | The folder of your color schemes |
Opening `~/.alta/skills` or `~/.alta/plugins` lets you work on all your skills or plugins at once. CodeAlta creates the folder if it does not exist yet. A file that does not exist yet, such as the `.alta/config.toml` of a project that has none, is listed without the button that opens it.

What you create in Settings you can remove there: a prompt, a skill, a plugin, an MCP server, a color scheme and a kind of pull request each have a red **Remove** button, which asks before it removes anything.

`config.toml` and `mcp.json` of `~/.alta` open alone in their tab: the other files of that folder are not shown there. The prompts that ship with CodeAlta open read-only.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-settings.webp" alt="CodeAlta Desktop Settings window showing the Appearance page" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Appearance page: language, theme, color scheme, project sorting and window behavior.</figcaption>
</figure>

The **Appearance** page sets:

- the UI language: **Auto**, English, Spanish, French, German, Japanese, or Simplified Chinese;
- the theme: **Dark**, **Light**, or **Auto**, which follows the operating system;
- **Darker dark theme**, for deeper backgrounds in the dark theme with the same text and accents;
- one of 13 color schemes, each with a dark, a darker and a light variant, or a color scheme of your own;
- how projects are sorted, how many recent sessions are listed per project, and how many sub-agents per session;
- the width of the conversation, as a percentage of the space of a session;
- **Ask before deleting a session** and **Ask before archiving a project**: turn one on again after you ticked **Do not ask again** in its question;
- **Show the welcome page at startup** and **Animate the welcome page**, the two switches of the [welcome page](#welcome-page-desktop);
- what closing the window does: ask, keep CodeAlta running in the notification area, or exit.

The zoom of the window is in the title bar, as a percentage before the theme switch. Click it to zoom out, to zoom in, or to go back to 100% with a click on the percentage. `Ctrl+-`, `Ctrl+=` and `Ctrl+0` do the same, and the zoom is kept for the next start.

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

In the desktop app, each session has a [permission mode](#agent-prompt-model-and-permissions). Settings >
Permissions sets the **Default mode**, for the sessions that have none of their own: **Bypass permissions**
(the default: requests are approved automatically) or **Ask first**. A change applies to what the sessions
do next; a turn you sent while its session asked keeps asking until it ends. An agent runs with the
privileges of CodeAlta: a mode decides what you are asked, it is not a sandbox.

When a session asks, the request appears on top of its prompt, with the command and its folder, or the
folder of the file change. **Allow once** has the focus: press `Enter` to allow, `2` or `Escape` to deny,
or write in the field below what the agent should do instead. Once you answered, the focus goes back to
the prompt.

A session that waits for your answer is marked in the Explorer, on its project and on its tab. When it is
not the session on screen, a message says so, with **Show** to go to it. It is also counted in the
activity of its space.

A turn that starts without you asks the same way: a sub-agent that another session drives, or the turn
that receives the answer of a sub-agent or a reminder. Its session waits until you answer, however long
that is, and the mark and the message tell you which one it is. Stopping the session ends the request.

A session in a mode that asks cannot do what would run a command without asking you: start a
[background job](sessions.md#background-jobs), type in a terminal, give an automation a command to run, or
create and build a plugin. It cannot use the tools that drive the window either, where its requests are
answered.

A session that an agent creates, such as a sub-agent, does not ask you by default, whatever the default
mode: a sub-agent that waits for an answer stops the work of the session that created it. Settings >
Permissions > **Sessions created by agents** changes that:

- **Bypass permissions** (the default): the session an agent creates does not ask. A session that asks
  can then hand a command to a session that does not.
- **Same as the session that creates them**: it asks what its creator asks, and a session cannot send a
  prompt to a session that asks less than it does.

The mode is given when the session is created, and you can change it in its prompt like any other.

A request appears on top of the prompt of its session, with the command it would run and its folder, or the
folder it would write under, and what the agent says it is for. Answer with **Allow once** or **Deny**, or
write in the last field what the agent should do instead and press Enter: the request is denied and your text
is sent to the agent. A denied request does not stop the agent, which goes on without it: to stop it, use the
Stop button of the prompt. The choices answer a moment after the request appears,
so a click meant for something else does not answer it.

When you have no prompt draft, the request takes the focus: press an arrow key to reach its choices, then
Enter answers with the chosen one; 1 and 2 answer directly, and Escape denies. A key you were typing when the
request appeared answers nothing. Nothing appears while no request
waits.

> [!CAUTION]
> With automatic approval, commands and file changes run with your user privileges. They are not limited to the project folder.

## About and updates

Open it with `Ctrl+G Ctrl+A` or `/about`. It shows the current version and whether the update check found a newer .NET tool package.

### New version notice

CodeAlta checks NuGet for a newer package when it starts. When there is one, it shows a notice with the new version and a link to the release notes. The desktop app checks again every few hours while it stays open, and when you open About, so a version published in the meantime is announced without a restart.

- **Desktop**: the notice has an **Update and restart** button. CodeAlta exits, runs `dotnet tool update -g CodeAlta`, and starts again.
- **TUI**: the notice shows the `dotnet tool update -g CodeAlta.Tui` command with a copy action. After you exit, the same command is printed so you can run it in your shell.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-release-toast.png" alt="CodeAlta TUI update-available toast showing a newer package version and a dotnet tool update command" loading="lazy">
  <figcaption class="small text-secondary mt-2">The update notice appears once per session when a newer CodeAlta package is available.</figcaption>
</figure>

### Window size and position (desktop)

CodeAlta Desktop opens its window where you left it: at the same position and size, and maximized or full screen if it was when you exited. A maximized window still goes back to the size it had before when you restore it.

A window stretched over several displays opens over them again. If the display the window was on is not connected any more, the window opens on a display that is.

### Notification area (desktop)

Closing the desktop window asks whether CodeAlta keeps running or exits. **Keep running** hides the window and leaves CodeAlta and its sessions running, with an icon in the notification area: click the icon to show the window, or use its menu to exit. On macOS, CodeAlta started as an application (the Applications folder, Launchpad, Spotlight, the Dock) stays in the Dock instead: click its Dock icon to show the window, and quit with ⌘Q. Tick **Remember my choice** to stop being asked.

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-close-question.webp" alt="CodeAlta Desktop asking Keep CodeAlta running? with Cancel, Exit CodeAlta and Keep running buttons and a Remember my choice check box" loading="lazy">
  <figcaption class="small text-secondary mt-2">Enter keeps CodeAlta running, Escape leaves the window open.</figcaption>
</figure>

**Settings > Appearance > When the window is closed** changes the choice later. `Ctrl+Q` exits directly. If sessions are still running, CodeAlta asks before exiting because exiting stops them.

## Plugin management

{{ alta_shot "alta-desktop-plugins.webp" "alta-plugins.png" "Plugin management" "Plugin management lists built-in and source plugins and lets you enable or disable them." }}

Open it with `Ctrl+G Ctrl+N`, `/plugins`, or `/plugin`. The TUI dialog shows discovered global and project plugins, state, diagnostics, contributions, and actions for source or README files. The desktop **Plugins** page lists the plugins of the global and project scopes with a switch to enable or disable each one. A source plugin has a button to build and reload it while CodeAlta runs, one to edit it in the code editor and a red one to remove it; **New plugin** creates one. See [Plugins]({{site.basepath}}/docs/plugins/).

## Skills management

{{ alta_shot "alta-desktop-skills.webp" "alta-skills.png" "Skills management showing discovered skills" "Skills management shows discovered skill packages, their descriptions, and whether they are enabled." }}

Open it with `Ctrl+G Ctrl+K` or `/skills`. CodeAlta discovers Agent Skills-compatible `SKILL.md` packages from user and project locations. Skills management lets you inspect skills and their files, create a new skill, and enable or disable skills for the global `~/.alta/config.toml` or for the selected project's `.alta/config.toml`. Disabled skills remain inspectable but are not advertised to models and cannot be activated.

A skill is a folder that can hold several files. In the desktop app, the `</>` button of a skill in the list, or **Edit** in its details, opens the folder of that skill in a [code editor](#code-editor) tab, with its files on the left and `SKILL.md` open. A built-in skill, or one that a plugin brings, has **View files** instead: its files open read-only. **New skill** opens the skill it created the same way.

The red **Remove** button of a skill, in its row or in its details, moves the folder of the skill to the trash of your system after you confirm. It is there for your own skills and for those of the project; a built-in skill, a skill of a plugin or of GitHub Copilot has none.

To work on all your own skills at once, open `~/.alta/skills` from the top of the page: the code editor shows that folder, where each skill is a folder with its `SKILL.md`. The `.alta/skills` folder of the selected project opens the same way.

When a `config.toml` does not parse, the page still lists the skills and names that file in red above the list, with the button that opens it in the code editor. Until the file is fixed, the skills it disables are shown as enabled, and a change of a switch cannot be saved to it.

In the TUI, compact `G` and `P` checkboxes set the global and project state of a skill, and bulk actions can enable, disable, or invert the currently shown skills. Enabled skills can also be activated for the session when the selected provider supports injected skill context.

## MCP servers

Open it with `/mcp`, or `Ctrl+G Ctrl+Y` in the desktop app. See the [MCP plugin]({{site.basepath}}/docs/plugins/mcp/) page.

{{ alta_shot "alta-desktop-mcp.webp" "alta-plugin-mcp.png" "MCP server configuration" "MCP servers are defined globally or per project, with a local command or an HTTP endpoint." }}

## Documentation (desktop)

CodeAlta Desktop ships this guide and shows it in a tab, so you can read it without a connection. Open it with the book icon at the top right of the window, or with `/documentation`.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-documentation.webp" alt="Shipped documentation open to Agent Prompts, with page navigation, an outline and Ask an agent" loading="lazy">
  <figcaption class="small text-secondary mt-2">Read the guide beside your work, follow its headings or ask an agent about the page.</figcaption>
</figure>

- The pages are listed on the left, in the order of the guide. **Plugins** opens with the pages of its folder.
- The headings of the page are listed on the right. Click one to go to it.
- **Search the guide** finds a text in every page and opens the page at the heading the text is under. Press `/` to go to the search.
- A link to another page of the guide opens in the tab. A link to a website opens in your browser.
- Click a picture to enlarge it, and press `Esc` to close it.
- The arrows above the page, or `Alt+Left` and `Alt+Right`, go back and forward through the pages you read.

The tab keeps its page when you close it. Like any other tab, it can be placed beside a session.

### Ask an agent

**Ask an agent**, above the page, sends a question about the page you read, or about the whole guide, to an agent. CodeAlta creates a chat with your default provider and its model, and shows it. The agent reads the guide and ends its answer with links to the pages it used. A link to a page of the guide opens that page in the Documentation tab.

Nothing is sent to a model until you send a question.

An agent can read the guide in any session with `alta documentation list`, `alta documentation search <text>` and `alta documentation read <page>`, and show you a page with `alta documentation open <page>`.

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
