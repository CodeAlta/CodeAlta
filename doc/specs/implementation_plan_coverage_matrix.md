# Spec Coverage Matrix: `doc/specs/implementation_plan.md`

As of: 2026-02-28  
Code state: `b41f453e399b9fababc43ab3b52557071d196506`

Legend:
- Done: implemented and covered by tests (or trivially verified)
- Partial: implemented but gaps remain versus the spec text
- Not Started: not implemented yet

Scope:
- This matrix is anchored on `doc/specs/implementation_plan.md` and uses the detailed plans it references to clarify what “covered” means:
  - `doc/specs/implementation_plan_mcp_server.md`
  - `doc/specs/implementation_plan_storage_search.md`
  - `doc/specs/implementation_plan_workspaces_bootstrap.md`
  - `doc/specs/implementation_plan_agent_orchestration.md`
  - `doc/specs/implementation_plan_dotnet.md`

## 1. Constraints / technology choices

| Spec Item | Status | Implementation Evidence | Test Evidence / Notes |
| --- | --- | --- | --- |
| MCP server: in-process using C# MCP SDK (`ModelContextProtocol`) | Done | `src/CodeAlta.Mcp/CodeAltaMcpServerFactory.cs`, `src/CodeAlta.Mcp/InProcessMcpConnection.cs` | `src/CodeAlta.Mcp.Tests/McpInfrastructureTests.cs` (`Mcp_InProcess_CanListTools`) |
| Embeddings: LLamaSharp | Partial | `src/CodeAlta.Search/LlamaSharpEmbedder.cs` exists; default wiring uses `HashEmbedder` in `src/CodeAlta.Search/EmbeddingModelManager.cs` | No unit test exercises LLamaSharp embedder; runtime default does not require LLamaSharp model on disk |
| Database: SQLite via `Microsoft.Data.Sqlite` | Done | `src/CodeAlta.Persistence/CodeAltaDb.cs` | `src/CodeAlta.Persistence.Tests/PersistenceInfrastructureTests.cs` (`CodeAltaDb_InitializeAsync_CreatesCoreSchema`) |
| Vector search: `sqlite-vec` native extension for similarity | Partial | Extension loading is supported by `src/CodeAlta.Persistence/CodeAltaDb.cs` + `src/CodeAlta.Persistence/CodeAltaDbOptions.cs` | Current embedding storage is `document_embeddings.embedding_blob` and similarity is computed in-process (`src/CodeAlta.Search/SearchService.cs`); no `sqlite-vec` table/query is used |
| Full-text search: SQLite FTS5 | Done | `documents_fts` virtual table created in `src/CodeAlta.Persistence/CodeAltaDb.cs`; query in `src/CodeAlta.Search/DocumentIndexStore.cs` | `src/CodeAlta.Search.Tests/SearchInfrastructureTests.cs` (`Indexer_ProcessNextAsync_IndexesDocuments`) |
| Markdown: Markdig | Done | `src/CodeAlta.Persistence/ArtifactStore.cs` (markdown plain-text extraction) | `src/CodeAlta.Persistence.Tests/PersistenceInfrastructureTests.cs` (`ArtifactStore_WriteReadAndExtractPlainText_RoundTrips`) |
| YAML: SharpYaml (frontmatter) | Done | `src/CodeAlta.Persistence/ArtifactStore.cs`, `src/CodeAlta.Workspaces/WorkspaceYamlSerializer.cs` | Persistence + Workspaces tests exercise YAML parsing/round-trips |
| Logging: XenoAtom.Logging + bridge to `Microsoft.Extensions.Logging` | Done | `src/CodeAlta.Mcp/Logging/XenoAtomLoggerProvider.cs` wired in `src/CodeAlta.Mcp/CodeAltaMcpServerFactory.cs` | Indirectly exercised by MCP server creation tests; no dedicated logger bridge unit test |
| Identifiers: generated GUIDs are UUID v7 (`Guid.CreateVersion7()`) | Done | `src/CodeAlta.Persistence/*Id.cs`, `src/CodeAlta.Workspaces/*Id.cs`, `src/CodeAlta.Mcp/McpSessionRegistry.cs` | Covered by inspection + usages; persistence tests create entities using `*Id.NewVersion7()` |

## 2. Delivery strategy (vertical slices)

