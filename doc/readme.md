# CodeAlta User Guide

An agentic AI coding CLI assistant developed in .NET.

## Infrastructure Status

Current infrastructure-first progress includes workspace bootstrapping primitives:

- `CodeAlta.Workspaces`: workspace/project descriptors, machine override profiles, catalog loading.
- Scope resolution (`global`, `workspace`, `project`) into concrete checkout and `.codealta` roots.
- Checkout planning (`clone` vs `update`) without network side effects.
- `CodeAlta.Persistence`: SQLite migrations, task/artifact/agent repositories, and markdown artifact store.

## Workspace Descriptor Layout

Global repository layout (implemented reader support):

- `workspaces/<workspaceKey>/workspace.yaml`
- `workspaces/<workspaceKey>/projects/*.yaml`
- `machines/<machineId>.yaml`

The YAML model uses UUID v7 strings for workspace/project `id` values and validates
workspace/project keys using `^[a-z0-9][a-z0-9\\-_.]{1,63}$`.

## Persistence Model

The persistence layer currently provides:

- SQLite schema bootstrap with `schema_version` migration tracking.
- Durable tables for `tasks`, `task_events`, `artifacts`, `artifact_links`, `agents`, and `agent_sessions`.
- Search foundation tables: `documents`, `documents_fts` (FTS5), and `document_embeddings`.
- Markdown artifact read/write with YAML frontmatter (`ArtifactStore`) and plain-text extraction for indexing.

## Indexing and Search

Current search infrastructure (`CodeAlta.Search`) includes:

- `IndexingQueue` + `Indexer` for background-capable indexing jobs.
- `DocumentIndexStore` to upsert documents, maintain FTS rows, and persist embeddings.
- `SearchService` with:
  - FTS query mode
  - hybrid mode (FTS prefilter + vector rerank).
- A deterministic local `HashEmbedder` for tests and offline indexing.
- `LlamaSharpEmbedder` for local GGUF-based embeddings when a model path is configured.

## In-process MCP Server

Current MCP infrastructure (`CodeAlta.Mcp`) includes:

- `CodeAltaMcpServerFactory` for building stream-transport MCP servers from internal services.
- `InProcessMcpConnection` for in-memory client/server transport wiring used by tests and internal callers.
- MCP tool sets:
  - `codealta.tasks.*` for durable task CRUD, notes, and markdown exports.
  - `codealta.artifacts.*` for markdown artifact write/read/list/link operations.
  - `codealta.search.*` for indexing, hybrid query, and queue status.
  - `codealta.workspaces.*` for workspace listing/getting/scope resolution.
  - `codealta.agents.*` for agent registry register/update/list operations.
- MSTest coverage for:
  - in-process tool discovery (`ListTools`)
  - task create/get roundtrip
  - indexed search query returning artifact-backed sources.

## Agent Orchestration

Current orchestration infrastructure (`CodeAlta.Orchestration`) includes:

- Identity and scope primitives for orchestrated agents (`AgentIdentity`, `AgentScope`, `AgentScopeKind`).
- `RoleProfileStore` for parsing role markdown from frontmatter-based and Copilot-style role files.
- `ContextPackBuilder` with provider composition and strict character-budget enforcement.
- `PlannerService` for durable plan creation:
  - root + child task creation in SQLite
  - persisted `plan.output` markdown artifacts.
- `BuilderService` for task completion and persisted `builder.verification` artifacts.
- `AgentHub` runtime for backend-agnostic agent registration, session lifecycle, and run event emission.

## .NET First-class Services

Current `.NET` infrastructure (`CodeAlta.DotNet`) includes:

- `DotNetWorkspaceService` for discovering `.sln`/`.slnx` and project files (`.csproj`, `.fsproj`, `.vbproj`).
- `SymbolIndexService` using Roslyn syntax trees to index:
  - namespaces
  - types
  - members (methods, properties, fields) with file line ranges.
- `DotNetContextProvider` for compact symbol/file snippets with stable source links (`file://...#Lx`).
- `DotNetDiagnosticsService` for `dotnet build` execution with persisted diagnostics artifacts and search indexing.
- `DotNetIndexService` for refreshing project-graph and symbol knowledge artifacts and indexing them.
- Fixture-backed MSTest coverage for:
  - tiny solution discovery
  - symbol lookup path/range validation
  - symbol context source-link generation
  - refresh-index and diagnostics artifact persistence.

## Terminal Shell

