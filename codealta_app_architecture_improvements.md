# CodeAltaApp Architecture Improvements

Status: Proposal
Audience: `src/CodeAlta/` implementers
Scope: terminal UI architecture, state ownership, view/view-model organization, lifecycle, and threading

## 1. Why this document exists

`CodeAltaApp` has become the place where almost everything meets:

- application bootstrap
- terminal lifecycle
- runtime event pumping
- durable catalog/view-state loading
- selection and navigation state
- prompt sending and delegation commands
- thread history loading
- chat timeline rendering
- tool call dialogs
- status and usage presentation
- most concrete `XenoAtom.Terminal.UI` control references

That makes the code hard to reason about because the boundaries are not clear:

- state is split between private fields, nested state bags, a small shell view model, and direct control mutation
- the partial files are organized by convenience, not by ownership
- `TerminalHost` is too small to justify its own abstraction, but `CodeAltaApp` is too large to absorb everything without a structure change
- UI-thread access is handled by multiple patterns (`PostToUi`, `ReadUiValue`, `RunOnUiThread`)
- the terminal loop callback is doing lifecycle work that should be explicit

The goal of this proposal is to refactor the terminal UI into a structure that is:

- easier to read
- easier to change safely
- easier to test without reflection-heavy private-field setup
- more explicit about threading and lifecycle
- still pragmatic and not over-engineered

## 2. Current diagnosis

The current codebase shows a few specific architectural issues.

### 2.1 `TerminalHost` is not a meaningful boundary

Today `TerminalHost` in `src/CodeAlta/App/TerminalHost.cs` mostly:

- creates logging, storage, catalogs, backends, `AgentHub`, and `WorkThreadRuntimeService`
- constructs `CodeAltaApp`
- disposes owned services
- exposes `ImportKnownProjectsFromBackendsAsync`, which is then called from inside `CodeAltaApp`

This creates an awkward dependency direction where the UI depends on a host helper.

### 2.2 `CodeAltaApp` is both shell and implementation bucket

The fields in `src/CodeAlta/Views/CodeAltaApp.cs` include all of the following in one type:

- service dependencies
- app/session state
- durable view state
- selection state
- lifecycle tasks and cancellation
- dispatcher access
- concrete controls
- per-thread render state

The partial files spread behavior across `Runtime`, `Presentation`, `Sidebar`, `ToolCalls`, `Usage`, `Settings`, and `ChatHelpers`, but the type boundary is still one large mutable object.

### 2.3 `ThreadTabState` mixes four distinct concerns

The current `ThreadTabState` combines:

- thread/session state
- per-thread preferences
- history loading state
- timeline rendering state
- tool call/dialog state
- concrete UI elements such as `DocumentFlow` and `TabPage`

This is the highest-cohesion problem in the current design.

### 2.4 UI invalidation is too broad

`RefreshView()` currently:

- normalizes selection
- rebuilds the sidebar
- invalidates computed visuals
- refreshes the thread pane

That is workable for a small app, but it creates hidden coupling because many operations do not update their specific target. They request a full shell refresh instead.

### 2.5 UI-thread rules are implicit

There are currently three UI access patterns:

- `PostToUi`
- `ReadUiValue`
- static `RunOnUiThread`

Some app state is mutated off the UI thread and only concrete control updates are later posted. That makes it hard to tell what is safe to touch from background paths.

### 2.6 The loop callback has too many responsibilities

The callback passed to `Terminal.RunAsync(...)` currently:

- marks the terminal loop as started
- kicks off startup refresh
- tries to restore pending thread history
- syncs sidebar selection

That is too much hidden lifecycle in a loop hook.

## 3. Refactoring goals

The refactor should achieve the following:

