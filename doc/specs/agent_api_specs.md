# Agent API Specs (Copilot SDK + Codex)

Status: **Draft** (implemented in `CodeAlta.Agent`, `CodeAlta.Agent.Codex`, `CodeAlta.Agent.Copilot`)  
Last updated: **2026-03-09**  
Primary references (local):
- GitHub Copilot SDK (.NET): `C:\code\github\copilot-sdk\dotnet\src`
- GitHub Copilot SDK docs: `C:\code\github\copilot-sdk\docs`
- Codex app-server protocol docs: `C:\code\codex\codex-rs\app-server\README.md`
- CodeAlta Codex SDK wrapper: `src/CodeAlta.CodexSdk/`

Related specs:
- `doc/specs/agent_instruction_templates_spec.md`

## 1. Problem statement

CodeAlta currently talks to the Codex app-server via `CodeAlta.CodexSdk`. Separately, GitHub offers an agentic runtime via `GitHub.Copilot.SDK` (Copilot CLI JSON-RPC).

We want a **shared .NET API** so that a single application can host multiple backends (e.g. Copilot + Codex), create agent conversations, stream events, handle tool calls/approvals/user-input, and generally treat both backends similarly for “most of the work”.

When a feature is backend-specific (or cannot be emulated efficiently), the design must:
- Expose a clear “escape hatch” to access the underlying backend objects.
- Document the mismatch and the recommended approach.

Instruction-template behavior for created sessions is defined separately in:

- `doc/specs/agent_instruction_templates_spec.md`

## 2. Goals / non-goals

### Goals
- A small, stable set of abstractions for:
  - backend lifecycle (start/stop),
  - session lifecycle (create/resume),
  - sending user input,
  - streaming agent events,
  - custom tool definitions/handlers,
  - approvals/permission requests,
  - user-input prompts from the agent/runtime.
- AOT/trimming friendly: avoid reflection-heavy designs in the abstractions.
- Preserve backend-specific capabilities via typed escape hatches.

### Non-goals (for this first cut)
- Perfect 1:1 event taxonomy alignment (Copilot has many events; Codex has many notifications/items).
- Re-implementing Copilot’s or Codex’s internal orchestration logic.
- Providing a single “universal tool format” that captures every backend tool-result modality.
- Defining a cross-backend storage format for conversation history.

## 3. Terminology (normalized)

| Term in this spec | Copilot SDK term | Codex app-server term |
|---|---|---|
| **Backend** | `CopilotClient` / Copilot CLI server | `CodexClient` / codex app-server |
| **Session** | `CopilotSession` | **Thread** |
| **Run** (1 user request) | message processing “turn” (events: `assistant.turn_*`, message id is `messageId`) | **Turn** (`turn/start`, notifications `turn/*`) |
| **Event stream** | `session.On(...)` (callbacks) | `CodexClient.NotificationsAsync()` / `StreamAsync()` (`IAsyncEnumerable`) |
| **Permission / approval** | `OnPermissionRequest` handler | server-initiated requests: command/file approval |
| **Tool call** | tool RPC to client, invokes `AIFunction` | server-initiated tool call request (experimental) |

We standardize on:
- **SessionId**: the conversation id (`CopilotSession.SessionId` / Codex `threadId`)
- **RunId**: the in-flight request id (`messageId` for Copilot; `turnId` for Codex)

## 4. Proposed project layout

Create a new library project:
- `src/CodeAlta.Agent/` → **`CodeAlta.Agent`** (abstractions + common DTOs)

Future adapter projects (not required immediately, but the spec assumes them):
- `CodeAlta.Agent.Copilot` (references NuGet `GitHub.Copilot.SDK`)
- `CodeAlta.Agent.Codex` (references `CodeAlta.CodexSdk`)

Rationale: keep `CodeAlta.Agent` dependency-free (or near dependency-free), and isolate backend-specific package references in adapter projects.

## 5. Shared API surface (C#)

This section defines the **target API surface** to implement in `CodeAlta.Agent`.

### 5.1 Backend IDs

Backends are identified by a stable string id to allow plugins/third-parties.

- `copilot_cli` → GitHub Copilot CLI runtime
- `codex_cli` → codex app-server runtime

### 5.2 Backend interface

