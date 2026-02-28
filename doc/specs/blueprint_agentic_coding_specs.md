# Blueprint: Agent Orchestration & Memory (Draft)

Last updated: **2026-02-28**

This document expands the agent + memory ideas described in `doc/specs/blueprint_codealta_specs.md`.

It focuses on two hard problems:

1. **Limited context windows** (typically 200K–400K tokens) for any single agent session/thread.
2. **Coordinating multiple agents** across multiple workspaces/projects without losing continuity.

Companion docs:

- `doc/specs/agent_api_specs.md` (shared backend/session API)
- `doc/specs/blueprint_mcp_server_specs.md` (built-in MCP services: tasks/search/skills)

---

## 1. Terminology (normalized)

- **Backend**: the runtime that executes sessions (Codex, Copilot).
- **Session/Thread**: the backend conversation container.
- **Agent**: a long-lived logical worker with a role + scope (implemented using one backend session).
- **Workspace**: a named collection of projects.
- **Project**: a folder root (often a git repo) with analyzers/indexers attached.
- **Task**: durable work item stored in SQLite (shared state).
- **Knowledge record**: durable, retrievable snippet with a source reference.
- **Context pack**: the material injected into a session to perform a task within the context budget.
- **Scope directive**: an explicit user-provided selector (workspace/project/path) used by the global agent to route work and attach context.

---

## 2. Agent hierarchy (role × scope)

We want both specialization (role) and locality (scope).

### 2.1 Roles

- **Knowledge agent**
  - answers questions
  - maintains summaries and knowledge records
  - curates retrieval results with citations
- **Planner agent**
  - converts goals into task trees
  - manages dependencies, priorities, and ownership
  - keeps plans synchronized with reality (build/test results, git state)
- **Builder agent**
  - executes tasks (code changes, tests, refactors)
  - produces patches and verifies via tools (compiler/test/git)

Optional future roles:

- **Reviewer agent** (cross-check diffs, style, correctness)
- **Indexer agent** (background indexing + freshness management)
- **Skiller agent** (skills discovery/activation; see §6)

### 2.2 Scopes

- **Global scope**
  - knows *all workspaces*
  - routes user requests to the right workspace/project
  - coordinates cross-workspace work explicitly (opt-in)
- **Workspace scope**
  - knows all projects in the workspace
  - can plan/build across multiple projects
- **Project scope**
  - knows files, symbols, and local conventions
  - does most implementation work

### 2.3 Identity model

Each agent has:

- `agentId` (UUID v7, generated via `Guid.CreateVersion7()`)
- `(role, scope)` tuple
- `workspaceId` and/or `projectId` depending on scope
- backend session id (`threadId`/`sessionId`)
- declared capabilities (tools it can provide to other agents)

Agents register with the MCP server and can be discovered/invoked by other agents.

### 2.4 Role profiles (custom agents)

Roles should be defined as **file-based profiles** so new roles can be added without recompiling CodeAlta.

Design goals:

- **Progressive disclosure**: load only `(name, description)` for discovery; load full prompt only when the role is instantiated.
- **Copilot compatibility**: support `.github/agents/*.md` profiles (GitHub Copilot custom agents format) as an input source.
- **CodeAlta extensions**: allow extra metadata such as `id` (UUID v7, generated via `Guid.CreateVersion7()`), default scope, and capability declarations.

At runtime, an agent instance references a role profile (by `name` and/or `id`) and the orchestrator injects the profile prompt into the session context pack.

---

## 3. Core problem: context window management

### 3.1 Context tiers

Treat context as a budgeted set of tiers (highest to lowest priority):

1. **Pinned**: explicit user/agent pinned notes, constraints, files, policies.
2. **Task-critical**: the current task, acceptance criteria, and relevant plan steps.
3. **Working set**: a small set of files/symbols actively being edited or discussed.
4. **Retrieved**: semantic/keyword search results (snippets with citations).
5. **Summaries**: workspace/project/file summaries (compressed, stable).
6. **Background**: optional “nice to have” context (recent commits, related tasks).

Rule: never let “background” evict “task-critical”.

### 3.2 Context pack builder (high-level algorithm)

Inputs:

- user request (goal)
- active workspace/project (UI selection + routing)
- active task tree (SQLite)
- budget (tokens/bytes; per backend/model)

Output:

- a context pack (structured sections) that is injected into the agent session

Suggested algorithm:

