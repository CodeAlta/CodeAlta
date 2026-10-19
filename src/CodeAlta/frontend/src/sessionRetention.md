# Desktop retention protection

`sessionRetention.ts` owns one random page identity (never persisted), increasing publication revisions,
and a monotonic protected union for the host's **one startup pass**. `sessionRetentionRpc.ts` uses the
generated contract hash and the normal `invoke` boundary. Only the isolated `demo` contract simulates
acknowledgement; missing/failed real RPC calls refuse admission. No timeout releases a claim.

## Inventory and restoration

- Read every `codealta.desktop.sessionTabs.v1` key and `.<spaceId>` variant, including hidden/orphaned
  spaces, before presentation reconciliation can overwrite them. Closed-tab history is not an open tab.
- Include every nonempty `codealta.desktop.prompt.<sessionId>` text draft, including whitespace and IDs
  absent from the catalog. Include every image-draft/pending-paste key from `createImageDrafts`, even
  offscreen. Images have no persisted storage format today.
- Storage access, enumeration, malformed/oversized records, and changes during enumeration fail closed.
  Recoverable IDs are still published; ambiguity is latched, never silently replaced by an empty set.
- `connect` and observation publications cannot establish initial readiness. `restore` includes the
  initial selection (including the implicit first catalog session) and all restored tabs. Only a complete
  inventory can accompany `ready:true`; the initial shell selection is exposed after its acknowledgement.
- Before `connect`, the shell consumes the first accepted item of `sessionRetention.watch`. Channel-open
  alone is insufficient. The server binds the page ID to the host-authenticated `DocumentSessionId` and
  requires that active lifetime for publications. The stream stays open; loss/error invalidates editing.
  The reader continues consuming it while publication is held, so a late publication acknowledgement
  cannot expose an editor after the lifetime stream has ended.
- Only the end of that stream (its error, a refusal, a closed connection) ends the lifetime. Missed liveness pings
  do not: aborting the stream retires the page ID on the host for good, so a host that was only slow left every
  pane held after it answered again. Once the lifetime ended, the window shows a lasting notice with **Reload**,
  the only way to a new page ID.
- A publication that fails (a transport failure, `internal_error`) adds nothing and keeps the acknowledgements
  before it: the host's claims only grow.

## Shell admission routes

`main.tsx` starts with an empty session tab strip. `protectNavigation` executes synchronously only when
every candidate already has an acknowledged claim. Otherwise it defers the **whole transition**, with
latest-navigation, catalog, browser revision, host epoch and component-lifetime fences.

| Route | Boundary |
| --- | --- |
| Initial selection / restored active and inactive tabs | `retention.restore`; no labels or editor before acknowledgement |
| Tab activation, reopen, reconciliation and close recovery | `applyTabState` / `setTabs` |
| Row, menu, keyboard, search, session links and delete recovery | `selectProject` / `setSessionId`; row/menu actions defer their follow-up controls too |
| Inactive space, externally requested space, removed-space recovery | `showSpace` admits all entering IDs plus the requested target before changing space |
| New-session text/image handoff | Existing creation admission before copying local draft or selecting the returned ID |
| Existing-session work item | Admission before invoking the work API |

All session-state writes use these boundaries; the previous effect-only tab admission is removed.
Re-selecting an already admitted session cancels an older pending selection. A refused or stale claim
cannot expose its editor. New-session local text/images have no saved session ID until the creation
handoff; host-created sessions/work remain the host/runtime owner's responsibility.

## Editors and plugin handoffs

`SessionRetentionGate` mounts saved panes only after their claim. Invalidation makes an admitted pane
inert rather than destroying an editor whose newest text may exist only in memory. Text edits, recall,
queue drafts, paste completion, image edits, programmatic sends and controls check the live claim and
component lifetime; a late callback cannot rely solely on a previous render's enabled state.

Plugin prompt handoffs do not acknowledge a React state update or a page-local queue. The composer
captures and awaits the existing send/steer/compact submission helper. Enqueue remains a deferred Send,
including for an unattached saved session, and carries its captured next-send selection. Its queue owner
resolves completion only after an actual receipt or conclusive removal before dispatch; cancellation
during a sending/uncertain original cannot release it or schedule a new attempt. `PluginUiHost` keeps reading
Close events while that original admission is held:

- `true`: a downstream accepted/replayed owner receipt; Respond may transfer ownership.
- `false`: a conclusive pre-dispatch or host refusal; Respond may cancel the handoff.
- `null` or throw: uncertain/canceled transport; no Respond, so the host retains protection.

Close/disconnect aborts the local waiter, not host work. Unknown Close suppresses a later delivery without
asserting that an earlier page's worker stopped. Duplicate IDs do not execute twice within a watcher
epoch; disposal prevents old completions acknowledging a replacement epoch. Backend pending handoffs
must retain their claim through timeout/disconnect until a conclusive response or downstream ownership.

## Required native composition

The RPC's callback-bound constructor must receive a **host-registered window owner on the same shared
registry as retirement**. Register the window unknown before exposing its bridge/page. A renderer's new
`pageId` is not window registration, and one window cannot certify another window's restoration.

The native owner calls `InvalidatePage()` on actual page/bridge disconnect, replacement or shutdown
(not initial navigation setup). It retains the admitted union and permanently latches that window
unknown for this one startup pass. Even an old callback queued before its first publication cannot
restore readiness after invalidation. This conservative rule avoids relying on a final unload RPC or
guessing whether a disconnected page still owns drafts. Stable startup still permits retirement of
unprotected IDs; there is no blanket lifetime claim in the ordinary path.

The lifetime iterator registers its effective cancellation token before yielding the first item. Channel
or transport cancellation marks the owner unknown synchronously, without waiting for channel draining
or resource disposal; iterator disposal also invalidates it. An unstarted channel cannot establish
readiness, and a different document cannot publish through another document's watch. Old stream cleanup
cannot invalidate a replacement stream, but the startup pass's unknown latch never resets.

The legacy unbound RPC constructor intentionally remains unqualified. Native host composition owns
registration, external-work qualification, its bounded first-ready wait, and the single captured
`RunOnce` call. Settings changes and later reconnection do not restart cleanup. Frontend/RPC tests alone
do not certify that native composition.
