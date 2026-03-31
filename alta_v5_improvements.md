# CodeAlta Frontend — v5 Pragmatic Architecture Improvement Plan

Date: 2026-03-31  
Scope: `src/CodeAlta/` frontend/TUI surface  
Inputs reviewed: `alta_v3_improvements.md`, `alta_v4_improvements.md`, current frontend code, and current orchestration/runtime docs

## Executive Summary

Both prior documents identify real pressure points, but neither should be adopted wholesale.

`v3` is strongest when it pushes for:

- a real command pipeline
- composition extraction from `CodeAltaApp`
- decomposition of oversized coordinators
- preparation for future help and fleet workflows

`v4` is strongest when it pushes for:

- replacing control-heavy "contexts" with better boundaries
- moving beyond the current "one selected thread + a few booleans" shell model
- separating runtime state changes from rendering concerns
- introducing command/help as first-class frontend concepts

Where both documents overreach is in inventing too many new abstractions at once. The current codebase already has meaningful orchestration primitives:

- `AgentHub`
- `AgentIdentity`
- `AgentScope`
- `WorkThreadRuntimeService`
- internal delegated threads with `ParentThreadId`

The frontend should converge on those primitives, not create a parallel architecture beside them.

The right v5 direction is incremental and product-driven:

1. Extract composition from `CodeAltaApp`.
2. Add a typed shell input/command pipeline.
3. Replace UI-handle contexts with state/intention boundaries.
4. Introduce an explicit shell selection/workspace-target model.
5. Split thread runtime state from tab/view state.
6. Decompose runtime event handling into reduction + rendering.
7. Build future fleet/help features on top of `AgentHub` and `WorkThreadRuntimeService`, not new frontend-owned registries and pumps.

## What The Current Code Actually Shows

The current frontend is materially better than the pre-refactor shell, but the next wave of features will stress a few remaining seams.

Grounded observations from the current tree:

- `Views/CodeAltaApp.cs` is still the main construction and wiring point at 709 LOC.
- `App/ShellThreadStateCoordinator.cs` still owns too much: catalog state, selection, open tabs, fallback logic, view-state persistence, delegated-thread registration.
- `App/ThreadCommandCoordinator.cs` still combines prompt dispatch, delegation, compaction, queue behavior, permission callbacks, and user-input callbacks.
- `App/ThreadRuntimeEventCoordinator.cs` still combines event interpretation, thread/session mutation, usage updates, queue draining, and timeline rendering.
- `App/Context/ChatSelectorUiContext.cs` and `App/Context/ShellWorkspaceContext.cs` still expose concrete controls like `Select<T>`, `ChatPromptEditor`, `Visual`, and `VSplitter`.
- `App/State/OpenThreadState.cs` still mixes durable thread data, runtime/session data, timeline presenter state, and tab view state.
- Shell selection is still modeled largely as `DraftTabOpen`, `GlobalScopeSelected`, `SelectedProjectId`, `SelectedThreadId`, plus `OpenThreadIds`.

At the same time, important future-facing pieces already exist outside the frontend:

- `CodeAlta.Orchestration.AgentIdentity`
- `CodeAlta.Orchestration.Runtime.AgentHub`
- `CodeAlta.Orchestration.Runtime.WorkThreadRuntimeService`
- durable internal thread relationships via `WorkThreadDescriptor.ParentThreadId`

This matters because future fleet/squad work should extend the existing runtime model, not bypass it.

## Integrated v5 Direction

### 1. Extract a real frontend composition root

This is the highest-leverage structural change.

`CodeAltaApp` should stop constructing the entire object graph. It should become a lifecycle host around a prebuilt shell composition.

Recommended outcome:

- add `CodeAltaFrontendComposition` or `ShellCompositionRoot`
- move coordinator/context/view-model construction there
- keep `CodeAltaApp` focused on prepare, tick, ensure root view, and dispose

This keeps growth mechanical instead of behavioral and reduces the chance that every new feature expands the same constructor and callback surface.

### 2. Add a typed shell input pipeline before backend dispatch

This is the most important product-facing change.

Today prompt submission is effectively "read text from `ChatPromptEditor` and send it". That is too narrow for:

- `/help`
- `/compact`
- `/abort`
- `/close`
- `/queue`
- `?` help
- future directed prompt syntax such as `@planner`

Recommended shape:

1. Prompt/editor submits raw input.
2. `ShellInputRouter` classifies it.
3. Classification produces a typed shell intent.
4. A command/intent handler executes locally or forwards to chat dispatch.

Suggested first-class intents:

- `SendPromptIntent`
- `SteerPromptIntent`
- `RunTextCommandIntent`
- `OpenHelpIntent`
- `AbortThreadIntent`
- `CompactThreadIntent`
- `CloseTabIntent`
- `DelegateThreadIntent`

This should be added before expanding the command/help surface further.

### 3. Use one unified command metadata model

`v3` proposed a command registry and a separate shortcut registry. `v4` proposed a shell command registry. The right move is one source of truth, not two.

Recommended model:

- command id
- label
- description
- keyboard gestures
- textual aliases
- scope/context
- availability predicate
- category/help grouping

That single registry should feed:

- command bar entries
- keyboard help
- `/help`
- future command palette/discovery UI

Do not build a standalone `ShortcutRegistry` unless the framework eventually forces it. The command system should own shortcut metadata.

### 4. Replace control-oriented contexts with intent/state boundaries

This is one of the best points in `v4`.

The current typed contexts were a good intermediate refactor, but several are still mostly delegate bags over concrete UI controls. That keeps app logic coupled to terminal widgets.

Recommended direction:

- views own `Select<T>`, `ChatPromptEditor`, `Visual`, and layout controls
- app/services operate on shell state, shell intents, and small view adapters
- coordinators should not need direct knowledge of concrete controls unless they are explicitly view-layer adapters

Pragmatic target:

- narrow `ChatSelectorUiContext` into a view adapter owned by the workspace view layer
- narrow `ShellWorkspaceContext` so it exposes shell capabilities instead of control getters
- stop reading prompt/editor state directly inside core command logic

This improves testability and makes alternate command surfaces possible later.

### 5. Replace the current selection booleans with an explicit shell target model

This is the most important state-model change, but it should stay pragmatic.

`v4` is right that the current model is too tied to a single selected thread. However, a full store/reducer rewrite is not required yet.

Recommended next model:

- `ShellSelection`
- `WorkspaceTarget`
- `ShellSurface`

Minimum capabilities:

- draft workspace target
- thread target
- agent target
- future squad/fleet target

This should replace the current growth pattern of combining:

- `DraftTabOpen`
- `GlobalScopeSelected`
- `SelectedProjectId`
- `SelectedThreadId`

The goal is not abstraction purity. The goal is to stop encoding future work surfaces as boolean combinations.

### 6. Split `OpenThreadState` into clearer layers

Both prior documents are correct that `OpenThreadState` is too blended.

The split should be modest and purposeful:

- thread descriptor/domain snapshot
- thread runtime/session state
- tab/workspace view state
- timeline presenter/render state

The shell can still keep a convenience wrapper if that helps ergonomics, but it should stop being the place where all four concerns accumulate indefinitely.

This will matter for:

- reused thread data outside the tab strip
- fleet/agent dashboards
- future non-thread work surfaces

### 7. Decompose runtime event handling without moving fan-in into the frontend

This needs to be handled carefully.

`v3` and `v4` are right that `ThreadRuntimeEventCoordinator` owns too much. But proposals like a frontend `CompositeEventPump` are the wrong layer for the problem.

`WorkThreadRuntimeService` already provides a normalized runtime event stream across active threads. Future multi-agent fan-in should continue to live there or in orchestration, not in the TUI.

Recommended frontend split:

- runtime state reducer
- timeline/event renderer
- usage updater
- queue-drain policy

Keep the event source model below the frontend. Extend event payloads with agent/source identity when fleet work needs it.

### 8. Align fleet/squad work with `AgentHub`, not a new frontend `AgentRegistry`

This is the most important correction to `v3`.

Creating a new frontend-owned `AgentRegistry` would duplicate an existing concept. `AgentHub` already owns agent identity, sessions, and orchestration events. The frontend should consume or project that state, not redefine it.

Recommended direction:

- extend `WorkThreadRuntimeEvent` or related frontend-facing models with source agent identity when needed
- add shell projections for agent status, agent/thread relationships, and grouped work surfaces
- reuse `AgentIdentity` and `AgentScope` as the canonical concepts

If a frontend-specific abstraction is needed, it should be a read/projection layer such as:

- `ShellAgentProjection`
- `FleetWorkspaceProjection`
- `ShellSelection` targeting an `AgentIdentity` or grouped workspace

Do not introduce a second authoritative lifecycle manager for agents in the frontend layer.

### 9. Prefer feature slices for new work, not a large folder rewrite

`v4` is right that the current structure spreads features across technical layers. But a broad reorganization now would create churn without enough value.

Recommended rule:

- leave existing folders largely in place
- introduce feature slices only for new major work such as commands/help and fleet

Examples:

- `Frontend/Commands/*`
- `Frontend/Help/*`
- `Frontend/Fleet/*`

Do this opportunistically as new features land. Do not spend a refactor cycle moving stable code only for aesthetics.

## What To Keep From v3

- composition extraction from `CodeAltaApp`
- command pipeline before chat dispatch
- decomposition of `ThreadCommandCoordinator`
- decomposition of `ThreadRuntimeEventCoordinator`
- recognition that shortcut/help discoverability needs a proper model
- caution that `ShellThreadStateCoordinator` is too large

## What To Keep From v4

- critique of control-heavy contexts
- stronger shell selection/workspace-target model
- split between runtime reduction and rendering
- warning that the current shell is too centered on one selected thread
- recommendation to keep changes incremental rather than rewriting the frontend

## What To Change From v3/v4

- Replace "new frontend `AgentRegistry`" with "frontend projections over `AgentHub`".
- Replace "frontend `CompositeEventPump`" with "runtime/orchestration-level event fan-in".
- Replace "command registry + shortcut registry" with one unified command metadata model.
- Replace "full reducer/store architecture now" with a pragmatic state-owner model first.
- Replace "large interface sweep" with targeted interfaces only at real seams.

## What Not To Do

- Do not build a parallel fleet runtime beside `AgentHub`.
- Do not move multi-source event fan-in into the TUI.
- Do not introduce many interfaces purely for test-mocking aesthetics.
- Do not do a wholesale folder rewrite before the missing abstractions exist.
- Do not add a plugin/panel registry yet; the product surface is still too early.
- Do not spend a cycle renaming every `Coordinator`/`Presenter` class unless it comes with a real boundary improvement.
- Do not let commands/help be implemented as scattered `if` statements inside `ThreadCommandCoordinator`.

## Recommended Incremental Plan

### Phase 1: Foundation

1. Extract a frontend composition root from `CodeAltaApp`.
2. Introduce `ShellInputRouter` and typed shell intents.
3. Introduce a unified shell command metadata model.
4. Implement `/help` and `?` on top of that same model.
5. Add architecture guardrails so command parsing does not drift into thread send logic.

### Phase 2: State and boundary cleanup

1. Narrow or replace the most control-heavy contexts.
2. Introduce explicit `ShellSelection` and `WorkspaceTarget`.
3. Start splitting `OpenThreadState` into runtime state and tab/view state.
4. Break up `ShellThreadStateCoordinator` around selection, open-tab management, and persistence responsibilities.

### Phase 3: Runtime event cleanup

1. Split `ThreadRuntimeEventCoordinator` into reducer/render/update pieces.
2. Add tests around runtime reduction without terminal controls.
3. Keep `WorkThreadRuntimeService` as the event-stream source of truth.

### Phase 4: Fleet/help expansion

1. Extend shell selection and projections to represent agents and grouped work surfaces.
2. Add agent-aware command/help metadata.
3. Add agent identity/source information to the frontend event and timeline projections.
4. Build the first fleet/squad surface on top of `AgentHub` and existing thread/orchestration concepts.

## Suggested Guardrails

Add or extend architecture tests so that:

- `CodeAltaApp` remains a lifecycle host, not a composition bucket
- textual command parsing does not live inside `ThreadCommandCoordinator`
- app contexts do not expose concrete UI controls unless explicitly marked as view adapters
- frontend state does not keep growing through more selection booleans
- help/shortcut metadata comes from the unified command model
- new fleet work reuses `AgentIdentity`/`AgentHub` concepts rather than introducing competing lifecycle abstractions

## Priority Order

If only a few things are done next, they should be:

1. composition extraction from `CodeAltaApp`
2. shell input/command/help pipeline
3. shell selection/workspace-target model
4. context boundary cleanup
5. runtime reducer/render split

That sequence improves maintainability now while also preparing the codebase for commands, help, delegated work, and future fleet-style workflows without over-engineering the frontend.
