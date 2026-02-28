# Blueprint: Built-in MCP Server (Draft)

Last updated: **2026-02-28**

This document proposes how CodeAlta embeds a **built-in MCP server** to provide durable services to agents:

- agent registry + inter-agent coordination
- tasks/plans (SQLite)
- semantic search (SQLite + embeddings)
- skills (Agent Skills format)
- compaction-safe artifacts (Markdown on disk)

It is intentionally implementation-oriented but not locked to a specific transport. The goal is a stable “tools/services layer” that survives backend changes (Codex/Copilot).

Companion docs:

- `doc/specs/blueprint_codealta_specs.md` (overview)
- `doc/specs/blueprint_agentic_coding_specs.md` (agent hierarchy + memory)

---

## 1. Deployment model

CodeAlta runs an MCP server **locally** as part of the application:

- default: in-process (fast, simple)
- optional: out-of-process for debugging/compatibility (still local)

Transport options (pick one per implementation phase):

- MCP over stdio (agent spawns the server)
- MCP over local TCP/Unix domain socket/Named pipe (server is long-running)
- “In-process MCP” adapter (no serialization; still modeled as tools)

Regardless of transport, services should be designed as **idempotent** and **auditable**.

---

## 2. Agent registry

### 2.1 Goals

- every agent has a stable identity (UUID v7, generated via `Guid.CreateVersion7()`)
- other agents can discover “who can do what”
- agents can form hierarchies (parent/child)

### 2.2 Suggested data (minimum)

- `agentId` (UUID v7, generated via `Guid.CreateVersion7()`)
- `name`
- `description`
- `role` (knowledge/planner/builder/…)
- `scope` (global/workspace/project)
- `workspaceId` / `projectId` (nullable, based on scope)
- `capabilities[]` (string identifiers)
- `parentAgentId` (nullable)
- `backend` + `backendSessionId` (optional for debugging)

### 2.3 Suggested tools

- `agent.register(agentInfo)`
- `agent.heartbeat(agentId)`
- `agent.unregister(agentId)`
- `agent.list(filter?)`
- `agent.get(agentId)`

---

## 3. Task & plan service (SQLite-backed)

### 3.1 Requirements

- tasks are durable and queryable
- tasks are hierarchical (plans as trees)
- tasks are scoped (workspace/project/file)
- tasks can attach artifacts and knowledge references

### 3.2 Suggested tools

- `task.create(scope, title, description?, parentTaskId?) -> taskId`
- `task.update(taskId, patch)`
- `task.assign(taskId, agentId?)`
- `task.list(filter) -> tasks[]`
- `task.get(taskId) -> task`
- `task.add_comment(taskId, markdown, authorAgentId?)`
- `task.add_link(taskId, link)` (file/symbol/commit/knowledge/artifact)

Optional:

- `plan.generate(taskId, options) -> planArtifactId` (planner agent can call this too)

---

## 4. Compaction-safe artifact store (files on disk)

### 4.1 Why artifacts must be files

If knowledge/plans only live in the session transcript, they will be lost or compressed.

Therefore:

- store derived documents as **plain files** (Markdown/JSON)
- store metadata + pointers in SQLite

### 4.2 Root directory

Artifacts can be stored in multiple roots depending on scope and portability goals:

- **Repo-local (project-scoped, shareable)**:
  - `<projectRoot>/.codealta/` (similar to `.github/`)
  - recommended split:
    - `.codealta/shared/` (optionally committed)
    - `.codealta/local/` (gitignored; caches/logs)
- **Per-user (workspace/global or sensitive)**:
  - Linux/macOS: `$HOME/.codealta/`
  - Windows: `%USERPROFILE%\\.codealta\\`
  - When multi-machine portability is desired, this directory can be a **git working copy** of a “global knowledge repository” (auto pull/push), containing global manifests + curated artifacts (no secrets).

The MCP server should decide the default root based on `(scope, type, policy)`, but callers may provide a storage hint.

### 4.3 Suggested tools

- `artifact.write(scope, type, title?, content, format="markdown", storageHint?) -> artifactId`
- `artifact.read(artifactId) -> content`
- `artifact.list(filter) -> artifacts[]`

Artifacts should be content-addressed (store `contentHash`) so agents can detect staleness.

Recommended artifact format:

- Markdown with YAML frontmatter (ids, scope refs, timestamps, tags), similar to Agent Skills’ `SKILL.md`

### 4.4 Linking artifacts to SQLite

SQLite stores:

- `artifactId` (UUID v7, generated via `Guid.CreateVersion7()`)
- `type` (`workspace_summary`, `project_summary`, `task_plan`, `decision_record`, `run_log`, …)
- scope references (`workspaceId`, `projectId`, `taskId`, `agentId`)
- `path`, `contentHash`, timestamps

---

## 5. Semantic search service

Search should support both:

- **Full-text search (SQLite FTS5)** for exact/keyword queries across all indexed documents
- **Semantic search (embeddings)** for similarity-based retrieval with citations

In practice, most queries should use a **hybrid** approach (FTS5 prefilter + embeddings rerank).

### 5.1 Indexable sources (first-class)