| Slice | Status | Implementation Evidence | Test Evidence / Notes |
| --- | --- | --- | --- |
| 1) Durable state + artifacts (SQLite + file store) | Done | `src/CodeAlta.Persistence/*` | `src/CodeAlta.Persistence.Tests/PersistenceInfrastructureTests.cs` |
| 2) In-process MCP surface exposing that state | Done | `src/CodeAlta.Mcp/*`, `src/CodeAlta.Mcp/Tools/*` | `src/CodeAlta.Mcp.Tests/McpInfrastructureTests.cs` |
| 3) Indexing + search (FTS5 + embeddings + hybrid retrieval) | Partial | `src/CodeAlta.Search/*` | Search tests cover FTS + hybrid rerank; `sqlite-vec` is not used for similarity (see above) |
| 4) Agent orchestration (roles/scopes/context packs) | Partial | `src/CodeAlta.Orchestration/*`, role profiles in `src/CodeAlta.Workspaces/Roles/*` | `src/CodeAlta.Orchestration.Tests/OrchestrationInfrastructureTests.cs` covers role parsing, context budget, and a minimal planner/builder loop |
| 5) .NET-first services (Roslyn-backed) | Done | `src/CodeAlta.DotNet/*` | `src/CodeAlta.DotNet.Tests/DotNetInfrastructureTests.cs` |
| 6) Terminal UI (TUI host) | Not Started | `src/CodeAlta/Program.cs` is a console menu host, not a TUI | Spec calls out a responsive TUI with job views/scope selection UX; not implemented |

Notes on “each slice should produce tests”:
- The plan text mentions tests under `src/CodeAlta.Tests/`, but the implementation uses per-assembly MSTest projects (`CodeAlta.*.Tests`). This is functionally equivalent but not a literal match to the plan’s suggested folder.

## 3. Proposed projects (assemblies) and dependencies

| Project (from plan) | Status | Implementation Evidence | Notes |
| --- | --- | --- | --- |
| `CodeAlta.Workspaces` | Done | `src/CodeAlta.Workspaces/CodeAlta.Workspaces.csproj` | Includes descriptors, resolver, YAML serializer, and bootstrap helpers |
| `CodeAlta.Persistence` | Done | `src/CodeAlta.Persistence/CodeAlta.Persistence.csproj` | SQLite + repositories + artifact store |
| `CodeAlta.Search` | Done | `src/CodeAlta.Search/CodeAlta.Search.csproj` | FTS + embeddings + indexing queue + hybrid search |
| `CodeAlta.Mcp` | Done | `src/CodeAlta.Mcp/CodeAlta.Mcp.csproj` | In-process server + tool types; DI wiring |
| `CodeAlta.Orchestration` | Partial | `src/CodeAlta.Orchestration/CodeAlta.Orchestration.csproj` | Core orchestration exists; tool routing via MCP surface is not fully implemented end-to-end |
| `CodeAlta.DotNet` | Done | `src/CodeAlta.DotNet/CodeAlta.DotNet.csproj` | Roslyn-backed services + artifact/index integration |

## 4. Cross-cutting implementation notes

| Spec Item | Status | Implementation Evidence | Test Evidence / Notes |
| --- | --- | --- | --- |
| Async-first, cancellation-first, non-blocking | Partial | Most public APIs are `async` and accept `CancellationToken` | Some operations are naturally synchronous (filesystem, processes) but still wrapped as `async`; no dedicated “cancellation” tests exist |
| SQLite write contention: serialized writer pipeline | Partial | `src/CodeAlta.Persistence/CodeAltaDb.cs` serializes writes via a `SemaphoreSlim` in `ExecuteWriteAsync` | This is serialized writes, but not a dedicated background writer queue as described in the plan |
| CPU-heavy work: background tasks and explicit concurrency limits | Partial | Indexing is queued (`src/CodeAlta.Search/IndexingQueue.cs`) | No explicit concurrency limit for embedding/Roslyn beyond “do it in async methods”; no stress/perf tests |
| Logging bridge for libraries expecting `Microsoft.Extensions.Logging` | Done | `src/CodeAlta.Mcp/Logging/XenoAtomLoggerProvider.cs` | MCP creation tests indirectly exercise DI composition |
| “Compaction-safe” durability: meaningful knowledge persisted as files | Partial | Artifact store exists (`src/CodeAlta.Persistence/ArtifactStore.cs`); planner/builder persist artifacts (`src/CodeAlta.Orchestration/*Service.cs`) | Task notes and exports are persisted; however, default artifact roots do not yet align with the storage plan’s repo-local `.codealta/{knowledge,plans,tasks}` layout |

## 5. Milestones (suggested) coverage

### Milestone 1 — Workspaces + persistence foundation

| Milestone Item | Status | Implementation Evidence | Test Evidence / Notes |
| --- | --- | --- | --- |
| Create `CodeAlta.Workspaces` and `CodeAlta.Persistence` | Done | `src/CodeAlta.Workspaces/*`, `src/CodeAlta.Persistence/*` | Workspaces + Persistence test projects |
| Define on-disk locations (`~/.codealta/...` and repo `.codealta/...`) | Partial | `src/CodeAlta/Program.cs` uses `~/.codealta/state/db/codealta.db` and `~/.codealta/repo` | Repo-local `.codealta/...` structure is used by some features (skills discovery, dotnet artifacts), but not consistently as “the” default artifact root |
| YAML frontmatter conventions for artifacts | Done | `src/CodeAlta.Persistence/ArtifactFrontmatter.cs`, `src/CodeAlta.Persistence/ArtifactStore.cs` | `ArtifactStore_WriteReadAndExtractPlainText_RoundTrips` |
| SQLite migrations + repositories + artifact store | Done | `src/CodeAlta.Persistence/CodeAltaDb.cs`, `*Repository.cs`, `ArtifactStore.cs` | `PersistenceInfrastructureTests` |
| Tests for migrations and artifact read/write | Done | See above | `CodeAltaDb_InitializeAsync_CreatesCoreSchema`, `ArtifactStore_WriteReadAndExtractPlainText_RoundTrips` |

