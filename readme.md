# CodeAlta [![ci](https://github.com/xoofx/CodeAlta/actions/workflows/ci.yml/badge.svg)](https://github.com/xoofx/CodeAlta/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/CodeAlta.svg)](https://www.nuget.org/packages/CodeAlta/)

<img align="right" width="160px" height="160px" src="https://raw.githubusercontent.com/xoofx/CodeAlta/main/img/CodeAlta.png">

An agentic AI coding CLI assistant developed in .NET.

## ✨ Features

- Project catalog descriptors, checkout planning, and machine profile overrides (`CodeAlta.Catalog`)
- Scope resolution and checkout planning APIs for global/project flows
- SQLite-backed durable state + migrations + repositories (`CodeAlta.Persistence`)
- Markdown artifact store with YAML frontmatter parsing and plain-text extraction
- FTS5 + embedding-backed hybrid search pipeline (`CodeAlta.Search`)
- In-process MCP server surface with tools for tasks, artifacts, search, projects, and agents (`CodeAlta.Mcp`)
- Agent orchestration services for role profiles, context packs, and planner/builder durable workflows (`CodeAlta.Orchestration`)
- .NET first-class services for solution/project discovery, symbol indexing, context snippets, diagnostics, and index refresh (`CodeAlta.DotNet`)
- Interactive terminal shell wired to project/task/search/.NET/MCP services, with explicit shell-controller/UI-dispatcher/view-presenter seams, rich agent timelines, chat backend/model/reasoning selectors, compact grouped tool-call chips plus a per-run modified-files recap card with per-file `+/-` diff totals and expandable diff details, a live `ctx --` / `ctx NN%` context-window indicator with normalized usage popup sections and backend-specific detail, and asynchronous Codex/Copilot probing so the TUI starts immediately even when a backend is missing (`CodeAlta`)
- Busy-thread prompt queueing with an editable waiting list, per-entry repeat counts, immediate steer conversion, and an `F10` clear-queue shortcut; steer requests also surface as transient pending rows at the top of the strip until the backend echoes the user prompt into the timeline (`CodeAlta`)
- Steering now falls back to a normal send when no active run exists, and failed prompt dispatches preserve the prompt text instead of silently dropping it (`CodeAlta`)
- Thread tabs can now be closed without stopping active runs, edited per-thread prompts surface as draft state in the footer/tabs/sidebar, and unsent thread prompts are persisted under `~/.codealta/machine/saved_prompts/` so they survive app restarts (`CodeAlta`)
- Press `F6` or click `Full Prompt` to open the current draft in a large prompt editor window; `Esc` closes it and keeps the edited draft (`CodeAlta`)
- Type `@` in the prompt to open a resizable project file/folder picker dialog with its own search box; accepted entries become markdown links such as `[Program.cs](src/CodeAlta/Program.cs)`, raw `@path`, `@"quoted paths"`, and optional `:line` or `:start-end` suffixes still resolve on submit, and references are dispatched as structured file/directory attachments (`CodeAlta`)
- Press `F1`, type `/help`, or enter `?` in the prompt to open shell command discovery; textual shell commands now include `/open`, `/abort`, `/compact`, `/close`, `/queue`, `/exit`, `/tab_left`, `/tab_right`, `/go_to_sidebar`, and `/go_to_prompt`, while `F5` steers, `F7` delegates, and `ALT+LEFT` / `ALT+RIGHT` switch between open tabs (`CodeAlta`)
- Press `Ctrl+O`, run `/open`, or use the always-visible `+` action on the `Projects` sidebar row to open a project dialog with project-name and directory completion: rooted paths such as `/`, `C:`, `D:`, and `~` open folders, visible projects match the sidebar display names by default, and an `Include hidden` toggle opts archived projects back into completion and opening while returning focus to the prompt (`CodeAlta`)
- Press `Ctrl+G Ctrl+S` to focus the sidebar on the current selection, `Ctrl+G Ctrl+P` to focus the prompt, `Ctrl+G Ctrl+U` to open the context-usage popup, and `Ctrl+G Ctrl+T` or the thread info icon to open the thread report; closing either popup returns focus to the prompt editor so keyboard flow stays intact (`CodeAlta`)
- A temporary `AlwaysQueue` checkbox beside `AutoScroll` lets you enqueue prompts on an idle selected thread to exercise the waiting-list UI without sending immediately (`CodeAlta`)
- Idle started threads can be compacted manually from the footer bar or with `F11`, using the thread's current backend/model/reasoning configuration; reopening an existing thread now re-resumes a dead backend session before compacting, and the timeline surfaces explicit manual compaction notices instead of dropping back to a generic busy state (`CodeAlta`)
- Codex backend sessions default to no sandbox in CodeAlta, so prompts can inspect sibling projects outside the current working directory without requiring the session cwd to be moved first
- CodeAlta writes rolling diagnostic logs under `~/.codealta/logs/` for chat/backend troubleshooting
- `CodeAlta --test` runs the real terminal app for a short smoke-test window and exits automatically after 10 seconds by default
- CLI entry points use generated visual `--help` / parse-error output via `XenoAtom.CommandLine`
- In-memory MCP transport tests for tool discovery and roundtrip tool calls

## 📖 User Guide

For more details on how to use CodeAlta, please visit the [user guide](https://github.com/xoofx/CodeAlta/blob/main/doc/readme.md).

## 🪪 License

This software is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause). 

## 🤗 Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).
