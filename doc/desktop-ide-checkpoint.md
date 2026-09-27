# Desktop IDE runnable checkpoint

**Parent acceptance:** independent source review and 61 focused functional tests covering
references, composer, tabs, runtime observations, shortcuts, Queue evidence, width and reminder
actions pass, along with strict TypeScript. Child Release desktop/frontend builds pass.
This checkpoint is accepted for an incremental implementation commit, not full redesign or
mounted/native/browser qualification. The historical pending-review statements below are superseded.

**Current status (18:50, supersedes historical continuation statuses below):** reference-popup
IME lifetime bug corrected regression-first. Durable captured-source validity is separate from
transient action readiness: composition/query renders do not retire the review, but metadata
read admission (including the debounce callback), choose, close-edit and explicit focus remain
IME-gated. Composition end permits only a still-authorized query; canceled responses remain
fenced. Post-close revalidation and stale-input regression are preserved. 20/20 scoped tests,
strict TS, Release desktop and frontend builds pass; known bundle-size advisory only.
Parent independent review/commit pending; nothing staged or committed by child. Browser/native
IME ordering, focus and mounted lifetimes remain unqualified. Exact logs are in REPORT.

This is a partial development checkpoint, not acceptance of the complete redesign.
The user deferred further visual/browser geometry qualification on 2026-09-27 to try the
application manually. Existing tests and failure evidence are retained; no tests are disabled.

**Continuation status (18:30):** real FlexLayout session-tab candidate is ready for parent
review. Both fixture-only stops were corrected with authorization; original reds remain.
33/33 scoped functional tests, strict TypeScript, ordinary Release desktop and separate
frontend builds pass. Mounted request/draft/scroll/subscription lifetime across the new
factory boundary remains unqualified; source/model tests are not browser/native acceptance.
See `tmp/ide-ux-20260927/REPORT.md` for exact evidence and remaining gaps.

**Reference popup continuation (18:44):** native-palette candidate replaces inline results.
18 scoped functional tests, strict TypeScript, ordinary Release desktop and separate frontend
builds pass. A regression first reproduced stale insertion during native close; source is now
revalidated after close before edit/focus admission. All original reds remain preserved.
Parent review is pending. Native focus/IME handoff remains unqualified under the browser-test
deferral; behavioral helper tests are not proof of mounted DOM event ordering.

## Implemented scope

- One stacked Projects/Sessions Explorer, activity-rail Settings and palette, bounded persisted
  Explorer width and full-content/restore control.
- Compact owned/read-only composer, secondary overflow and separate retained-request recovery.
- Reminders modal retaining the workspace and App-owned actions across close/reopen; closing
  does not cancel an admitted request or authorize a retry.
- Existing Queue review remains a read-only projection of unresolved local originals, not live
  inventory or settled-text recovery. Exact NeoAstra 0.2.0 is used.

Real FlexLayout session tabs now project existing identities (up to 32 plus draft), with
compact status/draft/close chrome and secondary Reopen/Refresh menus. App still owns selection
and the sole active workspace; inactive factories return no live content. This candidate
awaits mounted-lifetime qualification. The reference palette keeps its query separate from the
captured draft, retains existing bounded metadata search, and inserts only on deliberate valid
selection. Provider lifecycle/switching and large-history work remain unimplemented. Notes default closed. No native or screen-reader
acceptance is claimed.

## Run locally

From the repository root, the ordinary Release build is:

```powershell
dotnet build src/CodeAlta/CodeAlta.csproj -c Release
```

It generates the NeoAstra inputs and builds/packages the production frontend. To inspect only
the in-memory browser demo without a provider, profile or production-data connection:

```powershell
cd src/CodeAlta/frontend
npm run demo
```

The demo opens `http://127.0.0.1:5173`; it does not qualify owned-host actions.
For a deliberate native launch on Windows, the built entry is
`src/CodeAlta/bin/Release/net10.0/alta.exe`. Run it from the intended project directory.
**No-argument native startup owns the current project and shared `~/.alta` runtime, acquires
the runtime lock and may start configured providers.** It is not an isolated preview. See the
[desktop safety/isolated-root instructions](../src/CodeAlta/README.md) before launching.
Neither native nor demo launch was performed during this checkpoint verification.

## Verification and known gaps

Prior checkpoint functional owner/unit checks passed for Explorer preferences, Reminder actions/duration/
list observations, retained Queue evidence and Queue ownership, archived scope gates, composer
height/keyboard logic, project references, Info observations/presentation, Usage and shortcuts.
Strict TypeScript (`--strict --skipLibCheck false`) and the ordinary Release desktop build pass;
that build also executes the normal production frontend build. Vite retains its bundle-size
advisory. No dependency/configuration bypass was used.
Current tab-batch tests: `sessionTabLayout.test.ts` (7), `sessionTabs.test.ts` (5),
`runtimeObservations.test.ts` (5), `shortcuts.test.ts` (10), `paletteActions.test.ts` (6).
The separate frontend build also passes. Earlier owner suites were not rerun in this batch.

Earlier actual-App Save-lifetime and isolated Reminder browser passes remain evidence at their
recorded revisions, not blanket visual acceptance. Latest Info browser failure is a stale
Settings selector (`.project-rail .icon-label-button`); Settings moved to the activity rail.
The same selector remains in the Usage browser fixture. This is not fixed or suppressed.
The prior Info Flex ancestor selectors were migrated with exception/geometry diagnostics,
but the entire Info suite has not passed. Usage/transient/IDE visual reruns are deferred.

No full suite, website build, native launch, live-data/provider validation or parked visual
work was run. Detailed per-file results and preserved historical reds are in the local
`tmp/ide-ux-20260927/REPORT.md`. Manual feedback is the next acceptance step.