```csharp
namespace CodeAlta.Agent;

public interface IAgentBackend : IAsyncDisposable
{
    AgentBackendId BackendId { get; }
    string DisplayName { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        CancellationToken cancellationToken = default);

    Task<IAgentSession> CreateSessionAsync(
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default);

    Task<IAgentSession> ResumeSessionAsync(
        string sessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default);
}
```

### 5.3 Session interface

```csharp
namespace CodeAlta.Agent;

public interface IAgentSession : IAsyncDisposable
{
    AgentBackendId BackendId { get; }
    string SessionId { get; }

    /// Optional backend-managed working directory / cwd for the session.
    string? WorkspacePath { get; }

    /// Streams normalized agent events.
    IAsyncEnumerable<AgentEvent> StreamEventsAsync(CancellationToken cancellationToken = default);

    /// Subscribe to events (convenience for UI code).
    IDisposable Subscribe(Action<AgentEvent> handler);

    /// Sends user input; returns the backend run identifier (turn/message id).
    Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default);

    /// Steers an already active run without starting a new run.
    Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default);

    /// Abort/cancel in-flight work for this session (best effort).
    Task AbortAsync(CancellationToken cancellationToken = default);

    /// Returns stored history as backend-normalized events (best effort).
    Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default);
}
```

Notes:
- `StreamEventsAsync` is the “primary” abstraction. `Subscribe` is a convenience wrapper; adapters can implement it by pumping `StreamEventsAsync` into handlers or by bridging the backend’s native callback model.
- `AbortAsync` is standardized as “cancel current run”, not “cancel a specific run id”, because Copilot exposes `session.abort` at the session level.

### 5.4 Common DTOs

#### AgentModelInfo

```csharp
public sealed record AgentModelInfo(
    string Id,
    string? DisplayName = null,
    string? Description = null,
    string? Provider = null,
    AgentReasoningEffort? DefaultReasoningEffort = null,
    IReadOnlyList<AgentReasoningEffort>? SupportedReasoningEfforts = null,
    IReadOnlyDictionary<string, object?>? Capabilities = null);
```

#### AgentSessionMetadata / filtering

```csharp
public sealed record AgentSessionContext(
    string? Cwd = null,
    string? GitRoot = null,
    string? Repository = null,
    string? Branch = null);

public sealed record AgentSessionMetadata(
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Summary = null,
    AgentSessionContext? Context = null,
    string? WorkspacePath = null);

public sealed record AgentSessionListFilter(
    string? Cwd = null,
    string? GitRoot = null,
    string? Repository = null,
    string? Branch = null);
```

#### Session create/resume options

```csharp
public class AgentSessionCreateOptions
{
    public string? Model { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool Streaming { get; init; }
    public AgentReasoningEffort? ReasoningEffort { get; init; }

    // “Instructions” across backends:
    public string? SystemMessage { get; init; }
    public string? DeveloperInstructions { get; init; }

    // Custom tools callable by the backend.
    public IReadOnlyList<AgentToolDefinition>? Tools { get; init; }

    // MCP servers available to the session.
    public IReadOnlyDictionary<string, AgentMcpServerConfig>? McpServers { get; init; }

    // Required for safety (Copilot enforces this; Codex can also use it).
    public required AgentPermissionRequestHandler OnPermissionRequest { get; init; }

    // Optional; enables “ask user” flows where supported.
    public AgentUserInputRequestHandler? OnUserInputRequest { get; init; }
}

public sealed class AgentSessionResumeOptions : AgentSessionCreateOptions
{
}

[JsonDerivedType(typeof(AgentLocalMcpServerConfig), typeDiscriminator: "local")]
[JsonDerivedType(typeof(AgentRemoteMcpServerConfig), typeDiscriminator: "remote")]
public abstract record AgentMcpServerConfig
{
    public IReadOnlyList<string>? EnabledTools { get; init; }
    public TimeSpan? ToolTimeout { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Required { get; init; }
}

public sealed record AgentLocalMcpServerConfig(string Command) : AgentMcpServerConfig
{
    public IReadOnlyList<string>? Arguments { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
    public string? WorkingDirectory { get; init; }
}

public enum AgentMcpRemoteTransport { Http, Sse }

public sealed record AgentRemoteMcpServerConfig(string Url) : AgentMcpServerConfig
{
    public AgentMcpRemoteTransport Transport { get; init; } = AgentMcpRemoteTransport.Http;
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? BearerTokenEnvironmentVariable { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentHeaders { get; init; }
}
```