1. Remove `TerminalHost` and fold its role into `CodeAltaApp` without making `CodeAltaApp` another God class.
2. Make `CodeAltaApp` a clear shell/lifecycle owner instead of a large partial implementation bucket.
3. Replace the current partial-class organization with named types that match responsibilities.
4. Introduce focused bindable view models for shell state, sidebar state, thread workspace state, and per-thread chrome.
5. Keep the timeline rendering pragmatic. Do not force pure MVVM onto `DocumentFlow`, dialogs, and tool call widgets if that makes the code worse.
6. Centralize UI-thread access behind a single contract.
7. Replace broad `RefreshView()` usage with narrower update paths.
8. Make startup, background refresh, and runtime event consumption explicit lifecycle steps.
9. Improve testability by moving logic out of one private mutable type and into focused classes.

## 4. Non-goals

This proposal does not recommend:

- building a generic MVVM framework
- introducing a dependency injection container inside `CodeAlta`
- converting every control into a bindable item collection
- extracting dozens of small interfaces
- rewriting the UI all at once
- changing the product behavior unless needed to simplify architecture

The target is a better-structured terminal UI, not framework churn.

## 5. Design principles

### 5.1 One class, one reason to change

The current partial-class design hides that `CodeAltaApp` changes for many unrelated reasons. The target design should use named types with explicit ownership.

### 5.2 Keep state ownership obvious

For each piece of state, it should be clear whether it is:

- durable state loaded from catalogs
- application/session state
- bindable view-model state
- visual/control state

These should not be mixed in one bag unless there is a strong reason.

### 5.3 Views bind and forward commands, controllers coordinate

Views should:

- build controls
- bind controls to view models
- forward user intent
- own the concrete control tree

Controllers should:

- load data
- handle commands
- apply runtime events
- update view models and renderers

### 5.4 Use bindables for shell state, not for everything

`[Bindable]` is a good fit for:

- header text
- status state
- selected scope labels
- draft thread title
- backend/model/reasoning selection
- tab labels and tab status
- prompt availability and placeholder

It is not automatically the right fit for:

- `DocumentFlow`
- tool call dialogs
- complex timeline items that are better built by a dedicated presenter

### 5.5 One threading rule

All mutable shell state should be treated as UI-thread-owned.

Background work may:

- call services
- await I/O
- receive runtime events

But it should apply state changes by marshaling through a single UI dispatcher abstraction.

Important clarification:

- `[Bindable]` state must also be read and written on the UI thread

Bindable view models are not a thread-safe synchronization boundary. They should be treated exactly like the rest of the UI-owned state.

### 5.6 Prefer incremental migration

The refactor should be staged so behavior stays stable and tests can move with the code.

## 6. Recommended target architecture

The recommended target is a small shell composed of focused collaborators.

```text
Program
  -> CodeAltaApp.CreateAsync(...)
       -> bootstrap owned services
       -> create shell controller
       -> create shell view + root VM
  -> CodeAltaApp.RunAsync(...)
       -> start terminal
       -> initialize shell
       -> start runtime event pump
```

### 6.1 High-level responsibilities

#### `CodeAltaApp`

`CodeAltaApp` should become the application facade and lifecycle owner.

It should own:

- app creation/bootstrap
- shell startup/shutdown
- terminal run loop integration
- disposal of owned services

It should not own:

- sidebar tree building
- thread pane control creation
- timeline rendering details
- tool call dialog logic
- status formatting helpers

Target field shape for `CodeAltaApp` should be small and obvious. A good target is something close to:

- owned service bundle or individual owned services
- `CodeAltaShellController`
- `CodeAltaShellView`
- `IUiDispatcher`
- lifecycle cancellation/task fields
- logging ownership flag if still needed

It should no longer hold dozens of control references or shell selection fields.

#### `CodeAltaShellController`

This should become the main coordinator for shell behavior.

It should own:

- loading projects, threads, and view state
- selection/open-tab state
- startup refresh
- prompt sending, steering, delegation, abort
- backend/model/reasoning preferences
- thread history loading
- applying runtime events
- updating the shell view models

It should not hold concrete controls.

#### Views

Views should be explicit classes, not partial slices of `CodeAltaApp`.

Recommended views:

- `CodeAltaShellView`
- `SidebarView`
- `ThreadWorkspaceView`
- `ThreadStatusView`
- `SessionUsagePopupView`

