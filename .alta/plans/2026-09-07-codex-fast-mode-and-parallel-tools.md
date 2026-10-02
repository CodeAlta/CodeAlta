# Codex fast mode and parallel-tool-call parity

- Status: Complete — implemented and verified on `main` (2026-09-07).
- Plan file: `.alta/plans/2026-09-07-codex-fast-mode-and-parallel-tools.md`
- Created: 2026-09-07
- Task: Implement opt-in subscription fast routing and current Codex parallel-tool-call policy on `main`.
- Git: Not ignored; include this plan with scoped implementation. Existing untracked config/MCP files, desktop tests, prototypes, frontend dist, and compressed artifact are user-owned and out of scope.

## Objective
- Explain what works today and the smallest safe changes needed for ChatGPT/Codex subscriptions.
- Add explicit, opt-in fast routing and align model-request parallel-tool flags with current official Codex.
- Build/test permission on `main` was explicitly granted during implementation; the earlier restriction applied to another branch. Actual user configuration changes, paid requests, and credential inspection remain out of scope.
- Non-goals: concurrent tool-handler execution, a `/fast` UI command, session-level tier overrides, pricing/accounting redesign, arbitrary subscription `extra_body`, or unrelated Codex protocol parity work.

## Original investigation context and evidence (before implementation)
- Official checkout: `C:/code/codex`, clean at inspection, HEAD `6750f5bd13` (2026-09-07). CodeAlta tracked files were clean; unrelated untracked files were present.
- Official `codex-rs/protocol/src/config_types.rs:527-551`: `fast` and `priority` map to request value `priority`; `default` represents standard routing. `codex-rs/core/src/client.rs:977-990` serializes the service tier into Responses requests. `protocol/src/openai_models.rs:918-928` filters unadvertised tiers and omits explicit `default`.
- CodeAlta `src/CodeAlta.Catalog/CodeAltaConfigStore.cs:1375-1386` rejects subscription `extra_body`, `request`, and `model_request`. The public docs confirm the restriction in `site/docs/model-providers.md:396`; `CodeAltaConfigStoreRawApiTests.LoadGlobalProviderDefinitions_CodexSubscriptionRejectsExtraBody` covers it.
- `src/CodeAlta.Hosting/ConfiguredModelProviderRegistryBuilder.cs:406-458` constructs subscription options without extra-body/model-request forwarding or a service-tier option. `OpenAICodexSubscriptionOptions` in `src/CodeAlta.Agent.OpenAI/OpenAIModelProviderRuntimeOptions.cs:212-270` has no tier field. The only existing `ServiceTier` reference in C# source copies an already-set SDK option during request cloning (`OpenAIResponsesTurnExecutor.cs:968`); it does not enable config support.
- Raw `openai-responses` API-key providers do support `extra_body` through `OpenAIExtraBodyPatchHelper` and `OpenAIResponsesTurnExecutor.cs:1357-1360`. That is not a subscription workaround and changes authentication/billing.
- CodeAlta's July 10 history explains the parallel restriction: `f1b2fe49` (Add Codex model and request context parity) replaced unconditional true with model metadata; `21bf6451` (Add Codex Responses Lite parity) added the Lite false override. The approved historical `.alta/plans/2026-07-10-codex-protocol-parity.md:87-104` records the rationale. Leave that historical artifact unchanged.
- Official Codex subsequently changed policy in `86b1123ff6` on 2026-08-14, **Enable parallel tool calls for all model prompts (#38499)**: removed the model capability and enabled regular/compaction prompts, explicitly retaining the Responses Lite exception. Current `core/src/session/turn.rs:1396` sets true; `core/src/client.rs:984` applies `&& !model_info.use_responses_lite`.
- CodeAlta still parses absent `supports_parallel_tool_calls` as false (`CodexSubscriptionModelDiscoveryClient.cs:231`), exports it in `OpenAIProviderSdkFactory.CreateModelInfo`, and applies it at `OpenAIResponsesTurnExecutor.cs:1419`. Therefore modern discovery payloads lacking the removed property can disable regular-model parallel requests. This is source-confirmed conditional behavior, not a measurement of the user's live endpoint.
- Keep `CodexResponsesLiteRequestBuilder.cs:79`: its false override still matches upstream.
- Model-request parallel calls are separate from host execution concurrency: `src/CodeAlta.Agent/Runtime/AgentSession.cs:288-327` iterates calls and awaits each handler sequentially. `max_concurrent_requests` limits concurrent subscription requests, not tool execution or service tier.
- Existing request serialization is shared: HTTP writes SDK options (`CodexSubscriptionHttpStreamSession.cs:94`); WebSocket copies serialized option properties (`OpenAICodexSubscriptionWebSocketSession.cs:607-615`). Compaction uses the turn executor (`AgentTurnExecutorCompactionSummaryExecutor.ExecuteAsync`).

## Approved decisions
- Approved config is provider-scoped `service_tier = "priority"`, accepting `"fast"` as an alias and `"default"` as explicit standard routing. An omitted value leaves current routing unchanged.
- Fast routing is sent only when model discovery advertises `priority` in `service_tiers`; unsupported/unknown capability omits the tier with a clear diagnostic. Do not guess support in static fallback data or silently claim fast routing is active.
- Provider-wide tier configuration also affects requests from sessions/children and compaction using that provider. Document that breadth and the potential increased subscription usage/cost; never enable it by default.
- Execution approval (2026-09-07): user requested implementation on the current `main` branch and plan updates while manually validating it; this supersedes the earlier planning-only response.
- The user explicitly lifted the no-build restriction for `main` during implementation. No live provider requests are authorized.

## Design notes
- Add a dedicated typed/configured subscription tier rather than reopening arbitrary body injection. Follow existing config normalization, cloning, save/default elision, validation, and hosting mapping patterns next to `TextVerbosity`.
- Restrict initial tier values to standard/fast, normalize alias `fast` to `priority`, and reject typos with provider-qualified errors. Do not introduce unrelated flex/arbitrary tier selection.
- Carry advertised tier IDs through existing subscription discovery/model-capability plumbing. Set the SDK `CreateResponseOptions.ServiceTier` where supported by the pinned SDK, otherwise use the existing explicit JSON patch mechanism. Serialize only `priority`, never literal `fast`; omit standard/default.
- For regular Codex requests, set parallel calls true independently of obsolete metadata; let the existing Lite builder force false. Remove obsolete internal metadata consumption and keep any retained external `supportsParallelToolCalls` capability accurate by deriving it from `!UseResponsesLite`. Do not change other providers' capability/profile semantics.
- No migration, new dependency, credential change, endpoint change, or new scheduler is needed. Disabling/removing the tier config returns to standard routing.

## Risks and challenges
- Backend/model/account eligibility and actual latency/usage charging are not established by local source inspection. Sending `priority` is a request, not a latency guarantee; avoid hard-coded price multipliers or entitlement claims.
- Missing tier advertisements/static fallback intentionally mean no fast request; make that limitation visible. Do not add paid retry/probing behavior to discover support.
- Request cloning, retries, Lite transformation, and WebSocket-to-HTTP fallback must preserve tier selection and existing continuity fields.
- Existing tests intentionally expect the now-obsolete parallel metadata behavior and must change with regression cases, not simply be deleted.
- Concurrent refactoring may move files/tests. Recheck ownership and paths before editing; avoid touching unrelated refactoring. Builds/tests are now authorized on `main`.

## Implementation checklist
- [x] Recheck git state, applicable guidance, and target symbol locations; preserve unrelated work and retain the no-build constraint until explicitly lifted. No paid probes.
- [x] Add regression fixtures/tests first in `src/CodeAlta.Tests/OpenAICodexSubscriptionPipelineTests.cs` and `OpenAIRawApiModelProviderRuntimeTests.cs`: absent/null/false/true legacy parallel property all permit regular-model request batching; Lite remains false, including HTTP fallback. Update `OpenAIResponsesTurnExecutor_AppliesTrustedCodexModelCapabilitiesToRequest` without weakening its reasoning/verbosity assertions.
- [x] Update `CodexSubscriptionModelDiscoveryClient`, `CodexSubscriptionModelCapabilities`, `OpenAIProviderSdkFactory`, `CodexSubscriptionStaticModelCatalog`, and `OpenAIResponsesTurnExecutor` to remove obsolete parallel gating and report effective capability consistently. Keep the Lite override and sequential `AgentSession` execution unchanged.
- [x] Add tier config/round-trip/validation coverage in `CodeAltaConfigStoreRawApiTests` and registration coverage in `src/CodeAlta.Tests/ConfiguredProviderRegistrationTests.cs`; cover omitted/default/priority/fast, invalid values, non-Codex rejection, and preserving subscription extra-body restrictions.
- [x] Add the tier property and XML docs to `CodeAltaRawApiSettingsDocument.cs` and `OpenAICodexSubscriptionOptions`; update all relevant `CodeAltaConfigStore` normalize/validate/clone/persistence/has-values paths and `ConfiguredModelProviderRegistryBuilder.TryCreateCodexSubscriptionProvider` mapping. Do not edit the user's actual config.
- [x] Extend subscription discovery tests/plumbing with advertised service-tier IDs; test supported, unsupported, absent, and malformed `service_tiers`. Preserve existing tolerant parsing behavior and do not assume fast support in static fallback.
- [x] Add payload tests, then set the opted-in supported tier in `ApplyCodexSubscriptionRequestCustomizationAsync`; cover default omission, `priority` serialization, unsupported-tier diagnostic, clone/reconnect, HTTP, WebSocket, Lite, fallback, and compaction behavior using recording/fake transports only.
- [x] Update `doc/providers.md`, `site/docs/model-providers.md` (read `site/AGENTS.md` first), and the relevant provider summary in `readme.md`; document config syntax, standard opt-out, capability/eligibility caveats, cost scope, and batching versus concurrent handlers. Update `doc/development-guide.md` only where current Codex policy needs recording, without touching refactoring guidance.

## Verification checklist
- [x] Review the diff statically: no source/config changed outside scope, no secrets, no default premium routing, no Lite regression, no accidental host concurrency, no dependency changes, and new public API XML docs are present.
- [x] Retained the no-build restriction until the user lifted it; no stale-assembly tests were used as verification.
- [x] After explicit permission, run the focused config, registration, editor, discovery, and request-payload test classes at their current locations: 219 passed.
- [x] Perform project-required Release build/full tests from `src` and `lunet build` from `site`: build and site passed; full suite passed with one existing skipped test (details below).
- [x] No live premium request or actual subscription configuration change was performed. Live smoke testing remains optional and requires separate usage/cost consent.

## Verification results
- Focused command: `dotnet test CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CodeAltaConfigStoreRawApiTests|FullyQualifiedName~ConfiguredProviderRegistrationTests|FullyQualifiedName~ModelProviderEditorItemViewModelTests|FullyQualifiedName~OpenAICodexSubscriptionPipelineTests|FullyQualifiedName~OpenAIRawApiModelProviderRuntimeTests"` from `src`: **219 passed**.
- `dotnet build -c Release` from `src`: **passed, zero warnings/errors**.
- `dotnet test -c Release --no-build --no-restore` from `src`, immediately after the successful full build: **1,849 passed, 1 skipped, 0 failed** across all five test projects. Existing skip: `ProjectFileSearchSession_PublishesIncrementalUpdatesAndIgnoresStaleRefreshes`.
- `lunet build` from `site`: **passed** (111 files processed).
- Initial `--no-restore` focused build encountered stale branch restore assets/missing frontend references; rerunning with normal restore resolved it without source/dependency changes. Focused tests then caught the SDK's implicit string-to-tier conversion on a conditional null; explicitly casting null to `ResponseServiceTier?` fixed standard/unsupported routing. The config test was aligned with existing normalization eliding defaults on load as well as save. All subsequent focused and full runs passed.
- No live account eligibility, latency, or charging validation was attempted; fake/recording transports prove routing selection and serialization only.

## Handoff notes
- Current checkout differs from draft paths: registration builder is `src/CodeAlta/App/ConfiguredModelProviderRegistryBuilder.cs`; the cited hosting test file does not exist, so registration coverage is added under `src/CodeAlta.Tests/`. No architecture/refactoring moves are included.
- Pinned OpenAI 2.13.0 was inspected locally with the installed ILSpy tool (no build/install): `ResponseServiceTier(string)` supports `priority`, and existing option cloning already preserves `ServiceTier`.
- Additional preservation paths found during implementation: the provider editor clones `ServiceTier`; model capability JSON round trips use `object[]`, which tier recognition now handles. Both have regression coverage.
- Implementation and this plan are one scoped Codex request-policy change; unrelated user-owned untracked files remain excluded.
- User-facing conclusion: Codex fast routing can now be explicitly configured with `service_tier = "priority"` (alias `fast`) when the model advertises it. Omitted/default remains standard routing. Regular parallel-call gating is removed; Lite and sequential host-handler execution are unchanged.
