# Desktop Session Info

Open the existing composer **Session info** icon, use **Ctrl+G, Ctrl+T**, or the
command palette. The compact native HTML dialog preserves the existing modal,
IME/Escape and guarded focus-return behavior. Settings and other modals do not
dispatch these commands through their overlays. This is not native WebView or
screen-reader qualification.

## Recorded and observed sections

- **Saved metadata:** bounded displayed title, exact session identity, verified
  scope, recorded working directory/provider and recorded creation/update times.
  Dates retain their supplied offsets. Saved update time is not last-active time.
- **Observed runtime configuration:** explicit attachment/runtime/run identity,
  transition/retiring/terminated/queue-drain facts, provider ID/key, model,
  reasoning and current attachment/pending next-Send agent prompt IDs. These are
  not saved metadata, local next-Send overrides, prompt text or configuration files.
- **Last-observed usage:** one admitted event, its source/reported scope/sequence,
  source/event times, reported window and last-operation values, invalid/omitted
  flags and omitted callback count. Missing is unknown, not zero. Model/catalog
  limits are not occupancy; history is not summed. Reported cost is shown only as
  supplied, with currency explicitly unspecified.

Opening the dialog performs no observation read. **Refresh observed details**
uses the existing scoped noncreating runtime read, then the scoped usage read
only when there is a stable attached runtime. No polling, provider preparation,
activation, hidden Display attachment or history scan occurs. Catalog-only,
archived, demo and unverified scopes retain recorded information with unavailable
observations. Refresh does not change command permission or original uncertainty.

At most two unary reads are made per refresh: the existing 64 KiB runtime and
32 KiB usage response limits, each with a 10-second client wait timeout. They are
**not atomic** across actors, runtime changes or external catalog/header writers.
Host/runtime/attachment disagreement withholds both observed sections rather than
merging a false coherent snapshot. Missing or transitioning runtime does not mean
idle/completed. An observed active run is not liveness or finality.

App revisions synchronously fence host, catalog, selection/scope and navigation,
including same-value ABA. Input metadata changes, refresh, close/reopen and
teardown cancel/reset dialog-owned waits and feedback. Same-host runtime changes
and decreasing attachment generations are refused within the open capture;
this is not durable attachment history. Canceling a wait does not prove host
actor work stopped. Saved metadata updates retain the mounted dialog and reset
observations without replacing the active workspace/draft/scroll/request owners.

## Clipboard

**Copy session ID** remains exact. **Copy displayed details** copies only the
displayed sections' canonical English labels, literal data and non-authoritative
explanation, regardless of the WebView display language. It never includes raw
JSON, hidden configuration, prompt/source files or private error text. The copy
is refused above 32,768 UTF-16 code units rather than silently truncated. Success,
unavailable clipboard and failure have explicit feedback. Feedback is fenced by
input/request/lifetime generation; an already-started OS clipboard write cannot
be revoked after navigation.

The browser, Info and usage-inspector static presentation follows the six-locale
[WebView language](webview-localization.md). IDs, titles, paths, timestamps and
controller/backend diagnostics remain literal. Locale updates do not remount
dialogs, restart pending reads/Copy, or change observation/mutation authority.

Focused disposable fake-host tests cover the actual App, saved/observed separation,
unknown/zero/invalid usage, provenance/omissions, cross-read mismatch, canceled
reads, clipboard success/failure, close/reopen and catalog/host/session ABA,
keyboard/focus and narrow light/dark geometry. Continuous liveness, authoritative
recentness, comprehensive accessibility and full/native parity remain unclaimed.
