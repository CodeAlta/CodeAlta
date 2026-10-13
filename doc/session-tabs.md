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

Selecting a verified session in the sidebar opens/selects its tab: the transient tab
for a session that has none (see below). Tabs span
projects and explicit global sessions; selecting one restores its project scope.
The title includes the scope and the tooltip includes its path. Archived project
tabs remain subject to the existing read-only composer and recovery gates.

Close is **presentation only**: it never deletes, aborts, imports or detaches host
work. Closing the active tab selects the first remaining tab, following
`ShellSessionStateCoordinator.GetNextOpenSessionTabId` and `ShellTabService` in
the TUI. Closing the last saved tab selects the nonclosable local prompt draft.
The draft entry remains available alongside saved tabs; Ctrl+W on it does nothing.
Reopen restores the most recently closed valid tab (window-local history).

### The transient tab

Going through sessions does not open a tab for each. One of the open tabs can be
the **transient** one (`SessionTabs.preview`): the tab a session is looked at in,
which the next session looked at takes, as the preview tab of a code editor does.
Its title is in italics (`data-transient` on the label).

- A session that is selected without a tab is opened in it (`openSessionTab(state,
  tab, "preview")`, the mode of the selection effect and of `sessionTabPresentation`
  in `main.tsx`): a row of the Explorer, `Alt+Up` / `Alt+Down`, the search, a
  bookmark, a work item. The session that was there leaves, and is not listed among
  the closed tabs: it was never kept.
- A tab is **kept**, its own from then on (`pinSessionTab`, or `openSessionTab` in
  its default `permanent` mode), by a double click on the row of its session, a
  double click on its title, or the first thing typed in its prompt
  (`draftIndicators`, which the window is told of without following the prompts).
  A session the window creates (the prompt of a new session, the creation form, a
  fork) is kept from the start (`keptSession`), and so are a session opened from
  **Browse saved sessions** and a reopened tab.
- Showing a tab again never changes what it is: neither a click on a tab nor the
  tab keys keep the transient one, and looking at a session that was kept does not
  make its tab transient again.
- What becomes the transient tab takes the place of the one that leaves, in its
  pane and at its position. `reconcileSessionTabModel` finds the one that leaves by
  the `config: { preview: true }` of its node.
- A press on a tab is captured by the dock, which may be dragging it
  (`useSessionTabDrag`), so no `dblclick` names the tab: `SessionTabStrip` takes
  two plain clicks of the same tab within half a second as one double click.

### The prompt of a new session

The row of a scope in the Explorer (a project, **Chats**) selects it and shows the
prompt of a new session of it; it does not open or close the scope, which its
chevron and the arrow keys do. A scope that becomes the selected one is opened
only when a session of it is shown, so that the session is in view.

- The prompt of the selected scope is in the transient tab (`session-draft`): the
  session that was only looked at leaves it (`closePreviewTab`), and a session
  looked at afterwards takes it back.
- A double click on the row of the scope, or on the title of that tab, gives the
  prompt a tab of its own (`SessionTabs.drafts`, node `draft:` followed by the JSON
  of `[projectId, path]`), where the transient one was. Several scopes can have
  theirs, 16 at most. The tab is closed like any other; what was typed stays with
  its scope.
- A prompt that has a text or pictures keeps its tab by itself, as what is typed
  keeps the tab of a session that is looked at: when the prompt of another scope,
  or a session, takes its place, it gets a tab of its own (the effect on
  `leftPrompt` in `main.tsx`). The tab stays where the transient one was, and
  what replaced it comes after it (`reconcileSessionTabModel`). An empty prompt
  leaves its place as before. The prompt a session was just started from keeps
  no tab, since the session takes its place, and a prompt whose tab is closed
  (its own, the transient one, or several tabs at once) stays closed whatever is
  typed in it.
- Escape with no window open closes nothing (`runShortcut`): it does not end
  the view a session is being created from, so a creation started from the
  prompt is still confirmed when the host answers.
- The composer of a new session belongs to the window and to the selected scope:
  it is drawn in the tab of the prompt that is in front. The tab of another scope
  that is visible in a split says what it is, and showing it selects its scope.
- The tab keys go through these tabs too, after the sessions (`cycleTab`); the
  prompt of the selected scope is in the ring once.

### A single tab for the sessions

