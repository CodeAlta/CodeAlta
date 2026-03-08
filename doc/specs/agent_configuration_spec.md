# Agent Configuration Specification (Draft)

Status: **Proposal**  
Audience: implementers of agent discovery/loading in `CodeAlta.Orchestration`, `CodeAlta.Workspaces`, and future catalog tooling.

References:
- GitHub Copilot custom agent configuration:
  - `https://raw.githubusercontent.com/github/docs/refs/heads/main/content/copilot/reference/custom-agents-configuration.md`
- Claude Code sub-agents:
  - `https://docs.claude.com/en/docs/claude-code/sub-agents`

## 1. Goal

Define a file format for CodeAlta agents that:

- is close to GitHub Copilot custom agents
- is still mappable to Claude Code sub-agents
- works for both user-defined and built-in agents
- avoids GUID/UUID-based agent identity

## 2. Canonical file shape

Agent definitions are single files.

Canonical filename pattern:

- `<agent-key>.agent.md`

Examples:

- `security-expert.agent.md`
- `planner.agent.md`
- `reviewer.agent.md`

Optional sidecar folder:

- `security-expert/`
  - `artifacts/`
  - `activity/`
  - `notes/`

The sidecar folder is optional and does not define identity.

## 3. Agent identity

CodeAlta should drop the notion of GUID/UUID agent ids for catalog agents.

Canonical identity:

- `agent-key`

Derivation:

- `agent-key` is the filename stem before `.agent.md`

Examples:

- `security-expert.agent.md` -> `security-expert`
- `planner.agent.md` -> `planner`

Rules:

- `agent-key` must be unique within a discovery root
- `agent-key` is the stable reference used by:
  - workspace defaults
  - task assignment metadata
  - agent activity logs
  - artifact ownership metadata
- catalog agents should not carry `uid` or `id` fields for identity

## 4. Compatibility strategy

CodeAlta should be **Copilot-first** for frontmatter shape.

Rationale:

- Copilot’s custom agent frontmatter is closer to the kind of shareable markdown config we want
- Copilot uses file-based agent definitions already
- Claude compatibility can be achieved by mapping from the CodeAlta/Copilot-style fields

This means:

- prefer Copilot-style field names when there is overlap
- allow a small set of CodeAlta extensions
- avoid designing a new unrelated schema if an existing field can be reused

## 5. Canonical frontmatter

Minimal recommended frontmatter:

```yaml
---
description: Reviews code and plans with a focus on security issues and threat modeling.
tools: [read, grep, search]
model: gpt-5.3-codex
---
```

Recommended richer form:

```yaml
---
name: security-expert
description: Reviews code and plans with a focus on security issues and threat modeling.
tools: [read, grep, search]
mcp-servers: [codealta]
model: gpt-5.3-codex
handoffs: [planner, reviewer]
disable-model-invocation: false
user-invocable: true
codealta:
  scope: workspace
  default_backend: codex
  builtin: false
  tags: [security, review]
---
```

## 6. Field rules

### 6.1 Copilot-compatible fields

These should be treated as first-class canonical fields:

| Field | Type | Required | Purpose |
| --- | --- | --- | --- |
| `name` | string | No | Human-friendly display name. If present, must match the filename-derived `agent-key`. |
| `description` | string | Yes | Short description used for delegation, discovery, and UI presentation. |
| `tools` | string or string list | No | Tool allow-list. If omitted, backend/platform defaults apply. |
| `mcp-servers` | object or list | No | Additional MCP servers or MCP server references, kept for Copilot compatibility. |
| `model` | string | No | Default model preference. It is a preference, not a runtime guarantee. |
| `handoffs` | string list | No | Optional downstream agent handoff hints when supported by the backend/runtime. |
| `target` | string | No | Environment targeting hint retained for compatibility. |
| `disable-model-invocation` | boolean | No | Copilot-compatible flag that disables automatic model-driven agent invocation. |
| `user-invocable` | boolean | No | Copilot-compatible flag that controls whether users can select the agent directly. |
| `metadata` | string map | No | Pass-through string key/value metadata for compatibility and annotations. |

Rules:

- `description` is required
- `name` is optional, but if present it must equal the filename-derived `agent-key`
- `tools` defaults to backend/platform defaults if omitted
- `model` is a default preference, not a runtime guarantee
- `disable-model-invocation` and `user-invocable` should be preserved as-is because they are part of the current Copilot shape
- `metadata` should be treated as a pass-through bag of string pairs for compatibility

Fields that exist in some Copilot environments but should not be required by CodeAlta:

- `target`
- `handoffs`
- `mcp-servers`
- `model`

### 6.2 CodeAlta extension block

CodeAlta-specific fields should live under:

- `codealta:`

Recommended fields:

| Field | Type | Required | Purpose |
| --- | --- | --- | --- |
| `scope` | enum | No | Preferred scope for the agent: `global`, `workspace`, or `project`. |
| `default_backend` | enum/string | No | Preferred backend, for example `codex` or `copilot`. |
| `builtin` | boolean | No | Marks whether the agent is shipped by CodeAlta rather than user-authored. |
| `tags` | string list | No | Free-form classification tags for filtering, grouping, or selection. |
| `skills` | string list | No | Skill keys associated with the agent. |
| `permission_mode` | string | No | Local execution/approval hint, mainly for Claude-style permission mapping. |
| `hooks` | object or list | No | Optional lifecycle hooks for agent setup/teardown or other runtime integration. |
| `notes_visibility` | string | No | UI/export hint controlling how notes or sidecar content should be surfaced. |

Why use a nested block:

- keeps top-level fields closer to Copilot
- avoids polluting the shared compatibility surface
- makes export/mapping easier

## 7. Claude compatibility

Claude Code sub-agents overlap with this model on the following concepts:

- `name`
- `description`
- `tools`
- `model`
- `permission mode`
- `hooks`
- `skills`

Likely mapping approach:

- filename-derived `agent-key` -> Claude sub-agent name if needed
- `description` -> Claude description
- `tools` -> Claude tool allow-list
- `model` -> Claude model preference
- `codealta.permission_mode` -> Claude permission mode when exporting
- `codealta.hooks` -> Claude hooks when exporting
- `codealta.skills` -> Claude skills when exporting
- `codealta.scope` / `codealta.default_backend` stay CodeAlta-only and are ignored when exporting to Claude

CodeAlta should not contort its canonical schema to exactly match Claude if doing so reduces Copilot compatibility.

## 8. Built-in agents

Built-in agents should use the exact same file model.

Examples:

- `~/.codealta/agents/builtin/planner.agent.md`
- `~/.codealta/agents/builtin/security-expert.agent.md`

Rules:

- built-ins are discovered the same way as user agents
- built-ins are overrideable by higher-precedence roots if we later define precedence rules
- built-ins are not identified by hidden internal GUIDs

## 9. Discovery rules

Canonical discovery pattern for the portable catalog:

- `agents/**/*.agent.md`

Repo-local discovery pattern:

- `.codealta/agents/**/*.agent.md`

Copilot compatibility discovery remains a separate import/read path:

- `.github/agents/*.md`

CodeAlta may later support importing Copilot agent files into `.agent.md` files, but the canonical CodeAlta catalog shape should be `.agent.md`.

## 10. References from other entities

Other catalog entities should reference agents by `agent-key`.

Examples:

- workspace frontmatter:
  - `default_agent_refs: [security-expert, planner]`
- task metadata:
  - `assigned_agent_key: security-expert`
- artifact metadata:
  - `owner_agent_key: security-expert`

No UUID-based agent references should appear in the catalog spec.

## 11. Validation rules

Recommended validation:

- filename must end with `.agent.md`
- filename stem must be a valid slug-like key
- `description` must be present and non-empty
- if `name` is present, it must match the filename-derived `agent-key`
- if `metadata` is present, it must be a mapping of string keys to string values
- unknown top-level fields should be warned on, not silently discarded
- unknown `codealta.*` fields can be tolerated for forward compatibility

## 12. Recommendation

Adopt the following:

- CodeAlta catalog agents are single files named `<agent-key>.agent.md`
- `agent-key` replaces GUID/UUID identity for catalog agents
- frontmatter is Copilot-shaped first, with CodeAlta extensions under `codealta:`
- built-in agents use the same file model
- sidecar folders are optional and only for artifacts/activity, not identity