#### Timeline presenters

Timeline rendering should stay imperative, but it should move behind dedicated presenters.

Recommended presenters:

- `ThreadTimelinePresenter`
- `ToolCallPresenter`
- `SessionUsagePresenter`

These presenters may own concrete `DocumentFlow`, dialog, and per-item visual state, but they should not own shell selection or runtime orchestration.

## 7. Recommended file and namespace layout

The exact names can change, but the target layout should look roughly like this:

```text
src/CodeAlta/
  App/
    CodeAltaApp.cs
    CodeAltaApp.Bootstrap.cs
    IUiDispatcher.cs
    TerminalUiDispatcher.cs
    CodeAltaShellController.cs
    RuntimeEventPump.cs
    KnownProjectImporter.cs

  ViewModels/
    CodeAltaShellViewModel.cs
    SidebarViewModel.cs
    ThreadWorkspaceViewModel.cs
    ThreadTabViewModel.cs
    PromptComposerViewModel.cs
    SessionUsageViewModel.cs

  Views/
    CodeAltaShellView.cs
    SidebarView.cs
    ThreadWorkspaceView.cs
    WelcomePaneView.cs
    SessionUsagePopupView.cs

  Presentation/
    Timeline/
      ThreadTimelinePresenter.cs
      ThreadTimelineState.cs
      ToolCallPresenter.cs
      ToolCallVisualState.cs
    Formatting/
      ChatMarkdownFormatter.cs
      ToolCallSummaryFormatter.cs
      SessionUsageFormatter.cs
    Styling/
      UiPalette.cs

  Models/
    ChatBackendState.cs
    ThreadSessionState.cs
    ShellSelectionState.cs
```

Important rule:

- `CodeAltaApp` should stop being a partial class.
- Partial types should be reserved for generated code or bindable source generation.

## 8. Contracts and responsibilities

This section describes the minimum set of explicit contracts worth introducing.

### 8.1 `IUiDispatcher`

The current `PostToUi`, `ReadUiValue`, and `RunOnUiThread` patterns should be replaced with one abstraction.

Suggested shape:

```csharp
internal interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action);
    Task InvokeAsync(Action action);
    Task<T> InvokeAsync<T>(Func<T> action);
}
```

Rules:

- all shell state mutation goes through this dispatcher
- controllers use this abstraction, not `Dispatcher.Current`
- views and presenters may use it when they must create or mutate controls
- remove duplicate UI helper methods once migration is complete

### 8.2 `KnownProjectImporter`

The backend-import helper currently hanging off `TerminalHost` should move out of the host abstraction.

Recommended ownership:

- `KnownProjectImporter` depends on `AgentHub` and `ProjectCatalog`
- `CodeAltaShellController` uses it during startup refresh and manual refresh

This avoids a UI-to-host dependency while keeping the behavior explicit.

### 8.3 `RuntimeEventPump`

`PumpRuntimeEventsAsync` should move into a small dedicated component.

Responsibilities:

- read `_runtimeService.StreamEventsAsync(...)`
- post events into the controller on the UI thread
- own cancellation and shutdown behavior

Important rule:

- `HandleRuntimeEvent(...)` should run on the UI thread and should become a controller method, not a background callback mutating mixed state.

### 8.4 `CodeAltaShellController`

Suggested responsibilities:

- `InitializeAsync`
- `RefreshCatalogAsync`
- `OpenThreadAsync`
- `CloseThreadAsync`
- `ActivateDraftAsync`
- `SelectGlobalScope`
- `SelectProjectScope`
- `SendPromptAsync`
- `SteerAsync`
- `DelegateAsync`
- `AbortAsync`
- `EnsureThreadHistoryLoadedAsync`
- `ApplyRuntimeEvent`

Suggested internal owned state:

- projects and threads
- loaded view state
- backend availability/model state
- selection state
- open tab/session state
- bindable view models

Suggested rule:

- no concrete `Visual`, `Button`, `TreeView`, `TabControl`, `DocumentFlow`, or `Dialog` fields on the controller

