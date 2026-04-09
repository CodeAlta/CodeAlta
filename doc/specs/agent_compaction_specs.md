# Agent Compaction Specification

Status: **Draft**  
Last updated: **2026-04-08**

Primary references:
- `src/CodeAlta.Agent/LocalRuntime/`
- `src/CodeAlta.Agent.OpenAI/`
- `src/CodeAlta.Agent.Anthropic/`
- `src/CodeAlta.Agent.GoogleGenAI/`
- `src/CodeAlta.Catalog/`
- `doc/specs/agent_local_specs.md`

## 1. Goal

Define a provider-agnostic compaction system for local raw-API agents used by:

- OpenAI-compatible backends
- Anthropic Messages backends
- Google GenAI backends

The system must be:

- simple enough to implement and maintain
- strong enough to keep long coding sessions usable
- configurable globally with per-provider overrides
- internally factored so future plugins can customize or replace compaction behavior

## 2. Problem

The local runtime owns conversation replay and prompt construction. That means context growth is also a local-runtime responsibility.

Today the local runtime only has a minimal manual snapshot mechanism:

- it is not provider-budget aware
- it is not automatically triggered
- it collapses the whole conversation into one synthetic message
- it does not preserve a recent working set intelligently
- it does not treat the latest user request as a protected anchor
- it does not maintain a structured iterative checkpoint

That is useful as a placeholder, but not sufficient for long-running coding sessions.

## 3. Non-goals

This spec does **not** require:

- provider-native server-side truncation or thread compaction APIs
- a perfect tokenizer for every provider in v1
- multi-summary graph structures
- full semantic reconstruction of every tool output
- compaction during an active tool execution

CodeAlta should keep local state authoritative and use application-managed compaction.

## 4. Core principles

1. **Local and provider-agnostic first**  
   Compaction must work even when the provider has no native memory/thread compaction feature.

2. **Protect the current objective**  
   The latest user-authored prompt must stay verbatim in active context whenever possible.

3. **Preserve recent working state**  
   Keep a recent suffix unsummarized, but allow split-turn compaction when a single turn becomes too large.

4. **Use structured summaries, not loose prose**  
   Checkpoints should preserve goals, decisions, progress, next steps, important files, and blockers.

5. **Do not elevate history into policy**  
   A compaction checkpoint is context, not a system instruction. It must not be merged into the system/developer prompt.

6. **Prefer reported usage over guesswork**  
   Provider-reported usage is the best source when available. Local estimation is a fallback.

7. **Leave room for the next response**  
   Capacity decisions must consider prompt tokens, reserved output tokens, and request overhead together.

8. **Be pluggable, but not framework-heavy**  
   The runtime should be internally staged so future plugins can hook or replace compaction, without requiring a large plugin surface in v1.

## 5. Terminology

- **Active context**: the exact message list sent to the provider for the next turn.
- **Checkpoint**: the synthetic message that represents compacted history.
- **Anchor message**: the latest user-authored message that should be kept verbatim.
- **Recent suffix**: the newest kept verbatim messages after the compacted region.
- **Split-turn compaction**: compacting part of a turn while keeping its later suffix verbatim.
- **Usable prompt budget**: the maximum safe prompt size after reserving output and framing headroom.

## 6. Required behavior

### 6.1 Trigger modes

The runtime must support:

- **manual compaction**
- **pre-send threshold compaction**
- **post-response threshold compaction**
- **overflow recovery compaction**

### 6.2 High-level flow

1. Resolve model capacity.
2. Determine current token usage from reported usage or local estimation.
3. If compaction is needed, prepare a compaction plan.
4. Summarize the compacted region with the current provider/model by default.
5. Persist a compaction checkpoint event.
6. Rebuild active context as:
   - system/developer instructions
   - compaction checkpoint message, if any
   - kept verbatim messages
7. Re-estimate post-compaction size.
8. If compaction was triggered by overflow, retry at most once.

## 7. Capacity model

### 7.1 Limits to resolve

The runtime should resolve, when available:

- `contextWindow`
- `inputTokenLimit`
- `outputTokenLimit`

Resolution order:

1. explicit config/model override
2. provider-reported model metadata
3. enriched catalog metadata
4. unknown

If limits are unknown:

- automatic threshold compaction should be disabled by default
- overflow recovery compaction may still run if the provider clearly signaled context overflow
- manual compaction must still be available

### 7.2 Safe request formula

The next request is safe only if:

```text
promptTokens + reservedOutputTokens + reservedOverheadTokens <= contextWindow
```

and, when known:

```text
promptTokens <= inputTokenLimit
reservedOutputTokens <= outputTokenLimit
```

The runtime must enforce all known constraints, not only `promptTokens < contextWindow`.

### 7.3 Usable prompt budget

```text
usablePromptBudget =
  min(
    contextWindow - reservedOutputTokens - reservedOverheadTokens,
    inputTokenLimit if known
  )
```

If `usablePromptBudget <= 0`, the session configuration is invalid for that model and compaction cannot fix it.

### 7.4 Default thresholds

Recommended defaults:

- `enabled = true`
- `trigger_threshold = 0.80`
- `target_threshold = 0.50`
- `reserved_output_tokens = 4096`
- `reserved_overhead_tokens = 2048`
- `keep_last_user_message = true`
- `allow_split_turn = true`

Interpretation:

- auto-compaction starts when estimated prompt usage reaches 80% of the usable prompt budget
- after compaction, the planner should aim to reduce the prompt to about 50% of the usable prompt budget

Per-provider overrides must be supported.

## 8. Trigger semantics

### 8.1 Manual compaction

Manual compaction:

- is always user-initiated
- may optionally accept extra focus instructions for the summary
- should run only from an idle session in the initial implementation

### 8.2 Pre-send threshold compaction

Before sending a new provider request, the runtime should estimate the prompt size of the pending active context.

If:

```text
estimatedPromptTokens >= usablePromptBudget * trigger_threshold
```

the runtime should compact before sending the request.

This avoids spending a full failed request just to discover that context is too large.

### 8.3 Post-response threshold compaction

After a successful turn, if the reported or estimated current window is above threshold, the runtime may compact immediately so the next user turn starts from a cleaner state.

### 8.4 Overflow recovery compaction

If the provider returns a context-overflow-style error:

1. remove the overflow error assistant message from active replay context
2. compact
3. rebuild context
4. retry once

If the retry still fails with overflow, surface the failure and stop.

## 9. Planning the compacted region

### 9.1 Protected regions

The planner must always keep:

1. system/developer instructions
2. the latest user-authored message, when one exists
3. the newest suffix that fits the post-compaction target

The latest user message is a hard anchor because it is usually the clearest statement of the current task.

### 9.2 Message classes

The planner should understand at least:

- user messages
- assistant text/reasoning messages
- tool call metadata
- tool result messages
- attachment/image placeholders
- future checkpoint messages

Tool results should never become dangling context. A cut point must not separate a tool result from the assistant turn it belongs to.

### 9.3 Split-turn compaction

If the suffix after the latest user prompt is itself too large:

- keep the latest user prompt verbatim
- summarize the early assistant/tool portion of that turn
- keep the most recent suffix verbatim

This is required for long coding turns where the assistant produced many tool calls and outputs after a single user request.

### 9.4 Aggressive fallback

If a normal plan still does not fit:

1. reduce the kept suffix
2. keep only the latest user anchor plus checkpoint
3. if it still cannot fit, fail with a clear error

The runtime must not loop compaction indefinitely.

## 10. Checkpoint content

The default checkpoint summary should be structured and concise.

Recommended format:

```md
## Objective
## Active User Request
## Constraints
## Progress
### Done
### In Progress
### Blocked
## Decisions
## Next Steps
## Critical Context
## Relevant Files
```

Rules:

- preserve exact file paths, identifiers, and important error text
- record what changed, what remains, and what must not be forgotten
- keep the summary optimized for continuation by another model call

### 10.1 Relevant files

