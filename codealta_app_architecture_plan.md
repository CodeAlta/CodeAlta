# CodeAltaApp Architecture Plan

Reference: [codealta_app_architecture_improvements.md](C:\code\CodeAlta\codealta_app_architecture_improvements.md)

This document is the implementation checklist companion to the architecture proposal. It is intentionally short and task-oriented.

## Phase 1: Remove `TerminalHost`

- [x] Add `CodeAltaApp.CreateAsync(...)` to own bootstrap and lifetime setup.
- [x] Move logging initialization/shutdown ownership into `CodeAltaApp` or a small bootstrap helper.
- [x] Move database/catalog/backend/runtime-service construction out of `TerminalHost`.
- [x] Move `ImportKnownProjectsFromBackendsAsync` into a dedicated collaborator such as `KnownProjectImporter`.
- [x] Update `Program.cs` to create and run `CodeAltaApp` directly.
- [x] Delete `src/CodeAlta/App/TerminalHost.cs`.

## Phase 2: Introduce explicit UI threading and shell coordination

- [x] Add `IUiDispatcher`.
- [x] Add `TerminalUiDispatcher`.
- [x] Add `CodeAltaShellController`.
- [x] Move startup loading and refresh orchestration from `CodeAltaApp` into `CodeAltaShellController`.
- [x] Move runtime event handling entry points into `CodeAltaShellController`.
- [x] Add a dedicated `RuntimeEventPump`.
- [x] Ensure runtime events are applied on the UI thread through `IUiDispatcher`.
- [x] Remove duplicated UI-thread helper patterns over time (`PostToUi`, `ReadUiValue`, `RunOnUiThread`) in favor of the new dispatcher path.

## Phase 3: Extract real view classes

- [x] Create `CodeAltaShellView`.
- [x] Create `SidebarView`.
- [x] Create `ThreadWorkspaceView`.
- [x] Create `SessionUsagePopupView`.
- [x] Move shell layout construction out of `CodeAltaApp` partials and into view classes.
- [x] Move sidebar control creation and tree rebuild wiring into `SidebarView`.
- [x] Move thread-pane control creation and selector wiring into `ThreadWorkspaceView`.
- [x] Introduce `ShellSelectionState` for open-tab and selected-scope state ownership.
- [x] Stop duplicating workspace control ownership between `CodeAltaApp` and `ThreadWorkspaceView`.
- [x] Move remaining draft-tab and status-spinner presentation ownership out of `CodeAltaApp`.
- [x] Move the shell/controller bridge out of `CodeAltaApp` partials.
- [x] Move backend preference/config coordination out of `CodeAltaApp` partials.
- [x] Move tab-page cache ownership into `ThreadWorkspaceView`.
- [x] Keep `CodeAltaApp` focused on app lifecycle only.

## Phase 4: Introduce focused bindable view models

- [x] Expand `CodeAltaShellViewModel`.
- [x] Add `SidebarViewModel`.
- [x] Add `ThreadWorkspaceViewModel`.
- [x] Add `ThreadTabViewModel`.
- [x] Add `PromptComposerViewModel`.
- [x] Add `SessionUsageViewModel` if needed by the popup/presenter split.
- [x] Move shell/header/status/sidebar/workspace scalar state into bindable view models.
- [x] Ensure all `[Bindable]` reads and writes happen on the UI thread only.
- [x] Remove command enablement logic that depends on direct control inspection when a view-model-based alternative is available.

## Phase 5: Split thread session state from visual state

- [x] Introduce `ThreadSessionState` as non-visual per-thread state.
- [x] Move history loading flags and cached history data into `ThreadSessionState`.
- [x] Move per-thread backend/model/reasoning/usage/status state into `ThreadSessionState` and `ThreadTabViewModel` as appropriate.
- [x] Remove concrete controls from the current thread state bag.
- [x] Replace the current `ThreadTabState` with smaller focused types.
- [x] Move the remaining open-thread state model out of `CodeAltaApp`.

