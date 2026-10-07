---
title: Model Providers
---

# Model Providers

A model provider is the user-facing execution target in CodeAlta: it owns credentials, endpoint settings, model discovery, the selected model, reasoning effort, and optional context/compaction metadata.

The preferred workflow is the provider editor (`Ctrl+G Ctrl+R`): the **Providers** page of Settings in the desktop app, or the **Model Providers** dialog in the TUI. Advanced users can edit the same TOML file directly at `~/.alta/config.toml`.

> [!IMPORTANT]
> Use **Test** before saving provider changes. It catches missing credentials, unsupported models, endpoint mistakes, and login-flow issues before the provider becomes the active execution target.

## Default configuration file

On first run, CodeAlta creates a default `~/.alta/config.toml` with common providers disabled. Later launches leave your existing global config untouched, even when a newer CodeAlta version bundles additional default entries, so you can remove, rename, or customize providers without startup adding them back. Enable only the providers you want to use.

| Provider key | Display name | Type | Default model or role | Credential field |
| --- | --- | --- | --- | --- |
| `alibaba` | Alibaba | `openai-chat` | `qwen3.7-max` | `CODEALTA_ALIBABA_API_KEY` |
| `anthropic` | Anthropic | `anthropic` | `claude-sonnet-4-6` | `CODEALTA_ANTHROPIC_API_KEY` |
| `azure-openai` | Azure OpenAI | `azure-openai` | Azure deployment name | `CODEALTA_AZURE_OPENAI_API_KEY` |
| `claude-code` | Claude Code | `claude-code` | chosen by Claude Code | none: the Claude Code CLI signs in by itself |
| `codex` | Codex | `codex` | `gpt-5.5`, high reasoning | ChatGPT/Codex OAuth state |
| `copilot` | Copilot | `copilot` | `claude-sonnet-4.6`, high reasoning | GitHub device flow by default |
| `deepseek` | DeepSeek | `openai-chat` | `deepseek-v4-pro` | `CODEALTA_DEEPSEEK_API_KEY` |
| `gemini` | Gemini | `google-genai` | provider-discovered | `CODEALTA_GEMINI_API_KEY` |
| `kimi-for-coding` | Kimi for Coding | `anthropic` compatible | `K2P6`, high reasoning | `CODEALTA_KIMI_API_KEY` |
| `minimax` | MiniMax | `anthropic` compatible | `MiniMax-M2.7`, low reasoning | `CODEALTA_MINIMAX_API_KEY` |
| `openai` | OpenAI | `openai-responses` | `gpt-5.5`, high reasoning | `CODEALTA_OPENAI_API_KEY` |
| `xai` | xAI Grok | `xai` | `grok-4.3`, high reasoning | xAI Grok OAuth state |
| `zai` | Z.ai | `openai-chat` | `glm-5.1` | `CODEALTA_ZAI_API_KEY` |

A separate `[chat]` section chooses the default enabled provider:

```toml
[chat]
default_provider = "openai"
```

Project-local overrides can live in `<project>/.alta/config.toml`. Project files currently override `[chat].default_provider` and each provider's selected `model` / `reasoning_effort`; global `~/.alta/config.toml` still defines the provider registrations, credentials, endpoints, and advanced provider metadata.

> [!NOTE]
> Project-local provider preferences are useful for repository-specific defaults, but avoid committing secrets. Prefer `api_key_env` in the global provider definition so credentials stay in your user environment.

## Provider dialog

Open it with `Ctrl+G Ctrl+R`, `/model_providers`, or the provider summary below the prompt. Use **Refresh** (or `/model_providers_refresh`) to reload the saved configuration from disk and retest provider availability, for example after starting a local LLM server that was offline earlier.

{{ alta_shot "alta-desktop-model-providers.webp" "alta-model-providers.png" "Provider list with the editable settings of the selected provider" "The provider editor edits the same TOML-backed configuration while keeping credentials, model selection, sign-in, and validation visible." }}

Both apps edit the same providers and share the same credentials, so a provider configured or signed in from one app is ready in the other. The provider editor can:

- add, delete, enable, and disable provider entries;
- validate provider keys, endpoint URLs, required credentials, and conflicting settings;
- store an API key directly or refer to an environment variable;
- list and choose a provider model on demand when the Model field is not using its default;
- run provider tests before applying changes; a successful test automatically enables that provider;
- start and monitor ChatGPT browser sign-in or Copilot browser/device login; successful authorization automatically enables that provider;
- refresh saved providers from disk and retest runtime availability without reopening the app;
- preserve advanced TOML settings such as `profile`, `compaction`, `extra_body`, `model_overrides`, and `protocol_trace`;
- open an Advanced TOML editor with live validation (the **Configuration file** page in the desktop app).

Each **Default** checkbox indicates that the field inherits its provider default rather than a custom override. Omitted settings stay marked as Default when you reopen or refresh the dialog; for example, a Codex entry containing only `type = "codex"` leaves its optional settings at their defaults. Uncheck Default to supply an override, or check it to remove the override when saving.