Instruction-building recommendation:

- session creation should not rely on callers manually constructing long ad hoc system prompts
- instead, the effective instructions for a session should be composed from the canonical templates in `doc/specs/agent_instruction_templates_spec.md`
- callers may still provide run-specific additions, but the base and role instructions should come from a central provider

Important boundary:

- `IAgentSession` is a low-level execution primitive
- project selection, thread identity, and thread cwd decisions belong to CodeAlta orchestration, not to backend adapters
- the host orchestrator decides:
  - whether a prompt belongs to the global thread or a project thread
  - which project a new thread belongs to
  - the working directory used for the session
  - that a thread keeps its backend for its lifetime
  - when a new project thread is required because another project is involved

Important storage rule:

- Copilot and Codex remain the owners of raw thread/session history
- CodeAlta should use backend session/thread ids as the canonical ids for project/global threads
- CodeAlta may keep lightweight host-owned linkage records for delegated internal work when backend history alone is insufficient
- the shared agent API does not define a cross-backend durable transcript format

#### Sending user input

```csharp
public sealed class AgentSendOptions
{
    public required AgentInput Input { get; init; }
}

public sealed class AgentSteerOptions
{
    public required AgentInput Input { get; init; }
    public AgentRunId? ExpectedRunId { get; init; }
}

public sealed record AgentInput(IReadOnlyList<AgentInputItem> Items)
{
    public static AgentInput Text(string text) => new([new AgentInputItem.Text(text)]);
}

public abstract record AgentInputItem
{
    public sealed record Text(string Value) : AgentInputItem;
    public sealed record ImageUrl(string Url) : AgentInputItem;
    public sealed record LocalImage(string Path) : AgentInputItem;

    // Attachments (Copilot):
    public sealed record File(string Path, string? DisplayName = null, AgentLineRange? LineRange = null) : AgentInputItem;
    public sealed record Directory(string Path, string? DisplayName = null, AgentLineRange? LineRange = null) : AgentInputItem;
    public sealed record Selection(string FilePath, string DisplayName, string SelectedText, AgentSelectionRange Range) : AgentInputItem;

    // Skill/app invocation (Codex):
    public sealed record Skill(string Name, string Path) : AgentInputItem;
    public sealed record Mention(string Name, string Path) : AgentInputItem;
}

public sealed record AgentLineRange(int StartLine, int EndLine);
public sealed record AgentSelectionRange(AgentPosition Start, AgentPosition End);
public sealed record AgentPosition(int Line, int Character);

public enum AgentReasoningEffort { Low, Medium, High, XHigh, None, Minimal }
```

### 5.5 Tools

This is the minimum shared “custom tool” model that can map to both:
- Copilot SDK tools (`AIFunction`)
- Codex dynamic tools (experimental; request/response tool call)

```csharp
public sealed record AgentToolDefinition(
    AgentToolSpec Spec,
    AgentToolHandler Handler);

public sealed record AgentToolSpec(
    string Name,
    string Description,
    JsonElement InputSchema);

public sealed record AgentToolInvocation(
    AgentBackendId BackendId,
    string SessionId,
    string ToolCallId,
    string ToolName,
    JsonElement Arguments);

public delegate Task<AgentToolResult> AgentToolHandler(
    AgentToolInvocation invocation,
    CancellationToken cancellationToken);

public sealed record AgentToolResult(
    bool Success,
    IReadOnlyList<AgentToolResultItem> Items,
    string? Error = null);

public abstract record AgentToolResultItem
{
    public sealed record Text(string Value) : AgentToolResultItem;
    public sealed record ImageUrl(string Url) : AgentToolResultItem;
}
```

Notes:
- Tool names use one provider-neutral policy across backends and must match `^[a-zA-Z0-9_-]+$`.
- This is intentionally narrower than Copilot’s full tool-result surface. Adapters can:
  - down-convert richer results to `Text`,
  - or expose backend-native tool result via escape hatch (see §8).

### 5.6 Permissions / approvals

We normalize everything to “permission request” callbacks with an allow/deny outcome.

