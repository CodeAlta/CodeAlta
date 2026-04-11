# Agent Compaction Specification

Status: **Draft**  
Last updated: **2026-04-11**

Primary references:
- `src/CodeAlta.Agent/LocalRuntime/`
- `src/CodeAlta.Agent.OpenAI/`
- `src/CodeAlta.Agent.Anthropic/`
- `src/CodeAlta.Agent.GoogleGenAI/`
- `src/CodeAlta.Catalog/`
- `doc/specs/agent_local_specs.md`

Inspirational references:
- `C:\code\pi-mono\packages\coding-agent\docs\compaction.md`
- `C:\code\pi-mono\packages\coding-agent\src\core\compaction\compaction.ts`
- `C:\code\pi-mono\packages\coding-agent\src\core\compaction\utils.ts`
- `C:\code\codex\codex-rs\core\src\compact.rs`
- `C:\code\codex\codex-rs\core\templates\compact\prompt.md`

## 1. Goal

Define a **local, provider-agnostic, high-compression** compaction system for raw-API agents used by:

- OpenAI-compatible chat/responses backends
- Anthropic Messages backends
- Google GenAI backends

The system must:

- preserve continuity for long coding sessions
- aggressively discard low-value verbose history
- avoid depending on provider-native compaction APIs
- use a normal chat/generation API for summary generation
- remain configurable globally with per-provider overrides

## 2. Problem statement

The local runtime owns prompt construction, so it also owns context growth.

The first implementation solved important structural problems, but it is still not compact enough for large coding sessions. In practice, long sessions are often dominated by:

- repeated tool calls
- large tool outputs
- verbose reasoning
- long file reads
- oversized single-turn histories

If those are serialized too literally, the checkpoint can remain **far too large**. A compaction result that still leaves the session near **50%** of its previous footprint is often a failure for this use case. For coding agents, the desired result is usually much smaller: often in the **1%–6%** range, and only approaching **10%** when a very large retained recent suffix must remain verbatim.

## 3. Non-goals

This specification does **not** require:

- provider-native server-side truncation or thread-compaction APIs
- a perfect tokenizer for every provider in v1
- exact semantic preservation of every tool output line
- compaction while tools are actively executing
- a public plugin API in the first implementation

CodeAlta should keep local state authoritative and treat compaction as an application-managed history reduction problem.

## 4. Core principles

1. **Compact for continuation, not for archival**  
   The checkpoint is not a transcript. It is a continuation handoff.

2. **Discard aggressively, preserve deliberately**  
   Low-value bulk data must be omitted by default. Retention should be earned by relevance.

3. **Protect the current objective**  
   The latest user-authored request should remain verbatim whenever it is physically possible.

4. **Prefer structured state over conversational prose**  
   Goals, decisions, blockers, next steps, changed files, and critical errors matter more than storytelling.

5. **Do not treat history as policy**  
   Compacted history is context, not a new system or developer instruction.

6. **Canonical content only**  
   Streaming deltas are never compaction input.

7. **Budget the summary itself**  
   A compaction pass that produces a verbose summary has failed even if it is “accurate”.

8. **Prefer recency when tradeoffs are required**  
   When history must be reduced, newer messages and newer high-signal tool results should be favored over older ones.

9. **Do not drop messages unnecessarily**  
   If canonical non-delta history fits within the summarizer input budget, it should be summarized as a whole rather than pre-trimmed.

10. **Use ordinary model calls, not remote compaction endpoints**  
   CodeAlta should use the normal provider chat/generation surface for summarization, with explicit output limits.

## 5. Terminology

- **Active context**: the exact message list sent for the next turn.
- **Checkpoint**: the synthetic message representing compacted history.
- **Anchor message**: the latest user-authored message that should stay verbatim when possible.
- **Recent suffix**: the contiguous newest messages kept verbatim after the compacted region.
- **Split-turn compaction**: summarizing the earlier part of a single large turn while keeping its later suffix verbatim.
- **Oversized item reduction**: summarizing part of a single message or attachment because the item itself is too large to fit.
- **Usable prompt budget**: safe prompt capacity after reserving output and framing overhead.
- **Compression ratio**: `postCompactionPromptTokens / preCompactionPromptTokens`.

## 6. Required outcomes

### 6.1 Compression target

Compaction should target a **post-compaction total active-context ratio** in this range:

- **ideal**: `1% - 3%`
- **normal acceptable**: `3% - 6%`
- **upper bound target**: `10%`

Interpretation:

- The total active context after compaction should usually be tiny compared with the pre-compaction context.
- The upper end of the range is reserved for sessions where the retained suffix itself is expensive.
- Exceeding `10%` should be treated as an exceptional fallback, not a normal success case.

This ratio is a **planning and validation target**, not a guarantee. If the protected suffix alone prevents it, the runtime may exceed it, but only after exhausting stricter fallback options.

All target numbers in this section must be configurable. The defaults should be opinionated, but deployments must be able to tune them globally and per provider.

### 6.2 Compaction success criteria

A compaction pass is successful only if all of the following are true:

1. the rebuilt prompt fits all known provider limits
2. the checkpoint is materially smaller than the summarized region
3. the latest task remains understandable
4. critical unresolved context is preserved
5. the summary does not contain large low-value copied output

## 7. Trigger modes

The runtime must support:

- **manual compaction**
- **pre-send threshold compaction**
- **post-response threshold compaction**
- **overflow recovery compaction**

### 7.1 Threshold compaction preservation rule

When compaction is triggered by threshold, the runtime should assume that it will **usually** be able to fit the entire canonical conversation history, excluding transient deltas, into the summarizer input request.

Implications:

- threshold compaction should first attempt to summarize the full canonical non-delta history region selected by the planner
- it should not eagerly discard older messages merely because compaction was triggered
- message dropping should begin only if the summarizer input itself does not fit the configured summary-input budget
- once reduction is necessary, recency bias should apply

## 8. High-level flow

1. Resolve model capacity.
2. Determine current prompt usage from provider usage or local estimation.
3. Decide whether compaction is needed.
4. Canonicalize history and discard non-canonical deltas.
5. Build a compaction plan.
6. Serialize a **budgeted** summarization input.
7. If the summarization input is too large, recursively chunk and summarize it.
8. If the latest protected anchor is itself too large, reduce it into a compact anchor synopsis first.
9. Generate a checkpoint using a normal provider chat/generation call.
10. Rebuild active context from checkpoint + retained suffix.
11. Validate exact post-compaction fit.
12. Persist the checkpoint and compaction metadata.
13. Retry the original turn at most once when recovering from overflow.

## 9. Capacity model

### 9.1 Limits to resolve

Resolve, when available:

- `contextWindow`
- `inputTokenLimit`
- `outputTokenLimit`

Resolution order:

1. explicit config/model override
2. provider-reported model metadata
3. catalog metadata
4. unknown

### 9.2 Safe request formula

The next request is safe only if:

```text
promptTokens + reservedOutputTokens + reservedOverheadTokens <= contextWindow
```

and, when known:

```text
promptTokens <= inputTokenLimit
reservedOutputTokens <= outputTokenLimit
```

### 9.3 Usable prompt budget

```text
usablePromptBudget =
  min(
    contextWindow - reservedOutputTokens - reservedOverheadTokens,
    inputTokenLimit if known
  )
```

If `usablePromptBudget <= 0`, compaction cannot fix the request.

### 9.4 Unknown-limit behavior

If limits are unknown:

- pre-send auto-compaction should be disabled by default
- post-response auto-compaction should be disabled by default
- overflow recovery may still run if the provider clearly reports context overflow
- manual compaction remains available

## 10. Budget policy

### 10.1 Separate budgets

The runtime must treat these budgets separately:

1. **request fit budget**: what the next real model request may safely send
2. **post-compaction target budget**: how small the rebuilt active context should become
3. **summary request input budget**: how large the summarizer input is allowed to be
4. **summary response budget**: how many output tokens the summarizer may emit

These must not be conflated.

The post-compaction target numbers and summary-input limits must all be configurable.

### 10.2 Recommended defaults

Recommended defaults:

- `trigger_threshold = 0.80`
- `reserved_output_tokens = 4096`
- `reserved_overhead_tokens = 2048`
- `target_context_ratio_ideal = 0.03`
- `target_context_ratio_max = 0.10`
- `recent_suffix_target_tokens = 16000-24000` with `20000` as the default center point
- `summary_input_token_limit = 20000-24000` when provider capacity allows it
- `summary_output_token_limit = 768-1536` depending on model size and split-turn complexity
- `keep_last_user_message = true`
- `allow_split_turn = true`

