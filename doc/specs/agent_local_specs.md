# Local Agent Runtime Specification

Status: **Draft**  
Last updated: **2026-04-06**

Primary references:
- `C:\code\CodeAlta\src\CodeAlta.Agent\`
- `C:\code\CodeAlta\doc\specs\agent_api_specs.md`
- `C:\code\CodeAlta\doc\specs\agent_event_stream_unification.md`
- `C:\code\codex\codex-rs\app-server\README.md`
- `C:\code\codex\codex-rs\core\src\rollout\list.rs`
- `C:\code\codex\codex-rs\core\src\tools\handlers\`
- `C:\code\pi-mono\packages\ai\src\providers\openai-completions.ts`
- `C:\code\pi-mono\packages\ai\src\providers\openai-responses.ts`
- `C:\code\pi-mono\packages\ai\src\providers\anthropic.ts`
- `C:\code\pi-mono\packages\ai\src\providers\google-shared.ts`
- `C:\code\openai-dotnet\src\`
- `C:\code\anthropic-sdk-csharp\src\`
- `C:\code\dotnet-genai\Google.GenAI\`
- `~/.copilot/session-state/<session-id>/events.jsonl`
- `https://openai.com/en/index/unrolling-the-codex-agent-loop/`
- `https://openai.com/index/unlocking-the-codex-harness/`

## 1. Problem

OpenAI, Anthropic, and Google GenAI SDKs are raw APIs, not full agent runtimes. To make them feel like Codex or Copilot, CodeAlta must locally own:

- layered instructions
- session persistence
- tool orchestration
- approvals and user-input pauses
- event streaming
- resume/replay
- context compaction

This is therefore a **local runtime** design, not a thin remote adapter design.

## 2. Project structure

Keep the common infrastructure in `CodeAlta.Agent`.

Reason:

- the abstractions already live there
- `CodeAlta.Orchestration` already depends on it
- the missing pieces are part of the same agent runtime boundary

Provider/protocol-specific projects:

- `src/CodeAlta.Agent.OpenAI/`
- `src/CodeAlta.Agent.Anthropic/`
- `src/CodeAlta.Agent.GoogleGenAI/`

Use the respective public NuGet packages for API providers:

- `OpenAI`
- `Anthropic`
- `Google.GenAI`

## 3. Naming

Use "local agent runtime" or "raw-API agents", not "remote agents".

The hard part is the local harness: storage, replay, tools, and compaction.

## 4. Principles taken from Codex and Copilot

The Codex harness material and codebase point to the right model:

- the harness owns the agent loop
- tool outputs are appended back into subsequent model calls
- client-facing state is emitted as a normalized event stream
- context compaction is part of the harness

The local Copilot layout is also instructive:

- one session directory
- one `events.jsonl`
- metadata/checkpoint files beside it

CodeAlta should follow those principles.

## 5. Backend and provider identity

Backend ids remain protocol-oriented:

- `openai-chat`
- `openai-responses`
- `anthropic-messages`
- `google-genai`

Provider identity is separate and first-class:

- `ProtocolFamily`
- `ProviderKey`
- `DisplayName`
- `BaseUri`
- `TransportKind`
- capability/profile overrides

Do not encode provider aliases into backend ids.

Configured providers are loaded from `~/.codealta/config.toml` through `CodeAlta.Catalog.CodeAltaConfigStore`, under:

- `[raw_api.openai.providers.<provider-key>]`
- `[raw_api.anthropic.providers.<provider-key>]`
- `[raw_api.google_genai.providers.<provider-key>]`

Runtime registration is conditional:

- OpenAI-compatible providers require a resolved API key and may enable `openai-responses`, `openai-chat`, or both
- Anthropic providers require a resolved API key
- Google GenAI providers require either an API key or a valid Vertex configuration with `use_vertex_ai = true`, `project`, and `location`

Registration is application-owned, currently through `CodeAlta.App.RawApiBackendRegistrar`, and only usable configured providers should surface as selectable backends.

## 6. Local storage root

Store local runtime state under:

- `~/.codealta/local/agents/`

This is machine-scoped operational state, even if the models are remote.

## 7. Filesystem layout

Use a provider-first layout so all data for one configured provider lives together:

```text
~/.codealta/
  machine/
    agents/
      <protocol-family>/
        <provider-key>/
          provider.json
          sessions/
            YYYY/
              MM/
                DD/
                  <local-session-id>/
                    session.json
                    events.jsonl
                    state.json
                    attachments/
```

This is preferred over separate global `providers/` and `sessions/` trees.

## 8. Session files

`session.json`
- lightweight summary for listing
- backend id, provider key, model, title, timestamps, latest usage

`events.jsonl`
- the only canonical session log
- stored as normalized `AgentEvent` items
- contains replay-significant finalized events, not duplicate streaming-only deltas
- used for both UI playback and replay/resume

`state.json`
- local runtime state not modeled as user-facing events
- latest provider-specific replay hints
- compaction cursor
- instruction hash and other resume metadata

Use `state.json`, not `provider-state.json`.

## 9. One canonical event log

Do not maintain both `conversation.jsonl` and `events.jsonl`.

The canonical durable transcript is a single `events.jsonl`. To make that viable, the normalized event model must preserve replay-critical final data, including provider extension bags where needed for:

- OpenAI Responses item ids
- Anthropic thinking signatures
- Google `thoughtSignature`
- provider-native tool call ids

Replay rule:

- delta/progress events are diagnostic
- finalized canonical events are replay-significant
- duplicate deltas that are superseded by a later finalized content event should not be written to `events.jsonl`
- if a backend streams deltas but does not provide a finalized content event, the runtime should synthesize one finalized canonical event instead of relying on durable deltas for replay
- resume rebuilds context from the last compaction point plus replay-significant events

The authoritative session transcript remains event-based even when provider SDKs expose richer in-memory item graphs. Provider-specific replay hints may still be cached in `state.json`, but the conversation must remain resumable from the normalized event log plus compaction state.

## 10. Relationship to SQLite

`CodeAlta.Persistence.AgentRepository` remains the lightweight index and mapping layer.

Recommended split:

- SQLite for registration, active mapping, and fast lookup
- filesystem for authoritative session state

## 11. Local runtime responsibilities

The shared runtime in `CodeAlta.Agent` must own:

- instruction composition
- event persistence
- replay/resume
- tool advertisement and dispatch
- approvals and user input
- usage tracking
- context compaction

Provider adapters should only own:

- request serialization
- stream decoding
- provider-specific compatibility rules

## 12. Instruction composition

Instruction composition is always local and authoritative.

Inputs may include:

- system instructions
- developer instructions
- user input
- project instructions
- `AGENTS.md`
- thread-level templates

If a provider does not support the `developer` role, the adapter deterministically folds that content into the supported instruction surface.

## 13. Local agent loop

Each run should follow this shape:

1. Load `session.json`, `events.jsonl`, and `state.json`.
2. Reconstruct context from the last compaction point and replay-significant events.
3. Compose the next provider request locally.
4. Stream provider output into normalized `AgentEvent`s for live subscribers.
5. Canonicalize streamed output into finalized replay-significant events for `events.jsonl`.
6. Execute local tools when tool calls arrive.
7. Append tool results as canonical events and feed them into the next provider call.
8. Persist finalized outputs and updated `state.json`.
9. Compact old context when needed.

This is the same broad model described by OpenAI for the Codex harness.

## 14. Context compaction

Compaction is a required design feature, not a later optimization.

The runtime should support:

- a compaction event in `events.jsonl`
- a compaction cursor in `state.json`
- replay from compaction summary plus later events

Compaction must operate over canonical replay-significant content only. Streaming deltas that merely duplicate finalized assistant, reasoning, or tool-output content must not be part of the compaction input.

## 15. Provider capability profile

Each configured provider resolves to an explicit profile object. It should cover at least:

- developer-role support
- `store` support
- reasoning-effort support
- streaming-usage behavior
- `max_tokens` field naming
- reasoning field shape
- tool replay rules
- cache-control support
- thought-signature support

Do not scatter provider quirks through ad-hoc conditionals.

The currently implemented profile override surface is:

- `supports_developer_role`
- `supports_store`
- `supports_reasoning_effort`
- `streams_usage`
- `supports_thought_signatures`
- `max_tokens_field_name`
- `reasoning_field_names`

## 16. OpenAI-compatible Chat/Completions

