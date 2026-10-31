---
title: The Prompt and the Timeline
---

# The Prompt and the Timeline

The prompt is where you write to a session; the timeline above it shows what the session does. For starting, steering, compacting and forking sessions, see [Sessions](../sessions/readme.md).

## Send and queue

{{ alta_shot "alta-desktop-prompt.webp" "alta-prompt.png" "The prompt of CodeAlta Desktop: status, Workspace and MCP chips above the editor; Agent chip, context ring, prompt-cache timer, ⋯ menu, microphone and Send below" "The inline prompt editor keeps model controls, context usage, queue state, and send actions close to your draft." }}

| Key | Action |
| --- | --- |
| `Enter` | Send; while the session works, add the prompt to its queue |
| `Shift+Enter` | New line |
| `Ctrl+Enter` (`Cmd+Enter` on macOS) | **Send now**: steer the running turn (CodeAlta TUI: `Ctrl+Enter`) |
| `F8` | Stop the running turn |
| `F6` | Open the larger prompt editor; `Esc` or `Ctrl+Enter` closes it and keeps the draft |
| `F10` | Clear the queue |
| `Alt+=` (`Ctrl+Alt+=` on macOS) | Write or edit an [equation](#equations) (CodeAlta Desktop) |

Queued prompts can be edited, repeated, sent at once to steer, or deleted. When a provider cannot steer a running turn, the prompt waits for the next turn. See [Busy sessions and queues](../sessions/readme.md#busy-sessions-and-queues).

## Above the editor

In CodeAlta Desktop, the line above the editor shows:

- **The status**: **Prompt ready**, or **Thinking · 4m 13s**, and the count of background tasks (click it to see or stop them). **N provider errors** opens the settings of failing providers.
- **The Workspace chip**: the project, the worktree (in turquoise) or branch, and the lines changed since the last commit (click them for the [changes](code.md#changes)), with buttons for the [code editor](code.md#code-editor) and a [terminal](code.md#terminals). Before a session starts, it chooses **Project folder** or **New worktree** and its base branch.
- **The MCP servers**: their status; click it to see the [servers and tools of the session](../plugins/mcp.md#servers-and-tools-of-a-session).

Click the project or the branch for the **Workspace** popover: switch the branch, copy the path or open it in your [external editor](code.md#external-editor) or file manager, **View changes**, **Pull request** (asks the session for one), **Open editor** and **Open terminal**.

<figure class="alta-figure my-4" style="max-width: 36rem;">
  <img src="{{site.basepath}}/img/alta-desktop-workspace-popover.webp" alt="The Workspace popover above the prompt of a session in a worktree: project, worktree, branch, changes and path with buttons to copy it and open it in Visual Studio Code and the file manager, then View changes, Pull request, Open editor and Open terminal" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Workspace popover of a session in a worktree: where it works, its changes, and what to do with them.</figcaption>
</figure>

## Below the editor

- **The Agent chip**, on the left: the agent prompt, the provider and model, the reasoning effort, a shield for the [permission mode](#permission-modes), and an eye when [review before commit](../reviews.md#review-before-commit) is on. Click it to change them (see [Agent and model](#agent-and-model)).
- On the right: the sub-agents at work (click for the list), the context ring with the tokens used and the window size (click for [context usage](#context-usage)), the prompt cache timer, the **⋯** menu, Stop, the [microphone](../speech.md) and Send.

The **⋯** menu holds **Session info** (a report of the session for troubleshooting or handoff), **Reminders**, [Remote Control](#remote-control), **Compact the conversation**, **Archive**, **Expand prompt editor**, **Insert equation**, **N active providers** and, while the session works, **Steer the running turn** and **Enqueue for the next turn**. A number or a dot on **⋯** says something inside needs you.

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-more-menu.webp" alt="The ⋯ menu of the prompt: Session info, Reminders, Remote Control, Compact the conversation, Archive, Expand prompt editor and the providers" loading="lazy">
  <figcaption class="small text-secondary mt-2">The ⋯ menu holds what is used less often.</figcaption>
</figure>

**Prompt cache.** For Claude Code, the Anthropic API, the Claude models of Copilot, the OpenAI API and Codex, a timer such as **37m** says how long the provider keeps the conversation in its cache: a message sent before then costs a fraction of the input price. It turns yellow in its last three minutes, then reads **expired**. Claude Code keeps the cache one hour, the Anthropic API and Copilot five minutes (one hour with that setting), the OpenAI API and Codex 30 minutes. See [Prompt caching](../model-providers/readme.md#prompt-caching).

## Agent and model

In CodeAlta Desktop, the **Agent** chip opens one picker for the agent prompt, the provider, the model, the reasoning effort, the permission mode and review before commit.

<figure class="alta-figure my-4" style="max-width: 38rem;">
  <img src="{{site.basepath}}/img/alta-desktop-session-config.webp" alt="The picker of the Agent chip: the agent prompt, provider, model, reasoning effort, Permissions and Review before commit" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Agent chip: agent prompt, provider, model, reasoning effort, permission mode and review before commit.</figcaption>
</figure>

- **Agent prompt**: built-in prompts, then yours and the project's (see [Agent Prompts](../prompts.md)). A new session starts with the agent prompt of its [space](../spaces.md), **Default** unless you changed it; a prompt you pick, such as **Plan**, is for that session only.
- **Model**: type part of a name or id in the search box and press `Enter`. [Model tiers](../model-tiers.md) come first, with the model each gives now, then [Adaptive](../model-tiers.md#adaptive-experimental), where CodeAlta chooses the model of each turn. **Settings > Models** sets how many models the list shows.
- **Reasoning effort**: the efforts of the model. For Claude Code, Copilot and the Anthropic API, a model that lists none has none. With other providers, an effort the model does not list is marked **Unverified**.
- **Model options**, for a model whose provider offers some, such as **Context** and **Fast** for Cursor's models: a list or a switch under the reasoning effort. An option belongs to the model: it applies to every session of the provider that uses this model.
- **Review before commit**: **On**, **Off**, or **Default** (the setting of **Settings > Sessions**). See [Review before commit](../reviews.md#review-before-commit).
- **Sandbox**: a profile, **Off**, or **Default** (what the settings choose for this provider, project or space). A small mark on the chip shows a sandboxed session. See [Sandbox](../sessions/sandbox.md).
- **More options**, at the end of the picker: options that are rarely changed. **Sub-sessions**, on by default, lets the agent [delegate](../sessions/delegation.md) to sessions of its own; off, it does all the work itself, is not told about sub-agents and has no tool to start them. A change applies from the next prompt. **Context size**, for a model that offers several, sets the [input limit](../model-providers/readme.md#context-and-compaction) of the session. The section opens by itself when an option is not its default, and a struck-through mark on the chip shows a session without sub-sessions.

<figure class="alta-figure my-4" style="max-width: 24rem;">
  <img src="{{site.basepath}}/img/alta-desktop-model-search.webp" alt="The model list of the picker of CodeAlta Desktop filtered by a search box" loading="lazy">
  <figcaption class="small text-secondary mt-2">Type in the search box of the model list to find a model.</figcaption>
</figure>

The picker of a new session has the same rows, except the model options: the session starts with your choices, and the next new session of the project starts from them, except **More options**, which are for that session only.

Agents use the same choices: `alta session choices <session-id>` lists the models, efforts, agent prompts, permission modes and sandboxes of a session, and `alta session send` and `alta session queue` take `--model`, `--reasoning`, `--prompt-id`, `--permission-mode` and `--sandbox`.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-system-prompt-and-user-prompt.png" alt="CodeAlta timeline showing system prompt details and an agent prompt" loading="lazy">
  <figcaption class="small text-secondary mt-2">Prompt and system-prompt details are visible in the timeline, including the selected agent prompt and source path, so you can review what context was sent.</figcaption>
</figure>

## Permission modes

The **Permissions** row of the picker sets what the session asks before it acts. The shield on the chip is green for **Ask first**, blue for **Accept edits** and **Auto**, yellow for the modes that ask nothing.

| Mode | What the session does |
| --- | --- |
| **Ask first** | Asks before it runs a command or changes a file |
| **Accept edits** | Changes files without asking; asks before commands |
| **Bypass permissions** | Runs commands and changes files without asking |
| **Auto** (Claude Code only) | Claude Code decides for each request |
| **Don't ask** (Claude Code only) | Runs what Claude Code's own settings allow and refuses the rest |

- **Default** is the mode of the provider, or else that of **Settings > Permissions** (see [Tool permissions](settings.md#tool-permissions)).
- A mode applies at once, also to a running turn and to a request that waits. It stays with the session until you switch provider.
- An agent sets the mode of a session it creates with `alta session create --permission-mode <mode>` (`default`, `acceptEdits`, `bypassPermissions`, and for Claude Code `auto` and `dontAsk`), never one that asks less than its own.

## Attachments

{{ alta_shot "alta-desktop-file-selection.webp" "alta-file-selection.gif" "File picker for attaching project files to a prompt" "The <code>@</code> picker searches project files and folders, then inserts accepted entries as structured prompt attachments." }}

Type `@` in the prompt to search the files and folders of the project. An accepted entry becomes a Markdown link and is sent as an attachment. You can also type references, with an optional line range:

```text
@src/CodeAlta.Tui/Program.cs
@"src/path with spaces/file.cs":10-40
```

`Esc` closes the picker for that `@`; put the caret right after it and press `Ctrl+Space` to open it again. The `#` issue picker works the same way. `Ctrl+E` or `/edit` opens the same picker to edit a file in the [code editor](code.md#code-editor).

> [!NOTE]
> Attach only what the task needs: focused context is easier to review and compacts less often.

## Equations

CodeAlta Desktop shows LaTeX math in messages and documents: `$…$` or `\(…\)` in a line of text, `$$…$$`, `\[…\]` or a `math` code block on lines of their own, and the `equation` and `align` environments. Chemistry is written `\ce{2H2 + O2 -> 2H2O}`. A price such as `$5 and $10` stays text. A formula that cannot be read shows its LaTeX in red, with the reason under the pointer.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-math-message.webp" alt="An answer in the timeline of CodeAlta Desktop with a displayed fraction, inline formulas, the formation of water written in chemistry, and a footnote at the end" loading="lazy">
  <figcaption class="small text-secondary mt-2">An answer with formulas, a chemical equation and a footnote, drawn as the model writes them.</figcaption>
</figure>

- **Copy and source**: a selection that holds formulas copies their LaTeX. A displayed equation has **LaTeX** and **MathML** buttons under the pointer (MathML pastes into Word as an equation), and **Source**, which shows its LaTeX in its place; a diagram has **Source** too.
- **In the prompt** and in the Markdown files of the [code editor](code.md#code-editor): formulas are colored as you type, and the one that holds the caret is drawn above it. Its **✎** button opens it in the equation editor.
- **Equation editor**: `Alt+=` (`Ctrl+Alt+=` on macOS), `/equation` or **Insert equation** in the **⋯** menu. Type the formula as it looks (`/` makes a fraction, `^` a superscript, `sqrt` a root, `alpha` a Greek letter) or open its keyboard of symbols; its LaTeX is kept below it, and can be edited there. Choose **Inline** or **Display**, then **Insert** (`Ctrl+Enter`). With the caret in a formula, the editor opens on it and **Replace** writes it back.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-equation-editor.webp" alt="The Edit equation window of CodeAlta Desktop over a session: the formula drawn in its field, its LaTeX below, Inline or Display, and Replace; in the prompt below, the formula is colored and drawn above the caret with its edit button" loading="lazy">
  <figcaption class="small text-secondary mt-2">The equation editor opened with <code>Alt+=</code> on a formula of the prompt, which is drawn above the caret as it is written.</figcaption>
</figure>

Footnotes are written `[^1]` with their note `[^1]: The source.`, or `^[a note]` in the text: the number goes to the note, and **↩** back.

CodeAlta TUI shows formulas and footnotes as written.

## Remote Control

A Claude Code session can be followed and driven from claude.ai or the Claude app on your phone. Turn it on with **Remote Control** in the **⋯** menu of the prompt or in the menu of the session. Its icon is green when on and red when failed. Its popover has the link of the session, a QR code for your phone, and **Turn off**.

<figure class="alta-figure my-4" style="max-width: 36rem;">
  <img src="{{site.basepath}}/img/alta-desktop-remote-control.webp" alt="The popover of Remote Control, opened from the ⋯ menu of the prompt: Remote Control is off, the popover explains how to follow a Claude Code session and offers Turn on" loading="lazy">
  <figcaption class="small text-secondary mt-2">Remote Control, in the <strong>⋯</strong> menu of the prompt, is off until you turn it on for the session.</figcaption>
</figure>

While it is on, a prompt sent from claude.ai runs as yours, a permission request is asked on both sides (the first answer wins), and stopping there stops the session; questions of the agent are asked in CodeAlta only. If Claude Code stops, your next prompt or **Try again** connects again.

Remote Control needs Claude Code signed in with your claude.ai account, not an API key. Closing CodeAlta ends it, and the session stays offline in claude.ai until you archive it there. **Settings > General > Startup and closing** can turn it back on at startup.

## Timeline

{{ alta_shot "alta-desktop-modified-files.webp" "alta-modified-files.png" "Diff of a modified file opened from the timeline" "Modified-file cards summarize changed files and diff totals. Open a file to read its diff." }}

The timeline shows:

- messages in Markdown; in CodeAlta Desktop a code block has a copy button, a `mermaid` block is drawn as a diagram (or stays code, with the reason, when it cannot be read), and [formulas and footnotes](#equations) are drawn. A picture on disk that a message shows (`![chart](out/chart.png)`, a PNG, JPEG, GIF, WebP or BMP file) is drawn in the message: click it to open it in a window with **Copy image**, its path with **Copy path**, and a button that shows the file in its folder. `Ctrl`+click (`Cmd`+click on macOS) opens the file in the code editor;
- reasoning and status messages;
- [tool calls](#tool-calls) as compact tiles;
- the modified-files card, with the lines added and removed per file and their diffs;
- statistics cards: timing, tools, usage and plugin details.

| Key | Action |
| --- | --- |
| `F3` / `F4` | Previous / next message |
| `Ctrl+F3` / `Ctrl+F4` | First message / back to the bottom |
| Arrows, `Page Up`, `Page Down`, `Home`, `End` | Scroll, after a click in the timeline (desktop) |

CodeAlta Desktop opens a session on its latest messages; earlier turns load as you scroll up. **Load all messages**, at the top, loads them all; **Stop** keeps what was loaded.

A very long message shows its start, with a line that says the rest is too long to show there: **Show the whole message** reads the rest.

### Focused timeline

In CodeAlta Desktop, the work between two messages (reasoning, tool calls, jobs) takes one line, with the latest thought of the model and counts of thoughts, calls, failures, lines changed and time. While the agent works, it shows the last three steps. Images the model read show under it as thumbnails. Click the line to unfold it. **Settings > Appearance** turns off **Focused timeline** and **Images under folded lines**.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-focused-timeline.webp" alt="A CodeAlta session in CodeAlta Desktop: the work of each turn folded into one line under the latest thought of the model, with its counts and duration, between the prompts and the answers; the work that goes on shows its last three tool calls under its line" loading="lazy">
  <figcaption class="small text-secondary mt-2">The work between two messages takes one line; the work that goes on shows its last steps.</figcaption>
</figure>

### Entry appearance

**Settings > Appearance > Entry appearance** (off by default) dims the entries you read less: its preset shows intermediate answers and tool calls at 70%, and reasoning, system prompts, status and plugin cards at 50%, at full strength under the pointer. Errors and requests that need you are never dimmed. **Customize** sets the opacity, tint, border and shadow of each entry type; **Reset all** restores the preset.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-entry-appearance.webp" alt="The Entry appearance window of CodeAlta Desktop: the settings of the intermediate answers and a preview of their two states" loading="lazy">
  <figcaption class="small text-secondary mt-2">Each type of entry has its own opacity, tint, border and shadow, with a preview of both states.</figcaption>
</figure>

### Conversation width

In CodeAlta Desktop, drag an edge of the prompt to narrow or widen the conversation, which stays centered; double-click it for the whole width. The width, 75% by default, is the same for every session (**Settings > Appearance > Width of the conversation**). An agent you ask ("show this conversation at 70%") changes its own session only.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-conversation-width.webp" alt="A session whose timeline and prompt take 70% of the width, centered" loading="lazy">
  <figcaption class="small text-secondary mt-2">A conversation at 70% of the width: the timeline and the prompt stay centered.</figcaption>
</figure>

## Tool calls

{{ alta_shot "alta-desktop-tool-details.webp" "alta-tool-input-output-dialog.png" "Details of a tool call opened from the timeline" "Open a tool call to follow a command while it runs, or to read the file or the diff it worked on." }}

A tool call has one tile showing the tool, what it works on, its state and its output; a running command shows its last line. In CodeAlta Desktop, click a tile to open the call, which follows it while it runs:

- **a command**: its command line and a terminal with its output;
- **a file read**: its lines, numbered and colored;
- **an edit**: the diff, with a tab per file for a patch;
- **a search**: its matches by file; **another tool**: its arguments and result.

The line under the title gives the duration, the exit code and the lines changed. **Details** shows the raw arguments and result. In CodeAlta TUI, a tile opens a dialog with the details and output.

The agent gets a bounded part of a long result, which stays in every later request:

- **a command** that prints more than 30,000 characters: the start and the end of its output, and the path of a file of the session that holds all of it, for the agent to read;
- **a file read**: lines cut at 2,000 characters, and at most 40,000 characters, with the line to read on from; a read of lines the agent already has, of a file unchanged since, gets a short note that points to that earlier read, until the conversation is compacted;
- **a search**: matching lines cut at 500 characters around the match, and at most 30,000 characters.

The window of a command still shows you its whole output.

## System prompts

The timeline has a line for the system prompt of the session, and one each time it changes, with the agent prompt, its size in tokens and the lines changed. In CodeAlta Desktop, click it: **Changes** compares it with the one before, and **Prompt** shows what was sent, with a button to copy it as Markdown.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-system-prompt.webp" alt="The System prompt window of CodeAlta Desktop comparing the Plan prompt of a session with the Default prompt it replaced" loading="lazy">
  <figcaption class="small text-secondary mt-2">A system prompt that changed, compared with the one before.</figcaption>
</figure>

## Session notes

Each session has Markdown notes its agent keeps up to date: a checklist, a status, next actions. CodeAlta Desktop shows them in a **Notes** window over the timeline that you can move, resize, collapse into a tab, copy and clear; `Ctrl+Shift+N` or `/notes` hides or shows them. CodeAlta TUI shows them in the **Notes** panel of the sidebar.

{{ alta_shot "alta-desktop-notes.webp" "alta-notes.png" "Session notes with a Markdown checklist" "Notes belong to the session and stay visible while the agent works." }}

## Context usage

{{ alta_shot "alta-desktop-context-usage.webp" "alta-context-usage.png" "Context usage with token sections and provider usage details" "The usage popup explains active context, recent usage, compaction pressure, and provider-reported limits." }}

Open it with the context ring below the prompt, or `Ctrl+G Ctrl+U`. It shows the size of the context window, what is used, how close the session is to compaction, and what the provider reports.

- **Last operation**: the tokens of the last request (cache reads, cache writes, other input, output, reasoning), with its cost in AI credits for Copilot. See [Prompt caching](../model-providers/readme.md#prompt-caching).
- **Current session** (desktop): the same for the whole session, with the time and the cost; a fork counts from the fork. **Copy as Markdown** copies it.
- **Subscription usage** (desktop; Codex, Copilot and Claude Code): the limits of your plan and when they reset. See [Usage of a subscription](../model-providers/readme.md#subscription-usage).

When the provider reports no token usage, as some [ACP agents](../model-providers/acp-agents.md#limitations) do, CodeAlta estimates the context from the conversation: the count starts with `~` and the window says **Estimated**.

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-current-session-costs.webp" alt="Context usage popup of CodeAlta Desktop with the Current session card under the last operation: a token bar, the duration and the cost of the whole session" loading="lazy">
  <figcaption class="small text-secondary mt-2">Current session adds up the tokens, the duration and the cost of every request of the session.</figcaption>
</figure>

**Session info**, in the **⋯** menu (or `Ctrl+G Ctrl+T`), reports the scope, provider and model, run and queue state and history of the session, for troubleshooting or a handoff.
