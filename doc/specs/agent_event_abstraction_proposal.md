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

`src/CodeAlta.Agent.CopilotCli/CopilotAgentMapper.cs` currently normalizes only:

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
3. optimize for the cleanest long-term event model, even if it breaks the current abstraction
4. preserve correlation identifiers so the UI can update the right row/block in place
5. treat subagents/collaboration as first-class activities, but not as nested transcripts by default
6. avoid forcing a 1:1 mapping for every backend-specific event
7. retain `AgentRawEvent` as a fallback

Non-goals:

- perfect parity for every backend-specific feature
- normalizing realtime audio / every OS-specific warning in the first pass
- preserving the current assistant-only event model for compatibility
- replacing backend-specific request handlers for approval and user input

Compatibility policy:

- CodeAlta is private and still evolving
- preserving legacy event shapes is **not** a design goal
- it is acceptable to replace the current event surface if the result is cleaner and easier to use

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

However, a few high-value cases should still use **dedicated typed payloads or dedicated records** rather than a generic `JsonElement Details` blob:

- structured plans
- approval prompts
- user-input forms
- structured tool / file-change results

## 5. Recommended Event Model

## 5.1 Replace the current assistant-only event surface

Because CodeAlta is private/pre-release, the proposal should prefer the cleaner model over compatibility wrappers.

Recommended replacement:

- replace `AgentAssistantMessageDeltaEvent` with `AgentContentDeltaEvent(Kind = Assistant, ...)`
- replace `AgentAssistantMessageEvent` with `AgentContentCompletedEvent(Kind = Assistant, ...)`
- fold session idle into the session-update/lifecycle model instead of keeping a dedicated compatibility event
- keep `AgentErrorEvent` and `AgentRawEvent` only if they still make sense semantically, not for legacy reasons

In practice, this means the implementation can update `CodeAltaTerminalUi`, tests, and both adapters in one coordinated change set instead of carrying duplicate legacy event types.

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
    Started,
    Resumed,
    Idle,
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

## 5.6 Some messages need dedicated structured types

The event families above are the right top-level shape, but some payloads are too important to leave as generic text or `JsonElement`.

The rule should be:

- use the event families for **timeline semantics**
- use dedicated typed payloads for **messages that drive richer UI controls**

The most important cases are below.

### 5.6.1 Plans need a dedicated typed shape

Plan information exists in two different forms:

1. **plan text / streaming proposal text**
2. **structured plan snapshot with steps and statuses**

Those are not the same thing.

Codex already exposes a real structured plan snapshot:

- `TurnPlanUpdatedNotification.Plan` → list of `TurnPlanStep`
- `TurnPlanUpdatedNotification.Explanation`
- `TurnPlanStep.Status` → `pending`, `inProgress`, `completed`

Copilot is weaker here:

- `SessionPlanChangedEvent` currently exposes only an operation (`create`, `update`, `delete`)
- there is no equivalent step list in the session event stream

Because of that, `AgentContentKind.Plan` is not enough by itself.

Recommended addition:

```csharp
public sealed record AgentPlanSnapshotEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentPlanSnapshot Snapshot)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);

public sealed record AgentPlanSnapshot(
    AgentPlanChangeKind? ChangeKind,
    string? Explanation,
    IReadOnlyList<AgentPlanStep>? Steps);

public sealed record AgentPlanStep(
    string Text,
    AgentPlanStepStatus? Status);
```

Recommended mapping:

- **Codex**
  - `TurnPlanUpdated` → `AgentPlanSnapshotEvent` with `Steps` + `Explanation`
  - `PlanDelta` / completed `PlanThreadItem` → `AgentContent*Event(Kind = Plan, ...)`
- **Copilot**
  - `SessionPlanChangedEvent` → `AgentPlanSnapshotEvent` with `ChangeKind` only and no `Steps`

This gives the UI a proper “plan card” surface when step data exists, without pretending Copilot has more structure than it actually does.

