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
- OpenAI .NET SDK NuGet package
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
- `anthropic`
- `google-genai`
- `vertex-ai`

Provider identity is separate and first-class:

- `ProtocolFamily`
- `ProviderKey`
- `DisplayName`
- `ApiUrl`
- `TransportKind`
- capability/profile overrides

Do not encode provider aliases into backend ids.

Configured providers are loaded from `~/.alta/config.toml` through `CodeAlta.Catalog.CodeAltaConfigStore`, under:

- `[providers.<provider-key>]`

Each configured entry describes one endpoint registration. Key fields include:

- `type = "codex" | "copilot" | "openai-chat" | "openai-responses" | "anthropic" | "google-genai" | "vertex-ai"`
- shared endpoint/auth fields such as `display_name`, `api_key`, `api_key_env`, and `api_url`
- provider-owned defaults such as `model` and `reasoning_effort`
- cross-provider extras such as `single_model_id` for single-model endpoints that cannot list models dynamically, and OpenAI-compatible `extra_body` for provider-specific request-body fields
- provider-specific fields such as `organization_id`, `project_id`, `project`, and `location`

Default provider selection lives under `[chat].default_provider`.

Runtime registration is conditional:

- OpenAI-compatible providers require a resolved API key and map directly from `type = "openai-chat"` or `type = "openai-responses"`
- Anthropic providers require a resolved API key
- Google GenAI providers require an API key
- Vertex providers require `project` and `location`

Registration is application-owned, currently through `CodeAlta.App.RawApiBackendRegistrar`, and only usable configured providers should surface as selectable backends.

## 6. Local storage root

Store local runtime state under:

- `~/.alta/`

This is machine-scoped operational state, even if the models are remote. Provider definitions live in `config.toml`; local runtime sessions persist as journals under `~/.alta/sessions/`.

## 7. Filesystem layout

Use a shared date-sharded session-journal layout:

```text
~/.alta/
  config.toml
  sessions/
    YYYY/
      MM/
        DD/
          <local-session-id>.jsonl
```

Do not persist provider descriptors under a separate `providers/` tree. Provider definitions come from `config.toml`; provider/model switches are recovered from the session journal.

## 8. Session files

`<local-session-id>.jsonl`
- the only canonical local-runtime session log
- stores normalized `AgentEvent` items alongside internal snapshot events used to recover the latest summary/state
- contains replay-significant finalized events, not duplicate streaming-only deltas
- captures provider/model switch events so the latest active provider can be reconstructed without auxiliary files

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
- explicit runtime context such as current date, platform/shell, current working directory, and project roots
- user input
- project instructions
- the largest matching local instruction file per directory among `AGENTS.md`, `CLAUDE.md`, and `.github/copilot-instructions.md`
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
- `write_file`
- `replace_in_file`
- `rename_file_or_dir`
- `delete_file_or_dir`

Official OpenAI providers (`https://api.openai.com/`, matched on base URL/host rather than full path) and the Codex ChatGPT subscription provider should additionally expose:

- `apply_patch`

Optional later additions:

- `request_user_input` once the host supports structured UI feedback
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
- on Windows it should invoke `pwsh -NoProfile -Command ...` to avoid profile-time prompt theming or ANSI noise contaminating tool output
- on Windows it should preserve the failing final external command's native exit code rather than collapsing it to `1` when PowerShell can report the original code
- login-shell semantics should remain Unix-oriented and only apply on shells that support them
- should stream stdout/stderr progress into tool-output deltas while the command is still running
- stdout/stderr should be returned as raw text; child processes may still emit ANSI/control sequences even when the host requests no-color behavior
- if a future direct-exec tool is added for structured argv/no-shell semantics, it should be additive rather than replacing `shell_command`

`webget` for direct retrieval, not only search
- should fetch and return webpage content in a model-friendly text form
- HTML responses may be simplified to plain text instead of returning raw markup, as long as that is documented clearly
- it may offer an explicit raw-HTML escape hatch when markup is actually needed, but plain-text extraction should remain the default
- on success it should clearly document that the default payload is the fetched body text, and any optional HTTP status/metadata prefix should be opt-in to avoid breaking body-only workflows
- should be usable for documentation pages, raw text resources, and simple HTTP retrieval
- it is distinct from search because the agent often already has the target URL
- it should support basic safeguards such as size limits, content-type checks, and timeout controls
- it should clearly document that it returns response body text rather than raw HTTP metadata
- it should clearly document supported schemes/content types and the configured size and timeout limits
- network failures, HTTP errors, and timeouts should come back as normal tool results with clear error text rather than breaking the tool-call round trip

