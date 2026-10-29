---
title: Provider Configuration
---

# Provider Configuration

Providers are tables of `~/.alta/config.toml`. The provider editor writes the same file and keeps every setting it does not show; the **Configuration file** page of CodeAlta Desktop edits it whole and checks it as you type. A project can override a few values in `<project>/.alta/config.toml` (see [Project settings](readme.md#project-settings)).

## Provider tables

Each provider is a `[providers.<key>]` table. Keys are lower case. `codex` and `copilot` get their type when `type` is left out.

The types are `codex`, `copilot`, `xai`, `openai-chat`, `openai-responses`, `azure-openai`, `anthropic`, `google-genai`, `vertex-ai`, `mistral`, `claude-code` and `llama-cpp`. Plugins add others, such as `acp` for the [agents of the ACP registry](acp-agents.md) and `codex-cli` for [Codex CLI](setup.md#codex-cli). Aliases such as `openai`, `responses`, `aoai`, `gemini`, `vertex` and `github-copilot` are read as these types.

A type your version of CodeAlta does not know, one a newer version added, leaves that provider out; its table stays in the file as written.

## Common fields

| Field | Purpose |
| --- | --- |
| `enabled` | Turns the provider on or off. Missing means on. |
| `display_name` | Name shown in the lists. |
| `icon` | Icon in CodeAlta Desktop: a brand logo such as `mistral`, `ollama` or `openrouter`, or a general icon such as `rocket`, `server` or `flask-conical`. Missing means the logo found from the key, name, URL and type. |
| `color` | Color of the icon, `#rgb` or `#rrggbb`. Missing means the colors of the logo. |
| `type` | Provider type, see above. |
| `model` | Default model id. |
| `reasoning_effort` | Default effort: `none`, `minimal`, `low`, `medium`, `high`, `xhigh` or `max`. Each model offers its own values. |
| `context_size` | Default [context size](readme.md#context-and-compaction), in input tokens, for the models that offer it. |
| `api_key` / `api_key_env` | The API key, or the environment variable that holds it. Prefer `api_key_env`. |
| `api_url` | Endpoint. Azure OpenAI, Codex and Copilot require HTTPS, except on localhost. |
| `network_timeout_seconds` | Network timeout of `openai-chat`, `openai-responses`, `azure-openai`, `codex` and `llama-cpp`; 100 seconds when unset. |
| `protocol_trace` | Traces the requests of the OpenAI, Codex and Copilot providers. |
| `models_dev_provider_id` | The models.dev provider that describes the models. |
| `single_model_id` | The one model of an endpoint that lists none. |
| `models_include_regex` | A .NET regular expression: only the model ids it matches are shown, such as `^gemini-.*-pro`. |
| `models_order` | The order of the models: `provider`, `name`, or `name_version` (by name, the newest version first). Missing means `provider`, except for ACP agents. |
| `sort_models` | Older form of `models_order`: `true` is `name`, `false` is `provider`. `models_order` takes precedence. |
| `profile` | [Compatibility profile](#compatibility-profile). |
| `compaction` | [Compaction settings](#compaction-settings). |
| `model_overrides` | [Model metadata overrides](#model-metadata-overrides). |
| `extra_body` | More request-body fields, for `openai-chat` and `openai-responses`. |
| `request` | Headers and body fields of every request: [Request customizations](#request-customizations). |
| `model_request` | The same for one model: [Model request overrides](#model-request-overrides). |

## Fields by type

| Type | Credentials and endpoint | Other fields |
| --- | --- | --- |
| `openai-chat`, `openai-responses` | `api_key` or `api_key_env`; optional `api_url`, `organization_id`, `project_id` | `network_timeout_seconds`, `models_dev_provider_id`, `single_model_id`, `models_include_regex`, `extra_body`, `request`, `model_request`, `profile`, `compaction`, `model_overrides`, `protocol_trace`; `text_verbosity` for `openai-responses` only |
| `llama-cpp` | no key unless the server asks for one; optional `api_url` (`http://127.0.0.1:9931/v1` by default) | `api` (`chat`, the default, or `responses`), `network_timeout_seconds`, `models_dev_provider_id`, `single_model_id`, `models_include_regex`, `extra_body`, `request`, `model_request`, `profile`, `compaction`, `model_overrides`, `protocol_trace` (see [llama.cpp](setup.md#llamacpp)) |
| `azure-openai` | `api_key` or `api_key_env`; the `api_url` of the Azure resource | `network_timeout_seconds`, `models_dev_provider_id`, `single_model_id`, `models_include_regex`, `profile`, `compaction`, `model_overrides`, `protocol_trace` |
| `anthropic` | `api_key` or `api_key_env`; optional `api_url` | `prompt_cache_ttl`, `refusal_fallback`, `models_dev_provider_id`, `single_model_id`, `models_include_regex`, `request.headers`, `request.remove_headers`, `profile`, `compaction`, `model_overrides` |
| `google-genai`, `mistral` | `api_key` or `api_key_env`; optional `api_url` | `models_dev_provider_id`, `single_model_id`, `models_include_regex`, `request.headers`, `request.remove_headers`, `profile`, `compaction`, `model_overrides` |
| `vertex-ai` | `project` and `location`; optional `api_url` | the same as `google-genai` |
| `codex` | ChatGPT sign-in, no key; optional `api_url` | `network_timeout_seconds`, `models_include_regex`, `auth_source`, `account_id`, `max_concurrent_requests`, `text_verbosity`, `service_tier`, `web_search`, `include_encrypted_reasoning`, `model_discovery`, `response_transport`, `send_responses_beta_header`, `send_installation_id`, `installation_id_source`, `experimental`, `codex_home`, `profile`, `compaction`, `protocol_trace` |
| `copilot` | GitHub sign-in; optional `api_url` | `auth_source`, `github_enterprise_url`, `github_token_env`, `copilot_token_env`, `model_discovery`, `enable_model_policies`, `include_preview_models`, `experimental`, `single_model_id`, `models_include_regex`, `models_dev_provider_id`, `profile`, `compaction`, `model_overrides`, `protocol_trace` |
| `xai` | xAI sign-in; optional `api_url` | `auth_source`, `model_discovery`, `single_model_id`, `models_include_regex`, `models_dev_provider_id`, `request`, `model_request`, `profile`, `compaction`, `model_overrides`, `protocol_trace` |
| `claude-code` | none: `api_key`, `api_key_env` and `api_url` are refused | `config_dir`, `command`, `args`, `permission_mode`, `anthropic_api_key`, `single_model_id`, `models_include_regex` (see [Claude Code settings](setup.md#claude-code-settings)) |
| `codex-cli` | none: Codex signs in by itself, from the form of the provider | `codex_home`, `command`, `env`, `permission_mode` (see [Codex CLI](setup.md#codex-cli)) |
| `acp` | none: the agent signs in by itself | `command`, `arguments`, `env`, `permission_mode`, and `agent` and `agent_version` for an agent of the registry (see [Write one by hand](acp-agents.md#write-one-by-hand)) |

`text_verbosity` (`low`, `medium` or `high`) sets how long the answers of the models of an `openai-responses` or `codex` provider are:

- Not set, each model gets its own default: `low` for most GPT-5 and GPT-6 models, as in Codex.
- On `openai-responses`, that default goes to the OpenAI API only. An endpoint at another `api_url` gets `text_verbosity` only when you set it.
- A model that takes no verbosity, such as GPT-4.1 or o3, gets none.

## Codex, Copilot and xAI

The fields of the subscriptions take these values:

| Provider | Field | Values |
| --- | --- | --- |
| Codex | `auth_source` | `codealta_oauth` only |
| Codex | `model_discovery` | `codex_endpoint_with_static_fallback` (default), `codex_endpoint`, `static` |
| Codex | `response_transport` | `http` (default), `websocket_with_http_fallback` |
| Codex | `text_verbosity` | `low`, `medium`, `high`; not set: the model's default |
| Codex | `web_search` | `cached` (default), `live`, `disabled` |
| Codex | `service_tier` | `priority` (or `fast`), `default` |
| Codex | `installation_id_source` | `codealta_state`, `codex_home_import`, `codex_home_readonly` |
| Copilot | `auth_source` | `github_device_flow` (default), `github_token_env`, `copilot_token_env` |
| Copilot | `model_discovery` | `copilot_endpoint_with_static_fallback` (default), `copilot_endpoint`, `static` |
| xAI | `auth_source` | `xai_browser_oauth`, `xai_device_flow` |
| xAI | `model_discovery` | `xai_endpoint_with_static_fallback` (default), `xai_endpoint`, `static` |

- `github_token_env` and `copilot_token_env` name the variable that holds the token of their `auth_source`, and are required with it.
- A `static` list, or the fallback to it, does not prove that a model is available to your account; a sign-in or permission error is never hidden behind it.
- Codex sends its requests to `https://api.openai.com/v1`. The old `https://chatgpt.com/backend-api/codex` URL is read as that address; a custom `api_url` is left as it is.
- Codex `account_id` does not choose an account: the account you signed in with does. `installation_id_source` only sets optional telemetry headers.
- `send_responses_beta_header` is off by default; turn it on only for an endpoint that needs it.
- Codex takes no `api_key`, `api_key_env`, `extra_body`, `request` or `model_request`.

## Compatibility profile

`[providers.<key>.profile]` is for an endpoint that behaves differently from the API it imitates:

| Field | Meaning |
| --- | --- |
| `supports_developer_role` | Requests may use a developer message. |
| `supports_store` | Requests may send the `store` flag. |
| `supports_reasoning_effort` | `reasoning_effort` can be sent. |
| `streams_usage` | Streamed answers include usage. |
| `supports_thought_signatures` | The provider keeps thought signatures between requests. |
| `requires_tool_result_name` | A tool result must carry the tool name. |
| `requires_assistant_after_tool_result` | An assistant message must follow the tool results. |
| `supports_tool_result_images` | A tool result can carry an image. When `false`, the image goes in a user message after the tool results. By default only OpenAI's own Responses endpoints take images in tool results. |
| `supports_cache_control` | Anthropic Messages requests carry the prompt-cache markers (on by default). Set `false` for a compatible endpoint that refuses them: it then caches nothing. |
| `supports_tool_deferral` | Anthropic Messages requests can defer tools, so the model loads MCP tools on demand. By default only the Claude API does, for Claude 4.5 models and later. |
| `supports_strict_tools` | Strict tool schemas are supported. |
| `thinking_format` | Name of the provider's thinking format. |
| `max_tokens_field_name` | Body field of the output limit, such as `max_output_tokens` or `max_completion_tokens`. |
| `reasoning_field_names` | Answer fields read as reasoning. |
| `reasoning_input_field_name` | Assistant-message field that carries earlier reasoning back. |
| `supports_parallel_tool_calls` | Send the Chat Completions `parallel_tool_calls` flag when there are tools (on by default). |

The built-in Alibaba and DeepSeek providers come with these settings, so you do not restate them:

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

Values you write in `profile`, `request` or `extra_body` take precedence over them.

## Single-model endpoints

`single_model_id` is for an endpoint that serves one model or lists none. The built-in MiniMax provider uses it:

```toml
[providers.minimax]
single_model_id = "MiniMax-M2.7"
```

## Request customizations

`[providers.<key>.request]` adds headers, and body fields for OpenAI-compatible providers:

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

The values apply in this order, the last winning:

1. the defaults of CodeAlta and of the built-in provider;
2. `request.remove_headers` and `request.remove_extra_body`, which remove defaults;
3. `request.headers` and `request.extra_body`;
4. a top-level `extra_body`, which is still read;
5. the authentication headers of the provider, which cannot be removed or replaced.

`Authorization`, `api-key` and `x-api-key` are reserved for authentication. Traces hide the headers whose names look like secrets (`Authorization`, `*-key`, `*token*`, `*secret*`).

## Session values in headers

For `openai-chat` and `openai-responses`, a header value in `request.headers` or `model_request.<model-id>.headers` can name values filled for each request:

| Placeholder | Value |
| --- | --- |
| `${session.id}` | The id of the session: the same for every request of a conversation, kept when it resumes. |
| `${run.id}` | The id of the current run: one prompt and the work it starts. |
| `${model.id}` | The id of the selected model. |
| `${provider.key}` | The key of the provider, such as `opencode-go`. |

OpenCode Go and Zen ask for an `x-opencode-session` header that stays the same for a conversation, which keeps it on the same backend and its prompt cache:

```toml
[providers.opencode-go]
type = "openai-chat"
api_url = "https://opencode.ai/inference/go/v1"
api_key_env = "CODEALTA_OPENCODE_GO_API_KEY"
models_dev_provider_id = "opencode-go"

[providers.opencode-go.request.headers]
"x-opencode-session" = "${session.id}"
```

A value can mix text and placeholders, such as `"codealta-${session.id}"`. An unknown `${...}` is sent as written. A request outside a session, such as the model list, leaves out the headers that name a placeholder. Other provider types send the value as written.

## Model request overrides

`[providers.<key>.model_request.<model-id>]` changes requests only for that model. Model ids match as in [model metadata overrides](#model-metadata-overrides).

```toml
[providers.openai.model_request."gpt-5.5"]
remove_extra_body = ["reasoning_split"]

[providers.openai.model_request."gpt-5.5".extra_body]
reasoning = { effort = "high" }

[providers.openai.model_request."gpt-5.5".headers]
X-Model-Mode = "high-reasoning"
```

## Model metadata overrides

`[providers.<key>.model_overrides.<model-id>]` corrects what CodeAlta knows of a model, when the provider and models.dev say nothing or are wrong. It changes the model lists, the context usage and when compaction starts, not the requests.

| Field | Effect |
| --- | --- |
| `display_name` | Name of the model. |
| `description` | Description of the model. |
| `context_window` | Total context window, in tokens. |
| `input_token_limit` | Maximum input tokens. |
| `output_token_limit` | Maximum output tokens. |
| `max_tokens` | Output limit sent with requests; `output_token_limit` when unset. |
| `supports_reasoning` | The model reasons. |
| `supports_tool_call` | The model calls tools. |
| `supports_attachments` | The model reads images and attachments. |
| `supports_structured_output` | The model gives structured output. |

Quote a model id that contains dots, slashes or other punctuation. A model id also matches its form with a date suffix.

```toml
[providers.openai.model_overrides."your-model-id"]
context_window = 200000
input_token_limit = 180000
output_token_limit = 20000
supports_reasoning = true
supports_tool_call = true
```

### Models of a provider

- `models_dev_provider_id` names the models.dev provider that describes the models; without it, CodeAlta finds one (see [Models and reasoning](readme.md#models-and-reasoning)).
- `models_include_regex` keeps only the models whose id it matches, such as `gpt-5|claude`.
- `single_model_id` gives the one model of an endpoint that lists none.
- `models_order` lists them in the provider's order (`provider`), by name (`name`), or by name with the highest version first among the names that differ only by their numbers (`name_version`): `Claude Fable 5.1` before `Claude Fable 5`, `Gemini 4 Argon` before `Gemini 3.8 Flash`. The models of an [ACP agent](acp-agents.md) are in the `name_version` order unless you choose another one.

## Compaction settings

`[providers.<key>.compaction]` tunes when and how CodeAlta compacts the sessions of a provider:

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

| Field | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | Compacts by itself when the context reaches `ratio`. |
| `ratio` | `0.95` | Share of the input limit that starts a compaction; above 0, at most 1. |
| `summary_output_ratio` | `0.10` | Output budget of the summary, as a share of the input limit; above 0, at most 0.50. |
| `post_compaction_target_ratio` | `0.10` | Size the context aims for after compaction; above 0, at most 1. |
| `summary_share_of_target` | `0.40` | Share of that target for the summary; above 0, at most 1. |
| `file_context_share_of_summary_target` | `0.15` | Share of the summary for the content of files; 0 to 1. |
| `keep_last_user_message` | `true` | Keeps your last message as it is. |
| `allow_split_turn` | `true` | Lets compaction split a large turn. |

The values are checked when the file is saved and at startup. Most users keep the defaults.