```csharp
public sealed record AgentPermissionRequest(
    AgentBackendId BackendId,
    string SessionId,
    string Kind,
    JsonElement Raw);

public sealed record AgentPermissionDecision(
    AgentPermissionDecisionKind Kind,
    IReadOnlyList<string>? ExecPolicyAmendment = null);

public enum AgentPermissionDecisionKind
{
    AllowOnce,
    AllowForSession,
    Deny,
    Cancel,
}

public delegate Task<AgentPermissionDecision> AgentPermissionRequestHandler(
    AgentPermissionRequest request,
    CancellationToken cancellationToken);
```

Codex has a richer approval response for commands (`acceptWithExecpolicyAmendment`); this is captured by `ExecPolicyAmendment`.

### 5.7 User input requests (“ask user”)

```csharp
public sealed record AgentUserInputRequest(
    AgentBackendId BackendId,
    string SessionId,
    IReadOnlyList<AgentUserInputQuestion> Questions);

public sealed record AgentUserInputQuestion(
    string Id,
    string Question,
    IReadOnlyList<string>? Choices = null,
    bool AllowFreeform = true);

public sealed record AgentUserInputResponse(IReadOnlyDictionary<string, string> Answers);

public delegate Task<AgentUserInputResponse> AgentUserInputRequestHandler(
    AgentUserInputRequest request,
    CancellationToken cancellationToken);
```

### 5.8 Normalized events

The normalized event set is intentionally small; adapters may emit `AgentRawEvent` for backend-specific events.

```csharp
public abstract record AgentEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId = null);

public readonly record struct AgentRunId(string Value);

public sealed record AgentRawEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    string BackendEventType,
    JsonElement Raw,
    AgentRunId? RunId = null)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);

public sealed record AgentAssistantMessageDeltaEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    string Delta)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);

public sealed record AgentAssistantMessageEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    string Content)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);

public sealed record AgentSessionIdleEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp)
    : AgentEvent(BackendId, SessionId, Timestamp);

public sealed record AgentErrorEvent(
    AgentBackendId BackendId,
    string SessionId,
    DateTimeOffset Timestamp,
    string Message,
    Exception? Exception = null,
    AgentRunId? RunId = null)
    : AgentEvent(BackendId, SessionId, Timestamp, RunId);
```

### 5.9 Concrete backend factory

To support applications that prefer concrete composition over interface-first registration, expose:

```csharp
public sealed class AgentBackendFactory
{
    public void Register(AgentBackendId backendId, Func<IAgentBackend> backendFactory);
    public bool TryRegister(AgentBackendId backendId, Func<IAgentBackend> backendFactory);
    public void RegisterOrReplace(AgentBackendId backendId, Func<IAgentBackend> backendFactory);

    public bool IsRegistered(AgentBackendId backendId);
    public bool Unregister(AgentBackendId backendId);
    public IReadOnlyList<AgentBackendId> ListRegisteredBackends();

    public IAgentBackend Create(AgentBackendId backendId);
    public bool TryCreate(AgentBackendId backendId, out IAgentBackend? backend);
}
```

Adapter packages can add fluent registration helpers:
- `RegisterCodex(...)` / `RegisterOrReplaceCodex(...)`
- `RegisterCopilot(...)` / `RegisterOrReplaceCopilot(...)`

## 6. Mapping to GitHub Copilot SDK (.NET)

This section describes how the adapters map the shared API to `GitHub.Copilot.SDK`.

### 6.1 Backend lifecycle

| Shared API | Copilot API |
|---|---|
| `StartAsync` | `CopilotClient.StartAsync()` (optional because `CreateSessionAsync` autostarts by default) |
| `StopAsync` | `CopilotClient.StopAsync()` |
| `DisposeAsync` | `CopilotClient.DisposeAsync()` or `StopAsync` + dispose |

### 6.2 Create/resume session

| Shared API | Copilot API |
|---|---|
| `CreateSessionAsync(options)` | `CopilotClient.CreateSessionAsync(SessionConfig)` |
| `ResumeSessionAsync(sessionId, options)` | `CopilotClient.ResumeSessionAsync(sessionId, ResumeSessionConfig)` |
| `SessionId` | `CopilotSession.SessionId` |
| `WorkspacePath` | `CopilotSession.WorkspacePath` |

