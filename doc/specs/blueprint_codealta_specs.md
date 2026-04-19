# CodeAlta Blueprint (Draft)

Last updated: **2026-02-28**

Historical note: sections in this blueprint that describe a separate workspace layer are obsolete. The active model is global plus projects, with no required workspace registry.

This document is the high-level blueprint for **CodeAlta**: a local-first, multi-backend, multi-agent coding assistant intended to work on real projects (multiple folders, multiple repositories, long-lived sessions) despite limited model context windows (typically **200K–400K tokens**).

This is a “why/what” blueprint, not a full protocol spec. Detailed designs live in the companion documents:

- `doc/specs/agent_api_specs.md` (shared backend/session API for Codex + Copilot)
- `doc/specs/agent_configuration_spec.md` (file format and compatibility rules for agent definitions)
- `doc/specs/blueprint_agentic_coding_specs.md` (agent hierarchy, orchestration, memory/context strategy)
- `doc/specs/blueprint_mcp_server_specs.md` (built-in MCP server: agent registry, tasks DB, semantic search, skills)

---

## Table of contents

1. [Problem statement](#1-problem-statement)
2. [Goals and non-goals](#2-goals-and-non-goals)
3. [Design principles](#3-design-principles)
4. [Core concepts](#4-core-concepts)
5. [High-level architecture](#5-high-level-architecture)
6. [Multi-workspace model](#6-multi-workspace-model)
7. [Memory window strategy](#7-memory-window-strategy)
8. [Agent hierarchy](#8-agent-hierarchy)
9. [Built-in MCP server](#9-built-in-mcp-server)
10. [Storage, indexing, semantic search](#10-storage-indexing-semantic-search)
11. [Skills integration](#11-skills-integration)
12. [Roadmap (suggested)](#12-roadmap-suggested)
13. [Open questions](#13-open-questions)

---

## 1. Problem statement

Agentic coding systems fail in predictable ways:

- **Context limits**: a single session cannot hold a whole multi-project codebase + history + decisions.
- **Lack of grounding**: without compilers/indexers, agents hallucinate APIs and miss cross-project breakage.
- **Lack of continuity**: decisions and rejected approaches are forgotten between runs.
- **Single-thread bottleneck**: one agent doing planning + searching + building is slow and brittle.

CodeAlta’s direction is to make these failures *system problems* solved by architecture:

- Multiple workspaces + persistent knowledge store + semantic retrieval.
- Multiple specialized agents coordinated by shared state (tasks/plans).
- A built-in MCP server exposing stable services/tools to all agents.

---

## 2. Goals and non-goals

### Goals (what we want)

- **Multiple workspaces** with a global registry (the app knows them all).
- **Git-backed global knowledge**: persist global registry + knowledge under a dedicated git repo (auto-sync/push), so a new machine can be bootstrapped from it.
- **Workspace switching** without losing running sessions, task state, or high-level context.
- **Hierarchical agents**:
  - global agent (user entry point)
  - knowledge/planner/builder agents at global/workspace/project scopes
- **Shared task/plan tracking** stored in SQLite and accessible via MCP services.
- **Semantic search** over files, symbols, git history, agent discussions, tasks/decisions.
- **Keep the core language-agnostic first**: preserve existing .NET support code, but do not let .NET specialization drive the near-term architecture before orchestration, portability, and adaptive memory are solid.
- **Skills integration** (Agent Skills format) with progressive disclosure to avoid bloating context windows.

### Non-goals (first cut)

- Perfect 1:1 feature parity between Copilot and Codex backends.
- Distributed multi-user collaboration (can be added later; design should not block it).
- Indexing “the entire internet”; we focus on workspace-local and repo-local knowledge.
- First-class semantic language services for non-.NET languages in v1.0 (initial support remains useful via generic tooling + search; add language-specific servers later).

---

## 3. Design principles

- **Local-first, auditable**: store state on disk (SQLite + files), keep an audit trail of agent actions.
- **Memory-aware by default**: everything should support progressive disclosure and context budgets.
- **Grounded outputs**: prefer compiler/indexer facts and citations over free-form guesses.
- **Scope-first**: every operation is scoped (global/workspace/project/file) to reduce mistakes.
- **Language-agnostic core**: orchestration/storage do not assume a language. Language-specific intelligence remains pluggable and should not dominate the near-term architecture.
- **Extensible backends**: Codex/Copilot are backends; everything else is CodeAlta-owned logic.
- **Safe execution**: file changes and command execution go through explicit approval policies.

---

## 4. Core concepts

### 4.1 Workspace

A workspace is a named collection of projects plus settings.

Key constraints:

- A **project cannot belong to more than one workspace at a time**.
- Users can switch workspaces without losing current context (sessions/tasks remain).

Suggested identity:

- `workspaceId` (UUID v7, generated via `Guid.CreateVersion7()`)
- `name`
- `projects[]` (list of project roots and metadata)
- `settings` (backend preferences, indexing options, skill directories, etc.)

### 4.2 Project

A project is a folder root with detected type(s) and optional graph edges:

- `dotnet` solution/project graph
- `node` package graph
- `git` repo metadata

Projects are the unit for:

- indexing boundaries (file watchers, symbol extraction)
- permission boundaries (allowed roots)

### 4.3 Agent

An agent is a long-lived unit of work with an identity and scope.

- `agentKey` (stable textual key, typically derived from the `.agent.md` filename)
- `role` (knowledge / planner / builder / reviewer / …)
- `scope` (global / workspace / project)
- `backend` (Codex/Copilot) + backend session/thread id
- `capabilities[]` (declared services/tools it can provide)

### 4.4 Agent role profiles (custom agents)

It should be easy to define new agent roles without code changes by using file-based **agent role profiles** (similar to skills).

Compatibility goal:

- Support GitHub Copilot “custom agents” format (`.github/agents/*.md`) so roles can be shared across tools.

Suggested discovery locations (highest precedence first):

- Repo: `.github/agents/*.md` (Copilot-compatible)
- Repo: `<projectRoot>/.codealta/agents/*.agent.md` (CodeAlta-specific)
- User: `$HOME/.codealta/agents/*.agent.md`
- Workspace home/repo (optional): `<workspaceHome>/agents/*.agent.md`

Suggested format (compatible superset):

- Markdown file with YAML frontmatter
- Preferred filename: `<agent-key>.agent.md`
- Required: `description`
- Body: the role prompt/instructions
- Optional (Copilot-compatible): `tools`, `mcp-server`
- Optional (CodeAlta extensions): `name`, `defaultScope`, `capabilities`, `metadata`

Built-in roles (knowledge/planner/builder/…) should ship as built-in profiles but remain overrideable by repo/workspace/user profiles.

### 4.5 Task and plan

Tasks are first-class, durable coordination primitives:

- hierarchical (parent/child)
- assignable to agents
- scoped to workspace/project/file
- status tracked (`pending`, `in_progress`, `blocked`, `done`, …)

Tasks live in SQLite (via MCP service), not in an agent’s ephemeral memory.

### 4.6 Knowledge record

A knowledge record is an indexed unit that can be retrieved later, always with a source link:

- file slice (path + line range + hash)
- symbol (fully-qualified name + signature)
- git commit / diff / blame snippet
- conversation anchor (sessionId + messageId)
- task / decision entry

### 4.7 Agent artifacts (disk)

Compaction-safe continuity requires that agent outputs are not only “in the chat”.

For planner/knowledge (and optionally builder) outputs, store durable artifacts as plain files on disk:

- Markdown summaries (workspace/project/file)
- Plans and task breakdowns
- Decision records
- Retrieval snapshots (“why this was suggested”)
- Agent run logs (what tools were called, high-level outcomes)

These artifacts should be stored in a way that supports **portability** and **review**:

- **Project-scoped, shareable artifacts**: store under a repo-local folder (similar to `.github`):
  - `<projectRoot>/.codealta/`
  - these can be committed when you want knowledge to travel with the repository
- **User/workspace-scoped or sensitive artifacts**: store under a central per-user root:
  - Linux/macOS: `$HOME/.codealta/`
  - Windows: `%USERPROFILE%\.codealta\`

To keep artifacts machine-readable, prefer **Markdown files with YAML frontmatter** (similar to Agent Skills’ `SKILL.md` and optionally project `AGENTS.md`).

For the portable per-user catalog, workspace and thread folders should use lowercase `readme.md` as their canonical metadata file, while project descriptors should use flat files named `<projectSlug>.md`.
Example:

```md
---
id: 01963b36-0d6f-7e4b-a7e0-6b2e6d1f4c8a
type: project_summary
projectId: <projectId>
workspaceId: <workspaceId>
createdAt: 2026-02-28T12:34:56Z
tags: [architecture, onboarding]
---

# Project summary
...
```

The SQLite DB stores metadata + pointers to these files (path + hash), so agents can reload them after compaction.

### 4.8 Skill

Skills follow the **Agent Skills** format: a directory with `SKILL.md` + optional `scripts/`, `references/`, `assets/`.

Skills must be integrated with **progressive disclosure**:

- load only metadata (name + description) globally
- load full instructions only when activated

---

## 5. High-level architecture

At a high level:

```
User (CLI/TUI/IDE)
   │
   ▼
Global Orchestrator (CodeAlta)
   │   ├─ Agent backends (Codex, Copilot) via CodeAlta.Agent API
   │   ├─ Built-in MCP server (tools/services)
   │   ├─ Workspace registry + switcher
   │   └─ Knowledge store (SQLite) + indexers (Roslyn, Git, embeddings)
   ▼
Workspace/Project Agents (knowledge/planner/builder)
```

The key idea is **separating concerns**:

- backends provide chat/session execution
- CodeAlta provides memory, orchestration, persistence, tooling, and adaptive behavior

---

## 6. Multi-workspace model

Requirements:

- The app maintains a **global registry** of workspaces (`workspaceId`, `name`, `projects`, settings).
- Switching workspaces:
  - does not delete/lose sessions or tasks
  - changes the “active scope” for commands and prompts
- Projects:
  - are unique-membership (one workspace only)
  - can be added/removed (with indexing updates)

Recommended approach:

- Store the workspace registry in a global config file + global SQLite table.
- Each workspace has its own SQLite DB (or a workspace namespace in a single DB).
- The global agent routes user requests to the right workspace agent(s).

Portability note (multi-machine):

- Persist global metadata and knowledge in a **global knowledge repository** (git-backed) so a new machine can be bootstrapped by cloning it.
- Optionally, store workspace-specific curated artifacts in separate **workspace repositories**, but the global repository remains the “index of everything” (all workspaces/projects).

### 6.1 Scope selection (user-facing)

When the user interacts with the **global agent**, the user should be able to specify scope quickly and unambiguously, so the orchestrator can attach the right context (workspace/project summaries, retrieval results, active tasks).

Core behavior:

- Maintain an **active scope**: `activeWorkspaceId` and optional `activeProjectId`.
- If the user does not specify scope explicitly, default to the active scope.
- If the user specifies an unknown/ambiguous scope, ask a clarifying question rather than guessing.
- Always surface the active scope in the UI (status line / prompt header) so the user can see where work will happen.

Scope should be specifiable in at least three ways:

- **UI selection** (TUI/IDE): workspace/project picker sets the active scope out-of-band.
- **Commands**: e.g. `:ws <name|id>`, `:proj <name|id>`, `:scope global|workspace|project`.
- **Inline directives** (for plain chat-only environments): e.g. `@workspace(<name|id>)`, `@project(<name|id>)`, `@path(<path>)`.

The chosen scope affects:

- which agents are invoked (workspace vs project knowledge/planner/builder)
- which roots are allowed for file operations and approvals
- retrieval boundaries for FTS5/embeddings search
- where tasks and artifacts are stored/linked

### 6.2 Global knowledge repository and bootstrap

The per-user directory (`$HOME/.codealta/` on Linux/macOS, `%USERPROFILE%\\.codealta\\` on Windows) should be able to act as a **git working copy** of a “global knowledge repository”.

This repository is the durable global memory for CodeAlta. It should contain:

- the global workspace registry (all workspaces, all projects, and their metadata)
- curated knowledge artifacts (global/workspace-level summaries, decision records, playbooks)
- optional shared agent role profiles and policies
- checkout/bootstrapping rules so CodeAlta can “set up my dev experience” on a new machine

Bootstrapping should be mostly automatic:

1. clone the global knowledge repo (or initialize it)
2. resolve machine-specific settings (path roots, credentials strategy, etc.)
3. clone/sync the project repositories for selected workspaces using templated rules
4. rebuild indexes and start the relevant scoped agents

Machine variability:

- Manifests should use **templated local paths** (e.g. `${codeRoot}/${org}/${repo}`) rather than absolute paths.
- Each machine can override path roots (e.g. `codeRoot = C:\\code` on Windows vs `/code` on Linux).
- Store machine overrides either:
  - committed in the global repo under `machines/<machineId>.yml` (no secrets), or
  - locally outside git (preferred for privacy), with the repo containing defaults.

Sync strategy:

- Prefer “local-first” writes (update files/SQLite immediately).
- Background sync can `git pull --rebase` and `git push` automatically when configured (GitHub/GitLab/etc.), with clear conflict handling and audit logging.

---

## 7. Memory window strategy

The system must treat the context window as a scarce resource.

Core tactics:

- **Hierarchical summaries** (global → workspace → project → file/symbol).
- **Retrieval-first**: fetch only what is relevant (semantic + keyword + graph-based).
- **Pinned context**: users/agents can pin notes/files into the “always include” set.
- **Lazy materialization**: prefer manifests and symbol summaries over full file contents.
- **Session compaction**: summarize and store durable outcomes (decisions/tasks/anchors) after each run.

Implementation details are in `doc/specs/blueprint_agentic_coding_specs.md`.

---

## 8. Agent hierarchy

We want a consistent hierarchy across scopes:

- **Global agent**: user entry point, routes work, maintains cross-workspace awareness.
- **Knowledge agents**: answer questions with the right abstraction level.
  - global: knows all workspaces (structure + summaries)
  - workspace: knows projects and their shape
  - project: knows files/symbols and local decisions
- **Planner agents**: create/maintain plans and tasks.
  - global: multi-workspace plans
  - workspace: multi-project plans
  - project: file-level plans
- **Builder agents**: execute tasks (code changes, tests, refactors) within their scope.

Coordination primitives:

- tasks/plans in SQLite (shared state)
- MCP services for queries and actions
- event stream for progress updates (tasks, indexing, agent status)

---

## 9. Built-in MCP server

CodeAlta should embed an MCP server to provide a shared services layer.

Key responsibilities:

- **Agent registry**: agents register with id/name/description/capabilities; parent/child relationships.
- **Inter-agent communication**: request/response + publish/subscribe (task updates, indexing progress).
- **Task/plan service** backed by SQLite.
- **Semantic search service** backed by SQLite + embeddings.
- **Skills service** (discover, activate, validate skills).

Detailed tool/API surface is in `doc/specs/blueprint_mcp_server_specs.md`.

---

## 10. Storage, indexing, semantic search

### 10.1 Storage

Primary store: **SQLite** (local, portable, transactional).

In addition, use a **disk artifact store** for human-readable, compaction-safe documents (Markdown).
The DB links to artifacts rather than duplicating large derived text blobs.

At minimum, store:

- workspaces, projects, files
- agent sessions (metadata), conversation anchors
- tasks/plans and their history
- indexed knowledge records (with citations)
- embeddings vectors + search indexes
- artifact pointers (paths + hashes) for plans/summaries/decision records

### 10.2 Indexing sources

- Files (chunked + hashed)
- Symbols (Roslyn, where applicable)
- Git metadata (status/diff/log/blame; commit history as retrievable knowledge)
- Agent conversations and task logs
- Agent-generated artifacts (plans/summaries/decisions) stored under the owning project/workspace `.codealta/` area or the matching portable catalog scope under `$HOME/.codealta/`

### 10.3 Full-text search (SQLite FTS5)

In addition to semantic retrieval, we should support **fast exact/keyword search** across all indexed documents using SQLite’s **FTS5** module.

Rationale:

- Developers often need exact identifier matching (symbols, file names, error messages)
- FTS5 is a strong prefilter for embeddings (reduce candidate sets)
- Full-text search is easier to audit than purely semantic retrieval

What should be searchable:

- agent-generated artifacts (plans/summaries/decisions) from project/workspace `.codealta/` areas and the matching portable catalog scope under `$HOME/.codealta/`
- file chunks and extracted symbol text (where available)
- task comments and decision records
- conversation anchors (when indexed)

### 10.4 Semantic retrieval

Use embeddings to retrieve relevant knowledge records:

- small local embedding model via **LlamaSharp** (download + cache + integrity check)
- vector storage in SQLite (BLOB) plus a query strategy:
  - start: brute-force cosine over a filtered candidate set (FTS5 prefilter)
  - later: add a SQLite vector extension (e.g. sqlite-vec or sqlite-vss) when packaging is proven

### 10.5 Keeping it fresh

We need explicit invalidation strategies:

- file watchers re-index on change
- git branch/checkout detection triggers targeted re-indexing
- background jobs with rate limits (avoid thrashing on large repos)

---

## 11. Skills integration

We should integrate the Agent Skills format as a first-class concept:

- configure one or more skill directories per workspace
- load only `(name, description, path)` metadata into agent context
- activation loads full `SKILL.md` + referenced resources on demand

A dedicated **Skiller agent** can:

- manage skills discovery/validation
- decide which skills to activate (reducing load on other agents)
- keep a “skills index” in SQLite for fast matching

---

## 12. Roadmap (suggested)

A pragmatic, value-first order:

1. **Workspace registry + switching** (no semantic search yet).
2. **Tasks DB + MCP task service** (make plans durable and queryable).
3. **Indexing v1**: file hashes + FTS5 + basic chunks (keyword retrieval).
4. **Embeddings + semantic search v1**: small model + retrieval with citations.
5. **Agent hierarchy** (knowledge/planner/builder) backed by tasks/search.
6. **Roslyn host + speculative edit loop** for “always compiles” guarantees (for C#).
7. Skills integration + Skiller agent (progressive disclosure).

---

## 13. Open questions

- One SQLite DB per workspace vs a single DB with workspace namespaces?
- How do we represent cross-workspace tasks safely (explicit user opt-in)?
- Which SQLite vector strategy is shippable on Windows/macOS/Linux without pain?
- What is the minimal viable set of MCP tools to unblock multi-agent work?
- How do we persist “user intent” and “coding standards” per workspace/project?