## Advanced TOML reference

Global provider entries live under `[providers.<provider-key>]`. Provider keys are normalized to lower case; `codex` and `copilot` also receive their default provider type when `type` is omitted. Supported canonical provider types are `codex`, `copilot`, `xai`, `openai-chat`, `openai-responses`, `azure-openai`, `anthropic`, `google-genai`, and `vertex-ai`.

Common provider fields are:

| Field | Purpose |
| --- | --- |
| `enabled` | Enables or disables the provider entry. Missing means enabled after normalization. |
| `display_name` | Label shown in provider selectors and dialogs. |
| `type` | Provider type. Common aliases such as `openai`, `responses`, `aoai`, `gemini`, `vertex`, and `github-copilot` are normalized to the canonical types above. |
| `model` | Default model id for this provider. |
| `reasoning_effort` | Default reasoning effort: `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, or `max`. Providers advertise the values supported per model. |
| `api_key` / `api_key_env` | Literal API key or environment variable name for API-key providers. Prefer `api_key_env`. |
| `api_url` | Absolute endpoint override. Azure OpenAI, Codex, and Copilot require HTTPS except localhost test transports. |
| `network_timeout_seconds` | Positive OpenAI/Azure SDK network timeout override for `openai-chat`, `openai-responses`, `azure-openai`, and `codex`. Leave unset for the OpenAI SDK default timeout of 100 seconds. |
| `protocol_trace` | Enables low-level protocol tracing for OpenAI, Codex, and Copilot transports. |
| `models_dev_provider_id` | Optional models.dev provider id used to enrich model metadata where supported. |
| `single_model_id` | Fixed model id for endpoints that do not expose a model list. |
| `models_include_regex` | Optional .NET regular expression applied to model ids after discovery/fallback. When unset, all discovered models remain visible; examples include `model1|model2` or `model\d+`. |
| `sort_models` | Set to `true` to sort discovered models alphabetically by display name (then id) in selectors. Defaults to `false`, preserving provider-returned order. |
| `profile` | Compatibility profile block; see below. |
| `compaction` | Local raw-API compaction settings; see [Context usage and compaction](#context-usage-and-compaction). |
| `model_overrides` | Per-model metadata overrides; see [Model metadata overrides](#model-metadata-overrides). |
| `extra_body` | Additional OpenAI-compatible request-body TOML fields; only used by `openai-chat` and `openai-responses`. |
| `request` | Request-level headers and OpenAI-compatible body defaults; see [Request customizations](#request-customizations). |
| `model_request` | Per-model request overrides for OpenAI-compatible providers; see [Model request overrides](#model-request-overrides). |

Provider-type-specific fields and restrictions:

| Type | Credential and endpoint fields | Additional fields handled for that type |
| --- | --- | --- |
| `openai-chat`, `openai-responses` | `api_key` or `api_key_env`; optional `api_url`, `organization_id`, `project_id` | `network_timeout_seconds`, `models_dev_provider_id`, `single_model_id`, `models_include_regex`, `extra_body`, `request`, `model_request`, `profile`, `compaction`, `model_overrides`, `protocol_trace` |
| `azure-openai` | `api_key` or `api_key_env`; required Azure OpenAI resource `api_url` | `network_timeout_seconds`, `models_dev_provider_id`, `single_model_id`, `models_include_regex`, `profile`, `compaction`, `model_overrides`, `protocol_trace` |
| `anthropic` | `api_key` or `api_key_env`; optional `api_url` | `models_dev_provider_id`, `single_model_id`, `models_include_regex`, `request.headers`, `request.remove_headers`, `profile`, `compaction`, `model_overrides` |
| `google-genai` | `api_key` or `api_key_env`; optional `api_url` | `models_dev_provider_id`, `single_model_id`, `models_include_regex`, `request.headers`, `request.remove_headers`, `profile`, `compaction`, `model_overrides` |
| `vertex-ai` | `project` and `location` are required when enabled; optional `api_url` | `models_dev_provider_id`, `single_model_id`, `models_include_regex`, `request.headers`, `request.remove_headers`, `profile`, `compaction`, `model_overrides` |
| `mistral` | `api_key` or `api_key_env`; optional `api_url` | `models_dev_provider_id`, `single_model_id`, `models_include_regex`, `request.headers`, `request.remove_headers`, `profile`, `compaction`, `model_overrides` |
| `codex` | ChatGPT/Codex OAuth state; no `api_key` or `api_key_env`; optional `api_url` | `network_timeout_seconds`, `models_include_regex`, `auth_source`, `account_id`, `max_concurrent_requests`, `text_verbosity`, `service_tier`, `include_encrypted_reasoning`, `model_discovery`, `response_transport`, `send_responses_beta_header`, `send_installation_id`, `installation_id_source`, `experimental`, `profile`, `compaction`, `protocol_trace` |
| `copilot` | GitHub device flow by default; optional `api_url` | `auth_source`, `github_enterprise_url`, `github_token_env`, `copilot_token_env`, `model_discovery`, `enable_model_policies`, `include_preview_models`, `experimental`, `single_model_id`, `models_include_regex`, `models_dev_provider_id`, `profile`, `compaction`, `model_overrides`, `protocol_trace` |
| `xai` | xAI Grok OAuth (browser PKCE or device flow); optional `api_url` | `auth_source`, `model_discovery`, `single_model_id`, `models_include_regex`, `models_dev_provider_id`, `request`, `model_request`, `profile`, `compaction`, `model_overrides`, `protocol_trace` |
| `claude-code` | none: `api_key`, `api_key_env` and `api_url` are rejected | `command`, `args`, `permission_mode`, `single_model_id`, `models_include_regex` |

For a recognized reasoning model through `openai-responses` against the official OpenAI endpoint, CodeAlta requests `summary: auto` and encrypted reasoning content even when effort is left to the service's model-specific default. The summary feeds the visible reasoning timeline, while the opaque encrypted item preserves stateless reasoning continuity between locally replayed calls. OpenAI-compatible custom endpoints retain their existing summary and encrypted-content request shape. Local replay preserves the relative order of assistant messages, opaque reasoning items, and tool calls.

### Claude Code

The `claude-code` provider runs your sessions through the [Claude Code](https://code.claude.com/docs/en/overview) CLI installed on your computer, with the account that CLI is signed in to. This is how a Claude subscription is used from CodeAlta: Anthropic does not let other applications sign in to a Claude account, so CodeAlta does not. It starts the `claude` program you installed and talks to it.

1. Install Claude Code and sign in: run `claude` in a terminal and use `/login` (an API key or a cloud provider configured for the CLI works too).
2. In the provider editor, enable **Claude Code** and use **Test**. The test asks the CLI for its models; it does not call a model.
3. Start a session with it. The models are the ones the CLI offers for your account; **Default** lets Claude Code choose.

CodeAlta never sees your Claude credentials, and there is no API key, endpoint or sign-in for this provider in CodeAlta. Usage counts against the account the CLI is signed in to, with the limits of its plan.

A session of this provider is a Claude Code session shown in CodeAlta:

- Claude Code keeps its own system prompt, tools (`Read`, `Edit`, `Bash`, ...), settings, permission rules, hooks, skills and `CLAUDE.md` files. The instructions CodeAlta composes for the session (the agent prompt, skills, project context such as `AGENTS.md`) are added to its prompt, with a short note that says how they apply there: which tool `alta` is, and that CodeAlta's sessions, questions, skills, plans and notes are the ones to use where Claude Code has a tool of its own. Claude Code keeps the prompt a conversation started with, so when those instructions change later (you switch the agent prompt, a skill is activated, tools are turned on) CodeAlta gives what changed with your next prompt.
- CodeAlta's own tools are available to it as MCP tools named `mcp__codealta__<name>`: the `alta` live tool, plugin tools, and the tools of the MCP servers you activated in CodeAlta.
- Its tool calls, command output and file diffs appear in the timeline, and the session is saved, resumed, queued and steered like any other. The CLI keeps the conversation in its own transcript (`~/.claude/projects`), which is what a resumed session continues from.
- When Claude Code asks a permission that your Claude Code settings do not already decide, CodeAlta answers it the way it does for its own tools: commands and file edits follow the session's review setting. A question of Claude (`AskUserQuestion`) opens the question form where CodeAlta takes live questions; in the desktop application Claude asks with `alta ask`, as the other providers do.
- A picture a tool returns (a screenshot of the window, for instance) is also saved by Claude Code in its own folder, `~/.claude/projects/<folder>/<session>/tool-results`.
- Claude Code compacts its context itself. The **Compact** command asks it to (`/compact`).
- A prompt that starts with `/` is a Claude Code slash command.

```toml
[providers.claude-code]
type = "claude-code"
model = "sonnet"                 # optional
reasoning_effort = "high"        # optional: low, medium, high, xhigh, max
command = "~/.local/bin/claude"  # optional: when `claude` is not on PATH
permission_mode = "default"      # optional: default, acceptEdits, plan, auto, dontAsk, bypassPermissions
args = ["--add-dir", "/specs"]   # optional: more arguments for the CLI
```

`command` is looked for on `PATH`, then in the folders the Claude Code installers use (`~/.local/bin`, Homebrew, npm). On Windows CodeAlta runs the native `claude.exe`; it does not run the `claude.cmd` shim of an npm installation. Add another `type = "claude-code"` entry with its own `command` or `args` to run a second configuration.

### ChatGPT sign-in and migration

The `codex` provider uses OpenAI's [Sign in with ChatGPT token-sharing flow](https://developers.openai.com/siwc/token-sharing-open-source). In the provider editor, choose **Continue with ChatGPT** (TUI) or **Sign in with the browser** (desktop app) and authorize CodeAlta to use your plan in the system browser. No developer registration, pre-issued client ID, client secret, or partner API key is needed. CodeAlta starts with `dynamic_agent_client`, then saves and reuses the issued client ID for that account/workspace, together with a stable host ID. The loopback callback uses `127.0.0.1` and an available port; browser sign-in must reach the machine running CodeAlta.

Add a separate `type = "codex"` provider with a recognizable `display_name` for another account or workspace, even if it has the same email address. Selecting that provider selects its separate registration and credentials; **Account Info** shows the validated account and issued registration ID. Reauthorization validates the selected identity before replacing its credentials. If plan access is declined, the validated sign-in is retained but inference remains disabled; choose **Continue with ChatGPT** to grant access, or configure a separate API-key provider.

If the first sign-in's code exchange fails with `invalid_grant`, choose **Continue with ChatGPT** again. CodeAlta retains the issued registration ID for the retry, even after restart, but does not save unvalidated identity or tokens or enable inference. Temporary signing-key service failures are checked before renewing a rotating refresh token, leaving it available for retry. OAuth registration/permission errors such as `invalid_client` are reported instead of silently displaying a static model catalog.

Existing users must sign in again: old Codex credentials, credential imports, and device-code login are no longer supported. Remove legacy `auth_source` values or set `auth_source = "codealta_oauth"`. The former built-in `https://chatgpt.com/backend-api/codex` URL is normalized to `https://api.openai.com/v1` when loading configuration, without rewriting your config file. Custom endpoint overrides are not automatically changed; new tokens are intended for the public OpenAI API, not ChatGPT's private backend.

