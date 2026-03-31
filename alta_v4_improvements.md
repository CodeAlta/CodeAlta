# CodeAlta Frontend — v4 Architecture Review and Recommendations

Date: 2026-03-31  
Scope: `src/CodeAlta/`  
Audience: maintainers of the CodeAlta TUI/frontend

## Executive Summary

The frontend is in a materially better state than it was before the v1/v2/v3 cleanup work. The code now has real seams:

- `RuntimeEventPump` is the single runtime event consumer.
- `CodeAltaApp` is smaller than before and protected by architecture tests.
- Timeline, sidebar, tab-strip, usage, and formatting logic are no longer buried inside one giant view class.
- `ThreadWorkspaceView` and `SidebarView` are real view objects instead of incidental code inside the app host.

That said, the frontend is still not yet shaped for the next wave of features:

- textual commands (`/`, `?`, command/help discovery)
- multi-agent and squad/fleet workflows
- more pluggable frontend features
- long-term maintainability as the TUI keeps growing

The main remaining problem is not “the code is messy”. The main problem is that the code is still heavily organized around a single selected thread and a single app host that knows too much. That is workable for the current shell, but it will become a structural constraint for commands, help, internal agents, and richer work surfaces.

This document recommends an incremental v4 direction, not a rewrite.

## What Is Already Working Well

These parts should be preserved:

1. `Views/CodeAltaApp.cs` is now mostly an application shell/facade instead of a UI god class.
2. `src/CodeAlta.Tests/ArchitectureGuardrailTests.cs` is doing useful work and should continue to be expanded.
3. Presentation-only logic is increasingly isolated in `Presentation/*`.
4. The bindable view-model approach is consistent enough to scale further.
5. `RuntimeEventPump` + `CodeAltaShellController` is a good direction for async runtime isolation.
6. The code generally prefers specific coordinators over random helper methods.

## Current Snapshot

Approximate file sizes in the current tree:

| File | Lines | Observation |
|---|---:|---|
| `src/CodeAlta/Views/CodeAltaApp.cs` | 709 | Still the central composition and routing point |
| `src/CodeAlta/App/ThreadCommandCoordinator.cs` | 568 | Too much command/session policy in one place |
| `src/CodeAlta/App/ShellThreadStateCoordinator.cs` | 548 | Still owns too many kinds of state |
| `src/CodeAlta/Views/ThreadWorkspaceView.cs` | 547 | Large view with command registration and footer orchestration |
| `src/CodeAlta/App/ThreadRuntimeEventCoordinator.cs` | 425 | Mixes event policy, state mutation, and rendering |
| `src/CodeAlta/Presentation/Workspace/ChatSelectorCoordinator.cs` | 368 | Good extraction, but still coupled to concrete controls |
| `src/CodeAlta/App/CodeAltaShellController.cs` | 317 | Reasonable, but still part of a wider central host model |
| `src/CodeAlta/App/SidebarCoordinator.cs` | 302 | Healthy direction overall |
| `src/CodeAlta/App/ShellWorkspaceCoordinator.cs` | 264 | Important refresh hub, still imperative |

The structure is better than before, but the main workflows still converge through a small number of large classes.

## Main Findings

### 1. `CodeAltaApp` is still the mandatory wiring point for almost every feature

`src/CodeAlta/Views/CodeAltaApp.cs` still constructs and wires nearly every coordinator:

- `ShellThreadStateCoordinator`
- `ShellWorkspaceCoordinator`
- `ThreadPromptQueueCoordinator`
- `ThreadRuntimeEventCoordinator`
- `ThreadCreationCoordinator`
- `ThreadCommandCoordinator`
- `ChatSelectorCoordinator`
- `ThreadTabStripCoordinator`

This is better than putting all behavior directly in `CodeAltaApp`, but it still means every new feature lands in the same constructor and usually needs new callbacks or contexts there.

Impact:

- Commands/help will need new routing services and likely new UI integration.
- Fleet/squad features will need new selection, orchestration, and status surfaces.
- Every addition will keep inflating the app constructor and its relay surface.

Recommendation:

- Extract a dedicated frontend composition root, for example `CodeAltaFrontendComposition` or `ShellCompositionRoot`.
- Keep `CodeAltaApp` focused on lifecycle only: prepare, tick, dispose, ensure root view.
- Move object graph construction out of `CodeAltaApp`.

This is the single highest-value structural change.

### 2. The current “contexts” are still mostly delegate bags, not real boundaries

Examples:

- `src/CodeAlta/App/Context/ChatSelectorUiContext.cs`
- `src/CodeAlta/App/Context/ShellWorkspaceContext.cs`
- `src/CodeAlta/App/Context/ThreadCommandContext.cs`

This is better than raw constructor lambdas, but several of these contexts still expose:

- concrete controls (`Select<T>`, `ChatPromptEditor`, `VSplitter`, `Visual`)
- UI-thread access details
- arbitrary imperative actions from the app host

That means application logic is still coupled to concrete terminal controls and refresh choreography.

Examples:

- `ThreadCommandCoordinator` reads prompt text from `ChatPromptEditor`.
- `ChatSelectorCoordinator` manipulates `Select<T>` controls directly.
- `ShellWorkspaceCoordinator` swaps concrete visuals into the splitter.

Impact:

- Harder to add alternative surfaces, command palettes, or non-editor command entry points.
- Harder to test command and selection logic independently from the terminal UI.
- Harder to introduce richer shells for fleet/squad views without threading more control handles everywhere.

Recommendation:

- Replace control-oriented contexts with intent/state-oriented interfaces or internal services.
- Coordinators should read and write view-model state, not terminal controls.
- Views should own control wiring; app/services should own intents and state transitions.

In short: keep the typed contexts idea, but make them represent frontend capabilities, not UI handles.

### 3. The state model is still built around “one current thread”, which is too narrow for fleet/squad work

Today the main shell state is essentially:

- `DraftTabOpen`
- `GlobalScopeSelected`
- `SelectedProjectId`
- `SelectedThreadId`
- open thread ids
- per-thread `OpenThreadState`

This is visible in:

- `src/CodeAlta/App/ShellThreadStateCoordinator.cs`
- `src/CodeAlta/App/Context/ThreadSelectionContext.cs`
- `src/CodeAlta/App/State/OpenThreadState.cs`

That model is good for a classic single-conversation TUI. It is not a good foundation for:

- multiple active agents in one workspace
- squad/fleet dashboards
- grouped delegated work
- command scopes like “act on selected agent”, “act on selected thread”, “act on squad”
- richer views where the main surface is not just one thread timeline

Current delegation is still basically “create a child internal thread from the selected thread” inside `ThreadCommandCoordinator`. It is useful, but it is not yet a first-class fleet model.

Recommendation:

- Introduce a first-class shell target/work surface model.
- Do not keep extending booleans like `GlobalScopeSelected` plus `SelectedThreadId`.
- Add explicit concepts such as:
  - `ShellSurface`
  - `ShellSelection`
  - `WorkspaceTarget`
  - `AgentNode` / `ThreadNode`
  - `FleetSession` or `SquadSession`

Concrete goal:

- the shell should be able to select “draft workspace”, “thread”, “agent”, “squad”, or later “task/plan” without baking that into boolean combinations.

### 4. There is still no textual command pipeline

Right now, prompt submission is fundamentally “send chat text”, with a steer variant and a few direct button/shortcut commands.

This is centered around:

- `src/CodeAlta/App/ThreadCommandCoordinator.cs`
- `src/CodeAlta/Views/ThreadWorkspaceView.cs`

The current model has keyboard commands in the editor and command bar, but no textual command router. That will make `/...` commands and `?` help awkward to add cleanly.

Recommendation:

- Introduce a dedicated input/command pipeline before backend dispatch.

Suggested shape:

1. Prompt editor submits raw text.
2. `ShellInputRouter` classifies it:
   - empty input
   - textual command (`/compact`, `/agent`, `/help`, `/queue`, `/close`)
   - shortcut help (`?`)
   - normal prompt
   - future: directed prompt syntax (`@planner`, `@reviewer`, etc.)
3. Commands become typed intents, not ad hoc `if` checks in thread send logic.
4. Only normal prompts reach backend send/steer execution.

This needs to exist before the command surface expands.