- file chunks (path + line range + hash)
- symbols (Roslyn for .NET in v1.0; later: other language services)
- git artifacts (commit messages, diffs, blame snippets)
- task comments and decision records
- conversation anchors (sessionId/messageId) when valuable

### 5.2 Retrieval contract

Every search result must include a **source reference**:

- file path + line range + file hash (and optionally commit id)
- artifact id + path + hash
- task id
- conversation anchor id

This avoids “orphan knowledge” and enables reconstruction.

### 5.3 Suggested tools

- `search.query_text(query, scope, limit?, filters?) -> results[]` (FTS5)
- `search.query(text, scope, limit?, filters?) -> results[]` (hybrid: FTS5 + embeddings)
- `search.query_vector(embedding, scope, limit?, filters?) -> results[]`
- `search.reindex(scope, mode)` (manual rebuild)
- `search.status(scope) -> indexingStatus`

Implementation note:

- use SQLite **FTS5** virtual tables to index all textual content (file chunks, artifacts, task comments, decision records)
- start with FTS5 + embeddings stored as BLOB
- add a SQLite vector extension later (sqlite-vec/sqlite-vss) once packaging is proven cross-platform

---

## 6. Skills service (Agent Skills format)

Skills should be integrated as a service so agents don’t load all skills into context.

Suggested tools:

- `skills.list(scope?) -> [{ name, description, path, metadata }]`
- `skills.get(skillName) -> SKILL.md content`
- `skills.get_resource(skillName, relativePath) -> content/bytes`
- `skills.validate(skillPath) -> validationResult`

Security:

- scripts/resources are untrusted by default
- the server should support allowlisting skill directories and require approvals before execution

---

## 7. Agent role profiles service (custom agents)

Agent roles should be defined as files so new roles can be added without recompiling CodeAlta.

Compatibility goal:

- Support GitHub Copilot “custom agents” profiles (`.github/agents/*.md`) as an input source.

Suggested discovery locations:

- Repo: `.github/agents/*.md` (Copilot-compatible)
- Repo: `<projectRoot>/.codealta/agents/*.md` (CodeAlta-specific)
- User: `$HOME/.codealta/agents/*.md`
- Workspace home/repo (optional): `<workspaceHome>/agents/*.md`

Suggested profile format (compatible superset):

- Markdown with YAML frontmatter (`name`, `description` required)
- Body is the role prompt/instructions
- Optional (Copilot-compatible): `tools`, `mcp-server`
- Optional (CodeAlta extensions): `id` (UUID v7, generated via `Guid.CreateVersion7()`), `defaultScope`, `capabilities`, `metadata`

Suggested tools:

- `roles.list(scope?) -> [{ name, description, id?, path, metadata? }]`
- `roles.get(nameOrId) -> { frontmatter, prompt, path }`
- `roles.validate(path) -> validationResult`

---

## 8. Workspace/project services (optional but recommended)

Suggested tools:

- `workspace.list()`
- `workspace.get(workspaceId)`
- `workspace.create(name, projects[], settings?)`
- `workspace.activate(workspaceId)`
- `workspace.add_project(workspaceId, path, metadata?)`
- `workspace.remove_project(workspaceId, projectId)`

Portability tools (multi-machine):

- `workspace.export(workspaceId) -> workspaceManifest`
- `workspace.import(workspaceManifest) -> workspaceId`
- `workspace.set_home(workspaceId, homePathOrGitRemote)` (optional: git-backed workspace repo)

Global knowledge repository tools (multi-machine):

- `global_repo.configure(remoteUrl, branch?, autoSync?)`
- `global_repo.sync(mode?)` (e.g. pull/push/rebase; conflict reporting)
- `global_repo.status() -> { lastSync, dirty, remote, branch }`

Bootstrap tools (“setup my dev experience”):

- `bootstrap.plan(workspaceIds?, machineId?) -> plan` (templated checkout rules → concrete paths)
- `bootstrap.apply(plan) -> result` (clone/sync repos, set active scope, rebuild indexes)

Machine configuration tools (path roots, no secrets):

- `machine.get_profile() -> { machineId, roots, overrides }`
- `machine.set_roots(rootsPatch)`

Optional scope helpers (for “global agent” UX and routing):

- `workspace.resolve(nameOrId) -> workspaceId`
- `project.resolve(workspaceId, nameOrId) -> projectId`
- `context.get_active_scope() -> { workspaceId, projectId? }`
- `context.set_active_scope(workspaceId, projectId?)`

These tools are used by the global agent to route work reliably.

---

## 9. Security & audit

Minimum requirements:

- every tool call is logged (agentId, timestamp, parameters summary, outcome)
- file and command execution are gated by approval policy
- scope boundaries are enforced (agents can only access allowed roots for their workspace/project)

Recommended:

- append-only audit log (file on disk + optional SQLite table)
- “undo” model for file changes (store patches)

---

## 10. Versioning

We need three versions:

- MCP tool schema version (what tools exist and their shapes)
- DB schema version (SQLite migrations)
- index version (embedding model + chunking strategy)

All should be queryable via a `server.info` tool so agents can adapt.
