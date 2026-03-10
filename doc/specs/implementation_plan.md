# Implementation Plan: CodeAlta MVP Core Experience

This document is the current implementation entry point for CodeAlta.

It replaces the earlier “infrastructure first” emphasis with an **MVP-first** plan centered on the core user experience.

Related specs:
- `doc/specs/readme.md`
- `doc/specs/codealta_adaptive_orchestration_architecture.md`
- `doc/specs/filesystem_metadata_catalog_spec.md`
- `doc/specs/agent_api_specs.md`
- `doc/specs/agent_configuration_spec.md`
- `doc/specs/agent_instruction_templates_spec.md`

Deferred/follow-up plans:
- adaptive behavior: `doc/specs/implementation_plan_adaptive_orchestration.md`
- MCP server: `doc/specs/implementation_plan_mcp_server.md`
- storage + indexing + search: `doc/specs/implementation_plan_storage_search.md`
- .NET support: `doc/specs/implementation_plan_dotnet.md`
- older workspace/bootstrap plan: `doc/specs/implementation_plan_workspaces_bootstrap.md`
- older agent-orchestration plan: `doc/specs/implementation_plan_agent_orchestration.md`

## 1. Goal

Deliver a minimal but solid coding-agent product that feels close to a raw Copilot/Codex CLI while adding CodeAlta’s own structure for:

- workspaces
- projects
- durable work threads
- multiple tabs / sessions
- agent configuration
- host-owned orchestration

The MVP should let a user:

1. create/select a workspace
2. configure/select projects inside that workspace
3. create a work thread
4. send prompts and continue work in that thread
5. manage multiple concurrent work threads
6. restore those threads after restart

## 2. What is explicitly not MVP

The following areas should be postponed or disabled for now:

- semantic search and indexing
- MCP-centered product flows
- adaptive/proactive orchestration
- background suggestions
- .NET-specific product features
- ambitious knowledge/memory automation beyond what is needed to restore work threads and scope

Those areas remain valid future directions, but they should not block or complicate the first product slice.

## 3. Product shape

The MVP product should be organized around:

- one **Global Thread**
- multiple **Workspace Threads**
- workspace/project-first navigation
- one workspace per non-global thread
- Copilot/Codex as pluggable execution backends
- a host orchestrator that owns thread routing, dispatch, and restoration

The MVP should not require semantic infrastructure in order to feel useful.

## 4. Implementation order

Proceed in these ordered slices.

## 4.1 MVP implementation checklist

Use the list below as the concrete step-by-step checklist for implementation.

The list is intentionally ordered, but it is not rigid in the wrong way:

- an implementer may split a step into smaller steps
- an implementer may add new steps when they simplify the MVP or remove ambiguity
- an implementer should not expand scope into deferred areas unless a step is truly required for the MVP

- [x] Keep `doc/specs/readme.md` as the clear start-here document for the spec set.
- [x] Keep `doc/specs/implementation_plan.md` aligned with the actual MVP scope and sequence.
- [x] Finalize the workspace metadata model, identity rules, and loading rules.
- [x] Finalize project descriptors and project-to-workspace attachment rules.
- [x] Implement catalog loading for workspaces, projects, and agents from `~/.codealta/` and project overlays.
- [x] Introduce `WorkThread` as the primary user-facing unit.
- [x] Support both `Global Thread` and `Workspace Thread`.
- [x] Enforce workspace selection before the first prompt in a workspace thread.
- [x] Enforce workspace lock after the first prompt in a workspace thread.
- [x] Allow project focus to evolve only within the owning workspace.
- [x] Persist durable thread metadata, summaries, and status.
- [x] Restore open threads/tabs and their scope after restart.
- [ ] Load file-based global and project-local agents.
- [ ] Compose coordinator and general-agent instructions consistently across Copilot and Codex.
- [ ] Keep Copilot and Codex sessions usable as thread execution backends.
- [ ] Give each work thread one coordinator session.
- [ ] Implement minimal host-owned orchestration for send, steer, dispatch, and explicit thread handoff.
- [ ] Project backend/coordinator/host activity into a curated thread timeline.
- [ ] Hide raw schedule payloads from the normal user timeline.
- [ ] Implement workspace/project-first sidebar navigation with threads and activity under scope.
- [ ] Implement thread creation UX for selecting a workspace and initial project scope.
- [ ] Implement multi-thread UX so several concurrent threads can be continued and steered without confusion.
- [ ] Make the global thread the cross-workspace overview and delegation surface.
- [ ] Add regression coverage for workspace/project/thread/orchestration basics before expanding scope.

### Phase 1 — Clarify and simplify the active specs

Work:

- keep `doc/specs/readme.md` as the entry point
- make the MVP-driving specs obvious
- mark search, MCP, .NET, and adaptive behavior as deferred
- remove conflicting “infrastructure first” guidance from active planning docs

Exit criteria:

- a new contributor can tell where to start
- the MVP story is clearer than the long-term vision

### Phase 2 — Workspace and project catalog

Work:

- finalize the durable file model for:
  - workspaces
  - projects
  - global agents
  - project-local agents/skills
