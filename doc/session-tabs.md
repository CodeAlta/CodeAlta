# Desktop session tabs

## Recent-session presentation

The browser also supports bounded visible multi-selection and reviewed sequential
exact-session deletion. Pending originals and outcomes belong to App, not the
dialog. See [batch deletion safety and limits](session-batch-deletion.md).

General settings now provides a local 1–50 loaded-session display limit. The active
navigator row and needed ancestors are retained, with explicit show-all/browse
routes. The saved browser can explicitly order bounded observations without
reordering tabs. See [observed activity and its limits](session-activity.md).

## Archive interaction

Single-project archive/unarchive retains saved tabs, local drafts and original
pending work. It changes catalog metadata, not work ownership. Archived sessions
remain browsable read-only; explicit unarchive does not start work. See
[project archive](project-archive.md) for confirmation, retained evidence and
persistence race limits.

## Browse saved sessions

### Explicit runtime observations

**Refresh statuses** on the tab strip, in the saved-session browser, or in the
commands of the search window reads point-in-time runtime facts. It does not poll or create a
background workspace. Only the first 32 displayed/requested rows are considered;
unverified/archived rows and excess rows are explicitly counted as omitted. Reads
are sequential, with a 10-second per-read timeout and a 20-second batch cancellation
budget, at most 64 KiB per response (2 MiB for 32 responses). Canceling a wait does
not guarantee cancellation of already-admitted actor work.

The owned-host RPC verifies host epoch, exact saved ID/creation time, header kind,
project ID/path and bounded catalog ownership; the renderer path is an expectation,
never a filesystem root. Global sessions must match the host's global scope.
Archived projects refuse observation; catalog-only hosts show unknown/unavailable.
One bounded header is read, never history. The existing actor is queried without
activation, provider preparation or additional Display attachment. The original
selected-session runtime query remains separate.

Labels distinguish loading, unknown/not attached, read errors, closed runtime,
observed active run, transition, retiring, terminated attachment, queue drain and
attached/no active run. Details include observation time and runtime/attachment/run
identity. Missing entries do **not** mean idle/completed; active runs are not proof
of liveness or finality. Facts may already be stale when received. The aggregate
is not atomic across actors or external catalog/header writers.

App-owned generations cancel/invalidate on host, catalog, selection, scope/filter,
navigation and unmount changes, including ABA. A new explicit refresh supersedes
old batches; late canceled results cannot publish or clear newer observations.
Same-host runtime mismatch and decreasing attachment generations refuse facts.
Only the current bounded batch/fences are retained, not durable identity history.
Badges never grant Send/Abort permission or replace original-command uncertainty,
draft, selection, scroll or request ownership. No `LastActiveAt` or recent ordering
is derived from saved `UpdatedAt` or run IDs. Continuous/native running-status
parity and authoritative recentness remain separate, unimplemented claims.

Use **Browse saved sessions** in the session rail, its command in the search window, or
**Ctrl+Alt+B outside text editors**. This native dialog searches titles and IDs in
the already-loaded bounded workspace snapshot. It offers the selected project
and explicit global scope; choosing a scope or typing never changes the active
session. The existing rail search remains independent.

Up/Down in search selects a result; Enter opens it. Result buttons also support
native keyboard activation or a mouse click. Escape/Close dismisses the dialog.
IME, repeats and modified editing keys do not activate results or close it, and
shell shortcuts do not run through an open modal. Help documents the entry.
**Ctrl+E is not reassigned**: as in the TUI (`BuiltinShellCommands.EditFile`) it
opens the file editor (see File editor tabs below); TUI session browsing is a
sidebar row action.

The dialog shows loaded/matching/displayed counts, explicit snapshot/text
truncation and any matches omitted by the 200-row display limit. Queries are
limited to 256 characters. Rows label timestamps as **saved metadata updated**,
not last-active time or running status. Counts do not establish catalog completeness.
Ambiguous/unverified identities are visible but disabled; an empty scope or no
matches is distinct from a stale/error capture. Archived projects and catalog-only
hosts permit browsing, subject to existing read-only behavior after opening.

Activation checks the exact session ID, project ID and path against the current
snapshot, then uses `selectSessionTab` and its normal project transition. A
synchronous catalog/host/navigation revision fences refresh and scope/host ABA:
if anything changes while browsing, close and reopen to use the new snapshot.
No stale result is rebound to a moved, removed or duplicate identity. Browsing itself adds no RPC,
filesystem scan, provider preparation, mutation, automatic refresh or background
workspace is added. Local draft, original uncertain Send and scroll/follow stores
remain owned by the existing workspace.

