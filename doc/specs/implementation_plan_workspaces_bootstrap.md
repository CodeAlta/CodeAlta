# Implementation Plan: Workspaces, Bootstrap, and Global Knowledge Repo (Draft)

This document details the implementation plan for multi-workspace support and the global git-backed knowledge repository under `~/.codealta/repo/`.

Related specs:
- `doc/specs/blueprint_codealta_specs.md` (multi-workspace + global repo)
- `doc/specs/blueprint_agentic_coding_specs.md` (workspace portability)

## 1. Goals

- Support multiple workspaces, each containing multiple projects (repos/folders).
- A project belongs to exactly one workspace.
- Switching workspaces is fast and does not lose context (because durable artifacts exist on disk).
- Persist global knowledge/config in a git repository so a new machine can “bootstrap” quickly.
- Allow per-machine overrides (different checkout roots on different machines).
- Provide “setup my dev experience” automation: clone global repo, then clone/update workspace repos using templated rules.

## 2. Project (`CodeAlta.Workspaces`)

Suggested namespaces:
- `CodeAlta.Workspaces`
  - descriptors and ids
  - scope selectors and resolution
- `CodeAlta.Workspaces.Bootstrap`
  - git operations
  - checkout rules
  - machine profiles

Dependencies:
- SharpYaml for YAML descriptors
- (Optional) Markdig if we ever store descriptors as markdown with frontmatter

## 3. On-disk configuration model

### 3.1 Global repo layout

Location (default):
- `~/.codealta/repo/` (git repo)

Proposed layout:
- `workspaces/`
  - `<workspaceKey>/workspace.yaml`
  - `<workspaceKey>/projects/<projectKey>.yaml`
  - `<workspaceKey>/knowledge/` (global workspace-level artifacts, optional)
- `machines/`
  - `<machineId>.yaml` (override checkout roots, auth hints, etc.)
- `templates/`
  - checkout templates and macros (optional)
- `README.md` (human guidance)

### 3.2 Workspace descriptor (`workspace.yaml`)

Minimal fields:
- `id` (UUID v7, generated via `Guid.CreateVersion7()`), `key`, `display_name`
- `default_checkout_root` (logical; machine override can remap)
- `projects` (or references to per-project yaml files)
- `tags`, `description` (optional)

### 3.3 Project descriptor (`projects/<projectKey>.yaml`)

Minimal fields:
- `id` (UUID v7, generated via `Guid.CreateVersion7()`), `key`, `display_name`
- `repo_url` (git URL)
- `default_branch`
- `checkout` rule:
  - `path_template` (e.g. `C:\\code\\{workspaceKey}\\{projectKey}`)
  - `depth`, `submodules`, etc. (optional)

### 3.4 Machine profile (`machines/<machineId>.yaml`)

Purpose: allow machine-specific overrides without forking the entire global repo model.

Examples:
- default base directories for workspaces (Windows vs Linux paths)
- “do not checkout this project on this machine”
- credential hints (never store secrets directly; store references)

## 4. IDs and scope selectors

### 4.1 IDs

Plan:
- `WorkspaceId` and `ProjectId` are UUID v7 values (generated via `Guid.CreateVersion7()`).
- `workspaceKey` / `projectKey` are stable human-friendly ids (slugs) used for:
  - UI scope selection (`:ws <workspaceKey>`, `:proj <projectKey>`)
  - global repo folder names (`workspaces/<workspaceKey>/...`)
- Validate keys as `^[a-z0-9][a-z0-9\\-_.]{1,63}$` (implementation detail).

### 4.2 Scope selectors (user-facing)

Even before we build the TUI, we should standardize the internal representation:
- `AgentScope` (global/workspace/project + id)
- `ScopeSelector` parsed from:
  - `:ws <workspaceKey>`
  - `:proj <projectKey>`
  - inline `@workspace(<key-or-id>)` / `@project(<key-or-id>)`

`WorkspaceResolver` maps selectors to:
- concrete checkout paths
- known projects in that scope
- `.codealta` roots to scan for artifacts/roles/skills

## 5. Bootstrap flow (“setup my dev experience”)

Bootstrap is a background-capable operation (progress + cancellation).

### 5.1 Global repo bootstrap

`GlobalRepoBootstrapper`:
- ensures `~/.codealta/repo/` exists
- if missing:
  - clone from configured remote URL (user/machine config)
  - or create a new repo and set remote later
- pull latest on startup (optional) or on explicit command

### 5.2 Workspace bootstrap

`WorkspaceBootstrapper`:
- reads workspace + projects descriptors
- resolves checkout paths using:
  1) machine profile overrides
  2) workspace defaults
  3) project path templates
- for each project:
  - clone if missing
  - fetch/pull if present
  - ensure `.codealta/` exists (create scaffolding if desired)

### 5.3 “Templated checkout rules”

Support simple macro expansion:
- `{workspaceKey}`, `{projectKey}`, `{repoName}`, `{machineId}`
- Optional advanced macros (when stable ids are needed): `{workspaceId}`, `{projectId}`

Implementation detail:
- `PathTemplateResolver.Resolve(template, context)`
- validate output path is safe (no traversal outside expected roots)

### 5.4 Per-machine defaults

Store per-machine config in `~/.codealta/state/config.yaml` (machine-local, not in git), e.g.:
- `machine_id`
- `global_repo_remote`
- default checkout roots for new workspaces

Machine id generation:
- first run generates a stable id and stores it.
- do not rely on hostnames (they change).

## 6. Git sync policy (global repo)

We want the global repo to be “always up to date” without being annoying.

Plan:
- `GlobalRepoSyncService` (background):
  - optionally pull on startup
  - periodically commit and push (debounced) when files change
- Provide explicit commands (MCP tools / future CLI commands):
  - `bootstrap.sync` (pull + commit + push)
  - `bootstrap.status` (last sync time, dirty flag)

Implementation choices:
- Prefer shelling out to `git` (avoids heavy dependencies).
- Ensure all git operations are cancellable and time-bounded.
- Never store credentials in repo; rely on user’s git credential manager.

## 7. Tests (minimum)

- Descriptor parsing round-trips for workspace/project yaml.
- Template resolver produces expected paths.
- Bootstrapper plans checkouts without performing network operations (unit tests should not clone).