### 8.5 Views

Views should be small and concrete.

#### `CodeAltaShellView`

Owns the root layout:

- header
- sidebar
- thread workspace
- command bar host

#### `SidebarView`

Owns:

- tree construction
- refresh catalog button
- draft thread title text box

It should bind to `SidebarViewModel` and expose minimal callbacks for user intent.

#### `ThreadWorkspaceView`

Owns:

- tab strip
- active thread body host
- prompt editor area
- backend/model/reasoning selectors
- per-thread footer/status area

It should bind to `ThreadWorkspaceViewModel`.

### 8.6 Timeline presenters

The timeline should not be forced into a large collection-binding abstraction.

Recommended split:

- `ThreadSessionState`: non-visual session and rendering state
- `ThreadTabViewModel`: bindable chrome and selection state
- `ThreadTimelinePresenter`: owns `DocumentFlow` and translates events/history into visuals
- `ToolCallPresenter`: owns tool call chips, grouping, and dialogs

This keeps the complex imperative part in one place without leaking it through the whole app.

### 8.7 Concrete shell skeleton

The target shape should be close to the following:

```csharp
internal sealed class CodeAltaApp : IAsyncDisposable
{
    private readonly OwnedServices _services;
    private readonly CodeAltaShellController _controller;
    private readonly CodeAltaShellView _view;
    private readonly RuntimeEventPump _runtimeEventPump;
    private IUiDispatcher? _uiDispatcher;

    private CodeAltaApp(
        OwnedServices services,
        CodeAltaShellController controller,
        CodeAltaShellView view,
        RuntimeEventPump runtimeEventPump)
    {
        _services = services;
        _controller = controller;
        _view = view;
        _runtimeEventPump = runtimeEventPump;
    }

    public static async Task<CodeAltaApp> CreateAsync(CancellationToken cancellationToken)
    {
        var services = await OwnedServices.CreateAsync(cancellationToken).ConfigureAwait(false);
        var controller = new CodeAltaShellController(/* services */);
        var view = new CodeAltaShellView(controller.ViewModel);
        var pump = new RuntimeEventPump(/* services, controller */);
        return new CodeAltaApp(services, controller, view, pump);
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        var initialized = false;
        return Terminal.RunAsync(
            _view.Root,
            () =>
            {
                if (!initialized)
                {
                    initialized = true;
                    _uiDispatcher = new TerminalUiDispatcher(Dispatcher.Current);
                    _controller.AttachUi(_uiDispatcher, _view);
                    _ = _controller.InitializeAsync(cancellationToken);
                    _runtimeEventPump.Start(cancellationToken);
                }

                return TerminalLoopResult.Continue;
            },
            cancellationToken);
    }
}
```

The important point is not the exact API shape. The important point is that:

- lifecycle is obvious
- bootstrap is separate from shell behavior
- the controller owns shell logic
- the view owns controls
- the event pump is explicit
- UI access goes through one dispatcher

## 9. Recommended view-model structure

Use `[Bindable]` for scalar shell state and simple selections.

Threading rule for all view models in this section:

- bindable properties must only be read or written on the UI thread
- background work must marshal through `IUiDispatcher` before touching bindable state
- do not update bindable properties directly from runtime-event or background tasks

### 9.1 `CodeAltaShellViewModel`

Keep and expand the current shell model for top-level shell state:

- `HeaderText`
- `StatusText`
- `StatusBusy`
- `StatusIconMarkup`
- `IsInitialized`

### 9.2 `SidebarViewModel`

Owns sidebar-specific shell state:

- `DraftThreadTitle`
- `SelectedScopeLabel`
- `IsRefreshing`
- `CanRefresh`
- `SelectedProjectId`
- a lightweight project/thread tree projection

If collection binding support is weak for the tree, keep the collection projection as plain data and let `SidebarView` rebuild only when that projection changes.

### 9.3 `ThreadWorkspaceViewModel`

Owns active workspace state:

- open tabs projection
- selected tab id
- selected backend id
- selected model id
- selected reasoning effort
- backend status markup
- prompt placeholder
- prompt enabled
- auto-scroll state
- usage-summary state
- draft mode vs active thread mode

### 9.4 `ThreadTabViewModel`

Owns tab chrome and thread-level status:

- `ThreadId`
- `Title`
- `Subtitle`
- `IndicatorKind`
- `StatusText`
- `StatusTone`
- `StatusBusy`
- `BackendId`
- `ModelId`
- `ReasoningEffort`
- `AutoScroll`
- `HasHistoryLoaded`
- `CanSendPrompt`

### 9.5 `PromptComposerViewModel`

Owns editor-adjacent state:

- `Placeholder`
- `IsEnabled`
- `CanSend`
- `CanSteer`
- `CanDelegate`
- `CanAbort`

This lets command enablement stop depending on direct control inspection.

## 10. State split for open threads

The current `ThreadTabState` should be split into at least three concepts.

### 10.1 `ThreadSessionState`

Pure state for an open thread session:

- `WorkThreadDescriptor`
- load/history flags
- cached history events
- backend/model/reasoning preferences
- usage snapshot
- permission and user-input request tracking
- status snapshot

No controls.

### 10.2 `ThreadTabViewModel`

Bindable per-thread shell-facing state:

- tab title
- tab indicator
- footer status
- backend/model/reasoning selection
- auto-scroll

No controls.

### 10.3 `ThreadTimelinePresenter`

Imperative visual presenter for the active timeline:

- owns `DocumentFlow`
- owns `PendingAssistantState`
- owns content/status/tool rendering caches
- owns tool call dialog interactions through `ToolCallPresenter`

This is where concrete XenoAtom controls should live.

## 11. Threading and lifecycle model

This is the most important architectural rule in the proposal.

### 11.1 Single UI-thread mutation rule

After terminal startup, treat the following as UI-thread-owned:

- shell selection state
- projects/threads collections used by the shell
- open-thread session state
- all view models
- all presenters and controls

Background work should never directly mutate those structures.

Instead:

- background work produces results
- results are applied via `IUiDispatcher`

This simplifies reasoning much more than selective posting of only control mutation.

For avoidance of doubt:

- a `[Bindable]` property read from a worker thread is also invalid
- code should not rely on bindables for cross-thread state publication
- if background code needs a value currently held by a bindable VM, fetch it via `IUiDispatcher.InvokeAsync(...)` or keep a separate non-UI state copy in the controller

### 11.2 Replace ad hoc helpers with one dispatcher path

Migration target:

- remove `PostToUi`
- remove `ReadUiValue`
- remove static `RunOnUiThread`

Replace them with:

- `IUiDispatcher.Post(...)` for fire-and-forget UI work
- `IUiDispatcher.InvokeAsync(...)` for request/response UI work

### 11.3 Make startup explicit

The terminal loop callback should not be the hidden place where most startup happens.

Recommended approach:

1. `CodeAltaApp.CreateAsync(...)` performs bootstrap that does not need the live terminal dispatcher.
2. `RunAsync(...)` starts the terminal with the shell view.
3. On first loop iteration or first UI attach, capture the dispatcher and call `InitializeAsync()` exactly once.
4. `InitializeAsync()` loads data, starts runtime pumping, starts backend refresh, and schedules pending restoration.

If the terminal API requires a loop callback for this, keep the callback tiny and one-time.

### 11.4 Minimize fire-and-forget operations

Fire-and-forget work should be limited to:

- long-running event pump startup
- non-blocking view-state persistence where failure is already contained

Prefer explicit tracked tasks for:

- startup refresh
- history loading
- restoration
- prompt send/delegate operations

This does not mean serializing everything. It means making ownership visible.

## 12. Visual update strategy

The current `RefreshView()` approach should be replaced by narrower update mechanisms.

Recommended strategy:

- bindables update header, status, selectors, footer text, tab chrome
- `SidebarView` refreshes only when the sidebar projection changes
- `ThreadWorkspaceView` syncs tabs only when open tabs or selected tab changes
- `ThreadTimelinePresenter` updates timeline incrementally per event
- `SessionUsagePopupView` refreshes only when the selected thread usage changes

The app should stop using one broad shell refresh as the default response to most events.

## 13. Mapping from current files to target types

This mapping should guide the extraction work.

### Current `CodeAltaApp.cs`

Keep:

- constructor/factory shape
- `RunAsync`
- disposal

Move out:

- nested thread/session state bags
- selection helper records and enums that belong to controller state

### Current `CodeAltaApp.Runtime.cs`

Move mostly into:

- `CodeAltaShellController`
- `RuntimeEventPump`
- `KnownProjectImporter`
- `ThreadTimelinePresenter`

### Current `CodeAltaApp.Presentation.cs`

Split into:

- `CodeAltaShellView`
- `ThreadWorkspaceView`
- `PromptComposerViewModel`
- `ThreadWorkspaceViewModel`
- `TerminalUiDispatcher`

### Current `CodeAltaApp.Sidebar.cs`

Split into:

- `SidebarView`
- `SidebarViewModel`
- sidebar projection/builder helpers

### Current `CodeAltaApp.ToolCalls.cs`

Move into:

- `ToolCallPresenter`
- `ToolCallVisualState`
- `ToolCallSummaryFormatter`

### Current `CodeAltaApp.Usage.cs`

Split into:

- `SessionUsagePresenter`
- `SessionUsagePopupView`
- `SessionUsageViewModel`
- `SessionUsageFormatter`

### Current `CodeAltaApp.ChatHelpers.cs`

Split into:

- `ChatMarkdownFormatter`
- timeline visual factory helpers under `Presentation/Timeline`
- any remaining UI-thread helpers into `IUiDispatcher` consumers

The file should stop existing as a generic helper bucket once the migration is complete.

## 14. Recommended migration plan

This work should be done in phases.

### Phase 1: Remove `TerminalHost` cleanly

1. Move bootstrap/disposal responsibilities into `CodeAltaApp`.
2. Introduce `CodeAltaApp.CreateAsync(...)`.
3. Update `Program.cs` to construct and run `CodeAltaApp` directly.
4. Move `ImportKnownProjectsFromBackendsAsync` out of `TerminalHost`.

Recommended immediate result:

- `TerminalHost` deleted
- `CodeAltaApp` owns lifetime
- project-import logic lives in a real collaborator, not a host helper

### Phase 2: Introduce the shell controller and dispatcher abstraction

1. Add `IUiDispatcher` and its terminal implementation.
2. Add `CodeAltaShellController`.
3. Move selection, startup refresh, runtime event handling, and command methods out of `CodeAltaApp` into the controller.
4. Keep existing views mostly intact for this phase if needed.

Recommended result:

- `CodeAltaApp` becomes small
- lifecycle is clearer
- UI-thread ownership is centralized

### Phase 3: Extract real view classes

1. Convert `CodeAltaApp.Sidebar.cs` into `SidebarView`.
2. Convert thread-pane and shell layout construction into `CodeAltaShellView` and `ThreadWorkspaceView`.
3. Keep palette/styling separate as `UiPalette`.

Recommended result:

- visual code has named homes
- partial files stop being the primary organization tool

### Phase 4: Introduce focused view models

1. Expand `CodeAltaShellViewModel`.
2. Add `SidebarViewModel`, `ThreadWorkspaceViewModel`, `ThreadTabViewModel`, and `PromptComposerViewModel`.
3. Move command enablement and selector state out of direct control inspection.

Recommended result:

- most shell chrome becomes declarative and bindable
- control mutation reduces sharply

### Phase 5: Split `ThreadTabState`

1. Create `ThreadSessionState`.
2. Create `ThreadTimelinePresenter`.
3. Create `ToolCallPresenter`.
4. Move timeline/tool-call control state out of the shell controller.

Recommended result:

- runtime/session state is separate from visual state
- the biggest mixed state bag is removed

