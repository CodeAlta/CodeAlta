# Desktop IDE runnable checkpoint

**Parent history acceptance:** independently passed 18 reader, 12 Desktop RPC/budget,
1 owned-read admission, and 57 frontend functional tests plus strict TypeScript.
Source review covered reader bounds, store containment, shared read admission, RPC projection,
retained history windows and source-inspection lifetimes. Child Release/frontend builds pass.
Accepted for an incremental implementation commit; mounted/native behavior remains unqualified.
Provider switching still requires the explicit failed-switch recovery decision below.

## Step5 scoped candidate ready for independent review (19:28)

The authorized `historySource.ts` → `loadHistorySource.ts` rename and two import updates resolve
the Windows module collision without compiler relaxation. Normal generated RPC registration is
in place. Extended timeline/source routes now pass focused Desktop history tests12/12; reader18/18
and owned-admission1/1 remain earlier child evidence, not independent parent verification.
Final browser-free history/source tests17/17 and timeline/reconciliation/scroll/localization tests
40/40 passed. Strict TS, normal Release desktop (generated bridge/assets) and frontend builds
passed. Exact commands, timestamped logs and retained original failures are in REPORT.
Vite's pre-existing large-chunk warning remains; no build threshold was changed.

History is bounded to1,000 events,2 Mi UTF-16 text units and32 automatically acquired pages.
Later errors retain healthy rows with an explicit partial/old-revision notice; refresh and older
paging remain deliberate. Extended records are capped at8 MiB, incremental reads at64 KiB;
legacy128 KiB policy and208 KiB inbound RPC remain unchanged. Timeline serialized projection
retains700 KiB budget with16 KiB reserved for source metadata; source responses stay below104 KiB.
These are CodeAlta policies, not NeoAstra transport limits. Parsing still materializes full bounded
records and has payload-dependent overlapping allocations; it is NOT streaming JSON parsing.

Explicit full raw source is an inline plain-text inspector:16 KiB UTF-8 chunks replace each other,
and Copy copies only the displayed chunk. Original/current browser lifetime, navigation generation,
selected revision, modal retirement, disconnected/inert controls, IME/repeat guards and aborts fence
reads/actions. Copy completion is bound to its source chunk. Static chrome is localized; raw content
is not translated. Existing mounted fixtures forward the new route without inventing source bytes;
their source route is unconfigured. Browser-test route assertions were migrated but NOT executed.

This is scoped implementation/build evidence, not native/mounted focus, geometry, rendering or
event-order acceptance. No browser/native/live-data/real-provider/full-suite/site runs, staging,
commit or notes retry. Provider switching remains blocked on recovery policy. Parent review is
pending; earlier stop sections below are historical and all original logs remain intact.

## Historical stop: source loader/component module collision blocks frontend compilation (19:20)

Parent-authorized HistorySourceRequest JSON registration is applied adjacent to its response.
Normal RPC contract generation and .NET desktop compilation succeeded; the project-invoked
frontend TypeScript build then failed. Log: `history-rpc-registration-20260927-192018-641.log`,
exit1, from the focused DesktopTimelineHistoryTests/DesktopHistoryTests command in REPORT.
**Zero Desktop tests executed; complete desktop/frontend builds have not passed.**

Diagnosis: on Windows, extensionless `./HistorySource` imports in HistoryPanel.tsx10 and
TimelineMessage.tsx9 resolve to historySource.ts (loader), not HistorySource.tsx (component).
TS2724/TS2305 missing-export errors accompany TS1149/TS1261 casing diagnostics. Proposed narrow
correction is rename loader to `loadHistorySource.ts` and update its imports in HistorySource.tsx
and historySource.test.ts. Not applied; stopped at first new unexpected failure without retry.
Original NEORPC009 and earlier red logs preserved. Reader18/18 and owned1/1 are child evidence,
not independent parent verification. Remaining RPC/frontend fences, localization/fixture coverage,
strict TS and completed Release desktop/frontend builds are pending. No generator/config weakening,
notes retry, staging/commit or prohibited execution. Provider recovery still blocked; no policy.
Earlier statuses below are historical; materialized-record allocation qualifications still apply.

## Historical stop: RPC JSON metadata blocks generated bridge compilation (19:18)

Exact parent-authorized inner catch rename to `parseError` is applied without filter/semantic
changes. Focused reader verification passed18/18 (`history-parser-rename-20260927-191429-401.log`,
exit0). The original parser compile-red remains preserved and executed zero tests.
Focused owned-workspace timeline/source admission verification passed1/1
(`history-owned-20260927-191741-545.log`, exit0), covering shared read capacity, canceled waits,
validation and close/drain behavior using literal callbacks only.

