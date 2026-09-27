---
title: Getting Started
---

# Getting Started

## Install

The CodeAlta terminal workspace is packaged as the .NET global tool `CodeAlta.Tui`; the installed command is `altatui`. The in-session agent tool remains named `alta`.

> [!NOTE]
> This development branch documents the terminal package rename. The commands below apply once `CodeAlta.Tui` is released; older releases used `CodeAlta` / `alta`. The replacement desktop head is still in development. Existing `~/.alta` state is not renamed or migrated by the terminal rename.

The development desktop includes an interactive workspace. Run `alta` with no options to start the
normal owned agent host for the current directory and `~/.alta`, matching the TUI default. You can
select an existing session and send a prompt immediately; the running agent appears in the live view
while persisted events appear in its timeline. The desktop acquires the same runtime lock and may
update project catalog, journal, cache and provider state. A submission may authenticate or use the
configured provider's storage/network. Plugins remain disabled in this desktop host, and command
permissions and provider input remain denied unless separately enabled through the explicit scoped
options. Desktop-owned WebView data stays under the platform-local `CodeAlta/desktop` directory, so
existing terminal versions continue to use the same compatible `.alta` data without a migration.

The current development IDE presentation stacks Projects and Sessions in one Explorer, with
Settings at the bottom of the activity rail. Explorer width is saved locally; the full-content
button hides it and can restore it. Secondary composer actions are under **More composer actions**,
while retained-request recovery remains separate. Reminders opens a modal popup without replacing
the workspace. Alta notes start closed. This is a runnable development checkpoint, not completed
visual acceptance: real FlexLayout session tabs are now a functionally/build-verified candidate
with compact status/close chrome and secondary Reopen/Refresh menus. Mounted-lifetime and
visual/browser qualification remain deferred. The native @ reference-palette candidate has
separate bounded query editing and guarded insertion into the original draft; functional/build
checks pass, but mounted focus/IME qualification remains pending. Provider switching and
large-history work remain unfinished.

To use an intentionally read-only isolated catalog copy, launch
`--data-root <new-absolute-browser-directory> --catalog-root <existing-absolute-catalog-copy> --allow-catalog-cache`
with trusted non-overlapping roots outside `.alta`. The browser shows
persisted project/session metadata and bounded event history, not live runs. Project, session and
timeline lists have dedicated scrollbars. Selecting a session accumulates up to 1,000 persisted
events in chronological order and preserves the viewport while pages load. The timeline
distinguishes messages, reasoning, tools/file changes, plans, prompt information, usage, interactions
and errors. Tool cards identify the tool and primary command/input; prompt, usage, model and secondary
event details use compact summaries with disclosure. Hover or focus a message to use its icon-only
copy action. Internal raw persistence records are not shown as empty provider cards. The bounded reader supports UTF-8
LF/CRLF journals, with a 128 KiB record limit; unsupported or oversized records are not
silently skipped. If the journal changes, restart history rather than refreshing the catalog.

The explicit catalog-only mode includes an editable session-scoped prompt draft and a bottom-left Alta
notes Markdown pane, but it remains intentionally read-only. The project picker opens projects already
in that catalog; it does not add an arbitrary folder. Use the Shortcuts dialog (`F1`) to discover
navigation, search, prompt, notes and configuration shortcuts. Agent-prompt/model/reasoning values in
the compact composer strip are current-session status, not editable selections in this development version.

