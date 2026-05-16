# Codex ChatGPT Subscription API Access Specification

Status: **Draft**  
Last updated: **2026-05-01**

## 1. Purpose

Add an experimental LocalRuntime provider that lets CodeAlta run Codex-capable ChatGPT subscription models through CodeAlta's own local agent loop.

This feature is the direct API variant of Codex support:

- it uses ChatGPT/Codex OAuth credentials, not an OpenAI platform API key;
- it calls the ChatGPT Codex backend endpoint, not `https://api.openai.com/v1/responses`;
- it reuses the official OpenAI .NET SDK `ResponsesClient` for Responses serialization and SSE parsing;
- it keeps CodeAlta responsible for local tools, approvals, persistence, compaction, and normalized events.

This is **not** a replacement for `src/CodeAlta.Agent.Codex`. The Codex app-server backend remains the primary supported Codex integration. This provider is only for users who explicitly want Codex models inside CodeAlta's LocalRuntime.

## 2. Non-goals

Do not implement these in the first version:

- Direct Chat Completions access to the ChatGPT Codex backend.
- Files, batches, images, realtime, conversations, or any other OpenAI SDK subclient under this provider.
- Agent identity, remote-control, Codex cloud task, or Codex app-server protocol support.
- Copilot access.
- Generic OpenAI-compatible URL presets for ChatGPT subscription access.
- Multi-account rotation, fallback accounts, or any rate-limit bypass behavior.
- Automatic writes to Codex-owned auth files.

## 3. References

Primary local source references:

- CodeAlta OpenAI LocalRuntime:
  - `src/CodeAlta.Agent.OpenAI/OpenAIResponsesTurnExecutor.cs`
  - `src/CodeAlta.Agent.OpenAI/OpenAIProviderSdkFactory.cs`
  - `src/CodeAlta.Agent.OpenAI/OpenAIAgentBackendOptions.cs`
  - `src/CodeAlta/App/RawApiBackendRegistrar.cs`
- CodeAlta LocalRuntime contracts:
  - `src/CodeAlta.Agent/LocalRuntime/LocalAgentTurnContracts.cs`
  - `src/CodeAlta.Agent/LocalRuntime/LocalAgentSession.cs`
  - `src/CodeAlta.Agent/LocalRuntime/FileSystemLocalAgentSessionStore.cs`
- OpenAI .NET SDK NuGet package (`OpenAI` package `2.10.0`):
  - `OpenAI.Responses.ResponsesClient`
  - `OpenAI.Responses.CreateResponseOptions`
  - `System.ClientModel.Primitives.AuthenticationPolicy`
  - `System.ClientModel.Primitives.PipelinePolicy`
  - `System.ClientModel.Primitives.ClientPipelineOptions.AddPolicy(...)`
  - `System.ClientModel.Primitives.RequestOptions.AddPolicy(...)`
- Codex source at `C:\code\codex`:
  - `codex-rs/core/src/client.rs`
  - `codex-rs/backend-client/src/client.rs`
  - `codex-rs/login/src/server.rs`
  - `codex-rs/login/src/device_code_auth.rs`
  - `codex-rs/login/src/auth/storage.rs`
  - `codex-rs/login/src/auth/manager.rs`
  - `codex-rs/login/src/token_data.rs`
  - `codex-rs/codex-api/src/endpoint/models.rs`
  - `codex-rs/core/src/installation_id.rs`
  - `codex-rs/codex-api/src/endpoint/responses_websocket.rs`

Public references:

- Codex CLI docs: <https://developers.openai.com/codex/cli>
- Codex docs: <https://developers.openai.com/codex/cloud>
- OpenAI Responses API docs: <https://platform.openai.com/docs/api-reference/responses>
- OpenAI Shell tool safety docs: <https://developers.openai.com/api/docs/guides/tools-shell>

## 4. Provider identity

Add a first-class provider type:

```toml
[providers.codex]
type = "codex"
display_name = "Codex"
model = "gpt-5.3-codex"
experimental = true
```

Provider identity rules:

- The configured provider `type` must be `codex`.
- Do not register this as `openai-responses` with a custom URL.
- The default backend id should be distinct from the public OpenAI Responses backend, for example `codex`.
- The protocol family may reuse the OpenAI Responses executor internally, but user-facing provider/backend labels must make the subscription-backed nature explicit.
- The user agent and originator must be truthful: use CodeAlta identity, not another product's identity.

Recommended default endpoint:

```text
https://chatgpt.com/backend-api/codex
```

The SDK `ResponsesClient` appends `/responses`, producing:

```text
https://chatgpt.com/backend-api/codex/responses
```

## 5. Configuration schema

Extend the provider config model parsed by `RawApiBackendRegistrar` with the following fields for `codex`.

Required or defaulted fields:

