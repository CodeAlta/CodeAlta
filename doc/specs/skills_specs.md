# Skills Specification

> Historical note: This document predates the 1.0 core cleanup. Built-in persistence, semantic search, MCP services, local-model hosting, .NET intelligence, and hardcoded agent roles described here are not active 1.0 core features; they are future/plugin-oriented design notes unless reintroduced by a focused plugin or service.

Status: **Proposal**  
Audience: implementers of `CodeAlta.Catalog`, `CodeAlta.Mcp`, `CodeAlta.Agent*`, `CodeAlta.Orchestration`, and TUI/session-management surfaces.

Related specs:

- `doc/specs/filesystem_metadata_catalog_spec.md`
- `doc/specs/template_system_spec.md`
- `doc/specs/agent_instruction_templates_spec.md`
- `doc/specs/agent_api_specs.md`
- `doc/specs/codealta_adaptive_orchestration_architecture.md`
- `doc/specs/blueprint_mcp_server_specs.md`

Path note:

- local filesystem paths referenced in this document are relative to the root of the CodeAlta repository unless explicitly stated otherwise

## 1. Goal

Define a practical, backend-neutral skills model for CodeAlta that:

- works well with the existing filesystem-first catalog
- keeps context usage low through progressive disclosure
- integrates cleanly with Codex, Copilot, and local runtimes
- surfaces skills clearly in the TUI
- remains easy to extend later when CodeAlta grows a real plugin architecture

This document intentionally prefers a small, solid v1 over a large marketplace/plugin design.

## 2. Core decisions

### 2.1 Skills are content packages, not executable plugins

A skill is a directory rooted by a required `SKILL.md` file.

A skill may also contain helper material such as:

- `scripts/`
- `references/`
- `assets/`
- templates or sample files

Loading a skill does **not** execute code. It only makes instructions and packaged resources available to the agent. Any later file/script/tool execution still goes through the normal tool and approval flows.

### 2.2 Skills use progressive disclosure

CodeAlta should **not** inject the full body of every discovered skill into every prompt.

Instead, CodeAlta should use two stages:

1. **Discovery stage**: advertise only a compact list of available skills (`name`, `description`, and `location`).
2. **Activation stage**: load the full skill content only when the user, orchestrator, or model chooses a specific skill.

This keeps prompts smaller, reduces selection noise, and scales better as catalogs grow.

### 2.3 CodeAlta local runtime owns discovery and activation

CodeAlta should own:

- discovery roots
- collision resolution
- validation
- provenance/trust metadata
- UI presentation
- normalized skill activation events
- skill content injection
- skill resource reads

The important architectural rule for CodeAlta-managed local/raw backend sessions is:

- skills are handled by the **CodeAlta host/local runtime/agent loop**
- skills are **not** delegated to backend-native skill systems as the primary implementation for those backends

Codex and Copilot are the current exception: those providers manage their own native skills, so CodeAlta must not inject CodeAlta-managed skill advertisements or activation tools into Codex/Copilot sessions. CodeAlta may still observe or map backend-native skill concepts for compatibility/telemetry, but those provider-managed skills are not CodeAlta catalog state.

### 2.4 Do not build a full plugin system yet

The first implementation should stay filesystem-first.

However, the discovery layer should expose a **narrow seam** so that a future plugin can contribute skill roots or materialize skills into a cache directory without changing the rest of the runtime.

## 3. Non-goals for v1

The first implementation should **not** require:

- remote skill registries or skill download URLs
- a marketplace
- signed packages
- arbitrary code hooks during skill activation
- a separate skill-specific sandbox model
- a generalized plugin host

These can come later after the catalog/runtime model is stable.

## 4. User-facing model

A user should be able to:

- browse available skills at global and project scope
- understand what each skill does before loading it
- explicitly activate a skill in the current thread
- see when the runtime activated a skill automatically
- inspect where a skill came from and whether it is valid
- open the `SKILL.md` or its folder in the editor

A model should be able to:

- see a compact list of available skills
- request activation of a relevant skill
- receive the full skill body plus base-directory guidance only after activation

## 5. On-disk format

## 5.1 Canonical shape

Canonical skill layout:

```text
<skill-root>/
  SKILL.md
  scripts/
  references/
  assets/
```

Only `SKILL.md` is required.

## 5.2 Discovery roots

CodeAlta should support both the common Agent Skills locations and CodeAlta-specific locations.

Documented common scopes:

