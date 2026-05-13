# Agent Event Stream Unification

Status: Proposal  
Audience: implementers of `CodeAlta.Agent`, `CodeAlta.Agent.Codex`, `CodeAlta.Agent.Copilot`, and `CodeAlta` terminal UI

## Summary

The current `AgentEvent` abstraction is too small for the UI we want to build.

Today we normalize only:

- assistant text delta
- assistant final text
- session idle
- error
- raw fallback

That is enough for a minimal chat transcript. It is not enough for a coding-agent UI.

Both backends expose much richer signals:

- reasoning
- plan updates
- tool lifecycle
- command output
- file-change lifecycle
- usage/token updates
- session notices/warnings
- model/mode changes
- context compaction
- subagent/skill/hook activity
- action-required states such as approvals and user input

If we do not normalize these, the UI cannot explain what the agent is doing. That weakens trust and makes the coding workflow feel opaque.

This proposal recommends a **small but expressive normalized event model** that can cover most Copilot and Codex signals without exploding into dozens of public event types.

## Current Problem

Current `CodeAlta.Agent` events only model:

- `AgentAssistantMessageDeltaEvent`
- `AgentAssistantMessageEvent`
- `AgentSessionIdleEvent`
- `AgentErrorEvent`
- `AgentRawEvent`

This has three concrete drawbacks:

1. **Missing UI feedback**
   - reasoning is lost
   - tool progress is lost
   - plan updates are lost
   - usage is lost
   - warnings/info are lost

2. **Backend parity is artificially low**
   - Copilot already emits many session events
   - Codex already emits many notifications and item lifecycle signals
   - the abstraction hides that overlap instead of using it

3. **Action-required states are not first-class timeline events**
   - approvals and user input are handled as callbacks/server requests
   - the UI has no normalized event saying “the agent is waiting for you”

## Backend Inventory

### Copilot SDK categories

Representative events from `C:\code\github\copilot-sdk\dotnet\src\Generated\SessionEvents.cs`:

| Category | Representative event types | Useful fields |
|---|---|---|
| Assistant content | `AssistantMessageDeltaEvent`, `AssistantMessageEvent`, `AssistantIntentEvent`, `AssistantTurnStartEvent`, `AssistantTurnEndEvent` | `MessageId`, `DeltaContent`, `Content`, `Intent`, `TurnId`, `InteractionId`, `Phase`, `ParentToolCallId` |
| Reasoning | `AssistantReasoningDeltaEvent`, `AssistantReasoningEvent` | `ReasoningId`, `DeltaContent`, `Content` |
| Tool lifecycle | `ToolUserRequestedEvent`, `ToolExecutionStartEvent`, `ToolExecutionProgressEvent`, `ToolExecutionPartialResultEvent`, `ToolExecutionCompleteEvent` | `ToolCallId`, `ToolName`, `Arguments`, `ProgressMessage`, `PartialOutput`, `Success`, `Result`, `Error`, `ParentToolCallId`, `McpServerName`, `McpToolName` |
| Planning | `SessionPlanChangedEvent` | `Operation` (`create`, `update`, `delete`) |
| Session notices/state | `SessionStartEvent`, `SessionResumeEvent`, `SessionIdleEvent`, `SessionInfoEvent`, `SessionWarningEvent`, `SessionModelChangeEvent`, `SessionModeChangedEvent`, `SessionContextChangedEvent`, `SessionTitleChangedEvent`, `SessionTaskCompleteEvent`, `AbortEvent` | context, selected model, message text, warning type, model/mode transitions, summary |
| Usage/limits | `AssistantUsageEvent`, `SessionUsageInfoEvent` | token counts, cost, duration, model, quota snapshots, current tokens, token limit |
| Compaction/history | `SessionCompactionStartEvent`, `SessionCompactionCompleteEvent`, `SessionTruncationEvent`, `SessionSnapshotRewindEvent`, `SessionShutdownEvent` | success, token deltas, summary content, removed messages/tokens, shutdown reason |
| Workspace/handoff | `SessionWorkspaceFileChangedEvent`, `SessionHandoffEvent` | `Path`, `Operation`, handoff time, source type, summary, remote session id |
| Nested work | `SubagentSelectedEvent`, `SubagentStartedEvent`, `SubagentCompletedEvent`, `SubagentFailedEvent`, `SkillInvokedEvent`, `HookStartEvent`, `HookEndEvent` | agent/tool names, descriptions, hook type, success/error |
| System/user echo | `SystemMessageEvent`, `UserMessageEvent`, `PendingMessagesModifiedEvent` | message content, role, attachments, transformed prompt |