**Sign out** attempts to revoke the renewable session and clears access, refresh, and ID tokens locally, retaining the account/client mapping for later sign-in. If remote revocation cannot be confirmed, CodeAlta reports it; you can disconnect the app in ChatGPT Settings. Credential files are written atomically under CodeAlta's global state root, protected with Windows DPAPI or owner-only permissions on Unix. Never copy them into source control or diagnostics. Manage app limits and plan usage at [ChatGPT Settings → Usage](https://chatgpt.com/settings/usage).

Codex accepts these values for constrained fields: `auth_source = "codealta_oauth"` only; `text_verbosity = "low"`, `"medium"`, or `"high"`; `model_discovery = "codex_endpoint_with_static_fallback"` (default), `"codex_endpoint"`, or `"static"`; `response_transport = "http"` (default) or `"websocket_with_http_fallback"`; and `installation_id_source = "codealta_state"`, `"codex_home_import"`, or `"codex_home_readonly"`. The installation-ID setting controls optional telemetry headers, not the required OAuth host ID. A legacy `account_id` override is not an account picker; the issued registration determines the account/workspace.

Codex subscription transport is isolated from ordinary OpenAI-compatible Responses providers. The default sends HTTP/SSE to `https://api.openai.com/v1/responses`; WebSocket with bounded safe retry and HTTP fallback is opt-in. The legacy programmatic `"sse"` alias remains runtime-tolerated but is not a valid config value. Both transports preserve configured endpoint query parameters and send `session-id`, `thread-id`, and `x-client-request-id`; HTTP sends `Accept: text/event-stream`. `send_responses_beta_header` defaults to `false` and is an opt-in compatibility switch for turn requests only—model discovery never sends that beta header.

