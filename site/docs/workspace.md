---
title: Workspace and Dialogs
---

# Workspace and Dialogs

CodeAlta is designed for keyboard-first work in a terminal UI. Most popups close back to the prompt editor so you can keep typing without rebuilding context.

## Main workspace

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-home.png" alt="CodeAlta main workspace with projects sidebar, session tab, timeline, prompt editor, and provider footer" loading="lazy">
  <figcaption class="small text-secondary mt-2">The default workspace keeps navigation, the active session, prompt drafting, provider selection, and context status visible together.</figcaption>
</figure>

The main screen has four important areas:

- **Navigator/sidebar**: projects, open sessions, running work, and navigator actions.
- **Workspace tabs**: session tabs and editor tabs share the same tab strip.
- **Timeline**: user messages, assistant messages, reasoning/status updates, tool-call chips, results, statistics cards, compaction notices, and modified-file summaries.
- **Prompt/footer**: prompt editor, queue strip, **Agent:** profile selector, provider/model/reasoning selectors, context usage, compact button, and status text.

> [!TIP]
> Press `F1`, type `/help`, or type `?` when you are unsure where an action lives. Help and command discovery are designed to return you to the prompt quickly.

Use `Ctrl+Alt+Left` and `Ctrl+Alt+Right` to move between tabs. Use `Ctrl+G Ctrl+G` to collapse or expand the navigator, `Ctrl+G Ctrl+S` to focus the sidebar, and `Ctrl+G Ctrl+P` to return to the prompt.

