# Codex protocol parity plan

- Status: Approved
- Plan file: `.alta/plans/2026-07-10-codex-protocol-parity.md`
- Created: 2026-07-10
- Task: Bring CodeAlta's ChatGPT/Codex subscription transport, lifecycle, model, metadata, reasoning, and request behavior into staged parity with Codex `6138909d6e`.
- Git: `.alta/plans/` is not ignored; commit this plan with the related implementation. The worktree is on `main`, ahead of `origin/main` by one commit, with pre-existing untracked `.alta/config.toml`, `.alta/mcp.json`, and a diagnostic archive that must remain untouched.

## Objective

- Correct the confirmed high- and medium-impact Codex subscription gaps without changing ordinary OpenAI-compatible Responses behavior.
- Keep generic Responses HTTP/SSE on OpenAI .NET SDK 2.12.0. Add a Codex-only protocol layer where CodeAlta must observe initial headers and Codex extensions, while continuing to use SDK models/deserialization for standard `response.*` event bodies.
- Deliver the work as reviewable stages with regression tests before each behavior change.
- Do not add live ChatGPT backend calls, account rotation, provider switching, zstd request compression, arbitrary raw-header persistence, or new proxy/TLS configuration in this parity pass.

## Context and evidence

- `src/Directory.Packages.props:25` pins OpenAI .NET 2.12.0. Generic HTTP streaming currently delegates to the SDK in `OpenAIResponsesTurnExecutor.cs:507-548`, while CodeAlta owns lifecycle and reconstruction in `OpenAIResponsesTurnExecutor.cs:133-334`.
- HTTP completion/incomplete events currently set terminal state but continue enumeration (`OpenAIResponsesTurnExecutor.cs:249-260`); Codex stops on completion (`codex-rs/codex-api/src/sse/responses.rs:580-588`).
- CodeAlta reconstructs terminal output only when terminal `output` is empty (`OpenAIResponsesTurnExecutor.cs:1055-1110`), whereas Codex finalizes durable items from indexed `response.output_item.done` events.
- `bio_policy` is absent from CodeAlta's fatal error codes (`OpenAIResponsesTurnExecutor.cs:2449-2462`) but is non-retryable in Codex (`codex-rs/codex-api/src/sse/responses.rs:386-415`).
- CodeAlta discovery reads singular `supports_reasoning_summary`, does not retain `supports_parallel_tool_calls` or `use_responses_lite`, and defaults several absent fields to true (`CodexSubscriptionModelDiscoveryClient.cs:188-218`). Current Codex declares plural `supports_reasoning_summaries`, `support_verbosity`, `supports_parallel_tool_calls`, and `use_responses_lite` (`codex-rs/protocol/src/openai_models.rs:352-420`). GPT-5.6 Sol/Terra/Luna are Lite models in `codex-rs/models-manager/models.json`.
- CodeAlta always sends standard Responses tools/instructions and forces parallel calls/verbosity (`OpenAIResponsesTurnExecutor.cs:1113-1233`). Codex Lite moves tools and base instructions into developer input items and omits top-level tools/instructions (`codex-rs/core/src/client.rs:830-895`).
- WebSocket turn state is constructor/handshake-scoped (`OpenAICodexSubscriptionWebSocketSession.cs:228-275,437-445`) even though sockets are cached per session and state is keyed per run (`OpenAIResponsesTurnExecutor.cs:551-700`). Current Codex sends turn state in each WebSocket `response.create.client_metadata` and captures it from `response.metadata`.
- HTTP captures only `x-codex-turn-state` (`CodexSubscriptionHeadersPolicy.cs:89-95`). Codex also consumes request id, effective model, models ETag, reasoning inclusion, named rate limits, and safety-buffering treatment headers before body events (`codex-rs/codex-api/src/sse/responses.rs:28-99`).
- WebSocket metadata is reduced to a bounded persisted provider-state summary (`OpenAIResponsesTurnExecutor.cs:1560-1779`), and top-level `safety_buffering` is not recognized by `OpenAICodexSubscriptionWebSocketSession.cs:651-670`.
- SDK 2.12.0 exposes reasoning summary part/done event types, but the executor handles only summary/raw text deltas (`OpenAIResponsesTurnExecutor.cs:195-227`). Codex's sequential-cutoff mode uses atomic `response.reasoning_summary_text.done` keyed by `item_id` and `summary_index` (`codex-rs/codex-api/src/sse/responses.rs:354-370`; `codex-rs/core/src/session/turn.rs:2357-2432`).
- `response.completed.response.end_turn` is not consumed. Codex treats only explicit `false` as a request for another inference cycle (`codex-rs/core/src/session/turn.rs:2279-2305`).
- CodeAlta still defaults `OpenAI-Beta: responses=experimental`, sends `session_id`, strips WebSocket query strings, and gives discovery a one-shot request (`OpenAIModelProviderRuntimeOptions.cs:249-257`; `CodexSubscriptionHeadersPolicy.cs:64-86`; `OpenAICodexSubscriptionWebSocketSession.cs:105-119`; `CodexSubscriptionModelDiscoveryClient.cs:32-82`).