Important note: Copilot **approvals** and **user input** are not emitted as `SessionEvent`s. They arrive via `OnPermissionRequest` and `OnUserInputRequest` callbacks.

### Codex categories

Representative signals from `src/CodeAlta.CodexSdk/CodexNotification.cs`, generated DTOs, and `src/CodeAlta.Agent.Codex/CodexAgentMapper.cs`:

| Category | Representative notifications/items | Useful fields |
|---|---|---|
| Assistant content | `AgentMessageDelta`, completed `ThreadItem.AgentMessageThreadItem` | `ItemId`, `TurnId`, `Delta`, final `Text`, optional `Phase` (`commentary`, `final_answer`) |
| Reasoning | `ReasoningSummaryTextDelta`, `ReasoningSummaryPartAdded`, `ReasoningTextDelta`, completed `ThreadItem.ReasoningThreadItem` | `ItemId`, `SummaryIndex`, `ContentIndex`, delta text, final `Summary`, final `Content` |
| Planning | `PlanDelta`, `TurnPlanUpdated`, completed `ThreadItem.PlanThreadItem` | `ItemId`, delta text, full step list, explanation, final plan text |
| Turn/session lifecycle | `TurnStarted`, `TurnCompleted`, `ThreadStarted`, `ThreadStatusChanged`, `ThreadClosed`, `ServerRequestResolved` | `Turn.Id`, `Turn.Status`, thread status, active flags |
| Tool lifecycle | `ItemStarted` / `ItemCompleted` over `McpToolCallThreadItem`, `DynamicToolCallThreadItem`, `CollabAgentToolCallThreadItem` | tool name, arguments, status, success, result, error, duration |
| Command/file lifecycle | `CommandExecutionOutputDelta`, `CommandExecutionTerminalInteraction`, `ItemStarted` / `ItemCompleted` over `CommandExecutionThreadItem`, `FileChangeThreadItem` | command, cwd, output delta, stdin interaction, aggregated output, exit code, patch status |
| Other work items | `WebSearchThreadItem`, `ImageViewThreadItem`, `ImageGenerationThreadItem`, `EnteredReviewModeThreadItem`, `ExitedReviewModeThreadItem`, `ContextCompactionThreadItem` | query/path/result/status/review text |
| Usage/limits | `ThreadTokenUsageUpdated`, `ThreadCompacted` | per-turn and total token usage, context window |
| Notices/errors | `Error`, `ModelRerouted`, `ConfigWarning`, `DeprecationNotice`, `AccountRateLimitsUpdated`, `WindowsWorldWritableWarning`, `ThreadRealtimeError` | message text, rerouted model, retry flag, rate limits, warning details |

Important note: Codex **approvals**, **tool user input**, and **dynamic tool calls** are not notifications. They arrive as server requests:

- `item/commandExecution/requestApproval`
- `item/fileChange/requestApproval`
- `item/tool/requestUserInput`
- `item/tool/call`

## Overlap Between Backends

There is enough overlap to justify a real shared abstraction.

### Strong overlap

- assistant text streaming/final
- reasoning streaming/final
- plan updates
- tool lifecycle
- usage reporting
- lifecycle state changes
- notices/warnings/errors
- context compaction / truncation-like activity

