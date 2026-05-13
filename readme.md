# CodeAlta [![ci](https://github.com/xoofx/CodeAlta/actions/workflows/ci.yml/badge.svg)](https://github.com/xoofx/CodeAlta/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/CodeAlta.svg)](https://www.nuget.org/packages/CodeAlta/)

<img align="right" width="160px" height="160px" src="https://raw.githubusercontent.com/xoofx/CodeAlta/main/img/CodeAlta.png">

An agentic AI coding CLI assistant developed in .NET.

## ✨ Features

- Project catalog descriptors, checkout planning, and machine profile overrides (`CodeAlta.Catalog`)
- Scope resolution and checkout planning APIs for global/project flows
- In-memory project file usage tracking and file/folder prompt attachment discovery (`CodeAlta.Catalog`)
- Lightweight thread/session runtime primitives and file-backed default thread instruction composition (`CodeAlta.Orchestration`)
- Filesystem skill discovery for Agent Skills-compatible `SKILL.md` packages under project/user `.alta/skills/` and `.agents/skills/`, with validation, deterministic precedence, progressive disclosure, local-runtime activation events, and backend safeguards so Codex/Copilot keep managing their own native skills (`CodeAlta.Catalog`, `CodeAlta.Orchestration`, `CodeAlta.Agent`)
- Foundational plugin runtime services for trusted source plugins under `~/.alta/plugins/` and project `.alta/plugins/`, including generated root build files with plugin-root `global.json` SDK selection, default enablement unless `[plugins.<id>] enabled = false` is configured, native `dotnet build plugin.cs` support, manifest-based output reuse under `~/.alta/cache/`, transient live build progress, collectible load contexts, headless orchestration hooks for prompt processors/before-run/tools/agent events, and emergency `--no-plugins` / `--plugin-safe-mode` bypasses (`CodeAlta.Plugins`)
- In-process `alta` live tool for CodeAlta-managed sessions, exposing bounded JSONL command groups for project/session/model/provider/skill/plugin/tool inspection, session creation/control, durable prompt queueing, peer-agent messaging, plugin-contributed commands, and plugin invocation of built-in commands with provenance (`CodeAlta.LiveTool`, `CodeAlta.Orchestration`, `CodeAlta.Plugins.Abstractions`)
- Interactive terminal shell wired to providers, project/thread catalogs, plugins, skills, editor tabs, prompt attachments, and Catalog-owned project file search, with explicit shell-controller/UI-dispatcher/view-presenter seams, rich agent timelines, chat provider/model/reasoning selectors, a footer provider summary button that opens a full model-provider management dialog, compact grouped tool-call chips plus a per-run modified-files recap card with per-file `+/-` diff totals and expandable diff details, a live `ctx --` / `ctx N tok` / `ctx NN%` context-window indicator with normalized usage popup sections and backend-specific detail, and asynchronous backend probing so the TUI starts immediately even when providers are still being configured (`CodeAlta`)
- Busy-thread prompt queueing with an editable waiting list, per-entry repeat counts, immediate steer conversion where the backend supports live steering, and an `F10` clear-queue shortcut; steer requests also surface as transient pending rows at the top of the strip until the backend echoes the user prompt into the timeline, while unsupported providers such as the current local raw-API OpenAI/Anthropic/Google runtimes automatically re-queue the prompt for the next turn (`CodeAlta`)
- Configurable local raw-API agent runtimes for OpenAI-compatible providers, Anthropic, Google GenAI, experimental GitHub Copilot Direct access routed through the existing OpenAI/Anthropic local-runtime executors, and an experimental ChatGPT/Codex subscription Responses provider that uses ChatGPT OAuth rather than platform API keys; local session journals are persisted under `~/.alta/sessions/yyyy/mm/dd/<session-id>.jsonl`, model/provider switches are recorded in the journal history, and model metadata is enriched from authenticated/static catalogs plus models.dev where appropriate (`CodeAlta`, `CodeAlta.Agent.*`, `CodeAlta.Catalog`)
- Startup validates `~/.alta/config.toml` before creating providers or sessions; invalid TOML opens an 80%-size recovery editor with syntax highlighting, an error marker, live parse feedback, `Ctrl+S` Save and Continue when valid, and `Ctrl+Q` Exit (`CodeAlta`, `CodeAlta.Catalog`)
- Only one `alta` application instance can run on a machine at a time via `~/.alta/alta.lock`; a second launch exits with an error that reports the already-running alta PID because multiple instances would share thread/session state unsafely (`CodeAlta`)
- Steering now falls back to a normal send when no active run exists, and failed prompt dispatches preserve the prompt text instead of silently dropping it (`CodeAlta`)
- Thread tabs can now be closed without stopping active runs, edited per-thread prompts surface as draft state in the footer/tabs/sidebar, and unsent thread prompts are persisted under `~/.alta/saved_prompts/` so they survive app restarts (`CodeAlta`)
- In a thread tab, `F3` / `F4` jump to previous / next user or assistant messages; `Ctrl+F3` jumps to the first message and `Ctrl+F4` returns to the bottom of the latest message (`CodeAlta`)
- Press `F6` or click `Full Prompt` to open the current draft in a large prompt editor window; `Esc` or `Ctrl+Enter` closes it and keeps the edited draft (`CodeAlta`)
- Type `@` in the prompt to open a resizable project file/folder picker dialog with its own search box; accepted entries become markdown links such as `[Program.cs](src/CodeAlta/Program.cs)`, raw `@path`, `@"quoted paths"`, and optional `:line` or `:start-end` suffixes still resolve on submit, and references are dispatched as structured file/directory attachments (`CodeAlta`)
- Paste images into prompts with `Ctrl+V` when the selected model advertises image input support; pasted images open a preview/title dialog, appear as prompt attachment chips, are stored beside the session journal, and remain viewable from user messages in the timeline (`CodeAlta`)
- Press `Ctrl+E` or run `/edit` to open the same project file picker in editor mode; selected files open in reusable tabs beside thread/session tabs with TextMate syntax highlighting, line/column status, dirty markers, `Ctrl+S` save, reload prompts for on-disk changes, and close confirmation for unsaved edits (`CodeAlta`)
- Press `F1`, type `/help`, or enter `?` in the prompt to open shell command discovery; textual shell commands now include `/open`, `/edit`, `/abort`, `/compact`, `/close`, `/queue`, `/exit`, `/tab_left`, `/tab_right`, `/msg_prev`, `/msg_next`, `/msg_first`, `/msg_last`, `/go_to_sidebar`, `/go_to_prompt`, `/model`, and `/models`, while `F5` steers, `F7` delegates, and `ALT+LEFT` / `ALT+RIGHT` switch between open tabs (`CodeAlta`)
- Press `Ctrl+O`, run `/open`, or use the always-visible `+` action on the `Projects` sidebar row to open a project dialog with project-name and directory completion: rooted paths such as `/`, `C:`, `D:`, and `~` open folders, visible projects match the sidebar display names by default, and an `Include hidden` toggle opts archived projects back into completion and opening while returning focus to the prompt (`CodeAlta`)
- Press `Ctrl+G Ctrl+S` to focus the sidebar on the current selection, `Ctrl+G Ctrl+P` to focus the prompt, `Ctrl+G Ctrl+O` or `/model` to focus the provider/model selector, `Ctrl+G Ctrl+M` or the footer provider summary to open the model providers dialog, `Ctrl+G Ctrl+L` or `/models` to browse enriched provider/model metadata and select the current model, `Ctrl+G Ctrl+U` to open the context-usage popup, and `Ctrl+G Ctrl+T` or the thread info icon to open the thread report; closing these popups returns focus to the prompt editor so keyboard flow stays intact (`CodeAlta`)
- Use the `Show Logs` button in the navigator footer to open an in-app log viewer that replays diagnostic output from startup, supports Ctrl+F search, wraps by default, and can clear the retained session log buffer (`CodeAlta`)
- A temporary `AlwaysQueue` checkbox in the prompt bar lets you enqueue prompts on an idle selected thread to exercise the waiting-list UI without sending immediately (`CodeAlta`)
- Idle started threads can be compacted manually from the footer bar or with `F11`, using the thread's current provider/model/reasoning configuration; reopening an existing thread now re-resumes a dead provider session before compacting, and the timeline surfaces explicit manual compaction notices instead of dropping back to a generic busy state (`CodeAlta`)
- Codex is pinned to the SDK-generated release tag and auto-installed under `~/.alta/cache/bin/codex/<tag>/`; Copilot CLI is likewise downloaded on demand under `~/.alta/cache/bin/copilot/<version>/<platform>/` when the Copilot backend starts, with `providers.copilot.cli_path` and `providers.copilot.npm_registry` overrides for explicit binaries or mirrored npm registries
- Codex backend sessions default to no sandbox in CodeAlta, so prompts can inspect sibling projects outside the current working directory without requiring the session cwd to be moved first
- CodeAlta writes rolling diagnostic logs under `~/.alta/logs/` for chat/provider troubleshooting
- `CodeAlta --test` runs the real terminal app for a short smoke-test window and exits automatically after 10 seconds by default
- CLI entry points use generated visual `--help` / parse-error output via `XenoAtom.CommandLine`

## 📖 User Guide

For more details on how to use CodeAlta, please visit the [user guide](https://github.com/xoofx/CodeAlta/blob/main/doc/readme.md).

See also [CodeAlta live tool](doc/readme.md#alta-live-tool) for in-session `alta` command examples and [CodeAlta skills](doc/skills.md) for skill locations, validation rules, and backend behavior.

## 🪪 License

This software is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause). 

## 🤗 Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).