| Field | Type | Required | Default | Notes |
|---|---:|---:|---|---|
| `type` | string | yes | | Must be `codex`. |
| `display_name` | string | no | `Codex` | UI label. |
| `model` | string | no | first visible picker entry | Optional preferred default model. It must not narrow the model picker/catalog. |
| `base_url` / `api_url` | URI | no | `https://chatgpt.com/backend-api/codex` | Advanced/debug only. Reject non-HTTPS except localhost test transports. |
| `auth_source` | enum | no | `codealta_oauth` | See auth sources below. |
| `account_id` | string | no | token default | Explicit ChatGPT account/workspace id override. |
| `max_concurrent_requests` | int | no | `16` | Local CodeAlta safety limit per credential/account. Codex CLI and pi-mono do not impose an equivalent account-wide limiter; lower this only when the service or network shows instability. |
| `reasoning_effort` | enum | no | backend/model default | Normal CodeAlta reasoning mapping. |
| `text_verbosity` | enum | no | `medium` | `low`, `medium`, or `high`. |
| `include_encrypted_reasoning` | bool | no | `true` | Adds `reasoning.encrypted_content`. |
| `model_discovery` | enum | no | `static` | `static`, `codex_endpoint_with_static_fallback`, or `codex_endpoint`. |
| `response_transport` | enum | no | `websocket_with_http_fallback` | `websocket_with_http_fallback` or `http`. The default tries the Codex Responses WebSocket transport and falls back to HTTP/SSE only before any user-visible stream delta is emitted. |
| `send_responses_beta_header` | bool | no | `true` | Sends `OpenAI-Beta: responses=experimental` through a typed SDK pipeline policy. |
| `send_installation_id` | bool | no | `false` | Adds a stable non-secret installation id to `client_metadata` only when explicitly enabled. |
| `installation_id_source` | enum | no | `codealta_state` | `codealta_state`, `codex_home_import`, or `codex_home_readonly`. |
| `experimental` | bool | yes for UI-created config | `false` | Must be true before registration. |

Supported `auth_source` values:

- `codealta_oauth`: CodeAlta owns browser and device-code OAuth and secure token storage.
- `codex_auth_import`: Read Codex auth once with explicit user consent, then copy into CodeAlta-owned secure storage.
- `codex_auth_file_readonly`: Read `$CODEX_HOME/auth.json` read-only for development/testing. Do not write it.
- `external_token_command`: Deferred. If implemented later, must be explicit, warning-gated, and disabled by default.

Do not support `api_key` or `api_key_env` for this provider. If those fields are present, fail registration with a clear error: ChatGPT OAuth is required and access tokens must not be treated as OpenAI API keys.

Do not expose generic `extra_body` for this provider in normal config. Codex-specific body patches must be typed/provider-owned so users cannot accidentally create suspicious backend calls.

Installation identity:

- `CodeAlta` is the product identity and should appear in `originator`, `User-Agent`, and user-visible labels.
- `client_metadata.x-codex-installation-id` must be a stable per-CodeAlta-installation id, not the literal product name.
- Do not send this field by default in v1. Direct-access client implementations can operate without it, and omitting it avoids unnecessary correlation with another local Codex installation.
- If `send_installation_id = true`, generate a random UUID on first use and store it under CodeAlta-owned state, for example in an installation metadata file protected like other non-secret state.
- Do not derive this id from user/account identifiers, machine names, paths, or tokens.
- The id may be sent as the UUID string or a `codealta:<uuid>` value, but it must be stable across restarts and resettable when the user clears CodeAlta local state.

Codex installation-id inference:

- Codex stores its installation id in `$CODEX_HOME/installation_id` as a plain UUID string.
- CodeAlta may read this value when `installation_id_source = "codex_home_import"` or `installation_id_source = "codex_home_readonly"`.
- `codex_home_import` should copy a valid Codex UUID into CodeAlta-owned state once, then continue using the copied value.
- `codex_home_readonly` should read the Codex UUID for each process/session and must not create, rewrite, or normalize Codex-owned files.
- If the Codex file is missing, empty, or invalid, CodeAlta must fall back to its own generated installation id unless the user explicitly requested strict read-only import.
- Do not silently scan `~/.codex` in the default `codealta_oauth` path. Reusing the Codex id links CodeAlta requests with a separate Codex installation, so it should happen only as part of an explicit import/read-only mode.
- If `send_installation_id = false`, do not resolve, generate, import, or send any installation id for normal model turns.

## 6. Registration behavior

Update `RawApiBackendRegistrar`:

1. Add a case for `type = "codex"`.
2. Require `experimental = true` or an equivalent feature flag.
3. Reject API-key fields.
4. Create an OpenAI Responses-compatible LocalRuntime backend with Codex-specific provider options.
5. Set the descriptor display name to make usage explicit, for example `Codex`.
6. Set provider profile defaults:
   - `SupportsStore = false`.
   - reasoning effort supported for known Codex reasoning models.
   - function tools supported through CodeAlta LocalRuntime only.
   - image input support only if model profile says yes.
   - no public OpenAI model-list fallback.
7. Set compaction defaults conservatively. Do not assume Codex app-server compaction semantics are available.

Implementation can either live in `src/CodeAlta.Agent.OpenAI/` under a `CodexSubscription` namespace or in a sibling project such as `src/CodeAlta.Agent.OpenAICodexSubscription/`. Prefer the smaller change unless dependencies or public API boundaries become awkward.

## 7. OpenAI SDK integration

Use `OpenAI.Responses.ResponsesClient`.

Required client construction:

```csharp
var options = new OpenAIClientOptions
{
    Endpoint = new Uri("https://chatgpt.com/backend-api/codex"),
    UserAgentApplicationId = $"CodeAlta/{version}",
};
options.AddPolicy(new CodexSubscriptionHeadersPolicy(headerContext), PipelinePosition.BeforeTransport);

var client = new ResponsesClient(
    new ChatGptOAuthAuthenticationPolicy(authManager),
    options);
```

