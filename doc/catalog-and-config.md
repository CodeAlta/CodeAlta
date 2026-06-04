# Catalog, configuration, and state

CodeAlta keeps user-owned durable state under a global root and project-local `.alta` directories. The default global root is `~/.alta`, resolved from the current user's profile directory by `CodeAltaOwnedServices` and `CodeAltaHost` unless a host overrides it.

## Global root layout

`CatalogOptions` defines the current global layout:

| Path under `~/.alta` | Owner | Purpose |
| --- | --- | --- |
| `config.toml` | `CodeAltaConfigStore` | Global chat defaults, providers, and plugins. |
| `mcp.json` | MCP plugin | Global MCP server connection definitions. |
| `projects/` | `ProjectCatalog` | Markdown project descriptors keyed by project slug. |
| `checkouts/` | `ProjectCatalog` helpers | Default checkout root used by catalog planning APIs. |
| `machines/` | Catalog model | Machine-specific override profile root. |
| `agents/` | Catalog model | File-backed agent-definition root used by host-owned coordinator setup. |
| `cache/` | Process/runtime services | Machine-local cache root, including refreshed model metadata and plugin build cache. |
| `sessions/` | Agent session runtime and session catalog | Date-sharded session journals and optional protocol traces. |
| `saved_prompts/` | Frontend prompt draft service | Unsent per-session prompt drafts. |
| `ui-state.yaml` | Frontend view-state service | Open/selected tabs, session/model preferences, theme, and shell view state. |
| `plugins/` | Plugin runtime | User-scoped source plugin packages. |
| `skills/` | Skill catalog | User-scoped CodeAlta skill roots. |
| `sessions/internal/` | Work-session catalog | Internal session linkage descriptors still read by the catalog. |

The runtime creates directories as needed. Provider auth managers also write under `~/.alta/auth/`, for example subscription credentials and direct-provider token caches. Protocol traces, session journals, auth files, and provider caches can contain prompts, tool arguments, model output, file paths, command output, or credentials; treat them as private user data.

## Project-local state

Project-local CodeAlta state lives under `<project>/.alta/`:

| Path | Purpose |
| --- | --- |
| `<project>/.alta/config.toml` | Project-local config overrides. |
| `<project>/.alta/mcp.json` | Project-local MCP server connection definitions. |
| `<project>/.alta/plugins/<package-id>/plugin.cs` | Project-scoped trusted source plugin packages. |
| `<project>/.alta/skills/<skill-name>/SKILL.md` | Project-scoped skills. |

The skill catalog also reads `<project>/.agents/skills/` and `~/.agents/skills/` as common `SKILL.md` roots. Use `.alta` roots when the content depends on CodeAlta-specific behavior.

## Configuration model

The top-level TOML document has these active sections:

```toml
[chat]
default_provider = "my_provider"

[providers.my_provider]
type = "openai-responses"
enabled = true
model = "model-id"
api_key_env = "MY_PROVIDER_API_KEY"

[plugins.statistics]
enabled = true

[skills]
disabled = ["ilspy-decompile"]
```

`CodeAltaConfigDocument` maps these sections to:

- `chat`: chat-level defaults, currently the default provider key;
- `providers`: configured model-provider documents keyed by provider key;
- `skills`: skill enablement settings, currently normalized disabled skill names;
- `plugins`: plugin enablement keyed by built-in id or source package id, plus plugin-owned policy such as `[plugins.mcp]`.

Legacy `[acp]` and `[acp.*]` blocks are no longer active configuration. `CodeAltaConfigStore` ignores them while preserving their TOML text when saving normalized config so existing user data is not deleted.

Global config is loaded from `~/.alta/config.toml`. Project config is loaded from `<project>/.alta/config.toml` when a project scope is active. The provider-management UI edits the same global file and validates TOML before saving. During startup, `CodeAltaConfigStore` creates the bundled default template only when the global config file is missing; existing user config files are not reconciled with newly bundled defaults.