1. **Resolve scope**:
   - prefer an out-of-band scope from the UI (active workspace/project picker)
   - otherwise parse an inline scope directive (e.g. `@workspace(...)`, `@project(...)`, `@path(...)`)
   - if the directive is ambiguous or unknown, ask a clarifying question instead of guessing
   - if no directive exists, use the current active scope
2. **Load pinned context** for the scope (workspace + project).
3. **Load task tree**:
   - current task + parents (goal) + immediate children (next steps)
4. **Build candidate retrieval queries**:
   - from task title, file paths, symbols, git diff keywords
5. **Retrieve knowledge records**:
   - semantic search (embeddings) + keyword search (FTS5) + graph search (dependencies)
6. **Select working set**:
   - include only the files/symbols likely to be edited next
7. **Budget enforcement**:
   - trim retrieved snippets first
   - then summaries
   - only include full file contents when explicitly required

Notes:

- The context pack should be deterministic and explainable (auditable).
- The pack should include citations (source pointers) for every snippet.

---

## 4. Coordination model: tasks as shared state

### 4.1 Why tasks must be durable

Agents are not durable. SQLite is.

If plans live only in agent memory, they are lost on:

- compaction/summarization
- switching workspaces
- restarting the app
- changing models/backends

Therefore:

- Plans are **stored as task trees** in SQLite.
- Agents read/update tasks through MCP services.

### 4.2 Task lifecycle (suggested)

Statuses:

- `pending`
- `in_progress`
- `blocked`
- `waiting_for_user`
- `done`
- `canceled`
- `failed`

Required task fields:

- `taskId` (UUID v7, generated via `Guid.CreateVersion7()`)
- `title`, `description`
- `scope` (workspace/project/file refs)
- `assignedAgentId` (nullable)
- `status`
- timestamps
- optional `links[]` (related files/symbols/commits/knowledge records)

### 4.3 Planning flow

1. Planner agent creates a root task representing the user goal.
2. Planner agent decomposes into subtasks (usually project-scoped).
3. Planner agent assigns subtasks to builder agents (and reviewer agent, if present).
4. Builder agents update status + attach artifacts:
   - diff references
   - build/test outputs
   - knowledge records created during implementation

---

## 5. Execution model: builder agents

Builder agents are the “doers”. The blueprint assumes three hard requirements:

1. **Tool grounding** (compiler/test/git/file system).
2. **Safety** (approval policies, audit logs, undo).
3. **Verification loop** (repeat until acceptance criteria met).

### 5.1 Verification loop (example)

For a .NET project (first-class in v1.0), the default loop is:

1. Edit (patch) files
2. Run `dotnet build` (or targeted build)
3. Fix diagnostics
4. Run tests (`dotnet test`, filtered when possible)
5. Mark task done only when green

Where possible, integrate a Roslyn host for faster semantic feedback.

For other languages, CodeAlta should remain language-agnostic:

- run configured build/test commands for the project
- rely on generic retrieval (FTS5 + embeddings) and repo-local artifacts
- later: add language-specific “live servers” (LSP equivalents) where valuable

### 5.2 Handling limited backend capabilities

Some backends support features the other doesn’t (tools, attachments, specific events).

Rule:

- Keep CodeAlta’s orchestration/tooling stable.
- Use backend-specific features only behind adapters with clear fallbacks.

---

## 6. Skills integration (agent skills)

Skills exist to avoid re-teaching workflows in every session.

Key constraints (from Agent Skills spec):

- skills are directories containing `SKILL.md` with YAML frontmatter (`name`, `description`)
- progressive disclosure is required:
  - load only metadata globally
  - load full instructions only on activation

### 6.1 Why a dedicated Skiller agent helps

If every agent loads all skills:

- context is wasted
- skill matching becomes noisy

Instead:

- only the Skiller agent keeps the full skills catalog active
- other agents query the Skiller agent (via MCP) for:
  - “which skills might apply?”
  - “fetch the SKILL.md for skill X”
  - “validate skill X before running scripts”

### 6.2 Skills as tools vs filesystem

CodeAlta can support both:

- **Filesystem-style**: agent reads skill files via file tools.
- **Tool-style**: MCP exposes `skills.list`, `skills.get`, `skills.activate`.

The tool-style approach keeps the backend context smaller and is easier to audit.

---

## 7. Session lifecycle & continuity

### 7.0 Compaction-safe storage (files on disk)

To survive context compaction and to enable “recoverable memory”, agent work products must be durable.

