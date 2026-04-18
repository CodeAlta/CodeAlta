# CodeAlta User Guide

An agentic AI coding CLI assistant developed in .NET.

## Infrastructure Status

Current infrastructure-first progress includes project catalog and checkout primitives:

- `CodeAlta.Catalog`: project descriptors, machine override profiles, catalog loading.
- Scope resolution (`global`, `project`) into concrete checkout and `.codealta` roots.
- Checkout planning (`clone` vs `update`) without network side effects.
- `CodeAlta.Persistence`: SQLite migrations, task/artifact/agent repositories, and markdown artifact store.

## Project Catalog Layout

Global repository layout (implemented reader support):

- `projects/<projectSlug>/readme.md`
- `machines/<machineId>.yaml`
- `checkouts/<projectName>/`

The catalog model uses UUID v7 strings for project `id` values, validates slugs
using `^[a-z0-9][a-z0-9\\-_.]{1,63}$`, and keeps a separate project `name` for checkout directories.

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
  - `codealta.projects.*` for project listing/getting/scope resolution.
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

`CodeAlta` and the small CLI utilities under `src/` now use `XenoAtom.CommandLine` with `TerminalVisualCommandOutput`, so `--help` and parse errors render through the shared visual command help/error path.

`--test` still starts the real terminal UI, but it schedules cancellation after the requested duration so a smoke test can verify startup and a short steady-state run without manual Ctrl+C. Smoke-test lifecycle markers and normal diagnostic activity are written to the rolling logs under `~/.codealta/logs/`.

Current terminal shell capabilities:

- Chat (global agent) operations:
  - Chat screen powered by `PromptEditor` (input) and `DocumentFlow` + `MarkdownControl` (rendered conversation history).
  - Automatically probes and initializes both Copilot and Codex backends. Codex is pinned to the SDK-generated release tag and is downloaded on demand into `~/.codealta/local/bin/codex/<tag>/` when missing.
  - Codex backend sessions default to `danger-full-access` (no sandbox) in CodeAlta so prompts can inspect sibling projects outside the current working directory without first switching the session root.
  - Backend, model, and reasoning-effort selectors are shown under the prompt.
  - Press `F6` or use the `Full Prompt` button to edit the current draft in a large 80%-screen prompt window; `Esc` closes it and keeps the edited draft.
  - Type `@` in the prompt to open a resizable project file/folder picker dialog with its own search box. Accepted entries become markdown links such as `[Program.cs](src/CodeAlta/Program.cs)`, raw `@path` and `@"path with spaces"` references still resolve on submit, optional `:line` or `:start-end` suffixes map to attachment line ranges, and accepted references are sent as structured file/directory inputs.
  - Press `F1`, type `/help`, or enter `?` in the prompt to open shell command discovery. Textual shell commands now include `/open`, `/abort`, `/compact`, `/close`, `/queue`, `/exit`, `/tab_left`, `/tab_right`, `/go_to_sidebar`, and `/go_to_prompt`; `F5` steers, `F7` delegates, and `ALT+LEFT` / `ALT+RIGHT` switch between open tabs.
  - Press `Ctrl+O`, run `/open`, or use the always-visible `+` action on the `Projects` sidebar row to open a project dialog with project-name and directory completion. Rooted paths such as `/`, `C:`, `D:`, and `~` open folders, visible projects match the sidebar display names by default, and an `Include hidden` toggle opts archived projects back into completion and opening while successful opens return focus to the prompt.
  - Press `Ctrl+G Ctrl+S` to focus the sidebar on the current selection, `Ctrl+G Ctrl+P` to focus the prompt, `Ctrl+G Ctrl+U` or use the footer usage indicator to open the context/usage popup, and press `Ctrl+G Ctrl+T` or use the thread info icon in the footer to open the selected thread report.
  - Closing either popup restores focus to the thread prompt editor so the workflow stays keyboard-first.
  - Per-backend model and reasoning defaults are stored in `~/.codealta/config.toml`, with project-local overrides read from `<project>/.codealta/config.toml`.