### Partial overlap

- subagents, hooks, skills
  - Copilot has explicit events
  - Codex has related concepts via thread items and collaboration tools, but not identical shapes

- workspace/file changes
  - Copilot has explicit workspace-file event
  - Codex exposes file-change items and diff updates instead

- approvals/user input
  - both support them
  - neither exposes them as ordinary session-stream events

### Backend-specific tails

We should not try to erase every backend difference.

The abstraction should intentionally cover:

- **high-value shared signals**
- **UI-relevant progress**
- **action-required states**

Everything else should still fall back to `AgentRawEvent`.

## Recommendation

Do **not** model every backend event as its own public `AgentEvent` subclass. That would create a large, fragile API.

Do **not** collapse everything into one mega-event with many nullable fields. That would destroy invariants and make consumers harder to write.

Use a **small family of normalized event records**, each with a focused enum.

## Proposed Normalized Model

### 1. Content events

Use content events for text that the user may want to read inline in the timeline.

Recommended kinds:

- `Assistant`
- `Reasoning`
- `Plan`
- `System`
- `Info`
- `Warning`

Recommended shape:

- `Kind`
- `ContentId` (message id / reasoning id / item id)
- `ParentContentId` (optional)
- `Text` or `Delta`
- `RunId`
- `IsFinal`
- `BackendMetadata` (`JsonElement?`, optional)

Rationale:

- Copilot reasoning and assistant streams fit naturally.
- Codex reasoning/plan deltas also fit.
- Copilot `session.info`, `session.warning`, `system.message` become readable timeline entries instead of raw JSON.

### 2. Operation events

Use operation events for work units that are not “chat text”, but are important for progress visibility.

Recommended kinds:

- `Tool`
- `Command`
- `FileChange`
- `Search`
- `Image`
- `Subagent`
- `Skill`
- `Hook`
- `ContextCompaction`
- `ReviewMode`
- `Other`

Recommended phases:

- `Requested`
- `Started`
- `Progress`
- `PartialOutput`
- `Completed`
- `Failed`
- `Declined`
- `Canceled`
- `Selected`
- `Deselected`

Recommended shape:

- `OperationId`
- `ParentOperationId`
- `Kind`
- `Phase`
- `Title` / `DisplayName`
- `Message`
- `Arguments`
- `Result`
- `Success`
- `RunId`
- `BackendMetadata`

Rationale:

- Copilot tool/subagent/hook/skill activity can be shown consistently.
- Codex command execution, MCP tool calls, dynamic tool calls, file changes, web search, image operations, and collaboration tools fit here.
- The UI can render a single progress lane for “agent is doing work”.

### 3. Plan events

Planning is important enough to model explicitly instead of burying it in generic content.

Recommended shape:

- `PlanId`
- `Kind` (`Snapshot`, `Delta`, `Cleared`)
- `Steps`
- `Explanation`
- `Delta`
- `RunId`

Rationale:

- Copilot has coarse `create/update/delete`.
- Codex has both step snapshots and text deltas.
- The UI can show plan cards and live plan updates consistently.

### 4. State events

Use state events for lifecycle and environment transitions.

Recommended kinds:

- `SessionStarted`
- `SessionResumed`
- `RunStarted`
- `RunCompleted`
- `Idle`
- `ModelChanged`
- `ModeChanged`
- `TitleChanged`
- `ContextChanged`
- `Handoff`
- `TaskCompleted`
- `Abort`
- `Shutdown`
- `Truncation`
- `SnapshotRewind`

Recommended shape:

- `Kind`
- `Message`
- `PreviousValue`
- `NewValue`
- `Details`
- `RunId`

Rationale:

- Copilot emits many session-level transitions directly.
- Codex emits some directly and some indirectly through turn/thread notifications.
- The UI can use these for badges, banners, and status bars without inspecting raw payloads.

### 5. Usage events