Because some headers vary per session/turn, adjust the current factory shape. Replace or supplement:

```csharp
internal Func<string?, ResponsesClient>? ResponsesClientFactory { get; set; }
```

with a context-aware delegate:

```csharp
internal sealed record OpenAIResponsesClientFactoryContext(
    string? ModelId,
    string SessionId,
    AgentRunId RunId,
    LocalAgentProviderDescriptor Provider);

internal Func<OpenAIResponsesClientFactoryContext, ResponsesClient>? ResponsesClientFactory { get; set; }
```

Then update `OpenAIProviderSdkFactory.CreateResponsesClient(...)` and `OpenAIResponsesTurnExecutor.ExecuteTurnAsync(...)` to pass the full context. Keep a compatibility path for existing providers if needed.

Do not create an `OpenAIClient` root client for this provider unless a future Codex-specific endpoint needs it. The only SDK client needed in v1 is `ResponsesClient`.

Use the OpenAI .NET SDK NuGet package for `ResponsesClient` and generated Responses protocol models. CodeAlta owns the ChatGPT subscription WebSocket transport in `CodeAlta.Agent.OpenAI` so builds do not require a local `C:\code\openai-dotnet` checkout.

HTTP/SSE requests use `ResponsesClient`. The ChatGPT subscription WebSocket path uses a session-scoped WebSocket transport that mirrors the local SDK request/update shape while applying ChatGPT OAuth/session headers instead of platform API-key auth. If WebSocket connection, request setup, or in-stream receive fails before any user-visible stream delta is emitted, retry the same turn over HTTP/SSE unless `response_transport = "http"` already forced HTTP-only mode; after such fallback, keep that live CodeAlta session on HTTP/SSE to avoid repeated failed WebSocket handshakes.

## 8. Auth implementation

### 8.1 OAuth constants

Use the current Codex OAuth details from Codex source:

```text
Issuer:        https://auth.openai.com
Authorize:     https://auth.openai.com/oauth/authorize
Token:         https://auth.openai.com/oauth/token
Local port:    1455
Redirect:      http://localhost:1455/auth/callback
Client id:     app_EMoamEEZ73f0CkXaXp7hrann
Browser scope: openid profile email offline_access api.connectors.read api.connectors.invoke
```

Authorize URL parameters:

- `response_type=code`
- `client_id=<client id>`
- `redirect_uri=http://localhost:1455/auth/callback`
- `scope=openid profile email offline_access api.connectors.read api.connectors.invoke`
- `code_challenge=<S256 challenge>`
- `code_challenge_method=S256`
- `state=<random state>`
- `id_token_add_organizations=true`
- `codex_cli_simplified_flow=true`
- `originator=codealta`
- optional `allowed_workspace_id=<workspace/account id>` when the user selected a specific workspace/account.

Device-code flow is required in v1 for headless and remote onboarding:

- request user code: `<issuer>/api/accounts/deviceauth/usercode`
- verification URL: `<issuer>/codex/device`
- poll token: `<issuer>/api/accounts/deviceauth/token`
- exchange authorization code at `<issuer>/oauth/token`
- redirect for exchange: `<issuer>/deviceauth/callback`

Device-code rules:

- Show the user code and verification URL clearly.
- Poll with the server-provided interval and stop on expiry.
- Preserve the same final token storage shape as browser PKCE.
- Use device login as a first-class login option, not only a fallback.

### 8.2 Token shape

Store these fields in CodeAlta-owned secure storage:

```jsonc
{
  "issuer": "https://auth.openai.com",
  "client_id": "app_EMoamEEZ73f0CkXaXp7hrann",
  "access_token": "...",
  "refresh_token": "...",
  "id_token": "...",
  "expires_at": "2026-04-24T12:34:56Z",
  "account_id": "...",
  "account_label": "...",
  "is_fedramp": false,
  "scopes": ["openid", "profile", "email", "offline_access", "api.connectors.read", "api.connectors.invoke"]
}
```

Do not log tokens. Do not serialize tokens into normal session event history.

### 8.3 Account id extraction

Parse the JWT payload without validating signature only for local metadata extraction. Do not use unvalidated JWT content for security decisions beyond request routing metadata.

Use this precedence:

1. Explicit configured `account_id` if present.
2. `tokens.account_id` when importing a Codex auth record.
3. The ChatGPT auth claim object under `https://api.openai.com/auth` in the access token or id token.
4. Fail with a re-auth/account-selection message if the backend requires `ChatGPT-Account-Id` and none is available.

The claim object can also contain plan/user/account/FedRAMP metadata. Preserve only non-secret metadata needed for routing and user display.

### 8.4 Storage

Implement `OpenAICodexSubscriptionAuthManager` with these responsibilities:

- load credentials from CodeAlta secure storage;
- import Codex file/keyring shape only when explicitly requested;
- refresh tokens when they are expired or close to expiry;
- serialize refreshes per account with a single-flight lock;
- expose current access token, account id, FedRAMP flag, and display metadata;
- redact secrets in all exceptions/logs;
- never write to `$CODEX_HOME/auth.json` in v1.

Preferred secure storage by OS:

- Windows: DPAPI-protected file or Windows Credential Manager.
- macOS/Linux: platform keyring when available.
- Fallback: encrypted or access-restricted file under CodeAlta state root, with a startup warning.

For `codex_auth_file_readonly`, resolve Codex home as:

1. `CODEX_HOME` environment variable if set;
2. `%USERPROFILE%\.codex` on Windows;
3. `$HOME/.codex` on Unix.

Read the file shape from Codex source (`AuthDotJson`): `auth_mode`, `OPENAI_API_KEY`, `tokens`, `last_refresh`, optional `agent_identity`. Only `tokens` are relevant. Ignore API-key-only auth records.

### 8.5 Refresh

Refresh using the token endpoint:

```text
POST https://auth.openai.com/oauth/token
Content-Type: application/x-www-form-urlencoded

grant_type=refresh_token
client_id=<client id>
refresh_token=<refresh token>
```

Rules:

- Refresh before a request when `expires_at <= now + 5 minutes`.
- If refresh fails with invalid/expired refresh token, delete only CodeAlta-owned credentials and prompt re-auth.
- Do not keep retrying auth failures.
- Do not replay a streaming request after partial output or tool execution has begun.

## 9. Authentication and header policies

### 9.1 `ChatGptOAuthAuthenticationPolicy`

Create a custom SDK `AuthenticationPolicy`.

Behavior:

- Before each request attempt, call `authManager.GetAccessTokenAsync(cancellationToken)`.
- Set `Authorization: Bearer <access token>`.
- Redact the `Authorization` header in any diagnostics.
- Do not use `ApiKeyCredential`; the token is not an OpenAI API key.

### 9.2 `CodexSubscriptionHeadersPolicy`

Create a `PipelinePolicy` added at `PipelinePosition.BeforeTransport`.

Set these headers:

| Header | Value |
|---|---|
| `ChatGPT-Account-Id` | selected/resolved account id, when present |
| `originator` | `codealta` |
| `User-Agent` | truthful CodeAlta identifier if SDK default is insufficient |
| `OpenAI-Beta` | `responses=experimental` when `send_responses_beta_header = true` |
| `session_id` | `LocalAgentTurnRequest.SessionId` or stable CodeAlta thread/session id |
| `x-client-request-id` | same stable session/request id used for `session_id` |
| `X-OpenAI-Fedramp` | `true` only when token/account metadata says FedRAMP |

Do not set Codex WebSocket beta headers in the SSE provider.

Optional current Codex headers that should be supported behind typed options, not by default:

- `x-codex-beta-features`
- `x-codex-turn-metadata`
- `x-responsesapi-include-timing-metrics`

Do not send these unless CodeAlta has a clear purpose and tests:

- `x-openai-subagent`
- `x-codex-parent-thread-id`
- `x-codex-window-id`
- `x-openai-memgen-request`

`OpenAI-Beta: responses=experimental` is not a blocker for SDK use. The OpenAI .NET SDK does not need to model this header directly because `OpenAIClientOptions.AddPolicy(..., PipelinePosition.BeforeTransport)` can set arbitrary request headers before transport. Keep this header enabled by default until live backend testing proves it is unnecessary, then change only the default or provider option.

### 9.3 `CodexTurnStatePolicy`

Preserve `x-codex-turn-state` for same-turn sticky routing.

Local source behavior:

- Codex receives `x-codex-turn-state` from SSE/WebSocket response headers.
- Codex replays the value unchanged on follow-up requests within the same turn.
- Codex clears the value when a new turn starts.
- Codex tests assert the first request has no state, same-turn continuation has the captured state, and the next turn has no state.

SDK feasibility:

- `ResponsesClient.CreateResponseStreamingAsync(...)` returns an `AsyncCollectionResult<StreamingResponseUpdate>`.
- The normal `await foreach` over updates does not expose response headers directly.
- The SDK streaming implementation still receives a `ClientResult` whose raw `PipelineResponse` contains headers before the SSE content stream is consumed.
- A custom `PipelinePolicy` can inspect `message.Response.Headers` after `ProcessNext/ProcessNextAsync` returns, without reading or buffering the stream.

Implementation requirements:

- Add a stateful policy for this provider, separate from the static header-setting policy or as a clearly isolated component inside it.
- Before sending a request, read the current turn state's captured value and set `x-codex-turn-state` only if the value exists.
- After transport returns, read `x-codex-turn-state` from response headers and store it only if no value has already been captured for the current turn.
- Scope the storage to a single LocalRuntime turn or request chain, not the whole session, not the account, and not persisted session history.
- Clear the captured value before the next user turn starts.
- Apply the same state object to HTTP/SSE requests and WebSocket handshake headers for the active LocalRuntime run so retries and tool-call follow-ups share captured state without leaking it to the next run.
- Do not synthesize or mutate the value.
- Test the policy with a mock transport that returns the header and then assert that a same-turn follow-up sends it while a new turn does not.

If CodeAlta v1 performs only one Responses request per turn and never issues same-turn continuation requests, missing turn-state replay is unlikely to affect normal output. Still implement the policy now because it is low-risk with the SDK pipeline and protects retries/continuations added later.

## 10. Request body requirements

Start from the existing `OpenAIResponsesTurnExecutor` request mapping, then enforce Codex-specific deltas.

Required body shape:

| JSON field | Requirement |
|---|---|
| `model` | selected model id |
| `instructions` | composed CodeAlta system/developer instructions |
| `input` | full replayable LocalRuntime conversation in Responses item form |
| `tools` | CodeAlta-approved function tools only |
| `tool_choice` | `auto` when tools exist |
| `parallel_tool_calls` | `true` when tools exist, but CodeAlta may still execute safely/sequentially |
| `store` | `false` |
| `stream` | `true` |
| `reasoning` | from CodeAlta reasoning effort/profile when supported |
| `text.verbosity` | configured verbosity, default `medium` |
| `include` | include `reasoning.encrypted_content` when enabled and supported |
| `prompt_cache_key` | stable CodeAlta session/thread id |
| `client_metadata.x-codex-installation-id` | omitted by default; generated/imported stable non-secret installation id only when `send_installation_id = true` |
| `service_tier` | only if a typed provider option is configured |

Use first-class SDK properties where available:

- `Model`
- `Instructions`
- `InputItems`
- `Tools`
- `ToolChoice`
- `ParallelToolCallsEnabled`
- `StoredOutputEnabled`
- `StreamingEnabled`
- `ReasoningOptions`
- `TextOptions` where possible
- `ServiceTier`
- `IncludedProperties` for `IncludedResponseProperty.ReasoningEncryptedContent`

Use `JsonPatch` for fields not represented by SDK 2.10.0 or where the SDK type lags backend behavior:

- `prompt_cache_key`
- `client_metadata`
- `text.verbosity` if not first-class in the installed SDK
- any required beta/experimental backend field

Do not set `PreviousResponseId` by default. LocalRuntime already persists and replays the conversation. Only use provider-native continuation if a future design proves it is safe with tool replay, compaction, and encrypted reasoning continuity.

Do not send arbitrary user-supplied `extra_body`.

## 11. Turn executor changes

The current `OpenAIResponsesTurnExecutor` can be reused, but it needs small extension points.

Required refactor:

1. Add a context-aware `ResponsesClientFactory` as described above.
2. Add an internal request customization hook:

```csharp
internal sealed record OpenAIResponsesRequestCustomizationContext(
    LocalAgentTurnRequest Request,
    CreateResponseOptions Options);

internal Action<OpenAIResponsesRequestCustomizationContext>? ResponsesRequestCustomizer { get; set; }
```

3. Call the customizer after the common Responses payload is populated and before sending.
4. Keep existing public OpenAI behavior unchanged when no customizer is set.
5. For this provider, configure the customizer to:
   - force `StoredOutputEnabled = false`;
   - force `StreamingEnabled = true`;
   - add `IncludedResponseProperty.ReasoningEncryptedContent` when enabled;
   - set or patch `prompt_cache_key = request.SessionId`;
   - set or patch `text.verbosity`;
   - set or patch `client_metadata.x-codex-installation-id` only when `send_installation_id = true`;
   - remove/ignore user `ExtraBody`.

If the hook becomes too awkward, implement a dedicated `CodexSubscriptionResponsesTurnExecutor` by copying only the current Responses executor's necessary mapping logic. Avoid broad refactors.

## 12. Model discovery and model profiles

Do not use `OpenAIModelClient` for this provider.

CodeAlta defaults to a curated static picker list so the ChatGPT subscription experience stays aligned with Codex model visibility. Implement model discovery in this order:

1. If `model_discovery = "static"`, expose the bundled static Codex catalog.
2. If `model_discovery = "codex_endpoint"` or `codex_endpoint_with_static_fallback`, call the Codex model endpoint:

```text
GET https://chatgpt.com/backend-api/codex/models?client_version=<semver>
```

Use the same auth/header policies as Responses. Decode Codex's `ModelsResponse { models: Vec<ModelInfo> }` shape (including current fields such as `slug`, `visibility`, `input_modalities`, `support_verbosity`, `default_reasoning_level`, and `default_verbosity`), not the public OpenAI model-list shape. Preserve ETag for caching if returned.

3. Filter dynamic results to models where Codex metadata says `supported_in_api = true`. Expose only listable, non-hidden models for UI pickers, and include `requires_websocket` models only when WebSocket transport is enabled.
4. Treat a configured `model` as a preferred default selection only. It must not narrow the picker/catalog to one entry; if the preferred model is not visible, the UI falls back to the first visible entry.
5. If `model_discovery = "codex_endpoint_with_static_fallback"` falls back, expose the bundled static Codex catalog.

Static allow-list requirements:

- Mirror the model catalog Codex provides at CodeAlta release time. Do not invent or hand-curate unrelated public OpenAI models.
- Generate the list from Codex's bundled model catalog or from a checked-in copy of the Codex `ModelsResponse` shape reviewed during release.
- Treat this list as the default UI picker source; endpoint discovery remains an explicit opt-in for development or diagnostics.
- Include capability metadata per model:
  - context window when known;
  - supports reasoning effort;
  - supports reasoning summary;
  - supports encrypted reasoning continuity;
  - supports `text.verbosity`;
  - supports image input;
  - supports tools/function calls;
  - default reasoning effort and verbosity.
- Do not show unknown, hidden, or non-listable models in the picker by default.
- Allow an unsafe explicit model override only behind a clearly named debug flag.

At the time of this spec update, the curated visible/listable picker catalog is `gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`, `gpt-5.3-codex`, and `gpt-5.2`.

Model-list failures:

- Auth/account errors must surface as auth/account errors.
- 404/shape changes from the Codex endpoint should fall back to the cached/static list only in `codex_endpoint_with_static_fallback` mode.
- Never fall back to public OpenAI `/models`.

## 13. WebSocket transport

