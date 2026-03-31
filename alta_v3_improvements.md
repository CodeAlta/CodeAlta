# CodeAlta Frontend — Architecture Analysis & v3 Improvements

> **Date**: 2026-03-30  
> **Scope**: `src/CodeAlta/` — the TUI frontend application  
> **Context**: Two major refactoring passes (v1 and v2) already landed. This document evaluates the current state and proposes a third wave of improvements focused on **extensibility**, **pluggability**, and **preparation for upcoming features** (multi-agent fleet, slash commands, integrated help, and more).

---

## 1. Where We Stand After v1 & v2

The prior refactoring rounds achieved significant progress:

| What was done (v1) | What was done (v2) |
|---|---|
| Removed `TerminalHost` god class | Introduced 7 typed collaboration contexts (`App/Context/`) |
| Extracted `CodeAltaShellView`, `SidebarView`, `ThreadWorkspaceView` | Refactored callback-heavy coordinators to use typed contexts |
| Introduced focused ViewModels and `[Bindable]` pattern | Shrunk `CodeAltaApp` relay surface |
| Created `RuntimeEventPump` and `IUiDispatcher` | Moved UI dispatch abstractions to `Threading/` namespace |
| Split `ThreadSessionState` from visual state | Moved `OpenThreadState` to `App/State/` |
| Extracted timeline/tool-call/usage presenters | Cleaned up namespace ownership |
| Extracted formatters into dedicated classes | |

**The codebase is in materially better shape than before.** The MVVM-inspired pattern with coordinators, projections, and bindable ViewModels is consistent and well-applied. There are no circular dependencies in the presentation layer, and the projection/builder pattern for change detection is elegant.

However, the architecture still has structural issues that will compound as we add the next wave of features. These are documented below.

---

## 2. Current Architecture Snapshot

```
src/CodeAlta/
├── App/                          # Business logic coordinators (~25 classes)
│   ├── Context/                  # 7 typed collaboration contexts
│   └── State/                    # OpenThreadState
├── Models/                       # 11 data models (ThreadSessionState, etc.)
├── Presentation/                 # UI presentation logic (12 subfolders)
│   ├── Chat/                     # Backend selection, auto-approve
│   ├── Controls/                 # Reusable UI controls
│   ├── Formatting/               # Formatters (markdown, usage, tool calls, file changes)
│   ├── Prompting/                # Prompt composer projections
│   ├── Shell/                    # Welcome pane, status display
│   ├── Sidebar/                  # Tree navigation, node ViewModels
│   ├── Styling/                  # UiPalette (centralized colors)
│   ├── Tabs/                     # Tab strip projection & coordination
│   ├── Threads/                  # Thread info, scope, selection
│   ├── Timeline/                 # Timeline presenters (chat, tool calls, file changes)
│   ├── Usage/                    # Context usage presenter & aggregator
│   └── Workspace/                # ChatSelectorCoordinator
├── Services/                     # ChatAgentConnection
├── Threading/                    # IUiDispatcher, TerminalUiDispatcher
├── ViewModels/                   # 11 bindable ViewModels
└── Views/                        # 17 view/dialog classes (incl. CodeAltaApp)
```

**Key metrics (lines of code):**

| File | LOC | Role |
|---|---|---|
| `Views/CodeAltaApp.cs` | 791 | Composition root + orchestrator |
| `App/ThreadCommandCoordinator.cs` | 645 | Prompt dispatch + commands |
| `App/ShellThreadStateCoordinator.cs` | 642 | Central state manager |
| `Views/ThreadWorkspaceView.cs` | 615 | UI layout + shortcuts |
| `Presentation/Formatting/ChatMarkdownFormatter.cs` | 800 | Markdown rendering |
| `Presentation/Formatting/SessionUsageFormatter.cs` | 505 | Usage formatting |
| `App/ThreadRuntimeEventCoordinator.cs` | 491 | Event dispatch |
| `Presentation/Workspace/ChatSelectorCoordinator.cs` | 415 | Backend/model selectors |
| `App/ThreadHistoryCoordinator.cs` | 404 | History loading |
| `App/CodeAltaShellController.cs` | 364 | Controller + initialization |
| `App/SidebarCoordinator.cs` | 345 | Sidebar rendering |
| `App/ShellWorkspaceCoordinator.cs` | 301 | UI refresh orchestration |

