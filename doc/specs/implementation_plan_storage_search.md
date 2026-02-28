# Implementation Plan: Storage, Indexing, and Search (Draft)

This document details how CodeAlta will implement durable storage (files + SQLite), full-text search (FTS5), and vector search (sqlite-vec + LLamaSharp embeddings).

Related specs:
- `doc/specs/blueprint_codealta_specs.md` (section “Storage, indexing, semantic search”)
- `doc/specs/blueprint_agentic_coding_specs.md` (compaction-safe storage)
- `doc/specs/blueprint_mcp_server_specs.md` (task/artifact/search services)

## 1. Storage roots (disk)

We treat **files** as the source of truth for “knowledge” and human-readable artifacts; SQLite is the index and coordination state.

### 1.1 Global machine root

Default: `~/.codealta/` (platform-specific HOME).

Proposed subfolders:
- `~/.codealta/repo/`  
  Git-backed “global knowledge repository” (portable, can be cloned on another machine).
- `~/.codealta/state/`  
  Machine-local state: SQLite database, caches, download models, logs. Not committed.
- `~/.codealta/state/db/codealta.db`  
  Main SQLite database.
- `~/.codealta/state/models/`  
  Downloaded embedding models (GGUF).
- `~/.codealta/state/extensions/`  
  Native extensions like sqlite-vec (`.dll`/`.so`).

### 1.2 Repo-local root

Per project repo: `<repo>/.codealta/` (committed, like `.github/`).

Proposed subfolders:
- `.codealta/knowledge/` (markdown knowledge artifacts)
- `.codealta/plans/` (planner outputs)
- `.codealta/tasks/` (optional exported task snapshots)
- `.codealta/roles/` (role profiles, markdown + frontmatter)
- `.codealta/skills/` (optional pointer/manifest to skill repos)

### 1.3 Workspace-level storage

Workspace metadata lives in the global repo (portable) and references local checkout paths via machine overrides:
- `~/.codealta/repo/workspaces/<workspaceKey>/workspace.yaml`
- `~/.codealta/repo/workspaces/<workspaceKey>/projects/*.yaml`
- `~/.codealta/repo/machines/<machineId>.yaml` (per-machine path overrides)

## 2. Artifact file format (markdown + YAML frontmatter)

Artifacts are plain markdown, with YAML frontmatter for stable metadata:

```yaml
---
id: "01963b36-0d6f-7e4b-a7e0-6b2e6d1f4c8a" # UUID v7 (`Guid.CreateVersion7()`)
type: "knowledge.record" # or task.snapshot, plan.output, etc.
title: "Workspace overview"
workspace_id: "01963b36-0d6f-7e4b-a7e0-6b2e6d1f4c8a"
workspace_key: "wk-..." # optional convenience for humans
project_id: "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a" # optional
project_key: "prj-..." # optional convenience for humans
source:
  kind: "generated"
  agent_id: "01963b36-0d71-7f22-8d33-abcdef012345"
tags: ["architecture", "indexable"]
links:
  tasks: ["task-..."]
  files:
    - path: "src/Foo/Bar.cs"
      range: { startLine: 10, endLine: 42 }
created_at: "2026-02-28T08:15:00Z"
updated_at: "2026-02-28T09:01:00Z"
---
```

Implementation notes:
- Parse frontmatter with SharpYaml (see Lunet examples referenced in the prompt).
- Parse markdown body with Markdig when we need chunking/sections.
- Normalize dates to UTC ISO-8601.
- Keep frontmatter minimal, stable, and machine-editable.

## 3. SQLite database (coordination + index)

### 3.1 Why SQLite is not the only copy

SQLite is perfect for:
- task coordination (status, assignments, queries)
- “what exists” indexes (artifacts, knowledge records)
- fast retrieval (FTS5 and vector search)

But it is **not** the only durable copy of “knowledge”. Any important agent-produced knowledge must be persisted as files (repo-local `.codealta/` or global repo), so we can recover after context compaction and across machines.

### 3.2 DB location

Machine-local (not in git):
- `~/.codealta/state/db/codealta.db`

