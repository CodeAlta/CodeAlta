# Implementation Plan: CodeAlta Infrastructure (Draft)

This document proposes an implementation plan for the CodeAlta “infrastructure first” roadmap. The terminal UI is intentionally last.

Related specs:
- `doc/specs/blueprint_codealta_specs.md`
- `doc/specs/blueprint_agentic_coding_specs.md`
- `doc/specs/blueprint_mcp_server_specs.md`
- `doc/specs/agent_api_specs.md`

Detailed implementation plans:
- MCP server: `doc/specs/implementation_plan_mcp_server.md`
- Storage + indexing + search: `doc/specs/implementation_plan_storage_search.md`
- Workspaces + bootstrap + global repo: `doc/specs/implementation_plan_workspaces_bootstrap.md`
- Agent orchestration + roles: `doc/specs/implementation_plan_agent_orchestration.md`
- .NET first-class support: `doc/specs/implementation_plan_dotnet.md`

## 1. Constraints / technology choices

Hard choices (requested):
- MCP server: in-process, using the C# MCP SDK (`ModelContextProtocol`) and its “modern” hosting patterns.
- Embeddings: LLamaSharp (see commented example in `src/CodeAlta/Program.cs`).
- Database: SQLite via `Microsoft.Data.Sqlite`.
- Vector search: `sqlite-vec` (native extension) for vector similarity search.
- Full-text search: SQLite FTS5.
- Markdown: Markdig.
- YAML: SharpYaml (frontmatter).
- Logging: XenoAtom.Logging (bridge where other libs expect `Microsoft.Extensions.Logging`).
- Identifiers: all generated GUID identifiers use UUID v7 via `Guid.CreateVersion7()`.

Non-functional constraints:
- Async-first, cancellation-first, non-blocking; UI must remain responsive.
- Multi-thread aware (multiple agents and sessions running concurrently).
- Pluggable but not over-engineered (clear scopes, minimal dependencies, predictable layering).
- Language-agnostic overall, with first-class .NET support in v1.0.

## 2. Delivery strategy (vertical slices)

We implement in “thin vertical slices” that are individually testable and useful:

1) **Durable state + artifacts** (SQLite + file store)  
2) **In-process MCP surface** exposing that state  
3) **Indexing + search** (FTS5 + sqlite-vec + embeddings)  
4) **Agent orchestration** (roles, scopes, context packs) built on the above  
5) **.NET-first services** (Roslyn-backed)  
6) **Terminal UI** (TUI) built on stable services

Each slice should produce:
- A small set of unit tests in `src/CodeAlta.Tests/`.
- A minimal runnable “headless” entry point (even if the final UX is a TUI).
- Persisted artifacts on disk for compaction-safe recovery.

## 3. Proposed projects (assemblies) and dependencies

Existing (already in repo):
- `CodeAlta` (exe): currently a playground; will become the TUI host later.
- `CodeAlta.Agent`: backend-agnostic agent API (Codex + Copilot adapters already exist).
- `CodeAlta.Agent.Codex`: Codex adapter.
- `CodeAlta.Agent.Copilot`: Copilot adapter.
- `CodeAlta.CodexSdk`: codex app-server client.
- `CodeAlta.CodexSdk.Generator`: codex schema generator.
- `CodeAlta.Tests`: MSTest.

Planned new projects (in recommended creation order):

### 3.1 `CodeAlta.Workspaces`

Scope:
- Workspace/project configuration model.
- Loading/saving workspace descriptors from disk (global repo) and per-machine overrides.
- Repo-local `.codealta/` discovery (project knowledge, roles, skills, artifacts).

Key namespaces / types:
- `CodeAlta.Workspaces`
  - `WorkspaceId`, `ProjectId`
  - `WorkspaceDescriptor`, `ProjectDescriptor`, `CheckoutRule`, `MachineProfile`
  - `WorkspaceCatalog` (loads all known workspaces)
  - `WorkspaceResolver` (resolves “active scope” into concrete repo roots)

Dependencies:
- SharpYaml (for YAML config), optional Markdig (if descriptors in markdown).

### 3.2 `CodeAlta.Persistence`

Scope:
- SQLite schema + migrations + repository-style APIs for:
  - tasks/plans
  - agents/sessions/runs
  - knowledge records and artifact metadata
- File-backed “artifact store” (markdown + YAML frontmatter) with stable URIs and links into SQLite.

Key namespaces / types:
- `CodeAlta.Persistence`
  - `CodeAltaDb` (connection factory + migration runner)
  - `TaskRepository`, `ArtifactRepository`, `KnowledgeRepository`, `AgentRepository`
  - `ArtifactStore` (disk IO; creates/reads markdown files)
  - `ArtifactId`, `KnowledgeRecordId`, etc.

Dependencies:
- `Microsoft.Data.Sqlite`
- Markdig + SharpYaml (artifact parsing/format)

### 3.3 `CodeAlta.Search`

Scope:
- Indexing pipeline (sources → documents → FTS5 + embeddings).
- `sqlite-vec` integration and vector similarity queries.
- Embedding generation via LLamaSharp.

Key namespaces / types:
- `CodeAlta.Search`
  - `IndexingJob`, `IndexingQueue`, `Indexer`
  - `EmbeddingModelManager` (download/cache local GGUF, load weights)
  - `Embedder` (wraps `LLamaEmbedder`)
  - `SearchService` (FTS + vector hybrid retrieval)

