# Filesystem Metadata Catalog Specification (Draft)

Status: **Proposal**  
Audience: implementers of `CodeAlta.Workspaces`, `CodeAlta.Persistence`, `CodeAlta.Orchestration`, MCP services, and future bootstrap/sync flows.

Related specs:
- `doc/specs/agent_configuration_spec.md`

## 1. Problem

CodeAlta currently leans too much on SQLite for durable metadata such as:

- workspaces
- projects
- agents
- skills
- artifacts metadata

That is the wrong ownership boundary.

SQLite is useful for:

- full-text search (`FTS5`)
- embeddings / vector search
- local machine caches
- ephemeral coordination state

But SQLite is a poor source of truth for human-authored metadata that should be:

- readable without tooling
- diffable and reviewable in git
- easy to copy between machines
- easy to share with a team
- resilient to schema churn while CodeAlta is still pre-release

The source of truth for durable metadata should be plain text files under `~/.codealta/`.

## 2. Core Decision

CodeAlta should adopt a **filesystem-first metadata catalog**.

### 2.1 Source of truth

Durable metadata lives as files on disk, not in SQLite:

- workspaces
- projects
- agents
- skills
- durable artifacts metadata
- append-only activity logs

### 2.2 SQLite role

SQLite should be limited to:

- search indexes over files and logs
- embeddings
- other rebuildable machine-local caches
- ephemeral local coordination state
- optionally current tasks, if we decide they are machine-local and not part of the portable catalog

### 2.3 Breaking-change policy

CodeAlta is private and pre-release.

- preserving the current DB-centric metadata model is **not** a goal
- existing assumptions in older specs can be replaced
- the cleanest long-term ownership model should win

## 3. Terminology

The existing term `~/.codealta/repo/` is misleading.

This storage root is not “a source repo” in the normal sense. It is a **git-backed catalog of CodeAlta metadata and durable text artifacts**.

Recommended model:

- use `~/.codealta/` itself as the portable catalog root
- reserve `~/.codealta/machine/` for machine-local state

Rationale:

- avoids an unnecessary extra directory level
- keeps the user-facing layout simple
- makes `~/.codealta/` directly usable as a git repository
- still separates portable data from local-only state

If needed, CodeAlta can temporarily recognize `~/.codealta/repo/` as a legacy location during migration, but the spec should standardize on the root-level catalog model.

## 4. Goals

- Store durable metadata in plain text files under `~/.codealta/`
- Make the catalog easy to version in git
- Keep entity folders portable and movable
- Support workspace nesting without hardcoding parent ids
- Allow all agents, including built-ins, to be defined through files
- Keep artifacts close to the entity that owns them
- Keep SQLite rebuildable from filesystem sources

## 5. Non-Goals

- keeping DB tables as the authoritative copy of workspaces/projects/agents/skills
- designing the full task model in this document
- solving every sync/conflict policy detail up front
- supporting backward compatibility with the current storage shape

## 6. Top-Level Layout

Canonical machine root:

- `~/.codealta/`

Proposed structure:

- `~/.codealta/`
  - `workspaces/`
  - `projects/`
  - `agents/`
  - `skills/`
  - `artifacts/` (global/orphan/shared artifacts only)
  - `readme.md`
  - `.gitignore`
  - `machine/`
    - `codealta.db`
    - `config.yaml`
    - `logs/`
    - `cache/`
    - `models/`
    - `extensions/`

Rules:

- the root `~/.codealta/` is portable, text-first, and intended to live in git
- `machine/` is machine-local, rebuildable, and never the source of truth for durable metadata
- CodeAlta should create a `.gitignore` in `~/.codealta/` that excludes `machine/`
- use top-level files inside `machine/` when there is only one instance of a thing
- use subfolders only for multi-file or rolling content

Rationale for this shape:

- `machine/` is a clearer name than `local/`; it describes why the data is excluded
- `codealta.db` does not need a `db/` folder if there is only one database
- `config.yaml` does not need a separate `machine/` subfolder under an already machine-local root
- `logs/` remains a folder because rolling log files naturally create multiple files
- `cache/`, `models/`, and `extensions/` remain folders because they are collections, not single files

Recommended initial `.gitignore`:

```gitignore
/machine/
```

## 7. Entity Storage Model

## 7.1 General rules

Most durable entities get their own folder.

Preferred convention:

- workspace/project folders use `readme.md`
- agent definitions use single files named `<agent-key>.agent.md`
- skill folders keep `SKILL.md`

Folder-owned entities can contain:

- one primary markdown file with YAML frontmatter
- optional child folders for artifacts, activity logs, notes, attachments, or sub-entities

Single-file entities can use an optional sidecar folder when they need owned artifacts or logs:

- definition file: `agents/<agent-slug>.agent.md`
- sidecar folder: `agents/<agent-slug>/`

Examples:

- `~/.codealta/workspaces/<workspace-slug>/readme.md`
- `~/.codealta/projects/<project-slug>/readme.md`
- `~/.codealta/agents/<agent-slug>.agent.md`
- `~/.codealta/skills/<skill-slug>/SKILL.md`