Mapping notes:
- `AgentSessionCreateOptions.OnPermissionRequest` → required `SessionConfig.OnPermissionRequest`.
- `AgentSessionCreateOptions.OnUserInputRequest` → `SessionConfig.OnUserInputRequest`.
- `Model`, `WorkingDirectory`, `Streaming`, `ReasoningEffort`, `SystemMessage` map directly.
- `McpServers` maps to `SessionConfig.McpServers`.
- `Tools`:
  - Create `AIFunction` wrappers that call `AgentToolHandler` and return a tool result (prefer returning a string or JSON).

### 6.3 List models / sessions

| Shared API | Copilot API |
|---|---|
| `ListModelsAsync()` | `CopilotClient.ListModelsAsync()` |
| `ListSessionsAsync(filter)` streams `AgentSessionMetadata` | `CopilotClient.ListSessionsAsync(SessionListFilter)` |

Session listing streams results so callers can begin processing metadata before every backend/page has completed. Backends should yield sessions in the most useful order exposed by the provider; local runtime stores enumerate recently written session journals first. Recoverable thread/session loading should preserve the async stream through the shell/UI boundary: CodeAlta-owned local-runtime sessions are emitted first, provider-owned sessions follow as each backend produces metadata, and completed listings are cached until session/backend state changes so repeated session-list consumers do not re-query providers.

Model mapping notes (Copilot):
- `AgentModelInfo.Description` is `null` (Copilot model payload does not expose a description field).
- `AgentModelInfo.DefaultReasoningEffort` / `SupportedReasoningEfforts` are mapped from `ModelInfo.DefaultReasoningEffort` and `ModelInfo.SupportedReasoningEfforts`.

### 6.4 Sending user input

| Shared API | Copilot API |
|---|---|
| `SendAsync(AgentSendOptions)` | `CopilotSession.SendAsync(MessageOptions { Mode = "enqueue" })` |
| `SteerAsync(AgentSteerOptions)` | `CopilotSession.SendAsync(MessageOptions { Mode = "immediate" })` |

Input item mapping:
- `AgentInputItem.Text` → `MessageOptions.Prompt` (concatenate multiple text items with `\n\n`).
- `AgentInputItem.File/Directory/Selection` → `MessageOptions.Attachments` using `UserMessageDataAttachmentsItem*`.
- `AgentInputItem.ImageUrl/LocalImage/Skill/Mention` → **no direct equivalent** in Copilot SDK message input today; emulate by inlining into the prompt (see §9).

RunId mapping:
- `AgentRunId` = `SendAsync` return value (`messageId`).

Steering notes:
- Copilot uses `MessageOptions.Mode = "enqueue"` for regular sends, including the backend-managed queueing behavior.
- Copilot uses `MessageOptions.Mode = "immediate"` for steering/priority delivery against the current session flow.
- `AgentSteerOptions.ExpectedRunId` is currently advisory only for Copilot; the SDK only exposes the mode switch, not an explicit target run id parameter.
- MCP server mapping:
  - `AgentLocalMcpServerConfig` → `McpLocalServerConfig`
  - `AgentRemoteMcpServerConfig` → `McpRemoteServerConfig`
  - when `EnabledTools` is omitted, the adapter uses `["*"]` so Copilot exposes all tools from that server by default
  - environment-derived HTTP credentials are not supported by the Copilot adapter; use static headers instead

### 6.5 Abort

| Shared API | Copilot API |
|---|---|
| `AbortAsync()` | `CopilotSession.AbortAsync()` |

### 6.6 Event stream mapping

Copilot emits `SessionEvent` via callbacks (`CopilotSession.On(...)`).

Recommended adapter strategy:
- Subscribe to Copilot events.
- Convert to normalized `AgentEvent` and push into a `Channel<AgentEvent>`.
- `StreamEventsAsync` reads from that channel.

Core event mapping:

| Normalized event | Copilot `SessionEvent` |
|---|---|
| `AgentAssistantMessageDeltaEvent` | `AssistantMessageDeltaEvent` (`Data.DeltaContent`, `Data.MessageId`) |
| `AgentAssistantMessageEvent` | `AssistantMessageEvent` (`Data.Content`, `Data.MessageId`) |
| `AgentSessionIdleEvent` | `SessionIdleEvent` |
| `AgentErrorEvent` | `SessionErrorEvent` |
| `AgentRawEvent` | any other `SessionEvent` serialized as raw JSON |

## 7. Mapping to Codex app-server (via CodeAlta.CodexSdk)

### 7.1 Backend lifecycle