Deterministic edit tools for all providers
- `write_file` should replace a file's full contents in one call
- `replace_in_file` should perform exact-string replacement only, with no regex and no fuzzy matching
- `replace_in_file` should be explicit that `replace_all=false` errors unless exactly one match exists, and that this failure leaves the file unchanged
- `rename_file_or_dir` should rename or move files and directories
- `rename_file_or_dir` should be explicit that it will not overwrite an existing destination
- `delete_file_or_dir` should delete files or directories recursively
- these tools should stay easy to describe, easy to call, and return clear failure messages
- path-like tool arguments should resolve relative paths from the session working directory while still accepting absolute paths unchanged; they should not reject `..` paths solely because they target files outside the session working directory
- `read_file` should support negative line offsets to read from the end of a text file (`-1` = last line)
- `read_file` offsets are 1-based, so `0` should be rejected explicitly rather than silently acting like an empty range
- `read_file` should document that offsets past EOF return an empty text result rather than an error
- `grep` should document its no-match sentinel if it returns a literal string such as `(no matches)` instead of an empty array/object
- `read_file` descriptions should document the default line limit used when `limit` is omitted
- `list_dir` should fail clearly when the target path exists but is not a directory
- `list_dir` should use an explicit empty-directory marker instead of returning a visually blank result
- `grep` should document that matching is case-insensitive by default and that likely-binary/image files are skipped
- text-oriented tools such as `read_file`, `replace_in_file`, `grep`, and `apply_patch` should reject or skip likely-binary files instead of dumping binary content into the conversation

`apply_patch` for official OpenAI/Codex providers
- keep `apply_patch` available only when the provider is actually targeting `https://api.openai.com/` or using the `openai-codex-subscription` protocol family
- add/update/delete/move in one tool
- better auditability and diff rendering
- better fit for approvals and coding workflows
- should be forgiving for normal LLM output, especially around hunk anchors, blank context lines, and light whitespace drift
- should support intuitive rename-only edits via `Update File` + `Move to` without forcing dummy hunks
- keep the description concise and close to Codex/OpenAI expectations rather than over-explaining the tool

Recommended `apply_patch` grammar and guidance:

```text
*** Begin Patch
*** Add File: relative/path.txt
+new file content
*** Update File: relative/path.txt
*** Move to: new/path.txt
@@ optional anchor text
 unchanged context
-old line
+new line
*** End of File
*** Delete File: obsolete.txt
*** End Patch
```

- A new agent should be able to succeed by copying that template verbatim and filling in the paths and hunk lines.
- Paths resolve relative to the session working directory unless absolute, and may use `..` to target sibling or parent directories.
- Use `@@` or `@@ anchor text` before each changed region.
- Inside hunks, use:
  - space-prefixed lines for unchanged context
  - `-` for removals
  - `+` for additions
- Anchor indentation should be matched with light whitespace tolerance so agents do not need to reason about exact leading spaces.
- Blank lines inside hunks should be accepted as blank context lines to reduce model friction.
- `*** End of File` should bias matching toward EOF when a repeated context block exists.

Every tool call and tool result must round-trip through `events.jsonl`.
Tool execution failures should be converted into structured failed tool results whenever possible so provider conversations do not end up with orphaned tool calls.

## 21. Current implementation mapping

The current implementation maps this design through:

- shared runtime infrastructure in `src/CodeAlta.Agent/LocalRuntime/`
- provider adapters in `src/CodeAlta.Agent.OpenAI/`, `src/CodeAlta.Agent.Anthropic/`, and `src/CodeAlta.Agent.GoogleGenAI/`
- provider configuration in `src/CodeAlta.Catalog/CodeAltaRawApiSettingsDocument.cs`
- provider loading and normalization in `src/CodeAlta.Catalog/CodeAltaConfigStore.cs`
- application registration in `src/CodeAlta/App/RawApiBackendRegistrar.cs`
- composition-root wiring in `src/CodeAlta/App/CodeAltaOwnedServices.cs`

The application uses this machine-scoped state root:

- `~/.alta/cache/agents/`

Within that root, sessions are stored provider-first under:

- `~/.alta/cache/agents/<protocol-family>/<provider-key>/sessions/...`

## 22. Concrete decisions

1. Rename the spec to `agent_local_specs.md`.
2. Keep the common infrastructure in `CodeAlta.Agent`.
3. Use provider-first storage under `~/.alta/cache/agents/<protocol-family>/<provider-key>/`.
4. Use one canonical `events.jsonl` per session, containing finalized replay-significant events rather than duplicate deltas.
5. Use `state.json`, not `provider-state.json`.
6. Do not use `previous_response_id` as the session continuation model.
7. Build the runtime as a local harness in the same spirit as Codex.
8. Ship meaningful built-in tools by default.
9. Use `shell_command` as the cross-platform exec primitive.
10. Prefer `apply_patch` for official OpenAI providers, and deterministic edit tools for other providers.

## 23. Summary

These providers should be integrated as **local-agent runtimes backed by raw APIs**, not as thin remote adapters.

CodeAlta should own:

- one local canonical event log
- one provider-first storage layout
- one shared tool loop
- one compaction model
- one replay/resume model

The provider adapters should stay thin and focus on wire-format differences and compatibility profiles.