The next Desktop history test command stopped during compilation: NEORPC009 at BootRpc.cs137,
missing `[JsonSerializable(typeof(HistorySourceRequest))]` on DesktopJsonContext. Inspection
confirms the response is registered but the request is not. Other missing generated-contract
and Add*Service errors follow. Log: `history-rpc-20260927-191754-297.log`, exit1;
**zero Desktop tests executed**. No fix or retry after this first new unexpected failure.
Next proposed correction is the one request registration, subject to parent authorization.

RPC source-envelope reserve now targets the existing700 KiB ceiling (16 KiB reserved inside it),
not the earlier experimental724 KiB assertion. New serialized100-row fixture and source validation
assertions remain unexecuted. Frontend source lifetime/modal/input/abort/stale-response fences,
fixture forwarding/localization, browser-free tests, strict TS, generated bridge Release desktop
and frontend builds remain pending. This is incomplete, unaccepted step5 work. No native/browser,
live-data/provider/full-suite/site execution, staging or commit. Provider recovery remains blocked.
The materialized-record allocation caveat below still applies; no constant-memory claim.
Full commands/evidence are in `tmp/ide-ux-20260927/REPORT.md`. Earlier stops below are historical.

## Historical stop: authorized parser correction does not compile (19:13)

Parent authorized treating relevant deserialization failures consistently as final-tail omission
versus interior `corrupt_record`. The candidate now catches only `JsonException` and
`NotSupportedException` around the deserialize calls in the extended fallback and both legacy
small-record paths. Legacy adjustment is necessary because the extended normal-page path uses
the legacy tail parser: discriminator failures must not classify differently solely by size.
I/O, cancellation and exceptions outside those deserialize boundaries remain uncaught by this
filter. Existing byte ceilings and final/interior position rules are unchanged.

The original fixture/assertions remain intact. Added explicit small/large cases for truncated
canonical events, malformed known-discriminator payloads and missing/unsupported discriminators,
each at final and interior positions; small forward reads are included too.

**First NEW unexpected verification failure: CS0136** at
`AgentJournalHistoryReader.Timeline.cs:73`. The newly introduced inner catch variable `error`
shadows the enclosing `record_too_large` catch variable at18. Build stopped before tests ran:
`history-parser-correction-20260927-191256-379.log`, exit1. No correction or retry after this red.
The exact next correction is renaming the inner catch variable; it has not been applied.
All broader step5 work and qualification listed below remain pending. Candidate does not compile.

Allocation qualification: the fallback uses incremental I/O, **not streaming JSON parsing**.
At the8 MiB physical-record ceiling it can retain an8 MiB byte array, up to16 MiB decoded
UTF-16 string, plus deserializer scratch and the parsed event/object graph; these may overlap
the original256 KiB fast-path buffer and64 KiB scan block until collected. The parsed graph and
RPC formatting add payload-dependent allocations; no measured peak or constant-memory claim
is established. Concurrent admitted reads multiply this cost. Source chunks and displayed
windows have separate finite budgets and do not remove that materialization cost.

## Current step 5 STOP: first new unexpected failure (19:10)

**Incomplete candidate, not ready to commit.** Reader verification stopped at16/17 (exit1):
`tmp/ide-ux-20260927/history-reader-20260927-191002-405.log`.
`LargeJournalHistoryTests.Timeline_RecordCeilingAndCorruptionRemainExplicit` uses truncated
`{"content":"...` without an AgentEvent discriminator. System.Text.Json throws
`NotSupportedException` for that missing polymorphic discriminator before validating the
truncated string. `AgentJournalHistoryReader.Timeline.cs:72-73` catches only `JsonException`,
so the tail-omission assertion is not reached. This is an unexpected classification gap,
not a demonstrated size/transport failure. The interior-corruption portion of that test was
not reached. No catch broadening, fixture change, assertion weakening or retry was performed.

The new multi-MiB paging/full raw-source round-trip and revision/cursor/UTF-8/cancellation/
between-page append tests passed, alongside the unchanged legacy-reader cases. Exact8 MiB
and over-limit checks execute before the failure, but the enclosing test remains failed.
No claim of complete malformed-record or concurrent-reader qualification is made.

Reader/store/owned route, new RPC contracts, bounded frontend accumulation/source inspector
and associated tests are currently written but incomplete. Desktop RPC tests, browser-free
frontend tests, strict TS, and ordinary Release desktop/frontend builds were NOT run after
this stop. The generated bridge has not yet been rebuilt for the new endpoints. Remaining
integration review must cover demo/mounted fixture forwarding and source guards that currently
expect `workspace.historyTail`, source-review lifetime, translated chrome, owned read admission,
and worst-case multirow wrapper accounting (legacy page700 KiB plus bounded source envelope;
the new wrapper test currently uses a724 KiB ceiling). These are open checks, not passing claims.