**Settings > Sessions > Single tab for sessions** (off unless the user turned it
on) gives the sessions one tab. It is a preference of the window
(`singleSessionTab` of `useWindowPreferences`, local storage key
`codealta.desktop.single-session-tab.v1`, `on` or `off`, written only when the
user chooses), like the tabs themselves: the host does not know it. With the
setting off nothing of what follows applies, and the tabs are stored as before.

- `App` opens every session with the mode `single` (`tabMode`), whatever the
  caller asked: `openSessionTab(state, tab, "single")` leaves that session alone
  in `open`. The session that leaves is not listed among the closed tabs, as the
  one of a transient tab is not. What is stored is `{ version, open, active }`
  with one tab: the shape a window with one kept tab stores.
- `singleSessionTab(state)` is the shape of the tabs with the setting on: the
  active session alone, or no session while the prompt of a new session is
  shown, no transient tab and no prompt with a tab of its own. `App` applies it
  (`shapeTabs`) to what it restores at the start, to the tabs of a space it
  enters, and in the selection effect, which is where turning the setting on
  closes the other tabs. The tabs that leave are not closed tabs. Turned off,
  the state has one kept tab and the usual rules apply from there.
- `sessionTabPresentation(…, "single")` applies the same shape in the render,
  before the effect, and says so with `single` on the view. `SessionTabStrip`
  then shows no title in italics, keeps nothing on a double click, has no
  **Keep open**, and opens no menu for a tab that has no entry to offer (the
  prompt of a new session alone in its pane).
- The prompt of a new session takes the tab: with no session selected `open` is
  empty and the dock shows the `session-draft` node, which does not close. The
  `leftPrompt` effect gives no tab to a prompt that is left with a text: the
  text stays with its scope (`localDrafts`, `codealta.desktop.localPrompt.*`)
  and is shown when the prompt of that scope is shown again.
- The tab is replaced where it is. `reconcileSessionTabModel` marks the node of
  the single tab `config: { preview: true }`, which is how the node that leaves
  gives its pane and its position to the one that comes, among the tabs of
  projects.
- The tab keys do not go from the session to the prompt of a new session: the
  prompt is in the ring only while it is the one shown (`cycleTab` without its
  transient entry).
- A session is not its tab: its run, its requests and its drafts are the
  host's or `App`'s, and stay when its pane leaves. The prompts that wait above
  a composer (`queue.composer`) are the exception, since the pane of their
  session is what sends them when it is idle. `App` follows that queue with the
  setting on, and `backgroundSessionTabs` names the sessions that have rows and
  are not the one shown: `SessionTabStrip` renders their pane in
  `.session-background-panes`, hidden, as the pane of a tab that is not in
  front. The pane leaves with the last row. A prompt a plugin queued
  (`submitPlugin`) is tied to the pane that admitted it: while it still waits it
  is taken off the queue and refused to the plugin when that pane leaves, as
  when its tab is closed.

### The menu of a tab

The other button of the pointer on a tab, or the menu key or Shift+F10 on the tab
that has the keyboard, opens its menu (`tabMenu` in `SessionTabStrip.tsx`, shown
by `SessionTabMenu` at the pointer, or under the tab for the keyboard). The pointer
asks through the `onContextMenu` of the dock. The keys are read by the key handler
of the strip (`isSessionContextKey`), as the rows of the Explorer do: only some
systems turn them into a request for a context menu, and macOS never does.
Windows and Linux make that request of the same key press as well (Windows a
moment after the key went up): `takeKeyContextMenu` takes it, wherever the focus
is by then, so that one key press opens one menu. Asked a second time, the menu
lost the keyboard to the prompt. The menu does not show the tab it is asked from.

- **Close** closes the tab, as its close button does and through the same action.
  **Close others**, **Close to the right** and **Close all** close tabs of the
  same pane, of any kind: sessions, prompts of new sessions, tabs of projects.
  The menu of a pane (`…`) has **Close others** and **Close all** too, counted
  from the tab in front of the pane.
- `sessionTabClosing.ts` holds what these commands mean: `tabsToClose` names the
  nodes, `dockTabs` says what they show, `closeSessionTabs` closes sessions and
  prompts in one change of the state, and `shownAfterClosing` says whether the
  tab the command was asked from comes in front, which it does only when what was
  in front is among the tabs closed. `App.closeTabs` writes the tabs once: the
  close handler of one tab reads the tabs of the render, so it cannot be called
  in a loop.