---

## 3. Critical Issues

### 3.1 `CodeAltaApp` is still the bottleneck for every new feature

**Status**: 791 LOC, 35+ wired dependencies, 15+ coordinators created in constructor.

Despite v1/v2 extracting logic into coordinators, `CodeAltaApp` remains the mandatory modification point for **every** new feature. Adding a new coordinator means: (1) add a field, (2) construct it in the ~200-line constructor, (3) wire callbacks, (4) hook into refresh paths. The class is the composition root, the lifecycle manager, and the inter-coordinator router all in one.

**Impact on upcoming features**: Multi-agent support, slash commands, and a help system will each need new coordinators, new ViewModels, new UI elements — all of which will require modifications to this single file. At the current trajectory, `CodeAltaApp` will balloon past 1000+ LOC.

**Recommendation**: Extract a **`ShellCompositionRoot`** (or `AppBuilder`) that handles pure object construction and wiring, leaving `CodeAltaApp` as a thin lifecycle facade with `PrepareForRun()`, `Tick()`, and `Dispose()`. The composition root becomes the only file that grows with new features, and its growth is purely mechanical (adding registrations), not behavioral.

### 3.2 `ShellThreadStateCoordinator` is a god object

**Status**: 642 LOC, 46+ public methods, 12+ injected callbacks, manages catalog, selection, tabs, preferences, navigation settings, and persistence all in one class.

This coordinator violates the Single Responsibility Principle severely. It manages:
- Project/thread catalog state (read cache)
- Selection state machine (global/project/thread)
- Open thread tab lifecycle (create, close, reset)
- Thread preference application (backend, model, reasoning)
- View state persistence (save/load)
- Navigator settings (sort mode, recent threads count)
- Selection fallback logic (complex state transitions)

**Impact on upcoming features**: Multi-agent fleet will add agent identity to thread state. Thread groups/squads will add hierarchical relationships. Every new state dimension compounds this class.

**Recommendation**: Decompose into focused managers:

| New class | Responsibility |
|---|---|
| `ThreadCatalogCache` | Read-only project/thread cache, reload |
| `ShellSelectionManager` | Selection state machine (global → project → thread) |
| `ThreadTabManager` | Open tab lifecycle (create, close, reset, find) |
| `ThreadPreferenceManager` | Per-thread backend/model/reasoning preferences |
| `ViewStatePersistence` | Save/load view state to catalog |

`ShellThreadStateCoordinator` becomes a thin façade composing these, or is removed entirely with coordinators referencing the specific managers they need.

### 3.3 No command system — all text goes directly to the agent

**Status**: `ChatPromptEditor.OnAccepted()` → `PromptDraftCoordinator` → `ThreadCommandCoordinator.SendSelectedThreadPromptAsync()` → `RuntimeService.SendAsync()`. There is **zero** interception, parsing, or routing. Every keystroke in the prompt is treated as a chat message to the backend.

**Impact on upcoming features**: Slash commands (`/help`, `/agent spawn`, `/compact`, `/clear`), `?` help trigger, and any client-side command would need an interception point that does not currently exist. Adding it later means retrofitting the entire prompt flow.

**Recommendation**: Introduce a **command pipeline** with clear stages:

```
User submits text
  ↓
1. CommandParser           — detect /commands, ? help, or pass-through
  ↓
2. CommandRouter           — dispatch to ICommandHandler or chat agent
  ↓
3. ICommandHandler.Execute — handle locally (help, settings, agent mgmt)
   — or —
   ChatCommandHandler      — forward to agent (current behavior)
```

Define an `ICommandHandler` interface:

```csharp
interface ICommandHandler
{
    string Name { get; }           // e.g. "help", "agent", "compact"
    string Description { get; }    // For help system
    ValueTask ExecuteAsync(CommandContext context, CancellationToken ct);
}
```

Register handlers in a `CommandRegistry`. The prompt editor calls the parser first. This makes the system open for extension (add a handler class) and closed for modification (no changes to existing prompt flow).

