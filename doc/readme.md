# CodeAlta User Guide

An agentic AI coding CLI assistant developed in .NET.

Additional guides:

- [Plugin abstractions](plugins.md)
- [Skills](skills.md)

## Infrastructure Status

Current infrastructure-first progress includes project catalog and checkout primitives:

- `CodeAlta.Catalog`: project descriptors, machine override profiles, catalog loading.
- Scope resolution (`global`, `project`) into concrete checkout and `.alta` roots.
- Checkout planning (`clone` vs `update`) without network side effects.
- `CodeAlta.Plugins`: foundational plugin runtime services for source package discovery, generated plugin-root build files, explicit enablement, structured build/load, contribution ownership, lifecycle helpers, and change monitoring.

## Project Catalog Layout

Global repository layout (implemented reader support):

- `projects/<projectSlug>.md`
- `machines/<machineId>.yaml`
- `checkouts/<projectName>/`

The catalog model uses UUID v7 strings for project `id` values, validates slugs
using `^[a-z0-9][a-z0-9\\-_.]{1,63}$`, and keeps a separate project `name` for checkout directories.

## Catalog and Orchestration

Current infrastructure includes:

- `CodeAlta.Catalog`: project descriptors, machine override profiles, catalog loading, checkout planning, filesystem skill discovery, and in-memory project file usage tracking.
- Scope resolution (`global`, `project`) into concrete checkout and `.alta` roots.
- `CodeAlta.Orchestration`: lightweight thread/session runtime primitives and file-backed default thread instruction composition.
- `CodeAlta.Plugins`: foundational plugin runtime services for source package discovery, generated plugin-root build files, explicit enablement, structured build/load, contribution ownership, lifecycle helpers, and change monitoring.
## Terminal Shell

The `CodeAlta` executable now runs as an interactive terminal shell instead of a playground script. Internally, the shell is organized around an explicit controller, UI dispatcher, focused views, and presenter seams rather than one monolithic host bucket.

Launch helpers:

- `dotnet run --project src/CodeAlta -- --test`
- `dotnet run --project src/CodeAlta -- --test --test-duration 15`

`CodeAlta` and the small CLI utilities under `src/` now use `XenoAtom.CommandLine` with `TerminalVisualCommandOutput`, so `--help` and parse errors render through the shared visual command help/error path.

Only one `alta` application instance can run on a machine at a time via `~/.alta/alta.lock`, the same lock-file location on every OS/platform. If another instance is already running, startup exits with an error that reports the PID of the existing alta process and explains that multiple instances would share thread/session state unsafely.

`--test` still starts the real terminal UI, but it schedules cancellation after the requested duration so a smoke test can verify startup and a short steady-state run without manual Ctrl+C. Smoke-test lifecycle markers and normal diagnostic activity are written to the rolling logs under `~/.alta/logs/`.

Current terminal shell capabilities:

- Chat (global and project thread) operations:
  - Chat screen powered by `PromptEditor` (input) and `DocumentFlow` + `MarkdownControl` (rendered conversation history).
  - Automatically probes and initializes enabled provider backends. Codex is pinned to the SDK-generated release tag and is downloaded on demand into `~/.alta/cache/bin/codex/<tag>/` when missing. Copilot CLI is also downloaded only on demand into `~/.alta/cache/bin/copilot/<version>/<platform>/` when the Copilot backend starts. The built-in Copilot backend is temporarily forced disabled until the upstream `GitHub.Copilot.SDK` process cleanup issue is fixed.
  - Codex backend sessions default to `danger-full-access` (no sandbox) in CodeAlta so prompts can inspect sibling projects outside the current working directory without first switching the session root.
  - Provider, model, and reasoning-effort selectors are shown under the prompt, while the footer now shows a compact provider summary button with enabled-provider and error counts.
  - In a thread tab, `F3` / `F4` jump to previous / next user or assistant messages, while `Ctrl+F3` jumps to the first message and `Ctrl+F4` returns to the bottom of the latest message. The same actions are available as `/msg_prev`, `/msg_next`, `/msg_first`, and `/msg_last`.
  - Press `F6` or use the `Full Prompt` button to edit the current draft in a large 80%-screen prompt window; `Esc` or `Ctrl+Enter` closes it and keeps the edited draft.
  - Type `@` in the prompt to open a resizable project file/folder picker dialog with its own search box. Accepted entries become markdown links such as `[Program.cs](src/CodeAlta/Program.cs)`, raw `@path` and `@"path with spaces"` references still resolve on submit, optional `:line` or `:start-end` suffixes map to attachment line ranges, and accepted references are sent as structured file/directory inputs.
  - Press `Ctrl+E` or run `/edit` to open the project file picker in editor mode. Selected files open in reusable tabs beside thread/session tabs with TextMate syntax highlighting, line/column status, dirty markers, `Ctrl+S` save, reload prompts for on-disk changes, and close confirmation for unsaved edits.
  - Press `F1`, type `/help`, or enter `?` in the prompt to open shell command discovery. Textual shell commands now include `/open`, `/edit`, `/abort`, `/compact`, `/close`, `/queue`, `/exit`, `/tab_left`, `/tab_right`, `/msg_prev`, `/msg_next`, `/msg_first`, `/msg_last`, `/go_to_sidebar`, `/go_to_prompt`, and `/model`; `F5` steers, `F7` delegates, and `ALT+LEFT` / `ALT+RIGHT` switch between open tabs.
  - Press `Ctrl+O`, run `/open`, or use the always-visible `+` action on the `Projects` sidebar row to open a project dialog with project-name and directory completion. Rooted paths such as `/`, `C:`, `D:`, and `~` open folders, visible projects match the sidebar display names by default, and an `Include hidden` toggle opts archived projects back into completion and opening while successful opens return focus to the prompt.
  - Press `Ctrl+G Ctrl+S` to focus the sidebar on the current selection, `Ctrl+G Ctrl+P` to focus the prompt, `Ctrl+G Ctrl+O` or `/model` to focus the provider/model selector, `Ctrl+G Ctrl+M` or use the footer provider summary to open the model providers dialog, `Ctrl+G Ctrl+U` or use the footer usage indicator to open the context/usage popup, and press `Ctrl+G Ctrl+T` or use the thread info icon in the footer to open the selected thread report.
  - Use the `Show Logs` button in the navigator footer to open an 80%-size in-app log viewer. It replays retained diagnostic output from startup, wraps by default, supports `Ctrl+F` search, and offers `Wrap` and `Clear Logs` controls without leaving the TUI.
  - Closing either popup restores focus to the thread prompt editor so the workflow stays keyboard-first.
  - Per-provider model and reasoning defaults are stored in `~/.alta/config.toml`, with project-local overrides read from `<project>/.alta/config.toml`.
  - On startup, CodeAlta validates an existing `~/.alta/config.toml` before creating provider runtimes or sessions. If the file cannot be loaded, an 80%-size modal recovery editor opens with TOML highlighting, a left-margin marker for the first syntax/config diagnostic, live parse feedback while typing, `Ctrl+S`/`Save and Continue` enabled only after the config is valid, and `Ctrl+Q`/`Exit` for leaving without changing the file. If the file is missing, startup continues to the model providers flow for first-time setup.
  - The model providers dialog supports add/delete, enable/disable, save/reload, validation, and per-provider connectivity tests. If no enabled providers are configured, CodeAlta opens this dialog on startup and prompts the user to configure one.
  - OpenAI-compatible, Anthropic, and Google GenAI providers can also be configured in `~/.alta/config.toml`; only providers with usable credentials or Vertex settings are registered at startup. Local raw-API providers enrich model metadata from the bundled `models_dev_db.json` snapshot, apply per-provider `models_dev_provider_id` mappings and optional `model_overrides`, and refresh the models.dev catalog in the background at runtime.
  - Thread-specific model and reasoning selections are preserved for reopened tabs through `~/.alta/ui-state.yaml`, so an existing thread keeps its model by default even after global or project defaults change.
  - The reasoning selector only shows concrete effort values. When a selected model supports `high`, CodeAlta prefers `high` by default.
  - Sending a prompt now follows an enqueue-first workflow for busy threads: `Ctrl+J`/`Ctrl+Enter` adds the prompt to a waiting list above the status line, where queued prompts can be edited, repeated, steered immediately when the backend supports live steering, deleted, or cleared with `F10`. Steer requests that have been sent locally but not yet echoed back by the backend also appear at the top of that strip as transient pending rows, and unsupported backends such as the current local raw-API OpenAI/Anthropic/Google runtimes automatically re-queue the prompt for the next turn.
  - A temporary `AlwaysQueue` checkbox in the prompt bar forces normal sends to enqueue for the selected thread even when it is idle, which is useful for exercising the waiting-list controls without dispatching work immediately.
  - Pressing `F5` with an empty draft now steers the first queued prompt immediately when the selected thread has queued work.
  - If a steer is requested while the thread has no active provider run, CodeAlta now falls back to a normal send instead of surfacing a provider steer error. If prompt dispatch still fails, the prompt is preserved in the UI instead of being dropped.
  - Closing a thread tab no longer stops an active run or drops an edited thread draft. Running threads stay live in the sidebar, edited prompts surface with a draft indicator, and unsent per-thread prompts are persisted under `~/.alta/saved_prompts/` so reopening the app restores them.
  - Idle local-runtime threads can now switch providers directly from the provider selector. CodeAlta rebinds the local session onto the newly selected raw-API runtime and preserves the replayable thread history; Codex and Copilot threads remain locked to their original provider because their hidden runtime instructions and compaction state cannot be reconstructed safely.
  - A compact button now sits beside the provider/model/reasoning selectors. Press `F11` or click it to trigger a manual session compaction when the selected thread has already started and is currently idle; if the reopened thread's provider session had already shut down, CodeAlta now resumes it again before compacting. Manual compaction keeps a dedicated status line and emits visible started/completed notices in the timeline even when the backend does not emit its own compaction events.
  - The footer usage indicator stays compact as `ctx --`, `ctx N tok`, or `ctx NN%`, while the usage popup is split into Summary, Usage breakdown, Limits and quotas, and Backend-specific details with explicit source/scope metadata.
  - CodeAlta now auto-approves provider permission requests and auto-resolves `ask_user` prompts by preferring continue/inspect-style choices (or a neutral fallback for freeform prompts).
  - Sequential Codex/Copilot tool activity is grouped into compact "Tool Calls" timeline cards so verbose command/tool logs stay out of the main document flow; each chip shows a live status icon, inferred tool/command label, compact context, and an expandable `LogControl` detail dialog with full output, wrapping toggle, and compact execution stats.
  - When a run finishes, CodeAlta emits a separate compact "Modified Files" recap card that aggregates all files changed during that run, shows per-file and total `+/-` line counts, and opens an inline diff viewer for each file when diff data is available from the backend.
  - The terminal shell writes rolling diagnostic logs under `~/.alta/logs/`, including chat prompt submission, selected provider/model/tool set, normalized agent events, and Copilot permission/user-input callback traffic.