- project: `{projectPath}/.agents/skills/`
- user: `~/.agents/skills/`

CodeAlta-specific scopes:

- project: `{projectPath}/.alta/skills/`
- user: `~/.alta/skills/`

Interpretation:

- `.agents/skills/` contains portable cross-client skills
- `.alta/skills/` contains CodeAlta-specific skills or CodeAlta-tailored variants
- both should participate in normal discovery, validation, and activation
- when a skill exists in both places, CodeAlta should use deterministic precedence rather than treating them as separate namespaces

Future-compatible roots:

- plugin-contributed roots materialized to local folders
- explicit temporary roots passed by tests or tooling

## 5.3 Discovery rules

Within each root:

- scan recursively for `**/SKILL.md`
- when a directory contains `SKILL.md`, treat that directory as the skill root
- do not recurse deeper under that skill root

This matches the mental model that one skill owns one directory subtree.

Implementation guidance:

- skill discovery should use `XenoAtom.Glob`
- discovery should respect `.gitignore` behavior through that library rather than implementing custom ignore parsing
- this is especially important for project roots so CodeAlta does not waste time scanning ignored build/vendor/generated trees

## 5.4 Frontmatter compatibility

CodeAlta should align `SKILL.md` with the Agent Skills specification.

`SKILL.md` should contain:

1. YAML frontmatter
2. markdown body content after the closing `---`

Minimal example:

```yaml
---
name: dotnet-test-fix
description: Diagnose and fix failing .NET tests with minimal churn.
---
```

Richer example:

```yaml
---
name: dotnet-test-fix
description: Diagnose and fix failing .NET tests with minimal churn.
license: Apache-2.0
compatibility: Requires dotnet on PATH and repository access.
metadata:
  author: codealta
  version: "1.0"
allowed-tools: Bash(dotnet:*) Read
---
```

## 5.5 Field rules

| Field | Type | Required | Purpose |
| --- | --- | --- | --- |
| `name` | string | Yes | Stable skill key. Must satisfy the Agent Skills naming rules and match the parent directory name. |
| `description` | string | Yes | Short routing summary used for discovery and model selection. |
| `license` | string | No | Optional license hint/reference. |
| `compatibility` | string | No | Optional environment requirements. |
| `metadata` | map<string,string> | No | Extra portable metadata defined by the Agent Skills specification. |
| `allowed-tools` | string | No | Optional experimental pre-approved tool pattern string from the Agent Skills specification. |

Notes:

- `name` and `description` are required for model-facing compatibility.
- `name` should follow the Agent Skills constraints: max 64 characters, lowercase Unicode alphanumeric characters plus hyphens, no leading/trailing hyphen, no consecutive hyphens, and directory-name match.
- `description` should stay concise, be non-empty, fit within 1024 characters, and describe both what the skill does and when to use it.
- `compatibility`, when present, should fit within 500 characters.
- `allowed-tools` should be treated as experimental metadata from the Agent Skills specification.
- CodeAlta should avoid introducing custom top-level `SKILL.md` fields in v1.
- If CodeAlta later needs local-only policy metadata, it should prefer `metadata`, host-owned catalog state, or a sidecar file rather than fragmenting the portable `SKILL.md` shape.

## 5.6 Body guidance

The body of `SKILL.md` should remain free-form markdown.

Recommended authoring guidance:

- start with what the skill is for
- describe when to use it and when not to use it
- state any required tools or approvals
- explain how relative paths should be interpreted
- keep large reference material in separate files under `references/`
- keep scripts/templates beside the skill rather than embedded inline
- keep `SKILL.md` lean enough for activation-time context; prefer the Agent Skills guidance of keeping it under 500 lines and under roughly 5000 tokens
- when referencing other files, use relative paths from the skill root
- keep file references shallow and avoid deeply nested reference chains

CodeAlta should not require a rigid body schema in v1.

## 6. Discovery, precedence, and diagnostics

## 6.1 Precedence

When multiple discovered skills have the same normalized `name`, use deterministic precedence:

1. project-local CodeAlta root: `{projectPath}/.alta/skills/`
2. project-local common root: `{projectPath}/.agents/skills/`
3. user CodeAlta root: `~/.alta/skills/`
4. user common root: `~/.agents/skills/`
5. future plugin-contributed roots with explicit priority metadata
6. built-in fallback skills, if CodeAlta later ships any

Within the same precedence tier, first discovered path wins, but CodeAlta should emit a diagnostic for the collision.