### 3.4 Single-agent architecture — no foundation for fleet/squad

**Status**: `ChatAgentConnection` manages exactly **one** `AgentId` per connection. `RuntimeEventPump` streams from **one** `WorkThreadRuntimeService`. Events are keyed by `threadId` only, with no agent identity.

```csharp
// Current: one agent per connection
private AgentId? _connectedAgentId;
```

**Impact on upcoming features**: Multi-agent fleet support requires:
- Multiple concurrent agents with distinct identities
- Events tagged with agent source
- UI showing which agent is speaking/acting
- Ability to direct prompts to specific agents
- Agent lifecycle management (spawn, stop, inspect)

None of this infrastructure exists today.

**Recommendation**: This is the deepest architectural change. Introduce:

1. **`AgentRegistry`** — manages lifecycle of multiple named agents (spawn, stop, list, get status)
2. **`AgentIdentity`** model — `(AgentId, Name, Role, BackendId, Status)` — visible in UI
3. **`MultiStreamEventPump`** — fan-in from multiple agent event streams, tagging each event with its source agent
4. **Thread-agent association** — extend `ThreadSessionState` with a list of participating agents, not just a backend ID
5. **Agent-aware UI** — timeline entries show which agent produced them; tab headers show active agents

This is a large effort but the foundational models (`AgentIdentity`, `AgentRegistry`) should be introduced now even before full fleet support lands, so that the single-agent case becomes `registry with one entry` rather than a separate code path.

### 3.5 Event dispatch is a monolithic switch statement

**Status**: `ThreadRuntimeEventCoordinator.HandleAgentEvent()` is 212 lines with 10+ case branches in a pattern-matching switch. Adding a new event type means adding another case block to this growing method.

```csharp
switch (@event)
{
    case AgentContentDeltaEvent delta: ...
    case AgentContentCompletedEvent completed: ...
    case AgentPlanSnapshotEvent planEvent: ...
    case AgentActivityEvent activity: ...
    case AgentRawEvent raw: ...
    case AgentPermissionRequest permissionRequest: ...
    case AgentUserInputRequest userInputRequest: ...
    case AgentInteractionEvent interaction: ...
    case AgentSessionUpdateEvent update: ...
    case AgentErrorEvent error: ...
}
```

**Impact on upcoming features**: Multi-agent will add agent-specific event types. Fleet coordination events (agent spawned, agent completed, agent delegated) will each need handling. The switch grows unbounded.

**Recommendation**: Introduce a **handler registry** pattern:

```csharp
interface IRuntimeEventHandler<TEvent> where TEvent : AgentEvent
{
    ValueTask HandleAsync(TEvent @event, EventHandlingContext context);
}
```

Register handlers per event type. The coordinator becomes a dispatcher that looks up the handler and delegates. New event types add new handler classes — no modification to the coordinator.

An alternative lightweight approach: keep the switch but extract each case body into a dedicated method object / handler class, making the switch a thin routing table.

### 3.6 Keyboard shortcuts are embedded in layout code

**Status**: `ThreadWorkspaceView.cs` (615 LOC) registers 13+ keyboard shortcuts inline during UI construction. Shortcuts are `Command` objects added via `editor.AddCommand()` calls scattered through the constructor.

There is no central shortcut registry, no way to discover all shortcuts (for a help system), and no way for plugins or new features to register shortcuts without modifying this view.

**Recommendation**: Create a **`ShortcutRegistry`**:

```csharp
sealed class ShortcutRegistry
{
    void Register(ShortcutDescriptor descriptor);
    IReadOnlyList<ShortcutDescriptor> GetAll();           // For help system
    IReadOnlyList<ShortcutDescriptor> GetForContext(string context);  // Context-sensitive
}

record ShortcutDescriptor(
    string Id,
    string DisplayName,       // "Steer prompt"
    string Description,       // "Send a steering prompt to the running agent"
    string Category,          // "Prompting", "Navigation", "Thread Management"
    KeyGesture Gesture,
    Func<bool> CanExecute,
    Func<Task> Execute);
```

Views pull from the registry instead of defining shortcuts inline. The help system queries the registry to build a keyboard shortcut reference. New features register shortcuts declaratively.