### 5. Runtime event handling still mixes three responsibilities

`src/CodeAlta/App/ThreadRuntimeEventCoordinator.cs` currently does all of the following:

- mutates `WorkThreadDescriptor`
- mutates `OpenThreadState`
- renders timeline interactions
- updates usage
- drives queued-prompt draining
- controls status transitions

This is functional, but it makes runtime behavior harder to reason about and harder to extend.

For multi-agent/fleet work, runtime events will become even richer:

- parent/child relationships
- fleet-level progress
- role-specific events
- orchestration/task milestones
- command/help/system events from the shell itself

Recommendation:

- Split event processing into at least two layers:
  - state reduction/projection
  - rendering/presentation

Suggested decomposition:

- `ThreadRuntimeStateReducer`
- `ThreadTimelineRenderer`
- `ThreadUsageTracker`
- `QueueDrainPolicy`

The important point is not naming. The important point is to stop using one class as the place where event meaning, state mutation, and rendering are all combined.

### 6. `OpenThreadState` still combines domain state, session state, and presentation state

`src/CodeAlta/App/State/OpenThreadState.cs` currently holds:

- the catalog descriptor (`WorkThreadDescriptor`)
- session/runtime state (`ThreadSessionState`)
- timeline presenter (`ThreadTimelinePresenter`)
- tab view-model (`ThreadTabViewModel`)

This is a pragmatic wrapper, but it is not a stable long-term model.

Problems:

- It mixes durable-ish state, ephemeral runtime state, and UI state.
- It makes thread features harder to reuse outside the main thread tab surface.
- It reinforces the idea that “thread state” and “tab state” are the same thing.

Recommendation:

- Split this into clearer layers:
  - descriptor/domain snapshot
  - runtime/session state
  - workspace projection/view state
  - timeline presenter/render state

Even if you keep a convenience wrapper, it should no longer be the unit where all concerns are merged.

### 7. The frontend is still organized mostly by technical layer, not by feature slice

Current layout:

- `App/`
- `Presentation/`
- `ViewModels/`
- `Views/`

This is workable, but it means every meaningful feature is spread across several folders.

Examples:

- thread commands touch `App`, `Views`, `Presentation`, and `ViewModels`
- navigator features touch `App`, `Presentation.Sidebar`, `Views`, and dialogs
- future command/help features will likely touch almost every top-level frontend folder

Recommendation:

- Do not rewrite the whole tree now.
- For new major features, introduce feature slices first.

Examples:

- `Frontend/Commands/*`
- `Frontend/Fleet/*`
- `Frontend/Navigator/*`
- `Frontend/Threads/*`
- `Frontend/Workspace/*`

Inside a slice, keep local state/presentation/view helpers together where that reduces jumping between folders.

This is especially important for commands/help and fleet/squad work, which are product features, not just technical layers.

### 8. Command discoverability and help are fragmented

`ThreadWorkspaceView` currently registers many useful commands with shortcuts:

- session usage
- thread info
- expand prompt
- steer
- delegate
- abort
- clear queue
- compact
- close tab

That is good, but the command knowledge is embedded in view construction. There is no unified registry that can power:

- inline help
- `?` help
- slash command discovery
- future command palette
- context-sensitive command lists

Recommendation:

- Introduce a `ShellCommandRegistry`.
- Commands should carry metadata:
  - id
  - label
  - description
  - gestures
  - textual aliases
  - scope
  - availability predicate
  - help category

Then:

- `ThreadWorkspaceView` binds visible commands from the registry.
- `?` and `/help` read from the same registry.
- future slash commands resolve from the same source of truth.

### 9. There are still a few parallel or stray abstractions that should be cleaned up

`src/CodeAlta/Services/ChatAgentConnection.cs` appears to be a standalone chat-session abstraction that is not used by the current frontend shell and is only exercised by tests.

That is not a critical problem, but it is a signal:

- either this abstraction belongs to another assembly or test support area
- or the frontend now has two mental models for “chat agent connection” and “thread runtime service”

Recommendation:

- prune or relocate frontend-local abstractions that no longer participate in the actual shell architecture
- keep the frontend project focused on the shell model it really uses

This will matter more as the TUI grows and conceptual duplication becomes more expensive.