The markdown body is free text intended for humans.
The YAML frontmatter is stable structured metadata intended for CodeAlta.

Rationale:

- `readme.md` makes workspace/project folders easier to browse in default git hosting views
- workspace/project folders are primarily human-facing catalog entries
- `.agent.md` aligns better with Copilot and Claude conventions than a nested `agent.md` file
- agents and skills remain explicit because those files behave more like executable/config definitions than folder landing pages

## 7.2 Workspace folders

Example:

- `~/.codealta/workspaces/platform/readme.md`
- `~/.codealta/workspaces/platform/activity/2026-03.jsonl`
- `~/.codealta/workspaces/platform/artifacts/...`
- `~/.codealta/workspaces/platform/workspaces/observability/readme.md`

This allows nested workspaces by folder structure.

Important rule:

- do **not** store `parent_workspace_id` in frontmatter

Parentage should be inferred from directory structure:

- the nearest ancestor workspace folder is the parent workspace
- moving a workspace folder should not require editing parent ids

Optional frontmatter may still include:

- `workspace_kind`
- `tags`
- `default_agent_refs`
- `project_refs`

But not a hardcoded parent id.

## 7.3 Project folders

Projects should be first-class catalog entities.

Example:

- `~/.codealta/projects/tomlyn/readme.md`

Workspaces reference projects by UID, not by embedding the whole project object.

Rationale:

- projects can be reused by multiple catalog views in the future
- project metadata stays stable even if a workspace is reorganized
- workspaces remain lightweight and portable

The project folder can also contain:

- `activity/*.jsonl`
- `artifacts/`
- optional local notes

## 7.4 Agent files

Agents should be catalog entities, including built-in agents.

Canonical definition shape:

- one file per agent
- filename pattern: `<agent-key>.agent.md`

Examples:

- `~/.codealta/agents/<agent-slug>.agent.md`
- `~/.codealta/agents/builtin/<agent-slug>.agent.md`
- sidecar folder when needed: `~/.codealta/agents/<agent-slug>/`

This is a major architectural rule:

- built-in agents should not be special-case code-only definitions
- CodeAlta should be able to discover agents from files and instantiate them
- the app can seed builtin agent files on first run, but afterwards they are just catalog entries

This is the foundation for CodeAlta creating its own configurable “team”.

Identity rule:

- agents do **not** use GUIDs or UUIDs as their canonical identifier
- the canonical identifier is the `agent-key`, derived from the filename stem before `.agent.md`
- if frontmatter includes `name`, it must match the filename-derived `agent-key`

## 7.5 Skill folders

Skills should also be catalog entities.

Example:

- `~/.codealta/skills/repo-onboarding/SKILL.md`

This does not replace the existing `SKILL.md` execution format for agent skills.
Instead, it adds a catalog layer that can describe:

- what the skill is
- who owns it
- where the executable skill content lives
- whether it is builtin, imported, or linked from another repo

The catalog entry can point to:

- an internal folder
- a checked out skill repository
- a repo-local skill definition

## 8. File Format

## 8.1 Primary format

All durable metadata entities should use:

- Markdown file
- YAML frontmatter

Example workspace file:

```md
---
uid: "01963b36-0d6f-7e4b-a7e0-6b2e6d1f4c8a"
kind: "workspace"
slug: "platform"
display_name: "Platform"
version: 1
project_refs:
  - "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"
default_agent_refs:
  - "security-expert"
tags: ["dotnet", "shared-infra"]
---

# Platform

This workspace contains the shared platform services, libraries, and build infrastructure.
```

Example agent file:

```md
---
kind: "agent"
name: security-expert
description: Reviews code and plans with a focus on security issues and threat modeling.
tools: [read, grep, search]
model: gpt-5.3-codex
codealta:
  default_backend: codex
  scope: workspace
  is_builtin: false
  tags: [security, review]
---

# Security Expert

Use this agent for security reviews, threat modeling, and hardening plans.
```

## 8.2 Frontmatter rules

Recommended shared frontmatter fields:

- `uid`
- `kind`
- `slug`
- `display_name`
- `version`
- `tags`
- `created_at`
- `updated_at`

Entity-specific fields live alongside them.

Rules:

- `uid` is the stable cross-reference key for workspaces, projects, artifacts, and similar folder-owned entities
- `slug` is human-friendly and used for folder names
- the markdown body is descriptive, not required for strict machine parsing

Exception for agents:

- agents should not carry `uid`
- agents use `name` / filename-derived `agent-key` as identity
- any references to agents should use that stable textual key

## 9. References Between Entities

Cross-entity references should use stable identifiers.

Examples:

- workspace -> project: `project_refs`
- workspace -> agent: `default_agent_refs`
- agent -> skill: `skill_refs`
- artifact -> workspace/project/agent: `owner_ref`

Rules:

- references should not depend on physical paths
- physical folder nesting is used for containment, not identity
- moving folders should not invalidate stable-id references
- agent references are the exception: they use textual `agent-key`, not UUID

## 10. Artifacts

Artifacts should be stored **under the entity that owns them** whenever possible.

Preferred layout:

- workspace artifacts:
  - `~/.codealta/workspaces/<slug>/artifacts/...`
- project artifacts:
  - `~/.codealta/projects/<slug>/artifacts/...`
- agent artifacts:
  - `~/.codealta/agents/<agent-key>/artifacts/...`

Use the top-level shared folder only for artifacts that have no clear owner:

- `~/.codealta/artifacts/...`

Rationale:

- ownership is obvious
- folders remain self-contained
- copying a workspace or agent folder brings its artifacts along
- indexing can still flatten these locations into SQLite later

## 11. Activity Logs

Activity that should be readable and append-only should be stored as JSONL.

Examples:

- `~/.codealta/workspaces/platform/activity/2026-03.jsonl`
- `~/.codealta/projects/tomlyn/activity/2026-03.jsonl`
- `~/.codealta/agents/<agent-slug>/activity/2026-03.jsonl`
  - owned by `~/.codealta/agents/<agent-slug>.agent.md`

Why JSONL:

- easy to append
- easy to read manually
- easy to stream
- easy to reindex into SQLite later

Suggested record kinds:

- `workspace_note`
- `workspace_decision`
- `workspace_sync`
- `agent_run_summary`
- `artifact_created`
- `project_attached`
- `project_detached`

This document does not lock the final event schema yet, only the storage pattern.

## 12. What Stays in SQLite

SQLite should remain machine-local and rebuildable.

Recommended contents:

- `FTS5` index tables
- embeddings / vector index tables
- file hash / reindex bookkeeping
- transient caches
- local execution queues
- optionally current task state if tasks are explicitly treated as machine-local coordination state

SQLite should **not** be the source of truth for:

- workspace definitions
- project definitions
- agent definitions
- skill definitions
- durable artifact metadata

## 13. Built-in Agents as Files

Built-in agents should be represented the same way as custom agents.

Recommended rule:

- CodeAlta ships seed templates for builtin agent files
- on first run, those files are materialized into `~/.codealta/agents/builtin/*.agent.md`
- discovery treats them exactly like user-defined catalog agents

This gives CodeAlta a file-defined team model:

- built-ins are inspectable
- built-ins are overrideable
- users can add or remove agents without code changes
- future orchestration can assemble a team by reading catalog files

## 14. Discovery and Loading

CodeAlta should load the catalog by walking the filesystem, not by querying a metadata DB first.

Suggested discovery order:

1. `workspaces/**/readme.md`
2. `projects/**/readme.md`
3. `agents/**/*.agent.md`
4. `skills/**/SKILL.md`

Discovery output:

- immutable in-memory descriptors
- path + uid + parsed frontmatter + markdown body

SQLite can then index the parsed files, but never owns them.

## 15. Naming and Folder Policies

Recommended policies:

- folder names use slugs, not opaque ids
- frontmatter always contains the stable UID
- one primary metadata markdown file per entity folder
- `artifacts/` and `activity/` are reserved subfolder names
- nested workspaces are allowed through `workspaces/<child>/readme.md` under a workspace folder

## 16. Migration Strategy

Because breaking changes are acceptable, migration can be direct.

Suggested phases:

### Phase 1

- introduce the filesystem catalog loader
- define markdown + frontmatter schemas
- keep existing DB code for search only

### Phase 2

- move workspace/project/agent/skill definitions to catalog files
- stop writing those entities into SQLite

### Phase 3

- move durable artifact metadata to owning folders
- keep SQLite only as index/cache

### Phase 4

- materialize builtin agents as catalog files
- instantiate built-in teams from catalog definitions

## 17. Implementation Impact

Expected impact areas:

- `CodeAlta.Workspaces`
  - move from YAML-only descriptors toward markdown + frontmatter entity folders
  - add nested workspace discovery
- `CodeAlta.Persistence`
  - remove authoritative workspace/project/agent metadata tables from the design
  - keep indexing and ephemeral state
- `CodeAlta.Orchestration`
  - load agents from catalog files
  - stop assuming built-ins are code-only registrations
- MCP services
  - read and write catalog files instead of DB metadata rows

## 18. Open Questions

These are intentionally left open for the follow-up implementation plan:

1. Should `machine/` remain the final name, or do we prefer a hidden variant such as `.machine/`?
2. Should tasks live in SQLite only, or also have an exportable plain-text form?
3. Should skill catalog entries wrap existing `SKILL.md` folders, or should the catalog file itself be the skill entry point?
4. How much machine-local override data belongs in `machine/config.yaml` versus the portable catalog?
5. Do we want agent activity logs per agent, per workspace, or both by default?

## 19. Recommendation

Adopt the following model:

- durable metadata is filesystem-first
- `~/.codealta/` is the portable git-backed root
- `~/.codealta/machine/` is reserved for machine-local state and ignored by git
- entities are folders with markdown + YAML frontmatter
- artifacts live under the entity that owns them
- activity logs are JSONL
- SQLite is reduced to indexing and ephemeral local state
- built-in agents are materialized and loaded from the same file model as user agents

That model best matches CodeAlta’s goals:

- transparent
- portable
- git-friendly
- easy to share
- easy to inspect
- robust against schema churn