> [!TIP]
> If these shortcuts do not work in Windows Terminal, see [Troubleshooting: Windows Terminal shortcuts do not reach CodeAlta]({{site.basepath}}/docs/troubleshooting/#windows-terminal-shortcuts-do-not-reach-codealta).

## Timeline cards

<div class="row g-3 my-4">
  <div class="col-lg-4">
    <figure class="h-100">
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-modified-files.png" alt="CodeAlta modified-files timeline card summarizing file changes" loading="lazy">
      <figcaption class="small text-secondary mt-2">Modified-file cards summarize changed files and diff totals.</figcaption>
    </figure>
  </div>
  <div class="col-lg-4">
    <figure class="h-100">
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-tool-input-output-dialog.png" alt="CodeAlta tool input and output dialog opened from a timeline tool call" loading="lazy">
      <figcaption class="small text-secondary mt-2">Tool inputs and outputs stay inspectable in expandable dialogs.</figcaption>
    </figure>
  </div>
  <div class="col-lg-4">
    <figure class="h-100">
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-plugin-statistics.png" alt="CodeAlta plugin statistics card in the timeline" loading="lazy">
      <figcaption class="small text-secondary mt-2">Timeline projections can include plugin-provided statistics.</figcaption>
    </figure>
  </div>
</div>

Timeline entries are grouped so the important parts stay visible:

- assistant messages render as Markdown;
- reasoning and status messages explain what the agent is doing;
- tool calls appear as compact chips with expandable details;
- tool results can be collapsed to avoid flooding the transcript;
- the modified-files card summarizes per-file `+/-` diff totals and can show diff details;
- statistics cards summarize timing, tools, usage, and other plugin-projected details.

Use `F3` / `F4` to jump between previous and next user or assistant messages. Use `Ctrl+F3` to jump to the first message and `Ctrl+F4` to return to the bottom.

## Prompt editor and prompt queue

<div class="row g-3 my-4">
  <div class="col-lg-6">
    <figure class="h-100">
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-prompt.png" alt="CodeAlta inline prompt editor with model controls, context usage, and send actions" loading="lazy">
      <figcaption class="small text-secondary mt-2">The inline prompt editor keeps model controls, context usage, queue state, and send actions close to your draft.</figcaption>
    </figure>
  </div>
  <div class="col-lg-6">
    <figure class="h-100">
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-prompt-fullscreen.png" alt="CodeAlta full-screen prompt editor for longer multi-line prompts" loading="lazy">
      <figcaption class="small text-secondary mt-2">Use the full-screen prompt editor for longer multi-line prompts without leaving the selected session.</figcaption>
    </figure>
  </div>
</div>

Press `Enter` to send and `Shift+Enter` for a new line. If the selected session is busy, `Enter` adds the prompt to the waiting list instead of dropping it. Queued prompts can be edited, repeated, steered immediately when supported, deleted, or cleared with `F10`.

The footer **Agent:** selector chooses the agent prompt profile for the current draft/session. Built-in prompts appear first, followed by global `~/.alta/prompts/agents` prompts and project `.alta/prompts/agents` prompts. Global/project prompts with the same file id override lower-precedence prompts; see [Prompts and Instructions]({{site.basepath}}/docs/prompts/).

`Ctrl+Enter` steers a running provider session. If the provider cannot steer live, CodeAlta re-queues the prompt for the next normal turn.

Use `F6` or the **Full Prompt** action to open a larger prompt editor. `Enter`, `Esc`, or `Ctrl+Enter` closes it and keeps your draft.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-system-prompt-and-user-prompt.png" alt="CodeAlta timeline showing system prompt details and an agent prompt" loading="lazy">
  <figcaption class="small text-secondary mt-2">Prompt and system-prompt details are visible in the timeline so you can review what context was sent.</figcaption>
</figure>

## Open Project dialog

Open it with `Ctrl+O`, `/open`, or the `+` action on the Projects sidebar row.

The dialog supports project-name and directory completion. Rooted paths such as `/`, `C:`, `D:`, and `~` open folders. The **Include hidden** toggle includes archived/hidden projects in completion.

## File/folder picker and prompt attachments

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-file-selection.gif" alt="CodeAlta animated file selection dialog for attaching project files to a prompt" loading="lazy">
  <figcaption class="small text-secondary mt-2">The <code>@</code> picker searches project files and folders, then inserts accepted entries as structured prompt attachments.</figcaption>
</figure>

Type `@` in the prompt to search project files and folders. Accepted entries become Markdown links and are sent as structured attachments. You can also type raw references such as:

> [!NOTE]
> Attach only the files or folders that are relevant to the task. Smaller, focused context usually makes provider responses easier to review and keeps compaction pressure lower.

```text
@src/CodeAlta/Program.cs
@"src/path with spaces/file.cs":10-40
```

Use `Ctrl+E` or `/edit` to open the same picker in editor mode.

## Editor tabs

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-code-editor.png" alt="CodeAlta editor tab with syntax-highlighted source code" loading="lazy">
  <figcaption class="small text-secondary mt-2">Editor tabs sit beside session tabs for quick inspection and focused edits without leaving the terminal UI.</figcaption>
</figure>

Editor tabs support TextMate syntax highlighting, line/column status, dirty markers, `Ctrl+S` save, reload prompts for on-disk changes, and close confirmation for unsaved edits. Editor tabs sit beside session tabs so you can inspect files without leaving CodeAlta.

## Model Providers dialog

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-model-providers.png" alt="CodeAlta Model Providers dialog with provider configuration and validation controls" loading="lazy">
  <figcaption class="small text-secondary mt-2">Provider setup, endpoint details, credential options, and tests live in one dialog.</figcaption>
</figure>

Open it with `Ctrl+G Ctrl+R`, `/model_providers`, or the provider summary. Use it to enable providers, enter credentials, test endpoints, handle Codex/Copilot login flows, refresh saved provider availability, and edit advanced TOML safely.

## Prompt manager

Open it with `Ctrl+G Ctrl+H` or `/prompt`. The dialog opens on **Agent Prompts**, which lists built-in, global, and project agent prompts, shows which prompts are shadowed by overrides, and edits the selected prompt on the right. Agent prompt properties are `name`, `description`, `system`, and the Markdown body. The **System Prompt** tab lists system prompt files with the same override rules and lets you edit only global/project override bodies. Built-in prompts are displayed for inspection but are read-only; create a global or project prompt/system prompt with the same id to override one.

## Model browser

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-models.png" alt="CodeAlta model browser showing provider models, model references, and capability metadata" loading="lazy">
  <figcaption class="small text-secondary mt-2">The model browser lists available provider models, copyable model refs, reasoning settings, and capability metadata.</figcaption>
</figure>

Open it with `Ctrl+G Ctrl+O` or `/models`. It shows provider/model metadata and copyable model refs such as `codex:gpt-5.5@high`. Use it to select the current model and verify whether reasoning/tool-call/image capabilities are available.

## Context usage popup

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-context-usage.png" alt="CodeAlta context usage popup with token sections and provider usage details" loading="lazy">
  <figcaption class="small text-secondary mt-2">The usage popup explains active context, recent usage, compaction pressure, and provider-reported limits.</figcaption>
</figure>

Open it with `Ctrl+G Ctrl+U` or the footer context indicator. The popup explains the current context denominator, active usage, compaction pressure, recent operation usage, and provider-specific usage details when available.

## Session report

Open it with `Ctrl+G Ctrl+T` or the session info icon. The report summarizes selected-session scope, provider/model state, run status, queue state, and history/session details useful for troubleshooting or handoff.

## Workspace settings

<div class="row g-3 my-4">
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-default.png" alt="CodeAlta default dark theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Default</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-blue.png" alt="CodeAlta blue theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Blue</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-green.png" alt="CodeAlta green theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Green</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-cherry.png" alt="CodeAlta cherry theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Cherry</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-light.png" alt="CodeAlta light theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Light</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-multi.png" alt="CodeAlta theme selector showing multiple available themes" loading="lazy">
      <figcaption class="small text-secondary mt-2">Theme selector</figcaption>
    </figure>
  </div>
</div>

Open it with `Ctrl+G Ctrl+W` or `/settings`. Workspace settings cover the selected workspace/project behavior and are separate from the model-provider editor.

## About and updates

Open it with `Ctrl+G Ctrl+A` or `/about`. The dialog shows the animated CodeAlta logo, current version, copyright, and whether the startup update check found a newer .NET tool package.

### New version toast

When the startup update check finds a newer CodeAlta package on NuGet, CodeAlta shows a non-blocking toast during the session. The toast includes the latest version and the matching `dotnet tool update` command, with a copy action for the command. CodeAlta does not update itself automatically; after you exit the terminal UI, it also prints the same command so you can run it in your shell.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-release-toast.png" alt="CodeAlta update-available toast showing a newer package version and a dotnet tool update command" loading="lazy">
  <figcaption class="small text-secondary mt-2">The update toast appears once per session when a newer CodeAlta package is available.</figcaption>
</figure>

## Plugin management

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-plugins.png" alt="CodeAlta plugin management dialog with diagnostics and contributions" loading="lazy">
  <figcaption class="small text-secondary mt-2">Plugin management exposes state, diagnostics, contributions, and source/README actions for trusted local plugins.</figcaption>
</figure>

Open it with `Ctrl+G Ctrl+N`, `/plugins`, or `/plugin`. The dialog shows discovered global and project plugins, state, diagnostics, contributions, and actions for source or README files.

## Skills management

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-skills.png" alt="CodeAlta skills management dialog showing discovered skills and activation details" loading="lazy">
  <figcaption class="small text-secondary mt-2">Skills management shows discovered skill packages, descriptions, and activation state for providers that support injected skill context.</figcaption>
</figure>

CodeAlta discovers Agent Skills-compatible `SKILL.md` packages from user and project locations. The skills dialog lets you inspect skills, open related files, activate enabled skills when the selected provider supports injected skill context, and manage enablement. Compact `G` and `P` checkboxes disable or enable a skill in global `~/.alta/config.toml` or the selected project's `.alta/config.toml`; bulk actions can enable, disable, or invert the currently shown skills for Global, Project, or Both scopes. Disabled skills remain inspectable in the dialog but are not advertised to models and cannot be activated.

## Logs viewer

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-logs.png" alt="CodeAlta logs viewer with diagnostic output and search controls" loading="lazy">
  <figcaption class="small text-secondary mt-2">The logs viewer keeps startup, provider, credential, and plugin diagnostics available inside the terminal UI.</figcaption>
</figure>

Open logs with `Ctrl+G Ctrl+L`, `/logs`, or the navigator footer. The log viewer replays diagnostic output from startup, supports search, wraps by default, and can clear the retained session log buffer.

## Config recovery editor

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-config-recovery.png" alt="CodeAlta configuration recovery editor with TOML validation feedback" loading="lazy">
  <figcaption class="small text-secondary mt-2">The recovery editor opens before normal startup continues, highlights TOML issues, and lets you save once the configuration is valid.</figcaption>
</figure>

If `~/.alta/config.toml` is invalid at startup, CodeAlta opens a recovery editor with TOML highlighting, an error marker, live parse feedback, `Ctrl+S` Save and Continue when valid, and `Ctrl+Q` Exit.