Focused disposable App tests cover search, keyboard/mouse/palette opening,
project/global scopes, stale and duplicate results, archived/catalog-only access,
pending Send/local draft/scroll retention and 390px light/dark dialog bounds.
Native WebView2, screen-reader and comprehensive contrast qualification remain
unperformed. Bulk select/invert/delete/archive and authoritative recent/running
order are not implemented by this browser.

## Saved tabs

Selecting a verified session in the sidebar opens/selects its tab. Tabs span
projects and explicit global sessions; selecting one restores its project scope.
The title includes the scope and the tooltip includes its path. Archived project
tabs remain subject to the existing read-only composer and recovery gates.

Close is **presentation only**: it never deletes, aborts, imports or detaches host
work. Closing the active tab selects the first remaining tab, following
`ShellSessionStateCoordinator.GetNextOpenSessionTabId` and `ShellTabService` in
the TUI. Closing the last saved tab selects the nonclosable local prompt draft.
The draft entry remains available alongside saved tabs; Ctrl+W on it does nothing.
Reopen restores the most recently closed valid tab (window-local history).

Only one `SessionWorkspace` is mounted. Its existing scope/session key and
App-owned request, draft, scroll/follow, display and runtime stores are retained.
Changing tabs uses the existing selected-session read/subscription lifecycle;
there are no hidden live workspaces or background polling/provider starts.
Closing an inactive tab does not read history or replace the active display
attachment. Pending/uncertain Sends retain the exact original request and are
never retried by navigation. Draft text retains the existing bounded local
storage behavior; scroll/follow memory is window-local, not restart persistence.

`SessionTabStrip` is an accessible navigation strip inside the existing
`SessionContentLayout` FlexLayout content slot. FlexLayout still controls the
responsive session/content geometry through its public adapter. The strip does
not create another docking model or put Settings into tabs. Keeping one content
slot avoids hidden live-session factories. Native popouts/floating are disabled;
Settings remains a sidebar-entry modal with mounted inert workspace, and
Reminders still intentionally unmounts/remounts it.

## Keyboard and indicators

- Ctrl+Alt+Left/Right (TUI tab navigation) or Ctrl+PageUp/PageDown: previous/next,
  wrapping. Ctrl+W closes; Ctrl+Shift+T reopens the last closed tab.
- These commands operate only in the workspace outside text editing and ignore
  IME composition, repeats and handled events. They do not override native text
  editing. Modal/Settings and non-workspace focus exclude them.
- Within the tablist, plain Left/Right and Home/End move focus and selection.
  Close buttons are separately labelled. Help and the palette describe the
  implemented commands; palette actions recheck their captured active identity.
- Draft badges use only the existing observed-edit/persistence indicator store.
  Restored text is not falsely labelled as a newly observed edit; a pending Send
  is not labelled as an unsent dirty draft. Explicit bounded runtime observations
  are separate from draft badges. **Continuously-live background status remains
  unavailable** and is never guessed from catalog timestamps; no hidden polling
  sessions or new command authority are introduced.

## Persistence and refusal

`sessionTabs.ts` owns pure bounded presentation state. Versioned local storage
(`codealta.desktop.sessionTabs.v1`) contains only open identities and active
identity, not providers, execution state or request authority. Limits are 32 open
tabs, 32 window-local closed entries, 65,536 UTF-16 code units of serialized text, 256-character IDs and
4096-character paths. Opening beyond the limit evicts the oldest inactive
presentation tab without deleting its draft/request. Storage is best-effort.

Restoration requires an exact unique catalog session ID, explicit project/global
scope, and matching path/project identity; duplicate session IDs anywhere refuse
because existing session stores use session IDs. Missing, changed or duplicate
identities are pruned on a ready snapshot, including truncated snapshots (no
guessing outside the available catalog). A transient loading/error snapshot
retains tabs but disables selection until verified data returns. Unknown/unmatched
legacy sidebar rows retain their existing inspection behavior but are not
persisted as cross-scope tabs. Archived is not deleted.

