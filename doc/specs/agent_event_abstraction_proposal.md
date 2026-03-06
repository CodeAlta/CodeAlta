# Agent Event Abstraction Proposal (Codex + Copilot)

Status: **Proposal**  
Audience: implementers of `CodeAlta.Agent`, `CodeAlta.Agent.Copilot`, `CodeAlta.Agent.Codex`, and terminal/UI surfaces.

## 1. Problem

`CodeAlta.Agent` currently normalizes only a very small event surface:

- assistant message delta
- assistant message final
- session idle
- error
- raw event

That is enough for a basic chat transcript, but it is not enough for a coding-agent UI.

Both backends already emit much richer signals:

- reasoning text
- plan updates
- tool start/progress/finish
- command and file-change output
- usage / compaction / model reroute
- warnings / info / notices
- subagent / collaboration signals
- approval and user-input requests

Today, most of that data is discarded into `AgentRawEvent`, which means the UI cannot render it consistently and cannot give the user a good sense of what the agent is doing.

## 2. Current State

### 2.1 Current shared abstraction

`src/CodeAlta.Agent/AgentEvent.cs` currently exposes:

- `AgentAssistantMessageDeltaEvent`
- `AgentAssistantMessageEvent`
- `AgentSessionIdleEvent`
- `AgentErrorEvent`
- `AgentRawEvent`

This is the limiting factor today.

### 2.2 Current Copilot mapping

`src/CodeAlta.Agent.Copilot/CopilotAgentMapper.cs` currently normalizes only:

- `AssistantMessageDeltaEvent`
- `AssistantMessageEvent`
- `SessionIdleEvent`
- `SessionErrorEvent`

Everything else from `C:\code\github\copilot-sdk\dotnet\src\Generated\SessionEvents.cs` becomes `AgentRawEvent`.

Important unmapped Copilot categories already available in the SDK:

- **Reasoning**: `AssistantReasoningDeltaEvent`, `AssistantReasoningEvent`
- **Planning**: `SessionPlanChangedEvent`
- **Tool lifecycle**: `ToolUserRequestedEvent`, `ToolExecutionStartEvent`, `ToolExecutionPartialResultEvent`, `ToolExecutionProgressEvent`, `ToolExecutionCompleteEvent`
- **Usage / compaction**: `SessionUsageInfoEvent`, `AssistantUsageEvent`, `SessionCompactionStartEvent`, `SessionCompactionCompleteEvent`
- **Session notices**: `SessionInfoEvent`, `SessionWarningEvent`, `SessionModelChangeEvent`, `SessionModeChangedEvent`, `SystemMessageEvent`
- **Subagents / hooks / skills**: `Subagent*`, `Hook*`, `SkillInvokedEvent`

### 2.3 Current Codex mapping

`src/CodeAlta.Agent.Codex/CodexAgentMapper.cs` currently normalizes only:

- `CodexNotification.AgentMessageDelta`
- `CodexNotification.ItemCompleted` when the item is `ThreadItem.AgentMessageThreadItem`
- `CodexNotification.TurnCompleted`
- `CodexNotification.Error`

Everything else becomes `AgentRawEvent`.

Important unmapped Codex categories already available in the SDK:

- **Reasoning**: `ReasoningTextDelta`, `ReasoningSummaryTextDelta`, `ReasoningSummaryPartAdded`, completed `ReasoningThreadItem`
- **Planning**: `PlanDelta`, `TurnPlanUpdated`, completed `PlanThreadItem`
- **Tool / operation lifecycle**: `ItemStarted` / `ItemCompleted` for `CommandExecutionThreadItem`, `FileChangeThreadItem`, `McpToolCallThreadItem`, `DynamicToolCallThreadItem`, `CollabAgentToolCallThreadItem`, `WebSearchThreadItem`, `ImageGenerationThreadItem`, etc.
- **Operation output**: `CommandExecutionOutputDelta`, `FileChangeOutputDelta`, `McpToolCallProgress`, `CommandExecutionTerminalInteraction`
- **Usage / compaction**: `ThreadTokenUsageUpdated`, `ThreadCompacted`, `ContextCompactionThreadItem`
- **Notices**: `ConfigWarning`, `DeprecationNotice`, `ModelRerouted`, Windows warnings
- **Requests handled outside the event model**: approval requests, user-input requests, dynamic tool calls in `src/CodeAlta.Agent.Codex/CodexAgentSession.cs`

Codex is not the limiting backend. The shared abstraction is.

## 3. Design Goals

The abstraction should:

