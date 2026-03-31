# CodeAlta Frontend v5 Remaining Work Plan

TEMPORARY WORKING PLAN. DO NOT COMMIT THIS FILE.

Reference:
- [alta_v5_improvements.md](C:\code\CodeAlta\alta_v5_improvements.md)
- review findings from 2026-03-31

This plan only covers the material work still remaining after the v5 refactor commits already landed.
It is intended as a short implementation brief for the next engineer, not as a permanent project document.

## Current Status

Completed enough to treat as baseline:
- composition extraction exists via `CodeAltaFrontendComposition`
- textual command/help pipeline exists
- unified command metadata exists
- runtime event handling is split into reducer and renderer
- `ShellSelection`, `WorkspaceTarget`, and split thread-state wrapper types exist
- draft/thread selection and persisted restore now flow through `ShellSelection`
- chat selector control state lives in view models instead of `Select<T>` adapters
- `CodeAltaFrontendCallbacks` is smaller and some guardrails now cover the reduced surface

Still incomplete in a way that matters for future feature work:
- the app still operates mostly as a thread-centric shell
- `ShellWorkspaceContext` still crosses UI/control boundaries with `Visual` and `OpenThreadState`
- `CodeAltaApp` still owns too much wiring and too many callback relays
- command routing is centralized rather than registry/handler-driven
- `OpenThreadState` still carries a broad pass-through surface
- fleet/agent readiness is mostly type scaffolding, not integrated behavior

## Priority Order

Implement in this order:

- [x] Finish shell selection/workspace-target integration.
- [ ] Finish context-boundary cleanup.
- [ ] Reduce `CodeAltaApp` and callback-bag wiring further.
- [ ] Make the command system extensible instead of switch-driven.
- [ ] Deepen the `OpenThreadState` split.
- [ ] Only then do fleet/agent readiness work if the next feature set needs it.

## Workstream 1: Make `ShellSelection` The Real State Model

Goal:
- stop treating `ShellSelection` as a thin wrapper around the old booleans
- make selection/state transitions flow through `ShellSelection` and `WorkspaceTarget` directly

Tasks:
- [x] Make `ShellSelectionState.Selection` the primary shell-selection source of truth.
- [x] Stop exposing and consuming `DraftTabOpen`, `GlobalScopeSelected`, `SelectedProjectId`, and `SelectedThreadId` as the main API surface in app-layer collaborators.
- [x] Update `ThreadSelectionContext` to expose `ShellSelection` and targeted helper methods instead of the legacy booleans.
- [x] Update `ShellWorkspaceCoordinator`, `ThreadTabStripCoordinator`, `ChatSelectorCoordinator`, prompt projection logic, and related status helpers to branch on `ShellSelection` or `WorkspaceTarget`.
- [x] Decide how draft selection and thread selection persist locally.
- [x] Update machine-local restore state so selection persistence is not thread-only forever.
- [x] Keep compatibility with current draft/thread UX while removing boolean-driven decision trees.

Acceptance criteria:
- [x] Major app/presentation flows no longer need the legacy selection booleans.
- [x] Selection restore/fallback logic operates through `ShellSelection`.
- [x] Current draft/thread behavior is unchanged.
- [x] Tests cover selection transitions, close/fallback behavior, and persisted restore behavior.

## Workstream 2: Finish Context Boundary Cleanup

Goal:
- views own terminal controls
- app/services operate on state, intents, and narrow view adapters only

Tasks:
- [x] Replace `ChatSelectorUiContext` with a model that no longer manipulates `Select<T>` controls directly from app logic.
- [ ] Replace `ShellWorkspaceContext` methods that pass `Visual`, layout ownership, or `OpenThreadState` across boundaries.
- [ ] Keep concrete `Visual`, `Select<T>`, `TabControl`, and splitter ownership inside view/presentation code.
- [ ] If a view adapter is still needed, name it explicitly as a view adapter and keep it minimal.
- [ ] Remove any remaining direct prompt/editor dependency from core logic paths.

Acceptance criteria:
- [ ] App/context classes no longer own generic access to terminal controls.
- [ ] Control mutation lives in view/presentation code.
- [ ] Architecture tests fail if app/context code starts exposing concrete controls again.

## Workstream 3: Shrink `CodeAltaApp` Further

Goal:
- make `CodeAltaApp` a lifecycle host plus shell root owner, not a relay bucket

Tasks:
- [x] Reduce the size and responsibility of `CodeAltaFrontendCallbacks`.
- [x] Move more callback wiring out of `CodeAltaApp` and into composition-owned collaborators.
- [x] Remove proxy properties in `CodeAltaApp` that simply mirror thread-state booleans and ids.
- [ ] Prefer passing focused collaborators into composition instead of dozens of function delegates.
- [x] Keep runtime startup, disposal, root creation, and terminal loop ownership in `CodeAltaApp`.