These are defaults only. Deployments must be able to override them globally and per provider.

Rationale:

- pi-mono's defaults keep roughly `20000` recent tokens and reserve `16384` tokens for the summarizer/response path
- CodeAlta should not copy pi-mono's looser summary behavior directly, but it should take inspiration from that scale so threshold compaction does not feel artificially starved
- the original v2 example values (`summary_output_tokens = 512`, `summary_input_tokens = 12000`) are likely too conservative as general defaults for long coding sessions
- CodeAlta should instead pair a **larger summarizer input budget** with **stronger omission/global-cap logic**, so it can read enough context without reproducing transcript-like checkpoints

### 10.3 Summary output bound

The summarizer call must use an explicit provider-specific output limit:

- OpenAI-compatible: `max_output_tokens`, `max_completion_tokens`, or equivalent abstraction
- Anthropic: `max_tokens`
- Google GenAI: `maxOutputTokens`

The compaction system must never rely on an unconstrained summarizer response.

The effective cap must also respect any smaller provider/model output limit. CodeAlta must not enforce a local minimum that exceeds the resolved provider maximum.

This applies to every compaction-related generation pass, including:

- main checkpoint generation
- chunk partial summaries
- oversized-anchor reduction
- final merge/update passes

## 11. Message relevance policy

Compaction quality depends more on **what is omitted** than on what is copied.

However, omission must be **budget-driven**, not arbitrary. If messages fit within the configured compaction-input budget, they should remain eligible for summarization instead of being dropped pre-emptively.

### 11.1 Priority classes

#### Must preserve

- current user objective
- latest explicit user instructions and constraints
- unresolved blockers
- important errors
- exact file paths and identifiers
- decisions that affect the next step
- changed files and high-signal read files

#### Usually summarize

- earlier user prompts
- assistant explanations
- assistant plans
- tool call intent
- prior partial progress

#### Usually omit

- repeated tool output
- raw file contents that were only read for inspection
- large shell logs
- repeated grep/list_dir/read_file output
- verbose reasoning text
- any streaming deltas
- raw base64 or binary payloads

When omission is required, the drop order should be biased toward **older** low-value material first. Recent high-signal material should be preserved longer than old exploratory context.

### 11.2 Reasoning policy

Reasoning may be useful, but it is frequently too expensive.

Rules:

- never include reasoning deltas
- never include provider-private/protected reasoning payloads
- prefer short reasoning summaries over raw reasoning text
- include reasoning only when it contains unique decisions not present elsewhere
- apply both a **per-item cap** and a **global reasoning budget**
- when reasoning pressure is high, drop reasoning entirely before sacrificing higher-signal state

Default policy should be **adaptive**, not “always include reasoning”.

### 11.3 Tool output policy

Tool outputs are the most common source of pathological compaction size.

Rules:

- do **not** include raw tool output by default
- extract only the small subset that is continuation-critical:
  - key error lines
  - diff summaries
  - explicit test/build result
  - exact values the assistant/user will need next
- replace bulky outputs with descriptors such as:
  - tool name
  - intent
  - touched files
  - approximate size
  - whether it succeeded or failed
- repeated tool activity should be collapsed into aggregate statements

When tool-output tradeoffs are required:

- recent tool outputs should be favored over older tool outputs
- recent modified-file context should be favored over old exploratory file reads
- recent failures and test/build outcomes should be favored over old successful noise

Example:

```text
Prefer:
- `grep` searched `src/CodeAlta.Agent/LocalRuntime/` for `compaction`
- `shell_command` produced a large build log; key error: `CS1591` in `Foo.cs`

Do not prefer:
- thousands of lines of raw grep/build output
```

## 12. Canonicalization and preprocessing

### 12.1 Canonical input only

Compaction input must be built from canonical finalized content:

- user messages
- finalized assistant text
- finalized reasoning or reasoning summary when retained
- finalized tool call metadata
- finalized tool results
- attachment descriptors
- previous checkpoint state

Never compact from transient UI deltas.

### 12.2 Event normalization

Before planning or summarizing, the runtime should normalize history into compactable units:

- user turn boundaries
- assistant message + attached tool calls
- tool results bound to their originating tool calls
- synthetic checkpoint messages
- attachment descriptors

The normalized representation should carry stable recency ordering so later reduction passes can preferentially preserve newer material.

### 12.3 Low-value collapse

