# Navigator Improvements

This document defines the next round of improvements for project/thread handling and the sidebar navigator.
It is written so an implementer can complete the work without needing additional product clarification.

## Goals

- Make the navigator denser and more informative without making it noisy.
- Put the important actions exactly where the user expects them:
  project-level actions on projects, thread-level actions on threads, global navigator actions in the footer toolbar.
- Make recency visible everywhere.
- Keep the sidebar fully reactive while avoiding unnecessary rebuilds when nothing changed.
- Support safe delete flows for both individual threads and whole projects.
- Provide a scalable way to inspect and manage more than the small “recent threads” subset shown in the navigator.
- Preserve keyboard-first workflows.

## Non-Goals For This Iteration

- No sidebar search yet. The design should leave room for it, but it is not part of this implementation.
- No deleted-items browser yet. Deleted threads and hidden projects are out of the default navigator view.
- No generic confirmation dialog contribution to `XenoAtom.Terminal.UI` yet. The first version lives in CodeAlta.

## Current Relevant Code Areas

- Sidebar view and projection:
  - `src/CodeAlta/Views/SidebarView.cs`
  - `src/CodeAlta/Presentation/Sidebar/SidebarTreeProjection.cs`
  - `src/CodeAlta/Presentation/Sidebar/SidebarTreeProjectionBuilder.cs`
  - `src/CodeAlta/App/SidebarCoordinator.cs`
- Thread/project shell state:
  - `src/CodeAlta/Views/CodeAltaApp.cs`
  - `src/CodeAlta/App/CodeAltaShellController.cs`
  - `src/CodeAlta/App/ShellThreadStateCoordinator.cs`
- Catalog models:
  - `src/CodeAlta.Catalog/ProjectDescriptor.cs`
  - `src/CodeAlta.Catalog/WorkThreadDescriptor.cs`
  - `src/CodeAlta.Catalog/ProjectCatalog.cs`
  - `src/CodeAlta.Catalog/WorkThreadCatalog.cs`
- Existing backend delete support:
  - `src/CodeAlta.CodexSdk/CodexClient.cs`
  - `src/CodeAlta.Agent.Codex/CodexAgentMapper.cs`

## UI Library Guidance

Use the XenoAtom.Terminal.UI patterns already available:

- Tree row right-side visuals:
  - `TreeNode.AddRightVisual(...)`
  - `TreeNodeRightVisualVisibility.Always`
  - `TreeNodeRightVisualVisibility.Hover`
- Tooltip support:
  - `.Tooltip(...)`
  - `TooltipHost`
- Modal windows:
  - `Dialog`
- Grid management dialog:
  - `DataGridControl`
  - `DataGridListDocument<T>`
  - `DataGridDocumentView`
  - `FilterRowVisible`
- Validation:
  - `ValidationPresenter`
  - `.Validate(...)`

## Reactivity Requirement

The sidebar must be fully reactive:

- relative dates update while time passes
- sort mode changes are reflected immediately
- settings changes are reflected immediately
- deleting, renaming, project edits, and thread activity updates are reflected immediately

But the sidebar must not be rebuilt or re-rendered continuously when nothing is actually changing.

Implementation guidance:

- prefer bindable/state-driven updates over brute-force tree reconstruction
- only update the parts of the sidebar whose underlying state changed
- if projection rebuilds are still used for some operations, guard them with equality/change detection so identical projections do not trigger work
- time-based recency updates should run only when at least one visible label would cross to a new bucket or at a coarse refresh cadence such as once per minute
- sort mode may legitimately require a projection rebuild because ordering changes, but that rebuild should be triggered only when the sort mode or the sortable source data changes
- per-row label changes such as relative time text should use bindable properties where practical so the node visual can react without reconstructing the whole tree

Recommended design direction:

- treat structural changes and presentation changes separately
- structural changes:
  - project added/removed
  - thread added/removed/deleted
  - sort mode changed
  - recent-thread count changed
  - project rename if ordering key changes
- presentation-only changes:
  - relative time label changes
  - inline rename validation state
  - hover action enablement

Structural changes may rebuild projection.
Presentation-only changes should prefer bindable property updates on existing row view models.

Relevant references:

- `C:\code\XenoAtom\XenoAtom.Terminal.UI\samples\ControlsDemo\Demos\TreeViewDemo.cs`
- `C:\code\XenoAtom\XenoAtom.Terminal.UI\samples\ControlsDemo\Demos\DataGridDemo.cs`
- `C:\code\XenoAtom\XenoAtom.Terminal.UI\site\docs\controls\treeview.md`
- `C:\code\XenoAtom\XenoAtom.Terminal.UI\site\docs\controls\datagrid.md`
- `C:\code\XenoAtom\XenoAtom.Terminal.UI\site\docs\controls\tooltip.md`
- `C:\code\XenoAtom\XenoAtom.Terminal.UI\site\docs\controls\validation.md`

## Navigator Layout Changes

### Footer Toolbar

Replace the current footer content entirely.

Remove:

- `Thread Title (optional)`
- its `TextBox`
- the text button form of `Refresh Catalog`

Add a compact icon toolbar at the bottom of the navigator.

Required buttons:

- Refresh catalog
  - icon-only
  - tooltip: `Refresh projects and threads`
  - behavior: same as current refresh action
- Sort mode
  - cycles or opens a compact selector for:
    - `Name` (default)
    - `Date` (descending)
  - tooltip should reflect current mode, e.g. `Sort projects by name` / `Sort projects by last activity`
- Navigator settings
  - opens a dialog
  - tooltip: `Navigator settings`

Recommended visual behavior:

- icon buttons should be small and low-noise
- all footer buttons should have tooltips
- footer toolbar should not consume more than one row if possible

### Tree Rows

Each project and thread row must support:

- a main title on the left
- an always-visible dimmed relative timestamp on the right
- hover-only action buttons to the left of the timestamp

Use `TreeNode.AddRightVisual(...)` with:

- relative timestamp: `Always`
- delete button: `Hover`
- project-only extra buttons: `Hover`

Hover-only buttons must never push the timestamp offscreen. The timestamp is always present and remains visually pinned at the far right.

## Recency Display

### What To Display

For every visible project and every visible thread, show a relative time on the right side.

Display values:

- `1 min ago`
- `1 h ago`
- `1 day ago`
- `1 month ago`
- `1 year ago`

Also support:

- `just now` for very recent updates under one minute
- `never` when no meaningful activity timestamp exists

Use local time for the tooltip.

Tooltip content should show exact date and time, for example:

- `2026-03-29 14:37:12 +02:00`

### Timestamp Source

Thread recency:

- use `WorkThreadDescriptor.LastActiveAt` as the canonical navigator activity timestamp
- rationale: it already maps best to “last conversational activity”
- if code paths exist that update messages without updating `LastActiveAt`, those paths must be fixed

Project recency:

- defined as the most recent `LastActiveAt` among the project’s non-deleted visible threads
- if a project has no non-deleted visible threads, show `never`

### Refresh Behavior

Relative timestamps must stay current while the app is open.

Implementation guidance:

- do not rebuild the entire sidebar every second
- recalculate only visible relative timestamp labels on a lightweight cadence
- minute-level refresh is sufficient for the current display buckets
- if the UI already has a regular tick, do not use that tick to force sidebar reconstruction on every frame
- use bindable per-row state or a dedicated recency refresh pulse that only updates rows whose displayed bucket actually changed

Recommended optimization:

- each visible row can cache:
  - `ActivityAt`
  - `CurrentRelativeBucket`
  - `CurrentRelativeText`
- on the recency pulse:
  - recompute the bucket
  - if unchanged, do nothing
  - if changed, update only the bindable relative text property for that row

### Styling

- timestamp text should use a dimmed or muted color
- keep it single-line and non-wrapping
- use a fixed-ish width strategy if needed to reduce tree jitter when values change
- timestamp visual should be bindable so updates do not require replacing the whole row header

## Sorting Behavior

Project sorting applies to the project list in the navigator.

Modes:

- `Name`
  - default
  - ascending
  - compare using `ProjectDescriptor.DisplayName`, case-insensitive
  - stable secondary sort by `ProjectDescriptor.Name`, then `Id`
- `Date`
  - descending
  - compare using project recency
  - projects with no activity sort last
  - stable secondary sort by display name

Thread ordering inside a project or under the global node:

- keep threads sorted by descending `LastActiveAt`
- stable secondary sort by title, then thread id

The `Global` root remains first.

## Navigator Settings Dialog

Add a navigator settings dialog opened from the footer toolbar.

Settings for this iteration:

- sort mode
- number of recent threads shown per project

Recommended additions to the data model:

- introduce a small persisted navigator settings model in machine-local UI state
- minimum settings:
  - `SortMode`
  - `RecentThreadsPerProject`