CodeAlta now defaults the ChatGPT subscription provider to WebSocket with HTTP/SSE fallback. Set `response_transport = "http"` to force HTTP/SSE only.

Observed official Codex behavior:

- WebSocket transport is enabled only when the provider advertises WebSocket support and the session has not switched to HTTP fallback.
- The WebSocket URL is the Responses URL with `https` changed to `wss`, for example `wss://chatgpt.com/backend-api/codex/responses`.
- The WebSocket handshake is a `GET` upgrade request with normal auth/account headers plus `OpenAI-Beta: responses_websockets=2026-02-06` and an `x-client-request-id`.
- The stream payload is JSON WebSocket messages, not SSE frames. A normal request is sent as a `response.create` message whose fields largely match the Responses create body.
- WebSocket response headers are used for the same routing/model metadata as SSE response headers, including `x-codex-turn-state`, `x-models-etag`, `x-reasoning-included`, and `openai-model`.
- The connection can be preconnected before a turn, and can be prewarmed with a `response.create` payload that sets `generate = false`.
- A healthy connection is reused across requests for the same model-client session/window.
- When the next request is a strict prefix extension of the previous request plus server-returned output items on a live reused WebSocket, the client can send only the input delta with `previous_response_id`.
- When request fields change or the transcript is not a strict extension, the client sends a full `response.create` payload.
- A WebSocket connection-limit error asks the client to reconnect and retry on a new connection.
- A `426 Upgrade Required`, failed connection attempts, or exhausted WebSocket retry budget switches the session to HTTP SSE fallback.

Why WebSockets exist:

- They reduce per-turn latency by allowing connection setup before the model request.
- They reduce repeated handshake/TLS overhead through connection reuse.
- They reduce payload size on follow-up requests through `previous_response_id` plus incremental input deltas.
- They provide a single bidirectional transport that can carry multiple request/response streams over the connection lifecycle.

Transport rules:

- HTTP/SSE remains the correctness fallback and uses the same `/codex/responses` model contract and core request body fields.
- Do not bypass CodeAlta's LocalRuntime tool/permission model for either transport.
- Fall back from WebSocket to HTTP/SSE only before any user-visible stream delta is emitted; when falling back, discard non-visible WebSocket response aggregation before reading the HTTP/SSE stream. After visible assistant/reasoning output starts, surface the stream failure so CodeAlta does not duplicate partial output or tool calls.
- Reuse one WebSocket session per active CodeAlta agent session and serialize requests on that session.
- Close and remove the per-session WebSocket when the CodeAlta session is disposed, and idle-expire cached WebSocket sessions after five minutes of inactivity.
- If a reused WebSocket is stale and fails while sending the next request, reconnect once and send the full request payload instead of a `previous_response_id` delta.
- Parse wrapped WebSocket error frames such as `{ "type": "error", "status": ..., "error": ... }` into status-bearing request failures; treat `websocket_connection_limit_reached` as retryable on a fresh connection rather than as an HTTP fallback signal.
- Wrap WebSocket receive waits in a per-message idle timeout so a silent socket cannot hang indefinitely.
- Capture known side-channel frames and handshake/header metadata such as `codex.rate_limits`, `response.metadata`, model verification, server-model notifications, and models-etag metadata as redacted provider-state diagnostics only; they must not alter assistant content, tool execution, retry policy, request identity, or usage-limit handling.
- `previous_response_id` and input-delta continuation may be used only on a currently live/reused WebSocket for in-memory CodeAlta sessions created in the current process. HTTP/SSE, HTTP fallback, new WebSocket connections, and sessions reloaded from disk must send full replayable input and must not resume a stored provider response id.
- When request fields change or the transcript is not a strict extension of the previous request plus assistant/tool output, send full input and clear continuation state.
- CodeAlta's ChatGPT subscription WebSocket transport applies the Responses WebSocket message shape with ChatGPT OAuth/account headers and must remain independent from platform API-key WebSocket helpers in the OpenAI SDK.

## 14. Concurrency, retry, and rate-limit behavior

Defaults:

- up to 16 active requests per ChatGPT account by default, configurable with `max_concurrent_requests`;
- one active turn per CodeAlta LocalRuntime session;
- no request replay after partial streamed output or after tool execution begins.

The account-level guard is a local CodeAlta safety valve, not a behavior observed in Codex CLI or pi-mono. If all slots are busy, CodeAlta should surface an explicit waiting message that points users to `max_concurrent_requests` under the affected provider in `config.toml`.

Retries:

- Let the OpenAI SDK handle its normal transport retries only for safe pre-stream failures.
- Add provider-level throttling around 429/5xx only if it does not duplicate SDK retries.
- Honor `Retry-After` exactly when present.
- Use exponential backoff with jitter for retryable transient failures.
- Stop after a small retry budget; the Codex subscription path defaults to six total pre-visible attempts to match Codex CLI reconnect behavior.
- Treat transient `response.failed` payloads such as `server_overloaded` like other retryable pre-visible failures, but keep subscription enforcement payloads terminal.

Do not retry:

- 400 request-shape errors;
- 401/403 auth/account/plan errors after one refresh attempt;
- quota, usage-limit, plan-limit, abuse, policy, or disabled-account responses, including 429 payloads such as `usage_limit_reached` or `usage_not_included`;
- streams that failed after partial content or tool calls.

Error translation:

- 401: request re-auth.
- 403: show account/workspace/plan/policy message.
- 429: surface the rate-limit/quota text and next retry time if present.
- 5xx/network: transient backend failure with retry status.
- malformed stream: provider protocol drift; include sanitized request id/session id.

Never automatically switch to another account, another provider, or an OpenAI API key.

## 15. LocalRuntime tool and safety rules

CodeAlta remains the harness.

Rules:

- Only expose CodeAlta-approved function tools from `LocalAgentTurnRequest.Tools`.
- Continue using existing command/file permission gates and sandbox/approval behavior.
- Do not expose Codex-native shell/apply-patch tools directly unless they are mapped through CodeAlta's own tool and approval model.
- If the model emits multiple tool calls, CodeAlta may execute them sequentially even when `parallel_tool_calls = true`.
- Tool results must be appended to LocalRuntime conversation history as existing Responses executor does.
- Encrypted reasoning content may be carried forward as protected data, but must never be logged or shown as normal text.

## 16. User experience

Before first use, show an explicit opt-in warning:

```text
This provider uses your ChatGPT/Codex subscription through ChatGPT authentication.
Requests may count against your ChatGPT/Codex plan limits. CodeAlta will not rotate
accounts, bypass rate limits, or fall back to another account automatically.
```

Required commands/UI flows:

- configure provider;
- login with ChatGPT browser flow;
- login with ChatGPT device-code flow for headless environments;
- list authenticated accounts/workspaces when token metadata supports it;
- select account/workspace;
- logout/delete CodeAlta-owned credentials;
- test authentication without sending a model turn;
- list models or show the static allow-list.

Provider status should clearly distinguish:

- not configured;
- login required;
- token expired but refreshable;
- account/workspace selection required;
- ready;
- rate-limited/quota-limited;
- unsupported backend/protocol drift.

## 17. Logging and diagnostics

Log these at debug/trace only:

- provider key;
- endpoint host/path without query secrets;
- model id;
- session id/run id;
- response id;
- HTTP status;
- retry count;
- sanitized error code/type.

Never log:

- access token;
- refresh token;
- id token;
- OAuth authorization code;
- PKCE verifier;
- raw JWT payload if it contains user PII;
- encrypted reasoning content;
- full request/response body by default.

Add a diagnostic mode that captures sanitized request shape for tests/support. It must redact secrets and omit conversation content unless explicitly enabled by the user for a local support bundle.

## 18. Tests

Add focused tests for CodeAlta-owned behavior. Do not duplicate OpenAI SDK serializer coverage except where CodeAlta patches fields.

### 18.1 Registration/config tests

- `codex` registers only when experimental flag is enabled.
- API-key fields are rejected.
- Default endpoint is `https://chatgpt.com/backend-api/codex`.
- Generic `extra_body` is rejected or ignored for this provider.
- `installation_id_source` defaults to `codealta_state`.
- Provider descriptor labels include ChatGPT/Codex subscription wording.

### 18.2 Auth tests

- OAuth authorize URL includes correct issuer, client id, redirect, scopes, PKCE S256, state, and `originator=codealta`.
- State mismatch is rejected.
- Token response is stored with expiry.
- Account id extraction works from imported token metadata and JWT claim metadata.
- Expired token triggers exactly one single-flight refresh for concurrent callers.
- Invalid refresh clears only CodeAlta-owned credentials and requests re-auth.
- Codex auth file import is read-only.
- Codex installation-id import reads `$CODEX_HOME/installation_id` only when configured, copies valid UUIDs in import mode, and never writes Codex-owned files in read-only mode.
- Tokens are redacted from logs/exceptions.

### 18.3 SDK client/header tests

Use a mock transport or capture pipeline policy.

Assert:

- request URL is `/backend-api/codex/responses`;
- `Authorization: Bearer <token>` is present at transport time;
- `ChatGPT-Account-Id` is present when account id exists;
- `originator=codealta` is present;
- `session_id` equals the LocalRuntime session id;
- `OpenAI-Beta: responses=experimental` is present if configured;
- `X-OpenAI-Fedramp: true` appears only for FedRAMP accounts;
- `x-codex-turn-state` is captured from HTTP/SSE and WebSocket handshake response headers and replayed only within the same turn/request chain;
- no API-key auth header is used.

### 18.4 Body tests

Capture serialized request body and assert:

- `store` is `false`;
- `stream` is `true`;
- `tool_choice` is `auto` when tools exist;
- `parallel_tool_calls` is true when tools exist;
- `include` contains `reasoning.encrypted_content` when enabled;
- `prompt_cache_key` equals the stable session/thread id;
- `text.verbosity` is present when configured;
- `client_metadata.x-codex-installation-id` is absent by default;
- when `send_installation_id = true`, `client_metadata.x-codex-installation-id` is stable across restarts and can come from CodeAlta state or an explicitly imported Codex installation id according to `installation_id_source`;
- arbitrary user `extra_body` is not applied.

### 18.5 Model tests

- Static model list returns capability metadata.
- Unknown model is rejected by default.
- Dynamic Codex endpoint is the default, calls `/codex/models?client_version=...`, and decodes Codex model shape.
- Dynamic model discovery does not call public `/models`.
- Dynamic discovery fallback behavior matches config.
- Static fallback catalog mirrors the Codex model catalog format and filters on `supported_in_api`.

### 18.6 Error/retry tests