## Phase 6: Extract timeline and tool-call presenters

- [x] Create `ThreadTimelinePresenter`.
- [x] Create `ToolCallPresenter`.
- [x] Create `SessionUsagePresenter` if the usage popup remains presenter-driven.
- [x] Move `DocumentFlow` ownership into timeline presenters.
- [x] Move tool-call chip/dialog state into `ToolCallPresenter`.
- [x] Move incremental timeline rendering logic out of `CodeAltaApp`.
- [x] Keep imperative timeline rendering localized to presenters instead of spreading it through the shell/controller.

## Phase 7: Extract formatting and helper buckets

- [x] Create `ChatMarkdownFormatter`.
- [x] Create `ToolCallSummaryFormatter`.
- [x] Create `SessionUsageFormatter`.
- [x] Move status/usage/tool-call/chat formatting helpers out of `CodeAltaApp`.
- [x] Remove `CodeAltaApp.ChatHelpers.cs` as a generic helper bucket.
- [x] Remove remaining `CodeAltaApp` partial files that exist only to group unrelated helpers.
- [x] Move session usage merge helpers out of `CodeAltaApp` partials.

## Phase 8: Reduce broad refresh paths

- [x] Replace broad `RefreshView()` usage with narrower update paths.
- [x] Make sidebar updates driven by sidebar projection changes.
- [x] Make tab-strip updates driven by open-tab and selected-tab changes.
- [x] Make status/header updates driven by bindable state changes.
- [x] Make timeline updates incremental per event/history load.
- [x] Make usage popup updates target only the selected thread usage state.

## Phase 9: Rework tests around the new seams

- [x] Add controller tests for selection and scope intent routing.
- [x] Add prompt/status availability projection tests.
- [x] Add controller tests for startup restoration and runtime event application.
- [x] Add projection tests for sidebar and tab-strip models/builders.
- [x] Add presenter-focused tests where they provide value.
- [x] Keep a small number of real UI interaction tests for behavior that requires controls.
- [x] Reduce reflection-heavy tests that poke private `CodeAltaApp` fields.

## Final cleanup

- [ ] Verify `CodeAltaApp` is no longer a large partial class spanning unrelated concerns.
- [x] Verify `CodeAltaApp` owns lifecycle only and does not hold many concrete controls.
- [x] Verify there is one explicit UI dispatcher contract in active use.
- [x] Verify runtime events are applied through one shell/controller path on the UI thread.
- [x] Verify bindable view models are only accessed on the UI thread.
- [x] Verify timeline/tool-call/dialog logic lives outside the shell controller.
- [x] Verify `ThreadTabState` is gone or reduced to one focused responsibility.
- [x] Verify `RefreshView()` no longer exists as a broad shell refresh primitive.
- [x] Update docs if the implementation materially changes structure or terminology.

## Phase 10: Remove remaining `CodeAltaApp` partial slices

- [x] Move `CreateAsync(...)` out of `CodeAltaApp.Bootstrap.cs` so `CodeAltaApp` no longer has an app-layer partial bootstrap slice.
- [x] Replace `CodeAltaApp.Sidebar.cs` with a named sidebar coordinator/home.
- [x] Replace `CodeAltaApp.Presentation.cs` with named presentation/view helpers.
- [x] Extract shell text, welcome-pane, status, and tab-header helpers out of `CodeAltaApp.Presentation.cs`.
- [x] Extract selector and prompt-availability coordination out of `CodeAltaApp.Presentation.cs`.
- [x] Extract tab-strip and workspace sync coordination out of `CodeAltaApp.Presentation.cs`.
- [ ] Replace `CodeAltaApp.Runtime.cs` with named runtime/lifecycle helpers or controller-owned collaborators.
- [ ] Add guardrails that fail if `CodeAltaApp` remains a partial type outside generated code.