Shadowed skills should remain inspectable in management UI even when they are excluded from runtime catalogs.

## 6.2 Provenance

Every discovered skill should carry provenance metadata:

- `SourceKind`: `project-common`, `project-alta`, `user-common`, `user-alta`, `plugin`, `builtin`, `temporary`
- `SourceId`: opaque identifier for later plugin integration
- `BaseDirectory`
- `SkillFilePath`
- `Scope`
- `IsShadowed`
- `Diagnostics[]`

This provenance should be visible in the UI.

## 6.3 Validation

Validation should be spec-aware, deterministic, and visible.

### Errors (skill excluded from runtime catalogs)

- no usable `description`
- missing or invalid YAML frontmatter
- `SKILL.md` unreadable
- missing `name`
- `name` violates Agent Skills naming constraints
- `name` does not match the parent directory name
- unknown top-level frontmatter fields
- duplicate name shadowed by a higher-precedence skill

### Warnings (skill still loadable)

- compatibility text is unclear for users/models
- `SKILL.md` is unusually large relative to progressive-disclosure guidance

Diagnostics should be queryable through code and surfaced in the UI.

## 6.4 Proposed catalog types

`CodeAlta.Catalog.Skills` should evolve toward a richer model:

- `SkillDescriptor`
  - metadata only, used for discovery/UI/prompt catalogs
- `SkillDocument`
  - descriptor + raw markdown content + parsed frontmatter
- `SkillValidationDiagnostic`
- `SkillSourceKind`
- `SkillCatalogQuery`
  - scope, roots, inclusion flags, name filter

The current `SkillCatalog`, `SkillInfo`, and `SkillDocument` can be evolved rather than replaced wholesale.

## 6.5 Reference-spec alignment

CodeAlta should treat the Agent Skills project as the reference guidance source:

- GitHub project: `https://github.com/agentskills/agentskills`
- local checkout when available: `../agentskills`

Use it as implementation guidance for:

- canonical frontmatter fields
- naming and directory-match validation
- recommended `<available_skills>` prompt shape
- progressive disclosure guidance

Recommended policy:

- do **not** take a production dependency on `skills-ref`
- do use it as a reference when designing CodeAlta validation and prompt rendering
- add compatibility tests in CodeAlta that mirror representative reference-library cases
- ensure CodeAlta discovery tests cover both `.agents/skills/` and `.alta/skills/` roots at project and user scope

For portable/public skills, CodeAlta should validate against the published Agent Skills specification rather than inventing a divergent variant.

This gives CodeAlta good interoperability without outsourcing runtime ownership.

## 7. Runtime behavior

The runtime model in this document is **host-owned**:

- CodeAlta discovers skills locally
- CodeAlta advertises available skills to the session
- CodeAlta activates skills through its own local tool/runtime path
- CodeAlta injects the activated content back into the transcript

Backends should receive the result of this process, not own the process themselves.

## 7.1 Prompt-time advertisement

When skills are enabled for a session, CodeAlta should append a compact skill catalog section to the effective instructions.

Recommended behavior:

- include only valid, model-visible skills
- include only metadata, not full bodies
- sort by precedence then name
- include the `location` of the `SKILL.md` file as recommended by the Agent Skills guidance

Recommended format:

```xml
<available_skills>
  <skill>
    <name>dotnet-test-fix</name>
    <description>Diagnose and fix failing .NET tests with minimal churn.</description>
    <location>C:\repo\.alta\skills\dotnet-test-fix\SKILL.md</location>
  </skill>
</available_skills>
```

The instructions should also say, in plain text, that:

- skills contain specialized workflows
- the agent should activate a skill only when the task clearly matches
- relative paths inside a skill resolve against the skill root

## 7.2 Activation model

Skill activation should be explicit and auditable.

Supported activation paths:

1. **User-driven**: the user chooses a skill from UI or command input.
2. **Model-driven**: the model calls a CodeAlta-owned local tool to activate a skill.
3. **Host-driven**: the orchestrator explicitly injects a skill for a delegated/internal task.

Activation should produce:

- a normalized skill activity event
- a visible timeline entry when appropriate
- a structured content block inserted into the session/transcript

For local-runtime-backed sessions, activation should also produce a durable session-log event so loaded-skill state can be reconstructed after restart or resume.

## 7.3 Canonical activated-skill payload

The canonical behavior is to inject activated skill content as ordinary tool/message content returned by a CodeAlta-owned local skill activation path.