- optional future-friendly settings:
  - `ShowArchived`
  - `ThreadListFilterRowEnabled`

Default values:

- sort mode: `Name`
- recent threads per project: `3`

Validation:

- recent thread count must be >= 1
- recommended upper bound for now: 50

Save behavior:

- persist immediately on save
- refresh sidebar projection after save

## Project Row Actions

Project rows get three hover-only buttons, shown to the left of the always-visible timestamp.

### 1. Delete Project

Behavior:

- deletes all threads belonging to the project
- marks the project itself as archived internally
- removes the project from the default navigator view
- does not delete the project directory on disk

Confirmation required.

Confirmation dialog text should clearly state impact:

- project display name
- number of threads affected
- that the project is removed from the default navigator view
- that the project directory on disk is not deleted

When project delete completes:

- if the selected scope is that project, move selection to global or the next visible project
- if any deleted threads are open in tabs, close those tabs cleanly
- refresh recovered catalog state and sidebar projection

### 2. Show All Threads

Opens a dialog with a `DataGridControl`.

The dialog is the main management view for a project’s full thread list.

### 3. Project Details

Opens a project details dialog with an editable form.

## Thread Row Actions

Thread rows get one hover-only button:

- Delete thread

Behavior:

- deletes that thread
- removes it from the default navigator view
- if currently open in a tab, close the tab
- if currently selected, move selection to:
  - another open thread tab if available
  - otherwise the project scope
  - otherwise global

Confirmation required.

Confirmation dialog text should include:

- thread title
- project display name if available

## Thread Delete Requirements

### Backend Exposure

Codex already exposes thread archive requests in `CodexClient`, and Copilot exposes session deletion in its client API.
The implementation must expose a single app-level delete operation for UI callers and map it per backend.

Required outcome:

- the UI should call a single application-level delete service or controller method
- that service handles backend-specific and catalog-specific work

### Delete Semantics By Thread Type

Project/global threads backed by a backend session:

- use backend delete if supported
- Codex maps delete to archive internally
- update local thread descriptor status to `Archived`
- persist any local state changes

Internal threads:

- no backend delete needed
- set local status to `Archived`
- persist through `WorkThreadCatalog`

If a backend does not support delete:

- fallback to local hidden-thread state only
- do not block the UI feature
- log the degraded path

### Recommended App-Level API

Add shell/controller methods so UI code never talks directly to backend SDKs.

Examples:

- `DeleteThreadAsync(string threadId, CancellationToken)`
- `DeleteProjectAsync(string projectId, CancellationToken)`
- `LoadProjectThreadsAsync(string projectId, CancellationToken)`
- `SaveProjectAsync(ProjectDescriptor project, CancellationToken)`

## Project Archiving Requirements

Projects currently do not have a hidden/deleted-in-navigator flag.

Add one.

Recommended model change:

- `ProjectDescriptor.Archived : bool`
- serialized in project markdown frontmatter as `archived: true|false`

Project catalog loading should continue to load hidden projects.
Filtering hidden projects out of the navigator should happen in the navigator projection layer or shell state layer, not by deleting them from the catalog load.

Why:

- future “show deleted/hidden” support stays simple
- hidden projects remain editable and recoverable

Project delete flow:

1. resolve all threads for the project
2. delete each thread
3. set `ProjectDescriptor.Archived = true`
4. persist the project descriptor
5. refresh shell state

Failure handling:

- do not silently leave the project half-deleted
- if some thread deletes fail, show an error status and keep the project visible
- only mark the project hidden after all thread deletes succeed

## Project “Show All Threads” Dialog

### Purpose

Provide a full project thread management surface beyond the recent subset shown in the sidebar.

### Dialog Structure

Use `Dialog`.

Expected content:

- dialog title with project name
- close button on the top right
- toolbar row for selection actions and filter toggle
- main `DataGridControl`
- bottom action row

Close behaviors:

- ESC closes the dialog
- close button closes the dialog
- opening a thread from the dialog closes the dialog after the thread is opened

### Data Source

Use a bindable row view model type, not raw `WorkThreadDescriptor`.

Recommended row VM fields:

- `bool IsSelected`
- `string ThreadId`
- `string Title`
- `DateTimeOffset LastUpdatedAt`
- `string LastUpdatedRelative`
- `string LastUpdatedExact`
- `int MessageCount`
- `bool IsArchived`
- `string? ProjectId`

Keep the absolute timestamp property separate from the formatted strings so sorting remains correct.

### Message Count Guidance

