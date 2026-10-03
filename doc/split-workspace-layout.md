# Session/content and opt-in split workspace presentation

## Actual App: controlled session/content projection

`main.tsx` uses the App-specific `SessionContentLayout.tsx` for its sessions
rail (including notes) and content. Projects and their existing splitter stay
outside it. Fresh caller nodes enter one stable private two-tabset model;
the existing `SessionWorkspace` key remains `[projectId, selectedSession.id]`.
Session/project/epoch, request, draft, display and preference owners have not
moved into factories. Settings remains a sidebar-entry native modal outside
the factories with the workspace mounted and inert. **Reminders intentionally
unmounts the workspace**; returning mounts new DOM while App-owned drafts and
requests survive. Closing notes still intentionally unmounts NotesPanel.

This is not the library's default splitter behavior. App retains its existing
accessible **Resize sessions** control: live pointer-captured dragging,
ArrowLeft/ArrowRight by 16px, Home and double-click reset. Public
`classNameMapper` plus application CSS suppress the library divider; no package
DOM or ARIA is rewritten. Public model attributes project the App-derived
session width plus the 8px control into the rail tabset's min/max dimensions.
All Layout-originated actions are refused; only App projection updates apply.

`paneLayout.ts` still owns the sizing policy: sessions 220–560px (default 310),
projects 160–440px (default 240), content minimum 480px. Collapsed projects use
no width budget. Only explicit gestures change persisted pixel preferences;
viewport/parent constraints and responsive projection do not save their reduced
sizes. Desktop clipping below the combined minima remains existing policy.

At viewport widths at most 875px the same model projects vertically, without
desktop width minima or an interactive divider. An App-owned hidden CSS sizing
probe measures the existing top-row rule: max(340px, 45vh), or max(180px, 40vh)
at heights at most 600px. Opening projects occupies that top row outside the
adapter. Sessions and notes stay mounted but hidden with zero allocation and
no focusability; content occupies the remaining row. Closing projects restores
the same child identities. Public tab resize subscriptions and model updates
share a coalesced public `Layout.redraw()`; cleanup disconnects the probe observer,
removes subscriptions and cancels any pending frame, including StrictMode replay.

The content slot now also contains the App-owned [session navigation strip](session-tabs.md)
above the one active keyed workspace. Tab navigation does not add hidden live
workspaces or change this adapter's private model, sizing or lifetime contract.

## Separate opt-in fixed horizontal split

`src/CodeAlta/frontend/src/SplitWorkspaceLayout.tsx` is a fixed horizontal
two-pane presentation component. It remains **opt-in only**, separate from the
App-specific projection above. It and the single-pane `WorkspaceLayout` remain
compatible and unchanged by the App migration.

The caller supplies fresh `left` and `right` React nodes. The private model and
pane IDs persist for the component lifetime; factory invalidation delivers new
props without changing the model. Normal React identity rules still apply: a
caller changing a child's type or key intentionally remounts that child.
Like `WorkspaceLayout`, the component uses the existing `.workspace-layout`
styles and requires a sized parent. Each pane has a 160px minimum; the tested
containers exceed the combined minima plus the 8px separator. Undersized
containers, vertical layouts and realtime dragging are not qualified here.

Only public weight-adjustment actions for the one fixed row are admitted.
Close, drag/docking, rename, maximize, popouts and command shortcuts remain
disabled. Structural separator arrow-key resizing remains available. There is
no persistence, topology mode, navigation or generalized docking API.

### Fixed-split public redraw adapter

Weight changes request one coalesced animation-frame `ILayoutApi.redraw()`.
The production regression also reproduced stale ARIA on viewport shrink:
at 420px, actual left width was 160px, but ARIA remained `16` instead of `38`.
This differed from the earlier accepted fake candidate's responsive result.
Both measured samples agreed; waiting for geometry alone was insufficient.