Rationale:
- avoids large binary diffs in global repo
- allows local caching, rebuilds, and migrations without git noise
- still portable because the “real” knowledge is in markdown artifacts

### 3.3 Schema (first cut)

We keep the first schema small, and add tables only when we have a consumer.

**Core tables**
- `workspaces`
  - `workspace_id TEXT PRIMARY KEY`
  - `display_name TEXT`
  - `config_uri TEXT` (points to yaml in global repo)
- `projects`
  - `project_id TEXT PRIMARY KEY`
  - `workspace_id TEXT NOT NULL`
  - `repo_url TEXT`
  - `checkout_path TEXT` (resolved local path)
  - `git_root TEXT` (detected)
- `agents`
  - `agent_id TEXT PRIMARY KEY` (UUID v7, generated via `Guid.CreateVersion7()`)
  - `role TEXT` (global/knowledge/planner/builder/custom)
  - `scope_kind TEXT` (global/workspace/project)
  - `scope_id TEXT NULL`
  - `backend_id TEXT` (codex/copilot/…)
  - `created_at TEXT`
- `agent_sessions`
  - `session_id TEXT PRIMARY KEY`
  - `agent_id TEXT NOT NULL`
  - `backend_session_id TEXT` (codex thread id, copilot session id)
  - `created_at TEXT`
  - `last_used_at TEXT`
- `tasks`
  - `task_id TEXT PRIMARY KEY` (UUID v7, generated via `Guid.CreateVersion7()`)
  - `workspace_id TEXT NULL`
  - `project_id TEXT NULL`
  - `parent_task_id TEXT NULL`
  - `title TEXT NOT NULL`
  - `status TEXT NOT NULL` (pending/in_progress/completed/blocked/cancelled)
  - `assigned_agent_id TEXT NULL`
  - `created_at TEXT`
  - `updated_at TEXT`
- `task_events`
  - `event_id INTEGER PRIMARY KEY AUTOINCREMENT`
  - `task_id TEXT NOT NULL`
  - `kind TEXT NOT NULL` (created/status_changed/note_added/…)
  - `payload_json TEXT` (small, for flexible evolution)
  - `created_at TEXT`
- `artifacts`
  - `artifact_id TEXT PRIMARY KEY`
  - `uri TEXT NOT NULL` (stable logical URI; also used by MCP resources)
  - `workspace_id TEXT NULL`
  - `project_id TEXT NULL`
  - `type TEXT NOT NULL`
  - `path TEXT NOT NULL` (absolute or workspace-relative, normalized)
  - `frontmatter_json TEXT` (cached parsed metadata)
  - `created_at TEXT`
  - `updated_at TEXT`
- `artifact_links`
  - `from_artifact_id TEXT NOT NULL`
  - `to_kind TEXT NOT NULL` (task/knowledge/file/agent_run)
  - `to_id TEXT NOT NULL`

**Index/search tables**
- `documents`
  - `document_id INTEGER PRIMARY KEY AUTOINCREMENT`
  - `source_kind TEXT NOT NULL` (artifact/file/task_event/…)
  - `source_id TEXT NOT NULL` (artifact_id, file path hash, etc.)
  - `workspace_id TEXT NULL`
  - `project_id TEXT NULL`
  - `title TEXT`
  - `mime_type TEXT`
  - `text TEXT NOT NULL` (plain text extracted from markdown/code)
  - `text_hash TEXT NOT NULL` (for incremental reindex)
  - `created_at TEXT`
  - `updated_at TEXT`

**FTS5**
- `documents_fts` (virtual table)
  - columns: `title`, `text`, plus optional `tags`/`type`
  - linked to `documents` (external content) or maintained manually

**Embeddings (sqlite-vec)**
- `document_embeddings` (sqlite-vec virtual table or regular table + vec column)
  - contains the embedding vector for each `document_id`
  - stores the embedding dimension and model id/version

Important: the exact DDL for sqlite-vec is extension-specific and must be verified against the sqlite-vec README at implementation time.

### 3.4 Migrations