- The transient prompt of a new session is never closed by these commands: it is
  what a pane shows when nothing else is. An editor with unsaved changes stays
  open, and the first of them asks what to do with its changes, as its close
  button does; the tab in front is then left as it is.
- Each closed tab can be reopened, the last one first.
- **Keep open** is in the menu of the transient tab: the session looked at, or the
  prompt of a new session; not with a single tab for the sessions, where nothing
  is transient. **Split right** and **Split below** move the tab to a
  pane of its own, when its pane has another tab.
- A press on a tab is captured by the dock, so an open menu sees no mouse press to
  close on: the strip closes it on any press outside it.

`SessionTabStrip` is the FlexLayout dock of the content area: its model
(`createSessionTabModel` in `sessionTabLayout.ts`) holds the tabs of sessions and
the tabs of projects (see File editor tabs below), which can be moved and split
into panes. Floating, popouts and edge docking are disabled. Settings and
Reminders are not tabs: each is a native modal dialog over the dock, which stays
mounted.

Each open session tab has its own `SessionWorkspace`, with its own display,
runtime, review and notes owners (`createSessionPaneOwners` in `main.tsx`). The
model sets `tabEnableRenderOnDemand: false`, so the content of a hidden tab stays
mounted: waits in flight, drafts and the loaded timeline survive a change of tab.
`SessionTabContent` follows the visibility of its node and tells the pane, which
observes only while it is visible, the workspace is shown and Settings is closed.
A pane that does not observe pauses its automatic history, live, runtime,
receipt, Ask, notes and choice reads until it is visible again; the visible panes
of a split keep observing. A hidden session tab also keeps the element it
rendered when it was hidden (`retained`), so a render of the window does not cost
more with each open tab; the tab of the selected session is never kept that way.
Hiding a pane does not cancel admitted commands. Closing an inactive tab does
not read history or replace the active display attachment. Pending/uncertain
Sends retain the exact original request and are never retried by navigation.
Draft text retains the existing bounded local storage behavior; scroll/follow
memory is window-local, not restart persistence. See "Workspace" in
[CodeAlta Desktop](desktop.md).

The tabs and the layout are those of the shown space. `App` keeps one model for
each space (`spaceLayout` in `main.tsx`), gives it to the strip as `layout` and
keys the strip by the id of the space. Showing another space unmounts the panes
of the space that is left: only the tabs of the shown space are in the page, and
the sessions of the others keep running in the host. The tabs of a space come
back when it is shown again, with its panes where they were; after a restart the
tabs come back from the storage keys of the space and the panes are laid out
anew, because the model is kept in memory only. See "Spaces" in
[CodeAlta Desktop](desktop.md).

## Keyboard and indicators

- Ctrl+Alt+Left/Right (TUI tab navigation) or Ctrl+PageUp/PageDown: previous/next,
  wrapping. Ctrl+W closes; Ctrl+Shift+T reopens the last closed tab.
- A middle click on a tab closes it as its close button does, through the same
  action (`tabMiddleClickAction` in `sessionTabLayout.ts`, called from the
  `onAuxMouseClick` of the dock), so a tab without a close button stays. A click
  with a key held does not close, and a middle press on a tab does not start
  scrolling.
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
identity, with the transient tab (`preview`) and the scopes whose prompt has a tab
of its own (`drafts`) when there are some, not providers, execution state or
request authority. A stored state without them, or with one that is not
understood, restores the tabs it has. Limits are 32 open
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

The one change of identity the window makes itself is a move of a session to another project: with the host's
answer naming each moved session and its new project, `retargetSessionTabs` (`sessionMove.ts`) points their
open, closed and active tabs at the new project and path, in the shown space, in the tabs kept for the other
spaces and in the tabs stored for spaces not entered yet, in the same update as the refreshed list. A tab keeps
its place in the list; the dock gets a new node for it.

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
selecting a saved tab restores that tab's scope. Settings and Reminders, modal
dialogs over the dock, keep the editor mounted and inert.

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
Restore drops tabs whose project is missing, archived or another folder. A stored
tab that is not understood (a kind that another build stored) is left out and the
tabs beside it are restored, with no active file tab when it was the active one;
a state that is malformed as a whole (another version, no list, over the limit)
restores nothing.

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