## Recommended v4 Direction

### A. Introduce a real composition root

Target outcome:

- `CodeAltaApp` no longer constructs the full object graph.
- it receives a prebuilt shell bundle or composition object.

Suggested structure:

- `CodeAltaFrontendComposition`
- `CodeAltaShellHost`
- `CodeAltaShellServices`

Minimum goal:

- move object construction and wiring out of `CodeAltaApp`
- leave behavior unchanged

### B. Add a shell intent model

Before adding many more features, define typed frontend intents:

- `SendPromptIntent`
- `SteerPromptIntent`
- `RunShellCommandIntent`
- `OpenHelpIntent`
- `CreateDelegatedThreadIntent`
- `AbortThreadIntent`
- `CompactThreadIntent`
- future: `CreateSquadIntent`, `FocusAgentIntent`, `OpenFleetSurfaceIntent`

This should become the application language that views trigger and coordinators handle.

### C. Add a first-class command system

The command system should unify:

- keyboard gestures
- command bar actions
- textual slash commands
- `?` help
- future palette/search-driven command discovery

This is the right moment to do it because the command surface is still manageable.

### D. Promote shell state into an explicit store/reducer model

This does not need a full MVU rewrite. A pragmatic store is enough.

What matters:

- one place owns shell selection/workspace state
- one place owns thread runtime/session state
- reducers handle events and intents
- projections feed the UI

If you do not formalize this soon, fleet/squad work will likely end up as more booleans and more callback-based coordinators.

### E. Model fleet/squad as a first-class frontend concept

Do not bolt it onto `ThreadCommandCoordinator`.

Needed concepts:

- parent/child relationships
- grouped agent/thread surfaces
- selection semantics wider than “selected thread id”
- status aggregation
- navigation model for agent trees or squads

The right place to solve this is the shell state model, not just the command layer.

## Concrete Incremental Plan

### Phase 1: Foundation

1. Extract a frontend composition root out of `CodeAltaApp`.
2. Add `ShellInputRouter` and typed shell intents.
3. Add a centralized `ShellCommandRegistry`.
4. Add an architecture guardrail that textual command parsing does not live inside `ThreadCommandCoordinator`.

### Phase 2: State cleanup

1. Split `OpenThreadState` into clearer runtime/view layers.
2. Split `ShellThreadStateCoordinator` into smaller focused collaborators.
3. Reduce direct control access from contexts; move logic to view-model/state services.

### Phase 3: Event cleanup

1. Decompose `ThreadRuntimeEventCoordinator`.
2. Separate runtime state reduction from timeline rendering.
3. Add tests around reducer behavior without requiring terminal UI objects.

### Phase 4: Fleet readiness

1. Introduce an explicit shell surface/selection model.
2. Add fleet/squad-aware commands and help metadata.
3. Build the first multi-agent surface on top of the new state/command system, not on top of special cases in thread send logic.

## Suggested Guardrails To Add

The current architecture tests are useful. Expand them with the next set of rules:

1. `ThreadCommandCoordinator` should not parse textual slash commands directly.
2. App/context classes should not expose `Visual`, `Select<T>`, or `ChatPromptEditor` unless the class is explicitly a view adapter.
3. New fleet/squad features should not extend the shell with additional selection booleans.
4. `OpenThreadState` should not keep growing with more mixed concerns.
5. Help/command metadata should come from a registry, not duplicated in multiple views.
6. `CodeAltaApp` should stay below a stricter size/constructor budget after composition extraction.

## What I Would Not Do

I would not:

- rewrite the frontend into a brand-new framework
- convert everything to interfaces for purity
- move files around just to satisfy namespace aesthetics
- do a massive folder rewrite before introducing the missing abstractions

The right strategy is incremental:

- extract composition
- add intents and command routing
- formalize shell state
- then build fleet/squad on top of that

## Priority Order

If only a few things are done now, they should be these:

1. Extract composition from `CodeAltaApp`.
2. Add a real textual command/help pipeline.
3. Replace control-oriented contexts with state/intention-oriented boundaries.
4. Introduce a shell surface/selection model that is not tied to one selected thread.

Those four changes will do the most to keep the frontend navigable and extensible as the TUI grows.