## Assumptions and open decisions

- The scope is the full confirmed parity set, implemented in stages. Each stage should leave the repository buildable and may be committed separately.
- Codex-specific strictness applies only when `provider.CodexSubscription` is set. Generic OpenAI-compatible Responses providers retain text-only EOF reconstruction; they will newly reject EOF after an observed tool-call item because committing an unterminated tool call is unsafe.
- Preserve the existing WebSocket `response.done` normalization as an explicitly tested legacy compatibility alias; it is not a current Codex SSE requirement and must still obey first-terminal-event semantics.
- Sequential-cutoff events will be supported before negotiation is enabled. Because Codex marks `concurrent_reasoning_summaries` under development and disabled by default, CodeAlta will not send `stream_options.reasoning_summary_delivery="sequential_cutoff"` by default in this pass; add an internal/test seam so it can be enabled later without reworking the reducer.
- Map CodeAlta `SessionId` to Codex session/thread compatibility identities and `RunId` to turn identity. Populate only metadata CodeAlta knows authoritatively; defer window, fork, subagent, sandbox, and git lineage until CodeAlta has explicit contracts for them.
- Keep `send_installation_id` opt-in for privacy compatibility. The immutable request context includes installation identity only when enabled.
- Keep existing public capability keys and add exact stable keys; do not remove or reinterpret serialized `AgentModelInfo.Capabilities` entries.
- No unresolved decision blocks implementation.
- The user approved this plan on 2026-07-10 and requested execution with the current model at medium reasoning effort.

## Design notes

- Introduce an internal Codex protocol event envelope with the original event type, transport, optional SDK `StreamingResponseUpdate`, and typed/allowlisted Codex metadata. Parse `headers`, `metadata`, `response`, `item`, and `safety_buffering` only; never retain arbitrary raw extensions or authorization/cookie headers.
- Use a Codex-only HTTP stream session built on the existing configured `HttpClient`, `System.Net.ServerSentEvents.SseParser`, and SDK `ModelReaderWriter.Read<StreamingResponseUpdate>` for standard event JSON. Generic Responses providers remain on `ResponsesClient.CreateResponseStreamingAsync`.
- Process initial HTTP metadata before body events and per-frame Codex metadata before the frame's standard lifecycle update. Represent `retry_model` with a presence bit plus nullable value so omission falls back to the treatment header while explicit `null` suppresses fallback.
- Keep the logical reducer transport-neutral: first terminal event wins; completed succeeds; incomplete/failed error immediately; EOF is strict for Codex; indexed done items override terminal items at the same output index and fill partial terminal output.
- Build one immutable Codex request context per executor call and reuse it across retries/fallback. Derive HTTP headers, WebSocket request metadata, and compatibility metadata from that source; protect reserved keys from later patches.
- Keep physical WebSockets session-scoped but pass logical turn context/state into every send/reconnect. Remove run-specific turn state from the socket constructor and handshake.
- Responses Lite is a code-backed model dialect, not a header toggle: prefix `additional_tools` and developer instruction input items, strip image detail, omit top-level tools/instructions, set reasoning context to all turns, and leave non-Lite/OpenAI payloads unchanged.
- Project only sanitized metadata: rate limits into usage; effective model as `ModelChanged`; verification/safety/moderation as transient structured session updates; selected request/model/ETag/reasoning fields into bounded provider state. Raw moderation and full headers must not be persisted.
- Add an explicit follow-up flag to `AgentTurnResponse`; `AgentSession` continues sampling on `end_turn:false` after appending durable output, without idling or requiring a tool call. Preserve current terminal-output validation for true/absent `end_turn`, but allow an empty intermediate completion when explicit false requires follow-up.
- Retain the `send_responses_beta_header` config key as an opt-in compatibility switch, change its default to false, and stop applying it to `/models`.