---

## 4. Structural Improvements

### 4.1 Split `ThreadCommandCoordinator` — separate command execution from prompt dispatch

**Status**: 645 LOC handling 8+ operations procedurally: send, delegate, abort, compact, queue management, permission handling, user input handling.

**Recommendation**: Extract:
- **`PromptDispatcher`** — the core `DispatchPromptAsync()` logic (build options, call runtime, handle errors)
- **`PermissionRequestHandler`** — auto-approve logic, rendering
- **`UserInputRequestHandler`** — response building, rendering
- **`QueueManager`** — queue operations (clear, convert-to-steer, delete, update, drain)

The coordinator becomes a thin router delegating to these focused handlers. This also prepares for the command system (§3.3) — each of these becomes an `ICommandHandler`.

### 4.2 Split large formatters

**`ChatMarkdownFormatter` (800 LOC)** — This is the largest single file in the presentation layer. It likely handles code block rendering, inline formatting, link handling, and syntax highlighting all in one class.

**Recommendation**: Split by concern:
- `MarkdownBlockRenderer` — paragraphs, headers, lists, quotes
- `CodeBlockRenderer` — fenced code blocks with syntax highlighting
- `InlineMarkdownRenderer` — bold, italic, links, inline code
- `ChatMarkdownFormatter` remains as the orchestrator composing these

**`SessionUsageFormatter` (505 LOC)** — Handles window usage, operation usage, rate limits, quotas, and backend-specific details.

**Recommendation**: Split into `WindowUsageFormatter`, `OperationUsageFormatter`, `QuotaFormatter`, `RateLimitFormatter`, composed by `SessionUsageFormatter`.

### 4.3 `ChatSelectorCoordinator` (415 LOC) mixes too many selector concerns

This coordinator manages backend selection, model selection, reasoning effort selection, auto-scroll toggle, and always-enqueue toggle — with bidirectional sync and feedback-loop prevention for each.

**Recommendation**: Extract per-selector logic into small focused classes:
- `BackendSelector` — backend options, selection, state
- `ModelSelector` — model options, selection, state  
- `ReasoningSelector` — reasoning options, selection
- `ChatSelectorCoordinator` composes them, handling shared concerns (scope changes)

### 4.4 Introduce interface layer for key services

Currently, most coordinators are sealed concrete classes with no interfaces. This limits testability (can't mock) and extensibility (can't substitute).

**Key interfaces to introduce:**

| Interface | Wraps |
|---|---|
| `IThreadTabManager` | Tab lifecycle (from §3.2 split) |
| `IShellSelection` | Selection state reads + commands |
| `ICommandRegistry` | Command registration + lookup (from §3.3) |
| `IShortcutRegistry` | Shortcut registration + discovery (from §3.6) |
| `IAgentRegistry` | Agent lifecycle management (from §3.4) |
| `IRuntimeEventDispatcher` | Event handler lookup + dispatch (from §3.5) |

These interfaces form the **extension surface** of the application. Future features and potential plugins depend on contracts, not concrete implementations.

### 4.5 Formalize the Coordinator vs. Presenter vs. Projection naming

The codebase uses three presentation-layer patterns but the naming is inconsistent:

| Pattern | Purpose | Current naming | Issue |
|---|---|---|---|
| **Projection** | Immutable snapshot for change detection | `*Projection` + `*ProjectionBuilder` | ✅ Consistent |
| **Presenter** | Stateful component managing UI + lifecycle | `*Presenter` | ⚠️ Also used for popup controllers |
| **Coordinator** | Bidirectional sync between app state and UI | `*Coordinator` | ⚠️ Overloaded — used in both `App/` and `Presentation/` with different meanings |

**Recommendation**: In `Presentation/`, rename coordinators that do bidirectional sync to **`*Synchronizer`** (e.g., `ThreadTabStripSynchronizer`, `ChatSelectorSynchronizer`). Reserve `*Coordinator` for `App/` layer business logic orchestration. Rename presenters that primarily manage popups to **`*PopupController`** (e.g., `ThreadInfoPopupController`, `SessionUsagePopupController`).

---