- Raw OpenAI-compatible, Anthropic, and Google GenAI backends can also be configured in `~/.codealta/config.toml`; only providers with usable credentials or Vertex settings are registered at startup. Local raw-API backends enrich model metadata from the bundled `models_dev_db.json` snapshot, apply per-provider `models_dev_provider_id` mappings and optional `model_overrides`, and refresh the models.dev catalog in the background at runtime.
  - Thread-specific model and reasoning selections are preserved for reopened tabs through `~/.codealta/local/ui-state.yaml`, so an existing thread keeps its model by default even after global or project defaults change.
  - The reasoning selector only shows concrete effort values. When a selected model supports `high`, CodeAlta prefers `high` by default.
  - Sending a prompt now follows an enqueue-first workflow for busy threads: `Ctrl+J`/`Ctrl+Enter` adds the prompt to a waiting list above the status line, where queued prompts can be edited, repeated, steered immediately when the backend supports live steering, deleted, or cleared with `F10`. Steer requests that have been sent locally but not yet echoed back by the backend also appear at the top of that strip as transient pending rows, and unsupported backends such as the current local raw-API OpenAI/Anthropic/Google runtimes automatically re-queue the prompt for the next turn.
  - A temporary `AlwaysQueue` checkbox next to `AutoScroll` forces normal sends to enqueue for the selected thread even when it is idle, which is useful for exercising the waiting-list controls without dispatching work immediately.
  - Pressing `F5` with an empty draft now steers the first queued prompt immediately when the selected thread has queued work.
  - If a steer is requested while the thread has no active backend run, CodeAlta now falls back to a normal send instead of surfacing a backend steer error. If prompt dispatch still fails, the prompt is preserved in the UI instead of being dropped.
  - Closing a thread tab no longer stops an active run or drops an edited thread draft. Running threads stay live in the sidebar, edited prompts surface with a draft indicator, and unsent per-thread prompts are persisted under `~/.codealta/local/saved_prompts/` so reopening the app restores them.
  - A compact button now sits beside the backend/model/reasoning selectors. Press `F11` or click it to trigger a manual session compaction when the selected thread has already started and is currently idle; if the reopened thread's backend session had already shut down, CodeAlta now resumes it again before compacting. Manual compaction keeps a dedicated status line and emits visible started/completed notices in the timeline even when the backend does not emit its own compaction events.
  - The footer usage indicator stays compact as `ctx --`, `ctx N tok`, or `ctx NN%`, while the usage popup is split into Summary, Usage breakdown, Limits and quotas, and Backend-specific details with explicit source/scope metadata.
  - Copilot sessions are started with `codealta.*` MCP tools bridged into the backend via `McpToolBridge` (tool calls execute against the in-process MCP server, with MCP tool ids normalized to Copilot-compatible function names).
  - CodeAlta now auto-approves backend permission requests and auto-resolves `ask_user` prompts by preferring continue/inspect-style choices (or a neutral fallback for freeform prompts).
  - Sequential Codex/Copilot tool activity is grouped into compact "Tool Calls" timeline cards so verbose command/tool logs stay out of the main document flow; each chip shows a live status icon, inferred tool/command label, compact context, and an expandable `LogControl` detail dialog with full output, wrapping toggle, and compact execution stats.
  - When a run finishes, CodeAlta emits a separate compact "Modified Files" recap card that aggregates all files changed during that run, shows per-file and total `+/-` line counts, and opens an inline diff viewer for each file when diff data is available from the backend.
  - The terminal shell writes rolling diagnostic logs under `~/.codealta/logs/`, including chat prompt submission, selected backend/model/tool set, normalized agent events, and Copilot permission/user-input callback traffic.
- Project operations:
  - list discovered projects
  - resolve global/project scopes.
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

## Raw-API Backend Configuration

CodeAlta can host local agent runtimes backed by raw provider SDKs:

- `OpenAI Responses`
- `OpenAI Chat`
- `Anthropic Messages`
- `Google GenAI`

These backends are configured from `~/.codealta/config.toml` under `providers.<provider-key>`. Each entry represents one endpoint registration plus one wire API. For example, OpenAI-compatible endpoints use `provider = "openai"` and can set `wire_api = "chat"` or `wire_api = "responses"`; when omitted, OpenAI defaults to `chat`. Anthropic and Google GenAI use provider-specific defaults, so `wire_api` can usually be omitted there. Each provider can optionally map to a models.dev provider id and override individual model limits so context usage stays consistent even when the upstream SDK does not expose context-window metadata. Legacy sections under `raw_api.providers.*`, `raw_api.openai.providers.*`, `raw_api.anthropic.providers.*`, and `raw_api.google_genai.providers.*` are still accepted for backward compatibility.

Example:

```toml
[providers.openai_chat]
display_name = "OpenAI"
provider = "openai"
api_key_env = "OPENAI_API_KEY"
models_dev_provider_id = "openai"
is_default = true

[providers.openai_chat.model_overrides.gpt-5]
context_window = 400000
output_token_limit = 128000

[providers.openai_responses]
display_name = "OpenAI Responses"
provider = "openai"
api_key_env = "OPENAI_API_KEY"
wire_api = "responses"
base_uri = "https://api.openai.com/v1"

[providers.minimax]
display_name = "MiniMax 2.7"
provider = "openai"
api_key_env = "MINIMAX_API_KEY"
base_uri = "https://api.minimax.io/v1"
wire_api = "chat"
single_model_id = "MiniMax-M2.7"
is_default = true

[providers.anthropic]
display_name = "Anthropic"
provider = "anthropic"
api_key_env = "ANTHROPIC_API_KEY"
models_dev_provider_id = "anthropic"
is_default = true

[providers.vertex]
display_name = "Vertex"
provider = "google_genai"
use_vertex_ai = true
project = "my-gcp-project"
location = "europe-west4"
models_dev_provider_id = "google"
is_default = true
```

Model discovery still comes from the upstream provider API when supported. `model_overrides` enriches or corrects discovered model metadata, while `single_model_id` can pin a provider to one fixed model for single-model endpoints. When an OpenAI-compatible endpoint does not implement `/models`, CodeAlta can also fall back to the local `models.dev` catalog if the provider key or `models_dev_provider_id` maps to a known catalog provider such as `minimax`.

CodeAlta also applies known provider defaults through a small defaults catalog before config overrides are applied. For example, MiniMax Chat registrations automatically disable the `developer` message role and merge developer instructions into the system prompt instead, while an explicit `profile` section in `config.toml` can still override that default.

The bundled snapshot lives in `src/CodeAlta.Agent/Data/models_dev_db.json`. To refresh it manually, run:

```sh
dotnet run --project src/CodeAlta.Agent.ModelsDev.Updater/CodeAlta.Agent.ModelsDev.Updater.csproj -c Release
```

Session state for these local runtimes is stored under `~/.codealta/local/agents/<protocol-family>/<provider-key>/sessions/...` with `session.json`, `events.jsonl`, and `state.json`.

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
- Run with `--help` to see the generated visual usage and option reference.