The serializer must support collapsing repeated low-value activity, for example:

- multiple `read_file` calls into the same file
- repeated `grep` searches in the same directory
- repeated shell commands whose only important output is a final failure line
- repeated reasoning updates that converge to the same plan

## 13. Planning the compacted region

### 13.1 Protected regions

The planner must always keep:

1. system/developer instructions
2. the latest user-authored message when possible
3. a contiguous newest suffix

The retained suffix must be contiguous.

When additional trimming is necessary inside the summarized region, newer summarized units should be favored over older summarized units unless a specific older item is uniquely critical.

### 13.2 Split-turn compaction

If a single turn is too large:

- keep the latest user message verbatim when possible
- summarize the earlier assistant/tool portion of the turn
- keep the newest suffix verbatim

Split-turn compaction must preserve tool-call/tool-result adjacency.

The preferred bounded fallback order is:

1. try the preferred recent-suffix target first
2. replan against the wider resolved usable prompt budget when the preferred target is too tight
3. fall back to anchor-only retention when necessary
4. only then reduce an oversized latest-user anchor

### 13.3 Aggressive fallback order

If a normal plan still does not fit:

1. reduce the retained suffix while preserving the latest user anchor
2. drop adaptive reasoning retention
3. drop older non-critical summarized material before newer summarized material
4. drop older non-critical tool-output extracts before newer ones
5. drop old exploratory file-read context before newer changed-file context
6. switch to split-turn compaction
7. keep only checkpoint + latest user anchor
8. if the latest user anchor itself is too large, use oversized-item reduction
9. if it still cannot fit, fail clearly

The runtime must never loop indefinitely.

## 14. Oversized-item reduction

This section is mandatory for v2.

### 14.1 Oversized latest user message

If the latest user message by itself cannot fit in the usable prompt budget:

- the runtime must not fail immediately
- it must create an **anchor synopsis** of that one message
- the synopsis must preserve:
  - exact task statement when possible
  - explicit numbered requirements
  - file paths
  - code identifiers
  - exact literals/errors the user supplied
- the canonical event log must still retain the original message unchanged

Preferred v2 behavior:

1. try wider bounded replanning first so the raw anchor stays verbatim whenever physically possible
2. if it still cannot fit, reduce only that protected latest-user anchor into a compact synopsis
3. feed the synopsis into final checkpoint generation instead of replaying the full raw anchor

### 14.2 Oversized attachments or file selections

If a user input includes a file, selection, pasted log, or attachment that is itself too large:

- prefer preserving the reference to the file/attachment
- summarize the content in chunks
- record that the input was summarized because it exceeded context capacity

### 14.3 Recursive chunked summarization

If the summarization request input is too large for one model call:

1. split the material into chunks by semantic boundaries when possible
2. summarize each chunk into a short structured partial summary
3. carry a rolling prior summary forward across chunks
4. run a final bounded merge pass that reintroduces retained prefix/suffix context when needed

This is required for:

- very large sessions
- very large single turns
- very large initial prompts
- summarization of a previous checkpoint plus too much new history

The preferred merge pattern is:

1. summarize old history chunks first
2. accumulate a compact rolling summary
3. perform one final bounded merge call that combines the rolling summary with:
   - any retained split-turn prefix
   - the retained newest suffix
   - any oversized-anchor synopsis

## 15. Summarization input preparation

The summarizer must not receive old conversation history as a normal continuation.

It should instead receive explicit tagged content such as:

- `<compaction-request>`
- `<conversation>`
- `<previous-checkpoint>`
- `<retained-suffix>`
- `<retained-prefix>`
- `<oversized-anchor-synopsis>`

Messages should be serialized in labeled plain text such as:

- `[User]`
- `[Assistant]`
- `[Assistant reasoning summary]`
- `[Assistant tool calls]`
- `[Tool result summary]`
- `[Attachment]`

## 16. Serializer budget rules

The serializer must support both **per-item caps** and **global caps**.

Serializer reduction should be **progressive**:

1. start from the full canonical non-delta material selected for compaction
2. keep everything if it fits the summary-input budget
3. only then begin budget-driven omission and truncation
4. apply recency bias when selecting what to keep

### 16.1 Required caps

At minimum:

- tool-result text cap per item
- tool-result total cap across the whole summary request
- reasoning cap per item
- reasoning total cap across the whole summary request
- attachment/base64 exclusion
- total serialized compaction-input cap

