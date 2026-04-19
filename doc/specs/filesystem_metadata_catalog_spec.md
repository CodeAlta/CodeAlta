# Filesystem Metadata Catalog Specification

Status: **Proposal**  
Audience: implementers of `CodeAlta.Catalog`, `CodeAlta.Persistence`, `CodeAlta.Orchestration`, and UI/project discovery flows.

Related specs:

- `doc/specs/codealta_adaptive_orchestration_architecture.md`
- `doc/specs/agent_configuration_spec.md`

## 1. Problem

CodeAlta should not depend on a database as the source of truth for durable user-visible metadata.

The durable source of truth should be plain text files under `~/.codealta/`.

The MVP should optimize for:

- low setup friction
- automatic project discovery
- durable project/thread restoration
- human-readable state

The older workspace-first shape created unnecessary complexity. The catalog should now be centered on:

- projects
- threads
- agents
- skills
- profile

## 2. Core decision

CodeAlta should adopt a **filesystem-first project catalog**.

### 2.3 Project boundary recommendation

The code project that owns this spec should be a catalog/discovery library, not a workspace-named library and not the SQLite persistence layer.

Recommended target:

- `CodeAlta.Catalog`

Why:

- this spec is about durable file-backed catalog semantics
- it includes project discovery, descriptor loading, overlay resolution, and host-owned linkage metadata
- those concerns are broader than “persistence” and no longer centered on “workspaces”

`CodeAlta.Persistence` should stay focused on machine-local storage mechanics:

- SQLite
- caches
- rebuildable indexes
- file-store mechanics where the concern is storage

`CodeAlta.Model` is not recommended as the primary destination for this logic:

- the name is too generic
- the main need here is behavior plus file conventions, not just passive records
- splitting into a pure model assembly would add indirection before there is a demonstrated dependency problem

### 2.1 Source of truth

Durable metadata lives as files on disk, not in SQLite:

- projects
- threads
- agents
- skills
- profile memory summaries
- durable artifacts metadata
- append-only activity logs

### 2.2 SQLite role

SQLite should be limited to:

- search indexes
- embeddings
- rebuildable caches
- ephemeral local coordination state

## 3. Top-level layout

Canonical machine root:

- `~/.codealta/`

Proposed structure:

- `~/.codealta/`
  - `projects/`
  - `threads/`
  - `agents/`
  - `skills/`
  - `profiles/`
  - `artifacts/`
  - `readme.md`
  - `.gitignore`
  - `machine/`
    - `codealta.db`
    - `config.yaml`
    - `logs/`
    - `cache/`

Rules:

- the root `~/.codealta/` is portable, text-first, and intended to live in git
- `machine/` is machine-local, rebuildable, and never the source of truth for durable metadata
- CodeAlta should create a `.gitignore` in `~/.codealta/` that excludes `machine/`

Recommended initial `.gitignore`:

```gitignore
/local/
```

## 4. Runtime root behavior

When CodeAlta starts, `~/.codealta/` is the global root from which it discovers:

- known projects
- known threads
- global agents
- global skills
- profile memory

Project repositories may also contain a local overlay under:

- `{projectPath}/.codealta/`

This project-local root should support at least:

- `{projectPath}/.codealta/agents/`
- `{projectPath}/.codealta/skills/`

## 5. Entity storage model

## 5.1 General rules

Most durable entities get their own folder.

Preferred convention:

- project descriptors use single markdown files named `<projectSlug>.md`
- thread folders use `readme.md`
- agent definitions use single files named `<agent-key>.agent.md`
- skill folders keep `SKILL.md`

## 5.2 Project folders

Projects are first-class catalog entities.

Example:

- `~/.alta/projects/tomlyn.md`
- `~/.codealta/projects/tomlyn/activity/2026-03.jsonl`
- `~/.codealta/projects/tomlyn/artifacts/...`
- `~/.codealta/projects/tomlyn/threads/<thread-id>/readme.md`

Important rule:

- projects should be discoverable and upsertable automatically from actual use
- the user should not need a manual “create project” flow to start using CodeAlta

## 5.3 Thread folders

Threads are first-class durable entities.

Recommended shape:

- project and global threads should be recovered primarily from backend session listings plus cwd
- CodeAlta-owned thread folders should exist only when CodeAlta has unique metadata to persist