- Project operations:
  - list discovered projects
  - resolve global/project scopes.

## Alta Live Tool

CodeAlta-managed/local-runtime sessions receive one in-process live tool named `alta`. It uses the same `XenoAtom.CommandLine` command registry as host/test command dispatch, so agents can discover commands progressively with help instead of relying on a large prompt schema:

```text
alta --help
alta session --help
alta session tail --help
alta tool capability list
```

Help commands return normal help text. Non-help commands return finite newline-delimited JSON records headed by an `alta.result` record; they do not keep a stream open for future activity. Use small limits when inspecting history so the result stays model-friendly:

```text
alta project list
alta project show CodeAlta
alta session list --project CodeAlta --state all --limit 20
alta session show <thread-id>
alta session status <thread-id>
alta session children <thread-id> --recursive
alta session model <thread-id>
alta session tail <thread-id> --last 10
alta session events <thread-id> --limit 20
```

Session control commands submit work and return submission/queue metadata instead of waiting for the target agent to finish. Agent-originated submissions can return `detached: true` after the runtime accepts a long-running turn. For parent/child delegated work, do not poll by default: CodeAlta forwards the child final reply or a child-run error back to the parent thread automatically. Parented delegated submissions include `notificationExpected: true`, `shouldPoll: false`, `followUpMode: "notification"`, and `recommendedAction: "stop"` so coordinators can stop immediately. Use `session status`, `tail`, or `events` only for diagnostics, explicit observation, or when no parent notification is expected. `send --queue-if-busy` and explicit `queue` persist durable queue items with caller attribution; the runtime drains at most one queued prompt for a thread when the active run becomes idle.