## Risks and challenges

- A Codex-only HTTP adapter replaces SDK-owned HTTP framing for this provider. It must preserve cancellation, response disposal, timeout/error detail, protocol tracing, auth refresh, and configured `HttpClient` behavior; tests must prove generic SDK paths are unchanged.
- Responses Lite changes request semantics materially. Gate it only from exact model metadata/static catalog flags and use full request snapshots for Lite and non-Lite models.
- Public additive changes (`AgentTurnResponse` follow-up and optional live usage on `AgentTurnSessionUpdate`) require XML documentation, serialization review, and runtime tests.
- Safety/moderation metadata may be sensitive. Keep it transient, size-capped, allowlisted, and absent from durable provider state/traces unless the existing redactor explicitly sanitizes it.
- Flipping the beta-header default changes behavior for existing configs that omitted the key. Preserve explicit `true`, update config round-trip tests, and document the compatibility switch.
- Socket reuse and retry races can associate metadata with the wrong run unless per-send context is immutable and callback ownership remains inside the existing stream semaphore.
- Codex is a moving reference. Record `6138909d6e` in tests/docs and avoid speculative validation of unknown events, sequence numbers, summary index gaps, or response IDs.

## Implementation checklist

### 1. Establish the Codex protocol boundary

- [x] Add internal Codex protocol contracts under `src/CodeAlta.Agent.OpenAI/Codex/` for transport, original event type, SDK update, terminal metadata, selected response metadata, safety buffering (including retry-model presence), and named rate-limit snapshots; keep all contracts internal and immutable.
- [x] Add parser tests in `OpenAICodexSubscriptionPipelineTests.cs` for known raw fields, unknown/malformed events, metadata-before-update ordering, omission versus explicit-null `retry_model`, and proof that authorization/cookie/arbitrary fields are discarded.
- [x] Add `CodexSubscriptionHttpStreamSession` (or equivalently narrow name) using the configured subscription `HttpClient`, `SseParser`, and SDK model deserialization; preserve auth, account/FedRAMP identity, cancellation, network timeout, error status/body/headers, and protocol tracing without adding a package.
- [x] Refactor `OpenAICodexSubscriptionWebSocketSession` to emit the same internal envelope while retaining JSON frame handling, idle timeout, error-envelope translation, and the explicitly labeled legacy `response.done` alias.
- [x] Route only `provider.CodexSubscription` HTTP/WebSocket streams through the envelope in `OpenAIResponsesTurnExecutor`; keep ordinary Responses providers on the current SDK enumeration path.

### 2. Correct lifecycle, error, and reconstruction semantics

- [x] Add regressions in `OpenAIRawApiModelProviderRuntimeTests.cs` for `bio_policy`, completion while the underlying async transport remains pending, first-terminal-event wins, incomplete/failed immediate failure, and Codex EOF before a terminal event.
- [x] Add `bio_policy` to fatal/non-retryable policy errors and use structured error code—not message text alone—for context-overflow classification where available.
- [x] Stop Codex HTTP/WebSocket reduction immediately on the first logical terminal event; completed succeeds, while incomplete/failed/top-level errors fail without consuming later frames.
- [x] Make indexed `response.output_item.done` authoritative at each output index, merge it with any terminal-only indexes, and add tests for empty and partially populated terminal `output` arrays, multiple indexes, and duplicate-index replacement.
- [x] Preserve generic text-only EOF reconstruction, but reject generic EOF after any observed tool call; add a regression proving no unterminated tool call is dispatched and existing generic text reconstruction still passes.
- [x] Keep unknown/malformed nonterminal events tolerant and unknown failed codes retryable, matching Codex's forward-compatible parser posture.

### 3. Parse and apply exact model capabilities