### 5.6.2 User input needs a typed form model

The current `AgentUserInputRequest` shape is too weak for rich UI:

- it only carries `Id`, `Question`, `Choices`, `AllowFreeform`

That loses important Codex information:

- `Header`
- option descriptions
- `IsSecret`
- multi-question forms with per-question UI metadata

Backend detail:

- **Copilot** `UserInputRequest`
  - `Question`
  - `Choices`
  - `AllowFreeform`
- **Codex** `ToolRequestUserInputParams`
  - `ItemId`
  - `Questions[]`
  - each question has `Header`, `Id`, `Question`, `IsOther`, `IsSecret`, `Options[]`
  - each option has `Label`, `Description`

Recommended shared model:

```csharp
public sealed record AgentUserInputRequestedEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    string InteractionId,
    AgentUserInputForm Form)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);

public sealed record AgentUserInputForm(
    IReadOnlyList<AgentUserInputPrompt> Prompts);

public sealed record AgentUserInputPrompt(
    string Id,
    string Question,
    string? Header = null,
    IReadOnlyList<AgentUserInputOption>? Options = null,
    bool AllowFreeform = true,
    bool IsSecret = false);

public sealed record AgentUserInputOption(
    string Label,
    string? Description = null);
```

Recommended model update:

- keep `AgentUserInputRequestHandler`, but extend `AgentUserInputQuestion` or replace it with the richer prompt model above
- emit `AgentUserInputRequestedEvent` before invoking the handler
- emit `AgentInteractionEvent(UserInputResolved, ...)` after resolution

This is important because “pick from a list”, “freeform vs constrained”, and “secret input” are UI behavior, not incidental details.

### 5.6.3 Approval requests need typed payloads

The current `AgentPermissionRequest` is also too weak for approval UI:

```csharp
public sealed record AgentPermissionRequest(
    AgentBackendId BackendId,
    string SessionId,
    string Kind,
    JsonElement Raw);
```

That is operationally sufficient, but too lossy for UI that wants to show:

- command text
- working directory
- parsed command actions
- network host/protocol
- file-write scope / grant root
- reason text
- proposed policy amendments

Backend detail:

- **Copilot** `PermissionRequest`
  - `Kind`
  - `ToolCallId`
  - loose `ExtensionData`
- **Codex** `CommandExecutionRequestApprovalParams`
  - `ApprovalId`, `Command`, `CommandActions`, `Cwd`, `NetworkApprovalContext`, `Reason`, `ProposedExecpolicyAmendment`, `ProposedNetworkPolicyAmendments`
- **Codex** `FileChangeRequestApprovalParams`
  - `GrantRoot`, `Reason`

Recommended shared model:

```csharp
public sealed record AgentPermissionRequestedEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    string InteractionId,
    AgentPermissionPrompt Prompt)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);

public abstract record AgentPermissionPrompt(string Kind);

public sealed record AgentCommandPermissionPrompt(
    string? ApprovalId,
    string? Command,
    string? WorkingDirectory,
    IReadOnlyList<AgentCommandPreviewAction>? Actions,
    string? Reason,
    AgentNetworkAccessRequest? Network,
    IReadOnlyList<string>? ProposedExecPolicyAmendment,
    IReadOnlyList<AgentNetworkPolicyAmendment>? ProposedNetworkPolicyAmendments)
    : AgentPermissionPrompt("commandExecution");

public sealed record AgentFileChangePermissionPrompt(
    string? GrantRoot,
    string? Reason)
    : AgentPermissionPrompt("fileChange");
```

This should be a first-class shared contract, not a compatibility-only side channel.

### 5.6.4 Tool and file-change results may need structured result blocks

Simple text output fits `AgentContentDeltaEvent`.

Some result payloads do not:

- **Copilot** `ToolExecutionCompleteData.Result.Contents`
  - text
  - terminal
  - image
  - audio
  - resource link
  - resource