The default implementation should track files from structured tool activity when available.

At minimum:

- files read
- files modified

This should be best-effort and metadata-driven. It should not depend on brittle parsing of shell output.

### 10.2 Iterative updates

If a previous checkpoint already exists, the runtime should update it instead of summarizing from scratch.

The update flow should:

- preserve prior durable context
- add new progress and decisions
- drop resolved blockers when appropriate
- update next steps

This keeps checkpoint quality more stable across repeated compactions.

## 11. Checkpoint message role

The active checkpoint must be inserted into the replayed conversation as a **synthetic user-context message**, not as a system message.

Reason:

- it is historical context
- it must not outrank system/developer instructions
- it should remain visible as replayable conversation state, not hidden policy

Recommended wrapper:

```text
<codealta-compaction-checkpoint version="1">
...
</codealta-compaction-checkpoint>
```

## 12. Summarization input preparation

The summarizer must not be given the old conversation as a normal chat continuation.

Instead, the runtime should serialize messages into labeled plain text such as:

- `[User]`
- `[Assistant]`
- `[Assistant reasoning]`
- `[Assistant tool calls]`
- `[Tool result]`
- `[Attachment]`

This reduces the chance of the summarizer trying to continue the conversation.

### 12.1 Truncation rules

Large tool outputs and attachments should be truncated before summarization.

Recommended defaults:

- tool result text: 2000 characters per result
- inline binary/base64: never include raw payload; emit a descriptor only
- images: summarize as image/attachment references, not raw data

Truncation applies to the **summary request**, not to the stored canonical event log.

### 12.2 Canonical input only

Compaction input must be built from canonical finalized content, not from transient streaming deltas.

Rules:

- assistant/reasoning/tool-output delta events that are later superseded by a finalized content event must be ignored for compaction
- if the runtime keeps transient deltas in memory for live UI streaming, they must still be excluded from compaction planning and summarization
- when a backend only streams deltas, the runtime should first synthesize a finalized canonical content event, then compact from that canonical form

## 13. Token accounting strategy

### 13.1 Priority order

1. **provider-reported window usage**
2. **provider-reported last-operation usage plus local trailing estimate**
3. **local tokenizer estimate**
4. **conservative heuristic estimate**

The runtime should record the confidence/source of the estimate internally.

### 13.2 What must be counted

Estimation should include:

- system/developer instructions that are actually sent
- user text
- assistant text
- reasoning summaries/text when sent back to the model
- tool call names and arguments
- tool results
- synthetic checkpoint messages
- attachment/image placeholders

### 13.3 TiktokenSharp evaluation

`TiktokenSharp` is a potentially useful optional estimator for OpenAI-compatible models, but it should **not** be a required baseline dependency for v1.

Reasons:

- it is OpenAI-encoding-centric, not universal across Anthropic and Google models
- its README states that encoder files may be downloaded on first use
- offline/local-runtime predictability is more important than marginal estimator precision in the baseline design

Decision:

- v1 should use provider usage first and conservative local estimation second
- tokenizer support should be abstracted behind an internal estimator interface
- an OpenAI-specific tokenizer implementation can be added later, potentially using `TiktokenSharp`

## 14. Persistence model

The canonical event log remains `events.jsonl`.

Compaction should be persisted as a dedicated raw event payload, for example:

- `local.compactionCheckpoint`

The payload should include at least:

- checkpoint schema version
- trigger reason (`manual`, `threshold`, `overflow`)
- summary text
- first kept event offset or stable boundary marker
- anchor user message identifier when present
- estimated tokens before compaction
- estimated tokens after compaction
- summarized message count
- read-files list
- modified-files list

`state.json` should track the latest compaction cursor and the latest checkpoint event identifier.

`events.jsonl` should not durably store duplicate streaming delta events when the same content is later represented by a finalized canonical event.

## 15. Rebuild model

Session replay after compaction should rebuild active context from:

1. composed system/developer instructions
2. latest checkpoint message, if any
3. kept verbatim messages after the checkpoint boundary