Per-item caps alone are insufficient. Hundreds of individually truncated tool outputs can still create an enormous prompt.

### 16.2 Priority-aware truncation

When trimming is necessary, the serializer should discard in this order:

1. deltas and transient content
2. old raw tool outputs
3. old verbose reasoning
4. old repeated assistant prose
5. older already-resolved conversation details
6. newer low-value tool outputs
7. newer low-value reasoning

It should discard before it paraphrases more important state away.

## 17. Checkpoint content

The default checkpoint should be a terse structured handoff, not an essay.

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

- preserve exact paths, identifiers, commands, and critical error text
- record what changed and what remains
- omit repeated narrative filler
- prefer compact bullets
- include only unresolved or continuation-relevant context

### 17.1 Relevant files

The checkpoint should track at least:

- modified files
- read files that are still relevant to the current task

Read files that were only exploratory and are no longer relevant should be droppable.

When both are present, modified files should be presented before read-only exploratory context, and the runtime should prefer newer unique paths over older duplicates.

### 17.2 Iterative update

If a previous checkpoint exists:

- update it rather than rewriting from scratch
- preserve durable facts
- remove resolved blockers
- refresh next steps
- compress old summary drift when it grows stale

Iterative update must not cause monotonic summary bloat.

## 18. Checkpoint role

The active checkpoint must be replayed as a synthetic **user-context message**, not as a system message.

Recommended wrapper:

```text
<codealta-compaction-checkpoint version="2">
...
</codealta-compaction-checkpoint>
```

The wrapper version should advance when checkpoint semantics materially change.

## 19. Summary generation mode

### 19.1 Required mode

The normal path must use an **LLM summarizer call through the ordinary provider chat/generation API**.

Requirements:

- do not use provider-native remote compaction APIs
- disable tools for the summarizer
- use deterministic or low-variance settings where supported
- bound output tokens
- pass serialized/tagged input rather than replay history as chat

Implementation note:

- the configured compaction output cap should be propagated into the actual provider turn request, not just described in the prompt text

### 19.2 Model choice

Default:

- use the current provider
- use the current model unless a configured summarizer model is explicitly selected later

Future flexibility:

- a dedicated summarizer model may be allowed by configuration, but the baseline design must not depend on that

### 19.3 Failure handling

If summarization fails:

- leave session state unchanged
- fail cleanly for manual compaction
- skip threshold/post-response compaction and log diagnostics
- surface a clear error for overflow recovery if recovery cannot proceed

No silent heuristic-only fallback should replace the normal LLM compaction path.

## 20. Token accounting strategy

Priority order:

1. provider-reported current window usage
2. provider-reported last-operation usage plus trailing local estimate
3. model-specific tokenizer estimate
4. conservative heuristic estimate

The runtime should record the estimate source internally.

### 20.1 What must be counted

Count everything that is actually resent:

- system/developer instructions
- checkpoint message
- retained suffix
- user text
- assistant text
- retained reasoning content
- tool call names and arguments
- tool-result payload actually retained
- attachment placeholders

### 20.2 Do not trust stale usage blindly

Old usage snapshots must not be mistaken for current full-window truth after compaction or after additional local messages were added.

## 21. Persistence model

The canonical event log remains `events.jsonl`.

Compaction should persist a dedicated checkpoint event such as:

- `local.compactionCheckpoint`

The payload should include at least:

- schema version
- trigger reason
- summary text
- first kept boundary marker
- anchor identifier when present
- pre-compaction tokens
- post-compaction tokens
- summarized message count
- read files
- modified files
- serializer statistics:
  - omitted tool result count
  - omitted reasoning count
  - chunk count when recursive summarization was used
  - compression ratio
  - whether the latest anchor was reduced

`chunk count` should describe the recursive checkpoint-summarization work used to build the final checkpoint. It should not be inflated by separate oversized-anchor reduction calls.

## 22. Validation

### 22.1 Post-compaction fit validation

After generating the checkpoint, the runtime must rebuild the exact next prompt and validate it against resolved limits.

If it still does not fit:

- tighten the plan
- optionally regenerate a smaller checkpoint
- or fail clearly

If the rebuilt prompt fits but its realized compression ratio still exceeds the configured target ceiling because the retained suffix remains expensive, the runtime should:

- accept the result only when no stricter safe fallback remains
- record that the realized ratio exceeded the target ceiling in diagnostics/compaction messaging