Do not require loading full history for every thread every time this dialog opens.

Recommended implementation:

- extend `WorkThreadDescriptor` or a related cached metadata source with `MessageCount`
- update it whenever:
  - thread history loads
  - runtime events add content/messages
  - startup recovery reconstructs known threads

If a message count is unavailable:

- show `0` only if it is genuinely known to be zero
- otherwise use a nullable count and render `—`

### Required Columns

1. Selection checkbox column
2. Thread Title
3. Last Updated
  - display relative time
  - tooltip shows exact time
  - sort uses absolute timestamp
4. Number of Messages
5. Action column
  - `Open` button

### Required Grid Features

- sortable columns
- multi-selection via checkbox column
- optional filter row toggle using DataGrid built-in filtering
- keyboard navigation through the grid

### Selection Toolbar Actions

Required actions:

- `Select None`
- `Select All`
- `Invert Selection`
- `Delete Selected`
- `Toggle Filter`

Recommended additional behaviors:

- show selected count in the toolbar
- disable `Delete Selected` when selection is empty
- support double-activate or Enter on the row action to open the selected thread

### Delete Selected Flow

- opens a confirmation dialog
- shows count of selected threads
- on success:
  - closes deleted thread tabs if open
  - refreshes grid contents
  - refreshes sidebar projection

## Project Details Dialog

### Purpose

Display all relevant `ProjectDescriptor` fields and allow editing of the safe ones in a structured form.

### Fields To Show

At minimum:

- `Id`
- `Slug`
- `Name`
- `DisplayName`
- `ProjectPath`
- `DefaultBranch`
- `Description`
- `Tags`
- `Checkout.PathTemplate`
- `SourcePath`
- `Archived`
- markdown body preview or summary if helpful

### Fields To Edit

Editable in this iteration:

- `DisplayName`
- `Name`
- `ProjectPath`
- `DefaultBranch`
- `Description`
- `Tags`
- `Archived` only through the delete flow, not a plain checkbox

Strong recommendation:

- inline rename from the navigator edits `DisplayName`
- full details dialog edits `DisplayName`, `Name`, and optionally `Slug`
- avoid changing `Slug` from inline rename because it affects catalog location and references

### Metadata File Path (`SourcePath`)

The dialog should show the metadata file path.

Implementation guidance:

- if direct path editing is implemented now, treat it as a file move operation
- on save:
  - validate new path
  - write updated markdown to the new location
  - only delete the old file after the new write succeeds
  - refresh the catalog

If that file move work is too large for the same pass, the first implementation may display `SourcePath` read-only with:

- copy action
- open location action

But the dialog structure should leave room for future editable relocation.

### Validation Rules

- `DisplayName` must be non-empty
- `Name` must be non-empty and valid as a single directory name
- `ProjectPath` must be non-empty
- `DefaultBranch` must be non-empty
- tags should normalize by trimming whitespace and removing empties/duplicates

Use `ValidationPresenter` so errors are shown inline rather than through status-only feedback.

### Save Behavior

- save/cancel buttons
- ESC closes the dialog
- if the form is dirty and invalid, do not save
- after save:
  - persist through `ProjectCatalog`
  - refresh shell state
  - refresh navigator projection

## Inline Rename On F2

### Scope

When the selected navigator node is a project, pressing F2 enters inline rename mode.

The inline rename target should be `ProjectDescriptor.DisplayName`.

Reason:

- it matches what is actually shown in the navigator
- it avoids accidental mutation of checkout naming or slug identity through a fast-path rename action
- deeper identity edits remain available in the details dialog

### UX Behavior

- replace the project title portion of the row with a `TextBox`
- keep right-side timestamp and hover actions hidden while editing
- initial selection should cover the full current display name

Keyboard behavior:

- Enter saves
- ESC cancels
- Up/Down/Left/Right should not accidentally leave edit mode until the edit is committed or canceled

Validation:

- empty or whitespace-only value is invalid
- validation message shown inline using `ValidationPresenter`
- save should be blocked until valid

On save:

- update `DisplayName`
- persist through `ProjectCatalog.SaveAsync`
- rebuild sidebar projection
- keep the same project selected

## Sidebar Projection/Data Model Changes

The existing `SidebarTreeNodeProjection` is too small for the new UI.
Extend it to carry the full row-state contract instead of overloading the visual construction logic.

Recommended new projection data:

- title
- title tooltip if needed
- icon
- accent
- selection target
- `DateTimeOffset? ActivityAt`
- `string RelativeActivityText`
- `string ExactActivityText`
- node kind
  - global
  - projects-root
  - project
  - thread