Next safe action is parent review/authorization for explicit malformed-syntax versus unsupported
polymorphic-record classification and corresponding tests; retain this red. No provider policy,
staging/commit, browser/native/user-host/live-provider/full-suite/site work. Mounted focus,
anchor/follow geometry and native transport remain unqualified. Historical statuses follow.

## Current step 5 contract (implementation in progress)

Parent authorized history before the blocked provider step. Disposable 2 MiB journals reproduce
`record_too_large` both at initial newest read and after a healthy newer page (reproduction log
`history-reproduce-20260927-190133-941.log`, exit0: expected limitation asserted).

The desktop timeline will use a separate extended-tail route, preserving legacy forward/tail
reader contracts. Normal pages retain the 256 KiB/100-record fast path. When that path cannot
fit a record, scan backwards in at most 64 KiB reads and admit one physical record up to 8 MiB;
no whole-journal load, skipping, rewrite or retry of provider work. The fallback has finite
scan plus record-read work (roughly 16 MiB plus the initial window), and materializes only one
bounded record for canonical parsing. Larger records remain an explicit limitation, not an
unlimited allocation. UTF-8 and final-malformed-record rules remain unchanged.

Every extended response carries the observed length/write-time revision and per-record source
ranges. Full raw UTF-8 journal source is explicitly readable in at most 16 KiB chunks through
the same contained, locked, cached store and owned read admission. Each request is bound to
session/revision and a bounded source range; responses replace the displayed plain-text chunk,
not append an unbounded DOM/string. No raw HTML/Markdown interpretation. Length/time is not a
transactional snapshot and cannot detect same-stamp rewrites; concurrent append rejects stale
pages/chunks. Source access is read-only, not permission to open arbitrary paths.

Existing history projection remains bounded at 700 KiB conservative serialized cost, including
cursor/envelope reserve; source responses fit below 104 KiB worst-case escaped JSON. Timeline
retention additionally limits string payload to 2 Mi UTF-16 units and initial acquisition to
32 pages (plus the existing 1,000-event limit); older windows slide explicitly. No auto loop
through unlimited metadata-only pages. Healthy displayed rows survive later errors, including
failed refresh, with explicit partial/error state. Existing anchor/follow hooks remain owners.
NeoAstra transport, CodeAlta's 208 KiB inbound envelope and legacy 128 KiB reader policy are
distinct: none of these limits is being globally raised. Provider recovery remains undecided.

## Current step 4 status: persistence/recovery decision required

The parent accepted and committed the integrated UI/Queue checkpoint as `6c849fdc`
(61 focused frontend tests and strict TS independently passed); the earlier isolated
NeoAstra pin is `e66d3956`. Historical pending-review statements below are superseded.

Provider lifecycle inspection stopped before backend/RPC/UI changes. Existing-session
provider switching remains unavailable, not a next-Send selection override. The genuine
new-session choice remains in its existing creation UI; compact-toolbar relocation is not
implemented in this blocked batch. No history work was started.

### Confirmed source contract and missing failure policy

- `OwnedSessionCommandContracts.cs:23-28` explicitly makes `ProviderKey` an expected
  provider, not switch authority. `OwnedSessionSelection.cs:95-101` rejects a different
  provider. The fake-provider regression
  `DifferentProviderSelection_IsAdmittedThenFailsPreparationWithoutReplacingSource`
  still passes; it checks the original receipt/selection and zero target lifecycle calls.
- TUI `SessionProviderSwitchCoordinator.cs:87-123` changes the view descriptor, applies
  preferences, detaches and persists view state. It does not attach the target. Detach's
  boolean is ignored; persistence is outside its in-memory rollback catch. This is not
  an existing atomic switch/recovery contract to port into Orchestration.
- `SessionRuntimeService.cs:996-1007` retires the old attachment before target creation.
  `CreateCoordinatorSessionAsync` resumes, and on `KeyNotFoundException` starts a session
  (`1136-1144`): that fallback must not be reused for the requested no-fallback switch.
- `AgentRuntime.cs:245-251` transfers provider summary then provider state in separate
  awaited writes. `TransferStateToProvider` (`346-356`) clears the current continuation
  ID/state. `FileSystemAgentSessionStore.cs:69-88,449-466` appends each snapshot then
  updates cache separately. An exception can occur after a journal append, not just
  before a change; the second write may never run. This is source evidence of possible
  partial persistence, not a reproduced I/O fault or a claim that old journal rows vanish.
- Orchestration subsequently writes metadata/local state (`1157-1169`) before publishing
  the replacement attachment (`1224-1232`). Recovery applies nonblank local-state provider
  over the descriptor (`517-529`). A failure between writes can leave old local selection
  and new summary/state disagreeing; a generic failed receipt does not select which wins.
- `IModelProviderSessionRuntime` exposes create/resume, not prepare/commit/rollback or
  cross-provider transfer capability. Existing transfer tests in `AgentRuntimeTests.cs:163-240`
  cover successful replay/reset, not switch failure/restart semantics.