Use usage events for token/cost/window information.

Recommended shape:

- `Window`
  - `CurrentTokens`
  - `TokenLimit`
  - `MessageCount`
  - `Label`
- `LastOperation`
  - `Model`
  - `InputTokens`
  - `OutputTokens`
  - `CacheReadTokens`
  - `CacheWriteTokens`
  - `CachedInputTokens`
  - `ReasoningTokens`
  - `Cost`
  - `DurationMs`
  - `Initiator`
  - `ParentToolCallId`
  - `ReasoningEffort`
  - `Label`
- `RateLimits`
  - `Name`
  - `PlanType`
  - `Primary`
  - `Secondary`
  - `Label`
- `Scope`
- `Source`
- `UpdatedAt`
- `Details`

Rationale:

- Copilot exposes both model-call usage and session window usage.
- Codex exposes thread/turn token usage.
- The UI can show live token pressure and post-run usage summaries.
- The footer can stay a simple `ctx --` / `ctx N tok` / `ctx NN%` view while the popup renders compaction pressure against the active input-token limit, with context-window/max-output limits as indicative metadata plus limits, quotas, and backend-specific detail sections.

### 6. Action-required events

This is the most important addition for user trust.

Approvals and user input should remain request/response APIs, but the adapters should also emit **synthetic timeline/state events** so the UI knows the agent is blocked on the user.

Recommended kinds:

- `PermissionRequested`
- `PermissionResolved`
- `UserInputRequested`
- `UserInputResolved`

Recommended shape:

- `Kind`
- `RequestId`
- `Title`
- `Message`
- `Details`
- `ResolvedBy`
- `Resolution`

Rationale:

- Both backends already support these interactions.
- Neither backend gives the UI a consistent event stream for them.
- Emitting synthetic normalized events from the adapters fixes that.

### 7. Keep `AgentErrorEvent` and `AgentRawEvent`

These still matter:

- `AgentErrorEvent` for failures the UI should always surface
- `AgentRawEvent` for backend-specific tails and forward compatibility

## Suggested Public API Shape

The exact C# names can change, but the public surface should look approximately like this:

- `AgentContentDeltaEvent`
- `AgentContentEvent`
- `AgentOperationEvent`
- `AgentPlanEvent`
- `AgentStateEvent`
- `AgentUsageEvent`
- `AgentActionRequiredEvent`
- `AgentErrorEvent`
- `AgentRawEvent`

Recommended enums:

- `AgentContentKind`
- `AgentOperationKind`
- `AgentOperationPhase`
- `AgentPlanEventKind`
- `AgentStateKind`
- `AgentUsageScope`
- `AgentActionRequiredKind`

## Mapping Guidance

### Copilot → normalized model

| Copilot event | Normalized event |
|---|---|
| `AssistantMessageDeltaEvent` | `AgentContentDeltaEvent(Assistant)` |
| `AssistantMessageEvent` | `AgentContentEvent(Assistant)` |
| `AssistantReasoningDeltaEvent` | `AgentContentDeltaEvent(Reasoning)` |
| `AssistantReasoningEvent` | `AgentContentEvent(Reasoning)` |
| `SessionPlanChangedEvent` | `AgentPlanEvent` |
| `ToolUserRequestedEvent` | `AgentOperationEvent(Requested, Tool)` |
| `ToolExecutionStartEvent` | `AgentOperationEvent(Started, Tool)` |
| `ToolExecutionProgressEvent` | `AgentOperationEvent(Progress, Tool)` |
| `ToolExecutionPartialResultEvent` | `AgentOperationEvent(PartialOutput, Tool)` |
| `ToolExecutionCompleteEvent` | `AgentOperationEvent(Completed/Failed, Tool)` |
| `Subagent*Event` | `AgentOperationEvent(..., Subagent)` |
| `SkillInvokedEvent` | `AgentOperationEvent(Started/Completed, Skill)` or `AgentStateEvent` if rendered as a note |
| `HookStartEvent` / `HookEndEvent` | `AgentOperationEvent(..., Hook)` |
| `SystemMessageEvent` | `AgentContentEvent(System)` |
| `SessionInfoEvent` | `AgentContentEvent(Info)` |
| `SessionWarningEvent` | `AgentContentEvent(Warning)` |
| `SessionUsageInfoEvent`, `AssistantUsageEvent` | `AgentUsageEvent` |
| `SessionCompaction*`, `SessionTruncationEvent`, `SessionSnapshotRewindEvent` | `AgentStateEvent` or `AgentOperationEvent(ContextCompaction)` |
| `SessionStart/Resume/Idle/...` | `AgentStateEvent` |
| permission/user-input callbacks | synthetic `AgentActionRequiredEvent` |