- hover action descriptors
- inline edit state for project rows

Recommended action descriptor model:

- action id
- icon
- tooltip
- enabled
- command callback or command key routed by coordinator

The projection builder should remain pure data-building logic.
Actual `Visual` creation stays in `SidebarView`.

Recommended next-level split:

- immutable projection for structure/order
- mutable row view models for live fields

Example:

- immutable:
  - node kind
  - identity
  - sort order position
  - icon/accent
  - child membership
- mutable/bindable:
  - relative time text
  - exact time tooltip text
  - inline rename mode
  - inline rename text
  - validation message
  - action enabled state if dynamic

This split gives good reactivity without forcing tree reconstruction for every small change.

## State Management Changes

Add dedicated navigator state instead of burying everything in ad-hoc locals.

Recommended state areas:

- navigator settings
- project sort mode
- recency refresh state
  - last refresh timestamp
  - next scheduled recency update
- inline rename state
  - active project id
  - edit text
  - validation result
- dialog state
  - thread list dialog open/closed
  - project details dialog open/closed

Prefer view-model-backed state for any UI that can stay open while data refreshes.

Specific guidance:

- navigator settings should be bindable
- row-level timestamp text should be bindable
- dialog row models should be bindable
- projection rebuild should happen only when structural state changes

## Recommended New UI Components In CodeAlta

Add small app-local components where needed:

- `ConfirmationDialog`
  - generic enough to ask “Are you sure?”
  - supports title, body, confirm tone, confirm text, cancel text
- `ProjectThreadsDialog`
- `ProjectDetailsDialog`
- small helper for relative time text
- small helper for dimmed timestamp visual with tooltip

The confirmation dialog should be reusable for:

- delete thread
- delete selected threads
- delete project
- dirty form discard confirmation if added later

## Keyboard and Mouse Behavior Summary

Navigator:

- arrows: existing tree navigation
- Enter on thread: open thread
- Enter on project: select project scope
- F2 on project: inline rename

Dialogs:

- ESC closes
- visible close button also closes

Project threads dialog:

- Enter on `Open` action or row action opens thread
- Space toggles checkbox selection when checkbox column focused

Hover actions:

- only appear for hovered row
- must be clickable without changing unrelated selection state unexpectedly

## Empty/Edge Cases

Projects with no threads:

- still shown in navigator
- timestamp shows `never`
- “Show all threads” dialog opens and shows an empty state

Threads with blank or long titles:

- continue using compact title presentation
- tooltip may show full title if truncation is applied

Archiving the last visible project:

- navigator falls back to global selection

Archiving a thread that is currently rendering live output:

- abort or detach live processing first if required by the backend
- do not leave the tab in a broken half-open state

Catalog refresh while a dialog is open:

- dialog should refresh its view model or close gracefully if the underlying project/thread disappears

## Testing Guidance

Add or update tests for:

- sidebar projection includes relative timestamp data for threads and projects
- project recency uses most recent thread recency
- sort mode name/date behavior
- sidebar reactivity updates relative time labels without forcing structural rebuild when only label text changes
- sidebar does not rebuild when unchanged data is reapplied
- deleted threads and hidden projects are excluded from the default navigator
- delete thread flow updates selection and closes open tabs
- delete project flow deletes all project threads and hides the project
- project threads dialog row view model sorting/filtering/selection behavior
- inline rename validation and save/cancel behavior
- details dialog save validation
- settings persistence and application

Also add focused tests for:

- relative time formatter bucket transitions
- message count fallback behavior

## Suggested Implementation Order

1. Add data/model support
   - project hidden flag
   - optional thread message count metadata
   - delete controller/service API
   - navigator settings persistence
2. Extend sidebar projection model
3. Introduce bindable row state for reactive timestamp and inline-edit behavior
4. Replace footer with icon toolbar
5. Add always-visible relative timestamps
6. Add thread delete action
7. Add project delete action
8. Add project threads dialog
9. Add project details dialog
10. Add F2 inline rename
11. Add final polish and tests

## UX Notes

- Inline rename should feel fast and low-risk, so it edits display name only.
- The thread list dialog is the “management mode”; the navigator remains the “fast navigation mode”.
- Timestamps should be quiet but always present.
- Hover-only action buttons should not create layout jump for the timestamp.
- Delete actions should always be confirmed.
- After any destructive action, selection should remain valid and obvious.