The default interactive desktop provides existing-session text submission and explicit receipt
refresh/retry. A separate scoped owned-host form remains available for isolated test roots. It requires
`--allow-owned-host`, `--project-root`,
`--discovery-home`, `--instruction-root` and `--builtin-skill-root`, all with explicit existing
absolute roots (the instruction root includes the project). This broader consent permits
configuration/discovery reads, journal/provider-state writes and configured-provider
registration; submissions may authenticate or use provider storage/network. Plugins/probes stay off,
permissions are denied by default and user input is cancelled.
Receipts describe submission, not live-run completion; **Abort original Send operation** is not
general Stop-agent behavior. Send/Abort uncertainty and live-waiter exclusion survive selection
changes, with up to 256 local intents combined. Use manual receipt refresh or exact retry after the
original waiter settles; Abort-only recovery preserves unrelated composer text. Late epoch changes
disable mutations even after leaving the old selection. Reload permits receipt browsing, not recovery
of lost local text/keys. No automatic retry or rollback/run-termination guarantee is provided.
The owned composer supports Enter to send, Shift+Enter for a newline and a bounded local draft. It also
shows observed model/reasoning/agent-prompt/context state plus configured MCP runtime state. Native
lifecycle and full agent parity remain unqualified; continue using `altatui` for normal workflows.

The experimental owned mode offers **Queue text — this host only** after manual runtime refresh,
including when the observed attachment is busy. Reservation, insertion retained in this host and
execution/cleanup are separate: acceptance is not durable storage or proof of execution. Use
**Cancel this queued operation** for the original operation, not a later run; signalling cancellation
does not prove rollback or completion. Uncertain requests retain exact text, keys and targets across
selection changes for manual receipt refresh or deliberate retry. Reload loses local retry intent;
receipts can still be browsed manually, but text and keys are not reconstructed. Restart recovery is
not provided. Existing terminal prompt queues are unchanged.

Beside the owned composer, **Review retained intent** opens read-only details for local unresolved
Queue and cancellation originals. It is not a live queue list. Cancellation-only evidence contains
the original target/key, not recoverable Queue text. **Copy text** copies the literal retained Queue
text without editing the current draft or images, sending, refreshing receipts or retrying. Scope,
input, image, runtime-observation, owner-revision or modal changes retire a stale review; reopen it
for current evidence. Definitive settlement removes originals rather than creating settled-text
history. Existing retained-request disclosure and guarded refresh/cancellation/retry controls remain
available separately.

In owned-host mode, **Refresh runtime state** before using **Steer observed run**. Steering sends
text only to that recorded runtime, attachment and run; no recorded run means unavailable, and
stale or unsupported targets fail rather than becoming a send/queue or targeting later work.
It preserves the run's existing permission policy. Success means input submitted, not run completed.
An uncertain request keeps its exact text, key and target across session selection changes. Use
**Refresh submissions** to reconcile it or deliberately **Retry exact steering request**; new
observations never retarget it, and no retry happens automatically. Reload is required if the
host/runtime identity changes. Closing the panel does not cancel accepted steering.

Repeated runtime refreshes keep one frontend request and the latest explicitly pending refresh.
Live-display reconnect waits for successful cleanup of the previous observation; cleanup failure
blocks reconnect in that view. Valid late host/runtime changes disable mutation controls even after
switching sessions. Reloading the renderer does not prove old backend work stopped, and these
observations do not recover missing history, effects or lost outcomes.

Live file-change notifications invalidate the shared file-search cache even when the terminal
frontend is not open. Replaying history does not repeat this invalidation. It is best effort,
not confirmation that a file write succeeded.

**Compact observed attachment if idle** is also available after manual runtime refresh. No
recorded run or queue drain makes an attempt eligible, but the provider must still admit it
without waiting. Stale or unsupported targets fail without replacement or fallback. This
summarizes context current at admission, not the history previously displayed, and may use the
configured model/network and save context changes. It adds no permission authority. Refresh
submissions for the outcome; a busy receipt stays busy on replay. Trying again is a new explicit
action. Uncertain compaction keeps its exact attachment/key for manual reconciliation or deliberate
retry, never automatic retry or retargeting. Closing the panel does not cancel admitted work.