1. give the UI enough signal to explain what the agent is doing in real time
2. keep a reasonably consistent surface across Copilot and Codex
3. remain additive and low-risk for current consumers
4. preserve correlation identifiers so the UI can update the right row/block in place
5. treat subagents/collaboration as first-class activities, but not as nested transcripts by default
6. avoid forcing a 1:1 mapping for every backend-specific event
7. retain `AgentRawEvent` as a fallback

Non-goals:

- perfect parity for every backend-specific feature
- normalizing realtime audio / every OS-specific warning in the first pass
- replacing backend-specific request handlers for approval and user input

## 4. Options Considered

### Option A — many new specialized record types

Examples:

- `AgentReasoningDeltaEvent`
- `AgentPlanDeltaEvent`
- `AgentToolStartedEvent`
- `AgentToolProgressEvent`
- `AgentUsageEvent`
- etc.

Pros:

- strong typing
- easy to pattern-match

Cons:

- event surface grows very quickly
- hard to keep parity between backends
- UI code becomes a large switch over many cases

### Option B — one generic event with a big enum

Example shape:

```csharp
public sealed record AgentProgressEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentProgressKind Kind,
    AgentProgressPhase Phase,
    string? Id,
    string? ParentId,
    string? Text,
    JsonElement? Details);
```

Pros:

- very flexible
- easy for adapters to emit

Cons:

- payload becomes sparse quickly
- weak discoverability
- too much semantic meaning ends up in enums + `Details`

### Option C — small set of orthogonal event families (**recommended**)

Recommended families:

1. **Content events** — text streams and finalized text
2. **Activity events** — start/progress/complete for tools, commands, subagents, hooks, etc.
3. **Session update events** — warnings, usage, model changes, compaction, plan snapshots
4. **Interaction events** — approval/user-input request lifecycle
5. **Raw events** — fallback

This gives a consistent UI model without exploding the number of concrete event types or forcing everything through one giant generic payload.

## 5. Recommended Event Model

## 5.1 Keep the current events

Do **not** remove the current event records in the first pass:

- `AgentAssistantMessageDeltaEvent`
- `AgentAssistantMessageEvent`
- `AgentSessionIdleEvent`
- `AgentErrorEvent`
- `AgentRawEvent`

They are already used by `CodeAltaTerminalUi` and tests.

Instead, add richer event families alongside them.

## 5.2 Add a content family

Recommended enums:

```csharp
public enum AgentContentKind
{
    Assistant,
    Reasoning,
    ReasoningSummary,
    Plan,
    CommandOutput,
    FileChangeOutput,
    ToolOutput,
    Notice
}
```

Recommended records:

```csharp
public sealed record AgentContentDeltaEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentContentKind Kind,
    string ContentId,
    string? ParentActivityId,
    string Delta)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);

public sealed record AgentContentCompletedEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentContentKind Kind,
    string ContentId,
    string? ParentActivityId,
    string Content)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);
```

Why:

- assistant, reasoning, plan, and operation output are all “renderable text”
- the UI can render them in one timeline component with different visual styles
- `ContentId` lets the UI update a specific streaming block

## 5.3 Add an activity family

Recommended enums:

```csharp
public enum AgentActivityKind
{
    Turn,
    ToolCall,
    CommandExecution,
    FileChange,
    McpToolCall,
    DynamicToolCall,
    CollabAgentToolCall,
    Subagent,
    Hook,
    Skill,
    Compaction,
    WebSearch,
    ImageGeneration
}

public enum AgentActivityPhase
{
    Requested,
    Started,
    Progressed,
    Completed,
    Failed,
    Canceled,
    Selected,
    Deselected
}
```

Recommended record:

```csharp
public sealed record AgentActivityEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentActivityKind Kind,
    AgentActivityPhase Phase,
    string ActivityId,
    string? ParentActivityId,
    string? Name,
    string? Message,
    JsonElement? Details = null)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);
```

Why:

- Copilot tool/subagent/hook events and Codex item lifecycle all fit naturally here
- the UI can show progress rows without needing backend-specific branches
- `ActivityId` is the stable key for updating one row over time

## 5.4 Add a session update family

Recommended enums:

```csharp
public enum AgentSessionUpdateKind
{
    Info,
    Warning,
    ModelChanged,
    ModeChanged,
    TitleChanged,
    ContextChanged,
    PlanUpdated,
    UsageUpdated,
    CompactionStarted,
    CompactionCompleted,
    Handoff,
    Truncated,
    Shutdown,
    TaskCompleted,
    DiffUpdated
}
```

Recommended record:

```csharp
public sealed record AgentSessionUpdateEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentSessionUpdateKind Kind,
    string? Message,
    JsonElement? Details = null)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);
```

Why:

- these events usually affect the status bar, notices area, or metadata panels rather than the main assistant transcript
- they should still be available to UIs and logs

## 5.5 Add an interaction family

Approval and user-input requests already exist as handler callbacks, but they should also produce timeline-visible events.

Recommended enums:

```csharp
public enum AgentInteractionKind
{
    PermissionRequest,
    PermissionResolved,
    UserInputRequest,
    UserInputResolved
}
```

Recommended record:

```csharp
public sealed record AgentInteractionEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentInteractionKind Kind,
    string InteractionId,
    string? Message,
    JsonElement? Details = null)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);
```

Why:

- Codex server requests already represent visible agent workflow
- Copilot request handlers are operationally equivalent, even when not emitted by the session stream
- the UI can show “agent is asking permission” or “agent requested user input” in a consistent way

## 5.6 Keep raw as the escape hatch

`AgentRawEvent` must remain.

Any event that does not map cleanly in the first pass should still flow through as raw rather than being lost.

## 6. Normalization Strategy

## 6.1 Core principle

Normalize only what is consistently useful for UI and orchestration:

- content streams
- activity lifecycle
- session updates
- interaction lifecycle

Everything else can remain raw until there is a clear UI need.

## 6.2 Mapping matrix

| Shared category | Copilot | Codex | Notes |
|---|---|---|---|
| Assistant content | `AssistantMessageDeltaEvent`, `AssistantMessageEvent` | `AgentMessageDelta`, completed `AgentMessageThreadItem` | already mapped today |
| Reasoning content | `AssistantReasoningDeltaEvent`, `AssistantReasoningEvent` | `ReasoningTextDelta`, `ReasoningSummaryTextDelta`, completed `ReasoningThreadItem` | Codex has separate summary/body channels |
| Plan content / snapshots | `SessionPlanChangedEvent` | `PlanDelta`, `TurnPlanUpdated`, completed `PlanThreadItem` | Codex has stronger explicit plan data |
| Tool lifecycle | `ToolUserRequestedEvent`, `ToolExecution*` | `ItemStarted` / `ItemCompleted` for tool-like `ThreadItem`s, `McpToolCallProgress` | Codex uses typed item lifecycle + deltas |
| Command output | `ToolExecutionPartialResultEvent` / tool result text | `CommandExecutionOutputDelta` | normalize as content, kind `CommandOutput` |
| File change output | tool result text when Copilot exposes it | `FileChangeOutputDelta` | normalize as content, kind `FileChangeOutput` |
| Usage / compaction | `SessionUsageInfoEvent`, `AssistantUsageEvent`, `SessionCompaction*` | `ThreadTokenUsageUpdated`, `ThreadCompacted`, `ContextCompactionThreadItem` | session updates |
| Notices | `SessionInfoEvent`, `SessionWarningEvent`, `SessionModelChangeEvent`, `SessionModeChangedEvent`, `SystemMessageEvent` | `ConfigWarning`, `DeprecationNotice`, `ModelRerouted`, Windows warnings | session updates |
| Subagents / collaboration | `Subagent*` | `CollabAgentToolCallThreadItem` lifecycle | useful for agentic UI |
| Interaction requests | request handlers | server requests in `CodexAgentSession` | should become `AgentInteractionEvent`s |

## 6.3 Correlation rules

The new abstraction should standardize correlation IDs.

### Content IDs

- Copilot:
  - assistant text → `messageId`
  - reasoning text → `reasoningId`
- Codex:
  - assistant/plan/reasoning/tool output → `itemId` when available
  - if only `turnId` exists, use `turnId` + content kind

### Activity IDs

- Copilot:
  - tool lifecycle → `toolCallId`
  - subagent lifecycle → `toolCallId`
  - hook lifecycle → `hookInvocationId`
  - assistant turn lifecycle → `turnId`
- Codex:
  - use `ThreadItem.Id` for item lifecycle
  - for turn-level concepts, use `turnId`

### Parent activity IDs

Use `ParentActivityId` when the backend provides it:

- Copilot: `parentToolCallId`, `interactionId`
- Codex: `turnId` is usually the parent for an item; collab/tool items may also contain explicit sender/receiver relationships

Without stable IDs, the UI will not be able to update the correct row in place.

## 7. Backend-Specific Mapping Notes

## 7.1 Copilot

### Straightforward mappings

- reasoning events map directly
- tool lifecycle maps directly
- subagent / hook / skill events map directly
- usage / compaction / warning / model change events map directly