Older compacted messages remain in the canonical event log for history/audit, but they no longer participate directly in active prompt construction.

## 16. Extensibility model

The internal implementation should be factored into four stages:

1. **budgeting**
2. **planning**
3. **summary generation**
4. **persistence/rebuild**

That is enough for future plugins.

The future plugin surface should allow:

- cancel compaction
- inspect the prepared plan
- provide a replacement summary/checkpoint
- augment metadata
- observe the saved result

The first implementation does **not** need a full public plugin API, but the code structure should preserve these seams.

## 17. Configuration

Use a global default block plus per-provider override blocks.

Recommended TOML shape:

```toml
[raw_api.compaction]
enabled = true
trigger_threshold = 0.80
target_threshold = 0.50
reserved_output_tokens = 4096
reserved_overhead_tokens = 2048
keep_last_user_message = true
allow_split_turn = true

[raw_api.openai.providers.openai.compaction]
trigger_threshold = 0.82
reserved_output_tokens = 4096

[raw_api.anthropic.providers.anthropic.compaction]
trigger_threshold = 0.80
reserved_output_tokens = 8192

[raw_api.google_genai.providers.google.compaction]
trigger_threshold = 0.75
reserved_output_tokens = 4096
```

Rules:

- provider settings override global defaults
- unspecified provider values inherit global defaults
- model-specific overrides are deferred unless a concrete need appears

### 17.1 Config document changes

The configuration model should be extended in `CodeAlta.Catalog` as follows.

Add a reusable document type:

```csharp
public sealed class CodeAltaRawApiCompactionDocument
{
    public bool? Enabled { get; set; }
    public double? TriggerThreshold { get; set; }
    public double? TargetThreshold { get; set; }
    public int? ReservedOutputTokens { get; set; }
    public int? ReservedOverheadTokens { get; set; }
    public bool? KeepLastUserMessage { get; set; }
    public bool? AllowSplitTurn { get; set; }
}
```

Add a global block to `CodeAltaRawApiSettingsDocument`:

```csharp
public CodeAltaRawApiCompactionDocument? Compaction { get; set; }
```

Add an optional provider override block to each provider document:

- `CodeAltaOpenAIProviderDocument`
- `CodeAltaAnthropicProviderDocument`
- `CodeAltaGoogleGenAIProviderDocument`

```csharp
public CodeAltaRawApiCompactionDocument? Compaction { get; set; }
```

### 17.2 Config normalization rules

`CodeAltaConfigStore` should normalize compaction settings using this precedence:

1. hardcoded runtime defaults
2. `[raw_api.compaction]`
3. `[raw_api.<family>.providers.<provider>.compaction]`

Validation rules:

- `trigger_threshold` must be `> 0` and `<= 1`
- `target_threshold` must be `> 0` and `< trigger_threshold`
- reserved token values must be `>= 0`
- `target_threshold` should default lower than `trigger_threshold`

The normalized runtime object should be non-null and complete by the time it reaches the local runtime.

## 18. Proposed C# type and interface changes

### 18.1 Public API

No large public API expansion is required for v1.

Keep:

- `IAgentSession.CompactAsync(...)`
- `IAgentCompactionOutcomeProvider`
- `AgentCompactionOutcome`

Recommended small addition:

```csharp
public enum AgentCompactionTriggerKind
{
    Manual,
    Threshold,
    Overflow,
}
```

`AgentCompactionOutcome` may optionally grow a `Trigger` field later, but this is not required for the first implementation.

### 18.2 Local runtime internal types

Add internal types under `src/CodeAlta.Agent/LocalRuntime/Compaction/`:

```csharp
internal sealed record LocalAgentCompactionSettings(
    bool Enabled,
    double TriggerThreshold,
    double TargetThreshold,
    int ReservedOutputTokens,
    int ReservedOverheadTokens,
    bool KeepLastUserMessage,
    bool AllowSplitTurn);

internal enum LocalAgentCompactionTrigger
{
    Manual,
    Threshold,
    Overflow,
}

internal sealed record LocalAgentTokenBudget(
    long? ContextWindow,
    long? InputTokenLimit,
    long? OutputTokenLimit,
    long UsablePromptBudget,
    int ReservedOutputTokens,
    int ReservedOverheadTokens);

internal sealed record LocalAgentTokenEstimate(
    long Tokens,
    string Source,
    bool IsEstimated);

internal sealed record LocalAgentCompactionPreparation(
    LocalAgentCompactionTrigger Trigger,
    IReadOnlyList<LocalAgentConversationMessage> MessagesToSummarize,
    IReadOnlyList<LocalAgentConversationMessage> TurnPrefixMessages,
    IReadOnlyList<LocalAgentConversationMessage> MessagesToKeep,
    string? AnchorContentId,
    bool IsSplitTurn,
    LocalAgentTokenEstimate TokensBefore,
    string? PreviousSummary);

internal sealed record LocalAgentCompactionResult(
    string Summary,
    string? AnchorContentId,
    bool IsSplitTurn,
    long TokensBefore,
    long? TokensAfter,
    int MessagesSummarized,
    IReadOnlyList<string> ReadFiles,
    IReadOnlyList<string> ModifiedFiles);
```

The exact names may vary, but the implementation should have explicit types for:

- normalized settings
- resolved budgets
- token estimates
- compaction preparation
- compaction result

### 18.3 Provider descriptor/runtime wiring

Extend `LocalAgentProviderDescriptor` with a normalized compaction block:

```csharp
public LocalAgentCompactionSettings? Compaction { get; init; }
```

This keeps provider-specific compaction policy attached to the resolved provider identity.

### 18.4 Session persistence types

Extend `LocalAgentSessionState` with enough compaction metadata to support replay/debugging:

```csharp
public string? CompactionCheckpointEventId { get; init; }
public DateTimeOffset? LastCompactedAt { get; init; }
public string? LastCompactionTrigger { get; init; }
public long? LastCompactionTokensBefore { get; init; }
public long? LastCompactionTokensAfter { get; init; }
```

Keep the existing compaction cursor/offset concept, but make the state explicit enough for diagnostics and future plugin hooks.

### 18.5 Canonical event persistence

The runtime should distinguish:

- **transient streamed events**
- **durable canonical events**

The simplest internal shape is:

```csharp
internal enum LocalAgentEventPersistenceMode
{
    TransientOnly,
    DurableCanonical,
}
```

or an equivalent filter/policy object.

This does not need to be public.

### 18.6 Turn failure classification

Overflow recovery requires provider-aware failure classification.

Add an internal provider/runtime seam such as:

```csharp
internal sealed record LocalAgentTurnFailure(
    string Message,
    bool IsContextOverflow);
```

and either:

- a classifier delegate on `LocalAgentBackendProviderRegistration`, or
- a typed exception/result contract from the turn executors

The session layer must be able to distinguish:

- context overflow
- generic provider failure

without brittle string matching in `LocalAgentSession`.

## 19. Implementation map by file/class

The implementation should be split as follows.

### 19.1 Catalog/config

- `src/CodeAlta.Catalog/CodeAltaRawApiSettingsDocument.cs`
  - add global/provider compaction config documents
- `src/CodeAlta.Catalog/CodeAltaConfigStore.cs`
  - clone, validate, normalize, and merge compaction settings

### 19.2 Local runtime models

- `src/CodeAlta.Agent/LocalRuntime/LocalAgentProviderDescriptor.cs`
  - attach normalized provider compaction settings
- `src/CodeAlta.Agent/LocalRuntime/LocalAgentSessionFiles.cs`
  - persist richer compaction state
- `src/CodeAlta.Agent/LocalRuntime/LocalAgentTurnContracts.cs`
  - add any failure classification/result details needed for overflow recovery

### 19.3 New compaction subsystem

Create:

- `src/CodeAlta.Agent/LocalRuntime/Compaction/LocalAgentCompactionSettings.cs`
- `src/CodeAlta.Agent/LocalRuntime/Compaction/LocalAgentTokenBudgetResolver.cs`
- `src/CodeAlta.Agent/LocalRuntime/Compaction/LocalAgentTokenEstimator.cs`
- `src/CodeAlta.Agent/LocalRuntime/Compaction/LocalAgentCompactionPlanner.cs`
- `src/CodeAlta.Agent/LocalRuntime/Compaction/LocalAgentCompactionSummarizer.cs`
- `src/CodeAlta.Agent/LocalRuntime/Compaction/LocalAgentCompactionSerializer.cs`
- `src/CodeAlta.Agent/LocalRuntime/Compaction/LocalAgentCompactionCheckpoint.cs`

The implementation may merge some of these files if it stays readable, but these responsibilities should remain distinct.

### 19.4 Session orchestration

- `src/CodeAlta.Agent/LocalRuntime/LocalAgentSession.cs`
  - run pre-send threshold checks
  - run overflow recovery compaction
  - protect the latest user prompt
  - rebuild replay context from checkpoint + kept suffix
  - stream deltas live but persist only canonical finalized content

### 19.5 Event persistence

- `src/CodeAlta.Agent/LocalRuntime/FileSystemLocalAgentSessionStore.cs`
  - write only durable canonical events to `events.jsonl`
- `src/CodeAlta.Agent/LocalRuntime/ILocalAgentSessionStore.cs`
  - no shape change required unless explicit transient/durable entry points improve clarity

### 19.6 Usage/budget helpers

- `src/CodeAlta.Agent/LocalRuntime/LocalAgentUsageFactory.cs`
  - expose budget-related helpers from model metadata
  - keep context-window/input/output limits normalized

### 19.7 Provider executors

- `src/CodeAlta.Agent.OpenAI/OpenAIChatTurnExecutor.cs`
- `src/CodeAlta.Agent.OpenAI/OpenAIResponsesTurnExecutor.cs`
- `src/CodeAlta.Agent/LocalRuntime/LocalAgentChatClientTurnExecutor.cs`

Responsibilities:

- preserve finalized assistant/reasoning/tool-call output
- provide enough failure information for overflow handling
- continue streaming deltas for the live UI

## 20. Recommended implementation phases

### Phase 1

- provider-aware capacity resolution
- global + per-provider config
- threshold compaction
- overflow recovery compaction
- checkpoint as synthetic user-context message
- structured summary
- iterative summary update
- rebuild from checkpoint + kept suffix

### Phase 2

- richer file/activity tracking
- tokenizer abstraction
- optional OpenAI-specific tokenizer support
- future plugin hooks

## 21. Test matrix

At minimum, add tests for:

1. no compaction when under threshold
2. threshold compaction at 80% of usable budget
3. correct budget calculation when output and input limits are both known
4. unknown limit behavior
5. latest user message always kept
6. split-turn compaction when the latest turn is too large
7. tool-result boundaries never split incorrectly
8. iterative checkpoint update
9. overflow compact-and-retry once
10. stale pre-compaction usage not reused as current-window truth
11. checkpoint replay rebuild correctness
12. per-provider override precedence

## 22. Concrete decisions

1. Compaction remains application-managed and provider-agnostic.
2. The latest user prompt is a protected anchor and should remain verbatim.
3. Capacity checks must reserve output and overhead, not only compare prompt size to context window.
4. Defaults are global, with per-provider overrides.
5. The checkpoint is context, not policy, and should not be stored as a system instruction.
6. Provider usage is preferred over local token estimation.
7. `TiktokenSharp` is not required for the first implementation.
8. The implementation should be staged internally so plugins can hook it later.

## 23. Summary

CodeAlta should evolve from a minimal manual snapshot into a real local-runtime compaction system built around:

- safe provider-aware budgeting
- automatic threshold and overflow recovery triggers
- a protected latest-user anchor
- structured iterative checkpoints
- replay from checkpoint plus recent verbatim history
- simple future-ready extension seams

That is a small enough design to implement now, but strong enough to support long-running coding sessions across all local raw-API providers.