In owned-host mode only, `--review-owned-command-permissions` opts into manual review of supported
plain command requests. Refresh the selected session's pending commands and choose **Allow once**,
**Deny**, or **Cancel** after reviewing the complete command and directory. Unsupported permissions
remain denied; there is no session-wide approval. Commands can run with the host's privileges, and
the explicit roots are not a sandbox. Switching sessions does not cancel pending permissions or
the original decision-response wait. Use **Observe retained decision** to check that response locally,
labelled with its original session; it never resends the decision. Explicitly observe the terminal
response, then refresh for a fresh review before deciding again. A pending response cannot be replaced.
A genuinely lost response may already have been accepted: review stays disabled until renderer reload.
Reload loses the local record; manually refresh pending commands rather than inferring an outcome
from an empty list. Host restart restores no old permission authority. This experimental workflow
does not provide full permission/ask parity or proof of command execution.

Owned Desktop submissions can also produce caller-session `alta ask --stdin` questions. Under
**Pending asks**, **Refresh asks**, then **Answer original ask** to start a new text submission or
**Cancel original ask** to remove an unclaimed question without stopping a run. Requests and answers
each allow up to 8,192 UTF-16 text units in aggregate. **Observe original action** reads retained
backend evidence without resending. A timeout means uncertainty, not definite failure, and blocks
competing actions. Original actions survive panel remounts and session selection changes; reload
can read retained same-host state but does not recreate lost intent, and restart restores no authority.
This restricted workflow provides no general LiveTool dispatch, attached files or provider-input
activation. Continue using `altatui` for complete agent workflows.

In owned-host mode, the **Alta notes** pane automatically reads current durable notes for the selected
session without starting a provider and renders the complete text as Markdown. The read remains bounded
at 16,384 UTF-16 units; oversized notes are refused without truncation, and read failure is distinct
from empty notes. Switching sessions does not stop an already-started read. Editing is not provided.

For supported **nonsecret provider input**, separately add `--enable-owned-user-input` to the complete
owned-mode command. It is off by default and does not approve commands or files. **Never enter secrets**:
answers may be stored in provider tool results and history. Use **Refresh input**, explicitly answer
each prompt, then **Submit literal answers**, or **Cancel this attempt only**. Unsupported or secret
forms are cancelled rather than answered automatically.

Original decisions survive selection changes and panel remounts. Observe a terminal result locally,
acknowledge it, then refresh for a new page; uncertainty cannot be replayed. Renderer reload can list
still-pending host requests, but absence does not reveal a lost decision. Closing the application or
cancelling the original operation/run invalidates pending requests. Host restart restores no old authority.

For providers supporting run-bound review, cancelling the owning run invalidates its pending
reviews, including requests that omit a run ID. It cannot revoke an already accepted decision.
After **Refresh runtime state**, use **Signal cancellation for observed run** to target exactly
that runtime, attachment and run. This is separate from **Abort submission**. Stale, unsupported,
retiring, transitioning or draining targets fail without fallback. Refresh submissions for the
outcome: **Cancellation signalled; run completion is not confirmed.** Failure can occur after
signalling, and accepted decisions are not rolled back. An uncertain request retains its original
target and key across selection changes; reconcile manually or deliberately retry that exact
request. There is no automatic retry or retargeting. Closing the panel cancels only its wait.

Owned-host mode also shows a selected-session **live status/text window**, separate from
persisted history and submission receipts. It retains at most eight text items with explicit
truncation/eviction indicators, plus two most recently updated **reported plain tool activities**.
These show a bounded name and reported phase, not arguments/results. Started may precede permission
resolution and proves neither approval nor process launch; later phases do not establish run
completion. Missing/evicted activity is unknown. This is not a complete transcript or tool-results/
usage/interaction view. Reload recovers only the host's retained partial display, not history,
permission decisions or effects; host restart restores no prior authority.
Missing live state means not yet observed or evicted, not idle or completed. Reconnect restarts
only the observation, never a prompt; a stale host identity requires reloading the UI.
Changing selection or closing the observation does not stop the run. Catalog-only browsing
does not enable this channel.