MCP server connection definitions are intentionally separate JSON files: global `~/.alta/mcp.json` and project `<project>/.alta/mcp.json`. CodeAlta loads one file per scope, global first and then project overlay; a project server key shadows a global server key. New files use the default `mcpServers` format, while supported existing `mcpServers`/`servers` flavors are detected and preserved. String values in stdio `env` entries and HTTP/SSE `headers` may reference process environment variables with `${NAME}` placeholders, keeping secrets out of JSON when desired. TOML under `[plugins.mcp]` is a policy overlay only: per-server enablement, per-tool `disabled_tools`/`allowed_tools`, prompt caps, timeouts, and direct-exposure controls. Runtime availability is finite diagnostic state rather than durable config: failed or timed-out MCP servers are reported as unavailable for the current request/test, and diagnostics/results are redacted before display. Dynamic MCP tool exposure is progressive: configured servers appear compactly in the prompt, and `alta mcp activate <id>*` enables tools from selected servers for the current session; see [MCP support](mcp.md).

### Provider enablement

The bundled template is `src/CodeAlta.Catalog/DefaultConfig/config.toml`; all built-in provider entries are explicitly disabled there. For user-authored provider entries, an omitted `enabled` value normalizes to `true`.

Provider registrations are skipped at startup when required credentials or provider-specific authentication settings are missing. Skipped providers remain in config and can be corrected through the UI or TOML editor.

### Project overrides

Project-local config can override effective chat/provider behavior for that project. The frontend also persists session-specific provider/model/reasoning selections in `ui-state.yaml`, so an existing session view can keep its selected runtime after global defaults change.

Skill disablement is additive rather than an override: a skill named in global `[skills].disabled` is disabled everywhere, and a skill named in project `[skills].disabled` is disabled only for that project. A project config cannot re-enable a globally disabled skill.

## Project catalog

`ProjectCatalog` stores project descriptors under `~/.alta/projects`. Current saves use flat `<slug>.md` files; the loader still reads older `<slug>/readme.md` descriptor paths so existing user state can be opened.

A project descriptor includes stable id, slug, display name, project path, archive/visibility state, and timestamps. At launch, the current directory is exposed as a selectable in-memory project when no persisted descriptor already exists for that path; it is not written to `~/.alta/projects` until a session is created for it. Opening a folder upserts a descriptor for that path, then the shell selects it in the sidebar. Filesystem roots are valid project paths: because they have no leaf folder name, the catalog stores a safe synthetic project `name` and uses the normalized root path (for example `D:\` or `/`) as the sidebar display name.

## Session and session-view storage

CodeAlta uses two related records for active work:

- **Session-view descriptors** are catalog/runtime metadata for global, project, and internal session views. They carry title, project reference, provider/model/reasoning preferences, parent/created-by attribution, and last-active timestamps. Some persisted readers and file names still use `SessionView`/`SessionId` for legacy compatibility.
- **Agent session journals** are CodeAlta-owned JSONL files under `~/.alta/sessions/yyyy/MM/dd/<session-id>.jsonl`. They contain replayable normalized `AgentEvent` records plus raw snapshot events for `local.sessionSummary`, `local.sessionState`, `codealta.sessionHeader`, and `codealta.sessionState`.

`SessionViewJournalStore` still reads and writes the legacy header/state event names in the same journal used by the agent runtime. This avoids maintaining separate provider-bound state files for the same session while preserving existing user data.

Optional protocol traces are written to `~/.alta/sessions/traces/<session-id>.trace` only when a provider has tracing enabled. Credential headers are redacted, but trace files can still contain sensitive prompts, outputs, tool arguments, and streamed protocol updates.

## Prompt drafts and view state

Unsent per-session prompts are stored under `~/.alta/saved_prompts/` so closing a tab or restarting the app does not discard edited drafts. The frontend stores view state in `~/.alta/ui-state.yaml`, including open/selected tabs, theme and navigator settings, and session-specific model preferences.

`ShellStateStore` is a UI-session projection of currently open shell state; it is not a replacement for the durable catalog, session journals, or runtime-owned session state.

## Plugin and skill state

Source plugins are discovered from user and project roots and are enabled by default unless disabled in config or safe mode is active. CodeAlta owns generated plugin-root build files and plugin build manifests under its roots; plugin package directories should contain only package-owned source/content files.

Skills are plain directories containing `SKILL.md` plus optional helper files. Discovery validates metadata, applies precedence/shadowing and config enablement, and reads resources without executing scripts. Disabled skills remain visible in the management UI, but are not advertised to models and cannot be activated through UI, runtime, or live-tool paths.