Rules:

- full local replay is required
- provider profiles handle `developer`, `store`, reasoning, token field naming, and tool replay quirks
- this backend must stay profile-driven because OpenAI-compatible providers are not uniform

## 17. OpenAI Responses

Rules:

- local state is authoritative
- do not base continuation on `previous_response_id`
- build each new request from local events

Reason:

- better compatibility across providers
- works with zero data retention expectations
- avoids coupling the session model to provider-side history retention

Response ids may still be stored in `state.json` for diagnostics only.

In the current implementation, CodeAlta explicitly sets `PreviousResponseId = null` and reconstructs each request locally from normalized events and tool results. That behavior is intentional and should remain the baseline.

## 18. Anthropic and Google

Anthropic:

- preserve block structure
- preserve thinking signatures
- preserve tool-use ids and tool-result pairing

Google:

- preserve part-level structure
- preserve `thought = true`
- preserve `thoughtSignature`
- distinguish Gemini Developer API from Vertex AI in the capability profile

## 19. Default built-in tools

CodeAlta should ship a stronger default baseline than `read/write/edit/bash`.

Recommended baseline:

- `read_file`
- `list_dir`
- `grep`
- `webget`
- `shell_command`
- `apply_patch`
- `view_image`
- `request_user_input`

Optional later additions:

- MCP bridge tools
- artifact/file bundle tools
- richer search/index tools
- planning/checklist tools

`update_plan` is intentionally not part of the default built-in baseline at this stage.

## 20. Tool design rules

`shell_command`, not `bash`
- must be cross-platform
- should use the session's configured shell
- should work on Windows, Linux, and macOS
- should stream stdout/stderr progress into tool-output deltas while the command is still running

`webget` for direct retrieval, not only search
- should fetch and return webpage content in a model-friendly text form
- should be usable for documentation pages, raw text resources, and simple HTTP retrieval
- it is distinct from search because the agent often already has the target URL
- it should support basic safeguards such as size limits, content-type checks, and timeout controls

`apply_patch`, not `write` plus `edit` as the primary editing primitive
- add/update/delete/move in one tool
- better auditability and diff rendering
- better fit for approvals and coding workflows

Every tool call and tool result must round-trip through `events.jsonl`.

## 21. Current implementation mapping

The current implementation maps this design through:

- shared runtime infrastructure in `src/CodeAlta.Agent/LocalRuntime/`
- provider adapters in `src/CodeAlta.Agent.OpenAI/`, `src/CodeAlta.Agent.Anthropic/`, and `src/CodeAlta.Agent.GoogleGenAI/`
- provider configuration in `src/CodeAlta.Catalog/CodeAltaRawApiSettingsDocument.cs`
- provider loading and normalization in `src/CodeAlta.Catalog/CodeAltaConfigStore.cs`
- application registration in `src/CodeAlta/App/RawApiBackendRegistrar.cs`
- composition-root wiring in `src/CodeAlta/App/CodeAltaOwnedServices.cs`

The application uses this machine-scoped state root:

- `~/.codealta/local/agents/`

Within that root, sessions are stored provider-first under:

- `~/.codealta/local/agents/<protocol-family>/<provider-key>/sessions/...`

## 22. Concrete decisions

1. Rename the spec to `agent_local_specs.md`.
2. Keep the common infrastructure in `CodeAlta.Agent`.
3. Use provider-first storage under `~/.codealta/local/agents/<protocol-family>/<provider-key>/`.
4. Use one canonical `events.jsonl` per session, containing finalized replay-significant events rather than duplicate deltas.
5. Use `state.json`, not `provider-state.json`.
6. Do not use `previous_response_id` as the session continuation model.
7. Build the runtime as a local harness in the same spirit as Codex.
8. Ship meaningful built-in tools by default.
9. Use `shell_command` as the cross-platform exec primitive.
10. Prefer `apply_patch` as the primary edit primitive.

## 23. Summary

These providers should be integrated as **local-agent runtimes backed by raw APIs**, not as thin remote adapters.

CodeAlta should own:

- one local canonical event log
- one provider-first storage layout
- one shared tool loop
- one compaction model
- one replay/resume model

The provider adapters should stay thin and focus on wire-format differences and compatibility profiles.