### Admission shape to preserve after a policy decision (not implemented)

Use the existing owned receipt owner and per-session actor/transition ticket, not another
frontend lifecycle owner. Capture host epoch, exact session/runtime/attachment, expected
source provider and explicit target provider/model/reasoning plus owner-issued operation ID
and immutable client replay key. Reject same-key changed requests and stale/ABA targets;
replaying a receipt must not perform another transition. Capability/observed readiness is
eligibility, not proof of an idle provider. Atomically reserve the existing session transition
against sends, queue/drain, interactions, compaction and other writes; reject retiring,
terminating, foreign-owned or incompatible attachments. Revalidate after asynchronous reads.
Retain admitted work through detach/shutdown; cancellation of an RPC waiter is not rollback.
Never edit earlier retained requests, selections, draft text/images or uncertainty, and never
automatically send, retry, fall back, import or mutate provider defaults.

**Decision needed before implementation:** must a failed switch preserve/recover the old
provider selection, or may it leave a partial durable transfer and no active attachment?
If partial transfer is allowed, specify the authoritative selection and explicit recovery
action after each failure/restart boundary; existing resume must not silently choose it.
If failure must preserve the old selection, authorize a durable transition/commit recovery
contract in the journal/store owner before lifecycle wiring. Do not assume compensating
writes can undo failed retirement or uncertain persistence. Neither policy is selected here.

Verification: one exact owning-project fake-provider baseline passed in Release; log
`tmp/ide-ux-20260927/provider-contract-baseline-20260927-185705-137.log`, exit0.
No production/test code changed, so frontend tests, strict TS and desktop/frontend builds
were not rerun for this documentation-only stop. Fake switch race/ABA/failure tests and
RPC/UI wiring remain pending the failure policy. Mounted/native gaps remain unchanged.

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

### Screenshot-driven compact presentation follow-up

Second screenshot iteration: project filtering and session management/search are now on-demand
disclosures rather than permanent rows between a project and its sessions. Notes start visible,
including when empty. The composer explicitly overrides the old column-direction rule; selectors
are inline and reference metadata uses `@` rather than an unexplained folder icon. Timeline grid
tracks are shrinkable, prose/code wraps, and its outer viewport suppresses horizontal overflow.
Reasoning/status previews use the existing sanitized Markdown renderer, and command previews show
bounded command identities rather than whole scripts. Idle, shutdown and usage updates are omitted
from conversation cards, following `ChatMarkdownFormatter.ShouldDisplaySessionUpdate` and the TUI
history coordinator. Complete supplied aggregate unified diffs now produce per-file hunk summaries.

This iteration passed 77 focused frontend tests, strict TypeScript, the production frontend
build and the Release desktop build. The TUI presenter/formatter implementations were inspected; no mounted/browser or native
visual qualification was performed. Remaining data limitations are explicit: truncated aggregate
diff JSON cannot produce trustworthy file summaries; the host must eventually project these before
truncation. Desktop model choices use `GetObservedSelectionChoicesAsync`, not the TUI's potentially
populated provider catalog. Missing observed models are labeled **Unverified**, not falsely declared
absent from the provider. No catalog activation/probing or provider-switch lifecycle changes were
introduced. Existing-session provider switching still needs the separately blocked recovery policy.

The Explorer now nests sessions beneath the selected project in an accordion rather than
reserving separate vertical project/session panes. Project and session rows are compact single
lines. Sessions initially use the configured recent-session limit (20 by default); **Show more…**
adds another limit-sized batch of loaded matches, and **Show fewer** restores the initial limit.
Project/search changes reset expansion. The active session and verified ancestors remain visible
even beyond the limit. These controls do not load additional catalog data or change search scope.
Notes and Explorer controls remain reachable from the activity rail when the Explorer is collapsed.

Timeline status/reasoning/tool rows use compact summaries and category accents; assistant prose
retains its body/long-message preview. Raw/source, tool and file inspections open dialogs rather
than expanding raw data inline. Timeline action icons appear on hover or keyboard focus (always
on non-hover devices). File counts still describe supplied hunks, not disk state or write success.
The composer has a status/integrations line, icon-labeled selectors and directly available actions;
idle attachment/ask disclosures no longer occupy space above the prompt. Ask refresh remains an
icon action rather than an idle “Check asks” strip. FlexLayout inherits the application palette.

This follow-up passed 86 focused frontend checks, strict TypeScript, the production frontend build
and ordinary Release desktop build. Vite's existing large-bundle advisory remains. The compact
presentation tests are included in the normal frontend test script. Static-render tests do not
qualify mounted dialogs, focus restoration, geometry or light/dark visual appearance. No browser
or native visual qualification, full suite, website work or live-data/provider run was performed.

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