Dependencies:
- LLamaSharp (+ chosen backend package)
- `Microsoft.Data.Sqlite` + sqlite-vec native loading

### 3.4 `CodeAlta.Mcp`

Scope:
- In-process MCP server “surface” and tool/resource/prompt implementations.
- Bridges MCP tool calls to internal services (tasks, artifacts, search, workspaces, roles, skills).
- Test-friendly in-memory transport wiring.

Key namespaces / types:
- `CodeAlta.Mcp`
  - `CodeAltaMcpServerFactory` (creates `McpServer` on a pair of streams)
  - `InProcessMcpConnection` (pipes + `McpClient` + server runner)
- `CodeAlta.Mcp.Tools` (tool types, attribute-based)
- `CodeAlta.Mcp.Resources` (resource types; optional in v1)
- `CodeAlta.Mcp.Prompts` (prompt types; optional in v1)

Dependencies:
- `ModelContextProtocol` (server + client)
- `Microsoft.Extensions.DependencyInjection` (DI)

### 3.5 `CodeAlta.Orchestration`

Scope:
- Global agent orchestration: roles, scopes, context pack builder, task-driven coordination.
- Bridges `CodeAlta.Agent` backends to MCP tools and durable state.
- Captures “agent work products” to disk artifacts for compaction-safe recovery.

Key namespaces / types:
- `CodeAlta.Orchestration`
  - `AgentRole` / `AgentScope` / `AgentIdentity`
  - `AgentHub` (creates/owns backend sessions; routes tool calls)
  - `ContextPackBuilder` (builds a bounded context for a run)
  - `PlannerService`, `KnowledgeService`, `BuilderService`
  - `RoleProfileStore` (loads role profiles from disk)

Dependencies:
- `CodeAlta.Agent`
- `CodeAlta.Mcp`, `CodeAlta.Persistence`, `CodeAlta.Workspaces`, `CodeAlta.Search`

### 3.6 `CodeAlta.DotNet`

Scope:
- Roslyn-backed .NET “first-class” services: solution/project graph, symbol search, diagnostics.
- Produces knowledge artifacts suitable for indexing and agent consumption.

Key namespaces / types:
- `CodeAlta.DotNet`
  - `DotNetWorkspaceService` (loads solution, manages `MSBuildWorkspace`)
  - `SymbolIndexService` (namespaces/types/methods → index records)
  - `DotNetContextProvider` (builds compact code context snippets)

Dependencies:
- `Microsoft.CodeAnalysis.*` (Roslyn)
- `CodeAlta.Persistence` / `CodeAlta.Search` for durable storage and retrieval

## 4. Cross-cutting implementation notes

### 4.1 Async model and threading

- Every service API should be `async` (or return `ValueTask`) even if underlying libraries are synchronous (SQLite, filesystem).  
  The calling layer (eventually the TUI) must not block.
- Use cancellation tokens in all long-running operations.
- For SQLite write contention, prefer serialized write pipelines:
  - one writer queue (background) for mutations
  - concurrent readers with separate connections
- For CPU-heavy work (embedding generation, Roslyn analysis), run on background tasks with explicit concurrency limits.

### 4.2 Logging

We standardize on XenoAtom.Logging for application logs.

When consuming libraries that depend on `Microsoft.Extensions.Logging` (e.g. MCP SDK), we add a small bridge:
- `XenoAtomLoggerProvider : ILoggerProvider` forwarding to `LogManager.GetLogger(categoryName)`.

### 4.3 “Compaction-safe” durability

Anything not trivially reconstructible from the repo working tree must be persisted as files:
- planner outputs
- knowledge summaries and extracted info
- decisions / rationale
- task and plan snapshots (human readable)

SQLite indexes and links these artifacts but should not be the only copy of “meaningful knowledge”.

## 5. Milestones (suggested)

Milestone 1 — Workspaces + persistence foundation
- Create `CodeAlta.Workspaces` and `CodeAlta.Persistence`.
- Define on-disk locations (`~/.codealta/...` and repo `.codealta/...`) and YAML frontmatter conventions.
- Implement SQLite migrations + repositories + artifact store.
- Add tests for migrations and artifact read/write.

Milestone 2 — In-process MCP server surface
- Create `CodeAlta.Mcp`.
- Add minimal tool sets: tasks, artifacts, workspaces, agent registry.
- Add in-memory transport tests (pipes) that call tools and verify results.

Milestone 3 — Indexing + search
- Create `CodeAlta.Search`.
- Implement FTS5 indexing + sqlite-vec embedding storage + LLamaSharp embedder wrapper.
- Provide hybrid search (FTS prefilter + vector rerank) and tests with a tiny fixture set.

Milestone 4 — Agent orchestration (headless)
- Create `CodeAlta.Orchestration`.
- Implement role profiles, scope resolution, context pack builder.
- Integrate with `CodeAlta.Agent` backends and route tool calls to MCP tools.
- Persist planner/knowledge outputs to artifacts.

Milestone 5 — .NET-first services
- Create `CodeAlta.DotNet`.
- Add Roslyn graph/symbol services and index them.
- Expose .NET services both as MCP tools and as orchestration “context providers”.

Milestone 6 — Terminal UI
- Replace `src/CodeAlta/Program.cs` playground with a real TUI host.
- Add responsive UI loops, background job views, and scope selection UX.
- Keep everything else reusable without the UI.