Examples:

- project thread recovery source:
  - backend session with cwd matching the project `path`
- global thread recovery source:
  - backend session with cwd `~/.codealta/`
- possible host-owned internal-thread root when needed:
  - `~/.codealta/threads/internal/...`

Working-directory rule:

- global thread sessions should use `~/.codealta/` as their backend working directory / cwd
- project thread sessions should use the owning project's local `path`
- this allows global threads to be rediscovered from backend session history in the same way project threads can be rediscovered from project paths

Backend-immutability rule:

- a thread is created against one backend
- the backend of an existing thread should not change
- moving the same logical work to another backend should create a new CodeAlta thread that links back to the prior one if needed

Important ownership rule:

- Copilot and Codex already persist their own thread/session history under their own roots
- CodeAlta should not duplicate the full backend transcript or raw event stream by default
- CodeAlta should also avoid duplicating basic thread identity when it can be recovered from backend session listings

For project and global threads, CodeAlta can recover the following without persisting its own duplicate manifest:

- canonical thread id = backend session/thread id
- backend identity = inferred from the backend that emitted the session
- project/global scope = inferred from cwd
- lightweight display title = inferred from backend summary or first user prompt

The main reasons to keep CodeAlta-owned thread metadata are narrower:

- parent/child relationships for delegated internal threads
- open-tab or pinned-thread UI state
- host-authored orchestration annotations that the backend does not store

If CodeAlta cannot point to a concrete piece of metadata it uniquely owns, it should not create its own thread manifest for that thread.

Important reopening rule:

- when the user selects a previous project/global thread in the UI, CodeAlta should reopen the existing backend-owned conversation
- the source of truth for prior interactions remains the backend session/thread store
- CodeAlta should not duplicate the full interaction transcript in `~/.codealta/` just to support reopening

Internal child threads may be:

- ephemeral runtime objects
- or persisted under the owning thread if their outputs or progress become valuable

Important internal-thread constraint:

- backend-generated session/thread ids are not available until after session creation
- therefore CodeAlta cannot generally choose an internal-thread cwd that embeds the backend id before the session exists

If CodeAlta needs a dedicated cwd marker for internal delegated work, it should use a host-chosen internal path such as a folder under `~/.codealta/threads/internal/`, then record the resulting backend session id after creation if the relationship must be restored later

The MVP does not require durable first-class browsing of internal child threads in the UI, but it should preserve enough state that a user can inspect delegated sub-agent work when needed.

## 5.4 Agent files

Agents should be catalog entities, including built-in agents.

Canonical definition shape:

- one file per agent
- filename pattern: `<agent-key>.agent.md`

Examples:

- `~/.codealta/agents/<agent-slug>.agent.md`
- `{projectPath}/.codealta/agents/<agent-slug>.agent.md`

## 5.5 Skill folders

Skills should also be catalog entities.

Example:

- `~/.codealta/skills/repo-onboarding/SKILL.md`

Project-local skill overlays:

- `{projectPath}/.codealta/skills/<skill-slug>/SKILL.md`

## 5.6 User profile

CodeAlta should keep a first-class durable profile entry.

Example:

- `~/.codealta/profiles/self/readme.md`
- `~/.codealta/profiles/self/activity/2026-03.jsonl`

This profile is the durable place for:

- confirmed preferences
- recent focus areas
- accepted or rejected suggestions later

## 6. File format

## 6.1 Primary format

All durable metadata entities should use:

- Markdown file
- YAML frontmatter

### Example project file

```md
---
id: "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"
kind: "project"
slug: "tomlyn"
display_name: "Tomlyn"
path: "C:\\code\\Tomlyn"
default_branch: "main"
tags:
  - dotnet
  - parser
---

# Tomlyn

TOML parsing project discovered from a local checkout.
```

### Example project thread file

A project thread does not need its own CodeAlta file by default.

It can be recovered from:

- backend session/thread id
- backend source
- cwd matching the project `path`
- backend summary or first user prompt for a display title

### Example global thread file

A global thread does not need its own CodeAlta file by default.

It can be recovered from:

- backend session/thread id
- backend source
- cwd `~/.codealta/`
- backend summary or first user prompt for a display title