ChatGPT plan requests enforce `store: false` and HTTP `stream: true`, replay the required local history instead of using HTTP `previous_response_id`, convert system messages to developer messages, group local function/custom tools in a namespace, and omit fields unsupported by the token-sharing preview (including temperature, output-token limits, and metadata). Hosted tools unsupported by this route are rejected. Model discovery calls `https://api.openai.com/v1/models`, uses the account's model slugs and display names, and preserves server order unless `sort_models` is enabled. Static fallback is not proof that a model is available to your account; authentication and permission failures are not hidden by fallback.

The Codex HTTP adapter uses the configured HTTP transport and combines SSE framing with SDK deserialization of standard `response.*` events. Codex streams stop at the first terminal event, reject premature EOF, retain indexed completed output items, and continue inference for explicit `end_turn: false`. Reasoning summary part/done and sequential-cutoff event forms are understood, although sequential-cutoff delivery is not requested by default. Models marked for Responses Lite (including the bundled GPT-5.6 Sol/Terra/Luna entries) receive Lite-specific developer input items and omit top-level tools/instructions; non-Lite request shapes are unchanged. With no session effort override, CodeAlta applies the model's advertised default and requests `summary: auto`, allowing the service to select its most detailed supported summarizer so provider summary events appear in the reasoning timeline; explicit `None` disables summary delivery. Terminal raw-reasoning extension data is not relabeled as a visible summary.