- **Codex** dynamic tool output content items
  - text
  - image
- **Codex** MCP tool call results
  - raw content blocks
  - `StructuredContent`
- **Codex** file changes
  - `FileChangeThreadItem.Changes[]` with path, diff, and change kind

Recommendation:

- do **not** create a giant “structured output” abstraction in the first pass
- do define a small optional typed result-block model for later UI work

Example:

```csharp
public abstract record AgentResultBlock
{
    public sealed record Text(string Value) : AgentResultBlock;
    public sealed record Terminal(string Text, int? ExitCode = null, string? Cwd = null) : AgentResultBlock;
    public sealed record Image(string MimeType, string DataOrUrl) : AgentResultBlock;
    public sealed record ResourceLink(string Name, string Uri, string? Description = null) : AgentResultBlock;
}
```

For v1, it is acceptable to keep most of this in `Details`, but the proposal should explicitly leave room for typed result blocks.

## 5.7 Keep raw as the escape hatch

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
| User input form | `UserInputRequest` callback | `ToolRequestUserInputParams` server request | needs a typed form model, not just text |
| Approval prompt | `PermissionRequest` callback | `CommandExecutionRequestApprovalParams`, `FileChangeRequestApprovalParams` | needs typed prompt data for UI |
| Structured tool result | `ToolExecutionCompleteData.Result.Contents` | dynamic tool output items, MCP structured content, file-change diffs | likely phase 2 typed result blocks |
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
- `UserInputRequest.Choices` / `AllowFreeform`
- `PermissionRequest.Kind` / `ToolCallId` / `ExtensionData`
- `ToolExecutionCompleteData.Result`
- `ToolExecutionCompleteData.Error`
- `SessionShutdownData`

These should not remain raw-only. Some can stay in `Details` for phase 1, but user-input forms and permission prompts should move to typed payloads.

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

### Important Codex structured payloads

Codex is the stronger backend for several structured UI cases:

- **Plans**
  - `TurnPlanUpdatedNotification.Plan` gives a real step list with status
  - `TurnPlanUpdatedNotification.Explanation` can be shown above the list
- **User input**
  - `ToolRequestUserInputQuestion.Header`
  - `ToolRequestUserInputQuestion.IsSecret`
  - `ToolRequestUserInputOption.Description`
- **Approvals**
  - command approval exposes command text, cwd, parsed command actions, network context, and policy amendments
  - file approval exposes grant root and reason
- **File changes**
  - `FileChangeThreadItem.Changes[]` already gives path, diff, and patch kind

These are the strongest argument for introducing typed payloads rather than relying only on `JsonElement Details`.

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
- **plan cards** — structured steps when `AgentPlanSnapshotEvent` is available, plain text otherwise
- **tool/activity rows** — one row per activity, updated in place
- **subagent rows** — summarized activity rows, expandable but collapsed by default
- **session notices** — dim/info/warning rows
- **usage/model changes** — status bar or side panel, optionally transcript rows
- **approval cards** — command/file/network-specific UI rather than generic JSON
- **user-input forms** — list/select/secret/freeform controls driven by typed prompt metadata

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
- `AgentPlanEvent.cs`
- `AgentPermissionPrompt.cs`
- `AgentUserInputForm.cs`
- optionally later `AgentResultBlock.cs`

Replace the current assistant-only event records where the richer model makes them redundant.

Also revisit these existing contracts:

- `src/CodeAlta.Agent/AgentUserInputRequest.cs`
- `src/CodeAlta.Agent/AgentPermissionRequest.cs`

They are currently too weak for richer interactive UI and should either be extended or complemented by new typed payload models.

## 10.2 `CodeAlta.Agent.Copilot`

Extend `CopilotAgentMapper.ToAgentEvent(...)` to emit:

- reasoning events
- tool lifecycle events
- usage / compaction / warning / model/session update events
- subagent / hook / skill events where useful
- structured plan snapshot events with operation-only payloads
- typed tool-result blocks when the UI is ready to consume them