## 5. Extensibility Preparation

### 5.1 Command pipeline (required for slash commands, help, and agent management)

This is the single most impactful extensibility improvement. Today, there is **no concept of a "command"** in the application — everything is a prompt sent to an agent.

**Architecture:**

```
┌─────────────────────────────────────────────────┐
│              ChatPromptEditor                   │
│  User submits text ───────────────────────────  │
└──────────────────────┬──────────────────────────┘
                       ↓
┌──────────────────────────────────────────────────┐
│              CommandPipeline                      │
│  1. Parse: detect /command or ? or raw text      │
│  2. Route: find handler or fall through to chat  │
│  3. Execute: handler.ExecuteAsync(context)        │
└──────────────────────────────────────────────────┘
         ↓                         ↓
   ICommandHandler            ChatSendHandler
   (local commands)           (current behavior)
```

**Built-in commands to ship initially:**

| Command | Description |
|---|---|
| `/help` or `?` | Show all commands and keyboard shortcuts |
| `/compact` | Compact the current thread (replaces F11) |
| `/abort` | Abort the current run (replaces F8) |
| `/clear` | Clear the prompt queue (replaces F10) |
| `/info` | Show thread info (replaces Ctrl+G, Ctrl+T) |
| `/usage` | Show context usage (replaces Ctrl+G, Ctrl+U) |
| `/new` | Create a new thread |
| `/close` | Close the current tab |

These give slash commands an immediate reason to exist while also making keyboard shortcuts discoverable via `/help`.

### 5.2 Agent registry (required for multi-agent / fleet)

Even before full fleet support, introduce the `AgentRegistry` abstraction:

```csharp
interface IAgentRegistry
{
    AgentIdentity PrimaryAgent { get; }
    IReadOnlyList<AgentIdentity> ActiveAgents { get; }
    ValueTask<AgentIdentity> SpawnAgentAsync(AgentSpawnOptions options, CancellationToken ct);
    ValueTask StopAgentAsync(AgentId agentId, CancellationToken ct);
    event Action<AgentIdentity> AgentStateChanged;
}
```

Initially, this wraps the existing `ChatAgentConnection` with a single-agent implementation. When fleet support arrives, the interface stays stable and the implementation changes.

### 5.3 Event system extensibility (required for multi-agent events)

Extend `RuntimeEventPump` to support multiple event sources:

```csharp
sealed class CompositeEventPump
{
    void AddSource(string sourceId, IAsyncEnumerable<WorkThreadRuntimeEvent> stream);
    void RemoveSource(string sourceId);
    // Fan-in: merges all sources into a single drain
}
```

Tag events with their source agent so that `ThreadRuntimeEventCoordinator` (or its handler registry replacement) can route appropriately.

### 5.4 Help system integration

The help system needs two data sources:
1. **`ICommandRegistry`** — lists all registered commands with names, descriptions, syntax
2. **`IShortcutRegistry`** — lists all keyboard shortcuts with names, descriptions, gestures

A `/help` command (or `?` keystroke) queries both registries and renders a formatted help document in the timeline or a popup. This is only possible once the command pipeline (§5.1) and shortcut registry (§3.6) exist.

### 5.5 Panel/view extensibility

Currently, adding a new panel or view requires:
1. Create the view class
2. Create a ViewModel
3. Create a coordinator
4. Wire into `CodeAltaApp` constructor
5. Hook into refresh paths
6. Modify `ThreadWorkspaceView` layout

**Recommendation**: Introduce a **`PanelRegistry`** where panels self-register with a position hint (sidebar, bottom, overlay). The workspace view queries the registry and renders registered panels. New panels add a class and a registration — no modification to existing views.

This is lower priority than the command system but important for fleet UI (agent status panel, agent logs panel, etc.).

---

## 6. Priority & Sequencing

### Phase 1 — Foundation (do first, enables everything else)

| Item | Section | Impact | Effort |
|---|---|---|---|
| Extract `ShellCompositionRoot` from `CodeAltaApp` | §3.1 | High — every future feature benefits | Medium |
| Introduce `CommandPipeline` + `ICommandHandler` + `CommandRegistry` | §3.3, §5.1 | Critical — enables slash commands, help | Medium |
| Introduce `ShortcutRegistry` | §3.6 | High — enables help, discoverability | Low |
| Introduce `IRuntimeEventHandler<T>` registry | §3.5 | High — enables multi-agent events | Medium |

