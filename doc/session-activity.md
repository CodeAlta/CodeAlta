# Observed session activity and recent-session presentation

The WebView does **not** claim complete persisted `LastActiveAt` parity. The TUI's
`SessionRuntimeStateReducer.UpdateSessionFromAgentEvent` assigns the event timestamp
in arrival order; `UpdateSessionSummary` also updates activity. In contrast,
`SessionViewJournalHeader.ToDescriptor` in `SessionViewJournalStore.cs` initializes
`LastActiveAt` from `CreatedAt`. Neither this header nor saved `UpdatedAt` proves the
historically latest activity. The TUI navigator applies `RecentSessionsPerProject`
to its hierarchy; its settings validate 1–50. WebView presentation uses the same
range but deliberately does not import that unproven historical interpretation.

### Persisted projection prerequisite

The Desktop workspace snapshot does not load `SessionViewDescriptor` records. Both
owned and catalog-only reads enumerate `AgentSessionMetadata` and optionally enrich
at most 500 eligible rows with `SessionViewJournalHeader`. Neither metadata (including
its cached view state) nor that header contains an independently persisted
`LastActiveAt`. The SQLite projection orders its stored `UpdatedAt`; that is not a
separate last-active source.

`SessionViewYamlSerializer` does preserve `last_active_at`, but
`SessionViewCatalog.LoadInternalAsync` / `SaveInternalAsync` use that representation
for legacy internal sessions, not the ordinary sessions enumerated by this snapshot.
Recovery descriptors also assign `LastActiveAt` from `UpdatedAt`; attached-runtime
descriptors can use the last terminal event or creation time. Thus projecting a
descriptor property solely because of its name would mix distinct sources.

A persisted last-active feature needs an explicit durable producer and cached
read contract for the already-enumerated sessions, with missing/legacy values
remaining unknown. It cannot be implemented by relabelling saved updates, importing
the legacy internal-session scan, or substituting attachment-local observations.
Until that prerequisite is authorized, existing saved-update and observed-activity
presentation remain separate and unchanged.

## Attachment-local facts

The existing session actor observes matching `AgentEvent.Timestamp` callbacks on
its current attachment, before event projection. Exact session and provider IDs
must match. Events on replaced, retiring, terminated or transitioning attachments
cannot update the observation. No event consumer, history scan, provider preparation,
session activation, persistence migration or lifecycle authority was added.

`SessionRuntimeCurrentEntry.Activity` and the existing bounded current/scoped RPC
projection expose:

- `timestamp`: last valid **admitted** matching timestamp, including its offset;
  arrival order may move it backwards. It is not a maximum, wall-clock observation
  time, creation/update date, run ID or complete history.
- `source`: `admitted_agent_event`, not a guarantee that every event was produced
  directly by a provider rather than by the agent infrastructure.
- `admittedEvents` and `omittedEvents`: canonical decimal strings, each bounded by
  signed Int64 maximum and saturating there. Omissions count foreign identities or
  invalid timestamps encountered on the current admissible attachment. Callbacks
  excluded by retirement/lifetime admission are **not** a counted history of loss.

The typed event timestamp is nonnullable; default/year-one values are rejected.
Before the first valid event the timestamp is null, admitted count is zero, and
activity is unknown. Invalid events leave the prior valid value unchanged.
Replacement resets all activity facts; returning to the same configuration does
not reuse them. Future provider-supplied times are not corrected using a clock.

## Presentation only

General settings persists **Recent session display count (1–50)** in WebView local
storage (`codealta.desktop.recent-session-count.v1`, default 20). Malformed values
are not overwritten automatically; defaults and unavailable/failed storage are
reported in settings. Failed writes still apply in this window.

The navigator limits loaded saved-update tree rows, retaining the active row and
verified ancestors of retained rows. Thus the displayed number can exceed the
preference. Existing explicit search filtering is unchanged. **Show all loaded
sessions** and **Browse saved sessions** remain available. Neither increases the
snapshot scope or makes a globally-most-recent claim.

The saved browser offers distinct Saved update, Name and **Observed activity
(explicit refresh)** ordering. The latter uses only eligible, nonstale attachment
observations, with exact submillisecond ordering; unknowns follow in stable saved
order with explicit unknown labels and dashed separation. Active/highlighted browser
rows are retained within the existing 200-match candidate cap. Search can reach
other loaded matches. **Show all loaded matches** bypasses only the preference,
not existing snapshot/200-row bounds.

Only the existing **Refresh statuses** action obtains facts: at most 32 loaded
candidates per explicit refresh, independent of the presentation count. Existing
64-KiB response/2-MiB batch limits, sequential reads, deadlines, scope/host/catalog/
selection and attachment-generation fencing remain. Partial observations are not
atomic or continuous liveness. Stale/unknown timestamps never fall back to saved
dates. Order/count changes do not change tab order, selection, follow state,
drafts, original pending Sends, command admission or hidden Display subscriptions.

Historical latest activity, native accessibility/screen-reader qualification and
full TUI parity remain open. Disposable fake-host/browser tests are not real-provider
or native-platform qualification.