Subscription model discovery uses a five-second outer timeout and up to three attempts for network, timeout, and HTTP 5xx failures, while 4xx responses—including 401—are not retried. Successful ETags are retained in model metadata, and `codex_endpoint_with_static_fallback` falls back to the bundled catalog after an eligible discovery failure. Initial/event metadata is allowlisted: rate limits and credits feed usage, model reroutes and safety/verification/moderation produce transient updates, and persisted provider state contains only bounded request/model/ETag/reasoning/rate-limit summaries. Raw headers, moderation payloads, turn state, credentials, and unknown metadata are not persisted.

Copilot accepts `auth_source = "github_device_flow"`, `"github_token_env"`, or `"copilot_token_env"`; `model_discovery = "copilot_endpoint_with_static_fallback"`, `"copilot_endpoint"`, or `"static"`. `github_token_env` is required when using GitHub-token auth, and `copilot_token_env` is required when using Copilot-token auth.

xAI Grok accepts `auth_source = "xai_browser_oauth"` or `"xai_device_flow"`; `model_discovery = "xai_endpoint_with_static_fallback"`, `"xai_endpoint"`, or `"static"`. Both auth sources store CodeAlta-owned access and refresh tokens through the public Grok-CLI OAuth client and unlock SuperGrok / Grok Heavy plan access on accounts that have subscribed.

### Compatibility profile

Use `[providers.<provider-key>.profile]` only when a provider-compatible endpoint needs behavior different from CodeAlta's default transport profile. The supported profile fields are:

| Field | Meaning |
| --- | --- |
| `supports_developer_role` | Whether requests may use a developer-role message. |
| `supports_store` | Whether the transport may use provider store/session flags where supported. |
| `supports_reasoning_effort` | Whether `reasoning_effort` can be sent to the provider. |
| `streams_usage` | Whether streamed responses include usage data. |
| `supports_thought_signatures` | Whether provider thought-signature continuity is supported. |
| `requires_tool_result_name` | Whether tool-result messages must include a tool name. |
| `requires_assistant_after_tool_result` | Whether a synthetic assistant turn must be inserted after tool results. |
| `supports_tool_result_images` | Whether a tool result can carry an image. When `false`, an image a tool returns is attached to a user message placed after the tool results. By default OpenAI's own Responses endpoints take images in tool results and every other endpoint gets the user message. |
| `supports_cache_control` | Whether cache-control metadata is supported. |
| `supports_strict_tools` | Whether strict tool schemas are supported. |
| `thinking_format` | Provider-specific thinking/reasoning format name. |
| `max_tokens_field_name` | Request-body field used for maximum output tokens, such as `max_output_tokens` or `max_completion_tokens`. |
| `reasoning_field_names` | Response fields inspected for reasoning content. |
| `reasoning_input_field_name` | Assistant-message field used when replaying prior reasoning content. |
| `supports_parallel_tool_calls` | Whether to send the Chat Completions `parallel_tool_calls` control when tools are available. |

The bundled Alibaba/DashScope and DeepSeek profiles are examples of verified compatibility overrides:

```toml
[providers.alibaba.profile]
supports_developer_role = false
supports_store = false
supports_reasoning_effort = false
max_tokens_field_name = "max_tokens"

[providers.alibaba.request.extra_body]
enable_thinking = true
preserve_thinking = true

[providers.deepseek.profile]
supports_developer_role = false
supports_store = false
max_tokens_field_name = "max_tokens"
reasoning_input_field_name = "reasoning_content"

[providers.deepseek.request.extra_body.thinking]
type = "enabled"
```

### Single-model endpoints

Use `single_model_id` when a provider-compatible endpoint serves one model or cannot list models. The bundled MiniMax entry uses this pattern:

```toml
[providers.minimax]
single_model_id = "MiniMax-M2.7"
```

### Request customizations

Use `[providers.<provider-key>.request]` for static request headers and OpenAI-compatible request-body fields. Existing top-level `[providers.<provider-key>.extra_body]` remains supported and takes precedence over `[providers.<provider-key>.request.extra_body]` for backward compatibility.

Merge order is predictable:

1. generic transport behavior in code;
2. bundled provider defaults from CodeAlta's copied provider-defaults content file;
3. user `request.remove_headers` / `request.remove_extra_body` removals for inherited defaults;
4. user `request.headers` and `request.extra_body`;
5. existing top-level `extra_body` for OpenAI-compatible providers;
6. provider-owned authentication headers, which cannot be removed or replaced by request config.

Header names such as `Authorization`, `api-key`, and `x-api-key` are reserved for provider authentication. Protocol traces redact common secret-bearing header names (`Authorization`, `*-key`, `*token*`, `*secret*`, and related names).