| Shared API | Codex API (CodeAlta.CodexSdk) |
|---|---|
| `StartAsync` | `CodexClient.StartAsync(ClientInfo, ...)` |
| `StopAsync` | `CodexClient.DisposeAsync()` |

### 7.2 Create/resume session (thread)

| Shared API | Codex API |
|---|---|
| `CreateSessionAsync(options)` | `thread/start` → `CodexClient.ThreadStartAsync(ThreadStartParams)` |
| `ResumeSessionAsync(sessionId, options)` | `thread/resume` → `CodexClient.ThreadResumeAsync(ThreadResumeParams)` |
| `SessionId` | Codex `thread.id` |

Mapping notes (Codex):
- `Model` → `ThreadStartParams.Model`.
- `WorkingDirectory` → `ThreadStartParams.Cwd`.
- `DeveloperInstructions` → `ThreadStartParams.DeveloperInstructions` (when supported by schema/version).
- `McpServers` → flattened `ThreadStartParams.Config` entries under `mcp_servers.<serverName>.*`.
- `SystemMessage`:
  - Codex does not expose the same “systemMessage” field as Copilot; emulate by prepending the system message into `baseInstructions` or `developerInstructions`.
- Tools:
  - Current `CodeAlta.CodexSdk` `thread/start` / `thread/resume` params do not expose dynamic tool registration.
  - `AgentSessionCreateOptions.Tools` / `AgentSessionResumeOptions.Tools` are therefore currently **not supported** by the Codex adapter and throw `NotSupportedException`.
  - If the server sends a dynamic tool call request anyway, the adapter returns a failure response and emits `AgentErrorEvent`.

### 7.3 Sending user input (turn)

| Shared API | Codex API |
|---|---|
| `SendAsync(AgentSendOptions)` | `turn/start` → `CodexClient.TurnStartAsync(TurnStartParams)` |
| `SteerAsync(AgentSteerOptions)` | `turn/steer` → `CodexClient.TurnSteerAsync(TurnSteerParams)` |
| RunId | Codex `turn.id` |

Input item mapping:
- `Text` → `UserInput.TextUserInput`
- `ImageUrl` → `UserInput.ImageUserInput`
- `LocalImage` → `UserInput.LocalImageUserInput`
- `Skill` / `Mention` → `UserInput.SkillUserInput` / `UserInput.MentionUserInput`
- `File/Directory/Selection` → not supported as structured input; emulate by inlining the content in the text input (see §9).

Steering mapping notes:
- `AgentSteerOptions.ExpectedRunId` maps to `TurnSteerParams.ExpectedTurnId`.
- When `ExpectedRunId` is omitted, the Codex adapter uses the most recent active run id it knows about.
- If there is no active run to steer, the adapter throws `InvalidOperationException`.
- MCP server mapping:
  - `AgentLocalMcpServerConfig` maps to `mcp_servers.<name>.command`, `.args`, `.env`, `.cwd`, `.enabled`, `.required`, `.tool_timeout_sec`, and `.enabled_tools`
  - `AgentRemoteMcpServerConfig` maps to `mcp_servers.<name>.url`, `.http_headers`, `.bearer_token_env_var`, `.env_http_headers`, `.enabled`, `.required`, `.tool_timeout_sec`, and `.enabled_tools`
  - Codex currently supports streamable HTTP and stdio MCP servers through this adapter; `AgentMcpRemoteTransport.Sse` throws `NotSupportedException`

Model mapping notes (Codex):
- `AgentModelInfo.Description` maps from `Model.Description`.
- `AgentModelInfo.DefaultReasoningEffort` / `SupportedReasoningEfforts` map from `Model.DefaultReasoningEffort` and `Model.SupportedReasoningEfforts`.

### 7.4 Abort

| Shared API | Codex API |
|---|---|
| `AbortAsync()` | best-effort: `turn/interrupt` against the most recently started in-flight turn id |

### 7.5 Permission/approval mapping

Codex approvals arrive as **server-initiated JSON-RPC requests** (in `CodexClient.StreamAsync()`), specifically:
- `ServerRequest.ItemCommandExecutionRequestApprovalRequest`
- `ServerRequest.ItemFileChangeRequestApprovalRequest`