### Codex → normalized model

| Codex signal | Normalized event |
|---|---|
| `AgentMessageDelta` | `AgentContentDeltaEvent(Assistant)` |
| completed `AgentMessageThreadItem` | `AgentContentEvent(Assistant)` |
| `ReasoningSummaryTextDelta`, `ReasoningTextDelta` | `AgentContentDeltaEvent(Reasoning)` |
| completed `ReasoningThreadItem` | `AgentContentEvent(Reasoning)` |
| `PlanDelta` | `AgentPlanEvent(Delta)` or `AgentContentDeltaEvent(Plan)` |
| `TurnPlanUpdated` | `AgentPlanEvent(Snapshot)` |
| completed `PlanThreadItem` | `AgentContentEvent(Plan)` |
| `ItemStarted` / `ItemCompleted` for command/tool/file/search/image/review/collab/context-compaction items | `AgentOperationEvent` |
| `CommandExecutionOutputDelta` | `AgentOperationEvent(PartialOutput, Command)` |
| `CommandExecutionTerminalInteraction` | `AgentOperationEvent(Requested/Progress, Command)` |
| `McpToolCallProgress` | `AgentOperationEvent(Progress, Tool)` |
| `ThreadTokenUsageUpdated` | `AgentUsageEvent` |
| `TurnStarted`, `TurnCompleted`, `ThreadStarted`, `ThreadStatusChanged`, `ThreadClosed`, `ModelRerouted` | `AgentStateEvent` |
| `Error` | `AgentErrorEvent` |
| approval/user-input server requests | synthetic `AgentActionRequiredEvent` |

## Why This Is the Best Tradeoff

This gives the UI enough signal to be useful without copying backend protocols 1:1.

### Benefits

- reasoning becomes visible
- plan updates become visible
- tool/command progress becomes visible
- approval/user-input waiting states become visible
- usage and token pressure become visible
- the UI can render one consistent timeline across both backends

### Costs

- `CodeAlta.Agent` event model becomes richer
- adapters must do more mapping work
- UI logic must branch on a few enums instead of only four event types

That tradeoff is worth it. The current model is too lossy.

## Impact on Existing Code

### Files that will change

- `src/CodeAlta.Agent/AgentEvent.cs`
- `src/CodeAlta.Agent.Codex/CodexAgentMapper.cs`
- `src/CodeAlta.Agent.Codex/CodexAgentSession.cs`
- `src/CodeAlta.Agent.Copilot/CopilotAgentMapper.cs`
- `src/CodeAlta.Agent.Copilot/CopilotAgentSession.cs`
- `src/CodeAlta/TerminalUi/CodeAltaTerminalUi.cs`
- `src/CodeAlta.Tests/CodexAgentMapperTests.cs`
- `src/CodeAlta.Tests/CopilotAgentMapperTests.cs`
- `src/CodeAlta.Tests/ChatAgentConnectionTests.cs`
- UI tests and live smoke tests
- `doc/specs/agent_api_specs.md`

### Expected code impact