Recommended payload shape:

```xml
<skill_content name="dotnet-test-fix" source="project" path="C:\repo\.alta\skills\dotnet-test-fix\SKILL.md">
# Skill: dotnet-test-fix

...full skill markdown body...

Base directory: file:///C:/repo/.alta/skills/dotnet-test-fix/
Relative paths in this skill resolve against this directory.

<skill_files>
  <file>scripts/run-tests.ps1</file>
  <file>references/test-triage.md</file>
</skill_files>
</skill_content>
```

Rules:

- include the raw body of `SKILL.md`
- include the resolved base directory
- include a bounded file listing from the skill directory
- do not inline the contents of every referenced file
- do not execute scripts automatically

This format is useful for Codex, Copilot fallback behavior, and local runtimes.

## 7.3.1 Durable local-runtime skill events

For CodeAlta local runtime sessions, skill activation should be persisted in the session event log as a first-class replay-significant event.

Recommended persisted information:

- skill `name`
- skill `location`
- source/provenance kind
- activation timestamp
- whether activation was user-driven, model-driven, or host-driven
- a stable activation/event id when useful for timeline correlation

Recommended behavior:

- resuming a prior session should rebuild the set of loaded skills from the durable event log
- the runtime should not depend only on transient in-memory state to know which skills were active
- if a previously loaded skill is still available on disk, the runtime may restore its active state without forcing the model to rediscover it from scratch
- if a previously loaded skill is no longer available, CodeAlta should preserve the historical event and surface that the skill could not be restored

## 7.4 Resource access

CodeAlta should support safe host-mediated access to files under a skill root.

Required rules:

- resource paths must be relative
- rooted paths are rejected
- path traversal outside the skill root is rejected
- file reads remain ordinary tool activity and are auditable

## 7.5 Compaction behavior

Skill usage should survive compaction without permanently bloating context.

Recommended rule:

- compaction should preserve the fact that a skill was activated, why it mattered, and which skill names remain relevant
- compaction should **not** blindly keep the entire full skill body if it can be recovered from the catalog
- compaction must preserve enough durable information that loaded skills can be restored on session resume/replay

Recommended compaction behavior for local-runtime sessions:

- retain replay-significant skill activation events in the durable log
- allow the compacted summary/state to include a small `loaded_skills` set for fast restore
- do not drop skill activation history merely because the full `SKILL.md` body was compacted away
- before compaction, keep activated skill payloads as ordinary replayed transcript/tool context rather than duplicating them into the composed prompt
- when replaying a compacted session, restore the active-skill set first, then rehydrate prompt-integrated skill content on demand if needed

If a skill is still relevant after compaction, CodeAlta may re-activate it from the catalog rather than preserving duplicated text forever.

## 8. Backend mapping

## 8.1 Shared abstraction

Do **not** make backend-native skill directories the primary abstraction.

Recommended shared model:

- skills are discovered and resolved by CodeAlta before or during a run
- the session sees a compact available-skills catalog in composed instructions
- activation happens through the CodeAlta-owned `alta skill activate <skill-name> --session <thread-id>` live-tool command or an equivalent UI action
- activated skill content is returned as ordinary transcript content/tool output

`AgentInputItem.Skill` may remain as a compatibility type, but it should not be the primary implementation path for CodeAlta-managed skills.

## 8.2 Codex

Recommended mapping:

- Codex manages its own native skills, so CodeAlta should not inject CodeAlta skill advertisements into Codex sessions
- keep `AgentInputItem.Skill` only as compatibility plumbing for explicit native Codex skill references
- do not treat Codex-native skills as CodeAlta catalog state

## 8.3 Copilot

Recommended mapping:

- Copilot manages its own native skills, so CodeAlta should not inject CodeAlta skill advertisements into Copilot sessions
- normalize observed native Copilot skill events only for compatibility/telemetry
- do not treat Copilot-native skills as CodeAlta catalog state

## 8.4 Local raw-API runtimes

Local runtimes and raw-API backend adapters should use the host-owned skill flow.

This is the reference behavior for CodeAlta-managed skills:

- advertise available skills in composed instructions
- expose skill activation through `alta skill activate <skill-name> --session <thread-id>` and equivalent UI actions
- inject the canonical activated-skill payload into the conversation history

Codex and Copilot are intentionally excluded from CodeAlta-managed skill registration because those providers own their native skill systems.

## 9. MCP surface