### Example internal thread manifest

If CodeAlta needs to persist delegated internal-thread linkage, the stored record should be minimal and host-owned:

```md
---
kind: "internal_thread_link"
backend_session_id: "019cc85b-01fb-76d1-a785-2ea9f177e184"
parent_backend_session_id: "019cc85b-01fb-76d1-a785-2ea9f177e111"
role: "reviewer"
cwd: "~/.codealta/threads/internal/review-pass-01"
created_at: "2026-03-10T09:00:00Z"
---

# Internal reviewer thread link
```

## 6.2 Frontmatter rules

Recommended shared fields:

- `id`
- `kind`
- `slug`
- `display_name`
- `tags`
- `created_at`
- `updated_at`

Entity-specific rules:

- projects use `id` as their stable identifier
- projects use `path` as the local project root
- project/global threads use the backend session/thread id as their stable identifier
- CodeAlta-owned internal linkage records should store `backend_session_id` only when CodeAlta needs to restore parent/child relationships
- agents do not use GUIDs; they use filename-derived `agent-key`

## 7. References between entities

Cross-entity references should use stable identifiers.

Examples:

- thread -> project: `project_ref`
- global thread -> projects: `project_refs`
- project -> agent defaults later: `default_agent_refs`
- artifact -> project/thread: owner reference fields

Rules:

- references should not depend on physical paths
- physical folder structure is containment, not identity
- agent references use textual `agent-key`, not UUID

## 8. Tags and future grouping

The MVP should not use workspaces as the required grouping boundary.

Instead, projects may carry optional metadata such as:

- `tags`
- `labels`
- inferred categories later

Examples:

- `dotnet`
- `infra`
- `xenoatom`
- `review-needed`

For the MVP:

- tags may be inferred or persisted
- full tag-management UX can wait

## 9. Activity logs

Activity that should be readable and append-only should be stored as JSONL.

Examples:

- `~/.codealta/projects/tomlyn/activity/2026-03.jsonl`
- `~/.codealta/projects/tomlyn/threads/<thread-id>/activity/2026-03.jsonl`
- `~/.codealta/threads/global/<thread-id>/activity/2026-03.jsonl`

Why JSONL:

- easy to append
- easy to read manually
- easy to reindex later

## 10. What stays in SQLite

SQLite should remain machine-local and rebuildable.

Recommended contents:

- FTS indexes
- embeddings
- file hash / reindex bookkeeping
- transient caches
- local execution queues

SQLite should not be the source of truth for:

- projects
- threads
- agents
- skills
- durable artifact metadata

## 11. Discovery and loading

CodeAlta should load the catalog by walking the filesystem, not by querying a metadata DB first.

Suggested discovery order:

1. `projects/*.md` (and legacy `projects/**/readme.md` during transition)
2. backend session listings from Copilot and Codex
3. `threads/internal/**/readme.md` only for host-owned internal linkage records if present
4. `agents/**/*.agent.md`
5. `skills/**/SKILL.md`
6. `profiles/**/readme.md`

Project upsert should also be possible from runtime signals:

- current working directory
- backend session history where `cwd`/repo root is known

Thread restoration should combine:

- backend session/thread listings
- backend session/thread listings from Copilot or Codex
- cwd-based matching for project/global thread recovery
- CodeAlta-owned internal linkage records only where parent/child recovery is needed

UI tab restoration may persist only the minimum host-owned state needed to reconstruct the visible UI, such as:

- which backend-owned thread ids were open in tabs
- tab ordering or selected-tab state if the UI needs it

That persisted UI state should point back to backend-owned threads rather than replace them.

## 12. Recommendation

Adopt the following model:

- durable metadata is filesystem-first
- `~/.codealta/` is the portable git-backed root
- `~/.codealta/local/` is reserved for machine-local state
- projects are first-class catalog entities
- project/global threads are backend-owned and should be recovered from backend history
- CodeAlta-owned thread records should be limited to genuinely host-owned linkage metadata
- workspaces are not part of the MVP ownership model
- tags replace the immediate need for rigid grouping
- SQLite is reduced to indexing and ephemeral local state

That model best matches CodeAlta’s MVP goals:

- transparent
- low-friction
- portable
- easy to inspect
- easy to adopt without setup ceremony