Guideline:

- **Project files are the source of truth** and already exist on disk.
- **Derived/extracted knowledge** (summaries, plans, decisions, retrieval results, run logs) must be stored as **plain files** (Markdown/JSON) and linked from SQLite.

Storage locations (both are useful):

- **Repo-local (`.codealta/`)**: for project-scoped, shareable knowledge that should travel with the repository.
  - `<projectRoot>/.codealta/` (similar to `.github/`)
  - recommend splitting:
    - `.codealta/shared/` (optionally committed)
    - `.codealta/local/` (gitignored; caches, per-machine logs)
- **Per-user (`$HOME/.codealta/`)**: for workspace/global artifacts and anything sensitive or machine-specific.
  - This directory can be a **git working copy** of a “global knowledge repository” when multi-machine portability is desired.
  - Keep secrets out of git; store only manifests, curated artifacts, and machine config without credentials.

Suggested root directory:

- Linux/macOS: `$HOME/.codealta/`
- Windows: `%USERPROFILE%\\.codealta\\`

Suggested structure (example):

```
~/.codealta/
  workspaces/<workspaceKey>/
    workspace.md
    projects/<projectKey>/
      project.md
      summaries/
      decisions/
    tasks/<taskId>/
      plan.md
      notes.md
    runs/<agentId>/
      2026-02-28T12-34-56Z.md
```

Repo-local structure (example):

```
<projectRoot>/.codealta/
  shared/
    project.md
    summaries/
    decisions/
  local/
    cache/
    runs/
```

Artifact format:

- prefer Markdown with YAML frontmatter for stable machine parsing (ids, scope, timestamps, tags)
- ensure artifact contents are indexed into SQLite **FTS5** so we can do full-text search across all documents (with or without embeddings)

SQLite should store:

- artifact type (`workspace_summary`, `task_plan`, `decision_record`, …)
- scope references (workspaceId/projectId/taskId)
- `path` + `contentHash` + timestamps

This allows agents to reload exactly the right artifacts after compaction without inflating every session’s context.

### 7.1 Persistent vs ephemeral sessions

- **Persistent sessions**: long-lived agents (workspace knowledge/planner/builder).
- **Ephemeral sessions**: short-lived helpers (researcher/reviewer), aggressively summarized.

### 7.2 Session compaction and memory capture

After a task completes:

- summarize “what changed” and “why”
- store decisions and anchors as knowledge records
- link them to tasks and files

The goal is that future sessions can retrieve the summary + citations without reloading full history.

### 7.3 Workspace switching

Switching workspaces should:

- keep agent sessions alive (unless user stops them)
- change routing + default scope for new tasks
- update pinned context and retrieval scope

### 7.4 Workspace portability (multi-machine)

If a workspace spans multiple repositories, paths are not stable across machines.

Suggested approach:

- allow a workspace to declare a **workspace home**:
  - local directory (default)
  - or a **git-backed “workspace repository”** containing workspace manifests + curated artifacts
- CodeAlta can clone/sync the workspace repo on a new machine, then reconstruct the workspace by:
  - cloning required project repos (by git remote URL)
  - applying local path mappings

### 7.5 Global knowledge repository and “setup my dev experience”

For multi-machine portability, prefer a single **global knowledge repository** that knows about *all* workspaces and projects.

The global repo should contain:

- a global workspace registry (all workspaces + manifests)
- curated artifacts (global/workspace summaries, decision records, playbooks)
- templated checkout rules for projects (avoid absolute paths)
- optional per-machine overrides (path roots), without secrets

Bootstrapping flow on a new machine:

1. clone the global knowledge repo into `$HOME/.codealta/` (or Windows equivalent)
2. select machine profile (e.g. by `machineId`/hostname) to resolve path roots
3. clone/sync the repositories for selected workspaces using checkout templates
4. start agents and rebuild indexes

---

## 8. Example scenarios (end-to-end)

### Scenario A: “Add feature across two projects in one workspace”

1. Global planner creates root task.
2. Workspace planner decomposes into per-project subtasks.
3. Two project builders execute in parallel (each with its own context pack).
4. Reviewer agent checks cross-project consistency.
5. Knowledge agent stores decision record + links to diffs/tests.

### Scenario B: “Explain why we chose X last month”

1. Global knowledge agent queries semantic search for decision records + conversation anchors.
2. Returns a compact explanation plus citations (links to tasks/messages/commits).
