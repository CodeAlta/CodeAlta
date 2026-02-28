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

- every agent has a stable identity (GUID)
- other agents can discover “who can do what”
- agents can form hierarchies (parent/child)

### 2.2 Suggested data (minimum)

- `agentId` (GUID)
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

- `artifactId` (GUID)
- `type` (`workspace_summary`, `project_summary`, `task_plan`, `decision_record`, `run_log`, …)
- scope references (`workspaceId`, `projectId`, `taskId`, `agentId`)
- `path`, `contentHash`, timestamps

---

## 5. Semantic search service

### 5.1 Indexable sources (first-class)

- file chunks (path + line range + hash)
- symbols (Roslyn)
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

- `search.query(text, scope, limit?, filters?) -> results[]`
- `search.query_vector(embedding, scope, limit?, filters?) -> results[]`
- `search.reindex(scope, mode)` (manual rebuild)
- `search.status(scope) -> indexingStatus`

Implementation note:

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

## 7. Workspace/project services (optional but recommended)

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

These tools are used by the global agent to route work reliably.

---

## 8. Security & audit

Minimum requirements:

- every tool call is logged (agentId, timestamp, parameters summary, outcome)
- file and command execution are gated by approval policy
- scope boundaries are enforced (agents can only access allowed roots for their workspace/project)

Recommended:

- append-only audit log (file on disk + optional SQLite table)
- “undo” model for file changes (store patches)

---

## 9. Versioning

We need three versions:

- MCP tool schema version (what tools exist and their shapes)
- DB schema version (SQLite migrations)
- index version (embedding model + chunking strategy)

All should be queryable via a `server.info` tool so agents can adapt.