The shared runtime retains admitted provider-event work through queue/parent
bookkeeping and shutdown joins. This is separate from the partial live display window,
not event replay. Noncooperative work can keep shutdown pending; failed runtime cleanup does
not guarantee that every outer Host/provider dependency remains alive.

> [!IMPORTANT]
> CodeAlta is currently distributed as preview `0.x` releases before the final `1.0`. Expect behavior, configuration shape, screenshots, and extension APIs to evolve between preview versions; review release notes before upgrading a workflow you depend on.

Install [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) first, then install/update CodeAlta:

```sh
dotnet tool install -g CodeAlta.Tui
```

Alternatively, use `dnx` to install, update, and run in a single command:

```sh
dnx --yes CodeAlta.Tui
```

Then launch the terminal UI:

```sh
altatui
```

CodeAlta stores user state under `~/.alta/`, including configuration, logs, cached provider state, session journals, agent prompts under `~/.alta/prompts/agents`, plugins, and skills.

### Help and startup admission

Run `altatui --help` (or `altatui -h`) or `altatui --version` with no additional arguments to print built-in information without initializing application state, logging, the terminal workspace, or plugins. Early help lists built-in options only.

All other invocations, including `--plugins-status`, plugin commands, and help combined with other arguments, acquire the shared `~/.alta/alta.lock` before mutable startup. The lock remains held through application, plugin, and logging cleanup. If admission fails, the command reports an error without starting plugins. Access-denied or other unexpected owner-inspection errors are not treated as evidence that the owner has exited; startup conservatively refuses to reclaim that lock. Close the existing instance before retrying; do not delete an active instance's lock file.

Plugin agent-event observation has a limit of 64 outstanding callbacks per activation, with no waiting queue. Events rejected because a plugin is full or closing are not delivered to it. Shutdown waits for admitted work before releasing its dependencies; a timeout or failed drain does not make those dependencies safe to release. A noncooperative plugin can therefore prevent shutdown from completing. This does not provide event replay or complete Desktop plugin parity.

## Terminal font requirement