### Milestone 2 — In-process MCP server surface

| Milestone Item | Status | Implementation Evidence | Test Evidence / Notes |
| --- | --- | --- | --- |
| Create `CodeAlta.Mcp` | Done | `src/CodeAlta.Mcp/*` | MCP tests |
| Minimal tools: tasks, artifacts, workspaces, agent registry | Done | `src/CodeAlta.Mcp/Tools/{Tasks,Artifacts,Workspaces,Agents}Tools.cs` | `Mcp_InProcess_CanListTools`, `Mcp_Tasks_CreateThenGet_RoundTrips` |
| In-memory transport tests (pipes) | Done | `src/CodeAlta.Mcp/InProcessMcpConnection.cs` | `src/CodeAlta.Mcp.Tests/McpInfrastructureTests.cs` |

Tool-surface gaps versus `implementation_plan_mcp_server.md`:
- `codealta.tasks.list` is not cursor-based pagination (it is `limit` only). Status: Partial. Evidence: `src/CodeAlta.Mcp/Tools/TasksTools.cs`.

### Milestone 3 — Indexing + search

| Milestone Item | Status | Implementation Evidence | Test Evidence / Notes |
| --- | --- | --- | --- |
| Create `CodeAlta.Search` | Done | `src/CodeAlta.Search/*` | Search tests |
| Implement FTS5 indexing + embedding storage | Done | `src/CodeAlta.Search/DocumentIndexStore.cs` + `documents_fts` + `document_embeddings` | `Indexer_ProcessNextAsync_IndexesDocuments` |
| sqlite-vec embedding storage + vector similarity queries | Partial | sqlite-vec extension loading support exists in `CodeAltaDb`; no vec virtual table or vec query exists | Hybrid rerank uses in-process cosine similarity, not sqlite-vec |
| Hybrid search (FTS prefilter + vector rerank) | Done | `src/CodeAlta.Search/SearchService.cs` | `SearchService_QueryHybridAsync_ReranksAndReturnsSourceLinks` |

### Milestone 4 — Agent orchestration (headless)

| Milestone Item | Status | Implementation Evidence | Test Evidence / Notes |
| --- | --- | --- | --- |
| Create `CodeAlta.Orchestration` | Done | `src/CodeAlta.Orchestration/*` | Orchestration tests |
| Implement role profiles | Done | Role parsing/storage in `src/CodeAlta.Workspaces/Roles/*` | `RoleProfileStore_ParsesFrontmatterAndCopilotMarkdown` |
| Implement scope resolution + context pack builder | Done | `src/CodeAlta.Workspaces/WorkspaceResolver.cs`, `src/CodeAlta.Orchestration/Context/ContextPackBuilder.cs` | `ContextPackBuilder_EnforcesBudgetAndPreservesSourceLinks` |
| Integrate with `CodeAlta.Agent` backends and route tool calls | Partial | `src/CodeAlta.Orchestration/Runtime/AgentHub.cs` exists; end-to-end tool routing via MCP is not fully implemented/tested | Tests use a fake backend; no “backend tool call -> MCP tool -> persistence” integration test |
| Persist planner/knowledge outputs to artifacts | Partial | Planner/builder persist artifacts; knowledge agent artifact flows are minimal | `OrchestrationFlow_PlannerCreatesAndBuilderCompletesTasks` covers planner + builder artifacts |

### Milestone 5 — .NET-first services

| Milestone Item | Status | Implementation Evidence | Test Evidence / Notes |
| --- | --- | --- | --- |
| Create `CodeAlta.DotNet` | Done | `src/CodeAlta.DotNet/*` | DotNet tests |
| Roslyn graph/symbol services and indexing | Done | `DotNetWorkspaceService`, `SymbolIndexService`, `DotNetIndexService` | `DotNetInfrastructureTests` |
| Expose via MCP tools | Done | `src/CodeAlta.Mcp/Tools/DotNetTools.cs` | Tool existence covered by MCP tests |

### Milestone 6 — Terminal UI

| Milestone Item | Status | Implementation Evidence | Test Evidence / Notes |
| --- | --- | --- | --- |
| Replace `Program.cs` playground with real TUI host | Not Started | `src/CodeAlta/Program.cs` is still a console menu | No TUI host exists |
| Responsive UI loops, background job views, scope selection UX | Not Started | N/A | N/A |