```text
alta session create --project CodeAlta --title "Investigate parser" --same-model-as <thread-id> --reasoning low
alta session send <thread-id> --message "Summarize the latest failing test."
alta session send <thread-id> --stdin --queue-if-busy
alta session queue <thread-id> --message "Run this after the current turn."
alta session steer <thread-id> --message "Focus on the smallest fix."
alta session abort <thread-id> --reason "Superseded by user request"
alta session compact <thread-id>
alta session join <thread-id>
```

Use `session send` for actionable delegated prompts that should make the target model run as its next normal turn. Use the peer-agent wrappers for attributed notes, handoffs, or coordination messages where the target should see a non-authoritative delegated-agent header rather than a user/developer/system instruction:

```text
alta session message <thread-id> --kind handoff --message "Here is the context I collected."
alta session request <thread-id> --reply-requested --stdin
```

Optional live-tool caps such as `maxOutputRecords`, `maxOutputBytes`, and `timeoutMs` should be omitted unless setting positive integers; explicit JSON `null` is treated like omission for compatibility.

Model and provider discovery use the same model-ref rules as session creation:

```text
alta provider list
alta provider model list --provider codex
alta model list
alta model resolve --model-ref codex:gpt-5-codex@high
```

Skills are available through the singular `skill` group; compatibility aliases exist for older `skills activate`/`skills_activate` guidance. Activation succeeds only when CodeAlta can inject local-runtime skill context; provider-managed native skill systems report an unsupported-capability diagnostic instead of pretending to activate.

```text
alta skill list --project CodeAlta
alta skill show codealta-plugin-runtime
alta skill activate codealta-plugin-runtime --session <thread-id>
```

Visibility and provenance are enforced by the host. The global coordinator can inspect local projects and sessions, while project-scoped sessions can inspect/control their own project and same-project descendants; denied cross-project reads/mutations return a policy-denied JSONL error. Sessions created or controlled by agents/plugins carry `createdBy`/`submittedBy` metadata so sidebars and timelines can reconstruct parent/child relationships and prompt ownership after restart.

Plugins can add fresh `alta` command roots that appear in `alta --help`, and trusted plugins can invoke built-in `alta` commands through `Services.Alta.InvokeAsync(...)`. Plugin-originated mutating commands include the runtime-owned plugin key in provenance.

## Provider Configuration

CodeAlta exposes providers as the single user-facing execution concept. Each provider entry owns:

- whether it is enabled
- how it is displayed
- which runtime/protocol `type` it uses
- its credentials and endpoint settings
- its default `model`
- its default `reasoning_effort`

Built-in providers use reserved keys:

- `providers.codex` → `type = "codex"`
- `providers.copilot` → `type = "copilot"`

Both reserved providers are present in the model providers dialog even when they are not explicitly configured in `config.toml`. They now default to disabled so a first-time user must explicitly opt into Codex or another provider before starting a thread. Copilot remains visible for existing preferences, but CodeAlta currently keeps it disabled regardless of `config.toml` until the upstream `GitHub.Copilot.SDK` process cleanup issue is fixed. Advanced Copilot config can set `cli_path` to use an explicit Copilot CLI executable or `npm_registry` to keep CodeAlta's on-demand npm download/extract flow while using a private or mirrored registry.

Additional local providers can target raw provider SDKs such as:

- `OpenAI Responses`
- `OpenAI Chat`
- `Codex (ChatGPT subscription)` experimental Responses access
- `GitHub Copilot Direct` experimental local-runtime HTTP access
- `Anthropic`
- `Google GenAI`
- `Vertex AI`

These providers are configured from `~/.alta/config.toml` under `providers.<provider-key>`. Each entry represents one provider registration and uses a single canonical `type` field such as `openai-chat`, `openai-responses`, `openai-codex-subscription`, `github-copilot-direct`, `anthropic`, `google-genai`, or `vertex-ai`. OpenAI-compatible and Anthropic-compatible endpoints may set `api_url`; Vertex uses `project` and `location` instead. Each provider can optionally map to a models.dev provider id and override individual model limits so context usage stays consistent even when the upstream SDK does not expose context-window metadata.

The preferred workflow is now the in-app model providers dialog (`Ctrl+G Ctrl+M`), which edits the same file. The dialog:

- uses a left/right split layout for provider selection and editing
- supports provider add/delete plus enable/disable toggles
- validates provider keys, endpoint URLs, required credentials, and conflicting entries before save
- lets users store API keys directly in config or point to an environment variable
- can save, reload from disk, and test a provider before applying changes
- preserves advanced TOML settings such as `profile`, `compaction`, `extra_body`, `model_overrides`, and `protocol_trace` even though those advanced settings are still edited manually today