```toml
[providers.openrouter]
type = "openai-responses"
api_key_env = "CODEALTA_OPENROUTER_API_KEY"
api_url = "https://openrouter.ai/api/v1"

[providers.openrouter.request.headers]
HTTP-Referer = "https://codealta.dev"
X-Title = "CodeAlta"

[providers.openrouter.request.extra_body]
include_usage = true

[providers.openrouter.request]
remove_headers = ["X-Unwanted-Default"]
remove_extra_body = ["unsupported_default"]
```

Alibaba/DashScope-style OpenAI-compatible endpoints can use request body defaults without replacing older `extra_body` configs:

```toml
[providers.alibaba]
type = "openai-chat"
api_key_env = "CODEALTA_ALIBABA_API_KEY"
api_url = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1"

[providers.alibaba.request.extra_body]
enable_search = false
```

Alibaba/DashScope, MiniMax, and DeepSeek compatibility defaults are bundled in CodeAlta's provider-defaults content file. You usually do not need to restate them in `config.toml`; explicit `profile`, `request`, and `extra_body` values still win when you do. The Alibaba defaults request streaming usage with `stream_options = { include_usage = true }` and enable Alibaba thinking plus thinking replay with `enable_thinking = true` and `preserve_thinking = true` for the Chat Completions transport.

DeepSeek defaults also avoid the non-standard `developer` role and `store` parameter, use `max_tokens`, send DeepSeek's documented `thinking = { type = "enabled" }` request control by default, and preserve `reasoning_content` on replayed assistant messages; `reasoning_effort` remains available where supported.

### Model request overrides

Use `[providers.<provider-key>.model_request.<model-id>]` to apply request mutations only when a matching model is selected. This is separate from `model_overrides`, which changes only metadata used by selectors and token budgeting. Matching uses the same exact/normalized/date-suffix model-id behavior as metadata overrides.

```toml
[providers.openai.model_request."gpt-5.5"]
remove_extra_body = ["reasoning_split"]

[providers.openai.model_request."gpt-5.5".extra_body]
reasoning = { effort = "high" }

[providers.openai.model_request."gpt-5.5".headers]
X-Model-Mode = "high-reasoning"
```

### Model metadata overrides

Use `[providers.<provider-key>.model_overrides.<model-id>]` to correct model metadata used by the model browser, selectors, usage denominator, and compaction budget when discovery or models.dev metadata is missing or incomplete.

| Field | Effect |
| --- | --- |
| `display_name` | Overrides the model label. |
| `description` | Overrides the model description. |
| `context_window` | Total context-window tokens. Also writes `contextWindow` / `contextWindowTokens` capabilities. |
| `input_token_limit` | Maximum input tokens. Also writes `inputTokenLimit` / `maxInputTokens` capabilities. |
| `output_token_limit` | Maximum output tokens. Also writes `outputTokenLimit`. |
| `max_tokens` | Maximum output token field used as `maxTokens`; defaults to `output_token_limit` when omitted. |
| `supports_reasoning` | Overrides reasoning capability. |
| `supports_tool_call` | Overrides tool-call capability. |
| `supports_attachments` | Overrides attachment/image capability. |
| `supports_structured_output` | Overrides structured-output capability. |

For a model id that contains dots, slashes, or other TOML punctuation, quote the key:

```toml
[providers.openai.model_overrides."your-model-id"]
context_window = 200000
input_token_limit = 180000
output_token_limit = 20000
supports_reasoning = true
supports_tool_call = true
```

## Popular providers

### OpenAI platform

Use the Responses API provider for current OpenAI platform models:

```toml
[providers.openai]
enabled = true
display_name = "OpenAI"
type = "openai-responses"
model = "gpt-5.5"
reasoning_effort = "high"
api_key_env = "CODEALTA_OPENAI_API_KEY"
api_url = "https://api.openai.com/v1"
```

Use `openai-chat` for OpenAI-compatible Chat Completions endpoints.

### Alibaba Cloud Model Studio