- `AgentHub` and `ChatAgentConnection` likely need little or no structural change
- most churn is in adapter mapping and UI rendering
- tests will need new assertions for richer event flows

## Migration Strategy

Because CodeAlta is still pre-release, prefer a **bounded breaking redesign** rather than a long-lived duplicate event model.

Recommended rollout:

### Step 1 — redesign `CodeAlta.Agent` event types

- replace the minimal event family with the normalized model above
- keep `AgentErrorEvent` and `AgentRawEvent`
- add enums first

### Step 2 — implement Codex mapping first

Reason:

- Codex already has explicit item types and deltas for many categories
- it is the richer source for operation-style mapping

Implementation order:

1. assistant / reasoning / plan
2. command / file / tool / search / image / collab item lifecycle
3. usage
4. state
5. synthetic action-required events from server requests

### Step 3 — implement Copilot mapping

Implementation order:

1. assistant / reasoning
2. tool lifecycle
3. notices/state/usage
4. subagent/skill/hook
5. synthetic action-required events from callbacks

### Step 4 — update the terminal UI renderer

Recommended rendering model:

- assistant/reasoning/system/info/warning → markdown/text timeline entries
- plan snapshots/deltas → dedicated “plan” block
- operation events → progress/status rows with expandable details
- action-required events → blocking prompt/status card
- usage/state events → footer/status lane or compact badges

### Step 5 — refresh tests

Add or update:

- mapper unit tests for both backends
- live Copilot/Codex smoke tests
- `ChatAgentConnection` tests
- terminal UI rendering tests for reasoning, plan, tool progress, and action-required states

## Concrete Implementer Instructions

1. Start in `src/CodeAlta.Agent/AgentEvent.cs`.
2. Introduce the new event families and enums first.
3. Update `src/CodeAlta.Agent.Codex/CodexAgentMapper.cs` to emit normalized events for:
   - assistant
   - reasoning
   - plan
   - command execution
   - file change
   - MCP/dynamic/collab tool calls
   - usage
   - state transitions
4. Update `src/CodeAlta.Agent.Codex/CodexAgentSession.cs` to emit synthetic `AgentActionRequiredEvent` values around approval and user-input request handling.
5. Update `src/CodeAlta.Agent.Copilot/CopilotAgentMapper.cs` to emit normalized events for:
   - assistant
   - reasoning
   - tool lifecycle
   - notices/state
   - usage
   - subagent/skill/hook activity
6. Update `src/CodeAlta.Agent.Copilot/CopilotAgentSession.cs` to emit synthetic `AgentActionRequiredEvent` values around permission and user-input callbacks.
7. Rewrite `src/CodeAlta/TerminalUi/CodeAltaTerminalUi.cs` event handling so it no longer assumes only assistant/error/idle.
8. Update mapper tests before UI polish.
9. Only after both adapters are emitting the richer events, update `doc/specs/agent_api_specs.md` so the spec matches the new contract.

## Implementation Notes

- Keep `AgentRawEvent` for unmapped or newly introduced backend events.
- Prefer stable correlation ids:
  - Copilot: `MessageId`, `ReasoningId`, `ToolCallId`, `TurnId`
  - Codex: `ItemId`, `TurnId`
- Do not mix action-request callbacks into raw JSON only. Emit normalized synthetic events.
- For backend-specific payload details, attach `JsonElement` metadata rather than adding backend-specific properties to every event type.
- For Codex command/file/tool completion, prefer the completed `ThreadItem` payload as the authoritative final snapshot.
- For Copilot plan updates, do not pretend the SDK gives structured plan steps; map them as coarse plan lifecycle until the backend exposes more detail.

## Bottom Line

CodeAlta should move from a **chat-only event abstraction** to a **timeline/event-feed abstraction**.

That is the right shape for a coding agent UI.

It gives us:

- readable assistant output
- visible reasoning
- visible work/progress
- visible waits/blockers
- visible usage/state

and it still leaves room for backend-specific raw events when needed.