### 22.2 Checkpoint quality validation

The runtime should reject or retry clearly bad checkpoints, for example:

- empty summary
- summary dominated by raw copied tool output
- summary that exceeds configured checkpoint caps
- summary missing all critical sections

## 23. Configuration

Use a global default block plus per-provider override blocks.

Recommended normalized shape:

```toml
[raw_api.compaction]
enabled = true
trigger_threshold = 0.80
reserved_output_tokens = 4096
reserved_overhead_tokens = 2048
keep_last_user_message = true
allow_split_turn = true

# New v2 controls
target_context_ratio_ideal = 0.03
target_context_ratio_max = 0.10
recent_suffix_target_tokens = 20000
summary_output_tokens = 1024
summary_input_tokens = 24000
tool_result_chars_per_item = 1200
tool_result_chars_total = 6000
reasoning_chars_per_item = 600
reasoning_chars_total = 3000
reasoning_mode = "adaptive" # none | adaptive | summary_only
max_chunk_passes = 4
allow_oversized_anchor_reduction = true
```

Per-provider overrides remain supported.

Notes:

- `recent_suffix_target_tokens` is the pi-mono-inspired “keep recent raw context” control and should guide how much verbatim recent context the planner tries to preserve
- `summary_input_tokens` should usually be set high enough that threshold compaction can summarize the full canonical non-delta selected history when it fits
- the larger defaults above do **not** mean serializer output should become verbose; global omission caps and recency-aware prioritization remain mandatory

Additional recommended policy flags:

```toml
prefer_recent_messages = true
prefer_recent_tool_outputs = true
drop_messages_only_when_summary_input_exceeds_budget = true
```

## 24. Edge cases that must be covered

At minimum:

1. no compaction when under threshold
2. threshold compaction when limits are known
3. unknown-limit behavior
4. threshold compaction keeps the full canonical non-delta history when it fits the summary-input budget
5. message dropping does not occur when the selected canonical history fits
6. recency bias prefers newer messages and newer tool outputs when reduction is necessary
7. latest user message preserved when it fits
8. latest user message reduced when it does not fit
9. split-turn compaction for an oversized turn
10. `allow_split_turn = false` blocks mid-turn cuts
11. tool-result adjacency is preserved
12. huge tool output does not dominate the checkpoint
13. verbose reasoning is budgeted or omitted
14. summarization request itself exceeds one call and is chunked
15. previous checkpoint iterative update does not grow unbounded
16. post-compaction fit validation replans or fails
17. overflow compact-and-retry once
18. provider last-operation usage plus trailing estimate is used when needed
19. per-provider overrides take precedence
20. no transient delta events participate
21. very large initial prompt or attachment is reducible
22. summary output token limit is applied to actual provider requests
23. automatic compaction does not silently downgrade to heuristic-only summary generation

## 25. Concrete v2 decisions

1. Compaction remains local and provider-agnostic.
2. Summary generation uses the regular chat/generation API, not remote compaction APIs.
3. The system optimizes for **very high compression**, not transcript fidelity.
4. Tool outputs are omitted by default unless continuation-critical.
5. Reasoning is adaptive and budgeted, never delta-based.
6. Per-item truncation is insufficient; global serializer budgets are mandatory.
7. Recursive chunked summarization is required for oversized histories and oversized single inputs.
8. Post-compaction validation is mandatory.
9. The latest user message remains a protected anchor whenever possible.
10. The target post-compaction total context should usually land between `1%` and `6%`, with `10%` as the normal upper planning bound.
11. Threshold-triggered compaction should keep the full canonical non-delta selected history when it fits the summarizer-input budget.
12. The default tuning should be inspired by pi-mono's order of magnitude for reserved/recent budgets (`~16k` reserve, `~20k` recent), while keeping CodeAlta's stricter omission-first and global-cap design.
13. When reduction is required, newer messages and newer high-signal tool context should be favored over older exploratory material.

## 26. Summary

CodeAlta should treat compaction as a **lossy state distillation** pipeline:

- canonicalize the history
- keep only the current objective and critical working state
- aggressively discard verbose low-value tool and reasoning bulk
- chunk when a single pass cannot fit
- generate a tightly bounded structured checkpoint
- validate that the rebuilt prompt is both safe and small

That is the standard required for long-lived raw-API coding sessions.