For approval and user-input callbacks, emit `AgentInteractionEvent`s from the session layer around the callback invocation.

## 10.3 `CodeAlta.Agent.Codex`

Extend `CodexAgentMapper.ToAgentEvent(...)` and `CodexAgentSession.HandleServerRequestAsync(...)` to emit:

- reasoning / plan / command / file/tool lifecycle events
- session updates
- interaction events for approval / user input / tool call handling
- typed plan snapshot events
- typed approval prompts
- typed user-input forms
- optionally later typed file-change summaries / result blocks

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

Implementation status in the current private codebase:

- **Phase 1** is implemented.
- **Phase 2** is implemented.
- **Phase 3** is implemented.
- **Phase 4** is implemented with a markdown timeline renderer in the terminal UI.
- Remaining work is follow-up polish, broader live-test coverage, and any additional backend-specific mappings that prove useful in practice.

Important current UI behavior:

- reasoning, plan snapshots, activity rows, notices, approval requests, user-input requests, and resolved interactions now render in the terminal chat timeline
- approval and user-input requests are visible even when the terminal UI still answers them automatically (`deny` or empty responses)
- timeline entries are keyed by content/activity/interaction identifiers instead of one shared assistant stream
- the chat view now preserves its timeline state when switching away from and back to the chat screen

### Phase 1 — Shared model

1. Replace the current event model in `src/CodeAlta.Agent/` with the richer families.
2. Add typed payload contracts for plans, user-input forms, and approval prompts.
3. Update current consumers (`CodeAlta`, tests, adapters) in the same change set.
4. Update `doc/specs/agent_api_specs.md` after the design settles.

### Phase 2 — Copilot adapter

1. Map reasoning events.
2. Map tool lifecycle events.
3. Map session warning/model/usage/compaction events.
4. Emit operation-only plan snapshot events.
5. Emit interaction events around permission/user-input callbacks.

### Phase 3 — Codex adapter

1. Map reasoning and plan notifications.
2. Map `TurnPlanUpdated` into typed plan snapshots.
3. Map `ItemStarted` / `ItemCompleted` by `ThreadItem` type.
4. Map command/file/tool output deltas.
5. Emit typed interaction events around server requests.
6. Map notices, usage, and compaction.

### Phase 4 — UI

1. Replace single-stream assistant state with keyed timeline state.
2. Add renderers for assistant, reasoning, activity, notice, and interaction events.
3. Add dedicated renderers for plan cards, approval cards, and user-input forms.
4. Keep the current minimal assistant path working during migration.

### Phase 5 — Cleanup

1. Evaluate which existing “raw” events remain valuable.
2. Add more mappings only when they improve UI or orchestration.
3. Consider whether the old assistant-specific event records should later become convenience wrappers over the richer model.

## 12. Implementer Checklist

If you implement this proposal, proceed in this order:

1. replace the current `CodeAlta.Agent` event surface with the richer model
2. add correlation IDs (`ContentId`, `ActivityId`, `ParentActivityId`) from day one
3. introduce typed plan, approval, and input-form payloads before wiring the UI
4. map Copilot reasoning/tool/session-update events first
5. map Codex reasoning/plan/item-lifecycle events second
6. emit interaction events from session-layer request handling
7. update the terminal UI to use keyed timeline entries instead of a single streaming buffer
8. add unit tests before live tests
9. keep `AgentRawEvent` for anything not yet normalized

## 13. Recommendation

Do **not** move to a single giant “message with enum” model.

The better tradeoff is:

- a **small number of event families**
- **enums inside each family**
- **dedicated typed payloads for plans, approvals, and user-input forms**
- **stable correlation IDs**
- **raw fallback for everything else**

That gives CodeAlta the best of both worlds:

- enough shared structure for a consistent UI
- enough flexibility to preserve rich backend-specific workflows
- a cleaner event surface without compatibility-only wrappers