### Copilot-specific details to preserve

- `AssistantMessageData.ToolRequests`
- `AssistantMessageData.ReasoningText`
- `ToolExecutionCompleteData.Result`
- `ToolExecutionCompleteData.Error`
- `SessionShutdownData`

These belong in `Details` for the first pass, not in raw-only storage.

### Events that can remain raw initially

- `PendingMessagesModifiedEvent`
- `SessionWorkspaceFileChangedEvent`
- `SessionForeground/Background` style lifecycle if later exposed through the client

## 7.2 Codex

### Key point

Codex does not emit a single generic “tool execution” family. Instead, it emits:

- typed item lifecycle (`ItemStarted` / `ItemCompleted`)
- specialized streaming notifications (`CommandExecutionOutputDelta`, `ReasoningTextDelta`, etc.)

The Codex adapter should use both.

### Important Codex mappings

- `ItemStarted` / `ItemCompleted` for `CommandExecutionThreadItem` → `AgentActivityEvent(CommandExecution, Started/Completed)`
- `CommandExecutionOutputDelta` → `AgentContentDeltaEvent(CommandOutput, ...)`
- `ItemStarted` / `ItemCompleted` for `FileChangeThreadItem` → `AgentActivityEvent(FileChange, Started/Completed)`
- `FileChangeOutputDelta` → `AgentContentDeltaEvent(FileChangeOutput, ...)`
- `ItemStarted` / `ItemCompleted` for `McpToolCallThreadItem` / `DynamicToolCallThreadItem` → `AgentActivityEvent(McpToolCall/DynamicToolCall, ...)`
- `McpToolCallProgress` → `AgentActivityEvent(..., Progressed, ...)`
- `ReasoningTextDelta` / `ReasoningSummaryTextDelta` → reasoning content deltas
- completed `ReasoningThreadItem` → reasoning content completed
- `PlanDelta` / `TurnPlanUpdated` / completed `PlanThreadItem` → plan updates
- `ThreadTokenUsageUpdated` / `ThreadCompacted` → session updates
- `ConfigWarning`, `DeprecationNotice`, `ModelRerouted` → session notices

### Requests currently handled outside the event stream

`CodexAgentSession.HandleServerRequestAsync(...)` should emit interaction events around:

- command approval request
- file change approval request
- tool user-input request
- dynamic tool call request / response

That change is important because these are visible workflow moments for users.

## 7.3 Subagent / collaboration treatment

Both backends appear to support delegated agent work:

- **Copilot** has explicit `SubagentSelectedEvent`, `SubagentStartedEvent`, `SubagentCompletedEvent`, `SubagentFailedEvent`, and `SubagentDeselectedEvent`.
- **Codex** already exposes collaboration-oriented structures such as `ThreadItem.CollabAgentToolCallThreadItem`, and the generated SDK surface also contains richer experimental collab event models in `EventMsg` (`collab_agent_spawn_*`, `collab_agent_interaction_*`, waiting/close events, etc.).

The shared abstraction should treat these as **activity rows**, not as normal assistant message streams.

### Recommended default UI behavior

Show subagents as summarized progress entries:

- selected
- started
- waiting
- completed
- failed

Optionally include:

- subagent display name / role
- short description
- target thread/session id when available
- final status / summary message

Do **not** attempt to merge the child agent transcript into the parent transcript in the first pass.

That matches the current Copilot UX more closely and keeps the parent session readable.

### Recommended abstraction treatment

- Copilot subagent events → `AgentActivityEvent(Kind = Subagent, ...)`
- Codex collaboration item lifecycle → `AgentActivityEvent(Kind = CollabAgentToolCall, ...)`
- experimental detailed collab events from Codex should remain optional/raw until the adapter surfaces them intentionally

If CodeAlta later wants a “drill down into child agent” experience, that should be a UI feature layered on top of these summarized activity events, not the default transcript model.

## 8. Recommended Scope for the First Implementation

## 8.1 Must-have in the first pass

Implement these first:

1. reasoning content
2. plan updates
3. tool / command / file-change lifecycle
4. usage / compaction / warnings / model change
5. interaction request events

That set covers the biggest current UI blind spots.

## 8.2 Defer to a second pass

These can stay raw initially:

- realtime audio
- fuzzy file search
- app list / account notifications
- image / review / web-search presentation details
- OS-specific warnings with no clear shared UI treatment

## 9. UI Impact

The current terminal UI is built around a single assistant text stream:

- one `_chatStreamingBuffer`
- one `_chatStreamingMarkdown`

That is not enough once reasoning and tool output are added.