- [x] Update `CodexSubscriptionDiscoveredModel` and `CodexSubscriptionModelDiscoveryClient` to read exact current fields and types: `supports_reasoning_summaries`, `support_verbosity`, `supports_parallel_tool_calls`, `supports_image_detail_original`, and `use_responses_lite`; preserve tolerant unknown properties and explicit legacy aliases only where existing fixtures require them.
- [x] Add current Codex `/models` fixture subsets for Lite GPT-5.6 and non-Lite models, including explicit false values, missing values, wrong types, reasoning efforts, and ETag propagation.
- [x] Add stable capability keys in `OpenAIProviderSdkFactory.CreateModelInfo` and the static catalog without removing current keys; mark GPT-5.6 Sol/Terra/Luna as Lite and align their relevant advertised capabilities with the reference fixture.
- [x] Centralize internal capability lookup so request construction omits reasoning summaries/verbosity when unsupported and sets parallel-tool-call behavior from model metadata instead of forcing true.
- [x] Preserve non-Codex and models lacking trusted metadata on their current conservative request path; cover manual/static selection and discovered selection separately.

### 4. Create canonical per-request metadata and fix turn-state ownership

- [x] Add an immutable `CodexSubscriptionRequestContext` built once per executor call from `SessionId`, `RunId`, request kind, start timestamp, optional installation id, and current logical turn state; reuse the same snapshot across retries and WebSocket-to-HTTP fallback.
- [x] Generate `client_metadata` and compatibility headers from that context, map session/thread to `SessionId` and turn to `RunId`, and reject/overwrite conflicting reserved metadata patches deterministically.
- [x] Remove `CodexTurnState` from the cached WebSocket session constructor; pass the current per-run state into every `response.create` and stale reconnect request.
- [x] Send WebSocket turn state as `client_metadata["x-codex-turn-state"]`, stop sending it on the upgrade handshake, and capture the first valid returned state from `response.metadata` for the active send.
- [x] Retain HTTP turn-state header behavior, and add cross-transport tests for same-run retry reuse, same-turn continuation, socket reuse across runs, stale reconnect, and first-valid-value-wins capture.

### 5. Implement Responses Lite and reasoning event parity

- [x] Add request snapshot tests for GPT-5.6 Sol/Terra/Luna and a non-Lite model before changing payload construction; assert input order, tool schema, instructions, image detail, reasoning context, top-level omissions, metadata, and HTTP/WebSocket equivalence.
- [x] Implement the Lite transformation in a narrow Codex request builder: prepend developer `additional_tools`, then developer instructions when nonempty; strip image detail; omit top-level tools/instructions; preserve conversation/reasoning/tool-result order; and set reasoning context to all turns.
- [x] Leave standard Codex and generic OpenAI request creation byte-shape compatible except for independently planned capability/header corrections.
- [x] Handle reasoning summary part added/done, `response.reasoning_summary_text.done`, and `response.reasoning_text.done`; correlate summary completion by `item_id` and `summary_index`, and retain final encrypted reasoning/summary parts from `output_item.done`.
- [x] Add sequential-cutoff reducer tests for done-only sections, interleaved item IDs, ordinary legacy deltas, duplicate/stale/gapped indexes, and no visible duplication; do not add ordering or gap validation that Codex itself does not enforce.
- [x] Add an internal/test-only request switch for `stream_options.reasoning_summary_delivery="sequential_cutoff"`, default it off, and verify the reducer works before any future user-facing enablement.

### 6. Surface selected metadata and honor `end_turn`

- [x] Parse and normalize allowlisted initial/event metadata: `x-request-id`, `OpenAI-Model`, `x-models-etag`, `x-reasoning-included`, all named `x-*-primary/secondary-*` rate-limit families, credits, verification recommendations, turn moderation, and safety buffering.
- [x] Extend `AgentTurnSessionUpdate` with optional usage and pass it through `AgentSession.OnSessionUpdateAsync`; emit transient `UsageUpdated`, `ModelChanged`, `Info`, or `Warning` updates with stable, size-capped details for rate limits, reroutes, safety buffering, verification, and moderation.
- [x] Extend Codex usage details additively to retain named rate-limit snapshots while preserving the current singular/default projection; update JSON source generation, usage aggregation/presentation, and serialization tests.
- [x] Merge the latest rate-limit/effective-model data into final `AgentTurnResponse.Usage` and bounded provider state, but explicitly exclude raw headers, raw moderation blobs, turn state, credentials, and unrecognized metadata from persistence.
- [x] Add `RequiresProviderFollowUp` (or equivalently explicit name) to `AgentTurnResponse`, set it only for `end_turn:false`, and update `AgentSession` to continue inference without idling when there are no tool calls; avoid appending an empty intermediate assistant message.
- [x] Add tests for `end_turn` false/true/absent, follow-up with and without durable output, usage persistence, metadata event ordering, safety retry-model precedence, effective-model mismatch, and proof that sensitive/raw metadata is not serialized into session state.