Malformed/oversized stored state falls back to the existing initial catalog
selection. Valid explicitly empty state opens the local draft on restart. Invalid
identities within structurally valid state are removed, never rebound by title
or timestamp. Closing/reopening cannot revive a missing identity.

## Local prompt draft and explicit handoff

Owned project drafts also support [bounded project prompt references](project-prompt-references.md).
Searching/inserting a reference remains local drafting; typed path references
resolve only during the eventual original normal Send, not during create/transfer.

Opening, selecting, typing or expanding the draft does not create a host session
or start a provider. It reuses the catalog composer and expanded prompt editor,
including Help/palette keys, F6 and modal keyboard ownership. Tab cycling includes
the draft. Text is App-owned per project ID / global scope, matching the TUI's
scope-local draft model. Selecting a project while drafting stays in the draft;
selecting a saved tab restores that tab's scope. Settings keeps the editor mounted
and inert; Reminders may unmount workspace DOM without losing App-owned text.

Bounded text (32,768 UTF-16 units) is also stored under a separate local-draft key
when storage permits. Reload restores text when its scope is selected; a restored
draft-only selection initially uses global scope, not an invented saved-session
identity. Storage denial retains text for this window only. No images/file-editor
or cross-window synchronization parity is claimed.

**Create and transfer draft** is explicit and available only with the existing
owned, mutable, nonarchived scope. It shares the original single-flight create
owner with ordinary creation. The captured scope, epoch/path, presentation
generation, exact text and input revision must still match after the original
reply and one catalog verification read. Cancel transfer invalidates only local
handoff authority: it cannot cancel or prove absence of host creation.

An unambiguous verified new session receives an exact copy only if its destination
draft is readable and empty, no retained Send is pending, and storage readback
matches. Newer edits, ABA navigation, unavailable/ambiguous catalogs, denied
storage and late results do not transfer or navigate. Original text and per-attempt
outcome/identity evidence remain available in this window, separately from newer
draft edits; evidence is not a durable backend receipt and is not reload-persisted.
Navigation never retries or reconciles a creation automatically.

**Remaining one-click gap:** this is create-and-transfer, followed by normal user
**Send** after review. There is no atomic create+send operation, no implicit Send,
and no claim that an explicitly requested host creation is provider-free. Original
local text is intentionally retained even after successful transfer. Unlike the
TUI's replace-draft presentation, the local draft entry remains available.

## File editor tabs

Ctrl+E (`/edit`) opens the file picker of the selected project and a chosen file
becomes a tab of the same FlexLayout strip. `fileTabs.ts` owns the pure state in
parallel to `sessionTabs.ts`: a tab is `{ projectId, projectPath, path }`, its node
id is `file:` followed by the JSON of `[projectId, path]` (one tab per file), and
`codealta.desktop.fileTabs.v1` stores the open and active identities (32 at most).
Restore drops tabs whose project is missing, archived or another folder.

App keeps the session selection as it is while a file is active: the active file
is the selected tab, session-scoped commands are unavailable, and any session,
project or New session selection leaves the file. `sessionTabLayout.ts` admits
select, move, split and active-tabset actions for file nodes with the same
captured-lifetime guard as session nodes; closing is an App intent, so a tab with
unsaved edits can ask Save / Discard / Cancel first. Close, reopen and
next/previous tab commands cover both kinds of tab (ring order: New session,
sessions, files; reopen follows the order of closing).

`ProjectFileEditor` reads and writes through `projectFiles.read` / `projectFiles.write`
with the read revision; its transitions (`fileEditorState.ts`) keep edits on every
refusal and turn a `conflict` into a Reload / Overwrite choice. Editors stay mounted
with their tab, so text and unsaved state survive tab changes; nothing unsaved is
persisted. `fileEditors.ts` gives App the unsaved marks and a save-by-key for the
close question.

## Focused verification

Unit tests cover state transitions, fallback/reopen, catalog ambiguity/scope/path
validation, archives, bounded/failed persistence, shortcut IME/editing exclusions
and palette capture fencing. The production-main fake-host test exercises draft,
pending Send, measured scroll restoration, close inactive/active/last, reopen,
cross-project/global selection, catalog error/removal/duplicates, remembered
selection, tablist/command keyboard and palette, and no hidden display attachments.
Existing Settings, Reminders, responsive geometry and archive/epoch regressions
remain in that test. These are finite disposable-provider browser observations,
not native/screen-reader, real-transport or exhaustive browser-containment proof.