`CodeAlta.Mcp.Tools.SkillsTools` should become the main local-runtime service boundary for skills.

For CodeAlta, this MCP/tool surface is not an optional add-on. It is the mechanism by which the host-owned runtime exposes skills consistently across backends and UI flows.

### 9.1 Keep

- `codealta.skills.list`
- `codealta.skills.get`

### 9.2 Add

- `codealta.skills.get_resource`
- `codealta.skills.validate`
- `alta skill activate <skill-name> --session <thread-id>` host/runtime command

Recommended responsibilities:

- `list`: metadata only
- `get`: raw `SKILL.md` and metadata for inspection/editing
- `get_resource`: bounded safe file access under the skill root
- `validate`: diagnostics for one skill or a scope
- `activate`: inject the canonical activated-skill payload plus metadata used for timeline/UI state through the host-owned runtime path

`alta skill activate` should be the primary activation path described to models. `get` remains the lower-level inspection tool.

## 10. Orchestration integration

## 10.1 Instruction composition

`AgentInstructionTemplateProvider` should gain a skills-aware section builder.

Recommended order inside effective instructions:

1. CodeAlta base instructions
2. role template
3. thread scope/runtime context
4. available skills catalog
5. project instruction files and other overlays
6. run-specific additions

This keeps discovery lightweight while preserving the higher-priority host instructions.

## 10.2 Role profiles and skills

`agent_configuration_spec.md` already reserves `codealta.skills` on agents.

Recommended interpretation for v1:

- treat agent-associated skill refs as **hints**, not mandatory bulk preloads
- use them to rank/filter the available skill catalog for that session
- allow the host/orchestrator to pre-activate one only when the role or task clearly requires it

This prevents roles from exploding prompt size.

## 10.3 Internal delegated threads

Delegated threads should be able to start with a bounded skill set.

Examples:

- a reviewer thread prefers review-related skills
- a .NET builder thread prefers .NET/test/build skills

The host should pass:

- relevant skill roots
- optionally a narrowed advertised skill list
- optionally one explicit activated skill

## 11. UI and UX

## 11.1 Skills browser

Add a dedicated skills-management surface, comparable in spirit to the existing model-providers dialog.

Minimum UI features:

- scope picker: global / current project / combined
- source visibility: indicate whether a skill came from `.agents/skills/` or `.alta/skills/`
- searchable list of skills
- detail pane with description, tags, source, validation status, and path
- preview/open actions for `SKILL.md`
- activate-in-current-thread action
- refresh discovery action

This can ship as a dialog before there is a richer sidebar node.

## 11.2 Thread-level visibility

The current thread UX should surface skill state.

Recommended surfaces:

- timeline cards for skill activations
- thread info report section listing recently activated skills
- small footer or prompt-adjacent indicator when the thread has active/recent skills
- a clear per-session list of currently loaded skills for local-runtime sessions

This should stay informative rather than noisy.

For resumed sessions, the TUI should be able to show which skills were restored from session history, not only skills loaded during the current process lifetime.

## 11.3 Prompt UX

Recommended explicit activation affordances:

- a shell command such as `/skill`
- command palette entry like `Use Skill`
- a keyboard shortcut for opening the skills browser; `Ctrl+G Ctrl+S` should **not** be reused because it is already assigned to sidebar focus in CodeAlta, so the skills surface should use a different shortcut (for example `Ctrl+G Ctrl+K` if it remains available)
- optional autocomplete/picker from the prompt editor

The resulting action should trigger host-mediated activation through the local runtime, not paste the raw file contents directly into the user draft.

## 11.4 Authoring UX

Because skills are files, authoring should stay simple.

CodeAlta already has an in-app file editor with syntax highlighting for project files. Skills authoring should reuse that same editing surface rather than requiring users to leave CodeAlta for normal skill authoring tasks.

Recommended actions:

- create a new skill from a built-in template scaffold
- open `SKILL.md` directly in the CodeAlta editor with markdown syntax highlighting
- open referenced files under `scripts/`, `references/`, and `assets/` in the same editor flow when useful
- navigate from the skills browser/detail pane into editor tabs for the selected skill
- validate skill and show diagnostics

Recommended behavior:

- treat skills as first-class editable artifacts inside CodeAlta
- prefer editing `SKILL.md` and related files through the existing CodeEditor-based experience
- keep validation and preview close to the editor workflow so authors can iterate quickly
- make it easy to switch between the skill detail pane, the editor tab, and validation results