### 7. Align headers, transport construction, discovery, config, and docs

- [ ] Centralize subscription HTTP/discovery identity helpers so endpoint resolution, account/FedRAMP auth, truthful `version`, and one CodeAlta User-Agent shape stay consistent; continue honoring supplied `provider.HttpClient` behavior.
- [ ] Remove underscored `session_id`, retain hyphenated `session-id`/`thread-id` and `x-client-request-id`, preserve configured query parameters when deriving the WebSocket URI, and send `Accept: text/event-stream` on Codex HTTP turns.
- [ ] Change `send_responses_beta_header` defaults in `OpenAICodexSubscriptionOptions`, `ConfiguredModelProviderRegistryBuilder`, and `CodeAltaConfigStore` to false; retain explicit true for legacy turn requests and never send `responses=experimental` to `/models`.
- [ ] Give model discovery a five-second outer timeout and bounded retry for network/timeout/5xx only, preserve 401 behavior and static-fallback rules, and reuse the shared subscription HTTP transport rather than an unrelated default `HttpClient`.
- [ ] Add pipeline/config regressions for default/explicit beta behavior, header identity, query preservation, discovery timeout/retry/ETag, HTTP 401 refresh, WebSocket 426 fallback, retry exhaustion, and non-retryable policy failures.
- [ ] Update `doc/providers.md` and `site/docs/model-providers.md` for the Codex-only protocol adapter, Lite capability gating, current metadata/turn-state flow, beta default, header names, reasoning delivery support, strict lifecycle, discovery behavior, and sanitized metadata projection.
- [ ] Remove the inaccurate documented `response_transport="sse"` config claim while retaining runtime tolerance for programmatic legacy values, unless implementation evidence shows the config validator should intentionally accept it as an alias.
- [ ] Review `readme.md` and `doc/development-guide.md`; update them only if public setup or repository-wide provider-boundary guidance changed, avoiding duplicate implementation detail.

## Verification checklist

- [ ] Run focused protocol/model tests from `src`: `dotnet test CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --filter "FullyQualifiedName~OpenAICodexSubscriptionPipelineTests|FullyQualifiedName~OpenAIRawApiModelProviderRuntimeTests"`.
- [ ] Run focused runtime/config/serialization tests from `src`: `dotnet test CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --filter "FullyQualifiedName~AgentSessionTests|FullyQualifiedName~CodeAltaConfigStoreRawApiTests|FullyQualifiedName~AgentJsonSerializationTests|FullyQualifiedName~CodeAltaAppTests"`.
- [ ] Run `dotnet build -c Release` and `dotnet test -c Release` from `src`.
- [ ] Run `lunet build` from `site`.
- [ ] Run `git diff --check` and self-review the staged diff for generic Responses regressions, public XML docs, config round-trip compatibility, disposal/cancellation, AOT/trimming friendliness, and absence of static mutable state.
- [ ] Inspect generated/request fixtures and persisted session state to confirm secrets, raw headers, turn state, and raw moderation metadata are absent.
- [ ] Confirm the pre-existing untracked `.alta/config.toml`, `.alta/mcp.json`, and diagnostic archive remain unchanged; no live subscription request is required for acceptance.

## Handoff notes

- Use `C:\code\codex` at exact commit `6138909d6ec58b2fbe635ef973e02caecad5a5aa` as the reference; do not silently follow later changes without rerunning the comparison.
- Start each behavior stage with its regression tests, then implement the smallest code path needed. Keep generic OpenAI Responses SDK handling isolated from Codex subscription protocol code.
- Prefer logical commits for: protocol adapter; lifecycle/errors/reconstruction; model capabilities/context/turn state; Lite/reasoning; metadata/end-turn; transport/config/docs. Commit subjects must follow the repository autolabel prefix rules.
- The plan intentionally defers compression and new proxy/TLS configuration. If the custom Codex HTTP adapter cannot preserve an existing configured transport guarantee, stop and revise the plan rather than bypassing it.
- Execute in Default/build mode using the current model at medium reasoning effort.
- No source/config/docs files were modified and no tests were run while preparing this plan.