- make `~/.codealta/` the clear global root
- keep machine-only state under `~/.codealta/machine/`
- define how a user creates/selects a workspace and configures projects

Exit criteria:

- CodeAlta can load workspaces and projects from the catalog
- the scope model is understandable from the filesystem alone

### Phase 3 — Work thread model

Work:

- implement `WorkThread` as the primary user-facing unit
- support:
  - global thread
  - workspace thread
- lock the workspace after the first prompt
- allow project focus to evolve within that workspace
- add durable thread metadata and summaries
- map tabs directly to work threads

Exit criteria:

- a user can create and reopen multiple threads cleanly
- the relationship between workspace, project, and thread is clear

### Phase 4 — Minimal orchestration

Work:

- keep orchestration host-owned
- give each thread one coordinator session
- support:
  - send
  - steer
  - thread restoration
  - explicit cross-thread/global handoff
- avoid bringing in adaptive/proactive orchestration yet

Exit criteria:

- one thread can run like a clean coding-agent conversation
- multiple threads can coexist without ambiguity

### Phase 5 — Minimal agent configuration

Work:

- finalize file-based agent definitions
- load global and project-local agents
- compose instructions consistently for Copilot and Codex
- keep custom-agent behavior simple and predictable

Exit criteria:

- CodeAlta can instantiate usable agents from files
- agent configuration is understandable without backend-specific knowledge

### Phase 6 — Core UI experience

Work:

- make the sidebar workspace/project-first
- let the user:
  - create/select workspaces
  - create/select threads
  - see recent thread activity
  - continue and steer work
- restore all open tabs/threads on restart

Exit criteria:

- the UI makes the scope model obvious
- the user can manage multiple threads without confusion

## 5. Technical constraints

Keep these near-term constraints:

- Async-first, cancellation-first, non-blocking; UI must remain responsive.
- Multi-thread aware (multiple agents and sessions running concurrently).
- Pluggable but not over-engineered (clear scopes, minimal dependencies, predictable layering).
- Language-agnostic overall.
- Markdown + YAML frontmatter remain the preferred durable metadata format.
- Copilot and Codex remain the execution backends for the MVP.

## 6. Project focus

For now, the spec and implementation focus should stay on:

- `CodeAlta`
- `CodeAlta.Agent`
- `CodeAlta.Agent.Codex`
- `CodeAlta.Agent.Copilot`
- `CodeAlta.CodexSdk`
- `CodeAlta.CodexSdk.Generator`
- `CodeAlta.Workspaces`
- `CodeAlta.Orchestration`
- `CodeAlta.Persistence` only for the minimum durable thread/workspace state needed by the MVP

Deferred from active product scope for now:

- `CodeAlta.Search`
- `CodeAlta.Mcp`
- `CodeAlta.DotNet`

These projects may remain in the repo, but they should not drive product design or implementation priority until the MVP core experience is solid.

## 7. MVP acceptance criteria

The MVP is successful when:

- a user can create/select a workspace
- a user can create/select/configure projects
- a user can start a new thread in a workspace
- a thread can target one, many, or all projects in that workspace
- a user can keep multiple work threads open at once
- each thread can send prompts and receive results through Copilot or Codex
- the workspace/thread model is understandable from the UI
- restart restores the open threads and their scopes

The MVP is not blocked on:

- semantic search
- MCP tools
- adaptive suggestions
- .NET-specific capabilities

## 8. Deferred follow-up work

After the MVP core experience is working, the next layers can be resumed in this order:

1. adaptive orchestration and durable suggestions
2. search/indexing/semantic retrieval
3. MCP integration as a product feature
4. language-specific intelligence such as .NET support

## 9. Cross-cutting implementation notes

### 9.1 Async model and threading

- Every service API should be `async` (or return `ValueTask`) even if underlying libraries are synchronous.
- Use cancellation tokens in all long-running operations.
- Keep orchestration, UI, and backend execution separated cleanly.

### 9.2 Logging

- Keep logging clear and host-owned.
- Logging should help explain:
  - thread selection
  - scope resolution
  - coordinator dispatch
  - restoration behavior

### 9.3 Durable user-visible state

Anything needed to restore the user’s work should be persisted as files:

- workspace/project metadata
- thread summaries
- decisions/rationale that matter to resumed work
- task and plan snapshots when they become part of the MVP restoration model

Machine-local state should not be the only copy of meaningful user-visible state.

### 9.4 UI implementation references

When implementing or refining the terminal UI, use the local XenoAtom.Terminal.UI materials as the primary reference:

- docs: `C:\code\XenoAtom\XenoAtom.Terminal.UI\site\docs`
- samples: `C:\code\XenoAtom\XenoAtom.Terminal.UI\samples`
- source: `C:\code\XenoAtom\XenoAtom.Terminal.UI\src`

The expectation is that an implementer should consult those local materials first instead of inferring behavior from compiled .NET assemblies.

## 10. Summary

The project should now optimize for:

- clarity
- scope ownership
- thread restoration
- predictable orchestration
- a minimal but strong coding-agent UX

The architectural extras remain valuable, but they should stay out of the critical path until the core experience is solid.