### Recommended UI rendering model

Maintain a timeline state keyed by content/activity IDs:

- **assistant blocks** — markdown, expanded by default
- **reasoning blocks** — markdown, collapsed by default
- **tool/activity rows** — one row per activity, updated in place
- **subagent rows** — summarized activity rows, expandable but collapsed by default
- **session notices** — dim/info/warning rows
- **usage/model changes** — status bar or side panel, optionally transcript rows
- **approval/user-input cards** — interactive rows in the timeline

### Practical implication

`src/CodeAlta/TerminalUi/CodeAltaTerminalUi.cs` should move from “single current markdown stream” to “dictionary of live timeline entries”.

Without that change, interleaved assistant/reasoning/tool streams will overwrite each other or be dropped.

## 10. Impact on Existing Code

## 10.1 `CodeAlta.Agent`

Add new enums and records, ideally in separate files:

- `AgentContentEvent.cs`
- `AgentActivityEvent.cs`
- `AgentSessionUpdateEvent.cs`
- `AgentInteractionEvent.cs`

Keep the current event records unchanged.

## 10.2 `CodeAlta.Agent.Copilot`

Extend `CopilotAgentMapper.ToAgentEvent(...)` to emit:

- reasoning events
- tool lifecycle events
- usage / compaction / warning / model/session update events
- subagent / hook / skill events where useful

For approval and user-input callbacks, emit `AgentInteractionEvent`s from the session layer around the callback invocation.

## 10.3 `CodeAlta.Agent.Codex`

Extend `CodexAgentMapper.ToAgentEvent(...)` and `CodexAgentSession.HandleServerRequestAsync(...)` to emit:

- reasoning / plan / command / file/tool lifecycle events
- session updates
- interaction events for approval / user input / tool call handling

Most of the data is already present in `CodexNotification`, `ThreadItem`, and server request types.

## 10.4 `CodeAlta` terminal UI

Update `HandleChatAgentEvent(...)` to:

- route assistant content to assistant blocks
- route reasoning to collapsible reasoning blocks
- route activity events to progress rows
- route warnings/usage/model changes to notice/status surfaces
- route interaction events to explicit approval/input cards

## 10.5 Tests

Add or extend:

- adapter mapping unit tests for both backends
- live Copilot test covering reasoning/tool lifecycle
- live Codex test covering reasoning/tool/plan notifications
- terminal UI tests for multi-stream rendering keyed by content/activity IDs

## 11. Implementation Plan

### Phase 1 — Shared model

1. Add the new event families to `src/CodeAlta.Agent/`.
2. Keep the current event types intact.
3. Update `doc/specs/agent_api_specs.md` after the design settles.

### Phase 2 — Copilot adapter

1. Map reasoning events.
2. Map tool lifecycle events.
3. Map session warning/model/usage/compaction events.
4. Emit interaction events around permission/user-input callbacks.

### Phase 3 — Codex adapter

1. Map reasoning and plan notifications.
2. Map `ItemStarted` / `ItemCompleted` by `ThreadItem` type.
3. Map command/file/tool output deltas.
4. Emit interaction events around server requests.
5. Map notices, usage, and compaction.

### Phase 4 — UI

1. Replace single-stream assistant state with keyed timeline state.
2. Add renderers for assistant, reasoning, activity, notice, and interaction events.
3. Keep the current minimal assistant path working during migration.

### Phase 5 — Cleanup

1. Evaluate which existing “raw” events remain valuable.
2. Add more mappings only when they improve UI or orchestration.
3. Consider whether the old assistant-specific event records should later become convenience wrappers over the richer model.

## 12. Implementer Checklist

If you implement this proposal, proceed in this order:

1. extend `CodeAlta.Agent` with additive event types only
2. add correlation IDs (`ContentId`, `ActivityId`, `ParentActivityId`) from day one
3. map Copilot reasoning/tool/session-update events first
4. map Codex reasoning/plan/item-lifecycle events second
5. emit interaction events from session-layer request handling
6. update the terminal UI to use keyed timeline entries instead of a single streaming buffer
7. add unit tests before live tests
8. keep `AgentRawEvent` for anything not yet normalized

## 13. Recommendation

Do **not** move to a single giant “message with enum” model.

The better tradeoff is:

- a **small number of event families**
- **enums inside each family**
- **stable correlation IDs**
- **raw fallback for everything else**

That gives CodeAlta the best of both worlds:

- enough shared structure for a consistent UI
- enough flexibility to preserve rich backend-specific workflows
- a migration path that does not break the current assistant-only consumers
