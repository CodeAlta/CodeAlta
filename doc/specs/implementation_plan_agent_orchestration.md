# Implementation Plan: Agent Orchestration, Roles, and Context (Draft)

This document describes how we will implement the “global agent + hierarchy of agents” model, using the existing backend adapters (`CodeAlta.Agent.*`) and the built-in MCP services (tasks, artifacts, search, etc.).

Related specs:
- `doc/specs/blueprint_agentic_coding_specs.md`
- `doc/specs/blueprint_codealta_specs.md` (agent hierarchy, memory strategy)
- `doc/specs/agent_api_specs.md` (backend abstraction)

## 1. Goals

- Run multiple agent roles (global / knowledge / planner / builder) concurrently.
- Make scoping explicit and easy (global, workspace, project).
- Persist “agent work products” (plans, summaries, extracted knowledge) to disk artifacts so the system can recover after context compaction.
- Coordinate agents via durable tasks/plans (SQLite + exported markdown snapshots).
- Keep orchestration backend-agnostic: Codex and Copilot sessions should be interchangeable for most flows.

## 2. Project (`CodeAlta.Orchestration`)

Suggested namespaces:
- `CodeAlta.Orchestration`
  - high-level orchestration primitives
- `CodeAlta.Orchestration.Roles`
  - role profile model + loaders
- `CodeAlta.Orchestration.Context`
  - context pack builder and providers
- `CodeAlta.Orchestration.Runtime`
  - long-running agent runs, scheduling, cancellation

Dependencies:
- `CodeAlta.Agent` (sessions, events, tools)
- `CodeAlta.Persistence` (tasks, artifacts)
- `CodeAlta.Search` (retrieval for context building)
- `CodeAlta.Workspaces` (scope resolution)
- `CodeAlta.Mcp` (tool surface, optional but recommended for routing)

## 3. Core runtime types

### 3.1 Identity model

Records:
- `AgentIdentity`
  - `AgentId` (UUID v7, generated via `Guid.CreateVersion7()`)
  - `RoleId` (string)
  - `Scope` (`AgentScope`)
  - `BackendId` (codex/copilot)
- `AgentScope`
  - `Kind` (Global | Workspace | Project)
  - `Id` (workspaceId/projectId or null)

### 3.2 Runs and events

We unify “doing work” as an `AgentRun`:
- `AgentRunId` already exists in `CodeAlta.Agent`.
- Add `OrchestratedRunId` if needed for higher-level workflows.

`AgentHub` responsibilities:
- owns active backend instances (created via `AgentBackendFactory`)
- owns active sessions per agent identity
- routes:
  - user input → session.SendAsync(...)
  - backend events → orchestration event stream
  - backend tool calls → tool router (MCP tools or internal services)

Output:
- orchestration emits `OrchestrationEvent` (channel-based) for UI later:
  - run started/completed/failed
  - task updates
  - indexing progress

## 4. Role profiles (easy to add new roles)

### 4.1 Storage locations

Role profiles can be discovered from:
- repo-local:
  - `.github/agents/*.md` (GitHub Copilot custom agents format)
  - `.codealta/roles/*.md` (CodeAlta extensions)
- global repo:
  - `~/.codealta/repo/roles/*.md` (shared roles across machines)

### 4.2 Parsing strategy

`RoleProfileStore`:
- scans known roots (workspace/projects/global)
- parses markdown + optional YAML frontmatter
- normalizes into:
  - `RoleProfile` record:
    - `RoleId`
    - `Name`
    - `Description`
    - `SystemPrompt` / `Instructions`
    - `ToolsPolicy` (allowed/denied tool groups)
    - `DefaultBackend` (optional)
    - `DefaultModel` (optional)
    - `DefaultReasoningEffort` (optional)

Compatibility note:
- Copilot’s format should be supported as-is; CodeAlta can add extra metadata via frontmatter.

### 4.3 Built-in roles

Ship built-in roles (as embedded resources and/or generated on first run):
- `global`
- `knowledge.global`, `knowledge.workspace`, `knowledge.project`
- `planner.global`, `planner.workspace`, `planner.project`
- `builder.global`, `builder.workspace`, `builder.project`
- `skiller` (optional early)

## 5. Context window management (context pack builder)

### 5.1 Concept

`ContextPackBuilder` creates a bounded context (token/size constrained) for each run:
- always includes: active task, relevant workspace/project scope, current user request
- optionally includes:
  - recent conversation summary (artifact)
  - retrieved knowledge records (search results)
  - file snippets (repo files)
  - recent task event history

### 5.2 Providers

Implement “context providers” as composable components:
- `TaskContextProvider`
- `SearchContextProvider`
- `ArtifactContextProvider`
- `FileSnippetContextProvider`
- `.NET providers` (from `CodeAlta.DotNet`) when available

Each provider:
- accepts `(scope, query, budget, cancellationToken)`
- returns:
  - `ContextItem` records containing text + source link URIs

### 5.3 Compaction-safe memory capture

When a run completes, we persist:
- `plan.output` artifact (planner)
- `knowledge.record` artifacts (knowledge agent)
- `builder.verification` artifact (builder)
- `run.summary` artifact (always)

The next run can load these artifacts (and/or search index) instead of relying on the backend’s thread memory.

## 6. Task-driven coordination

### 6.1 Task model

Tasks are the durable shared state between agents:
- created by planner
- executed by builder
- enriched by knowledge agent
- tracked by global agent

We persist:
- SQLite task rows (queryable, assignable)
- markdown exports (human readable snapshots) on significant transitions

### 6.2 Planner → builder loop (v1)

Minimal flow:
1) User asks global agent for goal.
2) Planner creates:
   - plan artifact
   - tasks in SQLite (hierarchy)
3) Builder picks next task and executes:
   - uses tools to read/write files, run commands (subject to approval policy)
4) Builder verifies (tests/build) and updates task status.

## 7. Tool routing (backend ↔ MCP ↔ services)

We want backends to call tools in a uniform way.

Plan:
- Define a single “tool registry” in orchestration:
  - `ToolCatalog` that exposes:
    - MCP tool surface (preferred)
    - plus local “native” tools where needed (filesystem, git, etc.)
- For each backend adapter:
  - map backend tool invocation into `ToolCatalog.InvokeAsync(toolName, args, ct)`

Implementation note:
- Codex adapter currently does not handle dynamic tool calls (it emits an error).
- When implementing dynamic tools:
  - register a set of tools that proxy to MCP `codealta.*`
  - return tool results as backend-specific payloads via existing mapping types.

## 8. Concurrency model

- One active run per agent session (enforced at orchestration layer).
- Multiple agents can run concurrently.
- Shared services (SQLite, embedder, Roslyn) must be concurrency-safe:
  - serialize SQLite writes
  - limit embedder concurrency
  - use background worker services for heavy jobs

## 9. Tests (minimum)

- Role profile parsing:
  - parse Copilot custom agent markdown
  - parse CodeAlta frontmatter extensions
- Context pack builder:
  - budget enforcement
  - stable source links
- Orchestration flow:
  - planner creates tasks and plan artifact
  - builder marks tasks completed (mock backend or fake session)