### Phase 6: Extract formatting/helper buckets

1. Move chat markdown helpers into `ChatMarkdownFormatter`.
2. Move tool call formatting into `ToolCallSummaryFormatter`.
3. Move usage formatting into `SessionUsageFormatter`.
4. Remove `CodeAltaApp.ChatHelpers.cs` as a generic helper bucket.

Recommended result:

- helpers stop depending on `CodeAltaApp` as a namespace surrogate

### Phase 7: Rework tests around new seams

1. Add tests for `CodeAltaShellController`.
2. Add tests for sidebar tree projection/builder logic.
3. Add tests for tab-strip projection logic.
4. Add tests for timeline presenters where worthwhile.
5. Reduce reflection-based tests that poke private fields.

## 15. Recommended implementation order inside the code

The concrete order below is safer than trying to move everything at once.

1. Delete `TerminalHost` by introducing `CodeAltaApp.CreateAsync`.
2. Add `IUiDispatcher`.
3. Move startup and event-pump code into controller/runtime classes.
4. Move view-building code into view classes.
5. Move bindable shell state into expanded view models.
6. Split thread session state from thread timeline presentation.
7. Move formatting/helper buckets into dedicated files.
8. Rename or relocate any remaining generic "models" that still hold controls.

## 16. Testing strategy

The target design should improve tests materially.

### 16.1 Controller tests

Add focused tests for:

- initial selection resolution
- open/close tab behavior
- scope selection behavior
- prompt availability resolution
- status precedence rules
- startup restoration sequencing

### 16.2 Projection tests

Add focused tests for:

- sidebar tree projection
- tab-strip projection
- selector option projection
- status and header text projection

### 16.3 Presenter tests

Keep targeted tests for:

- tool call summary formatting
- usage formatting
- timeline item generation for important event types

### 16.4 UI tests

Keep only a small number of full UI/control tests where they provide unique value:

- copy button interactions
- popup open/close behavior
- prompt editor command wiring

## 17. Acceptance criteria

The architecture refactor is successful when all of the following are true:

1. `TerminalHost` is removed.
2. `CodeAltaApp` is no longer a large partial class spanning unrelated concerns.
3. The app has one explicit UI dispatcher contract.
4. Runtime events are applied through a single shell/controller path on the UI thread.
5. The shell has focused bindable view models for sidebar, workspace, and tab chrome.
6. Timeline rendering and tool call dialogs live outside the shell controller.
7. `ThreadTabState` is gone or reduced to one focused responsibility.
8. `RefreshView()` no longer exists as a broad shell refresh primitive.
9. Tests can exercise selection/navigation/status logic without setting private fields on `CodeAltaApp`.
10. The resulting code is easier to follow because file/class names match responsibilities.

## 18. Risks and mitigations

### Risk: too much abstraction too early

Mitigation:

- keep the number of new contracts small
- prefer concrete classes unless a real interface boundary is needed
- keep views and presenters close to the terminal UI framework

### Risk: trying to force pure MVVM onto imperative terminal controls

Mitigation:

- use bindables for shell state
- keep timeline/dialog rendering imperative behind presenters

### Risk: behavior regressions during the split

Mitigation:

- migrate in phases
- preserve existing tests first
- add controller/projection tests before deleting old paths

### Risk: `CodeAltaApp.CreateAsync(...)` could become another bootstrap bucket

Mitigation:

- keep creation code in a dedicated bootstrap file
- keep runtime orchestration in the shell controller
- keep project import and event pump in named collaborators

## 19. Final recommendation

The right direction is not "more partial files" and not "MVVM everywhere."

The right direction is:

- remove `TerminalHost`
- make `CodeAltaApp` a small lifecycle facade
- introduce a real shell controller
- move visuals into named view classes
- use `[Bindable]` for shell-facing state
- keep timeline rendering imperative but isolated behind presenters
- centralize all UI-thread access through one dispatcher contract

That structure should make the terminal codebase significantly easier to understand, extend, and test while staying appropriately lightweight for the size of the app today.