Alibaba Cloud Model Studio exposes an [OpenAI-compatible Chat API](https://modelstudio.console.alibabacloud.com/ap-southeast-1?tab=api#/api/?type=model&url=3016807) that works with CodeAlta's `openai-chat` provider type:

```toml
[providers.alibaba]
enabled = true
display_name = "Alibaba"
type = "openai-chat"
model = "qwen3.7-max"
api_key_env = "CODEALTA_ALIBABA_API_KEY"
api_url = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1"
```

CodeAlta's bundled Alibaba/DashScope defaults follow the OpenAI Chat API documentation: system messages are used instead of developer messages, `store` is not sent, maximum output tokens use `max_tokens`, OpenAI-style `reasoning_effort` is not sent, streaming requests ask for the final usage chunk with `stream_options.include_usage = true`, and Alibaba's non-standard `enable_thinking = true` and `preserve_thinking = true` controls are sent through `extra_body` by default. Override either value under `request.extra_body`, legacy `extra_body`, or a model-specific `model_request` entry, or list it in `request.remove_extra_body` when an endpoint rejects it. Other Alibaba-specific parameters such as `thinking_budget`, `top_k`, `enable_search`, and `search_options` remain opt-in via `request.extra_body` or `extra_body` because their support varies by model and mode. CodeAlta keeps the OpenAI-compatible Chat default of sending `parallel_tool_calls` when tools are available; set `profile.supports_parallel_tool_calls = false` only for endpoints or models that reject that parameter.

### Azure OpenAI

Use `azure-openai` for Azure OpenAI resource endpoints. The Azure OpenAI SDK uses deployment names where OpenAI APIs use model ids, so set `model` and/or `single_model_id` to your deployment name:

```toml
[providers.azure-openai]
enabled = true
display_name = "Azure OpenAI"
type = "azure-openai"
model = "my-gpt-4o-mini-deployment"
api_key_env = "CODEALTA_AZURE_OPENAI_API_KEY"
api_url = "https://your-resource.openai.azure.com"
network_timeout_seconds = 180
```

Use `network_timeout_seconds` when long Azure OpenAI or OpenAI-compatible streaming requests otherwise fail with the SDK's default network timeout.

Azure OpenAI does not expose model listing through the SDK used by CodeAlta. If `single_model_id` is not set, CodeAlta uses `model` as the single deployment shown in model selectors.

### Anthropic

```toml
[providers.anthropic]
enabled = true
display_name = "Anthropic"
type = "anthropic"
model = "claude-sonnet-4-6"
api_key_env = "CODEALTA_ANTHROPIC_API_KEY"
```

Anthropic-compatible providers can use the same type with a custom `api_url` and, when needed, `single_model_id`.

CodeAlta always sends an explicit Anthropic output-token limit, using the selected model's discovered or configured metadata instead of the SDK's small default. If a custom model does not publish this metadata, configure `output_token_limit` under its `model_overrides` entry; CodeAlta reports a configuration error rather than silently limiting the response to 1,024 tokens.

### Gemini / Google GenAI

Use `google-genai` for the Gemini API with an API key. Leave `model` unset
to choose from provider-discovered Gemini models, or set it explicitly:

```toml
[providers.gemini]
enabled = true
display_name = "Gemini"
type = "google-genai"
api_key_env = "CODEALTA_GEMINI_API_KEY"
model = "gemini-2.5-pro"
```

### Vertex AI

Vertex uses a Google Cloud project and location instead of an API-key endpoint:

```toml
[providers.vertex]
enabled = true
display_name = "Vertex"
type = "vertex-ai"
project = "my-gcp-project"
location = "europe-west4"
models_dev_provider_id = "google"
model = "gemini-2.5-pro"
reasoning_effort = "high"
```

### Mistral

Use `mistral` for the Mistral chat-completions API. CodeAlta streams turns over Mistral's `/v1/chat/completions` endpoint, serializes user/assistant/tool roles directly, maps streamed tool-call fragments back into CodeAlta tool calls, and uses the Mistral SDK for model listing.

```toml
[providers.mistral]
enabled = true
display_name = "Mistral"
type = "mistral"
api_key_env = "CODEALTA_MISTRAL_API_KEY"
model = "mistral-small-latest"
models_dev_provider_id = "mistral"
```

### Codex subscription endpoint

The `codex` provider type is ChatGPT/Codex subscription endpoint access. It is intentionally distinct from OpenAI platform API keys.

```toml
[providers.codex]
enabled = true
display_name = "Codex"
type = "codex"
model = "gpt-5.5"
reasoning_effort = "high"
model_discovery = "codex_endpoint_with_static_fallback"
```

Codex credentials are stored in CodeAlta-owned state through its login flow. It does not accept `api_key`, `api_key_env`, or arbitrary `extra_body`.

#### Optional fast routing

Fast routing is off by default. To opt in, add `service_tier` to your existing Codex provider table in `~/.alta/config.toml` (also available through the provider's advanced TOML editor):

```toml
[providers.codex]
type = "codex"
service_tier = "priority" # "fast" is an alias
```

CodeAlta sends `service_tier: "priority"` only when subscription model discovery advertises a `priority` service tier for the selected model. If it is unsupported or discovery metadata is missing—including static discovery/fallback—CodeAlta omits the tier and reports a warning that standard routing is being used. It does not probe eligibility with premium requests. Actual availability, latency, and charging depend on the model, account, and service; this is not a speed or entitlement guarantee.

Set `service_tier = "default"` or remove the setting to return to standard routing. Standard routing omits the request field; saves normalize `fast` to `priority` and may omit explicit `default`. Other values and top-level `service_tier` on non-Codex providers are rejected. Subscription `extra_body`, `request`, and `model_request` restrictions remain unchanged.

This setting applies **provider-wide**, including all sessions, child sessions, and compaction summaries using that provider, over both WebSocket and HTTP. Fast routing may increase subscription usage or cost. It does not change reasoning effort or implement Codex's separate `ultra` delegation policy.

Regular Codex requests allow multiple tool calls in one model response, regardless of the obsolete `supports_parallel_tool_calls` model field. Responses Lite still disables that request flag. This is model-side batching, not concurrent host tool execution: CodeAlta continues to await tool handlers sequentially. `max_concurrent_requests` limits subscription requests, not handler concurrency or service tier.

#### Reasoning behavior

CodeAlta follows Codex's ordered per-model reasoning-effort catalog for recognized inference values. GPT-5.6 Sol, Terra, and Luna support `max` as their highest inference effort in the static fallback catalog. CodeAlta does not expose Codex's `ultra` client tier because CodeAlta does not implement its separate proactive delegation policy. A reasoning-summary part whose body, after an optional bold heading, is exactly `<!-- -->` has no body while streaming, then is hidden from completed chat history with that heading. Literal comments in real prose or fenced examples and raw session data are retained.

### Copilot

```toml
[providers.copilot]
enabled = true
display_name = "Copilot"
type = "copilot"
model = "claude-sonnet-4.6"
reasoning_effort = "high"
auth_source = "github_device_flow"
model_discovery = "copilot_endpoint_with_static_fallback"
```

For non-interactive environments, the dialog and TOML support GitHub-token or Copilot-token environment-variable modes.

### xAI Grok

The `xai` provider type talks to the xAI API at `https://api.x.ai/v1`. Both auth flows persist CodeAlta-owned access and refresh tokens against the public Grok-CLI OAuth client and unlock SuperGrok / Grok Heavy plan access on accounts that have subscribed.

```toml
[providers.xai]
enabled = true
display_name = "xAI Grok"
type = "xai"
model = "grok-4.3"
reasoning_effort = "high"
auth_source = "xai_browser_oauth"
model_discovery = "xai_endpoint_with_static_fallback"
```

Two auth flows are supported:

- `xai_browser_oauth` — PKCE login against `auth.x.ai`; the dialog opens the consent screen and listens on the registered loopback redirect URI before persisting tokens under CodeAlta state.
- `xai_device_flow` — the same OAuth client over RFC 8628 device authorization for headless / SSH / VPS hosts.

Refresh tokens are rotated automatically by the auth manager. The bundled static catalog ships `grok-4.3`, `grok-4`, and `grok-4-fast`; live discovery uses xAI's `/v1/language-models` endpoint so image / video variants are excluded automatically.

## Models, reasoning, and metadata

Provider model discovery comes from the upstream provider API when available. CodeAlta can enrich or correct model metadata with:

- `models_dev_provider_id` to map a provider to the local models.dev catalog;
- `model_overrides` for context windows or output limits;
- `single_model_id` for single-model endpoints that do not expose `/models`;
- `models_include_regex` to keep only model ids matching one .NET regular expression, for example `gpt-5|claude` or `^gemini-.*-pro`.

When the models.dev provider id is inferred from the provider key, CodeAlta maps local aliases to models.dev ids where needed: `copilot` resolves as `github-copilot`, and `gemini` resolves as `google`.

Reasoning effort is selected per provider/model where supported. When a selected model does not support reasoning, CodeAlta shows the effective setting in selectors and live-tool model resolution.

## Context usage and compaction

The footer context indicator uses the provider/model input-token limit. If only a total context window is known, CodeAlta treats it as the practical input limit; if both total context and output limit are known, it derives input limit as `context_window - output_token_limit` unless an explicit input limit is configured.

Local raw-API compaction can be tuned with an optional block:

```toml
[providers.openai.compaction]
enabled = true
ratio = 0.95
summary_output_ratio = 0.10
post_compaction_target_ratio = 0.10
summary_share_of_target = 0.40
file_context_share_of_summary_target = 0.15
keep_last_user_message = true
allow_split_turn = true
```

Compaction fields are validated before the configuration is saved or accepted at startup:

| Field | Default | Accepted range / meaning |
| --- | --- | --- |
| `enabled` | `true` | Enables automatic threshold compaction. |
| `ratio` | `0.95` | Active-context/input-limit ratio that triggers automatic compaction; must be `> 0` and `<= 1`. |
| `summary_output_ratio` | `0.10` | Summarizer output budget as a share of the input limit; must be `> 0` and `<= 0.50`. |
| `post_compaction_target_ratio` | `0.10` | Preferred active-context target after compaction; must be `> 0` and `<= 1`. |
| `summary_share_of_target` | `0.40` | Share of the post-compaction target offered to summary generation; must be `> 0` and `<= 1`. |
| `file_context_share_of_summary_target` | `0.15` | Share of the summary target available for model-visible file context; must be `>= 0` and `<= 1`. |
| `keep_last_user_message` | `true` | Keeps the latest user message as an anchor during compaction. |
| `allow_split_turn` | `true` | Allows compaction to split a large turn while preserving continuation state. |

Most users should leave these defaults unchanged.