> [!IMPORTANT]
> CodeAlta uses [Nerd Fonts](https://www.nerdfonts.com/) icons throughout the terminal UI. Install a current Nerd Font-patched font and select it in your terminal profile before using CodeAlta. Without a font that includes the required Nerd Font glyphs, icons may appear as empty boxes, question marks, or misaligned symbols.

Use the latest Nerd Fonts release when possible. Nerd Fonts v3.0.0 changed many icon code points, so older v2-era patched fonts can still render CodeAlta incorrectly even when the font name includes `NF` or `Nerd Font`.

Recommended setup:

1. Download a recent font from the official [Nerd Fonts downloads](https://www.nerdfonts.com/font-downloads) page.
2. Remove older copies of the same Nerd Font before installing the new one, especially if you have had the font installed for years. Duplicate old and new font files can cause the terminal or OS font cache to keep using the outdated glyph set.
3. Choose the updated Nerd Font family in your terminal profile, such as `CaskaydiaCove Nerd Font`. If a similarly named family such as `CaskaydiaCove NF` still shows broken tree-view icons, check for stale older copies and reinstall the current font.
4. Restart the terminal window and `altatui` after changing or reinstalling fonts.

See [Troubleshooting: glyphs or tree icons look wrong]({{site.basepath}}/docs/troubleshooting/#glyphs-or-tree-icons-look-wrong) if icons still do not display correctly.

## First launch

On first launch, CodeAlta creates a starter `~/.alta/config.toml` with common provider entries disabled. If no provider is enabled yet, the app opens the Model Providers dialog automatically.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-model-providers.png" alt="CodeAlta Model Providers dialog opened during first launch" loading="lazy">
  <figcaption class="small text-secondary mt-2">First launch routes you directly to provider setup so you can enable a provider, choose a model, and validate credentials before sending prompts.</figcaption>
</figure>

Use the dialog to:

1. Select a provider entry on the left.
2. Choose or confirm the model and reasoning effort.
3. Add credentials through an API-key environment variable, direct API key, or provider login flow.
4. Click **Test Provider** to validate credentials and model discovery.
5. Confirm the provider shows as enabled. Successful tests and subscription login flows enable it automatically.
6. Click **Save** to write `~/.alta/config.toml` and refresh the runtime.

You can reopen this dialog any time with `Ctrl+G Ctrl+R` or from the provider summary in the footer.

## Configure one provider quickly

The most common first setup is a subscription-backed provider: **Copilot** for GitHub Copilot subscriptions, or **Codex** for ChatGPT/Codex subscriptions. In the Model Providers dialog, keep or choose a model, start the browser/device login flow, then click **Save** after login completes. Successful browser/device login enables that provider automatically; a successful **Test Provider** does the same for any provider type.

Codex and Copilot credentials are stored in CodeAlta-owned state through their login flows. They are intentionally separate from OpenAI platform API keys.

If you use an API-key provider instead, set the provider's environment variable and enable that provider in the dialog or TOML. For OpenAI platform access:

> [!TIP]
> Environment variables keep API keys out of `~/.alta/config.toml` and project files. Set the variable in the same shell or profile that launches `altatui`, then restart `altatui` so the running process can see the new value.

```sh
# macOS/Linux
export CODEALTA_OPENAI_API_KEY="..."

# PowerShell
$env:CODEALTA_OPENAI_API_KEY = "..."
```

In `~/.alta/config.toml`:

```toml
[chat]
default_provider = "openai"

[providers.openai]
enabled = true
display_name = "OpenAI"
type = "openai-responses"
model = "gpt-5.5"
reasoning_effort = "high"
api_key_env = "CODEALTA_OPENAI_API_KEY"
api_url = "https://api.openai.com/v1"
```

The in-app dialog edits the same file and preserves advanced TOML values.

Other API-key providers use the same pattern with their own environment variables, such as `CODEALTA_ALIBABA_API_KEY`, `CODEALTA_AZURE_OPENAI_API_KEY`, `CODEALTA_ANTHROPIC_API_KEY`, `CODEALTA_DEEPSEEK_API_KEY`, or `CODEALTA_ZAI_API_KEY`. For Azure OpenAI, choose `type = "azure-openai"`, set `api_url` to the resource endpoint, and use the deployment name as the model.

## Send a first prompt

1. Open a project with `Ctrl+O`, `/open`, or the `+` button in the Projects sidebar row.
2. Select the **Agent:** profile and provider/model/reasoning combination in the footer if needed. The built-in **Default** agent prompt is a good starting point.
3. Type a prompt in the prompt editor.
4. Press `Enter` to send. Use `Shift+Enter` for a new line.
5. Watch the timeline for reasoning/status messages, assistant messages, tool calls, tool results, statistics, and modified-file summaries.

Agent prompts are selectable session profiles. Open the prompt manager with `Ctrl+G Ctrl+H` or `/prompt` to inspect Default, Plan mode, or your own global/project prompts. See [Agent Prompts]({{site.basepath}}/docs/prompts/) for the file layout, override rules, and custom workflow guidance.

Use **Plan** when you want CodeAlta to research and write a reviewable `.alta/plans/` file before implementation. Use **Default** when the task is already scoped enough to inspect, edit, verify, and report directly.

Useful first prompts:

```text
Summarize this repository and identify the main build/test commands.
```

```text
Inspect the failing test output below and propose the smallest safe fix.
```

## Add files to a prompt

Type `@` in the prompt editor to open the project file/folder picker. Accepted entries are inserted as Markdown links such as `[Program.cs](src/CodeAlta.Tui/Program.cs)` and are sent as structured attachments. Raw `@path`, quoted paths, and optional `:line` or `:start-end` suffixes are also recognized at send time.

In GitHub repositories, type `#` to search recent issues. The picker accepts numbers or words, matches words case-insensitively, and inserts Markdown issue links such as `[#18](https://github.com/org/repo/issues/18)`.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-file-selection.gif" alt="CodeAlta animated file selection dialog for attaching project files to a prompt" loading="lazy">
  <figcaption class="small text-secondary mt-2">Type <code>@</code>, search for files or folders, and accept entries to add them to the prompt as structured attachments.</figcaption>
</figure>

## Add images to a prompt

When the selected model supports image input, copy an image to the clipboard and press `Ctrl+V` in the prompt editor. CodeAlta opens a preview/title dialog so you can confirm the image before it is attached, then stores the image beside the session journal.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-image-prompt-copy-paste.png" alt="CodeAlta image paste dialog showing an image copied into a prompt" loading="lazy">
  <figcaption class="small text-secondary mt-2">Paste an image into the prompt, preview it, give it a title, and send it as model context when the provider/model accepts image input.</figcaption>
</figure>

> [!IMPORTANT]
> Image preview requires a terminal with inline image protocol support. Use a terminal that supports Sixel, such as Windows Terminal, or the Kitty/iTerm2 image protocols. Without that support, pasted-image previews may not render correctly even when the selected model can accept images.

## Essential shortcuts

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-command-bar-with-shortcuts.png" alt="CodeAlta command bar showing commonly used keyboard shortcuts" loading="lazy">
  <figcaption class="small text-secondary mt-2">The command bar keeps high-frequency shortcuts visible while you work, so common actions are discoverable without interrupting the prompt flow.</figcaption>
</figure>

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-help.png" alt="CodeAlta help dialog listing common shortcuts and commands" loading="lazy">
  <figcaption class="small text-secondary mt-2">Open help with <code>F1</code>, <code>/help</code>, or <code>?</code> whenever you need a reminder of workspace, provider, session, and dialog shortcuts.</figcaption>
</figure>

| Action | Shortcut or command |
| --- | --- |
| Help / command discovery | `F1`, `/help`, or `?` |
| Open project | `Ctrl+O` or `/open` |
| Open file editor | `Ctrl+E` or `/edit` |
| Open full prompt editor | `F6` |
| Focus provider/model selector | `/model` |
| Open model providers | `Ctrl+G Ctrl+R` or `/model_providers` |
| Refresh model providers | `/model_providers_refresh` |
| Manage prompts | `Ctrl+G Ctrl+H` or `/prompt` |
| Switch to next agent prompt | `Ctrl+T` or `/next_prompt` |
| Browse models | `Ctrl+G Ctrl+O` or `/models` |
| About / update status | `Ctrl+G Ctrl+A` or `/about` |
| Open workspace settings | `Ctrl+G Ctrl+W` or `/settings` |
| Open plugins | `Ctrl+G Ctrl+N` or `/plugins` |
| Open logs | `Ctrl+G Ctrl+L` or `/logs` |
| Toggle navigator | `Ctrl+G Ctrl+G` |
| Context usage popup | `Ctrl+G Ctrl+U` |
| Session report | `Ctrl+G Ctrl+T` |
| Steer a running session | `Ctrl+Enter` |
| Delegate to another session | `F7` |
| Compact idle session | `F11` |
| Clear prompt queue | `F10` |
| Previous/next user or assistant message | `F3` / `F4` |
| Switch tabs | `Ctrl+Alt+Left` / `Ctrl+Alt+Right` |

If these shortcuts do not work in Windows Terminal, see [Troubleshooting: Windows Terminal shortcuts do not reach CodeAlta]({{site.basepath}}/docs/troubleshooting/#windows-terminal-shortcuts-do-not-reach-codealta).

### Saving edited files

The terminal file editor and attached ask-file editor preserve supported Unicode encoding, byte-order marks, and existing line endings. If the file changed or was deleted on disk, saving stops and keeps your unsaved edits. The file editor offers Cancel, Reload (discarding your edits), or Overwrite; another disk change after the overwrite prompt requires confirmation again. An attached ask-file save failure also stops save-and-submit. Invalid encoded content is reported instead of silently replaced during decoding.