Acceptance criteria:
- [x] `CodeAltaApp` no longer defines the shell in terms of legacy selection fields.
- [x] Callback surface is materially smaller and more focused.
- [ ] Architecture tests explicitly guard against `CodeAltaApp` regressing into the main wiring bucket.

## Workstream 4: Make Commands Extensible

Goal:
- adding a new textual/local shell command should not require editing multiple central switches

Tasks:
- [ ] Replace the `ShellInputRouter` alias switch and `ShellInputCoordinator` intent switch with a command registration/handler model.
- [ ] Keep the existing `ShellCommandCatalog` as metadata source of truth unless a better minimal design emerges.
- [ ] Introduce command handlers for current built-ins: help, abort, compact, close, queue, delegate.
- [ ] Keep plain prompt send and steer as explicit non-command intents.
- [ ] Preserve current `/help`, `?`, and known command behavior.

Acceptance criteria:
- [ ] New command addition is metadata + handler registration, not router edits in multiple files.
- [ ] Tests cover unknown commands, alias resolution, and handler dispatch.
- [ ] `ThreadCommandCoordinator` remains free of text-command parsing.

## Workstream 5: Deepen The `OpenThreadState` Split

Goal:
- make the split between domain state, runtime state, workspace state, and timeline state meaningful in usage, not just in type names

Tasks:
- [ ] Reduce pass-through properties on `OpenThreadState`.
- [ ] Move consumers toward `Session`, `Workspace`, and `TimelineState` explicitly.
- [ ] Decide whether a smaller wrapper is still useful after consumer cleanup.
- [ ] Avoid introducing more mixed state into `OpenThreadState`.

Acceptance criteria:
- [ ] Downstream code uses the specific layer it needs.
- [ ] `OpenThreadState` stops growing as the default place to put any per-thread field.
- [ ] Tests cover the reduced state shape where needed.

## Workstream 6: Fleet/Agent Readiness Only If Needed Next

Goal:
- prepare the frontend for agent-aware surfaces without inventing a parallel agent architecture

Tasks:
- [ ] Keep reusing `AgentHub`, `AgentIdentity`, and `AgentScope`.
- [ ] Extend frontend-facing runtime/event projections with agent identity only when the next feature set actually needs it.
- [ ] Ensure `ShellSelection` persistence and workspace rendering can represent agent/fleet targets cleanly.
- [ ] Do not add a frontend `AgentRegistry`.

Acceptance criteria:
- [ ] No competing frontend-owned agent lifecycle abstraction exists.
- [ ] Agent/fleet targeting can be represented without reintroducing selection booleans.
- [ ] Event/timeline identity work is added in orchestration-aligned models, not UI-local hacks.

## Guardrails To Add Or Tighten

- [ ] `CodeAltaApp` should not re-grow legacy selection fields or broad relay responsibilities.
- [ ] App/context classes should not expose `Visual`, `Select<T>`, `TabControl`, or other terminal controls unless explicitly named as view adapters.
- [x] New selection growth should not happen through additional booleans or thread-id-only persistence.
- [ ] Help/shortcut/command discovery must continue to come from the unified command metadata model.
- [x] Fleet work must continue to reuse `AgentIdentity`, `AgentScope`, and `AgentHub`.

## Suggested Delivery Sequence

Recommended commit slices:

- [x] Convert selection consumers to `ShellSelection`/`WorkspaceTarget`.
- [x] Update persisted selection/restore model and tests.
- [ ] Replace control-heavy app contexts with narrower adapters/capabilities.
- [x] Reduce `CodeAltaApp` callback wiring and add guardrails.
- [ ] Convert command routing to handler/registration model.
- [ ] Trim `OpenThreadState` pass-through surface.
- [ ] Only if required, add agent-aware selection/event projection support.

## Verification

For each slice:
- [x] Preserve current draft/thread UX unless the change explicitly expands it.
- [x] Add or update regression tests before moving to the next slice.
- [x] Run `dotnet test CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-restore -v minimal /p:UseSharedCompilation=false /nodeReuse:false`.
- [x] Run additional targeted test projects if orchestration/event models change.

## Explicit Non-Goals For This Plan

- [ ] No large folder rewrite.
- [ ] No new frontend-owned agent registry.
- [ ] No speculative fleet UI before the selection/event foundations are ready.
- [ ] No drive-by formatter or naming cleanup unless directly needed by the work above.