Implement a minimal migration runner in `CodeAlta.Persistence`:
- `schema_version` table storing applied migration ids.
- migrations expressed as embedded SQL strings (no external tool required).
- each migration is idempotent and uses `IF NOT EXISTS` where possible.

Testing:
- create DB in temp directory
- apply migrations
- assert tables exist and schema_version updated

## 4. Indexing pipeline

### 4.1 Inputs (indexable sources)

First-class indexable sources (v1):
- markdown artifacts from `.codealta/**` and `~/.codealta/repo/**`
- task notes/events (exported into artifact markdown + indexed)
- selected repo files (opt-in rules; default to `.md`, `.cs`, `.csproj`, `.slnx`, `AGENTS.md`)

Later sources:
- agent conversations / run summaries
- git history snapshots
- diagnostics reports

### 4.2 Document extraction and chunking

For each source we produce 1..N “documents”:
- `Document` record: `(source_kind, source_id, title, text, metadata)`
- Chunking strategies:
  - markdown: chunk by headings and/or max token/char size (Markdig AST)
  - code: chunk by file sections (simple: fixed line windows; later: Roslyn symbols)

Store:
- canonical “artifact markdown” on disk
- extracted plain text in SQLite `documents.text` (for FTS and embeddings)

### 4.3 Incremental updates

Each extracted document includes a `text_hash` (SHA-256 or xxHash64):
- If unchanged, skip re-embedding.
- If changed, update `documents` row and associated FTS + embeddings.

### 4.4 Indexing scheduling

We need non-blocking behavior:
- `IndexingQueue` (channel-based) with bounded concurrency.
- `Indexer` runs as a background service, emits progress:
  - items processed
  - current source
  - embedding batch timings

Expose to MCP as:
- `codealta.search.index` (enqueue)
- `codealta.search.status` (queue depth, last completion)

## 5. Search (FTS5 + vector + hybrid)

### 5.1 Full-text search (FTS5)

FTS5 supports:
- fast keyword search
- phrase search
- BM25 ranking

Plan:
- `SearchService.QueryFtsAsync(query, filters, limit)`
- returns `document_id` + snippet + score

### 5.2 Vector search (sqlite-vec)

Vector search supports:
- semantic similarity based on embeddings

Plan:
- `SearchService.QueryVectorAsync(embedding, filters, k)`
- returns `document_id` + distance/score

### 5.3 Hybrid retrieval (recommended default)

In v1 we implement a pragmatic hybrid:
1) Run FTS5 to prefilter candidates (fast, narrows the set).
2) Compute embedding for query.
3) Vector-rerank the top N candidates (or union with pure vector top K).
4) Return results with:
   - stable links back to artifacts/files/tasks
   - enough snippet context to be useful

This avoids needing a full ANN index strategy beyond sqlite-vec’s capabilities.

### 5.4 Result linking contract

Every search result must be linkable back to source:
- `artifact://...` URI
- `file://...` path + line range
- `task://...`

SQLite rows store this in `documents.source_kind/source_id`, plus optional structured metadata JSON.

## 6. Embeddings via LLamaSharp

### 6.1 Model lifecycle

`EmbeddingModelManager` responsibilities:
- resolve “current embed model” (default: a small local GGUF)
- download model if missing (optional in v1; can be manual path first)
- load weights once and keep them alive
- expose embedder as a shared service

### 6.2 Performance considerations

- Keep one loaded model per process by default.
- Limit concurrent embedding requests (batching where possible).
- Normalize embeddings consistently (L2 normalization) if the similarity metric expects it.

## 7. Native extension loading (sqlite-vec)

We need a controlled way to load sqlite-vec:
- store the extension binary under `~/.codealta/state/extensions/`
- during DB initialization:
  - open connection
  - `EnableExtensions(true)`
  - `LoadExtension(pathToSqliteVec)`

This is likely the part that requires manual integration work and platform testing.

## 8. Tests (minimum)

Add MSTest coverage:
- Migration application creates expected tables.
- Artifact store writes/reads markdown + frontmatter.
- FTS5 indexing returns expected hits.
- Hybrid search returns stable source links.

Keep fixtures small and deterministic (no network downloads in unit tests).