The narrow correction subscribes to both private tabs' **public `resize`
events**, using that same coalescer. This handles parent/viewport geometry
changes without another `ResizeObserver`, private APIs, package changes or
manual ARIA writes. Effect cleanup removes both subscriptions, cancels a
pending frame and clears its handle, including StrictMode replay and unmount.
The adapter is not a universal React commit barrier.

## Maintained verification

For the focused App migration checks, from `src/CodeAlta/frontend`,
using existing installed tools (no full suite or other renderer reruns):

```sh
./node_modules/.bin/tsc --noEmit --skipLibCheck false
./node_modules/.bin/tsx --test src/paneLayout.test.ts
./node_modules/.bin/tsx --test src/splitWorkspaceLayout.browser.test.ts
npm run build
```

The split test bundles the **actual production components** with an isolated
fake caller (`splitWorkspaceLayout.mount.tsx`) and production CSS. Setup,
refresh and replacement use ordinary `root.render`, not `flushSync`.
It checks StrictMode same-DOM effect replay, fresh left/right props, retained
input drafts/focus, disabled commands/drag, single-pane compatibility,
geometry-matched separator ARIA, keyboard/pointer movement at 1000px/420px and
500px parent-only width, viewport expansion to 1100px, both 160px bounds,
synchronous action-burst frame coalescing, pending-frame cancellation and
fresh-instance isolation. Fixture-only wrappers capture ownership when each
public resize callback is registered, count genuine deliveries, and record frames
scheduled during that callback. Recording context is restored in `finally`;
pending/fired/canceled accounting retains that registration's owner rather than
the latest mounted instance. Separate source counters distinguish synchronous
key bursts, genuine resize callbacks and explicit callback replay. Ordinary
trusted keyboard/pointer action frames outside fixture bursts and arbitrary
library renders are not counted; this is not an independent redraw-call count.

Real viewport and parent-only changes must deliver public resize callbacks and
schedule/fire frames (at most eight callbacks/four resize-origin frames per
tested isolated size transition). Geometry/ARIA and source counters must settle
within paired samples after six then three frames. Explicit twelve-frame quiet
samples then require unchanged geometry, ARIA and all recorded counters, with
no pending frames. These are finite test budgets, not general library guarantees.
The same-turn key burst must coalesce to one frame; subsequent measured resize
deliveries may legitimately schedule more frames, so one *total* frame across
settling is not required.

For deterministic pending-resize cancellation only, the fixture replays both
captured public callbacks twice using their last genuinely delivered arguments,
then unmounts in the same JS turn. This is separately labelled **replay**, not
browser resize proof, and does not mutate model or DOM geometry. Four synchronous
replays must coalesce to one pending frame, canceled (never fired) on unmount.
Subscription cleanup and disposed counters must remain unchanged while a fresh
instance receives real parent resizing and passes another quiet sample. Public
subscription/removal wrappers also check StrictMode replay and balanced cleanup.
Balanced effects and disposed counters do not establish universal quiescence.

The same split fixture also mounts the production `SessionContentLayout` under
ordinary scheduling and StrictMode: responsive geometry at 875/876px, hidden
rail zero footprint, retained caller DOM/input/focus, fresh props, supplied
control ARIA, genuine parent-resize callbacks, finite quiet samples, replay
coalescing/cancellation, observer/subscription cleanup and fresh-instance
isolation. It does not reimplement App gesture handling.

The split browser harness uses the existing Windows Edge executable paths and
page-scoped HTTPS interception convention under the unchanged production CSP.
It permits only three fixture assets and no host bridge, fails closed on
setup/page violations, and has no alternate launcher/launch retry loop. An
absent approved browser fails this test rather than silently qualifying it.
Unique temporary evidence/profile directories are retained and reported in TAP;
`observations.json` includes finite paired geometry samples, page violations,
close acknowledgement and root-exit observations. This is **not** real transport
finality, all-target/descendant containment, native integration, screen-reader
qualification or universal settling. Original ignored candidate evidence is
not used as a runtime dependency and remains unchanged.