This aligns with the existing template-system direction.

## 12. Plugin-forward design

## 12.1 Narrow seam

Do not introduce a general plugin runtime for v1.

Instead, introduce one narrow discovery seam such as:

```csharp
public interface ISkillRootProvider
{
    ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(
        SkillDiscoveryContext context,
        CancellationToken cancellationToken = default);
}
```

Where `SkillRootRegistration` contains:

- local root path
- source kind/id
- precedence
- trust/provenance metadata

Built-in providers would cover:

- project-local `.alta/skills/`
- project-local `.agents/skills/`
- user `~/.alta/skills/`
- user `~/.agents/skills/`

A future plugin can then contribute skills by registering additional roots or by materializing packaged skills into a cache directory and returning that root.

## 12.2 Why this is enough

This keeps the rest of the system unchanged:

- the catalog still discovers filesystem skills
- the runtime still advertises metadata and activates a chosen skill
- the UI still shows provenance and diagnostics

So the future plugin story extends the source layer rather than replacing the skill model.

## 12.3 Provenance and trust

Plugin-forward design requires provenance from day one.

The UI and diagnostics should always be able to say:

- where a skill came from
- whether it came from project/user scope
- whether it came from `.agents/skills/`, `.alta/skills/`, or a plugin-provided root
- whether it shadowed another skill

## 13. Security and trust model

Rules for v1:

- skill activation is never equivalent to script execution
- helper scripts under a skill root are treated like any other local file/script
- existing command/tool approval flows remain authoritative
- safe relative-path enforcement is mandatory for skill resources
- project-local skills from untrusted repositories should not be advertised to the model until the project is trusted
- the UI should show provenance before activation when feasible

Later trust features such as signatures or publisher identity can build on this provenance model.

## 14. Proposed implementation slices

## Slice 1: solid catalog foundation

Implement first:

- richer `SkillCatalog` discovery/diagnostics/provenance
- recursive `SKILL.md` discovery by skill root
- `XenoAtom.Glob`-based discovery with `.gitignore` awareness
- deterministic precedence between project and global roots
- `codealta.skills.get_resource`
- `codealta.skills.validate`

This makes the feature inspectable and testable before prompt/runtime work.

## Slice 2: explicit user activation

Implement next:

- TUI skills browser or `Use Skill` command
- `alta skill activate <skill-name> --session <thread-id>`
- timeline skill activity rendering
- thread info/report integration

This gives users an immediate, reliable workflow even before automatic model selection is polished.

## Slice 3: automatic model-facing activation

Implement after explicit activation works:

- instruction-time available-skills catalog
- backend compatibility handling where unavoidable
- consistent local-runtime activation flow across all backends

This keeps the automatic behavior grounded in a working manual flow.

## 15. Testing recommendations

Add tests for:

- discovery from `{projectPath}/.agents/skills/`
- discovery from `{projectPath}/.alta/skills/`
- discovery from `~/.agents/skills/`
- discovery from `~/.alta/skills/`
- `.gitignore`-aware discovery behavior through `XenoAtom.Glob`
- recursive discovery and skill-root stopping behavior
- project-over-global precedence
- `.alta/skills/` precedence over `.agents/skills/` within the same scope
- duplicate-name diagnostics
- missing-description rejection
- safe resource path enforcement
- activated-skill payload shape
- instruction composition including `<available_skills>`
- normalized skill activity events for host-owned activation flows and compatibility edge cases
- durable local-runtime skill activation events
- session resume restoring the loaded-skill set from prior events
- compaction preserving enough skill state for correct restore and TUI reporting

## 16. Recommendation summary

Adopt the following model:

- skills are filesystem-first content packages rooted by `SKILL.md`
- CodeAlta discovers skills from both common Agent Skills roots (`.agents/skills/`) and CodeAlta-specific roots (`.alta/skills/`)
- CodeAlta local runtime owns discovery, validation, precedence, provenance, activation, and UI
- Agent Skills compatibility should guide the on-disk format and validation rules
- full skill bodies are loaded only on activation
- activated skills are injected as ordinary auditable content/tool output
- local-runtime session logs should persist replay-significant skill activation events so loaded skills can be restored and surfaced in the TUI
- Codex and Copilot should keep managing their own native skills; CodeAlta should not inject CodeAlta-managed skill tools or advertisements into those backend sessions
- UI should support browsing, activation, validation, and provenance inspection
- future plugins should extend skills by contributing roots, not by redefining the skill model