- 401 triggers refresh once, then re-auth failure if still unauthorized.
- 403 surfaces account/plan/policy failure.
- transient 429 honors `Retry-After` and stops after budget.
- usage/quota/plan-limit 429 responses are terminal and not retried.
- 5xx transient errors retry within budget.
- Streaming error after partial text is not replayed.
- Quota/rate-limit text is surfaced without generic rewriting.

### 18.7 LocalRuntime behavior tests

- Tool calls still pass through CodeAlta's local tool bridge.
- Permission-gated tools are not executed without approval.
- Encrypted reasoning is preserved as protected data and omitted from logs.
- Session resume replays LocalRuntime conversation rather than relying on `PreviousResponseId`.

## 19. Implementation sequence

Implement in small, reviewable steps:

1. Add config/registration shell for `codex`, feature-flagged and disabled without auth.
2. Add CodeAlta-owned OAuth credential model, secure storage abstraction, redaction helpers, and auth manager tests.
3. Implement browser OAuth flow and device-code OAuth flow.
4. Add optional CodeAlta installation-id generation/storage under CodeAlta-owned state, plus explicit Codex installation-id import/read-only modes.
5. Add `ChatGptOAuthAuthenticationPolicy`, `CodexSubscriptionHeadersPolicy`, and `CodexTurnStatePolicy` with capture tests.
6. Add context-aware `ResponsesClientFactory` and request customizer hooks without changing existing OpenAI provider behavior.
7. Add Codex request customization for body/header/session semantics.
8. Add Codex `/models?client_version=...` discovery as the default path, plus cache/static fallback.
9. Add static fallback model catalog and model validation from Codex-provided model metadata.
10. Add retry/concurrency guardrails and error translation.
11. Add WebSocket transport with HTTP/SSE fallback and live-session-only `previous_response_id` continuation.
12. Add UI/CLI configure/login/logout/status flows and warnings.
13. Run targeted tests, then `dotnet build -c Release` and `dotnet test -c Release` from `src`.
14. Update `readme.md`, relevant `doc/**/*.md`, and `doc/development-guide.md` if development rules or config shape changed.

## 20. Acceptance criteria

The feature is complete when:

- A user can configure `codex` distinctly from public OpenAI providers.
- A user can authenticate with ChatGPT browser OAuth or device-code OAuth into CodeAlta-owned secure storage.
- A LocalRuntime turn reaches `https://chatgpt.com/backend-api/codex/responses` through WebSocket by default or through `ResponsesClient` HTTP/SSE when configured or after safe fallback, with OAuth auth and Codex headers.
- The request body contains Codex-required defaults and no arbitrary user `extra_body`.
- Model listing/validation defaults to authenticated Codex `/codex/models` and never calls public OpenAI `/models`.
- `x-codex-turn-state` response headers are replayed only inside the same turn/request chain.
- WebSocket fallback is explicit: `response_transport = "websocket_with_http_fallback"` is the default, and `response_transport = "http"` forces HTTP/SSE only.
- `previous_response_id` continuation is used only inside live in-memory CodeAlta sessions and is disabled for sessions reloaded from disk.
- Installation metadata is omitted by default, while an explicit compatibility option can send a stable CodeAlta-owned or imported Codex installation id.
- Rate-limit/account/auth failures are clear and do not trigger account rotation or unsafe fallback.
- CodeAlta tool execution and permissions remain fully local and unchanged.
- Tokens and encrypted reasoning are not logged or persisted in event history.
- Tests cover registration, auth, headers, body patching, model listing, retry/error handling, and LocalRuntime tool behavior.
- Existing `openai-responses` and `openai-chat` providers continue to behave as before.

## 21. Decisions from 2026-04-24 research

The previous open questions are resolved as implementation guidance:

- Static model allow-list: ship a Codex-provided static fallback catalog, generated from Codex model metadata at CodeAlta release time. The static list is not the default authority.
- Device-code login: support it in v1 alongside browser PKCE because it materially improves onboarding for headless and remote environments.
- Installation id: omit `client_metadata.x-codex-installation-id` by default. If the user enables it, use a generated stable per-CodeAlta-installation UUID stored under CodeAlta state. Do not send the literal `CodeAlta` as the installation id; use `CodeAlta` for product identity headers and labels. A prior Codex id from `$CODEX_HOME/installation_id` may be explicitly imported or read read-only when the user selects a Codex-backed import mode.
- Third-party client comparison: the inspected direct-access clients do not send `client_metadata.x-codex-installation-id`; official Codex does. CodeAlta v1 follows the minimal direct-access default by omitting it, while keeping an explicit option for official-Codex-style metadata.
- `OpenAI-Beta: responses=experimental`: keep enabled by default and set it with a typed SDK pipeline policy. The SDK supports this through arbitrary request-header policies even if the generated Responses types do not expose the header.
- `x-codex-turn-state`: implement header capture/replay with a custom `PipelinePolicy`. The OpenAI SDK's typed streaming enumeration does not expose headers directly, but the pipeline response headers are available to policies before the SSE body is consumed.
- Dynamic models: enable authenticated `/codex/models?client_version=...` by default with cache/static fallback. This avoids stale local model names while preserving offline/test behavior.
- WebSockets: default to WebSocket with HTTP/SSE fallback for ChatGPT subscription turns. Keep HTTP/SSE compatible and configurable because official clients fall back to it and some builds may use the NuGet OpenAI SDK without local WebSocket support.