Adapter strategy:
1. Convert the server request to `AgentPermissionRequest` (`Kind` = `"commandExecution"` or `"fileChange"`, `Raw` = params).
2. Invoke `AgentSessionCreateOptions.OnPermissionRequest`.
3. Map `AgentPermissionDecision` to the corresponding Codex response type:
   - command: `CommandExecutionRequestApprovalResponse { Decision = ... }`
   - file: `FileChangeRequestApprovalResponse { Decision = ... }`
4. Reply via `CodexClient.RespondToRequestAsync(requestId, response)`.

Decision mapping:
- `AllowOnce` → `accept`
- `AllowForSession` → `acceptForSession`
- `Deny` → `decline`
- `Cancel` → `cancel`
- `ExecPolicyAmendment` (when set) → `acceptWithExecpolicyAmendment`

### 7.6 User input requests

Codex can request user input via server request:
- `ServerRequest.ItemToolRequestUserInputRequest` (contains 1–3 questions)

Adapter strategy:
- Convert to `AgentUserInputRequest` and invoke `OnUserInputRequest`.
- Map answers back to `ToolRequestUserInputResponse` and respond via `RespondToRequestAsync`.

### 7.7 Event stream mapping

Codex emits `CodexNotification` objects as notifications.

Core mapping:

| Normalized event | Codex notification |
|---|---|
| `AgentAssistantMessageDeltaEvent` | `CodexNotification.AgentMessageDelta` (`Data.Delta`, `Data.TurnId`) |
| `AgentSessionIdleEvent` | `CodexNotification.TurnCompleted` (treat “turn completed” as the session becoming idle) |
| `AgentErrorEvent` | `CodexNotification.Error` |
| `AgentRawEvent` | any other notification serialized as raw JSON |

Important: Codex notifications are scoped by `threadId` and often include `turnId`. The adapter must set:
- `SessionId` = `threadId`
- `RunId` = `turnId` when available

## 8. Backend-specific escape hatches

Some consumers will need direct access to:
- `GitHub.Copilot.SDK.CopilotClient` / `CopilotSession`
- `CodeAlta.CodexSdk.CodexClient` and the underlying thread/turn ids

Recommendation:
- Adapter projects expose backend-specific interfaces that extend `IAgentBackend`/`IAgentSession`:

```csharp
public interface ICopilotAgentBackend : IAgentBackend
{
    GitHub.Copilot.SDK.CopilotClient Client { get; }
}

public interface ICopilotAgentSession : IAgentSession
{
    GitHub.Copilot.SDK.CopilotSession Session { get; }
}
```

Similarly for Codex:

```csharp
public interface ICodexAgentBackend : IAgentBackend
{
    CodeAlta.CodexSdk.CodexClient Client { get; }
}
```

## 9. Emulation strategies / gaps

### 9.1 Copilot attachments vs Codex structured inputs

Copilot has structured attachments (`file`, `directory`, `selection`) on messages. Codex does not.

Current emulation in the Codex adapter:
- For each attachment, add lightweight fallback text describing type/path/range.
- For selections, include the selected text payload plus coordinates.
- The adapter does **not** currently read file contents from disk for attachment fallbacks.

### 9.2 Codex skill/app invocation vs Copilot

Codex supports explicit skill/app invocation via `$<name>` plus structured `skill` / `mention` input items.
Copilot supports skills via `skillDirectories` and emits `skill.invoked` events, but does not expose the same explicit input-item mechanism.

Guidance:
- Keep skill configuration as backend-specific for now.
- If we later normalize it, treat it as “session config” not “message input”.

### 9.3 Tool surface mismatches

Copilot tools:
- are `AIFunction` driven, with rich tool result possibilities.

Codex dynamic tools:
- are experimental and schema/version dependent.

Guidance:
- Keep `AgentToolResult` minimal (text + optional image urls).
- For richer results, expose backend-native tool result via escape hatch.

## 10. Implementation checklist (adapter authors)

For each adapter:
- Ensure `OnPermissionRequest` is always enforced (deny-by-default if missing).
- Ensure event pumping does not deadlock:
  - use `Channel` + background reader for callback-based backends (Copilot),
  - directly yield from `IAsyncEnumerable` for stream-based backends (Codex).
- Normalize timestamps:
  - Copilot events have `Timestamp` already.
  - Codex notifications may not; use local receive time for normalized events when absent.
- Preserve raw events:
  - emit `AgentRawEvent` for unknown/unmapped events.