When CodeAlta writes `config.toml` back, it now omits properties that match built-in defaults such as `enabled = true`, reserved-provider `type`/`display_name`, and default compaction values.

Default provider selection is configured separately:

```toml
[chat]
default_provider = "codex"
```

Example:

```toml
[chat]
default_provider = "openai_responses"

[providers.codex]
model = "gpt-5.4"
reasoning_effort = "high"

# Temporarily kept disabled even if enabled = true is present.
[providers.copilot]
model = "claude-opus-4.6"
reasoning_effort = "high"
# Optional: bypass CodeAlta's on-demand Copilot CLI installer.
# cli_path = "C:/tools/copilot.exe"
# Optional: keep on-demand install but use a private/mirrored npm registry.
# npm_registry = "https://registry.npmjs.org"

[providers.openai_chat]
display_name = "OpenAI"
type = "openai-chat"
api_key_env = "OPENAI_API_KEY"
model = "gpt-5"
reasoning_effort = "high"
models_dev_provider_id = "openai"
# Dev-only: writes raw HTTP request/response metadata and SDK stream updates to
# ~/.alta/sessions/traces/<session-id>.trace. Trace files contain prompts/outputs.
# protocol_trace = true

[providers.openai_chat.model_overrides.gpt-5]
context_window = 400000
output_token_limit = 128000

[providers.openai_responses]
display_name = "OpenAI Responses"
type = "openai-responses"
api_key_env = "OPENAI_API_KEY"
api_url = "https://api.openai.com/v1"
model = "gpt-5.4"
reasoning_effort = "high"

[providers.codex_subscription]
display_name = "Codex (ChatGPT subscription)"
type = "openai-codex-subscription"
model = "gpt-5.3-codex"
reasoning_effort = "high"
# Optional: set to "http" to disable the default WebSocket + HTTP fallback transport.
# response_transport = "http"
experimental = true

[providers.github_copilot_direct]
display_name = "GitHub Copilot Direct"
type = "github-copilot-direct"
experimental = true
# Default: start a GitHub OAuth device flow, cache the GitHub token and exchanged
# Copilot API token under CodeAlta state, and refresh the Copilot token before expiry.
auth_source = "github_device_flow"
model_discovery = "copilot_endpoint_with_static_fallback"
# Non-interactive alternatives:
# auth_source = "github_token_env"
# github_token_env = "GITHUB_COPILOT_GITHUB_TOKEN"
# auth_source = "copilot_token_env"
# copilot_token_env = "GITHUB_COPILOT_TOKEN"
# Optional: enable only after accepting that traces contain prompts/outputs.
# protocol_trace = true

[providers.minimax]
display_name = "MiniMax 2.7"
type = "anthropic"
api_key_env = "MINIMAX_API_KEY"
api_url = "https://api.minimax.io/anthropic"
single_model_id = "MiniMax-M2.7"
model = "MiniMax-M2.7"
reasoning_effort = "high"

[providers.anthropic]
display_name = "Anthropic"
type = "anthropic"
api_key_env = "ANTHROPIC_API_KEY"
models_dev_provider_id = "anthropic"
model = "claude-sonnet-4"
reasoning_effort = "high"

[providers.vertex]
display_name = "Vertex"
type = "vertex-ai"
project = "my-gcp-project"
location = "europe-west4"
models_dev_provider_id = "google"
model = "gemini-2.5-pro"
reasoning_effort = "high"
```

Model discovery still comes from the upstream provider API when supported. `model_overrides` enriches or corrects discovered model metadata, while `single_model_id` can pin a provider to one fixed model for single-model endpoints. When an OpenAI-compatible endpoint does not implement `/models`, CodeAlta can also fall back to the local `models.dev` catalog if the provider key or `models_dev_provider_id` maps to a known catalog provider such as `minimax`.

The `openai-codex-subscription` provider is experimental and intentionally distinct from public OpenAI platform access:

- it requires `experimental = true` and rejects `api_key`, `api_key_env`, and arbitrary `extra_body`;
- it uses ChatGPT/Codex OAuth credentials stored in CodeAlta-owned state and never treats ChatGPT tokens as OpenAI platform API keys;
- requests target `https://chatgpt.com/backend-api/codex` by default and use WebSocket Responses transport with HTTP/SSE fallback; set `response_transport = "http"` to force HTTP-only mode;
- `previous_response_id` continuation is used only for active in-memory CodeAlta sessions and is not resumed from sessions reloaded from disk;
- model discovery defaults to a bundled Codex subscription picker allow-list (`gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`, `gpt-5.3-codex`, `gpt-5.2`); set `model_discovery` to `codex_endpoint` or `codex_endpoint_with_static_fallback` to opt into authenticated endpoint discovery;
- `model` is optional and only sets the preferred default selection; it does not narrow the picker to a single entry;
- requests may count against the user's ChatGPT/Codex subscription limits, and CodeAlta does not rotate accounts, bypass limits, or automatically fall back to a different provider;
- `send_installation_id` defaults to `false`; when explicitly enabled, CodeAlta sends a stable CodeAlta-owned UUID in `client_metadata.x-codex-installation-id`.

Supported Codex subscription auth sources are `codealta_oauth`, `codex_auth_import`, and `codex_auth_file_readonly`. `codex_auth_import` is a one-time read-only copy from Codex's `auth.json` into CodeAlta state; `codex_auth_file_readonly` is intended for development/test scenarios and never writes Codex-owned files.

The `github-copilot-direct` provider is experimental and intentionally distinct from the reserved native `[providers.copilot]` backend. It requires a non-reserved provider key plus `experimental = true`, fetches Copilot `/models`, filters disabled/unsupported models, and dispatches turns per model to Copilot `/responses`, `/chat/completions`, or `/v1/messages`. Supported auth sources are `github_device_flow`, `github_token_env`, and `copilot_token_env`; device flow and GitHub-token auth exchange a GitHub OAuth token through `https://api.github.com/copilot_internal/v2/token` (or the configured Enterprise domain) and never log bearer tokens. `github_enterprise_url`, `model_discovery`, `enable_model_policies`, and `include_preview_models` provide Copilot-specific controls.

CodeAlta also applies known provider defaults through a small defaults catalog before config overrides are applied. For example, MiniMax Chat registrations automatically disable the `developer` message role and merge developer instructions into the system prompt instead. DeepSeek Chat registrations replay assistant reasoning through `reasoning_content` so thinking-mode tool calls can continue across tool-result turns. An explicit `profile` section in `config.toml` can still override those defaults, including `reasoning_input_field_name` for OpenAI-compatible providers that require a different assistant reasoning replay field.

The bundled snapshot lives in `src/CodeAlta.Agent/Data/models_dev_db.json`. To refresh it manually, run:

```sh
dotnet run --project src/CodeAlta.Agent.ModelsDev.Updater/CodeAlta.Agent.ModelsDev.Updater.csproj -c Release
```

Session state for these local runtimes is stored under `~/.alta/sessions/yyyy/mm/dd/<session-id>.jsonl`. The journal contains replayable agent events plus internal session summary/state snapshots, so provider or model switches are captured as events in the same durable file instead of rewriting multiple provider-bound files. For protocol debugging, `protocol_trace = true` on an OpenAI-compatible provider enables a per-session trace at `~/.alta/sessions/traces/<session-id>.trace`; this redacts credential headers but otherwise may contain prompts, tool arguments, model output, and streamed SDK updates, so keep it disabled outside targeted local investigations.

## Live Backend Smoke Tests

`src/CodeAlta.Tests` also contains opt-in live backend smoke tests for the local Codex and Copilot CLIs.

- Set `CODEALTA_RUN_LIVE_CODEX_TESTS=1` to run the Codex live prompt test.
- Set `CODEALTA_RUN_LIVE_COPILOT_TESTS=1` to run the Copilot live prompt test.
- Without those environment variables, the live tests are skipped as inconclusive during `dotnet test`.

## Session Diagnostics

`src/AgentMessageDiagnosticApp` provides a small CLI for dumping mapped backend session history as JSONL.

- `dotnet run --project src/AgentMessageDiagnosticApp/AgentMessageDiagnosticApp.csproj -- --codex <session-id>`
- `dotnet run --project src/AgentMessageDiagnosticApp/AgentMessageDiagnosticApp.csproj -- --copilot <session-id>`
- Add `--indented` to pretty-print each JSON payload instead of emitting compact JSONL.
- Run with `--help` to see the generated visual usage and option reference.

