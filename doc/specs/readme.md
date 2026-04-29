# CodeAlta Specifications

Status: **Working index**

This folder is the entry point for understanding what CodeAlta should build next.

The current direction is a **project-first MVP**:

- no mandatory workspace setup
- automatic project discovery and upsert
- user-facing threads scoped to either:
  - a single project
  - a global cross-project context
- internal host-owned child threads for delegated work
- Copilot and Codex used as execution backends behind a CodeAlta-owned orchestration layer

## 1. MVP goal

The MVP should let a user:

- start CodeAlta in any project directory without prior setup
- have that project discovered and added to the catalog automatically
- see previously known projects and previously used threads
- open a project thread and work on that project directly
- open one or more global threads that can coordinate work across projects
- keep multiple threads open at once
- reopen previous threads with their full backend conversation history
- restore those threads after restart

The MVP should **not** depend on:

- semantic search
- MCP-first product flows
- adaptive or proactive behavior
- .NET-specific product intelligence
- user-managed workspace structures

## 2. Start here

Read these first, in order:

1. `doc/specs/implementation_plan.md`
   - the current MVP delivery plan
2. `doc/specs/codealta_adaptive_orchestration_architecture.md`
   - the project-first system model
3. `doc/specs/filesystem_metadata_catalog_spec.md`
   - durable project, thread, agent, and catalog storage
4. `doc/specs/agent_api_specs.md`
   - backend/session abstraction for Copilot and Codex
5. `doc/specs/agent_acp_specs.md`
   - ACP generator, runtime, registry, and backend adapter design
6. `doc/specs/agent_instruction_templates_spec.md`
   - base instructions for coordinator, project, and internal agents
7. `doc/specs/system_prompt_specs.md`
   - system prompt builder, base/role resources, prompt templates, and prompt diagnostics
8. `doc/specs/agent_configuration_spec.md`
   - file format for agent definitions
9. `doc/specs/template_system_spec.md`
   - scaffold/enrichment model for catalog files

## 3. Implement first

The MVP implementation order is:

1. project discovery and durable project catalog
2. durable thread model
3. project and global thread orchestration
4. file-based agent loading and instruction composition
5. multi-thread UI and restart restoration

The main supporting code projects for that work should be understood as:

- `CodeAlta.Catalog` for file-backed project/thread/agent catalog loading and discovery
- `CodeAlta.Orchestration` for runtime routing and thread execution
- `CodeAlta.Persistence` for SQLite/caches/file-store mechanics only

If a proposal does not materially improve one of those five items, it is probably not MVP work.

## 4. Active specs

These documents drive the active MVP:

- `doc/specs/implementation_plan.md`
- `doc/specs/codealta_adaptive_orchestration_architecture.md`
- `doc/specs/filesystem_metadata_catalog_spec.md`
- `doc/specs/agent_api_specs.md`
- `doc/specs/agent_acp_specs.md`
- `doc/specs/agent_instruction_templates_spec.md`
- `doc/specs/system_prompt_specs.md`
- `doc/specs/agent_configuration_spec.md`
- `doc/specs/template_system_spec.md`

## 5. Deferred until after MVP

These documents remain useful, but they should not drive the first implementation pass:

- `doc/specs/implementation_plan_adaptive_orchestration.md`
- `doc/specs/implementation_plan_storage_search.md`
- `doc/specs/implementation_plan_mcp_server.md`
- `doc/specs/implementation_plan_dotnet.md`
- `doc/specs/implementation_plan_workspaces_bootstrap.md`
- `doc/specs/implementation_plan_agent_orchestration.md`
- `doc/specs/agent_event_abstraction_proposal.md`
- `doc/specs/agent_event_stream_unification.md`
- `doc/specs/blueprint_mcp_server_specs.md`

## 6. Historical and broad context

Useful as background, not as the implementation entry point:

- `doc/specs/blueprint_codealta_specs.md`
- `doc/specs/blueprint_agentic_coding_specs.md`

## 7. Practical reading path

If you are implementing the MVP:

- start with `implementation_plan.md`
- use `codealta_adaptive_orchestration_architecture.md` for the runtime model
- use `filesystem_metadata_catalog_spec.md` for durable state
- use `agent_api_specs.md` for backend boundaries
- use `agent_instruction_templates_spec.md` for session instructions
- use `system_prompt_specs.md` for prompt composition, base/role resources, prompt templates, and prompt diagnostics
- consult deferred specs only when a current task explicitly depends on them

