# CodeAlta [![ci](https://github.com/xoofx/CodeAlta/actions/workflows/ci.yml/badge.svg)](https://github.com/xoofx/CodeAlta/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/CodeAlta.svg)](https://www.nuget.org/packages/CodeAlta/)

<img align="right" width="160px" height="160px" src="https://raw.githubusercontent.com/xoofx/CodeAlta/main/img/CodeAlta.png">

An agentic AI coding CLI assistant developed in .NET.

## ✨ Features

- Workspace descriptors and machine profile overrides (`CodeAlta.Workspaces`)
- Scope resolution and checkout planning APIs for multi-repo workspaces
- SQLite-backed durable state + migrations + repositories (`CodeAlta.Persistence`)
- Markdown artifact store with YAML frontmatter parsing and plain-text extraction
- FTS5 + embedding-backed hybrid search pipeline (`CodeAlta.Search`)
- In-process MCP server surface with tools for tasks, artifacts, search, workspaces, and agents (`CodeAlta.Mcp`)
- Agent orchestration services for role profiles, context packs, and planner/builder durable workflows (`CodeAlta.Orchestration`)
- .NET first-class services for workspace discovery, symbol indexing, context snippets, diagnostics, and index refresh (`CodeAlta.DotNet`)
- Interactive terminal shell wired to workspace/task/search/.NET/MCP services, with explicit shell-controller/UI-dispatcher/view-presenter seams, rich agent timelines, chat backend/model/reasoning selectors, compact grouped tool-call chips plus a per-run modified-files recap card with per-file `+/-` diff totals and expandable diff details, a live `ctx --` / `ctx NN%` context-window indicator with normalized usage popup sections and backend-specific detail, and asynchronous Codex/Copilot probing so the TUI starts immediately even when a backend is missing (`CodeAlta`)
- Busy-thread prompt queueing with an editable waiting list, per-entry repeat counts, immediate steer conversion, and an `F10` clear-queue shortcut (`CodeAlta`)
- Press `F6` or click `Full Prompt` to open the current draft in a large prompt editor window; `Esc` closes it and keeps the edited draft (`CodeAlta`)
- A temporary `AlwaysQueue` toggle beside `AutoScroll` lets you enqueue prompts on an idle selected thread to exercise the waiting-list UI without sending immediately (`CodeAlta`)
- Idle started threads can be compacted manually from the footer bar or with `F11`, using the thread's current backend/model/reasoning configuration; reopening an existing thread now re-resumes a dead backend session before compacting, and the timeline surfaces explicit manual compaction notices instead of dropping back to a generic busy state (`CodeAlta`)
- Codex backend sessions default to no sandbox in CodeAlta, so prompts can inspect sibling projects outside the current working directory without requiring the session cwd to be moved first
- CodeAlta writes a persistent diagnostic log to `~/.codealta/logs/codealta.log` for chat/backend troubleshooting
- `CodeAlta --test` runs the real terminal app for a short smoke-test window and exits automatically after 10 seconds by default
- In-memory MCP transport tests for tool discovery and roundtrip tool calls

## 📖 User Guide

For more details on how to use CodeAlta, please visit the [user guide](https://github.com/xoofx/CodeAlta/blob/main/doc/readme.md).

## 🪪 License

This software is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause). 

## 🤗 Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).