The `CodeAlta` executable now runs as an interactive terminal shell instead of a playground script. Internally, the shell is organized around an explicit controller, UI dispatcher, focused views, and presenter seams rather than one monolithic host bucket.

Launch helpers:

- `dotnet run --project src/CodeAlta -- --test`
- `dotnet run --project src/CodeAlta -- --test --test-duration 15`

`--test` still starts the real terminal UI, but it schedules cancellation after the requested duration so a smoke test can verify startup and a short steady-state run without manual Ctrl+C. Smoke-test lifecycle markers and normal diagnostic activity are written to `~/.codealta/logs/codealta.log`.

Current terminal shell capabilities:

- Chat (global agent) operations:
  - Chat screen powered by `PromptEditor` (input) and `DocumentFlow` + `MarkdownControl` (rendered conversation history).
  - Automatically probes and initializes both Copilot and Codex backends (when available), with inline warnings when a local CLI/runtime is not installed.
  - Codex backend sessions default to `danger-full-access` (no sandbox) in CodeAlta so prompts can inspect sibling projects outside the current working directory without first switching the session root.
  - Backend, model, and reasoning-effort selectors are shown under the prompt.
  - Per-backend model and reasoning defaults are stored in `~/.codealta/config.toml`, with project-local overrides read from `<project>/.codealta/config.toml`.
  - Thread-specific model and reasoning selections are preserved for reopened tabs through `~/.codealta/machine/ui-state.yaml`, so an existing thread keeps its model by default even after global or project defaults change.
  - The reasoning selector only shows concrete effort values. When a selected model supports `high`, CodeAlta prefers `high` by default.
  - Sending a prompt now follows an enqueue-first workflow for busy threads: `Ctrl+J`/`Ctrl+Enter` adds the prompt to a waiting list above the status line, where queued prompts can be edited, repeated, steered immediately, deleted, or cleared with `F10`.
  - Pressing `F5` with an empty draft now steers the first queued prompt immediately when the selected thread has queued work.
  - The footer usage indicator stays compact as `ctx --` or `ctx NN%`, while the usage popup is split into Summary, Usage breakdown, Limits and quotas, and Backend-specific details with explicit source/scope metadata.
  - Copilot sessions are started with `codealta.*` MCP tools bridged into the backend via `McpToolBridge` (tool calls execute against the in-process MCP server, with MCP tool ids normalized to Copilot-compatible function names).
  - CodeAlta now auto-approves backend permission requests and auto-resolves `ask_user` prompts by preferring continue/inspect-style choices (or a neutral fallback for freeform prompts).
  - Sequential Codex/Copilot tool activity is grouped into compact "Tool Calls" timeline cards so verbose command/tool logs stay out of the main document flow; each chip shows a live status icon, inferred tool/command label, compact context, and an expandable `LogControl` detail dialog with full output, wrapping toggle, and compact execution stats.
  - The terminal shell writes a persistent diagnostic log to `~/.codealta/logs/codealta.log`, including chat prompt submission, selected backend/model/tool set, normalized agent events, and Copilot permission/user-input callback traffic.
- Workspace operations:
  - list discovered workspaces
  - resolve global/workspace/project scopes.
- Task operations:
  - list tasks
  - create tasks.
- Search operations:
  - run hybrid search queries over indexed artifacts/documents.
- .NET operations:
  - refresh `.NET` project/symbol index artifacts
  - run `dotnet build` diagnostics with persisted artifacts.
- MCP operations:
  - in-process MCP health check by listing registered `codealta.*` tools.

## Live Backend Smoke Tests

`src/CodeAlta.Tests` also contains opt-in live backend smoke tests for the local Codex and Copilot CLIs.

- Set `CODEALTA_RUN_LIVE_CODEX_TESTS=1` to run the Codex live prompt test.
- Set `CODEALTA_RUN_LIVE_COPILOT_TESTS=1` to run the Copilot live prompt test.
- Without those environment variables, the live tests are skipped as inconclusive during `dotnet test`.

## Session Diagnostics

`src/AgentMessageDiagnosticApp` provides a small CLI for dumping mapped backend session history as JSONL.

- `dotnet run --project src/AgentMessageDiagnosticApp/AgentMessageDiagnosticApp.csproj -- --codex <session-id>`
- `dotnet run --project src/AgentMessageDiagnosticApp/AgentMessageDiagnosticApp.csproj -- --copilot <session-id>`
- Add `--indented` to pretty-print each JSON payload instead of emitting compact JSONL.