### Phase 2 — State decomposition (reduces complexity for feature work)

| Item | Section | Impact | Effort |
|---|---|---|---|
| Split `ShellThreadStateCoordinator` into focused managers | §3.2 | High — unblocks clean multi-agent state | High |
| Split `ThreadCommandCoordinator` into dispatch + handlers | §4.1 | Medium — cleaner command ownership | Medium |
| Introduce `IAgentRegistry` (single-agent impl initially) | §3.4, §5.2 | High — foundation for fleet | Medium |

### Phase 3 — Cleanup & polish

| Item | Section | Impact | Effort |
|---|---|---|---|
| Split `ChatMarkdownFormatter` (800 LOC) | §4.2 | Medium — maintainability | Low |
| Split `SessionUsageFormatter` (505 LOC) | §4.2 | Medium — maintainability | Low |
| Split `ChatSelectorCoordinator` (415 LOC) | §4.3 | Medium — testability | Medium |
| Formalize naming (Coordinator vs. Synchronizer vs. PopupController) | §4.5 | Low — consistency | Low |
| Introduce interfaces for key services | §4.4 | Medium — testability, extensibility | Medium |
| Extend `RuntimeEventPump` for multiple sources | §5.3 | Medium — multi-agent prerequisite | Medium |

### Phase 4 — Advanced extensibility (when fleet work begins)

| Item | Section | Impact | Effort |
|---|---|---|---|
| Implement full `AgentRegistry` with multi-agent support | §5.2 | Critical for fleet | High |
| Implement `CompositeEventPump` | §5.3 | Critical for fleet | Medium |
| Implement `PanelRegistry` for dynamic views | §5.5 | Useful for fleet UI | Medium |
| Add agent identity to timeline entries | §3.4 | Required for fleet UX | Medium |

---

## 7. What's Working Well — Don't Touch

These patterns are solid and should be preserved:

- **`[Bindable]` partial property pattern** — consistent across all ViewModels, clean two-way binding with `.Bind` accessor
- **Projection + Builder pattern** — immutable snapshots with structural equality for change detection (`PromptComposerProjection`, `SidebarTreeProjection`, `ThreadTabStripProjection`)
- **Typed collaboration contexts** (`App/Context/`) — thin adapters that reduce callback sprawl in coordinator constructors
- **`IUiDispatcher` abstraction** — clean threading model with `CheckAccess()`, `Post()`, `InvokeAsync()`
- **Formatter pattern** — pure static functions for data → markup conversion (just need size reduction in a few cases)
- **`CodeAltaOwnedServices`** — clean factory with clear ownership semantics and proper disposal
- **Event merging in `CodeAltaShellController`** — combining consecutive `AgentContentDeltaEvent` to reduce UI refresh thrashing
- **Deferred bootstrap via `DeferredCodeAltaApp`** — UI renders immediately, heavy initialization happens asynchronously
- **No circular dependencies in `Presentation/`** — clean acyclic dependency graph with `Styling` as the leaf hub

---

## 8. Summary

The CodeAlta frontend has a strong architectural foundation after two refactoring rounds. The MVVM-coordinator pattern, the projection system, and the threading model are all sound.

The primary risks for the next wave of features are:

1. **No command system** — slash commands and help have no interception point
2. **Single-agent assumption** baked into connection, events, and state
3. **`CodeAltaApp`** still accumulates wiring for every feature
4. **`ShellThreadStateCoordinator`** holds too much state in one place
5. **Event dispatch** doesn't scale with new event types

Addressing items 1–3 in Phase 1 would provide the most leverage: the command pipeline enables the entire user-facing command/help system, the composition root extraction stops `CodeAltaApp` from growing, and the event handler registry makes the event system open for extension.

The multi-agent foundation (Phase 2) should follow closely, as the `AgentRegistry` abstraction can be introduced early with a single-agent implementation, making fleet support a gradual evolution rather than a breaking rewrite.
