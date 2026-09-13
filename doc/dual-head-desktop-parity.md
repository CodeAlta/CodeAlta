# Dual-head desktop parity — in development

> **Status: scoped owned Desktop command review, exact-target steering, idle compaction and observed-run cancellation verified, 2026-09-12.** Earlier bounded M2/M3 and persisted-workspace/owned-text-submission/display evidence remains recorded below. This is an implementation acceptance ledger, not documentation of a shipped desktop. Desktop feature parity and full platform qualification remain **pending**. The terminal project/package is `CodeAlta.Tui`, command `altatui`; `CodeAlta` / `alta` remains an explicitly in-development surface with opt-in experimental host operations. A successful managed build or existing unit test does not qualify desktop support.

Source of acceptance criteria: the approved [dual-head desktop plan](../.alta/plans/2026-09-05-dual-head-desktop.md), especially its feature-parity matrix, design §§6.2–6.6, and M0/M7 gates. This document records bounded milestone evidence separately from outstanding desktop acceptance. The coordinator owns the plan checklist and subsequent implementation.

## M4 checkpoint: Send/Abort document-lifetime correction — 2026-09-12

The same sole writer corrected five files; parent audited all source/fixture bodies and integrated.
Send/Abort now use an App-owned helper with immutable intents, original transport waiters, synchronous
exclusion and a combined 256-intent local bound. Exact manual retry/reconciliation survives selection
and remount. Receipt refresh cannot remove live-waiter intent, and accepted/replay requires matching
valid receipt identity. Abort retains its original Send operation/session/key, never a latest run.
Legacy nullable receipt fields and mixed pages remain valid; malformed or uncertain results retain intent.
Valid late epoch invalidation is independent of obsolete UI publication. Separate Send/Abort recovery
results preserve unrelated composer text on Abort-only recovery. Reload does not reconstruct lost
text/keys, and control settlement is not rollback, decision retraction or run termination.

Parent read all 359 fixture lines, the complete helper and App/panel diff. Canonical ordinal comparisons
preserved the C# source outside the one authorized method and the entire shared mutation capability.
Other command helpers/tests, runtime/RPC, permissions and generated contract definitions are unchanged.
The existing test/source guard that required signal-gated invalidation was intentionally replaced with
positive ownership and ordering coverage; unrelated history/display/security guards remain intact.

Initial execution passed 36 frontend tests, but TypeScript failed at two fixture sites: empty-array
assertion narrowing to `never[]` and assignment to generated readonly `next`. The sole writer corrected
only those two test bodies using a separate typed publication array and construction-time page cursor.
Parent re-audited both, with no assertion removal or suppression. **Final verification passes:**
36 frontend tests (`sessionOperations.test.ts`: 10; unchanged Queue/Steering/Compaction/AbortRun: 26),
TypeScript `--noEmit`, and exact C# source-only
`DesktopOwnedSessionSourceTests.Frontend_PreservesLegacyHistoryAndUsesEpochBoundMutations` (1 case).
No final failures/skips or timeouts. Cached targeted and full Release solution builds pass zero
warnings/errors with `--no-restore -p:NeoAstraRestoreFrontendDependencies=false -p:NeoAstraBuildFrontend=false`;
the source test used `--no-build --no-restore`. Generated TS/manifest/schema hashes remain unchanged,
and automatic contract checking passes. Cached Vite production build passes to retained fresh output
`%TEMP%/codealta-parent-send-abort-vite-a28eac5b02514f7286a8adfa83cacf2f`.
Logs: `%TEMP%/codealta-parent-send-abort-*-20260912.log`.

These are in-memory transport fixtures and named-checkout source checks, not mounted UI or new
managed runtime qualification. No provider/tool/native/default-profile, network/install/restore,
full-suite or website execution. Website theme acquisition remains outside the no-network boundary.
Deferred runtime interleavings and durable reauthorization are unchanged. M4–M6 remain open;
this closes a document-lifetime prerequisite, not the admission/observation or interaction milestones.

## M4 checkpoint: volatile Desktop queue/cancel — 2026-09-12

The same sole writer implemented the adapter-only 11-file vertical and a three-file correction;
parent independently audited complete source/fixtures, generated contracts, verified and integrated.
This does **not** complete M4–M6 or durable queue parity. Runtime/owner, permission policy and Display
architecture are unchanged from the prerequisite below.

- Unary `sessions.queue` / `sessions.cancelQueue` route only to existing owner admission. Epoch-first
  validation, canonical GUIDs and positive Int64 attachment strings preserve exact targets; text is
  neither trimmed nor reconstructed. Shared 256-receipt retention, ordinal retry keys and manual
  64-row pages remain. Nullable insertion projection preserves nine-argument receipt construction.
- Queue reservation, insertion and execution/cleanup are distinct. Completion is sampled before
  insertion; settled/refused insertion with pending execution is valid. Generated serialization
  tests include escaped maxima and framing allowances within 208-KiB inbound /448-KiB response limits.
- App-owned immutable queue/cancel intents and synchronous latches survive selection/remount with a
  combined 256-intent bound. Manual exact retry/reconciliation only; late epoch mismatch invalidates
  shared mutation. Busy/draining capture permits an attempt, not authority. No browser persistence,
  retargeting, automatic polling/recovery or new event reader. After reload, manual receipt browsing
  cannot recreate lost local text/keys. Cancellation targets the original operation, not a later run.
- Parent found cancellation-only reconciliation could erase an unrelated new queue draft. The sole
  writer added a regression first, then distinguished `queueRecovered` from `cancellationsRecovered`;
  the panel consumes that production result directly. Parent re-audited all changed bodies.

**Actual verification:** 13 focused Desktop cases (8 new queue RPC methods plus 5 existing pure RPC
regressions) and 31 frontend tests (9 queue tests plus 22 from the four literal-only adapted fixtures)
passed, zero failures/skips. New C# filter: `FullyQualifiedName~CodeAlta.Desktop.Tests.SessionQueueRpcTests`.
Regression filter is the exact OR of these `CodeAlta.Desktop.Tests.DesktopOwnedSessionTests` methods:
`StaleEpoch_RejectsBeforeOwnerAdmission`, `ReceiptPages_BoundWorstCaseGeneratedJson`,
`SteerRpc_RejectsBeforeAdmissionAndBoundsGeneratedJson`,
`CompactRpc_RejectsBeforeAdmissionAndBoundsGeneratedJson`,
`AbortRunRpc_RejectsBeforeAdmissionAndBoundsGeneratedJson`. All five complete bodies were re-read.
Frontend selection: `sessionQueue.test.ts` and `{sessionOperations,sessionSteering,sessionCompaction,sessionAbortRun}.test.ts`.

Cached targeted and full Release solution builds passed zero warnings/errors with `--no-restore
-p:NeoAstraRestoreFrontendDependencies=false -p:NeoAstraBuildFrontend=false`; tests used `--no-build
--no-restore`. Parent-generated TS/manifest/schema and automatic contract check passed; TypeScript
`--noEmit` and cached Vite production build passed. Vite output is retained at
`%TEMP%/codealta-parent-desktop-queue-vite-7e0243ed83484417bde014a1b0629673`.
Logs: `%TEMP%/codealta-parent-desktop-queue-*-20260912.log`. New fixture roots remain retained; no
verification failure or timeout occurred. The prior 137 runtime passes are separate prerequisite
evidence, not this Desktop result.

No native/mounted UI, real-provider/tool/auth, default-profile, network/install/restore, full-suite
or website qualification. Website build remains omitted because configured theme acquisition crosses
the no-network boundary. The two deferred runtime interleavings below remain dynamically uncovered;
durable/restart recovery needs a separately approved trust/reauthorization contract.

## M4 checkpoint: volatile owned deferred execution — 2026-09-12

The same sole writer implemented only the shared-runtime queue prerequisite. Parent independently
audited complete source/fixture bodies, returned corrections to that writer, ran verification and
owns documentation/integration. This does **not** complete Desktop queue parity or M4–M6.

- One outstanding owned text item per session shares bounded receipt capacity and retry keys.
  Immutable session/runtime/attachment/text identity authorizes future execution on that exact
  attachment. Reservation, retained `QueueInsertion` and eventual `Completion` are distinct.
  `queue_accepted` means retained in this host, not persisted or executed. Exact replay preserves
  original identity/results after completion/closure; conflicting fields/kinds are rejected.
- Waiting items do not retain a long-lived attachment use. Setup temporarily protects registration;
  mailbox publication/claim revalidate identity, owned defaults and unavailable/retiring/transitioning
  targets. Shared drain arbitration gives eligible legacy records precedence; failed/null journal
  reads cannot start an independent drainer. One immediate attempt plus existing completion/event
  opportunities replaces polling. No volatile journal row or recovered-record approval authority.
- Captured-handle execution binds actual run permission authority only at dispatch. Default denial,
  TUI policy, AutoApprove, user input and raw-event/Display ownership remain unchanged. Hook-ignoring
  providers receive no owned review; binding failure after dispatch is not preflight rejection or rollback.
- Cancellation targets the original queue operation, never a current/later run. Waiting work settles
  on lifetime closure without acquisition; claimed work retains original sends, callback traversals,
  permission closure and registration joins before source/use release. Cancellation success does not
  prove rollback; noncooperative providers/callbacks may keep shutdown pending indefinitely.

Parent source audit corrected unconditional OCE classification: unsolicited provider cancellation
exceptions now produce bounded `Failed / queue_failed`, while actual operation cancellation and
explicit pre-dispatch retirement refusal remain `queue_cancelled`. The new inert regression covers
original cleanup, no raw failure detail, exact replay and no redispatch. The first cached test build
then failed with three fixture compile errors: summary `ViewState` lacks journal queue data. The
same writer changed only two fixture reads to `Journal.ReadLatestStateAsync` with retained tasks and
non-null assertions; parent re-audited complete bodies. No assertion or production policy was weakened.

Final verification: **137 focused .NET cases pass**, zero failures/skips: **22 new queue cases**
(18 exact-FQN methods), 85 previously audited owned-command/forwarding/run-binding/hub cases, and
30 previously audited actual-Agent lifetime/idle-compaction/permission cases. Regression fixture
files were unchanged from the integrated baseline. Cached targeted and full Release solution builds
pass zero warnings/errors using `--no-restore`, `NeoAstraRestoreFrontendDependencies=false` and
`NeoAstraBuildFrontend=false`. Tests used `--no-build --no-restore`; generated RPC contracts remain
current with no new queue RPC. Logs: `%TEMP%/codealta-parent-owned-queue-*-20260912.log`.

**Explicit deferred coverage:** injected failed/null journal reads in actual drain arbitration, and
retirement forced exactly between setup-use capture and registration/publication. Parent inspected
the concrete store/cache path, null refusal, setup-use protection and publication revalidation, but
found no existing safe deterministic injection/observation seam. These are source-supported paths,
not passing dynamic evidence; revisit before broader queue parity. No timing race tests or production
test hooks were added. Fixture roots are retained, and timeouts never establish termination.

That prerequisite step did not qualify Desktop queue RPC/UI, durable/restart recovery, frontend/native/mounted UI, real-provider/tool/auth,
default-profile, network/install/restore, full-suite or website qualification. Website build remains
omitted because configured theme acquisition crosses the no-network boundary.

## M4 checkpoint: exact observed-run cancellation — 2026-09-12

The same sole writer implemented the optional hub route, existing-only owned runtime capture,
independent AbortRun slot/shared bounded receipts and unary Desktop `sessions.abortRun`. Parent
audited source and complete fixtures before execution, owns documentation and integration, and
returned corrections to that writer without adding another implementation writer.

- Exact session/runtime/attachment/run identity is retained through original provider work, never
  rediscovered, replaced, recaptured or retargeted. Unsupported, stale, transitioning, retiring,
  terminated and draining targets fail closed. Event-derived run state is not cancellation authority.
- Only exact-capability providers bypass hub run/control gates for exact and trusted cancellation.
  Their concurrent cancellation and independent callback control reads avoid a retirement/control-gate
  cycle. Legacy trusted serialization remains. Original references, registration disposal and
  cancellation traversals settle before source/use release; independent shutdown cancellation starts
  before dependent joins. No new event reader, Display change, queue work or approval authority.
- Provider-bound run cancellation supplies permission authority, including matching/null-run requests
  with token None. Stale A cannot cancel B; accepted decisions and unrelated trusted requests survive.
- Exact replay returns the original receipt even after closure. Changed fields/kinds conflict.
  Bounded terminal codes distinguish signalled, not active, target unavailable, unsupported and failed.
  Failure can follow signalling. Success is **cancellation signalled, not run completion**.
- App-owned frozen targets and synchronous latches survive remount and selection changes. Reconcile
  only matching epoch/session/key AbortRun receipts after the original waiter joins; legacy Abort is
  distinct. Late epoch mismatch disables mutations without stale panel publication. Manual only:
  no automatic refresh, retry or retargeting. Generated contracts preserve all six identity strings.

Pre-execution audit corrected two fixture permission-handle type/member errors, joined the original
fixture event iterator and explicit cancellation traversal before source disposal, and bounded the
owner-shutdown failure code with an additional held-admission regression. Initial focused runs passed
all 13 new .NET/five frontend cases, but the broader run exposed one queue-drain fixture race (84 pass,
one fail): earlier detach had released its shared fake send gate. The same writer isolated the two
phases into fresh fixture lifetimes without weakening refusal assertions. Parent re-audited and
reran the new Orchestration filter and full audited regression selection successfully. The failed
root remains at `%TEMP%/CodeAlta-forwarding-2719f9bd355d42208c207127483abcad`.

Final verification: **128 focused .NET cases pass** (85 owned-command/forwarding/run-binding/hub,
30 actual-Agent lifetime/idle-compaction/permission mailbox, 13 Desktop-owned), zero failures/skips.
This includes **13 new .NET cases**, also passed in focused filters. All **64 frontend cases** pass,
including five new pure exact-cancellation cases. Cached targeted and full Release solution builds
pass zero warnings/errors with `--no-restore`, `NeoAstraRestoreFrontendDependencies=false` and
`NeoAstraBuildFrontend=false`; cached TypeScript/Vite passes separately. Normal cached generation
and contract validation expose unary `sessions.abortRun`. Logs: `%TEMP%/codealta-parent-exact-abort-*-20260912.log`.

No native/mounted UI, real-provider/tool/auth, default-profile, network/install/restore, full-suite
or website qualification. Website theme acquisition remains outside the no-network boundary.
Noncooperative/self-awaiting callbacks remain unsupported; timeout is not termination proof.
Queue ownership and remaining M4–M6 requirements are not completed by this command vertical.

## M4 checkpoint: provider run lifetime and owned run binding — 2026-09-12

The optional provider-only `IAgentTargetedAbortProvider` and per-send `AgentRunLifecycle` now
establish original-source cancellation and authoritative owned permission/run binding in the
in-process AgentSession. This historical prerequisite did not itself add an AgentHub/owned AbortRun
command or Desktop RPC/action (integrated separately above), queue implementation or completed M4 milestone.

- Exact admission matches the actual run under provider ownership; caller cancellation only
  cancels admission. Success means the original cancellation traversal settled, not run completion
  or rollback. Callback failure is retained even after signalling. Exact admission closes at
  Closing; trusted/disposal cancellation remains available while Closing is pending.
- One source/worker per run coordinates exact, trusted, caller-forwarded and disposal cancellation.
  Teardown joins hooks, forwarding registration disposal and the original worker before releasing
  the source. New sends and idle compaction cannot overtake successful postprocessing/Closing.
  Logical turn bookkeeping still completes before post-turn usage/compaction, independently of
  retained source authority. Concurrent disposal callers join one cleanup task.
- Started binds the actual run/token before permission-capable work; Closing joins exactly that
  execution even after startup failure. Bound-token cancellation rejects later mailbox approvals,
  including null-run requests with request token None; non-null mismatches deny. Unrelated/later
  executions, trusted registrations, AutoApprove and user input are preserved. Previously accepted
  decisions are not revoked. Unsupported providers retain baseline behavior, not inferred binding.

Parent independently audited source and complete new fixture bodies before execution. Initial
fixture compilation found six retained-task CS4014 errors and one missing namespace import;
the same writer corrected them without serializing cancellation initiation. The first actual-Agent
run passed eight of nine cases and exposed an early normal-success source clear that allowed a
send to overtake held Closing. Parent traced the call; the same writer separated logical turn
completion from source release and strengthened idle-compaction refusal. The original failing
assertion remains, and its failed root is retained (`CodeAlta-run-lifetime-18f54e14211e4720a6ecdf4001a2d185`
under the temp directory).

Final independent verification: **104 focused .NET cases pass**, zero failures/skips: 11 actual-Agent
run-lifetime/idle-compaction cases, 74 owned-command/forwarding/run-binding cases, and 19 permission
mailbox/policy cases. This includes all **13 new cases**, also rerun separately. Cached targeted
and full Release solution builds pass zero warnings/errors, with `--no-restore` and frontend
restore/build disabled in the solution build. Generated RPC contract remains current; no RPC or
frontend source changed. Logs: `%TEMP%/codealta-parent-run-binding-*-20260912.log`.

No frontend tests/build, native/mounted UI, real-provider/tool/auth, default-root, network,
install/restore, full-suite or website qualification is claimed in this slice. Website build is
omitted because its configured theme acquisition crosses the no-network boundary. Public/internal
docs are updated. Noncooperative hooks/callbacks can still prevent shutdown; callbacks must not
await their own send/control/disposal, and a timeout never authorizes abandoning retained work.
M4–M6 remain open.

## M4 checkpoint: owned exact-attachment idle compaction — 2026-09-12

The same sole writer implemented one further command vertical; parent independently audited all
changed production/fixture bodies, executed verification and owns documentation/integration.
`IAgentIdleCompactionProvider` is optional and has no unconditional fallback. The hub run gate
and in-process provider state gate attempt admission without waiting; the provider refuses an
active run and excludes new runs through settled compaction. Null means busy/no work started,
never background compaction. The extracted trusted compaction body preserves its original
instruction/model resolution, compaction call, awaits and result construction.

Owned requests capture epoch/session/runtime/attachment/key, not a run or history revision.
Existing-entry-only mailbox capture validates owned defaults and rejects stale/transitioning/
terminated targets, recorded runs and queue drains; retiring attachments refuse use. It does not
discover/replace a coordinator, clear a pending prompt, emit a premature start event, or create
permission execution. The provider check closes the capture-to-run race. Compaction operates on
context current at provider admission and may use the configured model and persist context.

One compaction slot per session shares bounded receipts with Send/Abort/Steer. Exact replay
returns the same receipt; changed fields/kind conflict. Busy, unsupported, unsuccessful provider
outcome and failure have distinct bounded codes, without arbitrary provider messages. Shutdown
starts independent cancellation and joins retained provider work and cancellation traversals
before releasing attachment/source lifetime. Default permissions and existing trusted operations
remain unchanged. Manual UI targeting, App-owned immutable uncertainty, synchronous double-click
exclusion and Compact-kind receipt reconciliation prevent automatic retry or retargeting. Busy
requires a new explicit action, not replay. Success is settled compaction, not a completed run.

**Parent verification:** all **83 focused .NET cases** passed: 70 owned-command/forwarding cases,
11 Desktop-owned cases and two actual-Agent compaction cases. Twelve are new compaction cases.
They cover actual checkpoint persistence, no start events on busy refusal, occupied hub/provider
gates, a run starting after runtime capture, unsupported/no fallback, failed outcomes, identity
rejection/no mutation, capacity/replay, caller cancellation and shutdown/retirement joins. The
actual Agent fixture uses explicit task-owned roots, cached metadata, precomposed instructions
and a gated text-only scripted executor; summary requests assert no tools. Those actual-Agent roots are
retained for audit. All **59 frontend tests**, TypeScript/Vite, cached targeted and full Release
solution builds passed, with zero build warnings/errors and no restore/acquisition. Generated
TypeScript/manifest expose unary `sessions.compact` and string-valued attachment generation.
Generated worst-escaping input plus framing fits 16 KiB within the existing 208 KiB frame budget.

Pre-execution audit corrected Idle readiness to observe the exact tagged event, not a separately
forwarded Notice whose commit could precede Idle. Complete fixture iterator tasks are joined
before their timeout sources are released; there is no competing or new production reader.
The first eight-case runtime execution had seven passes/one timeout: the shutdown fixture
released compaction but left its fake abort gate held. A one-line `ReleaseAll` correction after
the pending/cancellation assertions allowed full host disposal to join; all eight passed on
rerun, then the regression selections passed. Timeout/failure retention and production shutdown
were not weakened. Logs: `%TEMP%/codealta-parent-idle-compact-*-20260912.log`.

No native/mounted-React, configured-provider/auth/subprocess-tool, default-profile, network,
install/restore, full-suite or website qualification. Website theme acquisition remains outside
the verification boundary. Conditional active-run abort, queue execution/recovery ownership,
broader M4 interactions/recovery and all remaining M5/M6 work are **still open**.

## M4 checkpoint: owned exact-target text steering — 2026-09-12

The same sole implementation writer added `OwnedTextSteerRequest`, bounded owned admission,
existing-entry-only runtime dispatch, generated `sessions.steer`, and App-owned steering
retention/UI. Parent independently audited source and fixtures and owns verification/integration.
M4 command parity, M5 and M6 remain incomplete; this checkpoint does not close any whole milestone.

Steering captures epoch/session/runtime-instance/attachment/non-null run/text/key exactly once.
The mailbox rejects absent/stale/non-owned/transitioning/terminated targets before acquiring
attachment use; retirement refuses acquisition. Dispatch forwards the original `ExpectedRunId`
to the provider boundary and rejects a different returned run. No coordinator creation/replacement,
fallback send/queue, stale run clearing, trusted TUI permission authority or new permission window
is introduced. There is one independent steering slot per session, sharing the bounded owner
receipt limit with Send/Abort. Caller cancellation does not cancel admitted execution; dispatch,
cancellation tasks and forwarding registrations are joined before releasing their dependencies.

Manual current-runtime observation supplies UI target data. Immutable uncertain requests survive
selection/remount, synchronous latches exclude double clicks, and receipt matching checks command
kind. Manual refresh can reconcile a retained request; deliberate retry uses only its exact key,
text and original target. No automatic retry, polling, new event reader or silent retargeting.
Completed steering means input submitted, not run completed. Default denial, explicit command
review opt-in, TUI policy/AutoApprove and existing user-input behavior remain unchanged.

**Independent verification:** cached Orchestration/Desktop test-project and Release solution
builds passed with zero warnings/errors, no restore or frontend acquisition. All **71 focused
.NET cases** passed (62 owned-command/forwarding lifetime cases and 9 Desktop-owned cases),
including seven new runtime steering regressions and two new RPC cases. The race fixture gates
steering after capture, changes the fake active run, and verifies no delivery to the later run.
Tests also cover replay/conflict, independent slot/capacity, caller cancellation, shutdown and
retirement joins, absent/unowned/stale targets, canonical input validation and generated JSON
payload fit within the 208 KiB request budget including framing. All **53 frontend tests** and
**TypeScript/Vite build** passed. Generated TypeScript and manifest include the typed unary steer
method with string-valued attachment generation; no manual generated-file edits.

The first runtime run had three passes/four fixture-readiness failures: a provider Warning was
incorrectly expected in Display StatusMessage. The writer corrected only the fixture to emit a
unique supported inert content marker, await its fully joined observation, then query authority
once. All seven new cases passed on rerun; no production Display changes or polling were added.
Failed fixture roots remain retained. Logs: `%TEMP%/codealta-parent-owned-steer-*-20260912.log`.

No native/mounted-React, configured-provider/auth/subprocess-tool, default-profile, network,
install/restore, full-suite or website qualification. The configured website theme can acquire
remote content, so its build remains outside this verification boundary. Genuine conditional
active-run abort, owned compaction, queue drain/recovery ownership and broader interactions/UI
remain subsequent work, not features supplied by this slice.

## M4 checkpoint: opted-in Desktop command review — 2026-09-12

The explicit `--review-owned-command-permissions` flag now connects the existing owned-send
permission lifetime to Desktop. Complete owned-host/root consent remains mandatory and the
default remains denial. Preparation, unsupported permission payloads and user-input behavior are
unchanged; TUI AutoApprove and the trusted TUI registration/resolve surface remain independent.

`SessionPermissionService.ListOwnedCommandsAsync` and `ResolveOwnedCommandAsync` are separate
owned-only mailbox operations. They filter out trusted TUI attempts and atomically validate the
operation/runtime/attachment plus exact session/run/interaction/attempt handle. Generated unary
`sessionPermissions.list` / `resolve` RPCs add host-epoch validation, complete nontruncating payload
checks and stable failure codes. At most four commands are returned with `HasMore`; resolving and
refreshing reveals the next window. The measured maximum-escaping response is **161,936 bytes**,
**166,032 with framing allowance**, below **196,608 (192 KiB)**. This is not a heap bound.

The selected-session UI provides manual Refresh / Allow once / Deny / Cancel. The App-owned reviewer
retains one original immutable epoch/full handle/clicked decision and its transport waiter across
selection/remount. List reads remain selection-cancellable; the decision wait retains its 8,000-ms
transport deadline independently. Immutable windows and immediate retirement of actionable rows
prevent stale responses and double clicks from replaying a review. Exact original replies are
validated before obsolete presentation fencing; genuine uncertainty and epoch invalidation stay latched.

**Observe retained decision** reports original attribution and pending/result/error state locally,
without either RPC. Pending observation, mounting and live result publication do not acknowledge a
terminal response. Explicit terminal observation plus fresh manual review is required before another
decision can replace the record. Resolved means accepted, not executed; rejected identifies no earlier
decision. Pending-list absence cannot recover the consumed mailbox attempt's outcome. Renderer reload
loses the record and permits only existing explicit pending review; host restart restores no old
authority. This is not retry, list reconciliation, a completion ledger or durable recovery. Closing
presentation does not cancel a permission or revoke an accepted decision.

**Permission-observation prerequisite verification (2026-09-12):** parent audited the complete reviewer,
panel and all 523 fixture lines before execution, including all twelve retained/adapted historical
declarations and six additions. All **18 permission tests + 36 unchanged command-helper regressions**
passed, with zero failures, cancellations or skips; cached TypeScript passed. Fixture cleanup retains
original work/observers, releases gates and cancels selections before dependent joins, clears deadline
timers and permanently fails/retains uncertain work after timeout. This frontend-only slice ran no
managed build/runtime fixture, contract generation, Vite, mounted/native UI, provider/tool, full suite
or website build. No installs/restores; M4–M6, deferred runtime interleavings and durable recovery remain open.

**Earlier command-review vertical verification:** parent audited the complete new literal RPC/CLI/serialization fixtures,
three actual in-memory mailbox tests, pure frontend tests and the existing isolated fake-provider
send fixture before execution. Cached Release solution and targeted builds passed with zero
warnings/errors, without restore or frontend dependency acquisition. All **52 focused .NET cases**
passed: 8 new Desktop RPC cases, 19 mailbox cases, 24 existing Desktop startup/current-source cases
and the actual owned-send public-review regression. The latter checks receipt/runtime/attachment
identity, complete command data and nullable run identity, AllowOnce callback observation, replay
rejection, send completion and an empty pending window using retained inert fixture work.
All **47 frontend tests** and **TypeScript/Vite build** passed. The initial TypeScript build found
four readonly generated DTO mutations in tests; fixture construction was corrected, with a narrow
test-local mutable cast only for intentional adversarial transport mutation. Production compiler
warning policy and generated readonly contracts remain unchanged. Parent reviewed the final diff,
generated RPC contract and conservative reload wording; `git diff --check` passed.

No default-profile, native UI, real-provider/auth/subprocess-tool, network, dependency acquisition,
or full-suite qualification is claimed. The website build remains deferred because its configured
theme acquisition conflicts with this continuation's no-install/no-network boundary. **M4/full
desktop parity remain incomplete**, including notifications, file-change review, asks, broader
observation/effect recovery and real native usability.

## M4 checkpoint: backend owned permission execution/attachment lifetime — 2026-09-11

`CodeAltaHostOptions.ReviewOwnedCommandPermissions` is explicit and defaults **OFF**. At this backend
checkpoint no RPC or Desktop consumer enabled it; the 2026-09-12 continuation above adds that opt-in.
The existing `SessionPermissionService` mailbox owns each actual
receipt operation's canonical session/token association, binds it once to the runtime and acquired
attachment, and supplies the callback through the real `AgentHub.RunAsync` send route. Preparation
and persistent session callbacks deny; user input remains canceled. Retained delegates cannot join
a later send or replacement, including callbacks with null run IDs and `CancellationToken.None`.

Owned requests require exact session/provider identity, complete validated plain-command scalar
payloads and no approval/action/network/amendment extensions. Only Allow Once, Deny and Cancel
are accepted, including through trusted resolution. Bounds are 64 live executions, 128 total
pending/delivering owned attempts, four per execution; UTF-16 limits are 128 identity, 4,096 command
and 1,024 directory/reason units, without truncation. These do not bound legacy TUI pending state,
externally retained callbacks, waiting mailbox callers or process heap.

Send closure joins owner-controlled delivery cleanup before linked-source/handle-use release.
Operation abort, attachment retirement and shutdown close the appropriate admission before dependent
joins; cancellation and abort initiation remain independent, and runtime shutdown still initiates
permission disposal and retirement concurrently. Command disposal does not dispose the shared TUI
permission owner. Closed permissions are **not** provider quiescence, command execution or effect
acknowledgment; noncooperative provider/preparation work can still prevent termination.

Coordinator matching still ignores callbacks. Every owned send (OFF and ON) conservatively rejects
a matching coordinator with different defaults, even independently supplied equivalent denial/input
delegates. Parent review found and required correction of rejection after run/start mutation: owned
preparation and actor admission now preserve the existing pending prompt, attachment and recorded
run, and rejection fails only the owned receipt without publishing misleading run/error events.
The actual-path OFF/ON regression checks full runtime snapshot equality and original event output.
Parent also required the failed owned fixture object to be retained in exception data.

**Independent verification:** all changed actual-path fixtures were audited before the first build:
fresh explicit home/global/project/builtin roots, disabled plugins/empty plugin environment,
registered fake-only providers, throwing probe/turn-executor routes, inert permission decisions,
five-second observers and retained cleanup ownership. Mailbox internal binding tests are owner/
payload/capacity/race evidence, not substitutes for the actual host/send/attachment route.

- Cached targeted Orchestration.Tests and main CodeAlta.Tests Release builds passed with zero
  warnings/errors (`--no-restore`; main build used `BuildProjectReferences=false`). Initial compiler
  failures were corrected narrowly: a lambda `_` parameter/discard collision (CS0029), then three
  deliberately retained task results requiring explicit discards (CS4014). Warning policy unchanged.
- **81 distinct focused cases passed, zero failures/skips:** 54 audited owned-command/forwarding
  cases, 22 mailbox/current-source cases and five previously audited actual-Agent per-send cases.
  The 18 new/extended actual-path permission cases also passed separately before the 54-case run.
  Coverage includes denial defaults, actual per-send approval as inert data, stale delegates after
  return/failure/reuse, held cancellation callbacks, abort/detach/replacement, direct-runtime/host/
  command shutdown, preserved trusted TUI decisions, payload/decision bounds and cancellation races.
- Parent reviewed the final scoped diff and whitespace. Original TUI/plugin/event-reader routes,
  Agent callback selection and provider implementations are unchanged by this backend slice.

No full solution/suite, frontend/site, native/app, subprocess tool, default-root, real-provider,
auth/network, install or restore execution ran for this slice. This is backend-only qualification;
the actual Desktop permission consumer and wider shared-effect/interaction migration still require
separate scope and review. **M4/full desktop parity remain incomplete.**

## M4 checkpoint: explicit per-send permission callback prerequisite — 2026-09-11

`AgentSendOptions.OnPermissionRequest` is now an optional callback override for each send's
in-process `AgentSession` built-in tool definitions. Null keeps the existing session callback.
The runtime does not mutate session options or retain a mutable latest callback; custom tools and
user-input handling are unchanged. Other provider sessions must explicitly support the option.
This is callback selection only: retained tool definitions retain their original callback, not an
automatically canceled lease. This prerequisite alone added no owned execution/attachment binding;
the later backend checkpoint above supplies that binding without RPC or Desktop approval UI.

Parent audited the entire new fixture before executing: actual `AgentSession`, scripted turns,
fresh explicit work/store roots, precomposed instructions, supplied model metadata and disabled
compaction. Both override and fallback exclusively Deny/Cancel real built-in shell requests,
returning before process construction. No subprocess or provider transport is created. Retained
work owns setup/sends/disposal/CTS; unfinished work or failed disposal retains the root.

Independent verification passed all **five actual-session cases** (precedence, null fallback,
sequential sends, retained original tool callback, unchanged session/custom-tool/user-input
options) and **19 existing pure permission/current-source cases**, zero failures/skips. Cached
targeted and solution Release builds passed zero warnings/errors, no restore and frontend
acquisition disabled. No broad suite, native/app/default-root/real-provider/network or website
execution ran. The subsequent backend checkpoint above qualifies a bounded per-execution/attachment
permission lifetime using this association; **M4/full parity remain incomplete**.

## M4 checkpoint: manual selected-session current-runtime observation — 2026-09-11

`SessionRuntimeService.GetCurrentStateAsync` uses existing admitted-work ownership, actor lookup
and a synchronous actor query to copy immutable entry/transition facts. It creates no actor or
coordinator and reads no catalog, journal, provider or Display state. Pre-cancellation prevents
admission; later cancellation stops the caller's wait, not admitted work. Runtime/actor closure
fails explicitly. Runtime instance and attachment generation identify ownership, not revisions.
Missing entry/no recorded run is not idle, completed or proof of provider inactivity. Queue depth
remains unknown; captured configuration is not verified provider-effective, and pending prompt
selection remains distinct from the coordinator's captured prompt.

Owned-only unary `runtimeState.current` validates host epoch and session identity before querying,
refuses malformed/oversized output without truncation, maps failures to stable codes and serializes
attachment generations as decimal strings. The Desktop readout refreshes only on explicit request.
Its App-owned identity/reload latch survives panel remounts; selection cancellation and request
generations suppress late results. Stale host/runtime identity requires reload, not old-epoch retry.
No mutation gating, original-event/plugin/TUI routing or Display behavior changed.

Parent independently reviewed the complete slice and passed:

- Cached Release solution build: zero warnings/errors, no restore, frontend dependency restore/build
  disabled in MSBuild; generated unary/JSON/TypeScript contracts checked.
- **60 focused .NET tests, zero failures/skips**: 34 runtime publisher/projection/actual-state and
  related lifetime cases, 13 Desktop RPC/current-source cases, 13 main current-source guards.
- **27 frontend loader tests**, zero failures/skips; TypeScript/Vite build using cached dependencies.
- Actual generated JSON: **11,185 bytes**, **15,281 with 4 KiB framing**, below **32,768 bytes**.
  This bounds the response payload, not retained/transient runtime or renderer heap usage.

The five new actual-runtime cases use explicit isolated home/global/project/builtin roots, fake
providers, held preparation/send/abort/actor gates and tracked cleanup. They exercise absence,
validation/closure, post-admission cancellation, captured/pending configuration and replacement,
transition/drain, recorded run/Shutdown/detach, and immutable earlier snapshots. RPC test inputs
are literal transport values, not substitutes for those runtime transition tests. Parent's focused
selection is narrower than the child's reported 94-test total; only the 87 cases above are claimed
as independently rerun here.

No native/app or mounted-React qualification, real providers, network/install/restore, default-root
execution, broad suite or website acquisition ran. Current-state observations acknowledge no
effects and provide no replay or atomic Display/history/original-stream handshake. Actual shared
effect/TUI migration, richer recovery and pending interaction parity remain open; **M4 is incomplete**.

Next candidate is opt-in plain command permission review through the existing `SessionPermissionService`
used by TUI, limited to Allow once / Deny / Cancel. Parent confirmed a lifetime prerequisite: callbacks
can occur before attachment publication, abort waits for preparation, host shutdown joins commands
before runtime permissions, and coordinator reuse does not compare callbacks. The later backend
checkpoint above qualifies opt-in cancellation/retirement/reuse binding with isolated fake-provider
tests. No Desktop approval route is enabled; unbound requests remain denied.

## M4 checkpoint: exclusive original-event reader — 2026-09-11

`SessionRuntimeEventPublisher` now admits one original-event reader per instance at first
enumeration. A competing reader throws before touching the channel; rejected/unstarted iterators
cannot release the incumbent's claim. Admission uses the existing short gate only for claim/release,
with no asynchronous read/yield under it. Completion and cancellation do not release a suspended
reader: actual iterator termination/disposal does. Successors consume the remaining buffer without
replay. Pre-canceled admission consumes nothing; after admission, existing channel behavior may
still yield buffered events after cancellation. Abandoned enumerators therefore require disposal.

Parent reviewed the implementation, XML/runtime documentation and all eleven new in-memory
cases. Child reported an observed pre-fix regression: a completed, prebuffered publisher allowed
the second reader to steal the second original object instead of throwing. Parent independently
passed the cached Release solution build (zero warnings/errors) and **53 focused tests, zero
failures/skips**: 26 publisher/projection, 13 current main-source and 14 Desktop current-source/RPC.
Logs: `%TEMP%/codealta-parent-exclusive-reader-*-20260911.log`. No real host/provider/discovery,
native/app launch, installs/restores, broad suite or website acquisition was executed.

The generic channel/drop policy, Display, TUI pump and shell merging, history, reducers and plugin
routes are unchanged. Original references/FIFO hold at this stream boundary, not universally after
the existing TUI adjacent-delta merge. This guard prevents silent competing consumption; it does
not deliver dropped effects, drain the UI queue, join asynchronous plugin work, or establish replay,
an authoritative recovery snapshot or full TUI migration. Display revisions are not raw-event
watermarks, and its baseline must not be combined with an unrevisioned raw stream as a recovery
handshake. Next is a bounded actor-owned active-state/snapshot recommendation using the existing
runtime query/entry/queue owners, rather than expanding evictable Display into authoritative state.

## M4 checkpoint: scoped source-test cleanup — 2026-09-11

All eleven reconstruction-only methods listed in the historical checkpoints below are now
removed, together with their private hashes, compressed data, restoration routines and obsolete
inventories. The six source-test classes retain all **22 current-source guards**, now independent
of historical source transformations. Current command/read-owner joins, scoped discovery,
provider configuration, cancellation, bounded history, epoch-bound mutation and owned-only
composition assertions remain active. No behavior test or production code changed.

`CodeAlta.Desktop.Tests.csproj` no longer compiles the nine unused shared inverse-helper links;
`SourceTestText` remains linked. Shared helper **files** remain unchanged because older main-test
consumers still reference them: `CodeAltaHostLifetimeTests` uses the command-owner gateway;
plugin profile/MCP/statistics helpers use discovery/owner gateways, which retain the Desktop and
forwarding helpers. Historical text literals also remain in `PluginNeutralContractSourceInverse`.
These are residual legacy dependencies, not executable calls to the removed Desktop history method.
This closes the eleven-method deferral, not repository-wide reconstruction cleanup or M4.

Parent independently reviewed the seven-file diff and references and passed the cached Release
solution build (zero warnings/errors) plus **42 tests, zero failures/skips**: 13 main-source,
14 Desktop current-source/display-RPC and 15 in-memory projection cases, using the same focused
class filters as the preceding checkpoints. Child reported two intermediate assertion-anchor
failures corrected without production edits; parent final runs were green. Logs:
`%TEMP%/codealta-parent-source-cleanup-*-20260911.log`. No restore, app/native launch,
real provider/discovery/default-root execution, broad suite or website acquisition ran.
Remaining legacy consumers were not broadly qualified and may need later test rework.

Next is a bounded TUI observation/effect review of the actual pump/coordinator/reducer/plugin
routes. Rich history and original .NET plugin/effect inputs must not be replaced by truncated
display DTOs or reconstructed events; a scoped migration recommendation precedes implementation.

## M4 checkpoint: scoped Desktop selected-session display — 2026-09-11

Parent reviewed and independently verified the sole child's owned-only `SessionDisplayService`,
generated channel/JSON metadata, selected-session store and `LiveSessionPanel`. Default/catalog-only
startup, roots, provider registration and command/permission policy are unchanged. No competing
original event reader or DTO-to-plugin reconstruction was introduced. Malformed UTF-16 stable
identities are omitted/counted without changing the original event instances delivered for effects.

The channel exposes only the selected session, with separate host/projection epochs and decimal
revision strings. Complete replacements/removals, coalesced gaps, absence after eviction, terminal
closure, stale-selection fencing and iterator cancellation are explicit. Known `stale_epoch` requires
UI reload, not reconnect with the old epoch, and survives cleanup errors. The UI is plain text and
labels its retained window as partial; receipts/history remain separate from live state. Published
NeoAstra client inspection confirms opening-only timeout and channel-lifetime cancellation.

Independent parent verification passed:

- Cached Release solution and focused test-project builds: zero warnings/errors, `--no-restore`,
  frontend restore/build disabled in MSBuild. No dependency acquisition or application launch.
- `SessionDisplayRpcTests`, `DesktopOwnedSessionSourceTests`, `DesktopHistorySourceTests`, and
  `DesktopWorkspaceSourceTests`: **14 passed, three explicit historical reconstruction skips**.
- `RuntimeDisplayProjectionTests`: **15 passed**, including malformed identities/original delivery.
- `npm test`: **30 passed**; `npm run build`: TypeScript and Vite passed using cached dependencies.
- Generated serialization budget rerun: **236,725 bytes**, **240,821 with 4 KiB framing allowance**,
  below **262,144 bytes**. This is an item-payload bound, not a process/renderer heap limit.

Logs: `%TEMP%/codealta-parent-desktop-display-*-20260911.log`. Managed fixtures use in-memory
publishers/fake event data and checkout/generated-source reads, not hosts/default roots/providers.
Website build was deliberately not rerun: previous Lunet builds acquired the configured theme,
incompatible with this checkpoint's no-install restriction. Full suites, native/real-provider runs,
complete recovery/discovery, TUI observation/effect migration and interaction parity remain unverified.

Two additional reconstruction-only deferrals supplement the nine below:
`DesktopHistorySourceTests.Boundaries_ReconstructWholeSourcesAndPreserveHistoricalChains` and
`DesktopWorkspaceSourceTests.Boundaries_PreserveTrustAndDocumentReadLimits`. Current history,
workspace and owned-mode guards now inspect current source directly. No behavior test was skipped.
**All eleven were subsequently removed in the scoped cleanup above.** The original requirement
was to resolve them before M4 completion, without adding inverse reconstruction machinery.

## M4 checkpoint: reported plain ToolCall display — 2026-09-12

The existing selected-session Display channel now includes at most two recently updated reported
plain ToolCalls, keyed by exact provider/nullable-run/activity identity within the existing session
container. Immutable rows carry only those bounded identities, a supported reported phase and an
optional 128-unit name prefix/truncation flag. Third identities evict oldest with an explicit decimal
counter. Invalid identities/phases or malformed names omit the report without changing valid rows.
No tool details/message/arguments/results/paths/exception graphs are traversed or copied into Display.
Latest phase may regress; Started is not permission approval/process launch, and no phase establishes
run completion or exactly-once effects. Reconnect/reload recovers retained partial display only.

Parent audited all eight source/fixture files, including all 223 new managed fixture lines and
352 frontend fixture lines, before execution. Historical RPC async bodies and the source-guard/helper
suffix were independently compared unchanged; the old projection fixture remains untouched.
**16 focused .NET tests passed**: six new in-memory activity cases, the expanded generated budget
case, one new synchronous RPC projection case, four unchanged synchronous text/status regressions,
and four current-source/immutable-contract guards. **13 frontend Display tests passed** (nine retained
historical declarations and four additions), plus TypeScript. No failures, cancellations or skips.
Cached Desktop and both targeted test-project Release builds passed with zero warnings/errors and
no restore/frontend acquisition. Generated TypeScript/schema now include the activity DTO and string
counter; the manifest retains the same selected-session channel and automatic contract checks pass.

The combined generated worst-escaping payload is **247,886 bytes**, **251,982 with separate 4-KiB
framing**, below **262,144**. Revised conservative accounting is 247,296 escaped string bytes +8,192
JSON overhead +4,096 framing =259,584. The old 16-KiB overhead inequality was not reused. Existing
text/session/subscriber limits and original-instance event delivery remain unchanged. Payload bounds
are not heap/CPU bounds: malformed-name validation scans the whole supplied name. Managed async
coverage uses one completed/prebuffered original reader with retained bounded joins; frontend cleanup
uses explicit readiness gates and retained original opens/next/returns, with permanent timeout failure.

No actual host/runtime/provider/tool, mounted/native UI, solution-wide build, Vite, full suite or
website execution was added. Historical async projection/RPC suites were not rerun wholesale.
Permission policy, TUI/effects, native/file/ask authority, deferred queue interleavings and durable
recovery remain outside this vertical. **M4–M6 remain open.**

## M4 checkpoint: committed live display foundation — 2026-09-11

`SessionRuntimeService.Display` now commits immutable, bounded live status/text values before
the existing lossy event-delivery attempt. Atomic observation admission captures the initial
snapshot and registers one coalesced wakeup slot; subsequent messages replace the retained
window with an epoch/revision and explicit gap/eviction/truncation indicators. The original
event objects and TUI/plugin-effects route are unchanged; there is no second raw event reader.
Parent review required candidate projection before state/revision/eviction mutation. See
[runtime contracts and limits](runtime.md#committed-live-display-window-m4-foundation-not-complete-m4).

Parent independently passed the full cached Release solution build (zero warnings/errors,
no restore/frontend build), 25 Orchestration display/forwarding cases, 13 current main-source
checks, two Desktop source checks and two existing architecture checks: **42 passes, nine
explicit historical-source skips**. `lunet build` passed (111 files; configured theme acquisition
occurred), and diff/whitespace review passed. Logs are `%TEMP%/codealta-parent-display-*.log`.
Real-runtime fixtures use explicit isolated roots and fake providers; no default profile,
real authentication/provider network, native UI, package installation or full test suite ran.

Following the user's explicit test-refactoring decision, these obsolete historical source
reconstruction methods were temporarily ignored at this checkpoint, then removed in the cleanup
above; they were not behavior/safety tests:

- `OwnedSessionCommandSourceTests`: `Preservation_RestoresAllEightWholeOriginalsAcrossNewlineRepresentations`,
  `Preservation_RejectsMissingDuplicateAndUnrelatedSourceChanges`, `Preservation_NewestPreMapPreservesInheritedChains`.
- `RuntimeEventForwardingSourceTests`: `Preservation_RestoresWholeOriginalsAndRejectsDrift`,
  `Preservation_ClosesReaderMapsAndInheritedGateways`.
- `SessionDiscoveryScopeSourceTests`: `Preservation_RestoresAllNineWholeOriginalsAcrossNewlineRepresentations`,
  `Preservation_RejectsMissingDuplicateAndUnrelatedSourceChanges`, `Preservation_NewestPreMapsPreserveInheritedChains`.
- `DesktopOwnedSessionSourceTests`: `Boundaries_RestoreWholeOriginalsAndHistoricalReaders`.

The before-M4 deferral is now resolved for these methods; shared machinery still referenced by
other legacy tests remains. Current token-separation, discovery/path, resolver and forwarding checks remain
active; new coverage checks immutable DTO closure, actual publication before loss, race-free
observation, coalescing, cancellation, terminal closure and stable text identity.

This is deliberately partial live display storage, not full M4 recovery or an authoritative
active-session query. Running sessions can be evicted; at this checkpoint tool/interaction/plugin/usage
data was not projected (the later bounded ToolCall report extension is recorded above). Bounds cover retained payload/counts, not total process heap or caller-retained
snapshots. The later scoped Desktop channel/UI integration is recorded above; TUI observation/effect
migration, canonical history reconciliation and full interaction/run-state parity remain open.

## M3 follow-up: explicit source-plugin authoring profiles — 2026-09-08

The same sole child implemented Neutral/Terminal source-plugin profiles against `85c25ff2`,
without executing generation, discovery, loading or tests. Reusable APIs default to Neutral;
both actual TUI startup paths explicitly choose Terminal, including noninteractive usage.
Generation and loading share the profile; borrowed prestarted runtimes are not reprofiled.
Reference overrides are validated additive simple names, not replacement lists or MSBuild
expressions. Existing terminal package versions and runtime/native asset exclusions remain.
Deterministic profile/policy/API properties feed the existing four generated-file hashes.
Roots with reported generation failures cannot reach scheduling or cached loading, while
successful requests preserve order, object identity and the existing root comparer.

The normal loader preflights reserved main-artifact identity for both profiles. Neutral also
traverses reachable managed metadata before type discovery, within existing structured Load
diagnostics and conditional unload. Parent review corrected a resolution-domain issue:
dependencies of Default-resolved assemblies must resolve Default-only, not through the
plugin's private resolver. Visitation retains that domain, so a clean private dependency
cannot mask a terminal Default dependency. Only the actual core-library path is skipped;
System-prefixed names and all TPA entries are not platform grants. Missing, malformed or
uninspectable dependencies are explicit failures; executable resolver callbacks are not run.

Parent independently read all new production/fixture/inverse bodies and actual routing.
**14 exact inert and eight exact source methods passed**, using FQN equality and Release
`--no-build --no-restore`. The inert class is
`CodeAlta.Plugins.Tests.PluginAuthoringProfileTests`; the exact methods are:

- `NeutralProfile_ExcludesAllTerminalAuthoringReferences`
- `TerminalProfile_PreservesAllRichAuthoringReferences`
- `ExplicitOverrides_CannotBypassReservedIdentities`
- `InvalidProfile_IsRejectedBeforeRendering`
- `GeneratedFiles_AreIndependentOfPhysicalDllPresence`
- `GeneratedFiles_ProfileStampChangesDeterministically`
- `ManagedNames_NeutralRejectsTerminalFamily`
- `ManagedNames_TerminalRequiresHostIdentity`
- `ReferenceAdmission_RejectsDirectTerminalReference`
- `ReferenceAdmission_RejectsTransitiveTerminalReference`
- `ReferenceAdmission_HandlesCyclesWithoutRepeatedReads`
- `ReferenceAdmission_RejectsUninspectablePrivateDependency`
- `GenerationAdmission_ExcludesFailedRootsAndPreservesRequestIdentity`
- `GenerationAdmission_AllFailedRootsProduceNoBuildRequests`

The source class is `CodeAlta.Tests.PluginAuthoringProfileSourceTests`; exact methods are
`Routes_PropagateExplicitProfileThroughBothTuiEntries`,
`Routes_UseOneProfileForGenerationAndTheExistingLoader`,
`Generation_FailedRootsCannotReachBuildOrCachedLoad`,
`Cache_UsesStampedGeneratedFilesBeforeBothFastPaths`,
`Loader_PreflightsMetadataBeforeDiscoveryWithoutActivation`,
`Loading_ReservesTerminalIdentityWithoutPrivateFallback`,
`Preservation_RestoresCompleteOriginalsAcrossNewlineVariants`, and
`Preservation_ComposesEveryFrozenHistoricalChain`.

Fixtures use literal normalized facts, inert DTOs/AssemblyName values, recording callbacks
and in-memory rendering/XML/JSON/hash operations at actual production cores. They do not
construct generators, runtimes, hosts, ADRs, ALCs or PE readers. Historical source chains and
writerless test-assembly logging retain their prior admission; source/Git/assembly reads are
nonzero I/O. Targeted Plugins.Tests/Main.Tests/Desktop.Tests and full solution Release builds
passed with zero warnings/errors and no restore, with frontend build/dependency targets
disabled. Actual Desktop history→workspace preservation initially exposed a missing whole-
feedback-fixture inverse. The same child corrected only the Workspace frozen-read loop and
added its complete mandatory inverse, preserving the existing reader/adjacency anchor.
Both profile preservation methods and the separately selected exact Desktop methods
`DesktopHistorySourceTests.Boundaries_ReconstructWholeSourcesAndPreserveHistoricalChains`
and `DesktopArchitectureTests.DesktopAssembly_HasNoTerminalOrHostCompositionReferences`
then passed. The final inventory is **12 complete originals, 41 mandatory inverse edits,
36 LF/CRLF/mixed reconstructions and 18 direct profile-source reads**, plus all inherited
historical read maps. Parent independently checked the approved raw-Git hashes; no old
hash/payload was rebased. App remains47,026 bytes, strictly below47,064; guide, attributes,
strict decoder and unrelated work are preserved. A separate audit script stopped when it
mistakenly required a final newline in the intentionally newline-less `.gitattributes`;
the existing policy and file were retained, not normalized. No real profile/credential,
catalog, plugin-source acquisition, frontend restore or native operation was executed.

These are compatibility and source-preservation checks, not real metadata/CLR/loading,
source compilation, native UI, installed-package or default-profile qualification. Profiles
do not prevent arbitrary trusted source from requesting terminal packages during restore,
and inherited direct ALC entry points are not sandboxed. Builtin backend/renderer separation,
desktop plugin hosting and remaining M3–M7 work stay open. Website qualification remains
blocked by the remote-theme/offline constraint. No push, publish or merge is authorized.

## M3 follow-up: remaining neutral contract and package separation — 2026-09-08

The same sole child implemented the dialog/layout/prompt-host closure, fixtures first and
source-only. `PluginDialogRequest` and `IPluginPromptEditorHost` are neutral;
custom content/layout and the native editor anchor live in optional Plugins.Tui.
GitHub uses the actual deferred `PluginTui.PromptEditor` factory, with typed host admission,
exact forwarding, null/exception preservation and no factory-owned disposal. Generic dialog
operations remain unsupported no-ops; no frontend dialog presenter was invented.

Parent independently reviewed production, all new fixture/helper bodies and affected historical
chains. Twenty-three raw-Git anchors and complete context-diff reconstructions passed against
`6f2c1967`. The first targeted build then exposed one unused Graphics import in the GitHub
picker (CS0234). Parent verified its Color is the UI type, supplied a 24th Git anchor and
authorized only removal of that import plus its mandatory inverse. No extra dependency was added.
Final preservation covers **24 complete originals, 34 mandatory edits and 72 newline variants**,
including the moved layout from its real destination and entire historical fixtures.

**16 exact new parameterless tests passed, zero skips/failures.** Under
`CodeAlta.Tests.PluginNeutralContractTests`, the selected methods were:
`NeutralDialogs_PreserveDataAndFactoryDefaults`, `NeutralDialogs_PreserveValidationOrder`,
`TerminalDialogFactory_RejectsNullContent`, `PromptEditorFactory_PreservesMetadataAndDefersAttach`,
`PromptEditorAttach_DeclinesUnsupportedHostWithoutInvocation`, `PromptEditorAttach_ForwardsExactTerminalHostOnce`,
`PromptEditorAttach_PreservesNullAndExceptionWithoutFallback`, `PromptEditorAttach_ReturnsAttachmentWithoutOwningDisposal`,
`NoopDialogs_RemainUnsupportedAndDoNotReadContent`, `NoopDialogs_PreserveValidationBeforeCancellation`,
and `Layout_ResolvesSizesWithoutVisualConstruction`. Under `CodeAlta.Tests.PluginNeutralContractSourceTests`:
`Contracts_IsolateRemainingTerminalTypesInOptionalAssembly`, `Routes_UseTypedPromptAdmissionAndPreserveNativeConsumers`,
`Dependencies_RemoveOnlyUnusedContractPackages`, `Preservation_RestoresCompleteOriginalsAcrossNewlineVariants`,
and `Preservation_ComposesEveryFrozenHistoricalChain`.

Two separately selected desktop checks also passed:
`CodeAlta.Desktop.Tests.DesktopHistorySourceTests.Boundaries_ReconstructWholeSourcesAndPreserveHistoricalChains`
and `CodeAlta.Desktop.Tests.DesktopArchitectureTests.DesktopAssembly_HasNoTerminalOrHostCompositionReferences`.
Thus the actual desktop history/workspace preservation chain ran, not merely a source assertion
that it exists. All selections used exact FQN equality, matching Release outputs and no restore.
Targeted and full solution Release builds passed with zero warnings/errors after the import fix.
NuGet graph refresh used the task-owned source-disabled config and existing cache, with audit and
frontend dependency restoration disabled. Named resolved assets for Abstractions, Plugins and
Orchestration contain no terminal, optional-TUI or NeoAstra libraries. This is resolved build-graph
evidence, not installed-plugin or runtime-load qualification.

The 11 inert methods use DTOs, throwing fake hosts, a fake disposable, geometry values and the
explicitly admitted stateless no-op UI leaf. No native control, editor, plugin/runtime host,
catalog or provider is constructed. Source reads and the existing writerless test logging hooks
remain nonzero I/O. Noop/App/guide/attributes/architecture and source-plugin build/loading policy
remain unchanged. Migration docs and builtin skill are updated. Website, native/rich layout,
default-profile, installed packages and full-suite execution remain excluded. Source-plugin
profiles, builtin backend separation, desktop presentation and the remaining M3–M7 work are open.

## M3 follow-up: optional terminal session-event presentation — 2026-09-08

The same sole child delivered ten scoped code/test files against `bbe583e8`, fixtures first
and without execution. Native event/detail/dynamic factory contracts and visual context now
live in `CodeAlta.Plugins.Tui`. Neutral base records retain Markdown, details, identities,
timestamps, opaque in-process payloads and notifications. Statistics still calculates each
projection once, with unchanged cache, Markdown and native renderer bodies; its optional
presentation dependency is now explicit. This is not backend-only builtin loading.

Parent audited all actual changed sources, both new fixtures and their called historical
source helpers. The actual in-memory projection store and subscription are admitted here
because they own only a lock/dictionary/event references; explicit nonblank Markdown avoids
localization fallback. Inert tests construct no durable store, catalog, coordinator, native
visual, plugin/runtime host or provider. Deferred callbacks capture context then throw literal
errors; one documented null sentinel checks forwarding, not visual consumer behavior.

**16 new exact parameterless cases passed**, including the final child corrections to getter
prefix assertions and nested preservation entry points. Exact methods are the twelve in
`CodeAlta.Tests.PluginSessionEventPresentationTests` (`NeutralProjection_PreservesDataWithoutInvokingNativeFactories`,
`Apply_PrefersDynamicFactoryAndRetainsStaticFallback`, `Apply_PreservesDynamicGetterOrderAndFailure`,
`RefreshDynamic_NullFactoryClearsStaticFallback`, `RefreshDynamic_PreservesGetterOrderAndFailure`,
`ProjectionStore_PreservesUpsertRemoveAndEquality`, `DynamicContent_PreservesNotificationsAndSubscriptionDisposal`,
`CardFactory_DefersInvocationAndPreservesContext`, `DetailFactory_DefersInvocationAndPreservesSectionContext`,
`HeaderFactory_DefersInvocationAndPreservesSectionContext`, `FactoryAbsence_ReturnsNullWithoutConstructingContext`,
`FactoryFailure_PropagatesWithoutFallback`) and four in `CodeAlta.Tests.PluginSessionEventProjectionSourceTests`
(`Contracts_IsolateTerminalFactoriesInOptionalAssembly`, `SharedProjectionAndStatistics_RetainBackendLogic`,
`TuiRouting_PreservesFactoryPrecedenceAndDeferredContexts`, `Preservation_RestoresCompleteOriginalsAndFrozenChains`).
Two additional exact desktop checks passed: `DesktopHistorySourceTests.Boundaries_ReconstructWholeSourcesAndPreserveHistoricalChains`
and `DesktopArchitectureTests.DesktopAssembly_HasNoTerminalOrHostCompositionReferences`.

Twenty-four mandatory inverses reconstruct seven entire originals under LF/CRLF/mixed input.
Parent independently matched raw-Git anchors and reversed context diffs for all seven, then
checked old fixtures, history/workspace chains, architecture, guide and attributes unchanged.
The unchanged keybinding/UI-content/feedback preservation routes also executed. App remains
47,026 bytes; coordinator line anchors281/624 remain unchanged without padding. Offline
source-disabled NuGet graph refresh used cached dependencies; targeted/final solution Release
builds passed with zero warnings/errors and no dependency fetching or application launch.

Migration docs, public developer guide and builtin authoring skill now describe the API break.
Apply still prefers dynamic over static factories; refresh still uses dynamic only, so null
clears the previous native factory. Context construction/invocation remain deferred with no
new native-failure fallback, threading, notification, subscription or lifetime policy.
Actual rich statistics/native rendering, installed source-plugin loading, full suites and
website build remain unqualified. Dialog/prompt-host contracts, remaining assembly references,
builtin backend separation and full M3–M7 parity remain open.

## Latest M4 checkpoint: bounded persisted-event history — 2026-09-08

Session selection now calls generated `workspace.history` using the same retained session
store as catalog browsing. The shared reader frames at most 256 KiB plus five probe bytes,
limits each record to 128 KiB and each page to 100 physical records (including blank/excluded
snapshot records). A partial boundary record is left for the next page. Managed FileStream
read-ahead is disabled only for this route; complete-history APIs remain unchanged.
Versioned session-bound cursors carry decimal-string offsets/length/time stamps. Before/after
checks reject detected changes; lexical containment rejects out-of-copy journal opens after
existing cache/discovery resolution. Earlier existence probes, reparse points, same-stamp
rewrites, external races and catalog/discovery costs remain outside these guarantees.

The desktop replaces one displayed page rather than accumulating rows. Persisted deltas,
completed content, activity phases and notes remain distinct; preview truncation, payload
omission and malformed-final-tail omission are explicit. Unknown/unsupported, oversized or
interior-corrupt records do not silently advance. Failure responses hide infrastructure
exception text. Abort and keyed request state suppress stale publications; restart clears
paging, not the retained catalog snapshot. This is not transcript reconstruction or live parity.

Parent independently audited all 19 new exact C# methods and eight TS tests before execution:
**19 new C# + four existing workspace source regressions + eight TS tests passed**. Inert
streams/literal callbacks exercise mandatory production seams without store/catalog/SQLite,
provider/runtime/native acquisition. Source tests reconstruct six complete `4b63ee2f`
originals under LF/CRLF/mixed input, then the unchanged nine-original workspace chain.
Parent separately matched raw-Git anchors and reconstructed all six using occurrence-checked
context diffs; App remains 47,026 bytes and guide/attributes/older fixture sources unchanged.
Targeted and solution Release builds, generated contract validation, TypeScript checking and
cached Vite production build passed, without restore/fetch/install or application launch.

No actual cache/history access, React mounting/visual lifecycle, native/package/platform,
full-suite or website qualification was added. The site still lacks an approved offline
remote-theme route. Catalog cancellation/teardown and revision limitations remain. M3 closure,
M4 execution/recovery, M5–M7 parity and deferred M2/file-search gates remain open; no M8 exists.

### History identity follow-up

Parent review found that a default provider ID or missing event session ID could produce
null required strings in the generated response despite C# nonnullable annotations. The
new exact inert method `CodeAlta.Desktop.Tests.DesktopHistoryTests.Projection_RejectsMissingRequiredEventIdentities`
failed before the fix (expected validation exception, none thrown). Projection now requires
both identities before creating rows; the existing RPC failure mapping withholds the page
without exposing infrastructure details. Rebuilt targeted Release/contract checking passed;
that method plus the nine previously audited history desktop methods passed10/10. Existing
source inverses remain unchanged. This adds one regression case to the history evidence,
not new native/cache/DOM or full-suite qualification.

## Scope and evidence rules

- Target identities are desktop `CodeAlta` → `alta` and terminal `CodeAlta.Tui` → `altatui`, sharing .NET application/runtime services and durable state. Terminal identity is implemented at M1a; desktop and shared-service extraction remain in progress.
- Desktop uses the approved NeoAstra 0.1.0 NuGet integration, React/TypeScript/Vite and packaged local assets. No HTTP UI server, browser launch, SSR or installed Node runtime; explicit backend OAuth loopback callbacks are a separate preserved feature.
- One application owns the shared data root across both heads. Tab closure/reload must not stop an accepted run; explicit Stop and confirmed application quit have separate lifetimes. JSONL remains authoritative and SQLite remains a rebuildable cache.
- Use normal trusted-library integration. Document/model/plugin payload text remains data: raw Markdown HTML disabled, Mermaid strict defaults, controlled links/attachments and backend tool permissions. No auxiliary rendering view, custom sandbox or nonce infrastructure is part of this acceptance scope.
- **Pending** means implementation and/or acceptance evidence is outstanding. **Pass** requires a recorded scenario, revision, test/fixture and result; native scenarios also require actual OS/RID/engine evidence. **Fail** records the observed defect. **Skipped** is not a pass and requires a reason. Only explicitly terminal-specific diagnostics may be desktop **N/A**, with justification.
- Every parity row requires automated coverage and a manual scenario where UI/native behavior matters. Existing source references describe the current TUI baseline, not proof that a desktop counterpart exists. No placeholder page, mock-browser test, source fallback alone, or successful compilation establishes parity.
- Future evidence should record command/scenario, date, revision, fixture/data root, OS/RID/engine version, duration, pass/fail/skip, artifact location and remaining limitations. Keep tokens, user journals/configuration and other private data out of evidence.

## Baseline recorded on 2026-09-05

### Checkout, configuration and safety

- Branch: `feature/dual-head-desktop`; baseline revision: `7ef14f72e128798e4134036d462eff16158d1f7e`. Tracked files were clean before baseline execution. The approved plan and the pre-existing local configuration/artifact paths remained untracked; the configuration/artifact files were not read, staged, deleted or otherwise changed.
- Inspected root and website `AGENTS.md`, `doc/development-guide.md`, `src/CodeAlta.slnx`, project files, `src/Directory.Build.props`, `src/Directory.Packages.props`, `src/global.json` and `site/config.scriban`. Project files contain no custom `Exec` build targets launching the application. No repository `packages.lock.json` or test runsettings was found in the tracked-file inventory.
- The SDK policy requests .NET 10.0.100 with `latestMinor` roll-forward, no prereleases. Actual runner: Windows 10.0.26200, `win-x64`, SDK **10.0.303**, MSBuild **18.6.14**. Tests use centrally pinned MSTest **4.3.3**, target `net10.0`; warnings are errors. Existing project-specific warning exclusions were not changed.
- Used the already installed `dotnet` and `lunet` commands; no missing tools or new application dependencies were installed. Ordinary established dependency restore was allowed. Lunet fetched its configured `lunet-io/templates` extension during the site build.
- Build/test/site processes used a fresh task-owned temporary directory, with process-local `HOME`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `DOTNET_CLI_HOME`, `TEMP` and `TMP` pointing to its subdirectories. CLI telemetry/first-time-experience flags were disabled. Provider/token/secret environment-variable names and `ALTA_`/`CODEALTA_` overrides were removed from that shell without printing values. These changes did not change the parent host environment.
- `NUGET_PACKAGES` explicitly reused the established `.nuget/packages` dependency cache; the installed Lunet tool was resolved before changing the environment. This is dependency/tool reuse, not an isolated OS account or sandbox. Environment overrides alone do **not** prove Windows `SpecialFolder.UserProfile` or all runtime stores are redirected, so safety relied on the bounded audited test selection below. No CodeAlta executable, real provider, user source plugin or billing-capable session was launched.
- The audited project is `CodeAlta.Plugins.Abstractions.Tests`: its sole test file uses in-memory contracts, `NoopPluginServices`, fake event data and temporary-path descriptors. `NoopPluginStateStore` performs no filesystem reads/writes. This suite does not create the application host or load the checkout's project configuration.

### Commands and results

Durations below are measured command wall time, including command startup and restore/build work, not application performance benchmarks. `<EvidenceRoot>` denotes the task-owned temporary directory described below.

| Working directory | Command | Status / exit | Wall time | Evidence and classification |
| --- | --- | --- | --- | --- |
| `src` | `dotnet build -c Release` | **Pass**, 0 | 17.538 s | Full existing solution built; **0 warnings, 0 errors**. MSBuild reported 17.29 s. No pre-existing compilation failure observed. |
| `src` | `dotnet test CodeAlta.Plugins.Abstractions.Tests/CodeAlta.Plugins.Abstractions.Tests.csproj -c Release --logger "trx;LogFileName=plugin-abstractions.trx" --results-directory <EvidenceRoot>/results` | **Pass**, 0 | 2.140 s | **13 passed, 0 failed, 0 skipped** in this project only; runner test duration 70 ms. Contract regression baseline, not full-solution or desktop coverage. |
| `src` | `dotnet test -c Release` | **Skipped — safety**, not launched | Not measured | Unfiltered runtime/profile isolation was not established; see exclusions below. No full-suite success or failure is claimed. |
| `site` | `lunet build` | **Pass**, 0 | 3.152 s | Production site build: **111 files**, **1,011,576 bytes** written; Lunet reported 2,095.4111 ms. No pre-existing website failure observed. |

Local evidence was retained outside the repository in the original temporary directory under `codealta-m0-baseline-a2726f6b424147a98bf5336411b567c1`: `dotnet-info.log`, `build.log`, `tests.log`, `site.log`, `summary.json` and `results/plugin-abstractions.trx`. These are local, temporary execution artifacts, not committed evidence or a portable CI artifact. Standard ignored build/site outputs were generated; no application, dependency or website source was edited.

### Safety exclusions and baseline limits

- `CodeAlta.Tests/CodeAltaShellControllerTests.cs`, `OpenFolderAsync_TildePath_ExpandsHomeDirectoryBeforeUpsert`, resolves `Environment.SpecialFolder.UserProfile` and creates/deletes a test directory beneath that real home path. It was **not run**. The existing single-instance default-path test also resolves that special folder, although its default-path assertion alone is not a write. The broader suite must not be treated as profile-isolated merely because environment variables were redirected.
- All projects other than `CodeAlta.Plugins.Abstractions.Tests` were excluded from test execution in this bounded baseline: `CodeAlta.Tests`, `CodeAlta.Catalog.Tests`, `CodeAlta.Orchestration.Tests` and `CodeAlta.Plugins.Tests`. Their sources include runtime/host, filesystem, subprocess, provider and source-plugin integration paths. This is a conservative scope/safety omission, **not** a claim that every test in these projects is unsafe. They compiled in the solution build but their runtime results remain unknown.
- Source-plugin tests tagged `RequiresDotNet10FileBuild`, the catalog's existing ignored timing-sensitive traversal test and platform-specific branches were not evaluated by this selection. They must not be reported as passing or as runner-skipped tests within the 13-test result.
- No real-provider login/model request, default-root application smoke, multi-process lock test, cross-head handoff, desktop prototype, NuGet tool pack/install, GUI launch, benchmark or native accessibility test was attempted. There is no failing baseline to fix from the commands executed here; the unfiltered-suite omission is the regression-baseline gap.

### Coordinator regression follow-up (2026-09-05)

A bounded read-only audit identified additional temporary-root/in-memory fixtures. The coordinator ran these against the baseline Release outputs with `dotnet test <project>/<project>.csproj -c Release --no-build --no-restore --filter <filter>`, with TRX output in task-owned temporary `codealta-m0-followup-16cdec454c204bd8bd8ce3ae0e4d6355`. No executable startup, real provider, or default-root lock acquisition was used. The following results supplement, rather than replace, the initial baseline exclusions above:

| Project | Filter (`FullyQualifiedName` unless noted) | Result | Wall time |
| --- | --- | --- | --- |
| `CodeAlta.Tests` | `~CodeAlta.Tests.CodeAltaSingleInstanceGuardTests` | 6 passed | 1.247 s |
| `CodeAlta.Tests` | `=CodeAlta.Tests.CodeAltaHostTests.CreateAsync_HeadlessWithoutPlugins_ConstructsAndDisposesRuntimeServices` OR `=CodeAlta.Tests.CodeAltaHostTests.CreateAsync_ArchivedCurrentProjectIsVisibleInMemoryWithoutSaving` | 2 passed | 1.139 s |
| `CodeAlta.Catalog.Tests` | `~ProjectFilePromptReferenceParserTests` OR `~ProjectFileSearchServiceTests` OR `~SkillCatalogTests` | 15 passed, 1 existing ignored test | 0.869 s |
| `CodeAlta.Orchestration.Tests` | `~BoundedRuntimeEventStreamTests` OR `~OrchestrationMailboxActorTests` OR `~SessionActorRegistryTests` OR `~SessionEventSequencerTests` OR `~SessionPromptDispatchPlannerTests` | 22 passed | 0.874 s |

For command reproduction, expand each operand with `FullyQualifiedName` and join OR operands with literal `|`. All commands exited 0. The ignored catalog case is `ProjectFileSearchSession_PublishesIncrementalUpdatesAndIgnoresStaleRefreshes`. This brings executed baseline coverage to **58 passed, 1 ignored**, not a full-suite result.

The audit narrowed the remaining isolation gaps: `SystemPromptInfrastructureTests.SystemPromptBuilder_CodeFormatsPathsInGeneratedMarkdown` omits explicit user roots and can read global prompts; `AgentInstructionTemplateProvider` passes the real user profile into common-skill discovery even when the host's `GlobalRoot` is temporary. These need explicit root injection before full runtime coverage. The tilde-path test writes only its GUID test directory under `~/codealta-open-tests`, not `~/.alta`; the selected lock tests use temporary locks, and their default-path assertion only compares strings. Fake provider construction, task-owned filesystem fixtures and local-loopback tests are not inherently live-profile or billable operations. No blanket unsafe classification or full-suite isolation claim is made.

## Feature-parity acceptance matrix

### M1a rename checkpoint

All 315 tracked terminal files moved to `src/CodeAlta.Tui` without moving/deleting ignored build residue. The project/package/root namespace is `CodeAlta.Tui`, assembly/tool command `altatui`; the broad `CodeAlta.Tests` assembly, neutral libraries, `CodeAlta` product name, `.alta` paths, in-process `alta` tool and explicit FIGlet resource identity are unchanged. Namespace completion followed the user's cheapest-option decision after most declarations were already changed. Solution/test references, head-aware architecture guards, CLI/update metadata and current-source/install documentation now match the terminal identity, with unreleased-package caveats.

Parent independently ran `dotnet build -c Release --no-restore` from `src` (0 warnings/errors) and the `TuiIdentityTests`, `ArchitectureGuardrailTests`, `CodeAltaCliOptionsTests`, `ProgramThreadGuardTests`, `CodeAltaUpdateCheckerTests`, `CodeAltaUpdateServiceTests`, and `CodeAltaUpdateVisualFactoryTests` filter against matching Release output: **126 passed, none skipped**. Temporary evidence: `codealta-m1a-review-a947ac9503504aeba0222cd686ff38a8`. Child also passed the solution build and `lunet build` (111 files). Full-suite isolation and final installed TUI qualification remain open; the production TUI was not launched against the active profile. Historical baseline references below intentionally describe pre-rename paths.

### M2 editable-text checkpoint

`CodeAlta.Catalog.TextFileCodec` now owns complete Unicode text reads, trusted attached-file lookup and revision-conditional staged saves. Its raw-byte SHA-256 revisions include BOM bytes and distinguish missing from empty files. The TUI editor and attached ask-file review share one instance; their controls, dirty state, watchers, overwrite dialogs and comments remain frontend-owned. The superseded TUI codec is removed. Draft/image stores, UI-state changes and desktop file RPC authorization remain outstanding.

Parent independently passed the Release solution build with `--no-restore` (0 warnings/errors), the Catalog `FullyQualifiedName~CodeAlta.Catalog.Tests.TextFileCodecTests` filter (**22 passed, 1 Unix-only skip**), and the TUI filter joining `FileEditorSessionStateTests`, `FileEditorWorkspaceCoordinatorTests`, `AskQuestionFormViewTests` and `ArchitectureGuardrailTests` with fully qualified `FullyQualifiedName~CodeAlta.Tests.` operands (**124 passed**). Tests used temporary files, fake/in-memory state and repository source reads; no host/provider startup or profile discovery. Evidence: temporary `codealta-m2-text-accept-21bfb433807c42529504e7487612c6c0/{build,catalog-tests,tui-tests}.log`. Parent `lunet build` also passed after public save-behavior documentation updates.

Initial parent architecture coverage caught four changed line numbers in the legacy fire-and-forget allowlist; only those existing locations were updated, without adding allowances. Parent security review also caught temporary Windows files inheriting broader directory grants than their originals. The corrected implementation supplies a protected copy of the original DACL at staging-file creation and fails closed if ACL lookup/application fails. Regression checks verify DACL entries before writing and after handle close, retained target DACL after actual replacement, and no permissive fallback on lookup failure. Holding a `FileShare.Delete` staging handle open was experimentally rejected because Windows `File.Replace` failed with a sharing violation; no dependency was added for the ACL-aware framework API.

Fixtures cover byte-exact BOM/newline/Unicode preservation, same-timestamp external edits, deletion/creation, stale overwrite confirmation, competing saves, original preservation on cancellation/invalid-input/read-only failures, symbolic links, and editor/ask dirty-state retention. Atomic replacement is **not cross-process atomic compare-and-swap**: external content/path/ACL changes can race the checks, identical-byte recreation is indistinguishable, and hard-linked aliases retain their previous file identity. DACL checks are not cross-account impersonation, full security-descriptor/EFS replication or privileged-access qualification. Unix mode behavior is unexecuted here; cancellation coverage is pre-cancelled, not deterministic mid-write fault injection. See [Catalog ownership and limits](catalog-and-config.md#editable-text-files).

### M2 prompt-draft checkpoint

Catalog `PromptDraftStore` now owns legacy global-root draft filenames, session/global/project scope keys, BOM-aware reads, literal UTF-8-without-BOM writes and revision-conditional blank/null deletion. Project drafts remain in the global saved-prompts root; no new schema or image persistence was added. TUI retains bindings, selection, debounce and image lists, with an ordered owned work chain that joins started saves/deletes and advances only acknowledged revisions. Failed operations retain pending intent and report flush failures rather than silently adopting external revisions.

Parent review rejected the first green-test handoff until five issues were fixed: final symlink deletion must not delete its target; actual drafts/editor/ask composition must share one codec gate; conflicts must not expose an acknowledged snapshot; composer clears must preserve text/images if deletion fails and finish before queue/send admission; and draft-flush failure must not skip application-owned service disposal. Normal/dangling-link, repeated clear-failure/admission and dual cleanup-error regressions now cover these paths. Successful clear updates the acknowledged empty UI value without scheduling a second delete. The obsolete draft fire-and-forget exemption was removed and three existing facade allowlist locations shifted; no budgets were increased.

Parent independently passed `dotnet build src/CodeAlta.slnx -c Release --no-restore` (0 warnings/errors), Catalog `PromptDraftStoreTests` plus `TextFileCodecTests` (**30 passed, 1 Unix-only skip**), and the audited TUI selection (**154 passed**). TUI operands were `SessionPromptDraftPersistenceCoordinatorTests`, `CodeAltaAppTests.PromptDraftCoordinator_`, `CodeAltaAppTests.PromptDraftUiCoordinator_`, `ArchitectureGuardrailTests`, `ShellFrontendHostTests`, `SessionPromptAdmissionTests`, `FileEditorSessionStateTests`, `FileEditorWorkspaceCoordinatorTests` and `AskQuestionFormViewTests`, each prefixed by `FullyQualifiedName~CodeAlta.Tests.` and joined with `|`. Test commands used matching Release output with `--no-build --no-restore`. Parent evidence: temporary `codealta-m2-drafts-parent-c292c493de85471a9612896b884af901/{build,catalog-tests,tui-tests}.log`. Parent site build passed; its ordinary configured Lunet theme fetch was observed and is not a dependency/network-free claim.

Tests use unique temporary draft files, deterministic completion barriers, fake lifecycle/admission callbacks and source guards; they do not launch a host/provider/TUI or initialize instruction/MCP discovery. Barriers prove started save/delete joining, later edit/clear ordering and actual disk reopen, not merely an in-memory pending value. The admission helper is exercised with both fake clear failures and the real composer/store conflict path, without runtime/provider initialization.

This is not full application lifetime/recovery acceptance. Synchronous flush seams can block on storage; pending failures remain memory-only, and selection may already have changed in an outer caller before synchronization fails. An earlier failure within `CodeAltaApp.DisposeFrontendAsync` can still skip later frontend cleanup (including drafts), although the owned-service phase is now attempted. Complete confirmation, recovery, best-effort frontend disposal, admission-first lock lifetime and both-head qualification remain later plan work. External writers/path links retain the final-check/commit race, Unix execution is unqualified here, and unsent images remain nondurable.

### M2 prompt-image checkpoint

Catalog now owns `PromptImageAttachmentStore` and neutral payload/reference records, including byte-array copying. TUI retains localized factories, image decoding/clipboard/UI and `PromptSubmission`/AgentInput conversion. Production queue/dispatch coordinators are unchanged: actual dispatch saves before augmentation/submission; copies already saved may remain after downstream failure. Normal UTC shard/filename conventions remain, with atomic create-new and bounded native already-exists retries replacing the existence-check/write race. Whole-batch field validation precedes writes; failed/cancelled saves attempt rollback only of their created files, never earlier collision entries. This adds neither image GC nor durable unsent images.

Parent independently passed the Release solution build (0 warnings/errors), **41 Catalog tests, 1 Unix-only skip**, and **146 TUI tests**. Catalog filter operands were `PromptImageAttachmentStoreTests`, `PromptDraftStoreTests`, `TextFileCodecTests`, each prefixed `FullyQualifiedName~CodeAlta.Catalog.Tests.` and joined with `|`. TUI operands were `PromptImageAttachmentStoreTests`, `SessionPromptQueueCoordinatorTests`, `PromptSessionPortTests`, `LegacyPromptSessionPortTests`, `PluginHostBridgeTests`, `SessionPromptDraftPersistenceCoordinatorTests`, `CodeAltaAppTests.PromptDraftCoordinator_`, `CodeAltaAppTests.PromptDraftUiCoordinator_`, `ArchitectureGuardrailTests`, `ShellFrontendHostTests`, `SessionPromptAdmissionTests`, each prefixed `FullyQualifiedName~CodeAlta.Tests.` and joined with `|`. Tests used Release `--no-build --no-restore`; build used `--no-restore`. Parent site build passed (configured Lunet theme fetch observed).

The parent first included the prior text editor/ask regressions as well. That run **aborted**, despite reporting 162 completed passing cases: an unhandled `InvalidOperationException: Dispatcher is not attached to a running TerminalApp` came from `FileEditorTab.QueueExternalStateRefresh:221` via `OnWatchedFileChanged:357`. The watcher/editor code was unchanged by M2-C; the editor conflict fixture writes externally while no TerminalApp is running. Earlier passing runs did not prove this callback lifetime safe. The bounded deterministic follow-up is recorded below; the smaller 146-case passing selection did not erase this failure. Parent evidence: temporary `codealta-m2-images-parent-642a940a56ce486e92dc6790891fc4d8/{build,catalog-tests,tui-tests}.log` and `codealta-m2-images-tui-14d21e9539b047ffbd89b1e91191a227.log`.

Attachment fixtures use unique temporary roots, a fixed clock and injected write failure/cancellation, fake dispatchers/state, real queue/submission/store helpers and static plugin transformations. They do not initialize host/provider/plugin discovery or native clipboard/browser infrastructure. Dispatch ordering is source-guarded, not full live dispatch qualification. Verification is Windows-only: Unix native collision mapping remains unexecuted. Lexical validation/create-new are not a filesystem sandbox or renderer authorization; external directory/link/file replacement can race writes and best-effort rollback. No crash/power-loss durability guarantee is added.

### M2 editor-watcher verification follow-up

The preceding aborted run led to a bounded production lifecycle fix, not removal of the failing tests. The child first established three deterministic failures (unattached dispatcher posting, detached mutation, disposed mutation) and one mounted-path pass. `FileEditorTab` now publishes an attachment-owned app queue to watcher callbacks; each attachment owns its coalescing flag. Detach/disposal invalidates pending callbacks, rejected posts reset their flag, and attachment queues a disk-state reconciliation. Refresh runs synchronously on the UI queue; its former completed-task fire-and-forget exemption was removed. No architecture budget was increased.

Parent independently passed the clean Release solution build (0 warnings/errors), **7** `FileEditorWorkspaceCoordinatorTests`, then **173** cases in the exact previously crashing broad selection: the preceding checkpoint's 146-case filter plus `FileEditorSessionStateTests`, `FileEditorWorkspaceCoordinatorTests`, and `AskQuestionFormViewTests` (same fully qualified prefix and Release `--no-build --no-restore` options). Parent site build passed with its ordinary configured theme fetch. Evidence: temporary `codealta-watcher-parent-17aacc3a427848a18b369941ed9e19bf/{build,editor-tests,broad-tests}.log`.

Four lifecycle regressions call the watcher entry point directly from background tasks and drain explicit UI ticks. Their harness uses owned temporary files and XenoAtom's disposable in-memory terminal backend, not native terminal/clipboard or CodeAlta host/profile discovery. It follows existing private BeginRun/Tick/EndRun test conventions, which may need maintenance on dependency upgrades. Broader editor save/reload/focus async lifetime and complete shutdown are not qualified by this fix. The earlier failed invocation remains historical evidence, now addressed by deterministic regression coverage rather than an unexplained retry.

### M2 prompt-draft prerequisite-join checkpoint

Accepted against `2141d09c`: `SessionPromptDraftPersistenceCoordinator.PersistAsync` now uses mandatory static `JoinDraftPrerequisitesAsync(previous, waitDelay, isDelayCancellationRequested)`. Arguments are synchronously validated in signature order before the inline local core. The delay factory is invoked once, its original task is retained/awaited, and every terminal delay outcome is followed by an independent await of the supplied predecessor. A null returned task is an explicit InvalidOperationException seam contract. Only delay acquisition/await OCE evaluates the predicate; true suppresses, false retains, and a throwing predicate follows C# filter-false semantics retaining the original OCE. Predecessor OCE is never suppressed. A lone failure retains EDI identity; multiple failures remain direct ordered [delay, previous] references, including nested/repeated errors without flattening/deduplication. Infrastructure context suppression does not force a thread switch; frontend awaits remain unchanged.

This intentionally changes **exceptional ordering**, not just factoring: after terminal delay failure, error storage/logging/local source release wait for previous, and both failures can reach the unchanged outer catch. That catch ordinarily stores/logs rather than propagates; logger/finally failure precedence is not repaired. Pending delay still prevents reaching previous, and pending previous can block completion indefinitely. There is no new owner, cache, latch, retry, admission barrier, cancellation, release, save callback, scheduling or production timeout.

Parent accepted the read-only ownership investigation after complete persistence/UI/store/codec/revision/snapshot/Shell and named composition/command/cleanup boundary review. Phase A then added only the source fixture. Parent audited all1,099 lines/53,845 bytes, four methods,20 fixed reads/18 files,16 uniquely matching routing quotations and the exact accepted CallerFilePath/ordinal helper suffix before execution. Complete275-line persistence matches8,837 raw LF bytes; complete189-line Shell reconstructs6,324 CRLF bytes. The inverse preserves every unrelated byte and static-initialization boundary. Targeted Release build passed clean; exact baseline produced **two intended missing-seam failures at12/37 through RequireOnce1076, two preservation passes, zero skips**, exit1. Frozen source SHA256 `F9B5614B005895F4DFA087A288855541811AF95653452486694DAF62BA710A50`; `%TEMP%/codealta-parent-prompt-prerequisite-before-{build,tests}.log`.

Sole-child Phase B wrote the inert lifetime fixture first, then the exact adapter175–178 and appended XML255–285/core286–336, then exactly three development-guide bullets43–45. Parent independently audited all723 lifetime/helper lines, both tracked diffs, static callsites, task/context containment, complete persistence/guide byte inverses and frozen Phase A without corrections. Lifetime SHA256 `BB6279BF8176F8C970AB6464C8A8F56BC92C1ABDC7B95128FB3DEA882952D6A6`; production `E79370DFDB435E537718188BFE78C5821A5F2EDFB067086DD60077A3BA6A2482`; guide `6201B3563E116F556A914AF8572F964C63C6E2EFBB3DD7DCC506BE197A93D1BF`. The inverse reconstructs original persistence8,837 LF bytes and guide73,955 CRLF bytes. App46,998 bytes, all older owners/source gates and architecture allowances remain unchanged. Child corrected only an XML concept before handoff; two empty-output text-inspection failures had no established cause and were replaced by smaller inspections, not treated as verification passes.

Exact new method inventory (each selected with `FullyQualifiedName=CodeAlta.Tests.<Fixture>.<Method>`, joined by `|`; no class-wide selection):

| Fixture | Method | Cases |
| --- | --- | ---: |
| `PromptDraftPrerequisiteSourceTests` | `PromptDraftPrerequisites_SourceWiring_UsesMandatoryProductionCore` | 1 |
| `PromptDraftPrerequisiteSourceTests` | `PromptDraftPrerequisites_SourceCore_JoinsPreviousAfterDelayFailure` | 1 |
| `PromptDraftPrerequisiteSourceTests` | `PromptDraftPersistence_Source_PreservesAcknowledgementAndRelease` | 1 |
| `PromptDraftPrerequisiteSourceTests` | `PromptDraftPersistence_Source_PreservesFrontendAndStorageRouting` | 1 |
| `PromptDraftPrerequisiteLifetimeTests` | `Core_ValidatesMandatoryArgumentsBeforeCallbacks` | 7 |
| `PromptDraftPrerequisiteLifetimeTests` | `Core_StartsDelayInlineAndJoinsPreviousBeforeCompletion` | 2 |
| `PromptDraftPrerequisiteLifetimeTests` | `Core_DelayFailureStillJoinsPendingPrevious` | 6 |
| `PromptDraftPrerequisiteLifetimeTests` | `Core_DelayFactoryFailureStillJoinsPendingPrevious` | 4 |
| `PromptDraftPrerequisiteLifetimeTests` | `Core_DelayCancellationUsesExistingFilterOnly` | 15 |
| `PromptDraftPrerequisiteLifetimeTests` | `Core_PriorFailurePreservesOriginalIdentity` | 8 |
| `PromptDraftPrerequisiteLifetimeTests` | `Core_MultipleFailuresRemainDirectAndOrdered` | 8 |
| `PromptDraftPrerequisiteLifetimeTests` | `Core_PendingPrerequisitePreventsCompletion` | 2 |
| `PromptDraftPrerequisiteLifetimeTests` | `Core_OnlyJoinsSuppliedOriginalTasks` | 5 |
| `PromptDraftPrerequisiteLifetimeTests` | `Core_BackgroundJoinPreservesFrontendStageSevenContext` | 4 |

The lifetime fixture has only three runtime callsites29/557/564, targeting the mandatory prerequisite and accepted Shell static frontend cores. No concrete coordinator/store/codec/UI/owner/logger/version initialization, CTS/registrations or lifetime source/filesystem reads. Inert task originals/producers/unselected/callback/caller/core/frontend/signal/dispatch/observer/bound/aggregate work is retained before fallible setup. Finally releases gates, starts every independent five-second observer before each aggregate, and takes a second finite snapshot; timeouts permanently fail the fixture even after later completion. Originals are independently observed; faulted-OCE references retain identity, whereas genuine canceled tasks assert token/status without requiring separately materialized exception identity. The context row retains dispatch before callbacks and concurrent observations, queues additional posts, permanently records late-post/callback failures, and restores prior context in body/teardown. It asserts the caller's context after plain-await stage-seven/frontend completion, not an invented eighth stage or forced thread switch.

Parent targeted and solution Release builds passed **zero warnings/errors**. **65 new cases**, **157 unchanged previously audited frontend/editor regressions** (35 exact method operands, raw fixture bytes matched HEAD before selection), and **four separately audited exact source-only architecture checks** passed. Architecture names under `CodeAlta.Tests.ArchitectureGuardrailTests`: `FrontendUiFlows_DoNotUseConfigureAwaitFalseOutsideExplicitBackgroundBoundaries`, `PromptDraftAdmission_ClearsBeforeQueueOrDispatchAcceptance`, `SessionDraftPersistence_UsesMachineSavedPromptsAndDeleteHooks`, and `UiStatePersistence_SaveSeamsReportFailureWithoutShortCircuitingCleanup`. Parent read their full bodies/output-root helpers before execution. Tests used `dotnet test src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-build --no-restore --filter <exact operands>`; builds used `--no-restore`. Logs `%TEMP%/codealta-parent-prompt-prerequisite-after-{build,tests}.log`, `codealta-parent-prompt-prerequisite-{frontend-editor-regressions,architecture,solution-build}.log`; the regression log includes its complete exact filter. Older controller/pump/updater/Deferred/host/outer fixture groups were not rerun in this bounded slice; their prior accepted evidence is historical, not a new pass. Source reads/architecture checkout enumeration/path probes and pre-existing writerless assembly logging are nonzero I/O.

This does **not** close partial QueueFlush construction escaping before a retained-tail join, Observe/CTS setup, adapter allocation/invocation setup, reentrant admission, hidden/withheld descendants, concurrent delete/edit, UI rollback/callback invalidation or complete draft shutdown. Durable replacement/deletion can precede failed result/ack/UI/admission; no rollback, cross-process CAS or linearizable concurrent-delete guarantee. Composition's fixed500ms is not a reproduced invalid delay; these are inert seam cases, not an oversized-delay/BCL/logger/durable-write reproduction. Editor and controller lower-owner gaps, Program/early admission, reminders/metadata and M2–M7 remain open. No broad/concrete prompt/UI/catalog/startup/native tests, installed-tool/archive/other-RID/interactive/accessibility/performance/full-parity qualification were added.

Parent `lunet build` passed111 files/1,029,456 bytes with configured theme download/install observed, not network-free; `%TEMP%/codealta-parent-prompt-prerequisite-site.log`. Public shutdown guidance explains waiting for prior draft work after a delay failure without promising every draft is saved or partial-flush failures are solved. Next bounded work is read-only admission/publication plus partial-flush retention design, not another implementation authorization.

### M2 prompt partial-flush design decision

Read-only follow-up against `8d59ebe7`: parent accepts **deferral, not a production fix or complete prompt ownership**. Parent rechecked the admission/queue/completion source and five frozen checkout hashes after the child confirmed no edits/execution. Observe intent mutation, persistence invocation and returned-task publication are distinct boundaries. QueueFlush publishes each returned original before continuing construction; a later synchronous escape can leave a retained tail unawaited by that caller. Dispose stops only Observe, not later Flush/Load/Has or repeated retries. A captured tail cannot include later publications or recover a branch overwritten through injected same-thread reentry.

No bounded recoverable concrete trigger for the remaining construction escape was established. Ordinary storage/async-body/logger/finally failures become returned-task outcomes; candidate synchronous construction failures principally involve allocation/runtime setup or unestablished throwing cancellation/reentry channels. The concrete sealed store/codec path does not show a callback into admission. This is not proof that the structural hole is impossible, nor installed-BCL correspondence or an executed reproduction.

The proposed captured-tail completion helper remains **unaccepted**. It would require deliberate decisions about captured published work versus partially mutated intent/reentrant branches, delayed construction-error reporting, acknowledgement timing, direct construction/tail error order and explicit evolution of the frozen whole-original gate. A disposal-only sibling wrapper would overstate its ownership. No new source/lifetime fixture, production change or execution was authorized by this review; accepted prerequisite verification above remains the latest executed evidence. Public partial-flush limitations remain accurate. Next bounded read-only investigation is reminder UI/service ownership and retained task/source lifetime, without starting reminders or changing the coordinator's active reminder. M2–M7 remain open.

### M2 editor-workspace cleanup checkpoint

Accepted against `452fad30`: `FileEditorWorkspaceCoordinator.DisposeAsync` forwards the actual picker method group and existing late `FileEditorTab[]` snapshot, covariantly converted to `IAsyncDisposable[]`, to mandatory static `DisposeWorkspaceAsync`. Two callbacks are synchronously validated in signature order; the local Task core starts inline and plain-awaits picker terminal completion, invokes the snapshot once even after picker failure, then independently plain-awaits every returned entry in order. Snapshot throw/null has no fallback enumeration. Null snapshot and null entry are explicit InvalidOperationException seam contracts, not demonstrated dictionary corruption; a null entry does not skip later entries. Lone EDI identity and ordered direct aggregates retain nested/repeated references and OCE without suppression, sorting or deduplication. Successful-path late snapshot timing, App/Shell stage-four routing and frontend context remain unchanged. No cache, retry, latch, admission barrier, parallelism, scheduler, timeout or repeated/concurrent/recursive disposal guarantee.

Parent independently reviewed complete workspace272/picker342/tab751/search-session343-line sources and direct ownership/durable/UI/architecture boundaries. Phase A's full1,176-line/63,586-byte fixture was audited before execution: six methods,25 named reads/22 files, zero runtime production calls, identical accepted CallerFilePath/ordinal helper suffix,39 uniquely matching quoted blocks and complete raw-Git workspace reconstruction. Targeted Release build passed clean; the exact baseline observed **two intended missing-adapter/core failures at14/148 through RequireOnce1153 and four preservation passes**, exit1. Frozen source SHA256 `792902BE71FEF30908CF9EBCE8ACA9406EE68A8EC945212C0442DE9D916F6D6D`. Child corrected only its new-fixture transcriptions/anchors before handoff; parent needed no corrections. Logs: `%TEMP%/codealta-parent-editor-workspace-before-{build,tests}.log`.

The sole child wrote only the new inert lifetime fixture first, workspace adapter/appended XML/core second and three development-guide bullets third, without execution. Parent audited all801 fixture/helper lines and the entire production/guide diff without corrections. Exact adapter72–77, complete original final helper265–269, XML271–296/core297–357/class close358. Workspace inverse matches original raw LF SHA256 `5A53AFDDF4343B6B84DC3DE5B2EC2C747A87C400AEFC8CBCAC31BA8E29A22115` and explicit CRLF checkout `04330D916628D992BCB267B1CC844BAEA12C748095D572621D0C51E012F3469B`; guide inverse removes exactly three bullets. All21 other named sources plus10 frozen references match baseline bytes or explicit LF-to-CRLF conversion; App46,998 bytes stays below strict47,064, all older guards/allowances unchanged. Strict UTF-8/no BOM/per-file endings/final newline/whitespace passed. Lifetime SHA256 `90123CB6096C8990DC53C51679A040F37EDAFCFFA4871F9DFB22CF4369A509D8`; workspace `EA84A794E41DDF2F8652C653B9AC6CD1985C33B99F980071DE79AAEC2E0FA5A1`; guide `7C3C6F2B4AB448B30CB61C9960DC6D21263AA2C34B26DB5897DDE91AD6C1EACE`.

Observed inventory: **15 methods/64 inert behavioral cases,21 methods/70 including source**. Exactly three runtime production callsites: workspace validation26/wrapper628 and accepted Shell static frontend wrapper636; no concrete owners, editors, codec/files/watchers/UI/search services, CTS/registrations, version/logging/monitor initialization or lifetime-fixture source reads. All task-backed ValueTasks are converted once; every original/producer/unselected/callback/caller/workspace/frontend/observer/bound/aggregate task is retained before later fallible setup. Finally releases gates, independently starts each observation before its aggregate and takes a second finite snapshot; timeout history remains permanently failing even after later completion. The context case holds tab0 pending, retains completion and dispatch producers before starting dispatch, and independently observes dispatch/frontend/workspace/completion/original before aggregation. A later tab and pump/controller/drafts assert context identity, not a thread switch. The inert dispatcher uses retained signals/start gate and a signaled queue, not polling; additional posts are retained, late posts/callback exceptions become permanent failures, and callbacks/body/teardown restore prior context. Named source reads, architecture path probes and existing writerless assembly logging remain nonzero I/O.

Targeted and solution Release builds passed **zero warnings/errors**. **70 new cases**, **87 frontend +114 controller/pump +160 updater/Deferred +131 host/outer/Shell =492 accepted regressions**, and **four separately audited exact source-only architecture cases** passed. New filter: `FullyQualifiedName~CodeAlta.Tests.FileEditorWorkspaceCleanupSourceTests.|FullyQualifiedName~CodeAlta.Tests.FileEditorWorkspaceCleanupLifetimeTests.` Regression filters reuse the exact unchanged fixtures in the earlier checkpoints. Architecture operands use exact `FullyQualifiedName=CodeAlta.Tests.ArchitectureGuardrailTests.` names `FrontendUiFlows_DoNotUseConfigureAwaitFalseOutsideExplicitBackgroundBoundaries`, `SessionTabCloseSemantics_AreExplicitlySeparated`, `SessionDraftPersistence_UsesMachineSavedPromptsAndDeleteHooks`, and `UiStatePersistence_SaveSeamsReportFailureWithoutShortCircuitingCleanup`; parent read their complete bodies and output-root helpers, with no initialization hooks or concrete runtime calls. Logs: `%TEMP%/codealta-parent-editor-workspace-after-{build,tests}.log` and `codealta-parent-editor-workspace-{frontend-regressions,controller-pump-regressions,update-deferred-regressions,owner-regressions,architecture,solution-build}.log`. No broad suite or concrete editor/search/controller/startup/native execution.

This is **bounded sibling-failure containment, not complete editor shutdown**. Picker Close can fail before session cleanup/reset and does not stop admission or join query/dismiss/accept/usage/open/update work. Default cache/usage reads complete synchronously in memory; interface-delayed/overlapping acquisition remains possible, not reproduced default behavior. Search sets disposed before cancellation/release and does not join discarded refresh/traversal/ranking or already executing publication callbacks. Tab sets disposed before unsubscription/watcher release without retry/shared completion, and does not join saves/reloads/initial loads/dirty-dialog actions. Attachment identity invalidates stale watcher refreshes, not native callback completion or queue draining. Pending saves can commit durable bytes after disposal; replacement can precede acknowledgement/UI failure, with no rollback. Dirty RequestClose can return false before choice completion; application disposal bypasses dirty prompting/saving and retains dictionaries/projections. Hidden tab/watcher construction, duplicate unpublished-tab cleanup and partial/late dictionary/shell/UI publication remain open. Case-insensitive path-key aliases are not filesystem identity; snapshot allocation/concurrent mutation is not an admission transaction. Detached UI observation can drop continuations and global unobserved-fault reporting can FailFast. No ordinary CTS-release failure, application-defined throwing registration, native watcher join or installed UI-binary correspondence was established. Noncompleting stages can indefinitely block later tabs/frontend/owned services. Controller startup gaps, Program/early admission, other lower owners and M2–M7 remain open; no native/archive/install/other-RID/interactive/accessibility/performance/full-parity qualification was added.

Parent `lunet build` passed (111 files /1,029,069 bytes; configured theme download/install observed, not network-free), `%TEMP%/codealta-parent-editor-workspace-site.log`. Public shutdown guidance describes remaining-editor attempts and pending file load/save/search and durable-change limitations. No implementation/build/test/site blocker. Next bounded read-only investigation: prompt-draft UI/persistence disposal and flush-work ownership; no implementation or expanded execution is authorized by this record.

### M2 shell-controller initialization cleanup checkpoint

Accepted against `e044bdf7`: `CodeAltaShellController.DisposeAsync` snapshots its actual retained initialization task/linked source before cancellation and forwards to mandatory static `DisposeInitializationAsync`. The independent core matches the accepted pump template with only four identifier substitutions, not a pump call/shared abstraction. Four callbacks are synchronously validated in signature order; inline independent stages attempt own cancellation → linked cancellation → supplied original-task join with `ConfigureAwait(false)` → linked-source release → own-source release. Join-only unconditional OCE suppression includes faulted OCE without token/status classification; callback/release OCE and aggregates containing OCE remain failures. Lone EDI identity and ordered direct aggregate references preserve nesting/repeated references. No cache, latch, retry, timeout, scheduling, parallel cleanup or new repeated/concurrent disposal guarantee. App's unchanged plain await preserves the later draft stage's frontend context, not a guaranteed thread switch.

Parent independently accepted Phase A's complete 1,445-line source fixture, 15 literal reads across 13 named files and 34 quoted original blocks before its clean targeted build and observed **two intended missing-adapter/core failures and two preservation passes**. Frozen SHA256: `F9B410F1E354316E7D710E6E3CCBE044EFD31C2679D3A1AB7677170839DA0E35`. The sole child then wrote only the inert lifetime fixture first, controller adapter/appended XML/core, and three development-guide bullets; it performed no execution. Parent audited all 696 behavioral/helper lines and the entire production/docs delta without corrections. The exact 19-line adapter remains335–353, complete original TryMergeRuntimeEvents546–580, appended XML582–619/core620–697; scheduling allowances73/448 and all older production/fixtures/architecture guards remain unchanged. Full inverse reconstruction matches raw Git LF and explicitly converted checkout CRLF. A child line-oriented architecture comparison false mismatch was resolved by raw-stream comparison without edits. Strict UTF-8/no BOM/per-file endings/final newline/whitespace preservation passed.

Inventory and observed results agree: **12 behavioral methods/56 cases, 16 methods/60 cases including frozen source**. Five original outcomes cover success, ordinary fault, cancellation, faulted OCE and nested aggregate. Supplied-original-only rows retain separately gated unselected and callback-produced work; this characterizes the seam, not reproduction of skipped runtime tracks. Runtime production callsites are only controller validation/wrapper and accepted Shell frontend wrapper. Actual CTS/registrations occur only in three inert linked rows, retained before registration with uncanceled parents; the escaping own-cancel aggregate is asserted without an independent external Cancel caller. Every original/producer/unselected/callback/controller/frontend/dispatch task is retained before fallible assertions. Body and teardown independently launch dispatch/frontend/controller observations before aggregation; finally releases gates and observes two finite snapshots. Any five-second observation timeout permanently prevents actual registration/source release, even after later completion. No concrete owners, providers, history, UI, metadata, plugins, monitoring or version/logging initialization; named source reads and existing writerless assembly logging remain nonzero I/O.

Independent targeted and solution Release builds passed **zero warnings/errors**. **60 new source/inert cases**, separately selected **54 pump + 87 frontend + 160 updater/Deferred + 131 host/outer/Shell = 432 accepted regressions**, and **three separately source-audited architecture cases** passed. New filter: `FullyQualifiedName~CodeAlta.Tests.CodeAltaShellControllerInitializationSourceTests.|FullyQualifiedName~CodeAlta.Tests.CodeAltaShellControllerInitializationLifetimeTests.` Regression selections reuse the exact unchanged fixture filters in the corresponding checkpoints. Architecture operands are exact `FullyQualifiedName=CodeAlta.Tests.ArchitectureGuardrailTests.` names `FrontendCoordinators_DoNotAddUntrackedFireAndForgetTasks`, `ShellController_DoesNotReferenceTimelineOrDialogPresentationTypes`, and `CodeAltaApp_DelegatesTerminalLoopLifecycle`. Logs: `%TEMP%/codealta-parent-controller-init-after-{build,tests}.log` and `codealta-parent-controller-init-{pump-regressions,frontend-regressions,update-deferred-regressions,owner-regressions,architecture,solution-build}.log`. No broad suite or concrete controller/startup/runtime-event tests were run.

This is **not complete initialization shutdown**. Unchanged RunInitializationAsync442–468 can skip joining its local startup-tracks task448 when interaction dispatch fails/cancels; direct InitializeAsync406–407 is independent and unretained. Its None-token fallback can outlive cancellation, replace earlier errors or never finish. Provider/session wrapper exception policies remain unchanged. Dispatcher cancellation wraps waits, not queued-action execution. Provider state-reader final Stop can throw from Cancel before its join (a first-stop failure may reach the finally retry); posted actions and Agent provider-service independently retained probe-timeout refresh tasks can outlive controller-token waits. SessionLoad/controller restoration uses None; ShellSessionStateCoordinator discards restoration and SessionHistoryCoordinator separately retains history work in a tab. None is joined by this disposal. No application-defined throwing registration or updater-style released-source token-read race was established in the inspected serialized Start/first-disposal path. Synthetic errors characterize resilience, not ordinary CTS release failures. Joining after cancellation errors can now indefinitely delay releases/drafts/later owners instead of escaping early. External cancellation traversal remains caller-owned; unchanged monitoring can still invoke default FailFast. Local dispatcher source is not verified correspondence to the installed pinned UI binary. Hidden acquisitions, partial frontend/publication, earlier noncompletion, Program/early admission and editor/drafts/reminder/metadata/lower-owner completeness remain open; M2–M7 and native/full-parity qualification remain incomplete.

Parent `lunet build` passed (111 files /1,028,833 bytes; configured theme download/install observed, not network-free), `%TEMP%/codealta-parent-controller-init-site.log`. Public shutdown guidance distinguishes retained shell initialization from independent provider/history/queued-action work. No implementation/build/test/site blocker. Next bounded read-only investigation: file-editor workspace/picker/tab cleanup ownership; no implementation or expanded execution is authorized by this record.

### M2 runtime-event-pump cleanup checkpoint

Accepted against `c10181ad`: RuntimeEventPump snapshots its original task and linked cancellation source and forwards actual operations to mandatory static `DisposePumpAsync`. Four callbacks are validated synchronously before inline cleanup attempts disposal cancellation → pump cancellation → original-task join → linked-source release → disposal-source release. Independent catches preserve a lone original through EDI or multiple ordered direct references without flattening/deduplication. Only the original join retains its unconditional OCE suppression, including a faulted OCE without token/status classification; cancellation/release OCE remains a failure and nested aggregates stay intact. The pending join uses `ConfigureAwait(false)`; App's unchanged plain await retains later frontend context without promising a thread switch. No new owner, start latch, cache, retry, timeout or repeated/concurrent disposal guarantee.

Parent audited all 566 source-fixture lines before the clean Phase-A build and observed **two intended missing-adapter/core failures and one preservation pass**. The fixture remains frozen at SHA256 `94DAF88C49D933909A332D0DB0FFA341370CCD996BCEBC7501F7756C28BFF46E`. The sole child wrote the inert fixture first, then only the pump adapter/appended XML/core and three development bullets, without execution. Parent confirmed idle, corrected the count arithmetic to **11 methods/51 behavioral cases** (not 61; **14/54** with source), and replaced serial context-body observations with independent dispatch/frontend/pump observations started before their aggregate. No added rows or guard changes. Complete 645-line fixture/helper and production/docs audit preceded execution. Exact frozen templates and inverse reconstruction preserve the entire original pump outside the authorized changes, including Start line34, RunAsync and monitoring; all older production/fixtures/architecture remain unchanged. The first read-only inverse comparison mixed raw Git LF with checkout CRLF; explicit verified conversion and raw-Git round-trip passed without source edits. UTF-8/no BOM/per-file endings/final newline/whitespace checks pass.

Independent targeted and solution Release builds passed zero warnings/errors. **54 new source/inert cases**, separately selected **87 frontend**, **160 updater/Deferred**, **131 host/outer/Shell regressions**, and **two exact source-only architecture checks** passed. New filter: `FullyQualifiedName~CodeAlta.Tests.RuntimeEventPumpSourceTests.|FullyQualifiedName~CodeAlta.Tests.RuntimeEventPumpLifetimeTests.` Regression filters reuse the exact unchanged fixture selections in the corresponding checkpoints below. Architecture operands use exact `FullyQualifiedName=CodeAlta.Tests.ArchitectureGuardrailTests.` names `RuntimeEventPump_TargetsRuntimeEventProjectorFacade` and `CodeAltaApp_DelegatesTerminalLoopLifecycle`; parent audited both complete methods and their output-path ancestor/root-existence helpers before execution. Tests use `src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-build --no-restore`; builds use `--no-restore`. Logs: `%TEMP%/codealta-parent-pump-before-{build,tests}.log`, `codealta-parent-pump-after-{build,tests}.log`, `codealta-parent-pump-{frontend-regressions,update-deferred-regressions,owner-regressions,architecture,solution-build}.log`. No Phase-B build/test failure.

Behavioral tests invoke only the mandatory static pump operation and accepted static Shell frontend operation. Independently retained originals/producers/callback/pump/frontend/dispatch tasks, finally-released gates, concurrently started five-second observations and a second finite snapshot contain the fixture. Every timeout remains a failure, including body/context observations; later completion never permits actual resource release. Only three linked-callback rows allocate retained CTS/registrations, with uncanceled parents and no external racing Cancel caller; real release follows successful required observations and actual joins. Nested errors from the fixture's own cancellation traversal retain their direct identity. Source tests perform eight reads across six named files; architecture checks read named files and probe checkout ancestors. These operations and existing writerless assembly logging are nonzero I/O. No concrete pump/stream/projector/controller/frontend/service/provider/auth/plugin/metadata/version-info/monitor/reporter execution, startup, native smoke or broad suite.

The original serialized successful disposal already joined before releasing sources; no updater-style released-source token-read race was established here. Injected cancellation/release errors characterize failure channels, not demonstrated ordinary CTS-release failures. Joining enumeration, queue calls and enumerator cleanup does not drain queued UI events/no-op wakes, stop publishers/runtime/stream/controller/plugins, or join independent external cancellation traversals. Unchanged fault monitoring can still invoke the default fatal reporter/FailFast; these tests characterize cleanup if reached, not process survival. Noncooperative callbacks/workers and earlier noncompleting stages remain unbounded. Hidden acquisitions, partial frontend/publication/Tick-after-failure, Program/early admission, editor/controller/drafts/reminders/metadata/lower-owner completeness and full termination remain open. No new native/archive/install/other-RID/interactive/accessibility/performance/full-parity qualification; M2–M7 remain incomplete.

Parent `lunet build` passed (111 files /1,028,557 bytes; configured theme download/install observed, not network-free), `%TEMP%/codealta-parent-pump-site.log`. Public shutdown guidance describes runtime event-delivery failure containment and preserves queued-update/plugin/fatal-error limitations.

### M2 returned-app frontend cleanup checkpoint

Accepted against `fd2bf93b`: App forwards its existing seven cleanup operations to mandatory static `ShellFrontendHost.DisposeFrontendResourcesAsync`. Projection → reminder UI → view-state persistence → editors → runtime pump → shell controller → prompt drafts retain their successful order, inline entry and plain-await frontend context. Independent catches attempt later siblings after escaped synchronous failures, faults or cancellation; a lone original retains EDI identity, and multiple failures remain ordered direct aggregate entries without suppression or flattening. Persistence still uses `reportStatus: false` and discards the returned result; ordinary persistence errors/conflicts returned as data are not converted into exceptions. This existing adapter stores no resources and adds no owner, caching, retry, scheduling, parallel cleanup or repeated/concurrent disposal guarantee.

Parent audited all 441 lines of the source fixture before the baseline run, which actually produced the expected **two missing-wiring/core failures and one preservation pass** after a clean targeted build. The whole fixture remains frozen at SHA256 `F651116A74B06D7BB07137F21F8B221C0C8152526CDD46D90C4FCDBE635F089F`. After the sole child stopped, parent audited the complete 559-line behavioral fixture (11 methods/84 cases), all helpers, production/docs and compatibility changes before execution. Whole-file inverse reconstruction preserves all unrelated App/Shell bytes, the old Deferred guard, exactly two architecture literals and previous development guidance. The replacement Deferred source guard is frozen at `FBACA2F7CB84947AB049B4A27A814E4B5027E1F836BE77E1077DCB052262226A`; the updater source guard and all existing behavioral fixtures remain unchanged. App stays at 810 CRLF lines /46,998 bytes, below the unchanged strict 47,064-byte budget. Encoding/endings/final-newline/whitespace checks pass.

Independent targeted and solution Release builds passed with zero warnings/errors. **87 new source/inert cases**, **291 accepted updater/Deferred/host/outer/Shell regressions**, and **three separately audited exact architecture checks** passed. New filter: `FullyQualifiedName~CodeAlta.Tests.CodeAltaFrontendCleanupSourceTests.|FullyQualifiedName~CodeAlta.Tests.CodeAltaFrontendCleanupLifetimeTests.` The regression filter is the union of the updater checkpoint's four fixtures and the Deferred checkpoint's three owner/Shell fixtures below. Architecture selection uses exact `FullyQualifiedName=CodeAlta.Tests.ArchitectureGuardrailTests.` operands for `UiStatePersistence_SaveSeamsReportFailureWithoutShortCircuitingCleanup`, `CodeAltaApp_SourceStaysWithinFacadeSizeBudget` and `FrontendCoordinators_DoNotAddUntrackedFireAndForgetTasks`. All tests use `src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-build --no-restore`; builds use `--no-restore`. Logs: `%TEMP%/codealta-parent-frontend-cleanup-before-{build,tests}.log`, `codealta-parent-frontend-cleanup-after-{build,tests}.log`, `codealta-parent-frontend-cleanup-regressions.log`, `codealta-parent-frontend-cleanup-architecture.log`, `codealta-parent-frontend-cleanup-solution-build.log`. No Phase-B build/test failure occurred.

New behavioral tests call only the Shell static core and accepted Deferred core with an inert app; they do not construct App/Shell/Deferred/UI/runtime services or initialize App logging. All started operations, unselected startup tasks and callback-produced disposals are retained before fallible assertions. Finally-released manual gates, concurrently started five-second observations, second finite snapshots, explicit timeout rethrow and retained context dispatch contain the fixture; no CTS/registrations are introduced. Named-source tests read App/Shell/Deferred; the three existing architecture checks additionally read their named source files and enumerate checkout App/Presentation sources, using the existing output-path ancestor lookup. These source operations and writerless assembly logging are nonzero I/O, not startup qualification. No concrete frontend/provider/auth/plugin/metadata/native startup or broad suite ran.

App public disposal, Shell's entire frontend-then-owned-services traversal including late lookup, host/outer ownership, Deferred publication-before-preparation and exclusive app routing, and the updater stage remain unchanged. A noncompleting stage can still prevent later siblings, owned-service disposal and updater cancellation. Hidden constructor acquisitions, partial UI/surface/publication windows, Tick-after-failure, Program/early admission, reminder/metadata/lower-owner completeness and noncooperative UI/plugin/network termination remain open. This completes only returned-app sibling failure containment; no native/archive/install/other-RID/accessibility/performance/full-parity qualification, and M2–M7 remain incomplete. Historical checkpoints below retain their original scope.

Parent `lunet build` passed (111 files /1,028,283 bytes; configured theme download/install observed), `%TEMP%/codealta-parent-frontend-cleanup-site.log`. Public shutdown guidance describes best-effort frontend cleanup without promising completion or a deadline.

### M2 updater cancellation/join checkpoint

Accepted against `093eca97`: the Deferred-owned updater now captures its independent token before scheduling and retains the original check task. Three mandatory internal operations implement one-shot start admission, cached stop-before-core async-only disposal, and cancellation → original-task join → CTS release. Each cleanup stage is attempted after earlier failures; a lone original failure retains EDI identity and multiple failures retain ordered direct references without flattening or cancellation suppression. Deferred awaits the entire updater at its existing stage after exclusive app/startup cleanup and before presenter/startup CTS cleanup, retaining frontend context. App/About remain borrowers. Checking publication, the entire check-result/OCE policy, snapshot/UI generation logic and Program's pre-disposal result sampling are unchanged.

Parent independently audited the complete frozen 534-line source fixture and observed all three expected red assertions before Phase B. After the sole child stopped, parent reviewed the complete 22-method/70-case inert fixture and helpers, production and strengthened Deferred source guard. Exact inverse reconstruction preserved Deferred production and all three adapted existing fixtures; the original Deferred guard reconstructs to SHA256 `1952CDBD26FF7EDEC65D531D2D0B7D77A0A4F5F1FCBDEECBCB3FB846775D0AFA`, with the accepted replacement frozen at `5AF6DFC49C601B50D8F2CE3BE49A93B25C4FF6E9E0D2C67F9AA752D6047121C3`. The updater Phase-A hash remains `1277B1E8355B5F450738259450695A8CC995F7CFA13145567927622A9B5D8709`. UTF-8/no BOM/per-file endings/final newlines and whitespace checks passed.

Independent targeted and solution Release builds passed with zero warnings/errors. **160 updater/Deferred cases** and **131 accepted host/outer/shell regressions** passed. New filter: `FullyQualifiedName~CodeAlta.Tests.CodeAltaUpdateServiceSourceTests.|FullyQualifiedName~CodeAlta.Tests.CodeAltaUpdateServiceLifetimeTests.|FullyQualifiedName~CodeAlta.Tests.DeferredCodeAltaAppSourceTests.|FullyQualifiedName~CodeAlta.Tests.DeferredCodeAltaAppLifetimeTests.` Regression filter is the exact three-fixture expression in the Deferred checkpoint below. Tests used `src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-build --no-restore`; builds used `--no-restore`. Logs: `%TEMP%/codealta-parent-update-before-{build,tests}.log`, `codealta-parent-update-after-{build,tests}.log`, `codealta-parent-update-solution-build.log` and `codealta-parent-update-owner-regressions.log`. No Phase-B build/test failure occurred.

Behavioral coverage uses only the three updater seams and accepted Deferred core, with inert tasks/resources, retained CTS before fallible start callbacks, independently retained original/unselected/callback/caller/disposal tasks, finally-released gates and concurrent five-second observations plus a second finite snapshot. Timeout failures remain failures; actual fixture source release requires successful required observations and joins. The single inert context post is explicitly dispatched and retained before aggregate teardown observation. No concrete updater/checker/About/Deferred/app/service/host/provider/auth/plugin/metadata/version-info startup or broad suite ran. Named source reads and existing writerless assembly logging are nonzero I/O. The async About-fixture compatibility change was not executed.

An earlier noncompleting app/startup stage can still prevent reaching updater cancellation. There is no overall deadline or noncooperative callback/network/transport termination guarantee; the checker's ten-second HTTP timeout is not a total check/shutdown bound. The independent update CTS is not linked to Program/startup, and late results do not produce a new post-join print. Hidden acquisitions, partial frontend/publication windows, interactive plugin startup, Program CTS/exception precedence/early admission, reminders and metadata/lower-owner completeness remain open. No native/archive/install/other-RID/accessibility/performance/full-parity qualification was added; M2–M7 remain incomplete. Earlier checkpoints below describe their historical boundaries, not the latest updater behavior.

Parent `lunet build` passed (111 files /1,028,049 bytes), with configured theme download/install observed; `%TEMP%/codealta-parent-update-site.log`. Public shutdown guidance now describes the same-stage version-check cancellation/join without promising a deadline or automatic installation.

### M2 Deferred startup cancellation/join checkpoint

Accepted against `10adb094`: Deferred now owns a one-shot linked startup CTS allocated before terminal entry, while Terminal/iteration/Tick keep the original token. Five mandatory internal operations implement actual run/start admission, cached stop-before-cancellation disposal, exclusive ownership selection and original-task joining, and precise cancellation classification. Keep the existing `_app = Create` then preparation/root publication order. Disposal requests cancellation, then either disposes the existing app exclusively or joins the actual startup task and disposes its exact returned resource; update, presenter and linked CTS are independently attempted afterward. A lone failure retains identity through EDI; multiple errors retain direct execution-order references without flattening. Only exact live-recorded join failures or canceled tasks carrying the exact requested startup token are suppressed; cleanup errors are never suppressed by that identity.

Parent audited the complete 423-line source fixture before a clean targeted Release build and an exact two-method run: both source tests actually failed on the missing linked CTS/cache fields. The whole source fixture remained frozen through implementation. Parent then audited all 24 behavioral methods/85 cases and helpers, required timeout preservation and concurrent teardown observations, and reconstructed the entire unrelated Deferred source against the baseline. An initial build failed on MSTEST0017 because a prepared expected exception was named `actual`; renaming that local to `startupFailure` preserved assertion semantics and the retry passed. Final independent Release solution build passed with zero warnings/errors; **87 Deferred cases** and **131 accepted host/outer/shell regressions** passed. All production outside Deferred and existing lifetime fixtures remain unchanged. Encoding, final-newline and whitespace checks passed.

Exact new filter: `FullyQualifiedName~CodeAlta.Tests.DeferredCodeAltaAppSourceTests.|FullyQualifiedName~CodeAlta.Tests.DeferredCodeAltaAppLifetimeTests.` Regression filter: `FullyQualifiedName~CodeAlta.Tests.CodeAltaHostLifetimeTests.|FullyQualifiedName~CodeAlta.Tests.CodeAltaOwnedServicesLifetimeTests.|FullyQualifiedName~CodeAlta.Tests.ShellFrontendHostTests.` All tests used the explicit `src/CodeAlta.Tests/CodeAlta.Tests.csproj`, Release `--no-build --no-restore`; builds used `--no-restore`. Logs: `%TEMP%/codealta-parent-deferred-before-{build,tests}.log`, `codealta-parent-deferred-after-{build,build-retry,tests}.log`, `codealta-parent-deferred-solution-build.log` and `codealta-parent-deferred-owner-regressions.log`.

New behavior tests execute only the five static production seams with inert operations/resources, retained original/callback/caller/disposal tasks, finally-released gates and concurrently started five-second cleanup observations. Test CTS disposal requires actual joins; a timeout remains failure, not termination. No concrete Deferred/app/services/host/provider/auth/plugin/metadata startup ran. The source fixture reads seven named checkout files; existing writerless assembly logging remains nonzero I/O. The supported contract is one terminal-thread run completed before initial frontend-context disposal, or never-run disposal; ordinary nonreentrant repeated/concurrent disposal shares its task. Concurrent run/disposal, arbitrary-thread first UI cleanup and recursive disposal are unsupported. `PluginStartupFeedback` calls `Terminal.Live`, so await style does not establish UI-independent composed startup. Hidden constructor/frontend publication-window cleanup, Program CTS/exception precedence/early admission, update/reminder joining and metadata/lower-owner termination remain open. No new native/archive/install/other-RID/accessibility/performance/full parity qualification; M2–M7 remain incomplete.

### M2 acquired-object creation rollback checkpoint

The two existing creation methods now track successfully returned acquisitions and await best-effort rollback before reporting failure. Host rollback attempts runtime → hub → registry → owned plugin → owned logging using its unchanged disposal factory. Outer rollback awaits only the completed host, then metadata and owned logging; a failed inner creation returns no host and finishes its own rollback first. Clean rollback preserves the creation exception's identity through EDI; failed rollback reports exactly two direct references, creation then rollback failure, with nested aggregates intact. Creation cancellation remains canceled only with clean rollback; cleanup failure instead produces a faulted aggregate. Caller cancellation does not skip cleanup. Successful startup evaluation order, original option reads, token/registration/refresh timing, borrowed-plugin policy and durable effects are unchanged.

Parent audited all new source/synthetic cases and full helpers before execution. Raw UTF-8 Git-blob reconstruction against `f3a67b9c` preserves all production outside creation XML/body and new rollback XML/entrypoints/cores, including existing disposal factories, constructors and fields. Both successful acquisition bodies reconstruct under only approved local hoists and try indentation. Both frozen Phase A method hashes match; the prior 21 behavioral methods/attributes and entire helper suffixes remain unchanged. Whole fixtures reconstruct under only reviewed additions and the two narrow old-source-guard transforms. The initial parent indentation transform was too broadly scoped and touched a constructor literal; a creation-handoff-only selector passed without source edits. Strict UTF-8/no BOM, production/development CRLF, fixture LF and final newlines were retained.

After the two observed Phase A missing-slot source failures, parent independently passed the Release solution build with `--no-restore` (zero warnings/errors), **127 lifetime cases** with `FullyQualifiedName~CodeAlta.Tests.CodeAltaHostLifetimeTests.|FullyQualifiedName~CodeAlta.Tests.CodeAltaOwnedServicesLifetimeTests.`, and **four previously audited shell lifecycle cases** with `FullyQualifiedName~CodeAlta.Tests.ShellFrontendHostTests.`. Tests used Release `--no-build --no-restore`. This is 61 new rollback cases plus 66 existing lifetime cases, not additional concrete startup coverage. Logs: `%TEMP%/codealta-parent-creation-rollback-after-{build,tests}.log` and `codealta-parent-creation-rollback-shell-tests.log`; `before-{build,tests}.log` records the red baseline. Website evidence is in the qualification follow-up.

The user-requested close/reopen pause occurred after the child finished: parent confirmed idle and canceled its reminder without auditing, testing or committing; explicit resume restored coordination before this audit. Synthetic fixtures construct no concrete owner/service/manager/client/provider/coordinator and never invoke `CreateAsync`. Required-input checks validate mandatory operations synchronously; independently retained fault/canceled/gated tasks are observed in finally with five-second bounds and all gates released. Source reads and writerless assembly logging are I/O, not concrete lifetime qualification. Hidden constructor resources, unpublished plugin activations, faulty/noncooperative lower-owner cleanup, deferred-startup joining, frontend/refresh/reminder/update lifetime and early admission remain open. No new dependency, friendship, public API or Program/guard/Deferred/desktop/provider/plugin/lower-owner implementation change. No broad suite, archive/install, native/other-RID, accessibility/performance or full parity qualification; M2 and M3–M7 remain incomplete.

### M2 existing-owner disposal checkpoint

`CodeAltaOwnedServices` now retains the existing `CodeAltaHost`, exposing borrowed views instead of disposing a parallel runtime graph. Each owner uses one instance-owned lazy cleanup task. The host attempts runtime → hub → registry → owned plugin → owned logging; the outer owner awaits that entire operation before models.dev and owned logging. Failures and cancellation do not skip later stages. A single exception retains object identity through EDI; multiple direct references aggregate in execution order without flattening. Repeated/concurrent ordinary callers share pending and terminal outcomes without retries. A supplied prestarted plugin remains Program-owned and now shuts down in its existing outer finally, after TUI host/metadata cleanup.

Parent independently reviewed all 23 methods/66 cases and full helpers before execution, including the corrected independently retained gated/fault/canceled operations and finally cleanup. Both original method-with-attribute and complete source-helper SHA256 baselines match. Raw UTF-8 Git-blob reconstruction against `c34cba33` confirmed all production outside approved using/XML/fields/constructor/disposal/return regions unchanged; HostOptions is entirely unchanged. Production/development CRLF and fixture LF, BOM-free UTF-8 and final newlines were preserved. The initial parent hash selector omitted the test attribute; correcting that in-memory boundary matched without edits.

Parent passed the Release solution build with `--no-restore` (zero warnings/errors), **66 lifetime cases** with `FullyQualifiedName~CodeAlta.Tests.CodeAltaHostLifetimeTests.|FullyQualifiedName~CodeAlta.Tests.CodeAltaOwnedServicesLifetimeTests.`, and **four separately re-audited shell lifecycle regressions** with `FullyQualifiedName~CodeAlta.Tests.ShellFrontendHostTests.`. Tests used Release `--no-build --no-restore`. Logs: `%TEMP%/codealta-parent-host-lifetime-after-{build,tests}.log` and `codealta-parent-host-lifetime-shell-tests.log`; prior `before-{build,tests}.log` records both expected source-wiring failures before implementation. No broad previous filter was inferred from counts or rerun. Website evidence is recorded in the qualification follow-up.

The lifetime fixtures construct no concrete owner, manager, client, store, provider or coordinator: they exercise only the two mandatory operation factories with synthetic operations, plus named checkout-source assertions. Existing writerless assembly logging runs; source reads are I/O. The four existing shell tests use recording lifecycle callbacks without `RunAsync`/`GetRoot`. These checks do not establish actual startup/shutdown, plugin/metadata termination, native/installed-tool/other-RID, accessibility/performance or full parity. Same-owner reentrancy is unsupported, synchronous callbacks can block inline, and failed/noncooperative lower owners can leave work active. Partial-construction rollback, deferred-startup cancellation/join, frontend/refresh/reminder/update lifetime and early admission remain open. No dependency, friendship, public API, provider/auth/plugin implementation or Program/guard/desktop change; M2 and M3–M7 remain incomplete.

### M2 configured Codex authentication-test checkpoint

`ConfiguredCodexAuthentication.TestAuthenticationAsync` now backs actual TUI authentication testing with five nonoptional parameters and no new exported type. The exact deferred factory/core/delegate preserve root/store → HTTP/OAuth → original key/source/account → home discovery → manager construction and the original token. Invalid keys do not isolate discovery. The separately accepted once-only provider-resolved context ID callback differs from browser/device raw credential IDs; TUI retains blank fallback, whole-message localization, root policy and the noncancelable adapter. All original unused TUI manager/helpers remain intact.

Parent independently compared all 16 baseline method/attribute/body blocks (49 Hosting/15 TUI), both complete helper suffixes, factory/core/delegate, unrelated coordinator, and prior Hosting content under only the four approved XML changes. Five new public-null cases fail required-object guards before concrete construction; throwing callbacks and nonmatching type are secondary only. Ten source boundaries remain: the existing tenth now verifies Hosting wiring, while the prior nine reconstruct under only the public allowlist/caller changes and the complete tenth adapter/dialog/SourceRoot suffix is unchanged. Parent passed clean Release build, **531 unique Hosting cases** (64 focused/467 regressions), **646 audited TUI**, **two exact defaults**, **132 Catalog plus one Unix-only skip**, and Lunet (**111 files /1,026,739 bytes**, configured theme download/install). The focused 15 TUI cases overlap the 646. Exact filters/results: `%TEMP%/codealta-parent-codex-auth-{focused-hosting,focused-tui,hosting-regression,tui,defaults,catalog}.log`, with `build.log` and `site.log` alongside them. TUI preserves the previous 54 operands plus the new fixture; Catalog retains nine operands and only the three previously audited provider-coordinator methods are selected.

No implementation/build/test blocker remains. Authentication testing can import/refresh/save/delete credentials and fresh cached credentials need no live validation; auth-file readonly is not operation-wide readonly. The website now clarifies this user-visible behavior. Existing arbitrary base-exception display is not guaranteed sanitized. Mandatory synthetic factories, bounded gates/finally joins and source checks do not qualify concrete discovery/storage/protocol, full startup/shutdown, native/installed-tool/other-RID or interactive parity. SR output-directory localization and writerless logging remain. No new disposal, cancellation policy, retry or settlement behavior, dependency, friendship or package reference is introduced. Broader Hosting/M2 and M3–M7 stay open; historical archive evidence predates these auth changes.

### M2 configured Codex browser-login checkpoint

`ConfiguredCodexAuthentication.LoginWithBrowserAsync` now backs the actual TUI browser route. Eight nonoptional parameters carry the original definition/lazy root, mismatch formatter, `Uri` report/opener callbacks, prefix formatter, synchronous prefix/raw-nullable-ID completion and original token. No new exported type or secret-bearing context is added. Construction and account consumption, stored `.AsTask()`, two URI reads and report/open/await order remain preserved. Prefix-first once-only ID capture is the separately accepted browser deviation, not arbitrary-property/task identity/stack/settlement equivalence. TUI keeps presentation, launch exception suppression, root/dialog policies and the original shared helpers (now unused). Account/deletion/device remain intact; one stale device XML ownership sentence was corrected explicitly.

Parent independently preserved all 19 baseline methods/78 cases as 50 Hosting and 28 TUI, both complete corrected recording suffixes, exact factory/delegate/core and remaining TUI. Eight added public-null rows fail required-object guards before concrete construction; nonmatching type is secondary only. All eight earlier source boundaries reconstruct under only approved browser/API/comment changes; the ninth was updated, not supplemented by a tenth. Its full adapter/dialog/opener/reader/SourceRoot suffix remains unchanged. Parent passed clean Release build, **476 unique Hosting cases** (67 focused plus 409 regressions), **631 audited TUI**, **two exact defaults**, and **132 Catalog plus one Unix-only skip**. Final filters and results: `%TEMP%/codealta-parent-codex-browser-{focused-hosting,hosting-regression,tui,defaults,catalog}.log`; build/site logs sit alongside them. TUI retains the exact prior 53 operands plus the browser fixture, including only the three previously audited exact provider-coordinator methods, not that whole class.

An interrupted child stream temporarily left the TUI fixture absent; the same idle-confirmed child recovered it without reset or another writer, and parent verified the full split before tests. Separator-newline errors in child/parent reconstruction scripts were corrected in memory, not in production. No build/test failure or current blocker remains. These are mandatory recording operations and source/type-metadata guards, not concrete authentication, listener, storage or dispatcher-rendering qualification. Public-null tests introduce no pending resources; original fake waits retain finally gate release, bounded nested route/wait joins and disposal afterward. SR output-directory localization and writerless logging remain. Codex responds with plain text after persistence; response/cleanup/presentation failures can follow saving, and an abandoned wait may later persist. No lifetime remediation, extra URI/ID logging, full suite, updated archive/install/native/other-RID or interactive qualification. Authentication testing, broader Hosting/M2 and desktop parity remain open.

### M2 configured Codex device-login checkpoint

`ConfiguredCodexAuthentication.LoginWithDeviceCodeAsync` now backs the actual TUI device route with seven explicit nonoptional parameters and primitive display/completion callbacks. No exported type, credential/protocol DTO or account-metadata reuse was added. The deferred factory/core preserves ordinal selection and root/store → HTTP/OAuth → key/manager construction, original references/token, ignored report-adapter token and unspecified `TimeProvider`. The accepted completion sequence is prefix localization → once-only raw nullable ID capture → synchronous presentation. That read-count deviation is explicit, not arbitrary-property or task identity/stack/settlement equivalence. TUI retains exact display helpers/results/root/dialog behavior; the browser/shared manager helper/credential formatter and account/deletion remain unchanged. Completion failure can follow persistence, and reporting return does not imply dispatcher rendering completion.

Parent accepted 60 pre-move cases in `8a2c3f28`, then independently compared all 17 original method/attribute/body blocks (42 Hosting /18 TUI), both complete recording helpers, exact device factory/core and remaining TUI. Existing Hosting account/deletion content and adapter/dialog were preserved. Seven new required-null public cases were audited before execution; each faults before construction, with nonmatching type only secondary. The existing eighth boundary now verifies exact Hosting signature/nullability/forwarding and ownership; no ninth case or exported type was added. Parent independently preserved all seven preceding guards under only the API allowlist expansion and the full unchanged presentation/browser/adapter/dialog guard suffix and `SourceRoot`.

Parent passed clean Release build, **57 focused Hosting plus 360 regressions (417 unique total)**, **603 audited TUI cases**, **two exact defaults**, **132 Catalog plus one Unix-only skip**, and Lunet **111 files /1,026,191 bytes** (configured theme download/install). Focused filter: `FullyQualifiedName~CodeAlta.Hosting.Tests.ConfiguredCodexDeviceLoginTests.|FullyQualifiedName~CodeAlta.Hosting.Tests.HostingCompositionBoundaryTests.`. Final TUI filter retains the prior exact 52 operands plus `FullyQualifiedName~CodeAlta.Tests.ConfiguredCodexDeviceLoginTests.`; Catalog retains nine. Logs: `%TEMP%/codealta-parent-codex-device-{focused-hosting,hosting-regression,tui,defaults,catalog,build,site}.log`; pre-move evidence uses `before-{build,tests,boundary}.log`. No phase-2 build/test failures or current blockers occurred.

Original fixtures remain synthetic documents/primitives and mandatory instance-owned recording factories; all four pending methods retain finally releases, five-second bounds and expected fault/cancellation joins. No concrete authentication objects, credential/protocol records, resolver execution or discovery were added. SR output-directory localization and writerless logging remain. Source/forwarding evidence does not qualify real authorization, storage, dispatcher rendering, native parity or lifetime. Transient authorization-related display data and personal IDs receive no new logging/persistence. No full suite, new archive/install/native/other-RID or interactive qualification; historical registration archives predate this revision. Broader Codex browser/authentication, Hosting/M2 and desktop parity remain incomplete.

### M2 configured Codex account-metadata checkpoint

`ConfiguredCodexAuthentication.ReadAccountMetadataAsync` now backs actual TUI account lookup with one nullable two-string `CodexAccountMetadata` record and a required synchronous callback. Root/store construction still precedes key/load; missing credentials short-circuit before configured-ID access; a loaded credential uses the original definition's post-load `AccountId`, existing resolver, once-captured raw label and synchronous presentation before completion. The once-only label capture was explicitly accepted in the pre-move design, not claimed equivalent to two arbitrary mutable-property reads. No type/auth-source/enabled/expiry/access-token guard, secret-bearing or lazy credential contract, new logging, cancellation policy, disposal or application owner was added. Missing credential remains failure/zero count; missing ID remains success/zero count. TUI formatter, root policy, dialog cancellation behavior, deletion and unrelated routes/helpers are preserved.

Parent accepted 56 pre-move cases in `418801b3`, then independently compared all 14 original test/attribute/body blocks (37 Hosting /19 TUI), both complete recording helpers, factory/core, entire TUI after approved transformations and existing deletion XML/API/factory/core. Four new public-null cases necessarily fault before production construction; unlike deletion, a nonmatching type cannot serve as a second barrier. A seventh boundary test checks API/nullability metadata and four named checkout sources without executing an operation; all six original guards and `SourceRoot` were independently preserved. The first parent factory selector matched the earlier deletion factory; the account-scoped comparison passed without source changes.

Parent passed clean Release build, **48 focused Hosting plus 319 regressions (367 unique total)**, **585 audited TUI cases** including the 19 retained account cases, **two exact defaults**, **132 Catalog cases plus one Unix-only skip**, and Lunet **111 files /1,026,191 bytes** (configured theme download/install). Focused filter: `FullyQualifiedName~CodeAlta.Hosting.Tests.ConfiguredCodexAccountLookupTests.|FullyQualifiedName~CodeAlta.Hosting.Tests.HostingCompositionBoundaryTests.`. Final TUI filter is the prior exact 51 operands plus `FullyQualifiedName~CodeAlta.Tests.ConfiguredCodexAccountLookupTests.`; Catalog retains nine operands. Filters/results and build/site logs: `%TEMP%/codealta-parent-codex-account-{focused-hosting,hosting-regression,tui,defaults,catalog,build,site}.log`; pre-move evidence uses `before-{build,tests}.log`.

Fixtures use synthetic documents, inert metadata and mandatory recording factories; bounded manual gates, finally releases/joins and CTS cleanup are preserved. SR may read output-directory localization and existing writerless logging remains. Source/metadata checks and fakes do not qualify real credential storage, DPAPI/deserialization, JWT resolution, authentication or provider lifetimes. Trusted backend roots are not renderer grants or a sandbox. No full-suite, new archive/install, native/other-RID or interactive qualification was added; historical registration archives predate this revision. Broader Codex login/authentication, Hosting/M2 and desktop parity remain open.

### M2 configured Codex credential-deletion checkpoint

`CodeAlta.Hosting.ConfiguredCodexAuthentication.DeleteCredentialAsync` now owns only configured credential deletion, with one explicit public method and no result DTO, secret-bearing contract, new dependency or friendship. Actual TUI logout retains its definition guard, root selection and exact localized success projection. The validated core forwards original definition/root references into a mandatory deferred factory: root callback/store validation precedes HTTP/OAuth-client construction, then key read/login-manager validation, then deletion with the original token. It does not snapshot/normalize root/key values, bypass the manager, or add cancellation checks, wrapping, retry, scheduling, context suppression or disposal. The shared TUI login-manager helper remains required by unchanged browser/device routes.

Parent accepted the 38-case pre-move baseline in `8d08550c`, then independently compared all 11 original test/attribute/body blocks, the complete recording helper, core/delegate/deferred nested factory and entire TUI after only approved routing/removal. All original cases moved to Hosting and the obsolete TUI fixture was removed. Four new public required-null cases were audited before execution: each faults before the production factory, with a deliberately nonmatching type and throwing callbacks as additional barriers. A sixth source/API boundary test preserves deferred construction, exact logout projection and the unchanged shared login helper; prior checks remain intact.

Parent passed clean Release build (zero warnings/errors), **48 focused Hosting plus 277 Hosting regressions (325 unique total)**, unchanged **566 audited TUI cases**, **two exact defaults cases**, **132 Catalog cases plus one Unix-only skip**, Lunet (**111 files /1,026,191 bytes**, configured theme download/install) and full-file whitespace checks. New focused filter: `FullyQualifiedName~CodeAlta.Hosting.Tests.ConfiguredCodexCredentialDeletionTests.|FullyQualifiedName~CodeAlta.Hosting.Tests.HostingCompositionBoundaryTests.`. TUI/Catalog retain their prior 51/nine filter operands. Exact filters/results: `%TEMP%/codealta-parent-codex-delete-{focused-hosting,hosting-regression,tui,defaults,catalog}.log`, with build/site alongside them and `before-{build,tests}.log` recording the baseline.

Recording factories use synthetic definitions/inert strings and token-only operations, with two manually released/joined gates and five-second bounds. Their root-consumption behavior proves forwarding only, not real constructor/store validation; exact source comparison/guards do not qualify credential filesystem behavior. Output-directory localization and writerless assembly logging remain. No real credential/protocol objects, manager/client/store/coordinator/runtime/host or discovery execute in these cases. Local deletion is not remote revocation, and constructing an OAuth client is not an OAuth exchange. Existing resource non-disposal, trusted backend roots and Codex login/authentication/account workflows remain unchanged. No full-suite, new archive/install, native/other-RID or application-lifetime qualification was added; registration archives predate this revision. Broader Hosting/M2 and desktop parity remain incomplete.

### M2 configured xAI authentication checkpoint

`CodeAlta.Hosting.ConfiguredXaiAuthentication` now owns four configured operations: browser-PKCE login, separate device login, credential deletion and nullable cached status. All four actual TUI entries call Hosting directly. Existing non-secret provider contracts and four explicit public methods suffice; no new DTOs, dependencies, provider changes or application owner were introduced. TUI retains exact localized prompt/result formatting, ignored callback-token adapters, browser handling, lazy owned-services-root/UserProfile fallback and model-list routing. Required-object/type validation precedes deferred manager construction, then provider key, root callback and API parsing retain their original evaluation order. No early cancellation, context suppression, retry, wrapping or resource-disposal change was added.

Parent accepted the 80-case production-connected baseline in `9b54ef96`, then independently compared all 21 original test/attribute/body blocks, the complete recording-fake/helper suffix, four cores/delegates/options mapper, copied URI helper and all unrelated TUI text. Original cases split into **68 Hosting /12 TUI**; **18 added public required-input cases** fault before production construction, with deliberately nonmatching non-null definitions as a secondary barrier. A fifth source/API boundary check verifies deferred public forwarding and all actual routes; the four existing checks remain. Child's combined audit command exited 1 without diagnostics; split audits and parent comparisons passed without source correction.

Parent independently passed clean Release build (zero warnings/errors), **91 focused Hosting plus 191 Hosting regressions (282 unique total)**, **12 focused TUI cases included in 566 audited TUI cases**, **two exact defaults cases**, **132 Catalog cases plus one Unix-only skip**, Lunet (**111 files /1,026,191 bytes**, configured theme download/install) and whitespace checks. TUI adds only `FullyQualifiedName~CodeAlta.Tests.ConfiguredXaiAuthenticationTests.` to the previous audited filter; Catalog retains nine operands. Exact filters/results: `%TEMP%/codealta-parent-xai-{focused-hosting,focused-tui,hosting-regression,tui,defaults,catalog}.log`, with `build.log` and `site.log` alongside them; `before-{build,tests}.log` records pre-move evidence.

Successful operation fixtures use inert public records, explicit recording factories and manually released gates with finally cleanup/five-second bounds; no concrete manager/client/OAuth/listener/store/coordinator/runtime/host or browser launch occurs. Output-directory localization and writerless assembly logging remain. xAI cached status may report expired credentials as successful: this is not live authentication or a freshness check. Provider-owned PKCE, loopback/CORS, polling, cancellation transformation, browser-success-response-before-persistence and teardown remain source-only observations, not qualified behavior. Manager/HttpClient non-disposal and trusted backend root/URI semantics remain unchanged, not sandbox or lifetime guarantees. No new archive/install/native/full-suite qualification; earlier registration archives predate this revision. Broader Hosting/M2 and desktop parity remain incomplete.

### M2 configured Copilot authentication checkpoint

`CodeAlta.Hosting.ConfiguredCopilotAuthentication` now owns configured device-flow login, credential deletion and nullable cached-status orchestration through exactly three explicit public methods, using existing non-secret provider contracts. All four TUI routes call Hosting; both UI login choices still use the same device flow. TUI keeps localized prompt/result formatting, ignored callback-token adapters, browser handling and lazy owned-services-root/UserProfile fallback. The moved validated cores defer concrete manager construction until after required arguments and ordinal provider-type validation, then evaluate key, root, enterprise and absolute API URI options in the original order. Cancellation, callback/exception propagation and existing manager/HttpClient non-disposal remain unchanged. No DTO, dependency, provider-package, config/refresh, Codex/xAI or application-lifetime changes were made.

Parent accepted pre-move characterization in `4ec83031` (68 passing cases), then independently compared all 24 original method/attribute/body blocks, the complete fake/helper suffix, the three cores/delegates/options mapper and unrelated TUI content. Original cases split into **58 Hosting /10 TUI**; **13 added Hosting required-input cases** fault before production construction. Three previous boundary checks remain, with a fourth checking deferred public forwarding and actual TUI wiring. The fixtures use inert records, recording delegates and manually released bounded asynchronous gates, not concrete managers/clients/stores/coordinators/hosts. Presentation may read output-directory `SR.yml`; writerless assembly logging remains active.

Parent independently passed clean Release build (zero warnings/errors), **75 focused Hosting checks plus 120 Hosting regressions (195 unique total)**, **10 focused TUI cases included in 554 audited TUI cases**, **two exact defaults cases**, **132 Catalog cases plus one Unix-only skip**, Lunet (**111 files /1,026,191 bytes**, configured theme download/install) and whitespace checks. TUI adds only `FullyQualifiedName~CodeAlta.Tests.ConfiguredCopilotAuthenticationTests.` to the prior audited filter; Catalog keeps the same nine operands. Exact filters/results are in `%TEMP%/codealta-parent-copilot-{focused-hosting,focused-tui,hosting-regression,tui,defaults,catalog}.log`, with build/site in the corresponding `build.log` and `site.log`.

This qualifies wrapper preservation, not real authentication, credential I/O, provider/browser/host startup or full coordinator execution. Cached status is nullable and non-live; stored credentials may be unusable within the existing five-minute refresh skew. Relative/invalid API URLs still become null and any absolute URI remains accepted; backend roots/options are trusted inputs, not renderer grants or a sandbox. Existing concrete resource non-disposal and broader metadata/startup/shutdown ownership remain open. No new archive/install/native/full-suite qualification was performed; registration-era archive evidence predates these changes. Broader Hosting/M2 and desktop parity remain incomplete.

### M2 cached/temporary provider inspection checkpoint

`CodeAlta.Hosting.ConfiguredProviderInspection` now owns cached test/model-list policy and the directly owned temporary-runtime probe/sort/disposal path. Four explicit public methods and two neutral readonly result records are the only new exported API; mandatory internal call-scoped factory overloads allow fake-only characterization. Actual TUI methods retain key-only dictionary lookup/comparer, lazy original root selection, borrowed metadata, static localized message factories and result mapping. Superseded TUI cores/factory/sorting/policy are removed; auth model-list wrappers still use `TestProviderAsync`. No references, dependencies, registry or application owner were added.

Parent accepted and committed the production-connected pre-move characterization in `5e83d87a`: clean Release build and **74 new cases plus four existing checks**. After the move, parent independently confirmed identical core bodies after method/result renaming, identical factory/sorting, all 17 original test methods/attributes/bodies unchanged, and the complete fake-helper suffix unchanged after explicit API/count-getter mapping. An initial comparison misdecoded UTF-8 punctuation from Git; setting the command's UTF-8 decoding fixed the comparison without source changes. The 74 original cases split into **11 TUI and 63 Hosting**; **26 added Hosting cases** cover neutral cached policy/formatter failures and required public inputs. Parent audited all new cases before execution. Public uncached validation rows fail before builder forwarding; all valid fallback execution supplies in-memory fake factories. Pending operations use manually released completion sources, finally cleanup and bounded waits.

Parent independently passed clean Release build (zero warnings/errors), **92 focused Hosting and 15 focused TUI checks**, the unchanged **31 registration and two exact defaults cases**, expanded **544 audited TUI cases**, **132 Catalog cases plus one Unix-only skip**, Lunet (111 files; configured theme download/install) and diff checks. The TUI baseline adds only the 11-case `ProviderInspectionWorkflowTests` and three exact existing `ProviderFrontendCoordinatorTests` methods to the prior 45 operands; Catalog retains nine operands. Parent logs: `%TEMP%/codealta-parent-provider-inspection-{build,focused-hosting,focused-tui,registration,defaults,tui,catalog,site}.log`; the corresponding `before-{build,tests}.log` retains pre-move evidence. Test logs include exact filters. API/source guardrails verify forwarding and both disposal paths without raising budgets or adding exemptions.

Preserved quirks are not fixes: edited/disabled same-key settings can reuse active state; test handles Ready/Probing/Failed/Unsupported, whereas listing reuses Ready only; cached lists bypass sorting and remain borrowed/mutable; any returned probe, including non-Ready or empty, becomes workflow success. There is no explicit Start/Stop or early cancellation check. Success formatting runs before awaited disposal, and disposal failure can supersede probe/cancellation/formatting failure. Factory-internal partial construction, active-model mutation races, actual localized UI/dispatcher behavior and metadata/host lifetime remain outside qualification. No full coordinator, concrete provider, authentication, startup, native or full-suite execution occurred. No new pack/install/launch was performed; prior archive evidence remains historical registration-slice evidence, not verification of this revision. Broader Hosting/M2 and desktop parity remain open.

### M2 configured-provider composition checkpoint

The real `ConfiguredModelProviderRegistryBuilder`, raw-provider defaults resolver/generated TOML context and bundled TOML moved from TUI into nonpackable `CodeAlta.Hosting`. Startup and refresh in `CodeAltaOwnedServices` and temporary probe registration in `ProviderFrontendCoordinator` use the shared builder directly; no old implementation/facade or duplicate content entry remains. Hosting exposes only the builder, with five explicit public methods, and references Agent, Catalog and six concrete provider projects plus centrally pinned Tomlyn/Logging. Defaults types remain internal; OpenAI friend access preserves its internal per-batch Codex limiter. Guardrails include Hosting and remove the obsolete TUI builder exception without raising budgets.

Parent semantic comparison confirmed the builder is identical after namespace/accessibility/XML and two explicit forwarding-overload/default-parameter normalizations; the defaults resolver/context changed only namespace and the TOML Git blob is identical. Factory timing, logger category, ordering/filtering, overrides, nontransactional registry replacement and borrowed metadata/config/state-root ownership are preserved. The initial parent normalization check over-normalized a private parameter; correcting the comparison passed without a source change. Child reported **33 pre-extraction characterization/defaults passes**. Parent independently audited fixture paths, credential settings and lazy calls, then passed clean Release build (zero warnings/errors), **34 Hosting tests**, **two exact legacy defaults tests**, the unchanged **530 audited TUI tests** and **132 Catalog tests plus one Unix-only skip**, and Lunet (111 files; configured theme download/install). Parent logs: `%TEMP%/codealta-parent-hosting-{build,focused,defaults,tui,catalog,site}.log`; TUI/Catalog logs retain exact filters.

Parent independently opened all eight RID payload archives under `%TEMP%/CodeAlta.Hosting.Registration-b7a28827d6514d329330053f92ec7ab0`: each contains Hosting, Agent, all six provider assemblies, Hosting XML, exactly one byte-identical `ProviderDefaults/provider_defaults.toml` and unchanged `Data/models_dev_db.json`. The facade archive is also present; observed package version is `0.19.9-alpha.0.24`. Child's initial `--no-build` pack failed with `MSB3030` because RID outputs were absent; normal pack/build with `--no-restore` succeeded. Scoped restore was not established as network-free. No install, application launch, publish or native execution occurred in this slice.

This is registration/defaults extraction, **not full M2 or provider parity**. Auth/test/refresh workflows and models.dev lifetime remain TUI-owned; `CodeAlta.Orchestration.Hosting.CodeAltaHost`, startup admission, active-run replacement safety, partial-startup rollback and shutdown ownership remain unchanged. The broader host graph still has transitive terminal coupling through plugin abstractions. Archive reachability does not qualify runtime loading, installed tools, credentials/authentication, host startup, other operating systems or desktop/native behavior. No concrete provider factory, broad provider/ModelsDev/host fixture or full suite was executed.

### M2 reminder lifetime ownership investigation

Read-only investigation against `23cf9bd1` establishes an **unowned worker-join boundary**, not a completed disposal fix. Parent confirmed the sole child idle with no queued work, a clean tracked/staged baseline and the same three protected untracked artifacts, whose contents remained unread. Parent independently read the complete `AltaReminderService` (623 lines), `ReminderUiCoordinator` (85), `AltaServiceCollection` (38) and `ShellFrontendHost` (189), plus the named construction, App/outer/host cleanup, built-in lazy registration, send/detached-submission, UI-dispatch and dialog boundaries recorded in the plan. Further child findings about registry/runtime internals and individual old test bodies remain source-research evidence, not newly executed or independently requalified lower-owner behavior.

**Ownership:** `CodeAltaFrontendComposition.cs:116–147` creates a service per frontend composition and registers it in an instance dictionary that implements only `IServiceProvider`; registration is not cleanup ownership. `BuiltInAltaCommandContributor.cs:3170–3191` has another non-atomic fallback construction/registration route without cleanup transfer. `AltaReminderService.cs:111–120` publishes an entry, invokes its worker inline and discards the returned original before the Created notification pass. No inspected owner retains/joins these workers or stops service admission. The service retains its provider, and therefore references to runtime/catalog/hub dependencies that the Shell → outer → host route can dispose independently. App's unchanged stage two only unsubscribes reminder UI; it is not backend cancellation or joining. Borrowed-backend App construction does not make the frontend-created reminder service externally owned by inference.

**Resources and outcomes:** the sole worker reads its own CTS token and releases that source in terminal `finally` (`AltaReminderService.cs:267–374,485–508`). Delete removes membership, then requests cancellation and notifies outside the gate; it neither joins the worker nor retracts a captured delivery. A deleted-but-pending worker is absent from `_entries`, so a future snapshot of that dictionary alone cannot establish full worker ownership. No updater-style second disposer releasing the CTS before a worker token read, ordinary throwing private registration or concrete CTS-dispose failure was established. Positive duration can overflow initial due-time arithmetic before CTS acquisition; subsequent completion-relative due-time arithmetic and runner failure settlement have distinct exceptional paths. Requested worker cancellation is suppressed; unrequested OCE can escape without descriptor settlement. The non-OCE catch can itself fail and does not clear DueAt or increment FiredCount. No concrete failure reproduction or installed BCL correspondence was performed.

**Frozen firing/observer semantics:** preserve one-day delay chunks, immutable membership-checked capture, outside-gate delivery/clock/callback work, nonoverlapping per-entry delivery, completion-relative repeats, counted failed results, retained completed entries and no resurrection after deletion. Preserve captured observer membership, independent attempts despite ordinary observer exceptions/cancellation, immutable bounded query feedback and completion-order diagnostic publication without replay. Blocking observers can prevent worker/Create/delete completion. Shutdown cannot silently turn cancellation into a deletion notification, rewrite completion/count fields, cancel an already captured send or undo durable/runtime admission. A worker join would still not join every lower descendant: `BuiltInAltaCommandContributor.cs:2238–2257,4270–4312` can observe an original send through a discarded detached observer and return after acknowledgement/persistence. Its active-run queries are not bounded by the nominal acknowledgement deadline. Queue draining, provider completion and durable rollback are not implied.

**Frontend limits:** `ReminderUiCoordinator.cs:58–69` only unsubscribes. A service pass can already have captured its delegate, or the subscriber can already have queued a closure that mutates the version and refreshes the projection. `UiDispatch.Post` may invoke inline or submit a later action; a later action failure is outside the service observer pass. The coordinator does not retain the management dialog to close/invalidate it; dialog controls retain service and frontend callbacks. A disposed check could suppress stale publication but would not join executing callbacks, drain posts or qualify dialog/native teardown. Session-tab close is not service/application shutdown; reminders may intentionally continue after a tab closes.

**Next decision:** continue with one bounded **read-only backend contract design**, not a fixture or implementation. Resolve cleanup transfer for both construction routes, stop/admission, original-worker publication across inline completion/reentry, deleted-worker retention, cancellation traversal/source release, escaped-error and repeated-disposal policies, and ordering before dependency cleanup together. Recommend the smallest existing-owner route and exact mandatory production adapter/core; enumerate any necessary changes to frozen App/Shell/frontend/Deferred/outer/host and duplicated source gates. No API, cache, generic container cleanup, new runtime owner, test-only task model, hidden proxy, unbounded retention of deleted content or gate relaxation is accepted. UI invalidation remains a separate gap, not a substitute for backend lifetime. Parent review precedes source-gate Phase A and separately authorized inert fixture-first Phase B.

**Verification boundary:** no new build, test, site, application, service/timer/protocol or live-reminder execution. Old concrete reminder fixtures (including manual-clock and presentation cases that construct the service) delete entries/dispose clocks without original-worker joins; system-time UI/legacy cases also have successful-path-only deletion. They remain excluded. Source-only/helper-only methods are not automatic execution authorization; existing assembly logging is nonzero I/O. Parent checked eight frozen service/UI/prompt production/source/lifetime/guide/App/Shell hashes with strict UTF-8/no BOM, ordinal prefix/suffix checks, retained LF/CRLF and final newline. Service remains27,562 bytes/623 CRLF, SHA256 `4D1F9860E82357690576FB398661A9307D2EDD4CB62DE08E3F5558F494B146C0`; UI remains2,576 bytes/85 LF, `71DEB269F18D4E5C9F034E4CB9366E0562B2BE2DC3D7D762C2726E68EDF9C486`; App remains46,998 bytes, strictly below47,064. The first parent encoding command falsely rejected the service because its BOM prefix comparison was culture-sensitive; rerunning with ordinal comparison passed without source changes. Child reported no command failures and narrowly reread one truncated registry result. Only plan/parity integration records change here; prior prompt **65 new +157 regressions +four architecture** and build/site results remain historical. Protected artifacts, all production/fixtures/gates, active coordinator reminder and deferred prompt scope remain unchanged. **M2–M7 and complete shutdown remain open.**

#### Backend design review: narrower returned-App scope required

The first read-only design against `b2010879` is **not accepted for implementation**. It proposed an inactive/active service API and App activation, removal of lazy fallback registration, per-worker publication slots and mutator leases, original-completion retirement callbacks with one historical exception/omission count, one cancellation traversal with a source-release fence, and a shared `StopAsync` receipt. Shell would await that receipt before the existing frontend/owned traversal, carrying worker failures as data but withholding all that cleanup if a receipt could not be produced. The child also supplied five proposed mandatory seams, an inert inventory and inverse/gate map, and calculated an uncommitted App candidate at47,061 bytes. These are proposed-only artifacts, not implemented APIs, accepted runtime models or authorization to evolve frozen gates.

Parent confirmed the child idle/queue0, clean tracked/staged state and all eight frozen hashes, then independently read App150–183/388–399, Deferred130–189/350–459 and Program1–85 against the previously audited complete service/Shell/owner routes. **The fail-closed guarantee is only local to Shell:** `Program.cs:36–42,63–65` still disposes the prestarted plugin and logging after terminal failure, while `DeferredCodeAltaApp.cs:432–453` still attempts updater/presenter/source cleanup. Retaining host resources while those outer owners continue is a new partial-cleanup policy, not proof dependencies stay alive. The receipt also withholds unrelated frontend cleanup after terminal setup failure. Inactive construction/fallback removal, historical exception replay and callback-based retirement enlarge the compatibility and lifetime scope; no new retirement scheduling route or callback-tail exemption is accepted. Testing the startup callback seam cannot by itself prove the service's new admission/publication/retirement state machine.

**Selected refinement scope:** solve normal admitted reminder-worker ownership for a **successfully returned App**, using its existing Shell owner with explicit transfer before exposure and the same borrowed-backend rule. Do not add inactive/activation APIs or remove the lazy fallback in this slice. Hidden failed-App construction, fallback-created service ownership and externally initiated dependency cleanup remain open. Keep conventional best-effort cleanup after terminal reminder errors, followed by the existing frontend → late owned traversal; a pending reminder join may block progress, and catastrophic setup/unpublished work remains unqualified rather than receiving a receipt or Poisoned protocol. No complete termination promise is introduced.

The next sole-child assignment is a bounded read-only refinement: retain actual admitted originals across deletion and shutdown, and consider opportunistic completed-original observation/retirement on existing calls instead of a new completion continuation or observer task. Retention between calls and any bounded historical-error policy must be explicit, not claimed to be eager reclamation or unlimited replay. A shared service disposal can be justified by one stop snapshot/cancellation pass, but must not extend caching to Shell/frontend cleanup. Resolve cancellation-request return versus worker-only source release, exact API/error ordering and source-gate inverses before any fixture authorization. Keep UI stage2 unchanged; worker joining is not automatically synchronous-CRUD callback draining, queued UI invalidation, dialog closure or detached runtime-send completion. No replacement API/core, scheduling change, fixture or gate evolution is accepted yet. This review changed only plan/parity records; no builds/tests/site/runtime, live reminders or protected artifacts were touched. Existing prompt verification remains historical and **M2–M7 remain open**.

#### Accepted worker contract and source-gate-only authorization

The returned-App refinement is now accepted **for source-gate Phase A only**, following a further exact-code review against `2fc74e1a`. Parent required concrete worker-join/admission/publication/cancellation/finally bodies, a defensive input-array copy, explicit helper error precedence and corrected source-versus-runtime test claims. The sole child remained read-only and idle/queue0 at handoff. Parent rechecked eight frozen hashes and strict ordinal encoding/endings, independently read affected composition and source-guard bodies, checked named duplicate Shell assertions and confirmed the App argument-only candidate at47,026 bytes. There is no compiled/executed implementation yet.

**Ownership and scope:** active construction and lazy fallback remain unchanged. The frontend transfers its service to the existing Shell immediately after construction and before dispatcher exposure. App adds only `_frontendHost` as the mandatory composition-owner argument; no new App field, activation or PrepareForRun change. Public service `IAsyncDisposable` shares one instance-owned `Lazy<Task>` for one stopped snapshot/cancellation pass; this does not cache Shell/frontend cleanup. The service stops new worker admission/capture and joins retained published worker originals, including deleted entries, returned delivery, worker-fired observer passes and local finally. It does not drain synchronous CRUD notifications, pre-admission/rejected preparation, enclosing commands/output, queued UI/dialog work or detached sends/providers/queues. Failed hidden App construction, fallback ownership and independently disposed borrowed dependencies remain open. Recursive/self-waiting disposal can throw or deadlock; no total deadline or full service quietness is promised.

**Admission/publication/pruning:** retain a separate instance worker list rather than use membership as lifetime accounting. Prepare entry signals/delegates before CTS acquisition. Under the gate, reject stopped admission and transactionally insert membership/worker entry; known-unstarted failure removes those insertions before local outside-gate release. Invoke and assign the actual original outside the gate before completing its publication signal. Stop racing inline invocation selects that entry and awaits publication **then the actual task**, never merely a proxy signal. Delete removes membership only. Existing calls opportunistically prune only successful publications with actually completed, observed originals, under the same gate that disables pruning and snapshots at stop. Only the first encountered retired cancellation-or-worker failure reference is kept; later completed errors are observed but not replayed. This bounds retained history references, not exception bytes; completed graphs may remain between calls. No scheduled retirement callback/observer or copied runtime model is introduced. Failed disposal may retain its finite stopped snapshot; catastrophic before-assignment/publication/snapshot failure is not certified termination.

**Cancellation/state policy:** one private Delete/Stop cancellation claim runs outside the gate. Its error is recorded after traversal returns/throws, then a successful return-fence signal is completed outside ownership. Worker finally marks source-closing under the gate, preventing new claims, and awaits any claimed traversal's return fence before releasing its own CTS. A losing Delete does not repeat/wait for the winner or receive its later error; that intentional timing change is covered by source contract and the join core's stored-error read. No ordinary throwing private registration/BCL release failure was demonstrated and no actual CTS fixture is authorized. Valid mutations reject after stopping, including missing IDs, while original argument validation remains first. Queries remain available. The three normal worker checks include stopping, so stop can prevent capture/bookkeeping/repeat, but cannot retract a captured noncancelable delivery. Stop synthesizes no descriptor/event mutation: retained Active descriptors can be historical; the existing exceptional runner settlement can still notify through its joined worker.

**Exact mandatory-core policy:** `StartAndPublishReminderWorker`, `TryObserveCompletedReminder`, `CancelAndComplete`, `FinishReminderSourceAsync` and `DisposeReminderWorkersAsync` reside on the service; `DisposeRemindersThenFrontendAsync` resides on Shell. Startup/cancel completion-callback failure deliberately supersedes an earlier start/cancel error; successful completion preserves startup EDI or returns the cancel error. Such callback failure is not publication, cancellation-return or release permission. The join core takes one shallow array copy, validates ascending index/member order (request, publication, reader) synchronously, allocates error slots before callbacks, attempts all selected cancellations, then sequentially awaits every selected publication and actual nonnull original. A malformed null original fails explicitly; private reader failure cannot replace an original/publication failure. The reader runs only if this caller has no cancellation error; after malformed/failed publication it supplies only error-field evidence, not join/fence proof. Ordered direct errors are first retired reference, indexed cancellation/read failures, then indexed publication/original failures. Lone EDI and nested/repeated identities are preserved without flattening/deduplication. A lone observed OCE can cancel the returned disposal task even when its input was a faulted task. A blocking cancellation or pending selected task can prevent progress.

**Shell and frozen gates:** reminder cleanup precedes the unchanged seven frontend stages and late owned-services traversal. After terminal reminder failure, that old traversal is still attempted; lone error identity and direct `[reminder failure, existing nested failure]` order remain. Keep the old body verbatim under its new private name, freeze the new public expression adapter as an exact literal and scope only the brace-bodied private traversal/core. Preserve the entire old seven-stage adapter/core, synchronous UI unsubscribe, old `OriginalShell` literal and all reader/helper suffixes. Reviewed inverses must reconstruct whole service/Shell/App/composition and changed old-guard baselines. Only three existing App scheduling lines move (348→349,379→380,457→458); no allowance or budget increase. PrepareForRun, fallback, Program, Deferred/updater, host/outer and prompt/editor/controller production remain unchanged.

**Phase A boundary:** the sole writing child may create only `AltaReminderLifetimeSourceTests.cs` with fixed named-source reads, exact expected production code, inverses and unchanged-route checks. No old guard/production/docs changes or build/test/runtime execution are authorized to that child. Parent audits the complete source fixture/helper/read map before a targeted build and explicitly filtered source-only baseline, then freezes it before separately authorizing inert fixture-first Phase B. First-history mutation, actual instance Lazy sharing and admission/pruning/cancellation state placement are source-only evidence; fixture-side history, Lazy or copied state machines are not runtime proof. Later inert tests may exercise only the mandatory cores with retained supplied originals/callbacks and the accepted finite teardown discipline; no concrete service/owner/UI/timer/CTS/registration/protocol fixture. This acceptance changed only plan/parity records. No new verification execution, live-reminder changes, artifact inspection or complete-shutdown claim; prior prompt results remain historical and **M2–M7 remain open**.

#### Phase A independent source baseline and freeze

Against accepted contract HEAD `6af745e6737da594e3f5ff998a83102d7bef2db9`, the sole child added only `src/CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs`, with no compilation/execution. Parent confirmed child idle/queue0 and independently audited all1,202 lines: eleven source methods, exact future-code/XML literals, nine inverse maps, twelve unchanged routes and all byte/path/ordinal helpers. There are no production runtime calls or copied runtime models. `ReadSource` alone reads fixed named checkout paths resolved from this fixture's `CallerFilePath`; a complete traversal would make32 reads across21 unique files, reduced by early assertion failures. No config/home discovery or protected artifact contents were read. Existing writerless assembly logging and named source/hash reads remain nonzero I/O.

Parent independently parsed the C# quoted data without loading/executing inspected code, checked all21 baseline byte counts/line counts/SHA256/endings, then constructed and reversed all nine proposed edits in memory. Every forward/reverse occurrence count and complete original byte reconstruction passed. No production text was written. Service28 edits:27,562→47,461 bytes; Shell4:6,324→7,976; App1:46,998→47,026; composition3:28,803→28,958; frontend guard2:20,722→21,479; workspace guard1:63,586→63,926; Deferred guard1:27,695→28,073; prompt guard2:53,845→57,070; architecture3:147,905→147,905. App remains strictly below47,064; only the three accepted scheduling line relocations appear. Prompt inverse preserves `OriginalShell`, the exact adapter literal and old helper suffixes; no broad source/XML removal. Additional guide/prompt lifetime hashes match the accepted baseline. Git showed no tracked or staged changes before parent integration records.

**Observed verification:** `dotnet build src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-restore` passed with zero warnings/errors. `dotnet test src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-build --no-restore --filter <exact-filter>` returned exit1 with **10 failed, 1 passed, 0 skipped, 11 total**. The filter is the OR (`|`) of `FullyQualifiedName=CodeAlta.Tests.AltaReminderLifetimeSourceTests.` plus each exact method below, never a class/suite substring selection:

| Exact method suffix | Actual baseline outcome |
|---|---|
| `Admission_RetainsBeforeOutsideGateInvocation` | Missing `CreateAdmission` literal; expected1 occurrence, actual0 |
| `Publication_PublishesTheAssignedOriginal` | Missing `NewEntry` literal; expected1, actual0 |
| `Pruning_RequiresCompletedOriginalAndRetainsFirstHistory` | Missing `ObservationAndPruning` literal; expected1, actual0 |
| `Stop_SnapshotsUnderGateAndDisablesPruning` | Missing `DisposalMembers` literal; expected1, actual0 |
| `SharedDisposal_UsesOneInstanceLazy` | Missing Lazy constructor initialization; expected1, actual0 |
| `Cancellation_ClaimsOnceAndFencesWorkerRelease` | Missing `CancellationAdapters` literal; expected1, actual0 |
| `MutationsAndWorkerChecks_PreserveTheScopedContract` | Missing stopped worker checks; expected3, actual0 |
| `StaticCores_UseAcceptedValidationAndErrorPolicies` | Missing `JoinCore` literal; expected1, actual0 |
| `Ownership_TransfersToShellAndUsesBestEffortCleanup` | App byte count at fixture100: expected47,026, actual46,998 |
| `Preservation_InvertsOnlyApprovedProductionAndGuardChanges` | First reverse service edit absent: annotated full notification-failure update signature; expected1, actual0 |
| `Preservation_UnchangedRuntimeAndFrontendRoutesRemainFrozen` | Passed all twelve whole-file baselines and fallback/detached-send literals |

The nine missing-occurrence failures reached `RequireCount` at1190 (inverse at1170); Release stack frames for the first eight methods report source-read sequence points28/36/45/53/61/70/79/88, while inverse preservation reports114. These are expected absent-contract failures, not runtime cleanup results. No unexpected compilation/test blocker or fixture correction. Full logs are `%TEMP%/codealta-parent-reminder-phase-a-build-20260906-01.log` and `%TEMP%/codealta-parent-reminder-phase-a-tests-20260906-01.log`; bounded tails/error summaries were reviewed.

**Freeze/boundary:** fixture54,692 bytes/1,202 LF lines, strict UTF-8/no BOM/final newline, SHA256 `F65FA0B9652FC4F2BE92C89386660BEEF3E4FC1933E65162D823FCAF94B292FD`. Whitespace and post-verification hash checks passed. The scoped checkpoint contains only this fixture and parent plan/parity records; production and old guards are unchanged. This intentionally red Phase-A commit is not a green implementation milestone. Full-suite/solution/site verification was not run for this source-only gate; no public runtime behavior changed, and site build can download/install its configured theme. No concrete reminder/service/CTS/owner/startup/provider/auth/native execution, live-reminder changes, push/publish/merge or artifact cleanup occurred. Phase B remains separately unauthorized and must begin fixture-first under the accepted inert-only contracts. Actual admission/pruning/history/Lazy/CTS behavior, hidden acquisition/fallback/external disposal, CRUD/UI/output/descendant quiescence and **M2–M7 remain open**.

#### Phase B1 bounded inert-fixture authorization

After Phase A was frozen in `85ce913a`, the next coordinator check confirmed the existing child idle/queue0 and no tracked/staged changes; only the three protected untracked artifacts remained. The same sole writer is authorized to create **only** `AltaReminderLifetimeTests.cs` and `ReminderShutdownRoutingTests.cs` under `src/CodeAlta.Tests/`, then stop for parent audit. Production, existing guards, frozen Phase-A fixture and documentation/config/artifacts are not child-writable. No build/test/runtime execution is authorized; absent future cores mean compilation is not yet claimed. Parent must review all source/helpers and exact rows before separate production/guard authorization and explicitly filtered verification.

The proposed17 methods remain the bounded inventory: service fixture `Start_ValidatesBeforeCallbacks`, `Start_RetainsOriginalBeforeCompletion`, `Start_CompletionFailureHasExplicitPrecedence`, `Observe_RequiresActualCompletion`, `Cancel_ValidatesAndCompletesAfterTraversal`, `Cancel_CompletionFailureHasExplicitPrecedence`, `SourceRelease_RequiresSuccessfulReturnFence`, `Dispose_ValidatesSnapshotBeforeCallbacks`, `Dispose_PreservesInputsAgainstCallerArrayMutation`, `Dispose_AttemptsAllCancellationCallbacksBeforeJoining`, `Dispose_JoinsPublicationThenOriginal`, `Dispose_PreservesReaderAndWorkerFailures`, `Dispose_PreservesDirectErrorOrder`; routing fixture `Cleanup_ValidatesBeforeCallbacks`, `TerminalReminderFailureStillAttemptsExistingCleanup`, `PendingReminderCleanupBlocksExistingTraversal`, `CleanupPreservesFrontendContext`. Only actual mandatory static seams may be called, with supplied inert tasks/callbacks. No concrete service/entry/owner/App/Shell/CTS/registration/provider/registry/store/UI/version/logger/host construction, copied admission/history/Lazy model, sleeps/polling/fixture scheduling/timers/detached continuations/recursive probes or profile isolation substitute. Task retention, finally-released gates, concurrent independent five-second observations before every aggregate, second finite snapshot, permanent timeout failure, independent original fault/cancellation observation and body/teardown context restoration remain mandatory. Retain dispatch before concurrent dispatch/frontend/owner observations; context identity, not forced thread switching, is the contract. This authorizes fixture writing only, not broader reminder shutdown semantics or actual BCL qualification. M2–M7 remain incomplete.

#### Phase B1 independent fixture audit and Phase B2 boundary

The child delivered only the two authorized new fixtures against `235025eb`, without compilation/execution. Parent confirmed idle/queue0 and independently read every source line: `AltaReminderLifetimeTests.cs`716 lines and `ReminderShutdownRoutingTests.cs`304 lines. Git tracked/staged diffs were empty; only the two new fixtures and three protected artifacts were untracked. Parent re-read the complete accepted seven-stage frontend static core and checked all13 production method-call locations: service start34/62/101, observation140/148, cancellation186/194/213, release245/255 and join585; routing221 and frontend150 in the routing fixture. The future record alias/input construction is not concrete service/entry construction. No code was loaded or executed by the text/byte inspection.

Independent source counting confirmed the following inventory (declared rows, not test-discovery/execution results):

| Fixture | Exact method | Declared cases |
|---|---|---:|
| `AltaReminderLifetimeTests` | `Start_ValidatesBeforeCallbacks` | 3 |
| same | `Start_RetainsOriginalBeforeCompletion` | 5 |
| same | `Start_CompletionFailureHasExplicitPrecedence` | 3 |
| same | `Observe_RequiresActualCompletion` | 6 |
| same | `Cancel_ValidatesAndCompletesAfterTraversal` | 6 |
| same | `Cancel_CompletionFailureHasExplicitPrecedence` | 2 |
| same | `SourceRelease_RequiresSuccessfulReturnFence` | 9 |
| same | `Dispose_ValidatesSnapshotBeforeCallbacks` | 9 |
| same | `Dispose_PreservesInputsAgainstCallerArrayMutation` | 1 |
| same | `Dispose_AttemptsAllCancellationCallbacksBeforeJoining` | 3 |
| same | `Dispose_JoinsPublicationThenOriginal` | 9 |
| same | `Dispose_PreservesReaderAndWorkerFailures` | 9 |
| same | `Dispose_PreservesDirectErrorOrder` | 6 |
| `ReminderShutdownRoutingTests` | `Cleanup_ValidatesBeforeCallbacks` | 3 |
| same | `TerminalReminderFailureStillAttemptsExistingCleanup` | 11 |
| same | `PendingReminderCleanupBlocksExistingTraversal` | 1 |
| same | `CleanupPreservesFrontendContext` | 2 |

Totals: service13 methods/70 DataRows/one non-parameterized method =71 cases; routing4 methods/16 DataRows/one non-parameterized method =17 cases; combined17 methods/88 inert cases. With the frozen eleven source methods, the future new-test inventory is99 cases. None has been executed beyond the previously recorded Phase-A source baseline.

**Containment audit:** service methods21–582, production join wrapper584–585 and complete shared helper588–716; routing methods21–218, wrapper220–221 and inert context223–304. Instance-owned queues retain tasks, gate releases and permanent failures. Gates/publications retain their task before completion or exposure; actual originals, replacements/unselected inputs, core/frontend/dispatch/completion tasks and all bounded observers/wrappers/aggregates are retained before subsequent fallible assertions. There are no asynchronous original producers to discard. All17 methods finally await `FinishAsync`; it releases all gates before each of two finite snapshots. At the sole aggregate site689, every independent five-second observer is already started at687. Completed original fault/cancellation is independently observed, not inferred from wrappers. Timeouts from body/context/teardown remain permanent even after later completion. No actual CTS/registration/resource release is performed. Context dispatch is retained170 before completion observation171 and dispatch start172; body185–186 observes dispatch/core/actual frontend/completion and supplied tasks concurrently. Teardown206–209 retains/arranges dispatch before finishing all observations; prior context is restored167/201/215 and around every callback292–299. Assertions use context identity, not forced thread switching. Synchronous callbacks/core entry can still block indefinitely; finite asynchronous observations do not qualify synchronous termination. No copied admission/history/Lazy model, concrete owners, runtime filesystem reads, fixture scheduling/sleeps/polling/detached continuations/recursive probes or logger/localization initialization. Parent found no correction needed before the production step.

**Preservation/freeze:** parent raw ordinal checks passed strict UTF-8/no BOM/LF/final newline/trailing whitespace and exact hashes: service fixture34,450 bytes/716 lines `77B6E9BCB12F89CE5703D27451A7A55ABE1D9BAE673BA5745018CC5F9C143615`; routing13,966 bytes/304 lines `2A4462620FCE7EDF452B908BC9806C1AC2C1BCB8E59C47357F24297401139BF4`. Phase-A source remains `F65FA0B9652FC4F2BE92C89386660BEEF3E4FC1933E65162D823FCAF94B292FD`. All21 frozen production/guard/unchanged-route byte/line/hash/endings checks plus guide/prompt lifetime hashes matched. Child reported a truncated source read recovered with narrower reads and a whitespace wrapper that misclassified Git's LF-to-CRLF warning; command-local read-only retry and raw checks passed without config/endings edits. Parent inspection passed without a source correction. Both new runtime fixtures remain uncompiled/unexecuted/unstaged/uncommitted for the coherent implementation; absent cores are expected, not a discovered compiler blocker.

**Next sole-writer scope:** Phase B2 may change only `AltaReminderService.cs`, `ShellFrontendHost.cs`, `CodeAltaApp.cs`, `CodeAltaFrontendComposition.cs`, the frontend/workspace/Deferred/prompt source guards and `ArchitectureGuardrailTests.cs`, using exactly the nine frozen Phase-A forward maps, plus additive bounded reminder bullets in `doc/development-guide.md`. Freeze all three new fixtures; no test-side workaround or source-contract relaxation. Require whole-original inverses, verbatim renamed Shell traversal, exact expression adapter, unchanged helper suffixes and strict App47,026-byte candidate below47,064. Only existing architecture line relocations348→349/379→380/457→458 are allowed. Child performs no build/test/runtime/site/restore/commit and stops for independent parent production/guard/guide audit before explicitly filtered verification. Public integration guidance remains parent-owned after verification. No fallback/failed-acquisition/CRUD/UI/output/descendant completeness, new ownership/scheduling/cache policy or broad runtime qualification is authorized; M2–M7 remain incomplete.

#### Phase B2 implementation, independent verification and integration

Against `f87c4964`, the same sole child edited exactly the ten authorized files and stopped without execution. Parent confirmed idle/queue0, the exact tracked diff scope and the two frozen untracked runtime fixtures before review. Parent independently read actual service admission/mutation/query/worker checks and finally, all lifetime cores/cancellation/pruning/entry wiring, the complete Shell/App/composition diff, all five source-guard deltas and four guide additions. The three already fully audited fixtures remained byte-identical. No parent correction was needed.

**Whole-byte preservation:** independently parse frozen C# literals as data, never load/evaluate C#. For all nine targets, read the named HEAD blob, require normalized Git LF, explicitly reconstruct the metadata-specified checkout endings and verify the frozen baseline hash before applying the map. All45 edit entries/47 replacement occurrences passed forward and reverse cardinality checks; the candidate equals the entire edited file, and the inverse equals the complete original bytes. App is exactly47,026 bytes/811 CRLF lines, strictly below47,064; only the three approved architecture line relocations changed. Entire seven-stage frontend core, renamed old Shell traversal, `OriginalShell`, old helper suffixes, twelve unchanged routes and all three fixture hashes are preserved. Shell/App/composition contain no `ConfigureAwait(false)` literal. Removing only the reviewed four guide bullets46–49 (3,652 CRLF bytes; SHA256 `0D6C2153F59DADE68E6978F75D858B7933F1E5049C7A67676C51D7579059BB3A`) restores its78,407-byte frozen baseline. Strict UTF-8/no BOM/per-file LF or CRLF/final newline/trailing whitespace pass. Child's first raw-Git-versus-checkout comparison misclassified CRLF normalization, then passed after explicit conversion; parent comparison passed directly. Git's LF-to-CRLF warnings did not change actual endings or configuration.

Source wiring now implements the accepted returned-App contract: active service transfer before exposure, service-only shared Lazy stop/snapshot, retained actual admitted workers independent of dictionary membership, publication then original join, opportunistic first-error pruning, at-most-once cancellation return fence and worker-owned source release. Shell waits for reminder cleanup before its preserved frontend/late-owned traversal; terminal failure still attempts that old cleanup with direct ordered error policy. No fallback/Program/Deferred/outer/host or broader CRUD/UI/descendant policy changed.

**Observed builds/tests:** targeted `dotnet build src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-restore` and solution `dotnet build src/CodeAlta.slnx -c Release --no-restore` both passed zero warnings/errors. The solution reported its existing generated RPC contract, frontend dependencies and input fingerprint current. The new-test command was `dotnet test src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-build --no-restore --filter <exact-filter>`: the OR of `FullyQualifiedName=CodeAlta.Tests.<fixture>.<method>` for all eleven Phase-A names and all17 B1 names in the tables above, not fixture/class substrings. **99 passed, 0 failed, 0 skipped**. This is88 inert static-seam cases plus11 source cases; the prior Phase-A10 failures are now green. No compiler failure or runtime fixture correction occurred.

Parent separately reviewed the executable statements of the four affected source-guard methods and complete fixed CallerFilePath/ordinal helpers (previously audited frozen raw strings remain data), plus the affected architecture method and complete output-path ancestor/root-existence helpers. The four source guards perform3+3+6+17 named checkout reads; architecture enumerates only TUI App/Presentation C# files after locating the existing checkout from test output. This is nonzero source/path I/O, not profile isolation or concrete initialization. Their separate exact OR filter passed **5/5, zero failures/skips**:

- `CodeAlta.Tests.CodeAltaFrontendCleanupSourceTests.FrontendOwnership_SourceWiring_PreservesShellAndDeferredBoundaries`
- `CodeAlta.Tests.FileEditorWorkspaceCleanupSourceTests.WorkspaceCleanup_SourceWiring_UsesMandatoryCore`
- `CodeAlta.Tests.DeferredCodeAltaAppSourceTests.DeferredDisposal_SourceWiring_UsesCachedJoinAndExclusiveOwner`
- `CodeAlta.Tests.PromptDraftPrerequisiteSourceTests.PromptDraftPersistence_Source_PreservesFrontendAndStorageRouting`
- `CodeAlta.Tests.ArchitectureGuardrailTests.FrontendCoordinators_DoNotAddUntrackedFireAndForgetTasks`

No additional old behavioral fixture/class was assumed safe or run. Existing writerless assembly logging remains; the new runtime tests instantiate no concrete reminder service, entry, CTS/registration, owner, UI or provider. Actual admission/pruning/history/instance-sharing/source-close atomicity remains source-only evidence, not BCL/race qualification.

Logs: `%TEMP%/codealta-parent-reminder-phase-b-build-20260907-01.log`, `codealta-parent-reminder-phase-b-tests-20260907-01.log`, `codealta-parent-reminder-phase-b-guard-regressions-20260907-01.log`, `codealta-parent-reminder-phase-b-solution-build-20260907-01.log`, `codealta-parent-reminder-phase-b-site-20260907-01.log`. Parent updated public reminder and plugin-shutdown guidance. `lunet build` passed111 files/1,030,718 bytes; configured `lunet-io/templates` download/install was observed, so it was not network-free. No dependency installation command or native/default startup was run.

Replacement guard freezes (whole byte inverses retain their previous baselines): frontend `B876880F4867472EFCC03BC27EB8C49C87CDE7F637411706FBCE160B2BB81FEF`; workspace `F8FA8629783572476E570FE65191A9DAFBDBC94AFAA63E121D93FD63A166A62D`; Deferred `FF1F2C40CA27A4E0462AE034817A70986A74742CF4A379E4D0BB5ABC8C8A6793`; prompt `1C8AE92FA92CB7C8250450F4DC29A9DE04199572721742208F9B1B5C3CEF3609`; architecture `027D6857C155708F2BE147B169E3C4F57B02AE53D3C6B04345350C2B88371B4D`. Production hashes: service `5CBBFDC322CB00712D6979955F14988ECAB2BF2E6E5C87E712B2EA94D439CF73`; Shell `C0C9383DB4F4B52168801D3B9B01FEA385CE2D07769BAEAE8F1443AE3544F14B`; App `510AD5B44C683610FFEDD57B59A30FC3DC6A345F358B76595B9563E4F7E14177`; composition `966BEC14E53451734586BE5D5542C5596F399358BD03B2E512A153378A69D252`. Guide `30D053E30B2AE076B21048B0CE322B1E79DFACE841B4EFDC348A60C650003B50`. All three new fixture hashes remain as frozen above.

No remaining implementation/build/test/site blocker in this slice. Full suites, concrete reminder/protocol/presentation/UI/startup/provider/auth/native smoke, other RIDs and complete lower-owner shutdown were not qualified. Protected artifacts were not opened or changed; no live-reminder modification, push/publish/merge or cleanup occurred. Unknown setup/publication work, failed hidden App acquisition, ownerless fallback, externally disposed dependencies, synchronous CRUD notifications, queued UI/dialog/output and detached runtime descendants remain excluded. Pending operations can still delay shutdown indefinitely. This completes only the returned-App reminder worker substep; **M2–M7 remain incomplete**.

### M2 models.dev lifetime investigation boundary

After reminder integration `a4f4a8f6`, parent confirmed the existing child idle/queue0 and no tracked/staged changes; the three protected artifacts remained untracked and unread. Next scope is a **read-only** audit of `src/CodeAlta.Agent/ModelCatalog/ModelsDevCatalogService.cs`, its actual ownership routes and relevant existing test source. Preliminary parent reads1–165/179–298 show a guarded start with no stopped flag and a scheduled `_disposeCts.Token` read at82; disposal109–135 cancels before taking the task snapshot, suppresses awaited OCE, then releases the source and owned HttpClient without sibling-failure containment. Once an already-started worker is selected, the existing join precedes normal source release; a later token read alone is not proof of an updater-style race. Investigate actual start-versus-snapshot/source-close interleavings and distinguish source-backed exception channels from demonstrated ordinary BCL failures. Outer source references show creation/start138–143, owned cleanup64 and rollback191; existing concrete catalog tests can construct services/clients and must not be executed from this research authorization.

The same child may inspect named source/guard/fixture files and return ownership/task/token/resource/error-order evidence, exact affected preservation boundaries and a minimal proposed static-seam/inert-test inventory. No file edits, builds/tests, concrete service/HttpClient/CTS/registration construction, logger/version initialization, snapshot/cache/config/home/artifact contents, network/site execution, live-reminder changes or session compaction. Preserve accepted reminder/host/outer/frontend behavior and all budgets/scheduling allowances. Do not fold constructor rollback, cache-write durability, refresh outcome policy or broader startup ownership into a lifetime fix without a separate source-backed decision. This is not implementation authorization or completed lifetime qualification; parent must audit before the next fixture phase. **M2–M7 remain open**.

#### Independent models.dev policy review and next read-only boundary

Child research against `5d695050` is accepted as source evidence after independent parent review, not as implementation or runtime qualification. Parent confirmed the same child idle/queue0, no tracked/staged changes and exactly three protected unread artifacts. Parent reads cover the entire298-line catalog and outer ownership/construction/disposal/rollback/provider refresh, plus concrete tests419–464/585–636, outer source guards1–200, Deferred325–360, architecture744–810, frontend provider154–219, inspection135–234, registry42–70/97–149/247–270 and enricher1–48. The child additionally read complete outer1–379, concrete tests1–100/410–636 and broader named provider source, and searched tracked call sites; search-only results are not treated as complete audits. No runtime owner, service, CTS/registration/client, timer, logger, fixture or assembly was initialized by this work.

The missing same-gate stop boundary allows a **first start after a null disposal snapshot**, or after never-started disposal has completed, to escape the selected join and reach released resources. A task published before the snapshot is selected and ordinarily awaited through its delegate/refresh; late token access alone does not establish a race in that selected path. Ordinary outer creation constructs/starts catalog immediately at138–143 before host creation/return, so no normal TUI concurrent-start reproduction is claimed. Every current service disposal independently cancels/snapshots/joins/releases; the outer's cached disposal does not make the public service cached. Joining the worker is not proof that another cancellation traversal returned, but concrete BCL traversal behavior was not investigated. Cancellation failure currently skips the snapshot/join/releases; non-OCE worker failure escapes only after that selected original terminates and skips releases; source-release failure skips owned client. These are lexical exception channels, not demonstrated ordinary throwing CTS/default-client behavior. Supplied clients are borrowed and no application-defined registration was found.

Preserve whole host → metadata → owned logging and the same returned-acquisition rollback order. Outer best-effort logging after metadata failure and pending-host prevention of metadata cancellation do not establish global dependency liveness. Provider factories, inspection and frontend borrow metadata; enrichment only queries it. Preserve initial refresh before timer creation, sequential later refreshes, existing HTTP/I/O/access/JSON logging filters, query availability and retained database, and every cache/options/loading statement. Publication at215 precedes persistence216; existing refresh test451–457 observes publication before asserting the cache file, not completed persistence. Write/serialization/directory operations235–259 precede the Move cleanup try; constructor logger/CTS/client/load acquisitions can fail before the outer gets a returned reference. **Do not execute or repair concrete catalog tests, constructor rollback or cache durability in this slice.** No positive-interval timer failure, throwing default release, installed-BCL correspondence or runtime race was reproduced.

Accepted policy for the next exact-code proposal:

- Only future production file: `CodeAlta.Agent/ModelCatalog/ModelsDevCatalogService.cs`. One instance-owned `Lazy<Task>` caches one stop decision, selected original, cancellation traversal, join, releases and terminal outcome. Initialize it first in the internal constructor while preserving all existing constructor statements/order; earlier CTS acquisition remains outside rollback qualification.
- Under `_gate`, start calls `ObjectDisposedException.ThrowIf(_stopping, this)` before its already-started no-op, captures `_disposeCts.Token` and publishes the existing single `Task.Run(() => RefreshLoopAsync(token))` with no scheduling token. Stop sets `_stopping` and captures `_backgroundRefreshTask` under that same gate before invoking cancellation outside it. Completed/faulted retained tasks are not restarted; failed scheduling before assignment leaves null/retry-before-stop as before.
- Public disposal forwards `_disposeTask.Value`; private async snapshot adapter calls mandatory `ModelsDevCatalogLifetime.DisposeRefreshAsync(original, _disposeCts.Cancel, _disposeCts.Dispose, _httpClient.Dispose, _ownsHttpClient)`. The separate internal static type is appended **after the complete options type**, has no fields/initializer/logger/service access, and exists to avoid potential service static-logger initialization in inert tests, not to introduce a new owner/framework.
- Static signature is `Task DisposeRefreshAsync(Task? original, Action cancel, Action releaseSource, Action releaseHttpClient, bool ownsHttpClient)`. Validate callbacks synchronously in signature order, including borrowed HTTP; null original means never started. Allocate bounded error storage before callbacks. Attempt cancel; await actual original if present; then independently attempt source release and owned-client release. Retain callback errors including OCE; preserve **unconditional join-only OCE suppression**, including an observed faulted-OCE input. Nested aggregate containing OCE is not suppressed. Ordered direct errors are cancel → worker → source → owned client, lone EDI and multiple direct AggregateException without flatten/dedup. Async EDI of a lone callback OCE can yield canceled output. Pending original blocks releases even after cancellation failure.
- No second disposal cancellation caller or added fence protocol, retry, scheduling allowance, owner/guard change, recursive/self-disposal guarantee, synchronous-blocking bound or unknown allocation/setup recovery. No new context case for this infrastructure seam. Preserve all accepted reminder/frontend/outer/Deferred/host routes, App budget and UI/Views context rules. M2–M7 remain incomplete.

**Exact literals are still pending; no fixture authorization.** Same sole child may return six complete old/new code/XML edit pairs (EDI import; two fields; Lazy initialization; complete start XML/body; complete disposal XML/body/adapter; appended helper), exact anchors and reverse-order inverses. Parent must independently reconstruct the entire10,673-byte CRLF baseline, preserving all unrelated declarations/options and XML, before source-only fixture creation. No broad inverse deletion or XML stripping. This is a read-only handoff request: no files, builds, tests/discovery, source execution, network/site, live-reminder operation or compaction. Child's Agent-C# friendship search found no matches (search exit1, not a compiler error); project-level helper access remains a later compilation check, not a current blocker/result.

Candidate fixture names and source-declared inventory are now fixed for that handoff; any revision requires explicit parent review. No files or discoverable tests currently exist. Future filters must use `FullyQualifiedName=` for each exact namespace/class/method operand below, never a whole-class inferred-safe filter.

| Class (namespace `CodeAlta.Tests`) | Method | Exact rows |
| --- | --- | --- |
| `ModelsDevCatalogLifetimeSourceTests` | `Start_SourceWiring_StopsAdmissionBeforeTokenCaptureAndScheduling` | none |
| same | `Dispose_SourceWiring_SharesOneStopSnapshotAndCancellation` | none |
| same | `Dispose_SourceWiring_UsesMandatoryLoggerFreeCore` | none |
| same | `Preservation_InvertsOnlyApprovedCatalogLifetimeChanges` | none |
| same | `Preservation_OwnershipAndExistingGuardsRemainUnchanged` | none |
| `ModelsDevCatalogLifetimeTests` | `Dispose_ValidatesBeforeCallbacks` | null masks1,2,4,3,6,7; owns=false; cancel/source/client bits |
| same | `Dispose_PreservesJoinOnlyCancellationSuppression` | `absent`, `success`, `fault`, `faulted-oce`, `canceled`, `nested` |
| same | `Dispose_AttemptsLaterStagesAfterCallbackFailure` | (`cancel`,`fault`), (`cancel`,`oce`), (`source`,`fault`), (`source`,`oce`), (`client`,`fault`), (`client`,`oce`) |
| same | `Dispose_PendingOriginalBlocksReleases` | false,true cancellation failure |
| same | `Dispose_BorrowedClientIsNotReleased` | false,true original presence |
| same | `Dispose_PreservesDirectFailureOrder` | `cancel-worker-source-client`, `nested-repeated`, `suppressed-worker-oce` |
| same | `Dispose_JoinsSelectedOriginalOnly` | none |

Totals:12 methods/25 DataRows/31 cases =5 source +26 inert. First four source methods each read the catalog once; fifth reads the ten other named files below once each (14 planned reads/11 unique files, no discovery/enumeration). These establish source wiring, not actual admission/Lazy/BCL concurrency. Future inert methods call only the mandatory logger-free core; no test-side admission/history/Lazy model, concrete service/CTS/client/owner/App/Shell/provider/registry/store/UI/State/version/logger/host construction or source/filesystem reads. Retain every independent original, unselected, producer/callback/caller/core/observer/wrapper before fallible setup/assertions; finally release all gates, launch all independent five-second observers before every aggregate, take a second finite snapshot and independently observe original fault/cancellation. A timeout permanently fails that instance; later completion cannot rehabilitate release evidence. No sleeps/polling/fixture Task.Run/production timers/detached continuations/recursive disposal/context additions or temporary-HOME shortcuts.

Parent independently verified all frozen named raw baselines below: strict UTF-8/no BOM/ordinal ending checks/final newline, byte/line counts and SHA256; no normalized-line inference. Paths relative to `src/`. These reads are nonzero I/O; no build/test/site result is claimed.

| Path | Bytes / lines / endings | SHA256 |
| --- | --- | --- |
| `CodeAlta.Agent/ModelCatalog/ModelsDevCatalogService.cs` | 10,673 /298 /CRLF | `4FF490A7A3D403D6E9D72D063D9A87B0A34348D24F1D98DB635F52E77C374A15` |
| `CodeAlta.Tui/App/CodeAltaOwnedServices.cs` | 17,484 /379 /CRLF | `D2AD29EEF926D2D5F3BF6E99367D2D4C67F9C14BC4855ECD508D1676D49CBE61` |
| `CodeAlta.Tests/ModelsDevCatalogTests.cs` | 24,657 /636 /CRLF | `95FA97004A036B9EFAF0D1C978698E00C9F3484D0C87CAEA17B9047344A9CF51` |
| `CodeAlta.Tests/CodeAltaOwnedServicesLifetimeTests.cs` | 41,906 /934 /LF | `78A7B045DD137F3ABC1EA5AB2A5222F33AD668300B2915C12F99DCD29065620C` |
| `CodeAlta.Tests/DeferredCodeAltaAppSourceTests.cs` | 28,073 /503 /LF | `FF1F2C40CA27A4E0462AE034817A70986A74742CF4A379E4D0BB5ABC8C8A6793` |
| `CodeAlta.Tests/ArchitectureGuardrailTests.cs` | 147,905 /2,320 /CRLF | `027D6857C155708F2BE147B169E3C4F57B02AE53D3C6B04345350C2B88371B4D` |
| `CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs` | 54,692 /1,202 /LF | `F65FA0B9652FC4F2BE92C89386660BEEF3E4FC1933E65162D823FCAF94B292FD` |
| `CodeAlta.Hosting/ConfiguredModelProviderRegistryBuilder.cs` | 64,999 /1,384 /CRLF | `04AE668FEAE3C9D938856E02C7167AC2403FC1FC6FB2616A58E885404CE6C432` |
| `CodeAlta.Hosting/ConfiguredProviderInspection.cs` | 13,770 /241 /LF | `3BD3881A8AF8178C708EAA695D5DB3212D0D206F1C984A4E5990F61E6791A8AC` |
| `CodeAlta.Tui/App/ProviderFrontendCoordinator.cs` | 30,971 /682 /CRLF | `5A9E4705CF092BD2BCCAC1045BAD5C21671D785EA194D4FA7251812530DCE429` |
| `CodeAlta.Agent/ModelCatalog/AgentModelMetadataEnricher.cs` | 7,595 /183 /CRLF | `6E5707B44DCDE30CC3925059D6789FF11380B74FC3F5F8C4524D83ED786D43A1` |

Separately verified App47,026 bytes/811 CRLF lines, SHA256 `510AD5B44C683610FFEDD57B59A30FC3DC6A345F358B76595B9563E4F7E14177`, strictly below47,064. Only parent plan/parity records change for this decision. Protected artifacts, historical/ignored home residue, production/fixtures/guards and the active coordinator reminder remain untouched. No push/publish/merge; broader lifetime and M2–M7 remain open.

#### Exact catalog literals accepted; source-only fixture authorization

The sole child delivered complete E1–E6 old/new code/XML literals against `0f613fb0` without edits or C# execution (run `01a07a20-ab62-7c53-9ed5-2e9c00b22985`, content `msg_0cd04bac6eadda77016a9e3e422a6c87d298f2c45a3f6d1643`). Parent confirmed idle/queue0 and clean tracked/staged state, then independently audited the actual start, disposal adapter, snapshot, helper and XML—not merely their summary. Accepted unchanged: E1 imports; E2 retained-task/Lazy/stopping/database field anchor; E3 complete constructor signature/opening/first statement; E4 complete start XML/member; E5 complete disposal XML/adapter/private core; E6 **complete unchanged options XML/type** plus blank line and complete separate helper through EOF. Each displayed literal includes one final CRLF; all separators are CRLF, strict UTF-8/no BOM. Inverses replace each entire NEW with its corresponding entire OLD, E6→E5→E4→E3→E2→E1, not inferred brace/XML deletion.

Parent independently re-entered these exact literals as text, required one ordinal occurrence for all six forward and six reverse maps, and compared the reverse result ordinally and byte-by-byte. It reconstructs the entire10,673-byte/298-line CRLF baseline SHA256 `4FF490A7A3D403D6E9D72D063D9A87B0A34348D24F1D98DB635F52E77C374A15`. A separate raw named HEAD blob read established normalized LF and explicitly reconstructed CRLF before individual-byte checkout comparison. Candidate **16,820 bytes/417 CRLF lines**, SHA256 **`5293CB31630ED2ECABE9CAB719BDBECA3CDC9FFD2B9228382CD154F06F77ECAB`**, also passes strict UTF-8/no BOM/consistent CRLF/final newline. No candidate or script file was written; no C# compiled/executed. Text checks exit0, no inspection failure or parent correction.

Actual helper uses non-async synchronous signature-order null validation followed by an inline local async core, `new List<Exception>(4)` before callbacks, distinct cancel/join/source/owned-client catches and join-only unconditional OCE suppression. Four direct error slots suffice without growth under the accepted control flow. EDI reports a lone failure; a direct AggregateException reports multiple references in operation order. Separate helper has no fields/static initializer/service/logger access; cached public adapter and private same-gate stop/snapshot are mandatory routing, not test-side models. Public XML documents rejected starts, shared no-retry disposal, suppressed join cancellation, eligible releases/error order, borrowed-client exclusion and retained queries without recursive/external/constructor guarantees. Complete inverse preserves every existing logger/resource initializer, constructor statement/order, query, loading/refresh/cache/options byte. Unchanged outer/reminder/frontend/guard routes require no adjustments. Proposed code/accessibility remains **uncompiled**, and no concrete CTS/default-client failure, BCL race or complete shutdown is qualified.

**Authorize Phase A only:** same sole child may now create exactly `src/CodeAlta.Tests/ModelsDevCatalogLifetimeSourceTests.cs`; no other writes or execution. Five fixed methods/no rows from the preceding inventory, synchronous source-only tests, no production calls or initialization. Embed the accepted complete original and six literal maps including all XML; require exact new wiring/helper and whole-byte inverse reconstruction. Fixed14 reads/11 unique paths remain unchanged: first four each read only catalog once, fifth each of the ten named preservation files once. Use deterministic CallerFilePath-derived source roots and ordinal helpers without discovery/enumeration, source-assembly loading or hidden resource/config probes. Preserve per-file raw LF/CRLF baseline hashes; normalized-line checks alone cannot prove whole-byte equality. Source assertions qualify admission/Lazy/scheduling wiring only. No additional behavioral methods, copied state/history/Lazy model or synthetic concrete repeated-disposal test.

The intended unchanged-baseline outcome is four missing-contract failures (three wiring methods and full inverse) plus one preservation pass; **this has not been run or observed**. Parent must first audit the whole new fixture, every helper, literal/read map and five exact fully-qualified method operands; only then separately authorize targeted compilation and filtered baseline verification. Neither existing concrete `ModelsDevCatalogTests` nor whole fixture/classes/suites are inferred safe. No inert fixture, production/guide/old-guard edits, child compilation/tests/discovery, concrete catalog/CTS/client/owner/startup, native smoke, network/site, live-reminder change or compaction. Parent plan/parity changes only for this authorization; protected artifacts and historical home residue stay unread/untouched. M2–M7 remain incomplete.

#### Catalog Phase A parent audit and observed baseline

Sole child created only `src/CodeAlta.Tests/ModelsDevCatalogLifetimeSourceTests.cs` against `400e2c5c`, with no execution. Parent confirmed idle/queue0, empty tracked/staged diffs and only that new fixture plus three protected untracked artifacts. Independently read **all840 lines**: methods25–73, instance-value metadata/fixed baselines75–107, maps109–118, complete original120–419, twelve old/new literals421–744 and every helper747–840. Source methods are the fixed five synchronous void methods/no DataRows. ReadSource747–756 performs one named File.ReadAllBytes with CallerFilePath-derived root and strict decoding; helpers use only ordinal text/bytes/hash operations. First four each read catalog once and fifth ten fixed preservation files once (14 declared reads/11 paths), no enumeration/discovery/extra probes/runtime production calls/assembly loading. The logger/helper/CTS/client/timer code inside raw strings is data, not fixture initialization.

Parent independently parsed all13 raw literals as text with explicit indentation removal/LF→CRLF reconstruction; complete OriginalCatalog equals current source ordinally and byte-by-byte. Six unique forward/six reverse maps reconstruct every original byte and the accepted16,820-byte candidate/hash. All11 current named baselines matched strict UTF-8/no BOM/per-file endings/final newline/counts/SHA256; App remains47,026 bytes with its frozen hash. New fixture **34,690 bytes/840 LF lines**, strict UTF-8/no BOM/final newline/no trailing whitespace, frozen SHA256 **`3FB9E366E5ED952C347ABF22B9EED0E37B6A83F1B1487A2FF4171E1AA9433CC9`**. No parent corrections. All four catalog methods require complete inverse/forward/hash correspondence; missing future contract cannot silently pass. Source wiring does not qualify actual admission/Lazy/BCL concurrency.

Before execution parent re-read `CodeAlta.Tests.csproj`, `Directory.Build.props`, `global.json`, `.gitattributes` and complete `CodeAltaTestLogging.cs`: existing assembly hooks initialize writerless fallback logging and shut it down, not an initialization-free test process. `CodeAlta.Agent.csproj:19` explicitly declares InternalsVisibleTo for CodeAlta.Tests, resolving the earlier C#-only friendship-search uncertainty; the absent future helper still has no compilation result. Parent **targeted test-project Release build with `--no-restore` passed zero warnings/errors**. No dependency installation/restore, default startup or concrete service execution was invoked.

Parent then actually ran only the following exact filter with `dotnet test src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-build --no-restore --filter "<expression>"`:

```text
FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests.Start_SourceWiring_StopsAdmissionBeforeTokenCaptureAndScheduling|FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests.Dispose_SourceWiring_SharesOneStopSnapshotAndCancellation|FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests.Dispose_SourceWiring_UsesMandatoryLoggerFreeCore|FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests.Preservation_InvertsOnlyApprovedCatalogLifetimeChanges|FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests.Preservation_OwnershipAndExistingGuardsRemainUnchanged
```

Observed **4 intended missing-contract failures /1 preservation pass /0 skips /5 total**, exit1. Failure locations are start29 (NewStart), sharing38 (NewFields), mandatory-core50 (NewDisposal) and inverse820 (E6 NEW) through RequireOnce838; all are exact source occurrence assertions, not unexpected fixture/setup/runtime failures. The preserved ownership/guard case passed. Logs `%TEMP%/codealta-parent-catalog-phase-a-build-20260907-01.log` and `codealta-parent-catalog-phase-a-tests-20260907-01.log` (includes exact filter). Post-run fixture hash and Git scope unchanged. These named-source reads and existing writerless logging are nonzero I/O. No broad class/suite, concrete catalog/CTS/client/provider/auth/host/default startup/native or site execution; no test result for the absent future production helper. The intentionally red source gate is frozen for a self-contained source-only commit, not a finished implementation or unexpected blocker.

**Next bounded authorization, Phase B1 only:** same sole child may create `src/CodeAlta.Tests/ModelsDevCatalogLifetimeTests.cs`, with exactly the seven fixed methods/25 DataRows/26 inert cases above; no other writes/commits or compilation/tests/discovery/execution. Calls target only the future mandatory separate logger-free static cleanup core. Do not call/construct the service or other concrete owners/CTS/registration/client/timer/UI/State/version/logger/host, read source/filesystem, or model admission/Lazy/history on the test side. Retain independent actual originals/unselected/producer/callback/caller/core/observer/wrapper tasks before fallible setup/assertions; finally release every gate, start all independent five-second observers before every aggregate, take a second finite snapshot and separately observe original faults/cancellation. Timeouts permanently fail even if later completion occurs. Test direct ordered/nested/repeated exception identities and faulted-OCE versus canceled input without changing join-only suppression. No context addition, sleeps/polling/fixture Task.Run/production timers/detached continuations/recursive probes. Parent must audit the entire delivered fixture/helpers/rows before separately authorizing exact production implementation and later compilation. Existing source fixture/production/guard/guide bytes remain frozen; protected artifacts, ignored/historical home residue and live coordinator reminder untouched. No push/publish/merge; M2–M7 remain open.

#### Catalog Phase B1 fixture accepted; exact production authorization

Same sole child delivered only `src/CodeAlta.Tests/ModelsDevCatalogLifetimeTests.cs` against `034bc1f6`, without compilation/execution. Parent confirmed idle/queue0 and empty tracked/staged diffs, then independently read **all423 lines**. Inventory is exactly seven fixed methods/25 DataRows/26 source-declared cases, not test discovery. Seven direct calls38/74/109/137/172/229/268 target only mandatory future `ModelsDevCatalogLifetime.DisposeRefreshAsync`, each immediately retained. Every method has an awaited finally; complete assertion helpers301–329 check original and cleanup state/identity, including truly Faulted-with-OCE versus Canceled input from immutable token data, nested aggregates and repeated direct error references. No fixture-side service admission/history/Lazy model or actual service repeated-disposal claim.

Parent audited full per-case TaskScope333–422: originals/gate tasks are retained before later setup/assertions and gates register unconditional final release; no async callback/producer/frontend/context work is introduced. Observer/bound tasks are retained, original faults/cancellation separately observed, all supplied independent five-second observers start before the sole Task.WhenAll395 and its retained wrapper/aggregate. Finally releases every gate, observes a finite first snapshot, releases gates again and observes the second finite snapshot containing additional wrappers. Every timeout is retained permanently; late completion cannot rehabilitate release evidence. Finish itself is awaited by the test finally, excluded from its own joined set to avoid self-wait. Selected-only case retains both originals, starts the unselected observer275 before selected/core aggregate284, proves core completion leaves unselected pending, then faults/independently observes it and its first observer. Pending-original rows check both releases wait for terminal original even after cancellation failure. Borrowed-client callback remains excluded, and callback OCE remains an error distinct from suppressed join OCE. No concrete service/CTS/registration/client/owner/UI/State/version/logger/host, filesystem/source reads, sleeps/polling/Task.Run/production timers/detached continuations/recursive probes or context expansion.

Parent text/raw checks confirm seven methods/25 rows/seven retained calls/seven finally paths/one aggregate site and **20,018 bytes/423 LF lines**, strict UTF-8/no BOM/final newline/no trailing whitespace; frozen SHA256 **`DE9F5E136924E713467D80C998390F9EEF6FE4853E72C65D1146D32D5DBE6345`**. Source fixture remains `3FB9E366E5ED952C347ABF22B9EED0E37B6A83F1B1487A2FF4171E1AA9433CC9`; all11 fixed raw baseline/count/ending/hash checks pass, App remains47,026 bytes/below47,064 with frozen hash, and guide hash remains `30D053E30B2AE076B21048B0CE322B1E79DFACE841B4EFDC348A60C650003B50`. No parent correction or current inspection error; **no new build/test/discovery result**. The absent helper is expected and has not been compiled with the new inert fixture. Leave that fixture untracked/unstaged/uncommitted for the coherent production step; this checkpoint commits only parent audit/authorization records. Prior Phase-A clean build and four-intended-red/one-green evidence remain unchanged.

**Authorize Phase B2 exact production only:** same sole child may apply only the frozen six E1–E6 literal maps from the accepted source fixture to `src/CodeAlta.Agent/ModelCatalog/ModelsDevCatalogService.cs`. Exact candidate16,820 bytes/417 CRLF lines, SHA256 `5293CB31630ED2ECABE9CAB719BDBECA3CDC9FFD2B9228382CD154F06F77ECAB`; preserve strict UTF-8/no BOM/final CRLF and complete reverse E6→E1 reconstruction of the10,673-byte baseline. No fixture/old-guard/guide/plan/parity/other production changes; no child compilation/tests/discovery/execution/staging/commit. Parent must independently audit the actual full production/diff/inverses and confirm both fixture hashes before any separately authorized exact-filter verification. Parent then owns development/public/integration documentation and scoped production commit. No constructor/cache/query/refresh/options/client-ownership/outer/reminder/frontend policy change beyond the frozen map, new owner/fence/cache/retry/context fixture, architecture allowance or App budget change. Protected artifacts and coordinator reminder remain untouched; no push/publish/merge, and M2–M7 remain incomplete.

#### Catalog selected-refresh integration and passing verification

Sole child applied only the six frozen catalog edits against `1040f16a`, without execution. Parent confirmed idle/queue0 and scope, independently read all417 actual production lines and complete diff, then parsed the frozen source literals and reproduced all six unique forward/reverse maps. Actual checkout equals every forward byte; inverse restores the10,673-byte original and every raw HEAD blob byte after explicit normalized-LF→CRLF reconstruction. Actual production16,820 bytes/417 CRLF lines, strict UTF-8/no BOM/final CRLF, SHA256 `5293CB31630ED2ECABE9CAB719BDBECA3CDC9FFD2B9228382CD154F06F77ECAB`. No correction. Both fixture hashes and ten other fixed ownership/call/guard files remain frozen, as does App47,026-byte budget/hash. All other catalog constructor/initialization/query/loading/refresh/logging/cache/options bytes survive the full inverse.

Actual routing is cached adapter135 → same-gate stop/snapshot140–144 → mandatory separate helper146 outside the gate; start86 rejects before no-op/token92/scheduling93. The separate fieldless/initializer-free helper starts at320 after complete options281–315, avoiding service static-logger initialization in inert calls. Callback validation352–354 is synchronous; storage360 precedes cancel363, actual join374 suppresses only observed OCE, then source387/owned-client398 releases are attempted independently. EDI408/direct aggregate413 preserve accepted order/identity. This fixes only selected-refresh lifetime, not constructor rollback, cache durability, actual BCL traversal, external dependency liveness or complete startup/shutdown.

Parent targeted test-project and solution Release builds, both with `--no-restore`, **passed zero warnings/errors**. The newly implemented core and inert fixture now compile successfully. Parent ran only the exact OR of the five previously recorded source FQNs plus these seven operands, with `dotnet test src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-build --no-restore --filter "<expression>"` (the full single-line12-operand expression is retained in the test log):

```text
FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeTests.Dispose_ValidatesBeforeCallbacks
FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeTests.Dispose_PreservesJoinOnlyCancellationSuppression
FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeTests.Dispose_AttemptsLaterStagesAfterCallbackFailure
FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeTests.Dispose_PendingOriginalBlocksReleases
FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeTests.Dispose_BorrowedClientIsNotReleased
FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeTests.Dispose_PreservesDirectFailureOrder
FullyQualifiedName=CodeAlta.Tests.ModelsDevCatalogLifetimeTests.Dispose_JoinsSelectedOriginalOnly
```

Observed **31 passes /0 failures /0 skips** =5 source+26 inert cases. The source gate is now green after its actual Phase-A four-intended-red/one-green baseline. No fixture correction, unexpected failure or additional old class/suite/architecture method was inferred safe or run; preservation evidence includes ten exact unchanged source/guard hashes and the full catalog inverse, not newly executed older runtime qualification. No concrete catalog/CTS/client/registration/owner/default startup/provider/auth/native or broad test execution. Source/hash reads and pre-existing writerless assembly logging remain nonzero I/O, while runtime calls reach only the audited separate mandatory core with retained inert tasks/callbacks and bounded teardown.

Parent added three development-guide bullets46–48 and one public metadata-shutdown paragraph to `site/docs/plugins/readme.md`; removing only those exact three guide additions restores the entire82,059-byte guide baseline and every explicitly reconstructed HEAD checkout byte. New guide84,358 bytes/194 CRLF lines SHA256 `084B575D0DB8329078EE2FF8DE34F3A7B93A428F2117B0A5CCE1FF46EE268926`; both fixtures remain unchanged. Native qualification explicitly records no new native evidence. Parent `lunet build` **passed111 files/1,031,237 bytes**; configured `lunet-io/templates` download/install was observed, so it was not network-free. Logs: `%TEMP%/codealta-parent-catalog-phase-b-build-20260907-01.log`, `codealta-parent-catalog-phase-b-tests-20260907-01.log`, `codealta-parent-catalog-phase-b-solution-build-20260907-01.log`, `codealta-parent-catalog-phase-b-site-20260907-01.log`.

No remaining implementation/build/test/site blocker in this slice. Scope for production integration is catalog source, inert fixture and parent development/public/native/plan/parity documentation; the source fixture is already committed and frozen. Preserve protected artifacts and ignored/historical home residue unread/untouched; no live-reminder modification, push/publish/merge or cleanup. Pending refresh or earlier cleanup can still block indefinitely; query publication still precedes cache persistence, and hidden constructor acquisitions, external disposal, other lower owners and **M2–M7 remain open**.

### M2 project file-search session lifetime investigation boundary

After catalog selected-refresh integration `01fc8aaf`, parent confirmed clean tracked/staged state with the three protected artifacts unread. Next bounded step is **read-only** investigation of the editor/prompt file-search session lifetime left open by the earlier workspace review. Parent read complete `CodeAlta.Catalog/ProjectFiles/ProjectFileSearchSession.cs`1–343 and `IProjectFileSearchSession.cs`1–30, and searched only tracked named creation/caller/test paths. `RefreshAsync`110–136 cancels/releases a previous source under `_gate`, installs a new linked source, publishes state, then reads that source's token while discarding the actual refresh operation. Disposal140–155 marks disposed and cancels/releases without joining refresh/traversal/ranking; ranking scheduling253 discards its task. PublishState checks disposal/updates state under the gate but invokes Updated324 outside it. These are lexical ownership/ordering channels, not a demonstrated ordinary CTS failure, concurrent/reentrant trigger or approval of a new policy. Preserve the existing task scheduling/cancellation arguments, generation checks, scoring, cache/usage behavior and publication semantics unless a separately reviewed decision changes them.

Same sole child may inspect the session/interface/options, service acquisition and actual picker/popup ownership, snapshot cache/usage/traversal/scorer contracts and implementations, relevant frozen guards and existing test source. Distinguish returned-session cleanup from hidden acquisition, every refresh generation from descriptor/current-source membership, cancellation traversal from worker termination, and ranking/state publication from callback/queued-UI completion. Determine ordinary production triggers versus hypothetical injected/BCL/allocation failures, and document exception channels without executing them. Existing concrete file-search tests are **not authorized** by this research request, even with fake dependencies. Return evidence/file refs, preserved-versus-changed policy, minimal proposed mandatory inert seam/test inventory and frozen source/inverse boundaries, or justify deferral if a bounded safe fix is not established. No fixture/production edits, builds/tests/discovery, source-assembly or concrete CTS/registration/session/service/owner/provider/UI/logger/host execution, protected snapshot/cache/config/HOME/credential/artifact contents, network/site, reminder changes, compaction, new child or scope expansion. Parent independently audits before any implementation authorization; all accepted catalog/reminder/editor/frontend/outer/architecture boundaries remain frozen.

Parent raw baselines: session11,953 bytes/343 CRLF lines SHA256 `4A2C24DC1C6E11D259E6E033FEB2F9519D4AC35FD27F948CF2DD00C5533193AA`; interface1,037 bytes/30 LF lines SHA256 `A7311FB8B0DB4D50E380A15E10F429118BAA5DD845D0981D554AB379E55C5388`, strict UTF-8/no BOM/final newline. The first two-file check incorrectly assumed uniform CRLF and exited1 on the interface after validating session; corrected explicit LF validation passed, no source change or unresolved inspection blocker. Named reads are nonzero I/O, not execution. Catalog integration's passing31 cases/build/site remain historical evidence for that completed slice; no new runtime/native qualification, push/publish/merge or completed M2–M7 claim.

#### Restart checkpoint — paused at research handoff, 2026-09-07

User requested stopping at the next round to restart CodeAlta. The sole child completed its read-only investigation against `16e6371f` and is confirmed idle with zero queued prompts and no children. Parent deleted coordinator reminder `reminder-01a0775f965b7cc7b3549db62839e536`; do not resume from stale reminder delivery. No new assignment, implementation, fixture, build, test or discovery is authorized. Resume only after user instruction. Full child report is in session `01a0777e-c548-7684-998f-347ac55cd394`, run `01a07a54-35d8-76a1-9bc1-51ddc539ced1`, content `msg_05277c6174483888016a9e4cba448487d2ad46dadbcc98aef8`.

**Child-reported findings, not yet independently accepted by parent:**

- Session15–20/110–155/173–193/229–253 retains neither all replaced refresh originals nor separately scheduled ranking tasks; source membership and the scheduled flag are not terminal task evidence. Initial rank/publication123–136 precedes token access and worker invocation, exposing concurrent/reentrant token access and synchronous failure-before-start channels. Cancellation traversals and source release need separate ownership from worker joins; no default throwing BCL registration or runtime race was reproduced.
- Ranking279–308 and publication314–324 have generation/publication/callback windows distinct from worker joining. Previously discarded worker failures have no current session error channel; changing their observation, retention or ranking restart behavior requires explicit policy. Default cache/usage calls return completed operations, but seed timestamps and traversal perform filesystem I/O. Existing Catalog tests construct real services and include filesystem, delay/polling and ignored restart behavior; popup tests construct real UI. None was executed or approved for execution.
- Service44–65 starts usage/cache acquisition, constructs and initially refreshes before returning. Hidden acquisition/rollback remains separate. Picker controller245–273 clears mutable session state after awaiting disposal; making disposal pending can change ordinary close/reopen timing. Popup creation153–215 and dispatch495–504 can outlive disposal or lose attachment/cleanup; popup close425–438 clears its captured session before awaiting. Queued UI/hidden creation are not covered by a returned-session task join.
- Workspace source guard547–742 freezes the current service/session lifetime behavior. Its whole-file preservation is embedded in `AltaReminderLifetimeSourceTests.cs`119/158–159/981–984, whose whole-file hash is frozen by `ModelsDevCatalogLifetimeSourceTests.cs`64–72/97–98. Any later guard change needs explicitly reviewed inverse maps through that chain, not baseline rebasing or retained dead old code. Architecture scheduling allowances, App budget, prior whole-original inverses and settled catalog/reminder/frontend/workspace routes remain frozen.

Child recommends **deferring implementation** until five decisions are accepted: all admitted refresh-start/refresh/traversal/ranking work to join; synchronous initial exception timing; competing cancellation traversal and single source-release authority; terminal failure retention/order; and pending/failing disposal effects on callers plus guard inverses. No exact new code, mandatory seam or fixture inventory was proposed: a supplied-task join helper alone would not establish actual work ownership. Parent audit and bounded policy/design selection remain the next steps after restart, not an implementation authorization. The report contains full/targeted read maps and raw baselines; those additional claims remain child evidence until audited. No new runtime/native qualification or M2–M7 completion. Catalog integration and its prior passing verification remain unchanged; protected artifacts remain unread.

#### Resumed parent audit and checkout-preservation prerequisite, 2026-09-07

User explicitly resumed work; this supersedes the pause above. Parent independently audited the file-search report before any implementation or execution. Full reads: the thirteen Catalog session/interface/options/service/cache/usage/traversal/scorer/path sources listed in the child report; picker controller1–342, popup controller1–577, ChatPromptEditor1–153 and Catalog service tests1–335. Targeted reads: dialog1–175, workspace1–84, host212–241, popup tests1–65, workspace source guard535–744, reminder source64–173/960–1005, catalog source64–128/720–840, architecture744–811 and prompt prerequisite source504–518. Tracked named-source searches checked creation/refresh and guard references; no wider search result is a full audit. External Glob/BCL implementations remain uninspected, and no runtime race or concrete throwing cancellation callback was reproduced.

Parent confirms the report's source-level work/token/publication gaps and preservation chain. Initial ranking/publication remains synchronous before the discarded refresh original; all replaced generations and ranking originals are absent from disposal's inventory. Session-initiated cancellation traversal return must not be confused with worker termination or arbitrary external linked-token cancellation completion. Default memory reads are synchronous, but seed timestamps and traversal perform filesystem I/O. Source ownership does not include shared dependency disposal. Existing Catalog service tests have real construction/filesystem/polling/sleep behavior; popup tests have real UI initialization, so neither is execution-authorized. Additionally, popup `DisposeAsync`84–89 rereads `_session` after awaiting `CloseAsync`, despite close425–438 itself capturing/clearing before await. A pending-disposal design must account for that second mutable lookup as well as picker245–273's post-await state reset. Parent accepts **deferring file-search implementation** until returned-session and caller contracts are explicitly bounded; merely supplying tasks to a join helper or moving only the picker assignment is insufficient. The research/design row remains open, with the parent audit substep complete.

**New preservation evidence invalidates using historical raw-checkout baselines as current verification.** A named twelve-file baseline script passed session/interface/service/picker/popup, then exited1 at `FileEditorWorkspaceCleanupSourceTests.cs`. Subsequent named reads found four formerly LF fixtures now uniformly CRLF. For each, parent checked strict UTF-8/no BOM/final newline, ordinal CRLF reconstruction, byte round trip, exact historical LF-inverse byte count/SHA256, and binary equality with the named HEAD blob rendered into CRLF. This proves only newline representation differs for these four files; it does not identify what changed checkout endings or authorize rewriting them. Git tracked/staged state was clean. No source, fixture or config was restored/normalized. A first reconstruction command had a PowerShell interpolation parse error (`$p:`); corrected `${p}:` command passed without file changes. These were inspection failures, not test results.

| Fixture under `src/CodeAlta.Tests/` | Current bytes / lines / endings | Current raw SHA256 |
|---|---|---|
| `FileEditorWorkspaceCleanupSourceTests.cs` | 65108 / 1182 / CRLF | `975E466FF837D783FB159E20F2703C444C42668863E0836B9E63088ADC184CB6` |
| `AltaReminderLifetimeSourceTests.cs` | 55894 / 1202 / CRLF | `3DA20C67D1B0EA9892D7F600A39C165329B460513108548290C8E5F2B2B96F93` |
| `ModelsDevCatalogLifetimeSourceTests.cs` | 35530 / 840 / CRLF | `B08459AF5D056447A8C011AD4F51ECB0FD928714FCA610948253E2B5090CD05F` |
| `ModelsDevCatalogLifetimeTests.cs` | 20441 / 423 / CRLF | `9C7B839F02558B756099EC427232A811CB1A05453D867D411F4A64DC9C8D5ECF` |

The four historical LF byte/hash baselines remain unchanged and were matched only by **in-memory inverse**, not by today's raw checkout. Catalog production remains16820 bytes/417 CRLF with hash `5293CB31630ED2ECABE9CAB719BDBECA3CDC9FFD2B9228382CD154F06F77ECAB`; App remains47026 bytes/811 CRLF with hash `510AD5B44C683610FFEDD57B59A30FC3DC6A345F358B76595B9563E4F7E14177`, below47064; guide remains84358 bytes/194 CRLF with hash `084B575D0DB8329078EE2FF8DE34F3A7B93A428F2117B0A5CCE1FF46EE268926`. The first five file-search baseline rows also matched their historical raw bytes/counts/endings/hashes. Other child raw-baseline rows have not been requalified as current raw baselines. Plan/parity themselves now use CRLF and must retain that actual checkout style during this update.

`.gitattributes`1–4 sets auto text and LF only for shell/verified-text files. `ModelsDevCatalogLifetimeSourceTests.AssertEncoding`758–766 requires each recorded checkout ending, and `CatalogLiteral`771–775 explicitly rejects CR in frozen literals. At least its unchanged-route read of the now-CRLF reminder fixture conflicts with the frozen LF baseline. No test was executed to reproduce this; previous catalog31-case/build/site passes remain valid historical integration evidence, not a new-checkout pass claim. Do not silently update hashes, drop raw checks, accept arbitrary normalization, or use a broad cleanup/checkout to hide the discrepancy.

**Next sole-child assignment is read-only preservation compatibility design**, before more file-search design/implementation. Compare the smallest scoped attribute approach with strict checkout-aware in-memory reconstruction; enumerate exact affected named paths/readers/raw literals, expected LF/CRLF contracts, complete historical inverses and mutation rejection requirements. Return an exact proposed correction and inert source-only verification inventory when supportable, or a precise remaining decision. Preserve all production bodies, existing lifetime error/ownership policies, App budget, scheduling allowances and historical whole-original proofs. No actual attributes/config/source/fixture edits, line-ending rewrites, staging, commits, builds/tests/discovery, source-assembly/concrete service/UI/CTS/logger execution, protected artifact/HOME/cache reads, network or compaction. Parent must audit before any corrective implementation. This is a verification prerequisite, not permission for a repository-wide test rewrite. Same child is confirmed inactive/not running, queue0/children0; initial small status caps suppressed its large recoverable-summary record, and a larger bounded read confirmed state without a new writer. Coordinator may resume for this assignment; later pause/stop still takes precedence. M2–M7 remain open.

#### Accepted checkout contract and failing-before-fix evidence

Parent independently accepts the attributes-only recommendation from child run `01a07d3e-3d21-7402-a734-a21420de1899`, content `msg_001d5e3be608e515016a9f0b178cfc87d2a1258b1c39d46c82`. No parser, literal, C#/XML, historical hash, baseline occurrence-count or inverse change is approved; no new negative-test seams or test-side model are needed for this representation-only correction. The existing mandatory source guards supply regression coverage. Attributes are not encoding validators, and older workspace reader/conditional-inverse behavior is not silently upgraded. This pins a finite existing contract on every platform, not a platform skip or repository-wide style policy.

Parent re-read catalog1–119/746–840, reminder145–244/1129–1202, test project/assembly hooks/build props/global SDK selection and complete attributes. Independent literal-baseline extraction found catalog12 and reminder21 entries,29 unique direct paths with consistent shared ending contracts; adding the two catalog fixtures gives31. All31 strict UTF-8/no BOM/final-newline/uniform-ending/byte-round-trip checks passed, and each explicit LF content matched the corresponding binary HEAD blob. Exactly12 files need CRLF→LF;19 need no rewrite. All ten catalog and twelve reminder unchanged-route renderings matched their frozen byte counts/line counts/hashes. Changed reminder production/guard original hashes were not incorrectly compared without their inverse maps. `git check-attr` for all31 found text:auto and eol/filter/working-tree-encoding unspecified. No user content, file or Git state was changed during this audit.

Before correction, parent rebuilt only `CodeAlta.Tests.csproj` Release with --no-restore:0 warnings/errors. After source audit, ran only these exact FQNs, joined with FullyQualifiedName equality OR terms (not a class filter):

```text
CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests.Start_SourceWiring_StopsAdmissionBeforeTokenCaptureAndScheduling
CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests.Dispose_SourceWiring_SharesOneStopSnapshotAndCancellation
CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests.Dispose_SourceWiring_UsesMandatoryLoggerFreeCore
CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests.Preservation_InvertsOnlyApprovedCatalogLifetimeChanges
CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests.Preservation_OwnershipAndExistingGuardsRemainUnchanged
```

Result: **5 failed /0 passed /0 skipped**, exit1. First four failed at CatalogLiteral773 (`literal.Contains('\r')`); the unchanged-route case failed at AssertEncoding765 for `CodeAltaOwnedServicesLifetimeTests.cs`. This establishes the compiled-literal representation issue for this build, superseding prior source-only uncertainty. No concrete catalog/reminder/search/UI/runtime tests ran. Source reads and writerless assembly logging are nonzero I/O. Logs are `C:\Users\alexa\AppData\Local\Temp\codealta-parent-eol-phase-a-build-20260907-01.log` and `codealta-parent-eol-phase-a-tests-20260907-01.log`; historical catalog integration remains unchanged, and post-fix tests are still pending.

**Frozen attributes map:** old `.gitattributes` actual bytes159, four logical lines/three CRLF separators, no BOM and **no final newline**, SHA256 `5808EBAC8AE32E5A5F3C837ABD532F31B6CF28608315EAAF5E2B6D59275B3DF8`. Preserve all those bytes as the prefix. Append two CRLF separators, then exactly the following comment/rules joined with CRLF, with **no final newline**. Whole new file has37 logical lines/36 CRLF separators. This explicit EOF recipe corrects the ambiguity in the child code fence; no existing terminal newline may be assumed.

```gitattributes
# Exact checkout contracts for accepted catalog/reminder preservation guards.
/src/CodeAlta.Agent/ModelCatalog/AgentModelMetadataEnricher.cs text eol=crlf
/src/CodeAlta.Agent/ModelCatalog/ModelsDevCatalogService.cs text eol=crlf
/src/CodeAlta.Hosting/ConfiguredModelProviderRegistryBuilder.cs text eol=crlf
/src/CodeAlta.Hosting/ConfiguredProviderInspection.cs text eol=lf
/src/CodeAlta.LiveTool/AltaReminderService.cs text eol=crlf
/src/CodeAlta.LiveTool/AltaServiceCollection.cs text eol=lf
/src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs text eol=crlf
/src/CodeAlta.Orchestration/Hosting/CodeAltaHost.cs text eol=crlf
/src/CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs text eol=lf
/src/CodeAlta.Tests/ArchitectureGuardrailTests.cs text eol=crlf
/src/CodeAlta.Tests/CodeAltaFrontendCleanupSourceTests.cs text eol=lf
/src/CodeAlta.Tests/CodeAltaOwnedServicesLifetimeTests.cs text eol=lf
/src/CodeAlta.Tests/DeferredCodeAltaAppSourceTests.cs text eol=lf
/src/CodeAlta.Tests/FileEditorWorkspaceCleanupSourceTests.cs text eol=lf
/src/CodeAlta.Tests/ModelsDevCatalogLifetimeSourceTests.cs text eol=lf
/src/CodeAlta.Tests/ModelsDevCatalogLifetimeTests.cs text eol=lf
/src/CodeAlta.Tests/ModelsDevCatalogTests.cs text eol=crlf
/src/CodeAlta.Tests/PromptDraftPrerequisiteSourceTests.cs text eol=lf
/src/CodeAlta.Tests/RuntimeEventPumpSourceTests.cs text eol=lf
/src/CodeAlta.Tui/App/CodeAltaApp.cs text eol=crlf
/src/CodeAlta.Tui/App/CodeAltaFrontendComposition.cs text eol=crlf
/src/CodeAlta.Tui/App/CodeAltaOwnedServices.cs text eol=crlf
/src/CodeAlta.Tui/App/CodeAltaShellController.cs text eol=crlf
/src/CodeAlta.Tui/App/ProviderFrontendCoordinator.cs text eol=crlf
/src/CodeAlta.Tui/App/ReminderUiCoordinator.cs text eol=lf
/src/CodeAlta.Tui/App/RuntimeEventPump.cs text eol=crlf
/src/CodeAlta.Tui/App/SessionPromptDraftPersistenceCoordinator.cs text eol=lf
/src/CodeAlta.Tui/App/ShellFrontendHost.cs text eol=crlf
/src/CodeAlta.Tui/Program.cs text eol=crlf
/src/CodeAlta.Tui/Views/DeferredCodeAltaApp.cs text eol=crlf
/src/CodeAlta.Tui/Views/FileEditorWorkspaceCoordinator.cs text eol=crlf
```

Only12 existing checkouts may be re-rendered, to their exact historical LF conventions: `ConfiguredProviderInspection`, `AltaReminderLifetimeSourceTests`, `CodeAltaFrontendCleanupSourceTests`, `CodeAltaOwnedServicesLifetimeTests`, `DeferredCodeAltaAppSourceTests`, `FileEditorWorkspaceCleanupSourceTests`, `ModelsDevCatalogLifetimeSourceTests`, `ModelsDevCatalogLifetimeTests`, `PromptDraftPrerequisiteSourceTests`, `RuntimeEventPumpSourceTests`, `ReminderUiCoordinator`, and `SessionPromptDraftPersistenceCoordinator` at the explicit paths above. No rewrite of the other19 paths. Each candidate must be solely ordinal CRLF→LF and invert to every pre-write byte; fresh HEAD/index equality, unchanged normalized content, strict encoding, actual filter/encoding attributes and expected candidate hashes are required before a named checkout-index refresh. Stop on any drift, staged user change, unexpected filter or inability to preserve attributes EOF. No blanket normalization, reset, add --renormalize or config change.

Same sole child may implement only that attributes append and those12 verified checkout representations, then stop for parent audit. Use apply_patch for the attributes edit; the bounded Git checkout operation is only for already-verified content-identical representations. No builds/tests/discovery, C# execution, source/fixture semantic edits, historical hash rebasing, extra child, reminder changes, artifacts/HOME/cache reads, staging/commits or compaction. Parent retains docs, actual-byte/whole-inverse review, explicit filtered verification and scoped integration. Source/architecture/error/lifetime policies and App47026-byte budget remain unchanged. The broader proposed test inventory is not execution-authorized by this five-method regression run. M2–M7 remain open.

#### Checkout correction completed and parent verification

This supersedes the pending correction above, against `f4608798`; it does not change any accepted production or fixture contract. The child stopped correctly when apply_patch appended a terminal CRLF: actual attributes were2297 bytes (`F2A59003245F5720B6D53A9E66FD4E1D6F4A2763AA3EBE4799CC184B4E91506B`), exactly the approved candidate plus two bytes. Parent independently preserved the159-byte prefix, removed only those two bytes under an exclusive handle, and staged only attributes. The approved actual file is **2295 bytes/36 CRLF separators/no final newline**, SHA256 `0BAC76259FE9DA3E2DFADF3E9FA5B6965397F4778C49E970095BFB124C713543`; the2136-byte append hashes to `A50817EB274B8F5CFED718C96746494D16E84BA2C0A0B1537C60516B33C0809B`.

Tool failures are retained, not presented as successful correction: the first EOF inspection failed on PowerShell's generic SequenceEqual object-array overload before writing. Two exact twelve-path `git checkout-index --force` attempts exited0 but failed the actual-byte gate at `ConfiguredProviderInspection.cs`, before and after staging attributes. Old indexed attributes were therefore **not an established sufficient explanation**; no root cause is asserted. Parent recorded a narrow fallback in the plan before directly rendering the same twelve approved LF byte candidates. All originals were retained, all twelve exclusive handles acquired and rechecked before any write, then actual bytes and complete LF→original-CRLF inverses checked. Nineteen other rule targets remained byte-identical; no broad checkout/reset/add/renormalization, Git config change or additional writer was used.

Final independent audit passed all31 strict UTF-8/no-BOM/final-newline/ordinal round trips, intended13 LF/18 CRLF representations,248 effective+cached attribute values and62 named binary HEAD/index source comparisons. Normalized staged attributes equal the exact candidate; the original prefix reconstructs the old named HEAD blob. Sources have **no Git content diff**, including C#/XML, hashes, literals, inverse helpers, scheduling allowances and architecture exemptions. Git status can still report checkout-only stat/representation changes for the twelve files; this is not a staged semantic delta. App remains47026 bytes, strictly below47064, with frozen `510AD5B44C683610FFEDD57B59A30FC3DC6A345F358B76595B9563E4F7E14177`; development guide84358 bytes/`084B575D0DB8329078EE2FF8DE34F3A7B93A428F2117B0A5CCE1FF46EE268926` and search-session11953 bytes/`4A2C24DC1C6E11D259E6E033FEB2F9519D4AC35FD27F948CF2DD00C5533193AA` are unchanged.

Actual final LF values below are **accepted-current checkout bytes**, not Git-normalized evidence substituted for checkout checks or pre-reminder original hashes. Full historical catalog/reminder/workspace inverses remain in the unchanged mandatory source guards and passed after recompilation.

| Path relative to `src/` | Actual LF bytes | SHA256 |
| --- | ---: | --- |
| `CodeAlta.Hosting/ConfiguredProviderInspection.cs` | 13770 | `3BD3881A8AF8178C708EAA695D5DB3212D0D206F1C984A4E5990F61E6791A8AC` |
| `CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs` | 54692 | `F65FA0B9652FC4F2BE92C89386660BEEF3E4FC1933E65162D823FCAF94B292FD` |
| `CodeAlta.Tests/CodeAltaFrontendCleanupSourceTests.cs` | 21479 | `B876880F4867472EFCC03BC27EB8C49C87CDE7F637411706FBCE160B2BB81FEF` |
| `CodeAlta.Tests/CodeAltaOwnedServicesLifetimeTests.cs` | 41906 | `78A7B045DD137F3ABC1EA5AB2A5222F33AD668300B2915C12F99DCD29065620C` |
| `CodeAlta.Tests/DeferredCodeAltaAppSourceTests.cs` | 28073 | `FF1F2C40CA27A4E0462AE034817A70986A74742CF4A379E4D0BB5ABC8C8A6793` |
| `CodeAlta.Tests/FileEditorWorkspaceCleanupSourceTests.cs` | 63926 | `F8FA8629783572476E570FE65191A9DAFBDBC94AFAA63E121D93FD63A166A62D` |
| `CodeAlta.Tests/ModelsDevCatalogLifetimeSourceTests.cs` | 34690 | `3FB9E366E5ED952C347ABF22B9EED0E37B6A83F1B1487A2FF4171E1AA9433CC9` |
| `CodeAlta.Tests/ModelsDevCatalogLifetimeTests.cs` | 20018 | `DE9F5E136924E713467D80C998390F9EEF6FE4853E72C65D1146D32D5DBE6345` |
| `CodeAlta.Tests/PromptDraftPrerequisiteSourceTests.cs` | 57070 | `1C8AE92FA92CB7C8250450F4DC29A9DE04199572721742208F9B1B5C3CEF3609` |
| `CodeAlta.Tests/RuntimeEventPumpSourceTests.cs` | 27444 | `94DAF88C49D933909A332D0DB0FFA341370CCD996BCEBC7501F7756C28BFF46E` |
| `CodeAlta.Tui/App/ReminderUiCoordinator.cs` | 2576 | `71DEB269F18D4E5C9F034E4CB9366E0562B2BE2DC3D7D762C2726E68EDF9C486` |
| `CodeAlta.Tui/App/SessionPromptDraftPersistenceCoordinator.cs` | 13375 | `E79370DFDB435E537718188BFE78C5821A5F2EDFB067086DD60077A3BA6A2482` |

Parent rebuilt `CodeAlta.Tests` in Release with `--no-restore` to replace CR-bearing compiled raw constants: **0 warnings/errors**. The same five exact catalog source operands above now **pass5/5**, following the observed pre-fix5/5 failures. Before additional execution parent audited affected source-method bodies, fixed read maps and complete helper paths, and re-read all423 catalog inert fixture lines plus the mandatory separate fieldless/static-initializer-free production core. A combined inspection output was truncated; narrower reads supplied the missing executable ranges before selection. Raw production strings are data, not construction. Only two source methods were selected from mixed `CodeAltaOwnedServicesLifetimeTests`; its runtime tests were not inferred safe.

Additional source selection used exactly `FullyQualifiedName=CodeAlta.Tests.<Fixture>.<Method>` for every method below, joined by `|` (31 operands/cases, **31 passed**):

| Fixture | Exact method names |
| --- | --- |
| `AltaReminderLifetimeSourceTests` | `Admission_RetainsBeforeOutsideGateInvocation`; `Publication_PublishesTheAssignedOriginal`; `Pruning_RequiresCompletedOriginalAndRetainsFirstHistory`; `Stop_SnapshotsUnderGateAndDisablesPruning`; `SharedDisposal_UsesOneInstanceLazy`; `Cancellation_ClaimsOnceAndFencesWorkerRelease`; `MutationsAndWorkerChecks_PreserveTheScopedContract`; `StaticCores_UseAcceptedValidationAndErrorPolicies`; `Ownership_TransfersToShellAndUsesBestEffortCleanup`; `Preservation_InvertsOnlyApprovedProductionAndGuardChanges`; `Preservation_UnchangedRuntimeAndFrontendRoutesRemainFrozen` |
| `FileEditorWorkspaceCleanupSourceTests` | `WorkspaceCleanup_SourceWiring_UsesMandatoryCore`; `WorkspaceCleanup_SourceCore_ContainsFailuresInOriginalOrder`; `WorkspaceCleanup_Source_PreservesCompleteOriginalOutsideDisposal`; `WorkspaceCleanup_Source_PreservesPickerAndSearchLifetimeBoundaries`; `WorkspaceCleanup_Source_PreservesTabCloseAndDurableSaveBoundaries`; `WorkspaceCleanup_Source_PreservesQueuedWorkAndObservationBoundaries` |
| `CodeAltaFrontendCleanupSourceTests` | `FrontendCleanup_SourceWiring_UsesMandatoryOrderedCore`; `FrontendCleanup_SourceCore_ContainsEveryStageFailure`; `FrontendOwnership_SourceWiring_PreservesShellAndDeferredBoundaries` |
| `DeferredCodeAltaAppSourceTests` | `DeferredStartup_SourceWiring_UsesLinkedOneShotAdmission`; `DeferredDisposal_SourceWiring_UsesCachedJoinAndExclusiveOwner` |
| `PromptDraftPrerequisiteSourceTests` | `PromptDraftPrerequisites_SourceWiring_UsesMandatoryProductionCore`; `PromptDraftPrerequisites_SourceCore_JoinsPreviousAfterDelayFailure`; `PromptDraftPersistence_Source_PreservesAcknowledgementAndRelease`; `PromptDraftPersistence_Source_PreservesFrontendAndStorageRouting` |
| `RuntimeEventPumpSourceTests` | `PumpCleanup_SourceWiring_UsesMandatoryCore`; `PumpCleanup_SourceCore_ContainsFailuresWithoutChangingJoinPolicy`; `PumpOwnership_SourceWiring_PreservesStartStreamAndFrontendBoundaries` |
| `CodeAltaOwnedServicesLifetimeTests` | `OwnedServicesOwner_SourceWiring_RetainsHostAndRemovesParallelDisposal`; `OwnedServicesCreation_SourceWiring_TracksAcquisitionsAndAwaitsNamedRollback` |

The seven exact catalog inert operands already recorded under catalog selected-refresh integration were run separately: **26 passed**, including all25 DataRows and the selected-only case. Retained actual originals/gates/core/observer/bound/aggregate tasks, unconditional finally gate release, concurrent independent five-second observations before aggregates, second finite snapshot, permanent timeout failure and separate original fault/cancellation observation were re-audited. No actual service/CTS/registration/client/owner/UI/State/provider/store/logger/host acquisition; no test-side admission/history/Lazy model. Total post-fix evidence is **62 passed/0 failed/0 skipped**, from43 exact methods, not a whole-class/suite result. `ModelsDevCatalogTests` and Catalog `ProjectFileSearchServiceTests` remain noninert/excluded; no cache polling gap was changed.

Parent solution Release build with `--no-restore` also passed0 warnings/errors. Local logs are `%TEMP%/codealta-parent-eol-phase-b-{build,catalog-source-tests,source-regressions,catalog-inert-tests,solution-build}-20260907-01.log`; tests used the exact filters above with `-c Release --no-build --no-restore`. No lifetime/production policy changed, and no new native, default-startup, broad host/provider/auth/controller/runtime or installed-BCL qualification is claimed. Source/Git/logging reads remain nonzero I/O. Protected artifacts stay unread, no push/publish/merge or compaction; file-search's five policy decisions and M2–M7 remain open.

Site `lunet build` also passed111 files/1,031,237 bytes; configured theme download/install was observed, not network-free. Log `%TEMP%/codealta-parent-eol-phase-b-site-20260907-01.log`. No public runtime behavior changed, so public shutdown guidance and the frozen development guide require no edits in this correction.

#### Next bounded file-search design step

Checkout prerequisite integrated in `7429ffe1`. Parent confirmed the existing sole child idle/queue0/children0 and the coordinator reminder active; no recovery writer is needed. The next assignment is read-only policy/design, not implementation: recommend a coherent decision for each of the five file-search questions above, identify preserved versus deliberately changed timing/error/ownership behavior and the smallest necessary picker/popup caller scope. Account for initial synchronous rank/publication failures, every admitted original, competing cancellation traversals/source release and ranking terminal failures/restart; clearly exclude hidden acquisitions, queued UI and borrowed dependency disposal rather than claiming complete lifetime coverage. Return source anchors and remaining decision blockers, not code maps or a new fixture inventory. Parent audit/contract acceptance is required before any further fixture or production authorization. Existing62-case/build/site results qualify only the completed checkout correction; M2–M7 and file-search implementation remain open. All protected artifacts and accepted byte/inverse/architecture contracts remain preserved; no compaction or additional child.

### M2 reminder observer-isolation checkpoint

The bounded feedback slice is accepted. `AltaReminderService` keeps its existing CRUD signatures as forwarding wrappers and adds per-operation diagnostic overloads plus `GetLastNotificationFailure()`. Mutation context is captured under the instance gate; each notification pass captures its subscriber delegate once and attempts every callback individually outside ownership. Observer exceptions, including cancellation, cannot retract committed mutations, replace delivery fields or stop repeat scheduling. Timer/dispatch cancellation and captured-firing semantics remain unchanged.

`AltaReminderNotificationFailure` owns immutable scalar reminder/session/change/firing context, at most eight messages of 512 characters each, the total failure count and truncation flag. Throwing message getters use a constant fallback. One latest failed-pass publication is retained per service, including after deletion; successful passes do not erase it. Publication is gate-owned, independent of mutation ordering, never restores entry metadata and never emits another event. Reentrant edits/deletions remain effective. This is bounded query-based feedback, not reliable push, ordered replay, persistence or diagnostic history.

Actual live-tool create/delete preserve success records and exit codes then emit `reminder.notificationFailed` warnings; list can emit a clearly historical, explicitly target-filtered warning without changing reminder counts or delivery status. TUI CRUD uses the diagnostic overloads, escapes warning markup and separates post-commit reload/direct-refresh failures from mutation errors. Open/manual refresh displays matching-session retained context even for deleted/completed reminders. Blocking/infinite reentry, later posted-UI exceptions, failed status presenters, output truncation and overwritten diagnostics remain explicit limits.

Parent inspected the actual unchanged-service runtime-red (five failures: skipped subscriber, cancellation escaping committed create/edit/delete, and a missing second timer after first-firing metadata committed; eight existing tests passed). Parent reviewed the complete implementation/docs and independently passed clean Release build (zero warnings/errors), **31 focused tests** (19 service, seven protocol, five presentation), **530 audited TUI tests**, **132 Catalog tests plus one Unix-only skip**, Lunet (111 files; configured theme download) and diff checks. TUI adds only `AltaReminderProtocolTests.` and `ReminderPresentationFeedbackTests.` to the prior 43 operands; Catalog keeps nine operands. Parent logs: `%TEMP%/codealta-parent-reminder-observers-{build,focused,tui,catalog,site}.log`; baseline logs include exact filters. Child red/green evidence: `%TEMP%/codealta-m2-reminder-observers-{red-build,red-test,green-build,green-test,site}.log`.

Fixture audit: service tests extend the manual-clock/custom-contributor in-memory route and cover bounds/immutability, all-subscriber membership, cross-thread access/reentry, per-pass results, publication ordering, deletion/no resurrection, repeat continuity and preserved delivery failure. Protocol list/delete tests use a preregistered service, nonadvancing clock and built-in-only registry; no plugin/default contributors or runtime/catalog startup. Actual create is covered only by the production output helper, service and an exact source-wiring check, not an unsafe runtime fixture. Presentation tests exercise the production-used helper without constructing visuals. Test-assembly fallback logging remains active. Full dialog/queued-UI behavior, runner joining, whole-suite profile isolation, shared-host lifetime, runtime admission, persistence/reconnect, M2/P08 and native/platform qualification remain open.

### M2 owner-captured reminder firing checkpoint

Reminders already belonged to neutral LiveTool and targeted explicit session IDs; no migration or new scheduler was needed. `AltaReminderService` now verifies membership/cancellation and captures an immutable descriptor-plus-content snapshot under its existing gate immediately before delivery. Service resolution and the unchanged noncancelable `session send <target> --stdin --queue-if-busy` dispatch operate only on that snapshot outside ownership. Clock/timer callbacks also remain outside the gate; an internal `TimeProvider` overload supports deterministic fixtures while the public constructor retains system time.

Capture authorizes one local attempt, **not runtime admission**. Pre-capture edits affect that firing; post-capture edits affect later firings and survive completion bookkeeping. Pre-capture deletion prevents delivery; post-capture deletion prevents repeats/bookkeeping/resurrection but cannot retract the captured send, queued work or a run. Repeats remain fixed-delay after delivery returns, failed results count toward the repeat limit, and completions remain listed until deleted. Successful dispatcher output does not establish provider success or exactly-once execution.

Parent inspected the clean seam-only build and the actual old-route runtime-red: editing content in a controlled `IServiceProvider` dispatcher-resolution callback produced `later text` instead of `original text`. This is a retained executable regression against the real service route, not an API compile-red or mapped model. Parent then reviewed the full source/fixture/docs diff and independently passed a clean Release build (zero warnings/errors), **8 focused tests**, **507 audited TUI tests**, **132 Catalog tests plus one Unix-only skip**, Lunet (111 files; configured theme download) and diff checks. TUI adds only `FullyQualifiedName~CodeAlta.Tests.AltaReminderServiceTests.` to the previous 42-operand filter; Catalog's nine operands are unchanged. Parent logs are `%TEMP%/codealta-parent-reminder-firings-{build,focused,tui,catalog,site}.log`, with exact baseline filters in the test logs; child evidence is `%TEMP%/codealta-m2-reminder-firings-{red-build,red-test,green-build,green-test,site}.log`.

The eight new fixtures use a manually advanced one-shot clock, bounded waits, controlled in-memory delivery and a custom-contributor-only dispatcher; unexpected service resolution throws. They cover edit/delete capture boundaries, identity/flags/noncancelable token, cross-thread access during dispatcher resolution, later-edit retention, nonoverlap, completion-relative due time and failure counting/retention. No runtime/catalog/default contributors/provider/plugin/MCP/native/filesystem discovery starts. Existing test-assembly fallback logging still runs. Cleanup deletes remaining reminders and disposes timers, but does not prove runner joining: the service has no task-lifetime API. Observer exception/cancellation isolation, timer shutdown/join, lazy registration concurrency, retained-history bounds, busy/admission races, persistence/reconnect and full M2/P08/shared-host/native qualification remain open.

### M2 claim-guarded ask response settlement checkpoint

The bounded response slice is accepted on Windows after parent review and independent verification. `AltaAskService.RespondAsync` claims an owner-issued handle under its existing lock before notifications, formatting, history or dispatch. Exact Pending cancellation competes at the same ownership boundary; duplicate/stale/foreign/non-head submissions never dispatch. Immutable snapshots retain earlier state. Only private settlement may remove a claimed head; ordinary key removal cannot bypass Submitting/Indeterminate state. Callbacks and awaits remain outside the lock.

The actual TUI ask-only route uses Orchestration `SessionPromptResponseDispatch`: pre-invocation interception/failure means definitely not admitted **by this route** and rotates the handle; recognized Submitted plus nonblank returned run ID provides positive **late** evidence; exceptions/cancellation or malformed/unknown results after runtime entry remain indeterminate. Plugin side effects are not undone. Positive evidence is latched before UI projection and survives later feedback failure. Plugin replacement preserves AskId and images; uncertain responses never enter ordinary composer-restoration handling.

Parent-required presentation fixes reconcile only obsolete still-active handles (including save-dialog prechecks), preserve in-flight duplicate ownership, and scope warnings to the selected session. Synthetic busy cleanup requires the same dispatch revision/timestamp and no observed runtime run ID; it cannot clear another run or invent Idle/Abort. Foreground/focus/timeline operations are not fully attempt-owned transactions, and observed runtime projections can remain stale when lifecycle events are missing.

Verification: parent clean Release solution build (zero warnings/errors), **47 focused tests**, **499 audited TUI tests**, **132 Catalog tests plus one Unix-only skip**, Lunet build (111 files, configured theme download) and `git diff --check` passed. TUI baseline retains its 40 explicit operands and adds only `AltaAskResponseTests` and `AskResponsePresentationPolicyTests`; Catalog retains nine operands. Parent logs: `%TEMP%/codealta-parent-response-{build,focused,tui,catalog,site}.log`; baseline test logs include exact filters. Child logs: `%TEMP%/codealta-ask-response-{red-build,red,foreground-red-build,foreground-red,build,focused,tui,catalog,site}.log`. Parent inspected the lost-AskId runtime regression and two actual presentation-policy foreground failures. Three mapped legacy-path failures demonstrate old duplicate/cancel/interception behavior but are **not** full-coordinator runtime regressions. New owner/presentation fixtures use in-memory services, controlled delegates/TCS and scalar DTOs; plugin tests exercise the static transformation, not plugin execution. Guardrails verify actual wiring/catch-path separation without relaxing budgets or adding exemptions.

No early receipt, provider-success guarantee, general prompt deduplication, execution-task lifetime, persistence/reconnect or recovery/abandon operation is added. Runtime send still spans provider execution and late bookkeeping. An unresolved head intentionally blocks resubmission, local cancellation and FIFO progress rather than inviting duplicate sends; restart loses this in-memory state instead of recovering it. Full M2/P07/P08, isolated whole-suite, desktop/native and interactive qualification remain open.

### M2 immutable pending-ask queue checkpoint

`AltaAskService` remains the neutral, instance-owned LiveTool FIFO owner. Admission captures nested question/choice collections into read-only snapshots, preserving scalar values/order/caller identity. `GetPending(sessionId)` supplies immutable point-in-time FIFO state without a tab. `TryRemoveHead(sessionId, askId)` replaces unqualified dequeue; ordinal, untrimmed exact-head keys must match or the mutation returns `Accepted = false` without changing state/notifying. Admission alone retains existing session trimming. Actual TUI submit/cancel passes captured keys, only announces successful cancellation for accepted removal, and reconciles only its own active presentation. Prompt dispatch itself is unchanged.

Cancellation is rechecked under the queue lock after snapshot capture; observed pre-admission cancellation prevents enqueue/event, whereas post-check cancellation does not revoke admission. `QueueChanged` callbacks run outside the lock, may reenter/interleave, and invalidate snapshots rather than replay authoritative state. Every subscriber is attempted; immutable string `NotificationErrors` accompany committed outcomes. No queue logger/diagnostic sink/exception graph is introduced. The live command keeps `alta.ask.queued` and yield/identity fields and adds notification warnings; TUI uses localized status. Later output/status failures cannot undo a committed mutation, and asynchronously posted UI failures are not observed by this synchronous notification result.

Parent source review covered pure queue fixtures, the no-runtime/no-catalog command fallback and three exact legacy/new command cases with file-free payloads and in-memory registry/ask services. No default profile/provider/plugin/native startup was invoked. Parent inspected **2 original-service runtime-red failures** for mutation/read-only ownership, **3 mapped-legacy runtime-red failures** for outer/nested mutation and wrong-key removal, and **1 separate stale-removal failure**; new API compile-red is recorded separately, not counted as runtime evidence. Temporary legacy mappings were removed. Regression logs are temporary `codealta-child-ask-{red-tests,compat-red-tests,stale-red-tests}.log`; API compile-red is `codealta-child-ask-api-red-build.log`. Final coverage includes deterministic cancellation during snapshot capture, cross-thread callback requery/reentrant removal, all-subscriber failure summaries, FIFO and immutable snapshots, wrong/stale/non-head keys, and actual command warning/protocol behavior. TUI wiring coverage is source-only (`AskMode_RemovesOnlyCapturedHeadAndReconcilesStalePresentation`), not an executed coordinator lifecycle fixture.

Parent independently passed Release solution build (`--no-restore`, **0 warnings/errors**), **16 focused tests**, then **464 TUI/runtime tests** and **132 Catalog tests with 1 existing Unix-only skip**, using only `-c Release --no-build --no-restore --filter`. The TUI filter retains the durable-notes checkpoint's 36 operands and adds `FullyQualifiedName~CodeAlta.Tests.AltaAskQueueTests` plus three exact `FullyQualifiedName=CodeAlta.Tests.AltaLiveToolTests.` operands: `AltaAskService_QueuesFifoPerSessionAndRaisesQueueChangedEvents`, `AskCommand_QueuesForCallerSessionAndReturnsYieldGuidance`, and `AskCommand_NotificationFailureKeepsQueuedProtocolAndCapturedIdentity` (40 total). The focused filter contains those four additions plus exact `FullyQualifiedName=CodeAlta.Tests.ArchitectureGuardrailTests.AskMode_RemovesOnlyCapturedHeadAndReconcilesStalePresentation`. Catalog retains its nine operands unchanged. Parent logs: temporary `codealta-parent-ask-{build,focused,tui,catalog,site}.log`, with exact filters in test logs. Parent site build passed (111 files; configured theme download/install, not network-free), as did diff checks. The child's unchanged 36-operand baseline had 449 passes including the new guardrail; the parent's 464 additionally cover the dedicated queue and exact command fixtures.

This checkpoint completed only bounded M2 pending-queue authority. At that checkpoint the TUI send path could complete normally after plugin interception/cancellation or caught failures; exact removal was not proof of runtime admission. The subsequent claim-guarded settlement checkpoint above replaces that response behavior, but still does not establish general exactly-once execution, lifecycle/reconnect or restart persistence. Overall P07/P08 and M2 remain open.

### M2 durable notes checkpoint

The actual TUI/live-tool route now uses `RuntimeAltaNotesService` and `SessionRuntimeService` with canonical journal storage instead of `SessionAltaNotesService` and open-tab authority. Backend IDs/provider identity resolve without starting providers or prompt/skill discovery. An explicit source never falls back from an unknown ID; host fallback captures only the source ID before stdin/storage awaits. Reads stream the canonical parser, retaining only the latest notes event in journal order, including exact empty text and clear. Sidebar construction takes a snapshot without synchronous storage; matching open-tab/sidebar updates remain presentation-only.

The existing per-journal gate orders append, metadata refresh, catalog invalidation and feedback. Strict canonical validation on the same read/write handle precedes byte mutation. A valid final record without a newline receives a separator without changing existing bytes; malformed/truncated content is refused, not repaired or discarded. BOM-detected non-UTF-8 journals remain readable but cannot receive a UTF-8 notes append. Cancellation applies before write admission; an admitted single record finishes without caller cancellation. `AgentNotesCommittedException` explicitly reports cache/observer failure after persistence. Metadata refresh avoids reacquiring the gate, and the production TUI subscriber always enqueues to prevent inline notification overtaking. Callbacks remain contractually non-reentrant: synchronous storage reentry can deadlock and is not dynamically prevented.

Parent inspected the old closed-tab runtime-red (`codealta-notes-red-test.log`) and five pre-correction append-tail failures (`codealta-notes-tail-red-test.log`). Final fixtures also cover genuinely truncated JSON prefixes with/without newlines, encoding refusal, mixed journal events, restart/clear, competing adapters, independent sessions, caller capture, unknown/rooted project identity, waiting/pre-admission cancellation, actual command refusal without success/Changed, and explicit committed failure. Fixtures use owned temporary roots, throwing provider factories/discovery seams, and in-memory presentation; no default app/provider/plugin/native startup. The production-route guardrail is source-only, not an interactive TUI qualification.

Parent independently passed Release solution build (`--no-restore`, **0 warnings/errors**), **28 focused TUI/live-tool tests** and **14 focused Catalog tests**, then **448 TUI/runtime tests** and **132 Catalog tests with 1 existing Unix-only skip**. All tests used `-c Release --no-build --no-restore --filter`. The broader TUI filter retains the immediate-user-input checkpoint's exact 31 operands, adding `FullyQualifiedName~CodeAlta.Tests.SessionNotesServiceTests` and four exact `FullyQualifiedName=CodeAlta.Tests.` operands: `AltaLiveToolTests.NotesCommand_SetGetClearRoundTripsMarkdown`, `AltaLiveToolTests.AltaNotesService_ReplacesClearsAndRaisesChangedEventsPerSession`, `CodeAltaAppSidebarTests.SidebarView_NotesGroupUsesSingleScrollingHostAndUpdatesMarkdown`, and `CodeAltaAppSidebarTests.SidebarView_ClearNotesButtonDoesNotBlockOnAsyncClear` (36 operands total). Catalog adds only `FullyQualifiedName~CodeAlta.Catalog.Tests.SessionNotesStoreTests` to the established eight operands. Focused TUI uses these five additions plus exact `ArchitectureGuardrailTests.NotesComposition_UsesDurableAdapterAndKeepsOnlyPresentationInTui`; focused Catalog uses the notes store class only. Parent logs are temporary `codealta-parent-notes-{build,focused-tui,focused-catalog,tui,catalog,site}.log`; combined logs retain exact filters. Parent Lunet build passed (111 files, configured theme download/install; not network-free), as did diff checks.

This is a bounded M2/P08 ownership improvement, not desktop parity. Validation is a linear bounded-memory journal scan, not general history paging. Partial filesystem I/O/power-loss rollback, cross-process ordering, history-load/loss races, renderer grants/reconnect, loop-stop/shutdown transactions and asks/reminders durability remain unqualified or separate work. Callback non-reentrancy is a documented limitation, not a claim that arbitrary subscribers cannot deadlock.

### M2 immediate user-input policy checkpoint

Orchestration `SessionUserInputPolicy.CreateResponse` now owns the existing pure immediate-answer algorithm; the actual `SessionUserInputRequestCoordinator` calls it and the superseded TUI `ChatPromptResponseBuilder` is removed. Parent independently compared the public method through the final closing brace with the original Git version: identical implementation. Captured setting/session association, entry cancellation, open-tab timeline presentation and immediate completion are unchanged. Current substring scores/question heuristics, first-tie selection, literal labels, option precedence over secret/freeform flags, no-option defaults, ordinal identifiers and duplicate/null validation behavior are deliberately preserved, not redesigned.

Child characterization ran **42 passes before and after** the move: 39 dedicated pure cases plus three exact legacy methods. There is no bug-fix runtime-red claim. Parent audited all new cases, the three legacy bodies/class hooks and source guardrail; fixtures use Agent records/collections only, with no filesystem/provider/host/native setup. The actual factory callback fixture remains in the audited baseline. One source-only architecture assertion verifies the actual adapter and removal of the old helper; no exemptions or budgets changed.

Parent independent Release solution build (`--no-restore`) passed with **0 warnings/errors**. Focused tests passed **166**: `FullyQualifiedName~CodeAlta.Tests.SessionUserInputPolicyTests`, exact `FullyQualifiedName=CodeAlta.Tests.CodeAltaAppTests.` methods `CreateChatUserInputResponse_WhenAutoApproveEnabled_SelectsDefaultAnswers`, `CreateChatUserInputResponse_WhenChoicesIncludePositiveAndNegativeOptions_PrefersProceeding`, `CreateChatUserInputResponse_WhenAutoApproveDisabled_ReturnsEmptyAnswers`, plus `FullyQualifiedName~CodeAlta.Tests.SessionExecutionOptionsFactoryTests` and `FullyQualifiedName~CodeAlta.Tests.ArchitectureGuardrailTests`, joined with `|`. Broader TUI adds the dedicated policy class and three exact legacy operands to the permission checkpoint's 27 operands: **420 passes** (31 operands). Catalog retains the same eight operands: **118 passes, 1 Unix-only skip**. All tests used Release `--no-build --no-restore`. Parent site build (111 files, configured theme download/install) and diff checks passed; no end-user content changed. Temporary parent logs: `codealta-parent-userinput-{build,focused,tui,catalog,site}.log`; the TUI log contains the full expanded filter. Child before/after evidence remains under temporary `codealta-m2-userinput-child-20260906`.

This ownership-only extraction introduces no pending question UI, shared `alta ask`, reconnect/recovery, renderer authorization or lifecycle guarantees. Notes/other interactions, shared composition/startup, M2 and desktop parity remain open.

### M2 shared permission checkpoint

Orchestration `SessionPermissionService`, owned by `SessionRuntimeService.Permissions`, now owns AutoApprove and pending permission completions independently of rendered tabs and lossy runtime events. Its mailbox serializes register/list/resolve/cancel, while decision waits remain outside the runner. Immutable scalar summaries carry session, nullable provider run, interaction and fresh attempt identities. Wrong identities, reused provider IDs, replay and invalid decisions cannot resolve a different attempt. Completed entries are removed without tombstones. Caller-token checks in reads/resolution reject already-canceled requests even before asynchronous cleanup runs; cancellation after an accepted decision does not revoke it. Existing AutoApprove defaults, four TUI decisions and explicit provider-session precedence over captured fallback remain intact.

The actual TUI command coordinator injects the runtime-owned service. The permission adapter rechecks pending state inside queued show work, joins closure, and cancels on presentation failure. Dialog buttons/Escape resolve scoped handles rather than completion sources; presentation disposal alone does not decide a permission. Runtime disposal cancels owner completions before joining session actors/runs. A new source-only ownership/order guardrail adds no exemption or budget; it is not an executed whole-runtime shutdown test.

Child runtime-red reproduced one canceled request leaving **1 stale modal instead of 0**, before implementation (`permission-runtime-red.log`). Parent review identified cancellation-before-cleanup ordering: the late-Allow regression was added after its token-check correction, so no pre-fix runtime-red is claimed for resolution. A subsequent deterministic cancellation-callback-order fixture reproduced **1 read-contract failure** (`permission-cancellation-read-red.log`) before excluding canceled entries from List/IsPending. An intermediate in-memory fixture run timed out on an unpumped terminal synchronization context; the fixture now explicitly clears/restores that context and uses a controlled dispatcher. Another interim cancellation fixture suppressed timeline work; the adapter now retains direct rendering when already on the UI dispatcher. These are recorded fixture corrections, not unexplained successful retries.

Parent independently reviewed the owner, actual adapters/disposal wiring, all dedicated fixtures and docs. Release solution build (`--no-restore`) passed with **0 warnings/errors**. Focused filter comprises `FullyQualifiedName~CodeAlta.Tests.` plus `SessionPermissionServiceTests`, `SessionPermissionRequestCoordinatorTests`, `PermissionApprovalDialogTests` and `SessionExecutionOptionsFactoryTests`, joined with `|`: **42 passes**. Broader TUI selection adds the first three class operands to the portable-options checkpoint's exact 24 operands: **377 passes**. Catalog retains the same eight operands: **118 passes, 1 Unix-only skip**. Tests used Release `--no-build --no-restore`. Parent site build (111 files, configured theme download/install) and diff checks passed. Parent temporary logs: `codealta-parent-permission-{build,focused,tui,catalog,site}.log`; the TUI log records its full expanded filter.

New tests use pure mailboxes, explicitly rooted constructor-only catalogs, disposable in-memory terminals and deterministic joined dispatchers, not providers, default app startup, discovery, credentials or native GUI. Coverage includes independent sessions/runs, cancellation before/during registration and before cleanup, resolution races, attempt reuse/replay, immutable summaries, actual buttons/Escape and stale buttons, closure on cancellation/disposal, and failure before/after show. Summaries deliberately omit mutable/raw provider payloads: they are trusted backend previews, not renderer grants, full replay or durable restart recovery. Queued presentation/closure still requires a serviced UI dispatcher; the existing frontend-host terminal-loop stop/quit ordering remains unqualified. Immediate user-input behavior is unchanged. Shared user-input/asks, renderer reattachment, general host lifecycle and M2/P07/desktop parity remain incomplete.

### M2 portable execution-options checkpoint

Orchestration `SessionExecutionPolicy` now captures an immutable `SessionExecutionRequest` from trusted explicit catalog descriptors and provider/model/reasoning/prompt choices, without filesystem/provider/discovery I/O. It copies roots into a read-only collection, resolves existing-session project association by matching ProjectRef, and preserves global-root and missing-project fallback behavior. The actual creation composition/delegation chain carries its captured project to the TUI factory instead of deriving caller identity from selection. Existing send/resume routes use their session's own project lookup. Tool project/cwd/existing-session identity and handler fallback context are frozen with execution options; only deliberate post-creation canonical id binding remains deferred. Tools and UI adapters stay in TUI, with no core dependency on LiveTool.

Child red evidence: mutable roots and execution/tool cwd divergence failed in the original factory (2 failures, 9 passes); new policy contracts were compile-red with 7 missing-policy errors. A later provider-key compatibility regression failed once before preserving the distinction between normalized ProviderId and raw stored fallback ProviderKey. Intermediate fixture assumptions were corrected: an unbound canonical source id is absent, and selection/open helpers were replaced with in-memory setters/EnsureSessionTab to avoid incidental owned UI-state persistence. A baseline guardrail initially failed (323 passes, 1 failure) on a moved source line; only the existing fire-and-forget site's 180→181 reference changed, with no added exemption/budget.

Parent independently reviewed the policy, actual creation/send/resume wiring, callback captures and all new fixture dependencies. Final parent Release build (`--no-restore`) passed with 0 warnings/errors. Focused filter `FullyQualifiedName~CodeAlta.Tests.SessionExecutionOptionsFactoryTests|FullyQualifiedName~CodeAlta.Tests.SessionExecutionPolicyTests` passed **27 tests**. Broader TUI selection adds these two operands to the preceding prompt-mutation checkpoint's exact 22 operands: **351 passes**. Catalog retains the same 8 operands: **118 passes, 1 Unix-only skip**. Tests used Release `--no-build --no-restore`. Parent site build and diff checks passed; configured Lunet theme fetch/install occurred. Parent logs: temporary `codealta-parent-options-{build,focused,tui,catalog,site}.log`.

Pure policy fixtures use owned absolute paths/descriptors only. Factory fixtures use explicit temporary CatalogOptions/cwd, in-memory selection, tool-status-only invocations, canceling dispatchers that suppress native permission dialogs, and captured rendering callbacks. The actual creation-coordinator test changes selection in a status callback but unconditionally stops after options assembly, before runtime creation; its empty provider registry and throwing session/prompt/skill discovery fixtures are constructor-only. This is not end-to-end creation/runtime qualification. Permission callbacks deliberately retain explicit request-session precedence and draft transient fallback; immediate user-input behavior, AutoApprove configuration and interaction lifetimes are unchanged. Trusted policy inputs are not renderer grants or new tool approvals. Pending interaction ownership, shared composition/startup, full-suite isolation and desktop/native parity remain open; only the portable-options substep is accepted.

### M2 live-tool prompt-mutation checkpoint

Actual `alta prompt create/edit` handlers now delegate to `PromptResourceStore`, sharing `sessionCatalog.TextFiles` through the TUI live-tool service collection. Structured creation uses nonoverwriting Missing-revision publication and the existing neutral serializer, extended narrowly to retain optional system name/description metadata. Superseded LiveTool serializers/path-write helpers were removed. Catalog owns portable id/scope/built-in/link validation; command handlers translate usage, conflict and storage failures into JSONL diagnostics without a success mutation record.

Owner-bound `PromptResourceDocument` captures complete decoded text, format and raw-byte revision before stdin is read. Raw edit does not parse/reserialize metadata, so malformed-metadata Unicode files remain repairable and comments/unknown fields/newlines remain literal. Only genuine absence supplies Missing; read/decode failures fail closed. Existing missing-file edit and nonmutating no-content path lookup remain supported. Explicit empty/whitespace `--content` is now literal replacement, consistent with stdin, and cannot be combined with `--stdin`. Success record shapes/defaults are retained; duplicate creation remains Usage, edit conflicts use `prompt.conflict`, and storage errors use `prompt.storageFailed`. Cancellation before commit does not write; cancellation after acknowledged commit retains success.

Child red evidence reproduced UTF-16 BOM loss and stdin-time external clobber in the original route (2 failures). Parent independently reviewed all changed handlers/store/serializer/composition wiring, docs and dedicated fixtures, plus the two changed legacy fixtures and root-resolution source. `FileSystemPromptContentLocator.GetRoots` uses explicit `UserCodeAltaRoot` for prompt paths; these mutation routes do not call skill/instruction/plugin discovery. Final parent verification passed a clean Release solution build (`--no-restore`, 0 warnings/errors), **53 focused mutation tests**, then **324 audited TUI** and **118 Catalog tests with 1 Unix-only skip**. Dedicated mutation filter: `FullyQualifiedName~CodeAlta.Tests.AltaPromptMutationTests|FullyQualifiedName=CodeAlta.Tests.AltaLiveToolTests.PromptEdit_WritesGlobalAgentPromptFromStdin|FullyQualifiedName=CodeAlta.Tests.AltaLiveToolTests.PromptCreate_CreatesCompleteUserAndSystemPromptFiles`. Broad TUI adds exactly those 3 operands to the recovery checkpoint's 19 operands; Catalog retains the recovery checkpoint's 8 operands. Tests used Release `--no-build --no-restore`; guardrails passed unchanged. Parent site build and diff checks passed; Lunet fetched/installed its configured theme. Parent logs: temporary `codealta-parent-prompt-mutation-{build,focused,tui,catalog,site}.log`.

The dedicated registry/dispatcher fixture contains only built-in command registration, owned global/project/cwd paths, CatalogOptions and codec; it does not compose the application, providers, plugins or runtime. Controlled stdin/gates test changes, deletion, competing creation and cancellation without sleeps. Windows link/read-only/locked-file cases passed; other-platform behavior remains unqualified. Revision protection begins with this invocation's captured baseline, not when the caller prepared replacement text: there is no pre-command stale-content protection or new expected-revision flag. Standalone catalog-only calls without an injected codec use an invocation-owned instance. External path/check/commit races remain; no cross-process atomic CAS, sandbox, full-suite safety or desktop parity is claimed. This closes the bounded prompt/skill/config-storage extraction checklist, not M2's remaining runtime policy, interactions, composition and startup ownership.

### M2 configuration-recovery checkpoint

Catalog `ConfigRecoveryService` now captures the trusted absolute root and owns strict decoded snapshots, validation, first-run creation, reload and conditional recovery saves. `CodeAltaConfigStore.EnsureGlobalConfigExists` uses shared-codec missing-revision/nonoverwriting publication; recovery reloads and validates its own or a competing creation before startup admission. The actual `DeferredCodeAltaApp` preflight delegates to this service, with a small storage-only test seam that avoids constructing the application's update-starting constructor. The dialog retains editor/diagnostics/discard confirmation and continuation/exit callbacks, not filesystem writes.

Unreadable/undecodable content never supplies an empty save baseline. Failed reload revokes the old baseline without discarding editor text. Invalid syntax/provider semantics, external changes/deletion and read/create/save failures keep recovery paused; storage failure is separate from syntax validity. Retry uses the same acknowledged revision. There is no forced overwrite: dirty Reload asks before discard, then edits may be reapplied. Reload alone does not continue a recovery dialog; successful Save and Continue does, and Exit never saves. Complete parser-accepted TOML/comments/unknown fields/newlines and all six supported Unicode encoding/BOM combinations are preserved.

Child regression-first evidence: original dialog external-change test failed because it overwrote the file and invoked continuation (1 failure, 2 passes); new Catalog tests were compile-red with 2 missing-service errors. Parent independently reviewed the complete service, actual route, localized feedback and fixture isolation, then passed the Release solution build (`--no-restore`, 0 warnings/errors), **16 focused Catalog** and **18 focused TUI** tests. Exact dedicated filters: `FullyQualifiedName~CodeAlta.Catalog.Tests.ConfigRecoveryServiceTests` and `FullyQualifiedName~CodeAlta.Tests.ConfigRecoveryDialogTests`. Broader audited selections passed **271 TUI** and **115 Catalog with 1 Unix-only skip**: the preceding read-only checkpoint's 18 TUI / 7 Catalog operands plus those respective dedicated operands, joined with `|`. All tests used Release `--no-build --no-restore`; no guardrails or budgets changed. Parent `lunet build` passed, explicitly downloading/installing the configured `lunet-io/templates` theme; diff checks passed. Parent logs: temporary `codealta-parent-recovery-{build,catalog-focused,tui-focused,tui,catalog,site}.log`.

New fixtures only touch unique owned temporary files and use fake continuation/exit callbacks, the actual preflight seam and disposable in-memory terminals. They cover actual save/exit commands, reload confirmation/cancellation, strict decode/read/default-create failure, deterministic shared-gate competing creation, acknowledged retries and Windows read-only failures. No default app, real provider/plugin discovery, profile/credential inspection or full suite ran. Recovery storage remains synchronous in the TUI; existing update/prestarted-plugin/home/admission ownership and general config/provider/plugin mutation are unchanged. External-writer/link final-check/commit races remain; this is not a sandbox, cross-process atomic CAS, power-loss durability or cross-platform/native qualification. M2 and desktop P01/P10 parity remain incomplete; live-tool prompt mutation ownership is next.

### M2 built-in skill read-only workflow checkpoint

The actual skill dialog now dispatches Catalog-resolved `TextFileDocument` contracts through the existing `FileEditorWorkspaceCoordinator.OpenDocumentAsync`. `SkillManagementService.GetFileDocumentAsync` re-discovers the exact skill path under captured roots and matches related resources against the conventional file list; a view-model source claim cannot supply authority. Catalog owns immutable provenance and the shared codec's pre-I/O read-only save guard; writable paths still use existing revisions, encoding/BOM and conditional writes. Built-in main/related resources are protected, while plugin provenance alone does not imply immutability and plugin/user/project skills remain editable.

The pinned sealed CodeEditor has no read-only property. A small `ITextDocument` mutation filter preserves that editor's selection/copy/find; mutating commands and Save/Ctrl+S are disabled. Save protection also holds after trusted programmatic document replacement and stale overwrite callbacks. Existing path-keyed tabs only tighten policy, retaining dirty text, document/undo identity, saved snapshot and conflict state. Tightening while a save is outstanding is refused with retry feedback; opens finishing concurrently recheck tab reuse. Dirty protected close offers Cancel/Discard rather than Save. This is document-lifetime state, not a persisted/global protected-path registry: closing and reopening through ordinary trusted paths remains outside the skill workflow.

Child red evidence: both actual dialog opens returned raw writable strings (2 failures), followed by 9 compile-red missing-contract/API errors. The first implementation selection had 139 TUI passes/3 failures from unmounted reuse fixtures and the existing prohibited test-name check; corrections left guardrails unchanged. Two additional fixture compile errors around `KeyEventArgs.RawEvent` were repaired. Parent independently reviewed provenance, actual codec injection, control filtering, dirty/in-flight reuse and fixture isolation. Final independent results: clean Release build (`--no-restore`, 0 warnings/errors), **147 focused TUI passes**, **67 focused Catalog passes with 1 Unix-only skip**, then **253 broad TUI passes** and **99 broad Catalog passes with 1 Unix-only skip**. Focused TUI operands were `SkillsManagementServiceTests`, `FileEditorWorkspaceCoordinatorTests`, `FileEditorSessionStateTests`, `ArchitectureGuardrailTests`, prefixed `FullyQualifiedName~CodeAlta.Tests.` and joined with `|`; focused Catalog used `SkillManagementServiceTests` and `TextFileCodecTests` under `CodeAlta.Catalog.Tests`. Broad filters are exactly the preceding accepted skill-extraction selections documented below. Tests used Release `--no-build --no-restore`. Parent site build and diff checks passed; Lunet fetched/installed its configured theme. Evidence: temporary `codealta-parent-readonly-{build,focused-tui,focused-catalog,broad-tui,broad-catalog,site}.log`.

All added discovery fixtures use synthetic providers, explicit owned roots and owned Git configuration/excludes. Editor fixtures use in-memory terminals and deterministic codec-gate/callback entry points, not watcher sleeps or real clipboard/provider startup. Existing watcher regressions pass. Linked/reparse paths are checked during resolution and workflow load/save, but external path-replacement races remain; raw trusted codec operations are not renderer grants or subject to a new sandbox. No full suite, interactive native UX, broader cross-platform link/race qualification or rendered desktop parity was established. This closes the skill-editor gap recorded in the prior checkpoint, not all M2/P10 work.

### M2 skill-management checkpoint

Catalog `Skills.SkillManagementService` now owns management query assembly, related-file shaping, scaffold authoring and single/bulk/invert enablement through existing Catalog/config owners. `SkillAuthoring` prevalidates explicit scope/root/name/description, rejects observed linked/reparse components and occupied destinations, and publishes a complete adjacent staging directory without overwriting the destination. Actual TUI factory/dialog wiring uses the thin selection/scope adapter; project context is captured before worker dispatch and profile discovery is explicitly supplied. Activation and editor dispatch remain frontend adapters, with visible open-failure feedback. Provenance and template/layout conventions remain intact; built-in/plugin creation targets are not offered.

The child reproduced four original creation/enablement validation regressions (4 failures, 1 collision pass) and editor-open feedback failure before repairing them. A preservation test exposed existing whole-typed-document config rewriting (21 Catalog passes, 1 failure). The bounded repair stays in `CodeAltaConfigStore`, using `SkillConfigSyntax` to alter only disabled-name tokens across table/dotted/inline forms, retaining array comments and unrelated fields; no fallback serialization. Compilation/API assumptions and a missing-final-newline failure were corrected. Parent draft review caught top-level files named like resource folders and conflation of valid U+FFFD with malformed UTF-16. The former has red/green regression evidence; the latter was repaired before parent-required explicit coverage. Seven follow-up cases cover valid/rejected Unicode, CRLF/provider preservation and non-mutation on parser/type failures. The first expanded run (39 passes, 1 failure) exposed Tomlyn's valid-U+FFFD-comment rejection; that pre-existing limit is now an explicit failure/non-mutation regression, not mislabeled invalid Unicode.

Parent independently passed the final Release solution build (`--no-restore`, 0 warnings/errors), **41 focused Catalog** skill cases and **24 focused TUI** skill cases, then **244 TUI** and **95 Catalog passes with 1 Unix-only skip**. Focused filters were `FullyQualifiedName~CodeAlta.Catalog.Tests.SkillManagementServiceTests` and `FullyQualifiedName~CodeAlta.Tests.SkillsManagementServiceTests`. Broader selections add those respective operands to the prompt CRUD checkpoint's exact filters below. Tests used Release `--no-build --no-restore`; guardrails passed unchanged. Parent site build passed (configured Lunet theme fetch/install observed); diff checks passed. Evidence: temporary `codealta-parent-skills-{build,focused-catalog,focused-tui,broad-tui,broad-catalog,site}.log`.

Skill fixtures now bound both skill roots and Git ancestor/home fallback with owned Git metadata; see the qualification document's **Skill-management isolation follow-up** for dependency correspondence and exact exposure. No real profile/config/credential inspection, full suite or application/provider startup was used. Windows symlink cases executed, but Unix directory publication/permissions, mid-write cancellation and cleanup-fault behavior remain unqualified. External path replacement races and cleanup failure remain possible. Both still writes global then project with no cross-file transaction or external-writer CAS. General config recovery/conditional persistence and live-tool mutations remain separate. **Built-in skill files still open through a path-only generic editor with no read-only enforcement**; the plan retains a dedicated follow-up. This checkpoint does not complete M2/P10 or rendered desktop parity.

### M2 prompt CRUD checkpoint

Catalog `PromptResourceStore` now owns agent/system file-local CRUD over explicit separate roots and scope/kind/id identities. Built-in writes, wrong-root provenance, cross-store snapshots, invalid ids and existing linked/reparse path components are rejected below presentation. `PromptFileFormat` shares neutral flat frontmatter/scalar/mode parsing only; Orchestration retains discovery, precedence and runtime composition. Management-generated quoted scalar escapes are decoded by runtime parsing, absent append metadata remains inheritable, and explicit default-system overrides are retained.

The actual `CodeAltaApp → PromptDialogCoordinator → PromptManagementDialog` path uses the composition's shared Catalog codec. `PromptEditingSession` holds fields and revision from the same read. Create never overwrites a same-scope file; failed/conflicting save/delete keeps dirty fields without success notification/reload. Confirmed retries target the observed conflict revision, not an invisible latest read; Refresh confirms discard. Save preserves loaded Unicode encoding/BOM. Parent reviewed store validation, parsing, dialog selection/cancel/retry paths, actual injection and task-owned fixture roots; no additional correction was required after the child handoff. Three existing source-guard locations shifted by one line, with no new exemptions or larger budgets.

Parent independently passed `dotnet build src/CodeAlta.slnx -c Release --no-restore` with 0 warnings/errors. Focused tests passed: Catalog `FullyQualifiedName~CodeAlta.Catalog.Tests.PromptResourceStoreTests` (**11**); TUI `FullyQualifiedName~CodeAlta.Tests.PromptEditingSessionTests|FullyQualifiedName~CodeAlta.Tests.PromptManagementDialogTests|FullyQualifiedName=CodeAlta.Tests.ArchitectureGuardrailTests.PromptManagement_UsesCatalogCrudAndSharedSnapshotCodec` (**13**). Broader audited selections passed **220 TUI** and **54 Catalog, 1 Unix-only skip**. The TUI filter is the UI-state checkpoint's selection plus `PromptEditingSessionTests` and `PromptManagementDialogTests`, each prefixed `FullyQualifiedName~CodeAlta.Tests.`. Catalog adds `FullyQualifiedName~CodeAlta.Catalog.Tests.PromptResourceStoreTests` to the UI-state checkpoint selection. Tests used Release `--no-build --no-restore`. Parent `lunet build` passed, including its configured `lunet-io/templates` fetch/install; diff checks passed. Evidence: temporary `codealta-parent-prompt-{build,catalog,tui,broad-tui,broad-catalog,site}.log`.

Limits: trusted-backend paths are not renderer grants or a sandbox; external link/path/check/commit races and lack of cross-process atomic CAS remain. Managed frontmatter/comments are not lossless and dialog storage calls remain synchronous. Windows execution does not qualify Unix behavior, interactive desktop parity or other native platforms. No full-suite test, real application/provider startup or profile inspection was used. Skills, config recovery and live-tool prompt mutations remain separate M2 work; this checkpoint does not complete P10 desktop parity.

### M2 additive UI-state checkpoint

Catalog UI-state loads/saves now use raw-byte revisions and the same `TextFileCodec` injected into actual TUI drafts, editors and ask reviews. Save requests freeze mutable UI state before yielding; one ordered TUI chain advances only acknowledged revisions and retains pending YAML/baselines on conflicts or failure. Structured SharpYaml trees preserve unknown root/nested/tagged data, frontend-owned layouts and unavailable logical-tab descriptors across TUI → simulated desktop → TUI handoffs. Known fields/nulls win; removed project preferences and intentionally transient legacy session fields are not resurrected. The session journals are untouched.

Parent's initial independent check passed the Release build but failed **2** guardrails (126 passing cases): exact legacy source locations had shifted and codec construction moved into Catalog. Review also found that frontend callers discarded the new failure results. The correction updated six existing source locations, strengthened shared-codec wiring checks, and added actual settings/sidebar/ordinary-save feedback plus adapter regressions. Conflicts warn and failures error through the existing status channel; acknowledged retries report recovery. Failed settings saves skip success-path refresh/language actions. Shutdown suppresses UI feedback after projection disposal and keeps a nonthrowing failure result so it does not newly bypass later cleanup. The child also removed one unused private facade forwarding method to stay within the existing size budget; no exemptions or budgets increased.

Final parent verification: clean Release solution build (0 warnings/errors), **207 TUI cases passed**, **43 Catalog cases passed with 1 Unix-only skip**, and site build passed (configured Lunet theme fetch observed). The TUI filter is the watcher checkpoint's exact 14-clause selection plus `FullyQualifiedName~CodeAlta.Tests.UiStatePersistenceTests`; it includes 33 new UI-state/adapter cases and one new guardrail. Catalog selection is the image checkpoint's three storage classes plus exact `CatalogInfrastructureTests.SessionViewCatalog_ViewStateRoundTrip_Works` and `CatalogInfrastructureTests.SessionViewYamlSerializer_DeserializeViewState_MigratesLegacySessionSelection`, under `CodeAlta.Catalog.Tests`. Tests used Release `--no-build --no-restore`, build `--no-restore`. Evidence: temporary `codealta-ui-state-final-parent-052fddff3ac14961a890b462ba1aeffa/{build,tui-tests,catalog-tests}.log`.

The new fixtures use literal YAML, unique owned roots, deterministic barriers, inert shell-service delegates and actual settings/sidebar adapters without opening dialogs or initializing host/provider/plugin discovery. They cover semantic unknown-data retention, known-field clears, legacy empty/selection compatibility, stale same-mtime/missing/deleted-file conflicts, malformed data, cancellation/read-only failure, frozen ordering, repeated failure and acknowledged retry. Simulated desktop descriptors establish persistence only, not renderer restoration or feature parity. Anchors/aliases, duplicate/complex/merge keys, multiple documents and wrong section shapes are rejected explicitly; comments/formatting/encoding are not retained. Pending settings may stay effective in memory, dialogs still close, status can be replaced by later UI activity, and shutdown failures remain logged rather than recoverable transactions. External-writer/link races and other-platform execution remain unqualified.

### Desktop acceptance rows

M1b adds the isolated desktop boot entrypoint, 22 managed desktop tests, opt-in native qualification, and the migrated M0 fixture under `CodeAlta.Desktop.Tests/NativeSmoke`. Parent independently passed the 22 tests plus 104 architecture/TUI identity tests and the installed desktop's explicit-root boot/close/CLI checks. See [native qualification](desktop-native-qualification.md) for package hashes, output/glob corrections, script commands and limitations. The production boot DOM is not yet natively asserted; full RPC/visual checks remain test-fixture evidence, not desktop feature parity.

**Safety deviation:** a child ran the unfiltered full solution suite (1,821 passed, 2 skipped) without addressing known profile-discovery gaps. That result is not profile-isolated; the real-home test ran and global prompt/skill reads may have occurred. The evidence document records the exact command and corrects the child's initial no-access claim. Do not repeat that run until isolation is established; parent reruns used audited subsets and explicitly isolated native tools.

The [M2 source-only isolation audit](desktop-native-qualification.md#m2-source-only-test-isolation-audit-2026-09-05) further confirms reachable MCP global discovery/write probes, live-tool/runtime skill home forwarding, and instruction ancestor walks. Excluding only the three historical cases is insufficient. These are source-confirmed paths, not an inventory of historical user-file accesses; no follow-up profile inspection or full-suite run occurred. Bounded root/fixture repairs and separate subprocess/OS qualification remain prerequisites to broader testing.

All **14** approved areas are retained. The two evidence columns are deliberately separate: neither implementation intent nor existing TUI coverage establishes desktop acceptance. Paths/symbols below refer to the pre-rename implementation under `src/CodeAlta` unless otherwise noted.

| ID / area and current reference | Required desktop acceptance | Automated evidence | Manual/native evidence |
| --- | --- | --- | --- |
| P01 — Startup/config recovery: `Program`, `DeferredCodeAltaApp`, `ConfigRecoveryDialog` | Fast boot shell; offline sessions before providers; malformed TOML recovery; cache/provider/plugin errors visible; safe mode works. | **Pending** | **Pending** |
| P02 — Navigator/projects: `CodeAltaShellController`, `NavigatorActionCoordinator`, project dialogs | Current-folder project; add/open/import; rename/edit/archive/hide/delete as currently supported; sorting/recent limits; global and nested child sessions; refresh/search and complete session browsing. | **Pending** | **Pending** |
| P03 — Tabs and restore: shell/session tab coordinators, `SessionViewCatalog` | Draft/session/editor/plugin tabs, selected tab and preferences restored; close/reopen and keyboard movement; unsent drafts survive switching heads. Running sessions remain discoverable when tabs close. | **Pending** | **Pending** |
| P04 — Session commands: creation/command/queue/provider-switch coordinators | Create global/project/child sessions; send/queue/always-queue/steer/abort/compact; clear queue, inspect and perform existing queue edit/reorder actions; rename/delete; provider/model/reasoning/agent-prompt selection with actual runtime capabilities. | **Pending** | **Pending** |
| P05 — Composer: prompt/reference/image views and GitHub plugin | Multiline/full prompt; undo/redo, paste, IME; file `@` and GitHub `#` references; plugin attachments; clipboard/drop images; capability validation and persistent drafts. `?`/`/` shortcuts remain prompt UI, not a new slash-tail parser. | **Pending** | **Pending** |
| P06 — Timeline: runtime/timeline renderers, usage and session-info views | User/assistant/reasoning/notices/tools; streaming progress/errors/cancellations; code/diffs/images; expand/collapse and copy; history navigation/search; system-prompt details; context/tokens/cost/timing and provider/session metadata. No recomputing usage in JavaScript. | **Pending** | **Pending** |
| P07 — Interactions: permission/user-input coordinators, `AskModeCoordinator`, `AskQuestionFormView`, `AskFileReviewView` | Existing permission decisions and auto-approval policy; queued question/choice/freeform asks; attached-file review/edit/save; cancellation and exactly-once replies across document reload. | **Pending** | **Pending** |
| P08 — Coordination: `CodeAltaFrontendComposition`, LiveTool contributors, notes/reminders | All in-process `alta` actions, parent/child status/results, notes copy/clear, reminders create/list/delete and repeat/queue behavior, agent/profile switching. Independent of active tab selection. | **Pending** | **Pending** |
| P09 — Providers: provider coordinator/dialog/model catalog | Add/edit/delete/enable; advanced TOML; test/refresh/cancel; browser/device login/logout; accounts/models; readiness and diagnostics; model metadata and selection for draft/current session. | **Pending** | **Pending** |
| P10 — Prompts/skills: management dialogs/services and catalogs | Agent and system prompt create/edit/delete/inspect; scope/provenance/built-in read-only rules; skills discover/inspect/create/edit/enable/disable/activate and diagnostics. | **Pending** | **Pending** |
| P11 — Plugins/MCP/GitHub/statistics | Discovery, enable/disable/rebuild/reload/status/diagnostics/contributions; current MCP server/tool management, JSON/policy editing and OAuth; GitHub picker/attachments; statistics cards/details/live-tool commands. | **Pending** | **Pending** |
| P12 — File editing: `FileEditorWorkspaceCoordinator`, `FileEditorTab`, `TextFileCodec` | Scoped open/edit/save; encoding/BOM/newlines; syntax/search/undo/redo; conflict detection and dirty-close prompts. No unrestricted renderer filesystem access. | **Pending** | **Pending** |
| P13 — Preferences/help/diagnostics: built-in commands, navigator settings, logs/about | Scheme/language/permissions; navigator layout/density; help/palette; logs; version/update visibility; clipboard and screenshot/export equivalent. Terminal loop/terminal-cell debug commands stay TUI-only (see exception below). | **Pending** | **Pending** |
| P14 — Accessibility/native behavior | Keyboard-complete navigation, focus restore/trap, semantic labels/non-color statuses, contrast/reduced motion, text scale/high DPI, native window/menu/dialog behavior on every claimed engine. | **Pending** | **Pending** |

### Explicit terminal-only exception

Terminal loop/terminal-cell developer diagnostics have no native-window or browser-cell equivalent and may remain TUI-only, as the approved matrix permits. This does **not** exempt logs, status, help, clipboard, screenshot/export or ordinary diagnostics from desktop parity. The command-by-command inventory and proof that these diagnostic commands remain functional in TUI are **pending**; no other area is classified N/A.

## Additional approved v1 acceptance details

These rows retain the richer approved editor/visual/workspace requirements; they do not expand the plan. Each needs automated tests plus packaged native UI evidence where applicable.

| ID / surface | Required acceptance | Implementation / evidence |
| --- | --- | --- |
| V01 — Editor/composer engine (P05/P12) | One CodeMirror 6 engine with composer, file/config and read-only comparison presets; preserve editor state/undo/selection through tab changes and resizing; IME/completion must prevent accidental sends. Line numbers, folding, matching/indentation, multiselection, find/replace with regex, go-to-line, wrapping/tab size, line/column and encoding/newline status; dirty/conflict/revision-checked .NET save and backend diagnostics with correct UTF-16 positions. | **Pending** |
| V02 — Languages and large files (P06/P12) | Markdown/fences, C#, JSON, TOML, YAML, JS/TS/JSX/TSX, HTML/CSS, XML/MSBuild, Bash/PowerShell, Python and diff grammars with plain-text fallback; bounded transport/long-line work and explicit large-file mode; never save a truncated document over its original. No semantic IDE/LSP claim. | **Pending** |
| V03 — Markdown/code (P06/P08/P10/P12) | Shared GFM renderer for all surfaces; tables/tasks/footnotes/callouts, heading anchors and exact-source code copy/wrap/open controls; no raw HTML, automatic remote images or executable task boxes. Lazy Shiki JavaScript-engine token rendering, bounded language imports, stable block revisions and readable incomplete-fence fallback. | **Pending** |
| V04 — Markdown editing and comparisons (P07/P12) | Source / Preview / Split, heading outline, source-position links, bounded anchored scroll sync, source-preserving toolbar edits; inline/side-by-side read-only CM merge, context folding/gutters/intraline emphasis/change navigation. Partial patches remain patches, not fabricated full files; editable saves use the ordinary backend conflict checks. | **Pending** |
| V05 — Mermaid (P06/P08/P12) | Direct main-document strict-mode rendering for flowchart, sequence, class, state and ER; `.mmd`/`.mermaid` and fenced Markdown share the component. Diagram / Source, accessible title/description/source, fit/zoom/reset/pan/expand, copy/save source and bounded PNG export. Test theme/fonts/Unicode, invalid/incomplete/oversized input, stale results and cleanup. Initial 32 KiB/200-edge limits are targets to measure, not qualified results; source fallback alone is not completion. | **Pending** |
| V06 — Tabs/splits/native drops (P03/P04/P05/P14) | Persistent reorderable tab strip with overflow/pin/reopen/badges/dirty guards, pointer and keyboard dnd-kit moves/cancellation/announcements, no editor remount or duplicate subscriptions. Accessible resizable panes with constraints/reset/collapse and committed-size persistence. Native file/image/folder drops use explicit grants, not browser path inference; queue reorder remains a backend command. | **Pending** |
| V07 — Settings/tables/themes/localization (P09/P10/P11/P13) | One settings sidebar/search/scope workflow; compact searchable/sortable accessible model/provider/plugin tables with truthful backend paging; shared named neutral color schemes and localization, local licensed fonts/icons, configurable typography/density, live-preview/revert, reduced motion and scaling. Exact library versions/lockfile and license review still pending. | **Pending** |
| V08 — Portable plugin panels (P11) | Head-neutral backend contributions, optional TUI renderers, rich desktop adapters and meaningful TUI fallback; builtin MCP/GitHub/statistics retain shared backend ownership. One typed project/workspace panel example; backend-only, dual-head, TUI-only, unavailable/failing/reloading plugin tests in installed packages. | **Pending** |

Remote hosting/network authentication, multi-user access, installers/automatic updates, arbitrary plugin JavaScript canvases, executable HTML/JS previews, editable graph canvases, WYSIWYG, embedded terminals, general docking/tear-offs and a full IDE remain outside this approved scope. They are not silently substituted for, or prerequisites of, the pending rows above.

## Package, runtime and release gates

| Gate | Required evidence | Current status |
| --- | --- | --- |
| G01 — M0 NuGet consumer | Minimal consumer using published NeoAstra **0.1.0**, centrally pinned package and reviewed locked frontend dependencies; staged client and relative assets. | **Pass — isolated prototype**, not production |
| G02 — M0 serverless/native proof | Fake-data packaged Release launch, typed RPC round trip, channel cancellation, close/dialog behavior, dynamic asset loading and ordinary CM/Radix/direct Mermaid integration without Node/dev server/providers/plugins. | **Pass — Windows x64 prototype**, parent rerun |
| G03 — M0 isolated tool distribution | Local-feed pack/install outside checkout into task-owned tool path; native/assets/contracts/content inventory and command exit/output checks; Windows console/window subsystem behavior. No absolute local NeoAstra dependency or global tool replacement. | **Pass — Exe/WinExe prototype packages**; interactive console/Explorer UX and production plugin CLI pending |
| G04 — Shared ownership and compatibility | Both heads and both cross-head lock orders/races/stale/denied-access cases; admission before mutable startup; fake-provider sessions with closed tabs, reload, queue/stop and confirmed quit; legacy/current journal/config/UI-state/text/image draft handoff without unknown-field loss. | **Pending** |
| G05 — Recovery and permissions | One runtime reader; revisioned snapshot/subscribe, bounded nonblocking fan-out, loss/gap recovery with bounded history reads; exactly-once ask/permission resolution across reconnect/abort/quit and stale/wrong-session rejection. Preserve permission configuration including current AutoApprove default. | **Pending** |
| G06 — Content and package correctness | Controlled application navigation/links/attachments/native grants, default Markdown/Mermaid content handling, no secrets/source maps/remote assets/dev overrides in Release; explicit OAuth listeners cleaned up. No custom isolation framework. | **Pending** |
| G07 — Performance and accessibility | Hardware/fixture-recorded cold/warm startup, provider-independent catalog/history, ten active fake sessions and 100,000-event journal, bounded memory/payloads/DOM, input/scroll latency; keyboard/screen-reader/IME/contrast/reduced-motion/100–200% scale. Provisional <50 ms p95 input/frame targets are unmeasured. | **Pending — no performance budget qualified** |
| G08 — Full regression and release | Full safely isolated .NET suite, frontend typecheck/lint/tests/build, mock-transport UI tests distinguished from native tests; both final tool packages/content/plugin compatibility, all parity rows and public/internal installation/migration docs; no publishing or reduced support matrix without authorization. | **Pending — partial baseline only** |

### Native platform qualification

The [preserved M0 fixture README](../src/CodeAlta.Desktop.Tests/NativeSmoke/README.md) and its evidence ledger record actual Windows x64 execution on WebView2 **152.0.4191.62**, .NET **10.0.11**, Windows **10.0.26200**. Both ordinary Exe/WinExe local tool packages preserve native-dialog manifests and pass redirected CLI/native smoke. Parent independently reran the installed Exe smoke and all three frontend tests. Asset inspection verified 100 hashes (3,823,561 bytes) and six native binaries; no Node/runtime/source-map bundle. Only this bounded feasibility fixture is accepted; all production/native feature qualification remains open, including five other architectures/engines, interactive console behavior, accessibility/performance and complete release notices. Point-in-time npm/NuGet advisory checks reported no vulnerabilities.

| Intended desktop RID | Native engine | Actual engine/version evidence | Qualification |
| --- | --- | --- | --- |
| `win-x64` | WebView2 | 152.0.4191.62; M0 local-tool/native smoke passed | **M0 feasibility passed; full product qualification pending** |
| `win-arm64` | WebView2 | Not measured; no native run | **Pending / unqualified** |
| `osx-x64` | WKWebView | Not measured; no native run | **Pending / unqualified** |
| `osx-arm64` | WKWebView | Not measured; no native run | **Pending / unqualified** |
| `linux-x64` (glibc) | WebKitGTK | Not measured; no native run | **Pending / unqualified** |
| `linux-arm64` (glibc) | WebKitGTK | Not measured; no native run | **Pending / unqualified** |
| Desktop musl | No approved engine target | Explicitly unsupported by the plan | **Unsupported**, not a pending support claim |

The TUI retains its existing eight-RID intent, including `linux-musl-x64` and `linux-musl-arm64`; this Windows managed baseline is not new qualification of those packages. Each claimed desktop RID needs real engine/architecture runs covering local chunks/fonts, standard library styling/SVG, native menus/close/dialogs, clipboard/drop, focus/IME, resize/DPI and installed-package startup. Mock-browser or cross-compilation results cannot fill this table. A narrower initial public release requires user approval.

## Blockers and next step

1. The full Release test baseline is **incomplete by safety choice**; establish explicit runtime-root injection or audit additional no-profile/fake-provider fixtures before broadening it. Do not use the active user's profile to turn a skip into a pass. The startup checkpoint below records an observed and corrected newline-only fixture failure; earlier passing evidence is not a claim that no intervening verification failed.
2. **M0 feasibility and M1 dual-head foundation are accepted**, along with the bounded M2 storage/options/permission/immediate-user-input/durable-notes/pending-ask-queue/response-settlement/reminder-firing/observer/configured-provider-composition/inspection checkpoints above. Continue M2's remaining interaction lifetime, provider auth/configuration/refresh workflows and shared startup ownership, adapting actual TUI routes before desktop equivalents. No upstream NeoAstra change or custom launcher was required.
3. Desktop feature, cross-head persistence/ownership, full native platform, performance and release gates remain pending. Prototype integration success and the terminal rename are not desktop parity.

## M2 shared startup admission checkpoint — 2026-09-08

The user resumed execution and requested faster M2–M7 progress. Unaccepted file-search lifetime policy is deferred rather than blocking this independent startup step. The sole child implemented the shared guard extraction and mandatory synchronous `CodeAltaStartupAdmission.Run`; parent independently reviewed the complete helper, moved guard, Program/CLI changes, inert tests and source/preservation helpers. No desktop enablement or lock-algorithm change is included.

TUI admission now encloses logging, terminal opening, plugin bootstrap, plugin-aware command dispatch, and existing plugin/terminal/logging cleanup. Sole `--help`, `-h`, and `--version` use the existing built-in declarations with plain command output before acquisition or root resolution. Other arguments, including plugin/status/combined-help commands, acquire first. Acquisition errors use plain stderr. The exact acquired lease is disposed once with ordinary synchronous `using` exception precedence; the existing terminal main-thread entry and all lower-owner lifetimes are unchanged. `CodeAltaSingleInstanceGuard` and its exception moved into Hosting with public XML documentation, without a duplicate TUI implementation.

**Newline policy:** the user explicitly accepts LF, CRLF and mixed LF/CRLF. Source checks now canonicalize both inspected source and expected literals through test-only `SourceTestText`. Actual bytes still receive strict UTF-8/BOM/final-newline validation; historical hashes use their explicitly recorded rendering. Spaces, tabs, blank lines and other content are not stripped. Historical hashes and complete required NEW-to-OLD content inverses remain, including startup → Deferred/reminder → catalog preservation. Existing attributes are untouched, but the affected checks no longer depend on a uniform checkout representation. No production file was rerendered to satisfy a fixture.

Parent first reconstructed24 exact startup maps across nine changed existing files against complete named HEAD originals and actual forward bytes. After the newline amendment, parent reviewed the eleven additional Deferred/reminder compatibility maps and canonical read/literal/hash helpers. Program/CLI/guard content inverses, catalog E1–E6 inverses, and workspace/reminder historical reconstruction pass in the exact-filter tests. App and development-guide contents remain unchanged.

**Verification:** targeted TUI/Hosting Release builds passed with zero warnings/errors. Initial verification had30 passes and one updater caller source failure at its unchanged raw literal, before the changed Program checks: compiler CRLF versus reader LF. Two diagnostic attempts matched an earlier snapshot literal; the method-scoped retry confirmed the failing literal unchanged from HEAD and an exact canonical match. This was not a baseline runtime test. After the user-authorized compatibility fix,31 audited exact source/helper methods passed43 cases, including the previously failing updater method; the eight unchanged admission inert methods passed21 cases and the Hosting API boundary method passed1. Total distinct scoped coverage: **65 cases /40 methods**, no skips. No class-wide filters, concrete guard/Process/CTS/service/UI/provider/host acquisition, default startup or native smoke were run. Source/Git reads and existing writerless assembly logging are nonzero I/O. Plain-output package compilation is verified, not installed-command runtime qualification.

Exact-filter inventory uses `FullyQualifiedName=<namespace>.<class>.<method>` joined with `|`, always with Release `--no-build --no-restore` after the corresponding build. The following are the complete selected method suffixes, not class-wide authorization:

| Namespace / class | Exact methods |
| --- | --- |
| `CodeAlta.Hosting.Tests.CodeAltaStartupAdmissionTests` | `Run_ValidatesMandatoryOperationsBeforeInvocation`, `Run_EarlyFlagsDoNotResolveRootAcquireOrStart`, `Run_NonEarlyArgumentsAcquireBeforeStartup`, `Run_AcquisitionFailureDoesNotStartMutableWork`, `Run_PreservesStartupExitCodeAndReleasesLease`, `Run_StartupFailureStillReleasesLease`, `Run_KeepsLeaseThroughStartupCleanup`, `Run_RemainsOnCallingThread` |
| `CodeAlta.Hosting.Tests.HostingCompositionBoundaryTests` | `HostingAssembly_ExposesOnlyApprovedCompositionApiWithoutFrontendReferencesOrOptionalParameters` |
| `CodeAlta.Tests.SourceTestTextTests` | `Canonicalize_EquatesLfCrLfAndMixedSourceAndLiterals`, `HistoricalBytes_PreservesRecordedRendering`, `Canonicalize_PreservesNonNewlineDifferences`, `CanonicalizeAndDecodeSource_RejectBomAndLoneCr`, `DecodeSource_RejectsMalformedUtf8`, `DecodeSource_RequiresFinalNewline` |
| `CodeAlta.Tests.CodeAltaStartupAdmissionSourceTests` | `Program_AcquiresBeforeLoggingTerminalAndPluginStartup`, `Program_ReleasesAfterPluginCleanupAndLoggingShutdown`, `Program_EarlyOutputAndAdmissionFailureAvoidMutableStartup`, `Program_RunAsyncDoesNotReacquireAdmission` |
| `CodeAlta.Tests.DeferredCodeAltaAppSourceTests` | `DeferredStartup_SourceWiring_UsesLinkedOneShotAdmission`, `DeferredDisposal_SourceWiring_UsesCachedJoinAndExclusiveOwner` |
| `CodeAlta.Tests.CodeAltaUpdateServiceSourceTests` | `UpdateStart_SourceWiring_CapturesTokenBeforeScheduling`, `UpdateDisposal_SourceWiring_JoinsBeforeSourceRelease`, `UpdateCaller_SourceWiring_AwaitsOwnedStageAndPreservesPresentation` |
| `CodeAlta.Tests.AltaReminderLifetimeSourceTests` | `Admission_RetainsBeforeOutsideGateInvocation`, `Publication_PublishesTheAssignedOriginal`, `Pruning_RequiresCompletedOriginalAndRetainsFirstHistory`, `Stop_SnapshotsUnderGateAndDisablesPruning`, `SharedDisposal_UsesOneInstanceLazy`, `Cancellation_ClaimsOnceAndFencesWorkerRelease`, `MutationsAndWorkerChecks_PreserveTheScopedContract`, `StaticCores_UseAcceptedValidationAndErrorPolicies`, `Ownership_TransfersToShellAndUsesBestEffortCleanup`, `Preservation_InvertsOnlyApprovedProductionAndGuardChanges`, `Preservation_UnchangedRuntimeAndFrontendRoutesRemainFrozen` |
| `CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests` | `Start_SourceWiring_StopsAdmissionBeforeTokenCaptureAndScheduling`, `Dispose_SourceWiring_SharesOneStopSnapshotAndCancellation`, `Dispose_SourceWiring_UsesMandatoryLoggerFreeCore`, `Preservation_InvertsOnlyApprovedCatalogLifetimeChanges`, `Preservation_OwnershipAndExistingGuardsRemainUnchanged` |

Logs are `%TEMP%/codealta-parent-startup-{build,hosting-build,inert-tests,source-tests,regression-tests,boundary-tests,compat-build,compat-tests,compat-regressions,solution-build,site}-20260908.log`. Final solution Release build passed with zero warnings/errors; site build passed111 files/1,032,031 bytes, including configured theme download/install (not network-free). Parent updated README and getting-started behavior guidance and reviewed the scoped integration diff. The guard's fail-open process-inspection result, stale-delete/release races and cross-process/cross-head qualification remain open; the extraction must not be used to claim safe default-profile desktop startup. File-search, broader M2 and all M3–M7 remain incomplete. Protected artifacts remain unread; no push, publish or merge.

### Follow-up: fail-closed owner inspection

The startup slice was committed as `c1dea309`. A subsequent bounded correction fixes its explicitly retained fail-open inspection result, without changing acquisition, retry, pathname deletion, release order or default paths. The actual private Process adapter now calls the same generic disposable probe operation as the inert tests. Both callbacks validate before any work; nonpositive PID, null resource and unknown lookup/inspection/release errors conservatively mean the process may still be running. Only the positive-PID lookup's missing-process `ArgumentException`, or observed exit followed by successful release, permits a false result. An `ArgumentException` from inspection or disposal is not lookup absence. Every returned non-null resource is disposed once, even after inspection failure; no retries occur. This does not make a PID observation authority over the current pathname.

Parent independently audited all five new inert methods and the mandatory production operation, then reconstructed both deliberate content maps against the complete prior guard and reproduced the complete canonical current source. The existing eleven-map source inverse retains the original6402-byte historical rendering/hash. No other production code or source-reader policy changed. The old test that constructed a Process and expected access denial to return false was replaced; default-path/acquisition/Task.Run tests remain unchanged and excluded.

Targeted Release build passed with zero warnings/errors. The five exact `CodeAlta.Tests.CodeAltaSingleInstanceGuardTests` methods `IsProcessRunning_ValidatesCallbacksBeforeInspection`, `IsProcessRunning_UnknownInspectionFailsClosed`, `IsProcessRunning_OnlyLookupAbsenceOrObservedExitReturnsFalse`, `IsProcessRunning_InvalidPidDoesNotAuthorizeReclamation`, and `IsProcessRunning_ReleasesTheExactProbeResource` passed33 cases. The four exact startup-source methods listed above passed4 more: **37 passed /9 methods**, no failures/skips. Tests used inert recording disposables, not Process/guard acquisition, filesystem mutation or runtime startup. Existing writerless assembly logging and source/Git reads remain nonzero I/O. Logs: `%TEMP%/codealta-parent-lock-liveness-{build,tests}-20260908.log`; solution/site results are recorded in the plan after execution.

Final solution Release build passed with zero warnings/errors; site passed111 files/1,032,197 bytes with configured theme download/install observed. Logs also include `codealta-parent-lock-liveness-{solution-build,site}-20260908.log`. At that integration boundary the process-harness/persistent-exclusive-file proposal remained research; the subsequent qualification below supersedes that execution status, not its safety limits. Parent deliberately separated the conservative correction from future reclamation algorithm changes; **M2–M7 remain incomplete**.

### Isolated Windows lock-process qualification

Added `CodeAlta.Tests.LockProbe`, a non-packable, BCL-only net10 console linking the actual guard source, and `CodeAltaSingleInstanceGuardProcessTests` in Hosting.Tests. This exercises **linked source, not the shipped Hosting binary or either application head**. Production acquisition, stale deletion and release algorithms are unchanged. The helper's separate `persistent-probe` mode experiments with `OpenOrCreate`, read/write, `FileShare.None`, PID writing only after acquisition, and no unlink; it is not an adopted replacement.

The fixture requires explicit `CODEALTA_LOCK_PROBE_APPHOST` (absolute audited prebuilt apphost) and `CODEALTA_LOCK_PROBE_ROOT` (existing task-owned directory). Missing opt-in is inconclusive, never a qualification pass. No implicit build, executable search, application startup or default-profile root is used. The helper requires exact root/lock paths, mode, deadline and nonce, and uses a bounded READY/BEGIN/admission/RELEASE/RELEASED protocol. Each started process and its original exit/I/O tasks remain owned through cleanup; output is bounded while reading. Five-second response/startup and per-helper cleanup bounds are independent of the 30-second scenario budget. Cleanup terminates only retained helper processes and deletes only known files/directories nonrecursively after confirmed completion; failures retain roots and available owned PIDs. Synchronous OS calls remain unpreemptible by those async deadlines.

After reading every added source line and the complete linked source/logging closure, parent built both projects and separately admitted argument tests before ownership tests. All six exact methods in `CodeAlta.Hosting.Tests.CodeAltaSingleInstanceGuardProcessTests` passed **16 cases, zero skips**:

| Exact method suffix | Cases / evidence |
| --- | --- |
| `Probe_RejectsMissingRelativeOrOutsideTaskRootPaths` | 11 malformed/missing/path/mode/deadline/nonce rows |
| `Guard_HeldOwnerRejectsIndependentContender` | 1 held-owner scenario |
| `Guard_ReleasedOwnerAllowsIndependentSuccessor` | 1 clean successor scenario |
| `Guard_ReapedOwnerAllowsStaleRecovery` | 1 forced-owner-death scenario |
| `Guard_StaleContendersNeverReportOverlappingOwnership` | 1 case with four bounded contention opportunities |
| `PersistentProbe_RejectsContenderAndRecoversAfterOwnerTermination` | 1 experimental contention/death/recovery scenario |

Observed environment: Windows 10.0.26200, local NTFS; installed .NET 10 runtimes include 10.0.11 (the helper does not report its selected patch). All fixture cleanup succeeded, and parent removed the empty unique task root nonrecursively. Targeted/helper/solution Release builds passed with zero warnings/errors. Helper restore used its local project directory as the only source; its generated dependency manifest contains only the helper, with no package/provider dependency closure. Logs are `%TEMP%/codealta-parent-lock-probe-{build,helper-build,arguments,ownership,solution-build}-20260908.log`.

Final parent review corrected two cleanup catches so completion racing the catch cannot erase an observed timeout/cancellation. Solution Release rebuild and the same six exact methods passed again (16/16, zero skips); the new empty task root was removed nonrecursively. No injected cleanup-failure coverage is claimed. Website build passed111 files/1,032,197 bytes with configured theme download/install. Final logs: `%TEMP%/codealta-parent-lock-probe-{final-build,final-tests,site}-20260908.log`.

These results do **not** establish deterministic stale-observer/release-race freedom, real permission-denial behavior, Unix behavior, legacy/candidate interoperability, hard-link/alias/concurrent namespace safety, network-filesystem guarantees, or default-profile desktop admission. PID reuse remains a limitation; BUSY means rejection, not proven live ownership. Do not enable the desktop real profile or replace production locking based on this experiment. File-search policy remains deferred, and M2–M7 remain open.

### M3: host-selected plugin startup feedback

The bounded feedback extraction moves terminal presentation into `CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback`, with neutral `IPluginStartupFeedback`/`IPluginStartupProgress` in shared Plugins. Both Program prestart and owned-services/host fallback inject the terminal adapter; runtime/host feedback is borrowed and validated before mutable startup. Non-injecting callers now default silent even when nonheadless. The public interactive build helper moved into TUI, and interactive change notifications require an explicit sink instead of implicit global toast dispatch. Migration details are in `doc/plugins.md` and the public plugin developer guide.

Manager discovery/build/activation ordering, scheduler progress subscription/finally-unsubscription, Program admission and plugin cleanup, host borrowing, messages/acknowledgement and existing continuation policy are preserved. The production-used routing and presented-operation seams allow inert callback tests without acquiring runtime/scheduler/UI/CTS services. Normal driver completion joins the original task; driver/summary failure still may escape before that join. Notification sink exceptions still escape after status publication under the existing gate. These are documented preserved limitations, not repaired lifetimes.

Child authored fixtures before production changes and handed off18 files without execution. Parent read all added production/test code, checked the14 pre-extraction canonical baseline hashes against complete HEAD sources, and reviewed the occurrence-checked inverses and actual owner routing. An initial parent hash check incorrectly decoded Git stdout using the default text encoding; retrying from raw stdout bytes with strict UTF-8 matched all14 anchors. No file or hash was changed for that diagnostic. Parent reused `SourceTestText` in the new source fixture for strict BOM/lone-CR/EOF checks while accepting LF/CRLF/mixed content.

Initial scoped feedback tests passed39 cases. Additional source regressions passed10/11: host-creation wiring failed because a compiled CRLF raw literal was compared to canonical LF source. Parent canonicalized its four expected multiline expressions and `RequireOnce` input, adding exact inverses that preserve the original whole-host-fixture hash. Targeted retry passed3/3. Final TUI selection passed27/27; with24 unchanged Plugins passes, **51 distinct cases /34 exact methods passed, zero skips**.

Exact selection (class prefix followed by method names):

- `CodeAlta.Plugins.Tests.PluginStartupFeedbackPortTests`: `Route_RejectsMissingArgumentsBeforeCallbacks` (5), `Route_BypassesPresentationForHeadlessOrEmptyRequests` (3), `Route_ForwardsSelectedPortAndOriginalArguments` (1), `Route_PreservesTerminalOutcomes` (5), `NoFeedback_InvokesOriginalOnceWithoutSummaryOrStatus` (1).
- `CodeAlta.Plugins.Tests.PluginChangeNotificationServiceTests`: `Constructor_InteractiveRequiresExplicitSink`, `Notify_SinkFailurePreservesCommittedStatus` (2), `Notify_EmptyChangesClearsStatusWithoutClearingHistory`, `Notify_NullChangesFailsBeforeSink`, `NotifyCoalescesToastsFooterStatusAndActionDispatch`, `NotifyUsesHeadlessFallbackAndClearRemovesFooterStatus` (the other five each1).
- `CodeAlta.Plugins.Tests.PluginStartupFeedbackReporterTests`: `ReporterKeepsFastPathQuietAndWritesInteractiveProgress`, `ReporterUsesHeadlessFallbackWithoutMarkupControlSequences` (each1; package setup is now in-memory only).
- `CodeAlta.Tests.TerminalPluginStartupFeedbackTests`: `PresentedOperation_RejectsMissingArgumentsBeforeCallbacks` (4), `PresentedOperation_StartsInlineAndJoinsOriginal` (3), `PresentedOperation_RequestedCancellationDoesNotAbandonOriginal` (1), `PresentedOperation_DriverFailurePreservesExistingUnjoinedOriginal` (2), `PresentedOperation_SynchronousOperationThrowSkipsDriver` (1).
- `CodeAlta.Tests.PluginFeedbackExtractionSourceTests`: `Manager_UsesNeutralPortAndPreservesStartupOrder`, `TuiAndHost_ForwardFeedbackWithoutChangingOwnership`, `Feedback_ContainsNoTerminalDependency`, `Preservation_RestoresCompletePreExtractionSourcesAndGuardChain` (each1).
- Existing source regressions (each1): `CodeAlta.Tests.CodeAltaHostLifetimeTests.{HostOwner_SourceWiring_UsesSingleNamedDisposalFactory,HostCreation_SourceWiring_TracksAcquisitionsAndAwaitsNamedRollback}`, `CodeAlta.Tests.CodeAltaOwnedServicesLifetimeTests.OwnedServicesCreation_SourceWiring_TracksAcquisitionsAndAwaitsNamedRollback`; the four exact `CodeAltaStartupAdmissionSourceTests` Program methods listed above; `CodeAlta.Tests.AltaReminderLifetimeSourceTests.{Preservation_InvertsOnlyApprovedProductionAndGuardChanges,Preservation_UnchangedRuntimeAndFrontendRoutesRemainFrozen}`, `CodeAlta.Tests.ModelsDevCatalogLifetimeSourceTests.Preservation_OwnershipAndExistingGuardsRemainUnchanged`, `CodeAlta.Tests.DeferredCodeAltaAppSourceTests.DeferredStartup_SourceWiring_UsesLinkedOneShotAdmission`, and `CodeAlta.Tests.CodeAltaUpdateServiceSourceTests.UpdateCaller_SourceWiring_AwaitsOwnedStageAndPreservesPresentation`.

The moved `TerminalPluginStartupFeedbackTests.LiveStatusAppliesQueuedProgressToBindableState` remains excluded because it creates real UI/status and filesystem state. No runtime/config/source-build/scheduler/frontend/native fixture or application startup was selected. Synthetic tasks are retained and gates released in finally, with independent bounded observers and permanent timeout failures; driver-failure tests themselves join the original. Source/Git reads, DTO timestamp initialization and existing writerless test logging remain distinct from runtime/plugin execution. No historical hash, App budget, development guide or attribute policy changed.

Targeted and final solution Release builds passed without warnings/errors. Website passed111 files/1,033,509 bytes with configured theme download/install observed. Logs: `%TEMP%/codealta-parent-plugin-feedback-{build,plugins-build,tui-tests,plugins-tests,regressions,compat-build,compat-tests,final-tests,solution-build,site}-20260908.log`. This slice does not remove terminal dependencies from Abstractions, contribution adapters or shared source-build/loading lists. No real terminal presentation, full head-neutral loading or desktop parity is qualified; M2–M7 remain open and file-search remains deferred.

### M3: portable region content and optional terminal renderers

This bounded slice adds portable `PluginContentContribution` / `PluginUi.Content`, makes shared `PluginRenderResult` Markdown/text-only, and moves visual authoring into `CodeAlta.Plugins.Tui`. Optional native contributions require portable callbacks. The TUI bridge explicitly enables call-scoped `SupportsTerminalVisuals` and borrows the shared adapter through terminal selection. Both heads' potential content routes use the same production registration/filter/active-lookup/traversal mechanism; this is not an implemented desktop rendering path. MCP and the two existing visual samples provide plain-text fallbacks. Commands/status/resources, builtin activation, startup feedback and lifetime ownership are unchanged.

Direct native visuals bypass factory and context creation; absent native factories also create no context. Native null/failure never selects fallback. Null options retain their original distinction from explicit noninteractive options. Renderer context construction stays outside callback catch, invalidation remains success-only including null results, ordinary errors log then diagnose and continue, and OCE escapes. Parent caught a preliminary forwarding change that would have converted legacy asynchronous validation/setup failure into synchronous throws; child restored both shared and terminal `async ValueTask` boundaries with source guards. No outer-task identity or identical-stack guarantee is claimed.

The optional project references Abstractions and the already pinned terminal UI package, not the executable or Orchestration. Generated targets emit an MSBuild `Exists`-conditioned reference with `Private=false`; generation performs no installation probe. The shared ALC list adds the simple assembly name without a shared-project dependency or eager load. Existing generated-file hashes provide invalidation; no new cache policy was introduced. Other abstraction contracts, terminal package lists and mixed builtin constructors remain closure debt.

The sole child delivered24 files (seven new/17 modified), fixtures first and source-only, against `c1fbc8d8`. Parent independently inspected production/test diffs and all selected fixture code, decoded the JSON inverse data, matched17 complete baseline hashes to raw strict UTF-8 Git sources, and applied34 exact occurrence-checked inverses. All51 complete reconstructions (17 files × LF/CRLF/mixed) matched Git; an initial parent audit-script failure was corrected without changing source or anchors. All24 frozen source files and `.gitattributes` matched Git/current content. App remains47,026 bytes in its historical CRLF rendering, strictly below47,064. Feedback14 anchors and earlier guard chains were not edited. Parent added an assertion in the existing ordinary-renderer-failure row proving the next contribution still runs with success-only context invalidation.

**Verification: 50 passed cases /20 exact methods, zero failures/skips.** Selection uses `FullyQualifiedName=<class>.<method>` joined with `|`, Release `--no-build --no-restore`, never a whole-class filter:

| Class | Exact method suffixes (cases) |
| --- | --- |
| `CodeAlta.Plugins.Tests.PluginUiContentRoutingTests` | `ContentRoute_RejectsMissingInputs` (4); `ContentRoute_PreservesEligibilityAndOrder` (5); `ContentRoute_ForwardsOriginalValues` (1); `ContentRoute_PropagatesCallbackFailures` (2); `RendererRoute_RejectsMissingInputs` (5); `RendererRoute_PreservesFilteringAndResults` (5); `RendererRoute_PreservesExceptionAndInvalidationPolicy` (4); `RendererRoute_JoinsOriginalAndPreservesToken` (1) |
| `CodeAlta.Tests.TerminalPluginContributionAdapterTests` | `Selection_RejectsMissingFallbackBeforeNativeCallbacks` (1); `Selection_UsesTerminalOnlyWhenExplicitlySupported` (4); `Selection_PreservesDirectFactoryAndNullPrecedence` (3); `Selection_DoesNotFallbackAfterNativeFailure` (2); `Selection_UsesPortableMarkdownOrText` (2); `RendererSelection_ForwardsOriginalDelegateContextTokenAndTask` (1); `RendererSelection_PreservesOutcomes` (5) |
| `CodeAlta.Tests.PluginUiContentExtractionSourceTests` | `Contracts_RegionContentIsNeutralAndTerminalTypesAreOptional`; `Adapters_UseMandatoryRoutesAndExplicitTuiSelection`; `SourceBuild_ConditionallyReferencesAndSharesOptionalAssembly`; `Preservation_RestoresCompletePreExtractionSources`; `Preservation_LeavesHistoricalChainsAndFrozenBoundariesUnchanged` (each1) |

The 27 Plugins and23 TUI/source cases passed initially and again after the parent assertion and final solution build. Fixtures use actual mandatory production traversal/selection seams with literal DTOs and inert callbacks, no concrete runtime/services/CTS/App/controls/logger acquisition. They retain originals/callers/timed observations, release gates in finally, independently bound cleanup at five seconds and permanently retain observation timeouts. Source/Git/assembly reads and existing writerless assembly logging remain nonzero I/O. Portable format tests preserve DTO identity; actual Markdown/TextBlock rendering was not executed. Runtime-backed contribution-adapter, frontend and MCP fixtures received compile adaptations only and remain excluded, as do startup/config/build/scheduler/native fixtures and whole suites.

Cache-only restore used an empty unique task-owned package source, then removed it nonrecursively. Both targeted builds and `dotnet build src/CodeAlta.slnx -c Release --no-restore` passed zero warnings/errors; desktop RPC/frontend assets and npm dependencies were reported current. Logs: `%TEMP%/codealta-parent-ui-content-{restore,tui-build,plugins-build,tui-tests,plugins-tests,solution-build,final-plugins-tests,final-tui-tests}-20260908.log`. No dependency download/install, application startup, protected-file read, push/publish/merge or compaction was performed.

**Website qualification remains open:** the current instruction prohibits dependency installation. `site/config.scriban` extends remote `lunet-io/templates`; earlier site builds downloaded/installed that theme, and the installed `lunet build --help` has no offline option. The updated public documentation has not been built in this slice. Permit the configured theme download/install or supply an approved offline route before claiming site validation. Installed-tool contents, actual default-ALC identity, absent optional assembly behavior, real regenerated source-plugin builds and native presentation remain unqualified. This is not full terminal-free closure, backend-only builtin loading or desktop parity. M2–M7 remain open; file-search remains deferred.

### M3: neutral typed command key bindings

The sole child delivered a bounded 12-file slice (six new/six modified) against `3825f1ac`, fixtures first and without execution. `PluginKeyGesture` carries a stable named key or Unicode scalar plus explicit logical modifiers; `PluginKeyBinding` validates and copies one through four ordered strokes. Only letters normalize to invariant uppercase, preserving non-letter U+2170 separately from U+2160. The pre-release display/native initializer API is removed; null means unbound and raw control characters must become letter-plus-Ctrl definitions. MCP's Ctrl+G/Ctrl+Y initializer and the existing registry fixture's Ctrl+X initializer were migrated, without executing those runtime-backed callers.

`PluginTerminalKeyBindingMapper.TryMap` in the existing optional assembly explicitly maps all 27 named keys and 16 modifier masks. Ctrl+A–Z uses native control-character encoding without dropping modifiers. Unsupported supplementary scalars reject the entire binding with both outputs null. The actual internal `PluginShellCommandAdapter.CreateCommand` retains the existing command unbound, including metadata, placement, visibility and captured handlers. The actual internal registry iterator emits command-name identity first and at most one structural binding identity, preserving ordered kinds/scalars/full modifier masks and the existing deferred null-registration exception. Conflict warnings do not resolve collisions. Native single-before-prefix precedence, focus/ancestor/global routing, Escape/focus cancellation, 1500ms timeout and shorter-sequence behavior are unchanged. Meta matching remains intact; the native hint formatter still omits Meta and can show a leading `+` for Meta-only gestures.

Parent independently reviewed production changes, all four fixtures and their hooks, then corrected four raw-literal source assertions to canonicalize expected newlines. Six complete Git anchors and 13 mandatory occurrence-checked inverses passed all 18 LF/CRLF/mixed reconstructions. Both PluginContributions and MCP require the key-binding inverse/hash before their unchanged UI-content inverses. All 17 historical Git anchors and 51 UI-content reconstructions matched; the entire previous UI-content fixture, including its payload and legacy async inverses, reconstructs exactly. All 24 frozen boundaries, Feedback14/earlier chains, `.gitattributes`, development guide and historical App budget (47,026 < 47,064 bytes) remain intact. The first parent checker stopped on an incorrect expected constant count (18 versus 16); correcting that checker required no source or anchor changes.

**Verification: 100 passed cases /18 exact methods, zero failures/skips.** Each test command used `FullyQualifiedName=<class>.<method>` joined with `|`, Release `--no-build --no-restore`, not class/suite filters:

| Class | Exact method suffixes (cases) |
| --- | --- |
| `CodeAlta.Plugins.Abstractions.Tests.PluginKeyBindingTests` | `Gesture_ValidatesIdentityAndModifiers` (10); `Gesture_NormalizesLettersWithoutConflatingNamedKeys` (5); `Binding_ValidatesLengthAndCopiesInput` (7); `Formatting_UsesTypedValuesNotIdentityStrings` (4) |
| `CodeAlta.Plugins.Tests.PluginKeyBindingConflictTests` | `ConflictKey_UsesStructuralIdentity` (7); `ConflictKeys_PreserveCommandKeyAndTypedBindingOrder` (3); `ConflictKeys_RejectNullRegistration` (1) |
| `CodeAlta.Tests.TerminalPluginKeyBindingTests` | `Mapping_NamedKeysRemainExplicit` (27); `Mapping_MapsAllModifierCombinations` (16); `Mapping_CharactersUseTerminalEncoding` (5); `Mapping_RejectsWholeUnsupportedBinding` (3); `Mapping_PreservesSequenceOrder` (3); `Mapping_RejectsNull` (1); `CommandAdaptation_UsesActualMappingWithoutAcquiringServices` (4) |
| `CodeAlta.Tests.PluginKeyBindingExtractionSourceTests` | `Contracts_KeyBindingsAreNeutralAndMappingIsOptional`; `Routes_UseActualConflictAndTerminalMappingSeams`; `Preservation_RestoresCompletePreExtractionSources`; `Preservation_PreservesUiContentAndHistoricalChains` (each 1) |

Behavioral fixtures are synchronous value/DTO tests at mandatory production seams. Handlers are captured and inspected, never invoked; no runtime/services/CTS/App/controls/logger are acquired by fixtures. The historical methods called by the preservation test read source only. Repository/Git/assembly reads and writerless assembly logging remain nonzero I/O. Existing runtime-backed registry/MCP/frontend fixtures, real-acquisition/default-path tests, native dispatch and whole suites remain excluded.

All three targeted test-project Release builds and `dotnet build src/CodeAlta.slnx -c Release --no-restore` passed with zero warnings/errors. No restore was needed or invoked; no dependency was fetched/installed. Tool results in the parent session record these runs; no separate log artifacts were created. The website build remains unrun because the configured remote theme has no approved offline route. Dialogs, prompt attachments, projections, shared terminal package/reference lists and mixed builtin backends still prevent full terminal-free closure. No desktop key router/RPC schema, installed-plugin loading, keyboard delivery or cross-head qualification is claimed. M2–M7 and file-search policy remain open/deferred; no push, publish or merge.

### M4: first persisted-workspace browsing vertical

The sole child delivered 14 code/test files against `e2a097fa`, fixtures first, without execution. Parent independently reviewed and integrated the actual CLI → shared durable catalog → generated `workspace.snapshot` RPC → React browsing/selection route. Only Catalog is added as a desktop project reference; Agent is available transitively. Production composes `ProjectCatalog`, `SessionViewJournalStore.CreateSessionStore()` and `IAgentSessionCatalog` backed by `AgentSessionCatalog`, without Hosting, providers, plugins or duplicate metadata discovery. Cache/read errors propagate without header fallback.

Admission requires a new absolute browser `--data-root`; browsing additionally requires an existing absolute trusted task-owned COPY via `--catalog-root` and explicit `--allow-catalog-cache`. Roots cannot overlap or contain `.alta` components. The consent permits existing SQLite cache/sidecar writes, not production-profile access or proof of filesystem isolation. The screen shows project/global-unmatched groups, session selection and persisted metadata, with loading, unconfigured, error, empty and truncation states. Grouping compares exact persisted workspace paths, not active project ownership. Effect cleanup aborts the waiter and suppresses late publication. No refresh/invalidation, history, send/resume, live events or run ownership is implemented; boot still reports `HostAvailable=false`.

Wire projection orders deterministically, caps projects/sessions at 200/500 and uses a conservative 700 KiB accounting budget below the unchanged 1 MiB transport limit. Display labels are shortened at Unicode scalar boundaries; overlong identities/provider keys/paths, duplicate identities and malformed Unicode fail instead of silently aliasing identities. Generated serialization of worst-case escaping stays below the fixture's 768 KiB limit. These response limits do not bound the underlying whole-catalog load or implement paging. The shared catalog uses `Task.Run`/`CancellationToken.None`; canceling the waiter cannot guarantee stopping cache work, and NeoAstra stops waiting after five seconds during invocation teardown. Joined shutdown remains unqualified.

**Parent verification: 61 C# cases /14 exact methods plus seven TypeScript tests passed, zero failures/skips.** C# commands used Release `--no-build --no-restore` and equality filters joined with `|`, not class/suite filters:

| Class (all under `CodeAlta.Desktop.Tests`) | Exact method suffixes (cases) |
| --- | --- |
| `DesktopWorkspaceTests` | `RootAdmission_UsesExplicitSeparatedRoots` (21); `Read_UsesActualCallbacksAndPreservesFailure` (7); `Projection_PreservesMetadataMeaningAndBoundsResponse` (11) |
| `DesktopWorkspaceSourceTests` | `Composition_UsesOnlyExplicitCatalogReads`; `Rpc_UsesGeneratedContractAndActualReadSeam`; `Frontend_UsesRealRpcAndRendersAllStates`; `Boundaries_PreserveTrustAndDocumentReadLimits` (each 1) |
| `DesktopArchitectureTests` | `DesktopAssembly_HasNoTerminalOrHostCompositionReferences`; `DesktopAndTui_HaveSeparatePackageIdentityAndRidIntent`; `DesktopDefaultItems_ExcludeOldOutputsAndFrontendBuildArtifacts`; `NativeLifecycle_KeepsDispatcherAliveAndResetsExitCodeOnDisposalFailure` (each 1) |
| `DesktopStartupTests` | `EarlyFlags_DoNotEnterNativeStartup` (3); `BootRpc_ProjectsOnlyDevelopmentMetadata_WithGeneratedJson` (1); `Navigation_OnlyAllowsControlledApplicationDocument` (10) |

The seven exact `workspace.test.ts` names are: `workspace loading transitions to populated snapshot`; `workspace distinguishes unconfigured from empty`; `workspace reports read failure without exposing exception details`; `workspace ignores completion after abort`; `workspace ignores failure after abort`; `workspace groups by exact persisted path and keeps global or unmatched sessions`; `workspace shows truncation without implying paging`. Invoked only that file with the cached `tsx` CLI. Behavioral fixtures use actual mandatory seams, literal DTOs/existence facts and immediately completed/faulted/canceled work, with originals retained before assertions. No gates, CTS, concrete catalogs/stores/SQLite/runtime/RPC host/App/UI acquisition or native startup occurs. Source/Git/assembly reads remain nonzero I/O. Existing storage-backed CLI methods, including the narrowly adapted root-forwarding test, and all native/runtime fixtures were not selected.

Parent checked nine complete original hashes against raw Git bytes and all 13 mandatory inverses under LF/CRLF/mixed endings. All 24 historical frozen hashes and 46 complete historical sources (including the 17 UI-content inverse inputs and keybinding inputs/guard chains) match the previously qualified `e2a097fa` state. No historical inverse/hash was rebased. App is still 47,026 bytes, strictly below 47,064; development guide, source decoder and attributes are unchanged. An initial audit-script diagnostic used culture-sensitive BOM comparison; ordinal comparison corrected the checker, with no source/anchor changes.

Cached desktop/test restore graphs lacked the new Catalog/Agent entries. Parent refreshed them with a unique explicit NuGet config clearing all package/audit sources and `NuGetAudit=false`, using already cached packages only. No dependency was fetched/installed and no application profile or credential configuration was opened. Targeted and solution Release builds passed with zero warnings/errors; frontend restore/build/client-staging targets were disabled explicitly. Generated contract validation passed. Cached `tsc --noEmit` and Vite production build passed; Vite wrote to new `%TEMP%/codealta-workspace-offline-01a08087/frontend-dist` rather than emptying existing frontend output. Tool results in the parent session record verification; no separate test logs were created.

This is **not native, packaged-asset, actual-cache, shared-root or full-suite qualification**. No application was launched and no catalog/profile was opened during tests. Website build remains unrun because its remote theme has no approved offline route. M2 races/platform checks, remaining M3 closure, M4 execution/recovery and M5–M7 parity remain open; the plan defines no M8. No push, publish or merge.

### M3: GitHub backend/picker separation and builtin activation (2026-09-08)

The same sole writing child delivered15 original files (including three moves) and six new files against `d32391ae`, fixtures first and without execution. Parent independently reviewed every new fixture/helper and production delta, the actual activation and contribution routes, and the complete-source inverse maps. The builtin manager now passes the registration factory into actual activation; source plugins retain executable reflection construction. Assignment precedes null/exact-type validation inside the existing failure boundary. A supplied factory is called once per activation attempt without fallback after null/throw/cancellation; legacy metadata resolution may call it separately unless `PluginType` is explicit. Direct exceptions differ from reflection wrapping, and the existing cancellation exclusion/cleanup limitations are not repaired by this change.

GitHub's parameterless backend has no prompt presentation. The TUI injects a deferred neutral contribution factory receiving that exact backend; attachment creation remains deferred and does not initialize/dispose another backend. Attachment, picker dialog, parser and binding accessors now live under `CodeAlta.Tui/Plugins/GitHub/`. The backend retains its issue record, operations, authentication, HTTP/CLI and disposal bodies. Its project no longer references terminal UI or Plugins.Tui; refreshed source-disabled/cached-only NuGet assets confirm no terminal/optional-TUI/NeoAstra transitive libraries. MCP/Statistics remain separate pending extractions, although their existing explicit factories now participate in the corrected actual activation route.

**Parent qualification:30 exact methods/cases passed, zero failures/skips.** Equality filters were joined with `|`; no classes/suites were selected:

| Fully qualified class | Exact method suffixes |
| --- | --- |
| `CodeAlta.Plugins.Tests.PluginBuiltInFactorySelectionTests` | `SuppliedFactory_ReturnsExactInstanceWithoutFallback`; `SuppliedFactory_IsInvokedOncePerAttempt`; `MissingFactory_InvokesFallbackOnce`; `NullFactoryResult_DoesNotInvokeFallback`; `FactoryException_PropagatesWithoutFallback`; `FactoryCancellation_PropagatesWithoutFallback` |
| `CodeAlta.Tests.PluginGitHubCompositionTests` | `PlainBackend_HasNoPromptPresentation`; `InjectedFactory_IsDeferredUntilEnumeration`; `InjectedFactory_ReceivesSameBackendAndPreservesSequence`; `InjectedFactory_PropagatesFailureWithoutFallback`; `InjectedConstructor_RejectsNull`; `TerminalFactory_PreservesContributionMetadataWithoutAttachment` |
| `CodeAlta.Tests.PluginGitHubBackendSeparationSourceTests` | `Activation_BuiltInsSupplyFactoryAndSourcesRetainReflection`; `Activation_PreservesIdentityOrderingAndFailureDisposalBoundary`; `Composition_TuiInjectsPickerIntoSameBackendInstance`; `PromptEditor_UsesExistingTypedAdmissionAndBorrowedAttachmentLifetime`; `Dependencies_GitHubBackendContainsNoTerminalReferences`; `Presentation_MovesParserDialogAndAccessorsWithoutBehaviorChanges`; `Preservation_RestoresCompleteOriginalsAcrossNewlineVariants`; `Preservation_ComposesEveryFrozenHistoricalChain` |
| `CodeAlta.Tests.PluginAuthoringProfileSourceTests` | `Routes_PropagateExplicitProfileThroughBothTuiEntries`; `Routes_UseOneProfileForGenerationAndTheExistingLoader`; `Generation_FailedRootsCannotReachBuildOrCachedLoad`; `Cache_UsesStampedGeneratedFilesBeforeBothFastPaths`; `Loader_PreflightsMetadataBeforeDiscoveryWithoutActivation`; `Loading_ReservesTerminalIdentityWithoutPrivateFallback`; `Preservation_RestoresCompleteOriginalsAcrossNewlineVariants`; `Preservation_ComposesEveryFrozenHistoricalChain` |
| `CodeAlta.Desktop.Tests.DesktopHistorySourceTests` | `Boundaries_ReconstructWholeSourcesAndPreserveHistoricalChains` |
| `CodeAlta.Desktop.Tests.DesktopArchitectureTests` | `DesktopAssembly_HasNoTerminalOrHostCompositionReferences` |

The six factory cases use only inert callbacks and an unattached fake `PluginBase`; six composition cases admit only explicitly uninitialized GitHub constructors/iterators and the real neutral contribution factory without invoking Attach. No concrete activator/manager/context/services/CTS/native/accessor/auth/query/CLI/runtime startup or disposal runs. Writerless test-assembly logging remains active. Source methods perform named checkout reads/root probes, XML parsing and explicit historical fixture calls, not production execution. The new fixture directly reads27 distinct named sources:15 originals at actual destinations, two new production files, the new inverse helper and nine supporting abstraction/TUI/adapter/project sources. Parent's preliminary26-path inventory missed the final handoff's explicit inverse-source map assertions; final review included them before exact requalification. The three old moved-path existence checks remain explicit. Transitive execution retains actual profile→neutral→projection, both keybinding, both UI-content, feedback and separately Desktop history→workspace preservation chains.

All15 complete original hashes independently match strict raw-Git `d32391ae` bytes. Thirty mandatory context-bearing inverse edits plus two unchanged-body moves pass45 LF/CRLF/mixed whole-source reconstructions. Profile pre-restoration is confined to manager/whole neutral inverse/Desktop project; neutral pre-restoration is confined to four affected GitHub originals. Raw current-boundary assertions are separate from historical restoration; no reader substitutes reconstructed backend code as current evidence. Workspace/History readers and corrected Frozen loop, old hashes/payloads, strict decoder, guide/attributes and App47026<47064 remain preserved. Before execution, the parent requested expected-literal canonicalization only in the new source fixture; the child also corrected assertion indentation/ordering during source review. No build or test failed in parent qualification.

Three targeted and full-solution Release builds passed with zero warnings/errors. Restore used the existing explicit source-disabled NuGet config with audit disabled; builds disabled both NeoAstra frontend targets, and tests used `--no-build --no-restore`. Logs are `%TEMP%/codealta-parent-github-{restore,factory-tests,composition-tests,source-tests,profile-tests,desktop-tests,solution-build}-20260908.log` and `codealta-parent-github-{CodeAlta.Plugins.Tests,CodeAlta.Tests,CodeAlta.Desktop.Tests}-build-20260908.log`. Public/internal plugin migration and runtime skill guidance are updated. Website remains unrun because of its remote-theme acquisition requirement. Actual CLR/activation/native behavior, desktop picker, shared-profile startup and M3–M7 completion are not claimed; continue subsequent builtin and desktop work without treating this substep as the plan exit.

### M3: MCP backend/terminal presentation separation (2026-09-08)

The sole child implemented fixtures first, without execution, against `60a18cde`. The parent independently audited and qualified the result. Parameterless MCP construction now retains only backend owners and portable status, without native state or interactive commands. Explicit TUI composition borrows the same management/activation owners through an internal neutral delegate carrier and assembly-wide `altatui` friendship. Native command/status code, dialog, bindable rows and icons belong to `CodeAlta.Tui/Plugins/Mcp/`; no reverse dependency or new project/package was added. Only two existing backend helper visibilities changed. Actual registered-factory activation uses the previously qualified selection route; this slice does not execute its lifecycle.

Status decoration preserves the exact portable callback and metadata without evaluating content. Neutral content retains project/snapshot/visibility/scope resolution followed by tool counts then active servers. Native construction retains its initial snapshot before controls; each subsequent markup and tone evaluation independently resolves snapshot, active servers, then tool counts. Revision state and its synchronous subscription move together, preserving dispatcher acquisition, direct versus posted increment, exception boundaries and existing lifetime without a new unsubscribe/disposal policy. Backend commands, management/activation implementations and prompt discovery's separate temporary management service remain unchanged.

**Parent-admitted exact methods, all passing (39 total, zero failed/skipped):**

| Fixture prefix | Exact method suffixes |
| --- | --- |
| `CodeAlta.Tests.PluginMcpPresentationTests` | `NeutralBackend_HasNoCommandsAndPortableStatus`; `Construction_ValidatesFactoryAndPreservesInvocation`; `Commands_AreDeferredAndPreserveSequence`; `Commands_PropagateFactoryAndSequenceFailures`; `StatusDecoration_ReceivesExactContentOnceWithoutEvaluatingContent`; `StatusDecoration_PropagatesNullAndFailuresWithoutFallback`; `TerminalDecoration_PreservesMetadataAndDeferredCallbacks`; `Revision_DirectAccessIncrementsWithoutPosting`; `Revision_NoAccessPostsWithoutEagerIncrement`; `Revision_CheckAccessAndDirectIncrementFailuresEscape`; `Revision_PostInvalidOperationIsIgnored`; `Revision_OtherPostFailuresEscape`; `Revision_DeferredIncrementFailureEscapesAfterPosting` |
| `CodeAlta.Tests.PluginMcpBackendSeparationSourceTests` | `Composition_TuiCreatesOnePresentationFromOwnedBackendServices`; `Commands_PreserveMetadataAndDeferredDialogInvocation`; `Status_PreservesNeutralFallbackAndSnapshotReadOrder`; `Revision_MovesStateAndSynchronousSubscriptionWithDispatchPolicy`; `Dependencies_KeepNativeDialogRowsAndIconsInTui`; `LegacyTests_RerouteOnlyNativeStatusEntryPoints`; `Preservation_RestoresCompleteOriginalsAcrossNewlineVariants`; `Preservation_ComposesEveryFrozenHistoricalChain` |
| Existing GitHub/profile source fixtures | The eight exact `PluginGitHubBackendSeparationSourceTests` and eight exact `PluginAuthoringProfileSourceTests` methods listed in the preceding GitHub/profile evidence; selected individually by full name, not class filters. |
| `CodeAlta.Desktop.Tests` | `DesktopHistorySourceTests.Boundaries_ReconstructWholeSourcesAndPreserveHistoricalChains`; `DesktopArchitectureTests.DesktopAssembly_HasNoTerminalOrHostCompositionReferences` |

The 13 new inert cases admit normally constructed but lifecycle-uninitialized MCP objects, iterator enumeration and literal callbacks only. Parent review covered `PluginBase`, management/request/helper and activation initializers, the full terminal module's separate noninitializing stateless seams, inherited contribution records and writerless assembly logging. No backend context, content, visual, command handler, management/activation operation, snapshot/configuration, native state/icon/binding/dispatcher, OAuth/token/cache/listener or runtime operation was invoked. Both mechanically migrated native status tests remain excluded. Parent source inventory confirmed `McpOAuthOptions.cs` before admitting its text-only read.

Preservation covers 14 complete originals, including two intact moves, plus five new files. All 14 hashes independently match strict canonical raw Git `60a18cde`; 26 mandatory occurrence-checked inverse edits pass all 42 LF/CRLF/mixed reconstructions. The new fixture has 43 named direct source inputs: 14 originals at actual destinations, three new sources and 26 unchanged supporting sources. Newest-step restoration maps remain disjoint: five GitHub inputs, whole UI fixture through Profile, KeyBinding fixture/dialog through Neutral, MCP plugin through KeyBinding, and MCP project/whole native-test fixture through UI content. Eleven mapped and five unmapped assertions protect those routes. Actual GitHub→Profile→Neutral→projection/keybinding/UI/feedback chains and the separately selected Desktop history→Workspace chain pass without rebasing hashes/payloads or changing Workspace/History readers/Frozen loop. Current boundary assertions read actual current source; only the old GitHub fixture's MCP factory expectation reconstructs its historical registry explicitly.

Source-disabled cached-only restore refreshed MCP/TUI/main-test assets with NuGet audit disabled. MCP's 19-library transitive asset graph contains no terminal/Plugins.Tui/NeoAstra entries. Main-test and Desktop-test targeted builds and the full solution Release build passed with zero warnings/errors, with both NeoAstra frontend targets disabled. This compiles relocated bindable-row accessors in the TUI; it does not execute them. Tests used `--no-build --no-restore` and exact `FullyQualifiedName=` selections. Logs are `%TEMP%/codealta-parent-mcp-{restore,main-build,inert-tests,source-tests,github-source-tests,profile-source-tests,desktop-build,desktop-tests,solution-build}-20260908.log`. No parent verification command failed.

Internal/public migration docs and the bundled runtime skill are updated. App remains 47,026 bytes, strictly below 47,064; strict decoding, guide/attributes, architecture guard and unrelated work remain preserved. Website execution remains deferred because its theme can require remote acquisition. Native behavior, real loading/activation, default-profile startup, desktop MCP management, Statistics separation and broader M3–M7 completion are not claimed. Continue with Statistics and the remaining plan rather than treating this bounded extraction as completion.

Final staged review confirmed both moves as unchanged and reread the complete inert fixture, including its added null-command-sequence assertion. The exact `Commands_PropagateFactoryAndSequenceFailures` method passed again after that review (`codealta-parent-mcp-final-inert-case-20260908.log`); this adds no runtime/configuration/native admission and does not increase the 39 distinct-method count.

### M3: Statistics backend/terminal presentation separation (2026-09-08)

The parameterless Statistics backend now returns neutral transient events and detail sections. An internal constructor stores a borrowed decorator without invoking it; TUI builtin registration supplies the production `StatisticsTerminalContributions.DecorateProjection` route. The decorator runs inside each existing `GetOrAdd` candidate, after eager portable summary/details/payload creation. Sequential cache reuse retains the decorated event; concurrent candidates may each decorate. Null results have no portable fallback, and exceptions/cancellation propagate without cache insertion or translation.

`StatisticsPresentation` carries the plain title and four deferred suffix/table callbacks over the same already-built private turn. The private model graph and Markdown renderer remain backend-owned. Actual terminal controls and ANSI summary styling moved into `CodeAlta.Tui/Plugins/Statistics/`; card construction retains suffix → ANSI escaping/styling → header → metric → optional usage → optional bucket → styled stack → collapsed card order. Detail-only invocation omits the summary. The stateless decorator copies all event/detail metadata and retains payload/dynamic references without reading them or invoking callbacks. Internal assembly friendship to `altatui` and `CodeAlta.Tests` is not a public model API or reverse project reference.

Parent-qualified exact selections (46 methods/cases, zero failed/skipped):

| Fixture | Exact methods |
| --- | --- |
| `CodeAlta.Tests.PluginStatisticsPresentationTests` | `Construction_BackendAndInjectedCompositionAreDeferred`; `Construction_RejectsNullDecorator`; `Projection_EmptyAndIncompleteBatchesDoNotDecorate`; `Projection_NullContextAndCancellationRetainOrder`; `Projection_PlainBackendEmitsPortableReadyEvent`; `Projection_DecoratesCandidateWithoutNativeFormatting`; `Projection_ReusesCachedDecoratedEventForUnchangedTurn`; `Projection_SeparatesSessionAndFingerprintCacheKeys`; `Projection_DecoratorFailureAndCancellationPropagateWithoutCaching`; `Projection_NullDecoratorResultHasNoPortableFallback`; `TerminalDecoration_PreservesEventPayloadAndDetailMetadata`; `TerminalDecoration_DefersCardAndDetailCallbacks` |
| `CodeAlta.Tests.PluginStatisticsBackendSeparationSourceTests` | `Composition_TuiDecoratesCandidatesFromSameBackend`; `Projection_PreservesCalculationsPayloadAndCacheSemantics`; `Rendering_PreservesDeferredFormattingOrderAndStyles`; `Dependencies_StatisticsAndPluginTestsRemainTerminalFree`; `LegacyTests_MoveFixtureAndComposeOnlyNativeCase`; `Routing_PreservesDeferredNativeFactorySelection`; `Preservation_RestoresCompleteOriginalsAcrossNewlineVariants`; `Preservation_ComposesEveryFrozenHistoricalChain` |
| Historical checks | All eight previously qualified exact methods in each MCP, GitHub and authoring-profile source fixture, as enumerated in their checkpoints above; no whole-class selection. |
| Desktop checks | `CodeAlta.Desktop.Tests.DesktopHistorySourceTests.Boundaries_ReconstructWholeSourcesAndPreserveHistoricalChains`; `CodeAlta.Desktop.Tests.DesktopArchitectureTests.DesktopAssembly_HasNoTerminalOrHostCompositionReferences` |

The new inert cases use normally constructed but lifecycle-uninitialized backends, literal neutral context/handle/event records, fixed timestamps, null Details/Usage and synchronous empty/incomplete/Idle-only inputs. Parent audited base/record/field initializers, pending-turn fingerprints, the reachable turn/statistics/usage/compaction/token-estimation/portable-formatting closure, the stateless decorator and writerless assembly logging before execution. No context attachment, services, JSON/provider lookup, legacy helper, scheduling, CLI, runtime/profile, native text callback or visual construction ran. The complete old `StatisticsPluginTests.cs` moved from Plugins.Tests into existing TUI tests, namespace unchanged, with only its one native test's explicit composition changed; that native test and coordinator rendering cases remain excluded.

Preservation covers ten complete originals (one whole-fixture move), six new files and17 mandatory context-bearing occurrence-checked inverse edits. Parent independently matched every hash to strict canonical raw Git `fba989a92a8114dbea4e51a9fe0a7575786af53d` and the moved fixture to its exact one-edit original. All30 LF/CRLF/mixed reconstructions pass, along with actual MCP→GitHub→Profile→Neutral→Projection/Key/UI/Feedback and separate Desktop history→Workspace chains. The new fixture has25 direct content inputs plus75 distinct inherited content inputs; the source-only child received the unchanged UI payload's17-path inventory from the parent rather than executing it. Newest-step pre-maps restore five MCP inputs, Statistics project/whole Projection fixture through Neutral after existing MCP adjacency, and Statistics backend/moved fixture through Projection. Current readers remain current except the named moved-fixture routing; historical native/factory operands reconstruct explicitly. Frozen hashes/payloads, decoder, Workspace/History/Frozen readers and coordinator anchors remain unchanged. Parent caught and the child corrected an ambiguous renderer suffix assertion before execution, retaining an exact context-bearing adaptation against the actual relocated renderer body.

Source-disabled cached-only restore refreshed Statistics/TUI/main-test/runtime-test assets with NuGet audit disabled. Statistics has16 transitive libraries and Plugins.Tests37; neither graph contains terminal, optional-TUI or NeoAstra libraries. Both targeted and full-solution Release builds passed0 warnings/errors with `--no-restore` and both NeoAstra frontend targets disabled. Exact tests used `--no-build --no-restore` and `FullyQualifiedName=` filters. Logs: `%TEMP%/codealta-parent-statistics-{restore,main-build,inert-tests,source-tests,PluginMcpBackendSeparationSourceTests,PluginGitHubBackendSeparationSourceTests,PluginAuthoringProfileSourceTests,desktop-build,desktop-tests,solution-build}-20260908.log`. No verification command failed.

Migration docs and bundled skill are updated. App remains47,026 bytes, strictly below47,064; guide/attributes/strict decoder/architecture guard and unrelated work remain preserved. Website qualification remains deferred because its theme can require remote acquisition. This closes the bounded Statistics extraction, not all M3–M7 work: actual builtin loading/activation without terminal assemblies, native behavior, desktop adapters/runtime ownership and full parity remain unqualified.

## Explicit discovery-home and instruction-ancestry prerequisite (2026-09-08)

After Statistics `4a57dfea`, the same sole child implemented nine originals/four new files,
fixtures first and without execution. Parent independently audited and qualified the result.
`SessionDiscoveryScope` supplies immutable fully qualified home/boundary paths; scoped host
creation requires explicit global/project roots before bootstrap. Runtime derives its scope
from the exact template provider without changing its constructor signature. The provider's
old four-optional-argument constructor forwards to its new overload with null scope.
Three runtime and two template home expressions, and both builder discovery contexts, carry
the scope. Supplied working/project roots are validated before persistence, filtering,
prompt-catalog early returns and instruction probes. Existing callers retain ambient defaults.

The production instruction walk uses the source-linked pure ancestry implementation.
Scoped walks include the boundary and stop there. Parent identified and required preservation
of the legacy unscoped trailing-separator entry; the corresponding inert regression passes.
File selection, ordering and deduplication remain source-preserved. This is lexical policy,
not link/reparse protection, filesystem ownership, tool authorization or a sandbox.

Parent-selected exact cases (54 passed, zero failed/skipped after the correction below):

| Fixture | Exact methods |
| --- | --- |
| `CodeAlta.Desktop.Tests.SessionDiscoveryScopeTests` | `Constructor_RejectsMissingOrNonAbsoluteRoots`; `Constructor_NormalizesLexicalRootsAndPreservesVolumeRoots`; `ValidateHostRoots_RequiresExplicitAbsoluteGlobalAndProjectRoots`; `ValidateHostRoots_DoesNotDeriveDiscoveryHomeOrBoundary`; `ValidateProjectPath_AcceptsBoundaryAndDescendants`; `ValidateProjectPath_RejectsSiblingPrefixesAndNormalizedEscapes`; `ValidateProjectPath_UsesPlatformPathComparison`; `GetInstructionAncestors_ScopedWalkIncludesBoundaryInRootToLeafOrder`; `GetInstructionAncestors_RejectsInvalidRootsWithoutExistenceChecks`; `GetInstructionAncestors_UnscopedWalkPreservesFullAncestry` |
| `CodeAlta.Tests.SessionDiscoveryScopeSourceTests` | `Host_ValidatesScopeBeforeBootstrapAndPropagatesSameInstance`; `Constructors_PreserveExistingSignaturesAndAmbientFallback`; `Runtime_ValidatesBeforePersistenceAndDiscoveryAndReplacesThreeHomeReads`; `TemplateProvider_ValidatesBeforeSkillsAndPropagatesScopeAndBothHomeReads`; `PromptBuilder_ValidatesEveryRootBeforeDiscoveryOrExistenceProbes`; `InstructionWalk_UsesProductionPureAncestryAndPreservesSelectionOrder`; `Preservation_RestoresAllNineWholeOriginalsAcrossNewlineRepresentations`; `Preservation_RejectsMissingDuplicateAndUnrelatedSourceChanges`; `Preservation_NewestPreMapsPreserveInheritedChains` |
| Historical source checks | The eight previously qualified exact methods in each Statistics, MCP, GitHub and authoring-profile source fixture, enumerated above. Parent checked the four whole fixtures against HEAD before selecting their32 individual equality operands. |
| Runtime source guard | `CodeAlta.Tests.ArchitectureGuardrailTests.SessionRuntimeService_AbortRoutesThroughPerSessionActor` |
| Desktop checks | `CodeAlta.Desktop.Tests.DesktopHistorySourceTests.Boundaries_ReconstructWholeSourcesAndPreserveHistoricalChains`; `CodeAlta.Desktop.Tests.DesktopArchitectureTests.DesktopAssembly_HasNoTerminalOrHostCompositionReferences` |

The inert methods use only synthetic absolute strings and the production scope's lexical
methods; no directories, filesystem probes, environment/profile lookup or runtime objects.
The new source fixture reads exactly ten allowlisted files: its nine originals plus the new
production scope. Its invoked inverse methods only transform supplied strings (zero transitive
content reads). Separately selected historical fixtures retain their previously audited named
read inventories and checkout-marker probes; main-test writerless logging remains active.

All nine anchors independently match strict canonical raw Git
`4a57dfeaf313cc63b517a05448fffcb8d1e7a5a0`. The new helper owns43 occurrence-checked tuples,
51 replacement occurrences and27 whole-original LF/CRLF/mixed reconstructions. Pre-maps precede
the existing Decode statements, preserving old fragment adjacency: Profile restores Host/Options,
MCP restores whole Profile inverse, Statistics restores whole MCP inverse/Desktop project.
Unknown direct restoration fails; no optional inverses, hash/payload rebasing or historical
reader substitution. Full inherited and Desktop preservation chains pass.

First source run returned nine passes/one failure: its duplicate-input negative test appended
a partial signature without a final newline. The strict decoder correctly rejected that input
before the intended duplicate-count assertion. The sole child added final LF to that test input;
expected exception, decoder, production and inverses stayed unchanged. Rebuild and all ten exact
source/abort cases then passed. Both targeted and full-solution Release builds passed zero
warnings/errors using `--no-restore` with both NeoAstra frontend targets disabled. Tests used
`--no-build --no-restore` and individual `FullyQualifiedName=` operands. No restore or acquisition
was needed. Logs/filters: `%TEMP%/codealta-parent-discovery-*` (desktop/main builds, inert,
source/retest, historical, Desktop regressions and solution build).

App47026 remains below47064; guide/attributes/decoder/architecture guard and unrelated work
remain preserved. Runtime docs and qualification evidence are updated; no end-user frontend
behavior changed. Website remains unrun because of remote-theme acquisition. Real host/storage,
providers, native/plugin loading and Desktop session activation remain excluded. Builtin-skill
assembly-ancestor lookup and Git-config discovery are still ambient paths; actor waiter
cancellation and discarded provider-event forwarding require separate lifetime closure.
The next same-child assignment is read-only real-host owned-command closure, not an authorized
runtime fixture or another writing slice. This prerequisite does not complete M3–M7.

## Owned-command prerequisite audit (2026-09-08, not execution admission)

Discovery was committed as `9d17c067`. The same sole child remains read-only while the parent
closes real-host content, store, provider and lifetime routes. No command owner or Desktop
activation has been implemented in this follow-on audit.

Parent used the already installed ILSpy command with `--disable-updatecheck`, without fetching
tools or loading/executing target code. Decompiled `RepositoryDiscovery` and `GitConfigReader`
from the actual Orchestration.Tests output match the relevant local-source control flow:
an existing skill-root `.git/config` with valid `core.ignorecase` and relative
`core.excludesfile = fixture.ignore` returns before the lazy iterator accesses ambient Git
configuration/home. Every existing fixture skill root needs that local repository boundary;
nonexistent roots are skipped before discovery. This is not a general filesystem sandbox.

The following SHA256 values matched output and the assets-selected cached package files:

| Artifact | Package asset | SHA256 |
| --- | --- | --- |
| `XenoAtom.Glob.dll` | Glob1.0.0, `lib/net10.0` | `6783F04D89389907260B7EF8A395F790CEBD188C227367E10FC0AE76BB07901B` |
| `Microsoft.Data.Sqlite.dll` | Sqlite.Core10.0.11, `lib/net8.0` | `4ABD9C2A61E580EB853E93CA8953A3CEF2C05714AE28D2D1859D4DBC5E5700BC` |
| `SQLitePCLRaw.batteries_v2.dll` | bundle_e_sqlite3 2.1.12, `lib/netstandard2.0` | `815CE410C9EAB531DD219318E3BA16C5E38E83D361CFBBDBB4BBCA222C497B0B` |
| `SQLitePCLRaw.provider.e_sqlite3.dll` | provider_e_sqlite3 2.1.12, `lib/net6.0` | `DF996BCAE63C2CABCFE8D4B9C5110EB39A2A4706178B07D875665391C6EB3995` |
| `e_sqlite3.dll` | lib.e_sqlite3 2.1.12, `runtimes/win-x64/native` | `B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E` |

An initial provider comparison incorrectly selected netstandard2.0 and failed; the restored
assets explicitly select net6.0, whose comparison passed without changing files. Decompiled
SqliteConnection initializes Batteries and probes WinRT application-data types on Windows;
the selected provider uses `DllImport("e_sqlite3")`. The existing cache opens explicit absolute
database paths with private cache, pooling disabled and zero busy timeout. Nevertheless,
SqliteConnectionFactory has process-wide state and a prune timer even without pooling. These
library facts are not proof of actual native resolution, fixture isolation or host shutdown.

Parent and child independently confirmed that joining whole Runtime.Send also waits its
`ClearActiveRun(None)` actor query after cancellation, but does not necessarily observe a
faulted original reply task whose waiter was canceled. The proposed narrow owner route therefore
retains initial preparation with no waiter cancellation and separates coordination-wait from
provider-execution cancellation internally, preserving the public Send contract and real Abort
route. Direct journal lookup avoids AgentSessionCatalog's unretained listing load. This remains
a specification decision: six originals were anchored, but no new fixture execution, production
edit, default-root startup, provider authentication, native activation or broader parity is claimed.

Subsequent source handoff closes six originals/five new files and is authorized for the same
sole writer, without any execution. The parent rejected the proposed private-host wrapper:
the existing CodeAltaHost will own Commands and join it before runtime/dependency disposal.
The new service will not create a host or expose one; configured real provider registration,
not a test-only allowed-provider policy, determines available providers. Receipt capacity
defaults to256 and validates before acquisition. The new fixtures remain subject to complete
parent audit and exact-method admission after implementation. Seven shipped content files
matched their tracked sources and exact output inventory; only coordinator, both agent prompts
and the default system prompt are read by the proposed route. Cached Logging1.2.0 metadata
also confirms GetLogger does not initialize a processor when no manager is initialized;
fixture host logging ownership stays off. None of these checks executes the real-host fixture.

### Owned-command implementation and first qualification attempt

The implemented source inventory is eight existing files plus five new files. Existing Host
owns `Commands`; admission retains immutable text-send/abort receipts, reserves an active
session before lookup, and joins preparation, send and attachment-aware control before runtime
disposal. The direct resolver uses the existing journal store and recovery helpers. Public
runtime Send and actual Abort behavior remain intact; coordination waits are separated from
owned execution cancellation. This is not Desktop activation or transcript completion.

The final preservation inventory has eight unchanged anchors and 26 mandatory context-bearing
inverse tuples/occurrences, including the unchanged historical lifetime readers and frozen
chains. Parent requalification passed 55 exact source methods, two Desktop history/assembly
methods, five pure methods and a clean Release solution build. Explicit `PluginEnvironment`
copies a supplied map with case-insensitive keys; null preserves the ambient snapshot default.
It is not process/provider isolation.

After the separate complete route audit, parent admitted only
`CodeAlta.Orchestration.Tests.OwnedSessionCommandServiceTests.AdmitSend_UsesRealHostRuntimeAndRegisteredSessionProvider`.
Its first run failed with eight timeout observations (one failed, zero passed). The fixture
root remains retained as recorded in the plan. Parent and child independently traced a
header-only seeding defect: the direct store requires a genuine agent summary, and view-header
cache writes cannot insert a row. Source predicts `Failed / preparation_failed`, but the
test awaited provider readiness without logging that receipt. No provider-deadlock or
shutdown-success claim follows from this failure. Same-child fixture-only correction seeds
summary/local state through existing APIs, verifies readback and races readiness against its
receipt. Parent review/re-admission and all other real-route qualification remain pending.

Final correction and qualification: only the new runtime fixture changed. One journal seeds
header, genuine agent summary and local state, followed by retained metadata/cache readback.
All 19 readiness observations race their receipt and report stable outcome/code on early
completion; bare notifications are not mistaken for executable work. Unexpected admissions
are retained before assertions. Cleanup joins finite, independently bounded observations and
deletes roots only with confirmed work and zero failures.

Parent reviewed the correction and existing API I/O closure, rebuilt, and re-admitted the first
real method, which passed. Following separate review/admission of each remaining method, the
exact 17 real-route plus five pure methods passed with no failures/skips. The eight exact owned
source methods and full Release solution build passed again (zero warnings/errors), preserving
the previously qualified historical chains. App remains 47,026 bytes; guide, attributes,
architecture guard and strict decoder are unchanged. The original failed root is retained.
No frontend/default-profile/real-provider startup, website acquisition, plugin loading or
broader native qualification occurred. The same child continues read-only on the next useful
M4 Desktop operation/lifecycle vertical while the parent integrates this bounded slice; M3–M7
are not complete.

### Explicit Desktop text submission and owned cached reads — 2026-09-08

Implemented separately consented existing-session text submission, submission-specific abort,
bounded receipt recovery and direct cached workspace/history reads. Browser/catalog-only modes
remain unchanged. `CodeAltaHost` owns both commands and reads; Desktop borrows them rather than
creating another application owner. The full CLI/root/consent and shutdown limitations are in
[`src/CodeAlta/README.md`](../src/CodeAlta/README.md#explicit-owned-text-submission) and
[`runtime.md`](runtime.md#explicit-desktop-submissions-and-owned-reads).

Read admission retains eight actual uncancelled operations, rejects excess and joins both
command/read drains before runtime dependencies. Presentation remains bounded at200 projects,
500 sessions/700KiB; underlying enumeration is not bounded by those limits. Receipt references
are retained before projection, never evicted, capped by the host's256-receipt owner; pages
contain64 rows. Expected epochs validate before mutations. Canonical lowercase36-unit GUIDs
and Unicode/string bounds support6400 bytes/row plus8192 envelope bytes (417792 total), below
448KiB. Actual generated-JSON worst-case tests pass. NeoAstra's208KiB setting limits owned-host
inbound frames only, not responses or individual methods.

Frontend retries retain exactly epoch/key/session/text; refresh is manual, no polling or
automatic resend. App owns epoch validity across panel remounts. Stale selection/unmount
suppresses late success/failure callbacks. Reload recovers receipts, not prompt text; labels
distinguish submission dispatch from run completion. No runtime event reader was introduced.

The parent independently audited written fixtures, read/I/O closure and cleanup before exact
execution. **40 C# cases plus five TypeScript helper cases passed, zero failures/skips.** C#
commands used `dotnet test <project> -c Release --no-build --no-restore` with explicit
`FullyQualifiedName=` disjunctions, never suite/class filters:

- `CodeAlta.Orchestration.Tests.OwnedSessionWorkspaceTests`: `Snapshot_FullyJoinsDirectReadsAfterCallerCancellation`, `ReadCapacity_RejectsWithoutLaunchingAdditionalWork`, `Disposal_AttemptsAllReadsAndObservesFailures`, `History_RetainsActualReadThroughWaiterCancellation`, `HostDisposal_ClosesReadAdmissionAndStartsBothDrains`, `HostDisposal_JoinsReadsBeforeRuntimeDependencies`, `HostDisposal_AttemptsRuntimeAfterSettledReadFailure`.
- `CodeAlta.Desktop.Tests.DesktopOwnedSessionTests`: `OwnedFlags_RequireCompleteExplicitConsentBeforeAcquisition`, `LegacyBranches_DoNotComposeOwnedHost`, `StaleEpoch_RejectsBeforeOwnerAdmission`, `Admission_RetainsReceiptAndPreservesOwnerReplay`, `ReceiptPages_BoundWorstCaseGeneratedJson`, `Shutdown_RetainsLeaseUntilAllOwnedWorkIsConfirmed`, `OwnedRpc_UsesRealHostCachedStoreAndFakeProvider`.
- `CodeAlta.Desktop.Tests.DesktopOwnedSessionSourceTests`: `Composition_UsesSingleHostAndOnlyCachedDirectReads`, `Frontend_PreservesLegacyHistoryAndUsesEpochBoundMutations`, `Boundaries_RestoreWholeOriginalsAndHistoricalReaders`.
- `CodeAlta.Desktop.Tests.SessionDiscoveryScopeTests`: `Constructor_RejectsMissingOrNonAbsoluteRoots`, `Constructor_NormalizesLexicalRootsAndPreservesVolumeRoots`, `ValidateHostRoots_RequiresExplicitAbsoluteGlobalAndProjectRoots`, `ValidateHostRoots_DoesNotDeriveDiscoveryHomeOrBoundary`, `ValidateProjectPath_AcceptsBoundaryAndDescendants`, `ValidateProjectPath_RejectsSiblingPrefixesAndNormalizedEscapes`, `ValidateProjectPath_UsesPlatformPathComparison`, `GetInstructionAncestors_ScopedWalkIncludesBoundaryInRootToLeafOrder`, `GetInstructionAncestors_RejectsInvalidRootsWithoutExistenceChecks`, `GetInstructionAncestors_UnscopedWalkPreservesFullAncestry`.
- `CodeAlta.Tests.OwnedSessionCommandSourceTests`: `Requests_AreScalarOnlyAndOwnerDoesNotExposeHost`, `Host_UsesExplicitBuiltInRootWithoutChangingDefaultProviders`, `Runtime_SplitsWaitAndExecutionTokensWithLiveGuardedCalls`, `Resolver_UsesDirectStoreAndExistingRecoveryHelpers`, `Owner_RetainsPreparationSendAndAbortAndJoinsBeforeHostDisposal`, `Preservation_RestoresAllEightWholeOriginalsAcrossNewlineRepresentations`, `Preservation_RejectsMissingDuplicateAndUnrelatedSourceChanges`, `Preservation_NewestPreMapPreservesInheritedChains`.
- `CodeAlta.Tests.CodeAltaHostLifetimeTests.HostOwner_SourceWiring_UsesSingleNamedDisposalFactory`.
- `CodeAlta.Desktop.Tests.DesktopArchitectureTests`: `DesktopAssembly_HasNoTerminalOrHostCompositionReferences`, `DesktopAndTui_HaveSeparatePackageIdentityAndRidIntent`, `DesktopDefaultItems_ExcludeOldOutputsAndFrontendBuildArtifacts`, `NativeLifecycle_KeepsDispatcherAliveAndResetsExitCodeOnDisposalFailure`.

The two real Desktop fixtures seed/read back production journals and cache rows under unique
explicit roots, register only a literal fake provider, disable plugins/probes and supply empty
plugin environment/fixture-local Git discovery. Actual tasks/receipts precede assertions;
cleanup releases gates, starts independent five-second observations and preserves failures.
Final snapshots follow settled producers; failed/unconfirmed roots are never deleted. The old
failed owned-command fixture root remains untouched. Other selected cases use literal callbacks,
DTOs, lexical paths, named source reads or already-loaded assembly metadata, not native startup.

Cached `tsx` ran only `sessionOperations.test.ts` with an anchored exact-name pattern: `uncertain
retry preserves epoch key session and exact text`; `stale selection and unmount suppress
callbacks`; `epoch mismatch never resends`; `receipt recovery exposes no prompt text`; `refresh
is explicit and never submits`. Controlled gates release in `finally`; retained work and fault
observations receive independent five-second cleanup joins. Cached `tsc --noEmit` passed.

Intermediate compilation failures were fixed without suppressions: three explicit retained-task
discards; initially unfinished inverse linkage; wrong generated export alias; duplicate
source-linked/imported scope type; redundant constant-only assertion. Desktop now tests the
actual Orchestration scope via test friendship. Final Release solution build passed zero
warnings/errors with `--no-restore -p:NeoAstraBuildFrontend=false
-p:NeoAstraRestoreFrontendDependencies=false`. No dependency acquisition or frontend build ran.

Parent matched16 whole originals to raw Git `b069dbdb`;42 context-bearing occurrence-checked
inverse edits and48 LF/CRLF/mixed reconstructions pass, with missing/duplicate/drift/unknown/
already-restored rejection and complete History→Workspace/owner/discovery chains. The new
source fixture has30 direct/19 inherited content paths; the inverse reads no files. Original
15 hashes/40 edits and all historical frozen payloads remain unchanged. App47,026<47,064;
guide, attributes, main guard and strict decoder remain unchanged, as does unrelated work.

No configured-provider/auth/network/default-profile run, React mounting, native close/package/
platform qualification, plugin CLR loading, broad suite or website build was admitted. Native
lease retention is source-reviewed, not termination proof; five seconds remains diagnostic.
Website remote-theme acquisition, M2 races/file-search and broader M3–M7 parity remain open.
The same sole child continues read-only on the next M4 projection prerequisite; no M8 is defined.

## Runtime forwarding ownership prerequisite — 2026-09-08

After `67bb503e`, the same source-only writer implemented three-original/six-new-file
forwarding ownership. The parent audited actual Runtime/helper/fixture/inverse code
and executed only exact method filters. All nine
`CodeAlta.Orchestration.Tests.OwnedProviderEventForwardingTests` method identities
listed in the plan's forwarding admission entry passed; all ten
`CodeAlta.Orchestration.Tests.SessionRuntimeForwardingLifetimeTests` admitted
identities passed, with the pending-prompt replacement method admitted/run first.
The latter execute real Host/Runtime/Hub and cached SQLite journal I/O only under
fresh explicit fixture roots with one registered fake provider. No plugins, probes,
configured provider, credentials, network, default home or frontend/native app ran.
All fixture roots remain; timeout is diagnostic, never permission to release data.

Four exact `CodeAlta.Tests.RuntimeEventForwardingSourceTests` methods passed:
`Runtime_OwnsBodiesTailsAndCapturedAttachmentUses`,
`Fixtures_KeepControlledOwnershipAndClosedRoots`,
`Preservation_RestoresWholeOriginalsAndRejectsDrift`, and
`Preservation_ClosesReaderMapsAndInheritedGateways`. Ten direct named source inputs,
zero transitive content reads and a zero-read inverse reconstruct three frozen
originals: 95 ordered context tuples / 96 occurrences / nine newline variants.
Missing/duplicate/drift/unknown/already-restored negatives pass. Separately run exact
`CodeAlta.Desktop.Tests.DesktopOwnedSessionSourceTests.Boundaries_RestoreWholeOriginalsAndHistoricalReaders`
passed, retaining its 16/42/48 preservation and 30-direct/19-inherited reader maps.

Intermediate failures were fixture-only: CS8601 on nullable ProviderKey seeding
(parent first misidentified WorkingDirectory), then one of nine helper methods
failed because separate canceled-task awaits did not preserve exception identity.
Explicit fixture input validation and exact-task/type expected-cancellation tracking
fixed these; no production policy or historical assertion was weakened. Targeted
main, runtime-test and Desktop Release builds passed without warnings/errors,
using no restore and disabled frontend targets.
The final full Release solution build likewise passed with zero warnings/errors;
all four new source methods passed again after fixture and documentation corrections.

Full callback tails remain runtime-owned while captured attachment uses determine
retirement. Setup/retirement transitions are joined outside actors; abort starts
before work joins, and late handles/subscription receipts remain owned. Unconfirmed
unsubscribe skips provider Stop and faults runtime shutdown before actor/event
teardown. The unchanged outer Host nevertheless continues Hub/registry disposal
after settled runtime failure: runtime reference retention is not an outer lifetime
guarantee. See [forwarding boundaries](runtime-provider-event-forwarding.md).
Unbounded retained work/failures/actors, upstream callback-drain limitations and the
lossy competing-reader runtime stream remain explicit. This is not revisioned
projection, M4/live parity, native qualification or completed M3–M7. Website theme
acquisition and prior M2/file-search deferrals remain open; M8 remains undefined.

### Restricted owned Desktop ask round trip — 2026-09-12

Implemented the caller-session-only `alta ask --stdin` producer, private shared ask owner,
finite list/answer/cancel/observe RPCs and App-owned pending-ask presentation. It is enabled only
by explicit owned Desktop composition; ordinary hosts default to no owned asks. There is no
general LiveTool dispatcher, cross-session target, file/native effect, provider-input activation
or restart authority. Answer makes a new normal owned submission with original AskId; cancellation
only removes an unclaimed original head. Committed asks can outlive their producers. Actual
successful run-capture return, not an early receipt, is positive admission evidence. See
[runtime contracts](runtime.md#restricted-owned-desktop-asks) for lifetime and wire bounds.

The shared implementation moved to Orchestration, retaining `CodeAlta.LiveTool` public names and
20 LiveTool forwarders. Frozen-source restoration and dependent rebuilds pass; this is **not
old-binary forwarding qualification**. Original historical assertions/inverses remain intact.

Parent independently audited the complete production/fixture routes and the final five-path
wire/nullability correction before cached qualification. Targeted Release builds of the three
test projects and Desktop generation succeeded without restore/frontend acquisition. Generated
handles use canonical decimal strings, not raw `long` or frontend numeric conversion. **20
managed in-memory/source cases** passed (including the earlier three Agent cases, not additive),
**13 frontend ask cases** and cached TypeScript `--noEmit` passed. The earlier 12 frontend passes
are superseded. Framed generated payload maxima are **74,720 bytes page /59,662 bytes action**,
within 192/208 KiB respectively; no heap/latency/rendering guarantee follows.

The separately admitted exact inert-provider test
`OwnedSessionAskRuntimeTests.RealOwnedRoute_PropagatesAskIdRejectsPlainReplayAndClosesRetainedProducer`
also succeeded after parent transitive setup/cleanup audit. It uses actual Host/Commands/Runtime,
isolated retained roots, bounded discovery, fixture-local Git settings and shipped prompts, without
plugin startup. Original work, late setup and cleanup remain owned; gates release and independent
cancellation starts before dependent joins. Roots are retained even on success. Deadlines remain
permanent failure/uncertainty, never shutdown proof. This result does not qualify native UI,
configured providers or default profiles.

Intermediate CS8604/NEORPC005 build failures were corrected and rebuilt successfully; cascading
missing generated symbols were not separate defects. The two historical hazardous asynchronous
`AltaAskResponseTests` cases remain unchanged and unexecuted. Full solution/suite, mounted/native
UI, real providers/tools/auth and website are not newly qualified; website theme acquisition stays
outside the no-network/install boundary. Old-binary compatibility, deferred queue interleavings,
durable trust recovery and remaining M4–M6 work are explicitly open.

### Owned Desktop current-notes read — 2026-09-13

Implemented a 19-path source/test vertical: notes-specific contained store reader sharing the original
parser/lock, runtime known-session resolution, existing eight-read workspace admission/drain, owned-only
unary RPC, App-owned manual refresh coordinator and literal-text panel. No notes mutation, rich Markdown,
provider-input activation, tool expansion, permissions, attachments/native operations or event/queue
arbitration change. Empty/no-event/Clear collapse and journal order remain unchanged. Lexical containment
does not cover prior cache metadata/existence probes, reparse points or external races. See
[notes runtime boundaries](runtime.md#owned-desktop-current-notes-read).

Parent independently audited all production changes and complete fixture bodies/helpers/cleanup.
Two source-only fixture corrections retain primary failures during frontend cleanup and require actual
epoch-marker/click-handler presence in the source guard. All three cached targeted Release builds passed
zero warnings/errors, with no restore/frontend acquisition. Generated TS/schema/manifest have only the
new unary notes service and string/nullable-string envelope; cached TypeScript passed.

**24 focused managed cases passed:** five workspace ownership, seven notes RPC, two new source checks,
the updated composition and unchanged history-reader checks, four preserved ask source checks, and four
separately admitted real-reader/runtime tests. The 18 new methods are included, not additive. All **six
frontend notes cases** passed. No failures, skips or timeouts occurred. Ten frozen originals restore
against `7c3a1279` with LF/CRLF inputs; ask historical hashes remain intact. Generated full escaping
measured **99,935 payload bytes +4,096 framing =104,031 <131,072**. Neither response bounds nor these
fixtures qualify heap use, scan cost, latency or mounted rendering.

Real-reader tests use tiny task-owned journals and no-session runtime/Hub construction with forbidden
provider/discovery factories, not actual Host or configured-provider startup. Explicit-root cache SQLite
connections disable pooling and dispose through retained operations. Roots remain retained even on
success under `%TEMP%/CodeAlta-owned-notes-`: `8325b4c576284c02b1f1402f51f3f910`,
`46f4682092db43579178910fb602feef`, `162d11354ad44c81925f1638043bf1b0`, and
`b5718f41bbde4ce18f2dea0d24a67a01`. A deadline would permanently fail and retain ownership evidence,
not prove termination. Historical hazardous `SessionNotesServiceTests` remains unchanged/unexecuted.

No full solution/suite, Vite/native/mounted UI, real provider/auth, default profile or website build is
newly qualified. Website theme acquisition remains outside the no-install/network boundary. Old-binary
ask forwarding, deferred queue interleavings, durable trust recovery and M4–M6 remain open. This supplies
the current-notes read adapter, not notes editing or complete milestone parity.

### M4 owned nonsecret provider-input checkpoint (2026-09-12)

The owned Desktop branch adds independent default-false `--enable-owned-user-input`, with explicit
Host forwarding and per-send activation/callbacks for ordinary sends, restricted-ask responses and
claimed queue items. `request_user_input` requires both the flag and a selected callback; profile
overrides can disable it but cannot activate it alone. Session-default denial/cancellation, command
review authority, trusted TUI AutoApprove/immediate input, nullable request RunId, and raw-event/Display
boundaries remain unchanged. This is a limited input adapter, not M4–M6 completion.

The existing permission mailbox owns immutable nonsecret forms and exact operation/runtime/attachment/
session/request-run/interaction/attempt handles. Actual successful Started binding supplies run lifetime,
not a manufactured request RunId. Input and permission deliveries share capacity. Whole unsupported or
oversized forms cancel; duplicate/missing/extra answers are refused, freeform answers remain literal,
and accepted means owner decision only. Answers may enter provider tool-result/history storage.
Unary list/resolve/cancel contracts use canonical string generations and bounded sanitized responses.

Frontend originals are App-owned across remounts. Action admission and acknowledgment invalidate old
pages and outstanding reads; epoch evidence is processed before obsolete-presentation fences. A terminal
result requires explicit local observation/acknowledgment followed by a fresh list. Uncertainty is never
replayed, renderer reload recovers only host-pending forms, and list absence/restart does not recover a
lost decision. No browser ledger, polling or native credential-entry workflow is added.

Parent audited sole-writer corrections and independently passed **27 managed methods**: five Agent,
ten root-free mailbox, three runtime/lifecycle/cleanup, and nine Desktop RPC/source methods. The lifecycle
method covers 24 combinations. **Seven frontend tests** pass, including 12 retained-page and 32 racing-list
scenarios; TypeScript checking and all three cached targeted Release builds pass with zero warnings/errors.
All eighteen newest source originals and inherited notes/ask routes reconstruct without hash rebasing.
Measured generated full-nested escaping plus 4,096-byte framing is **224,920 / 262,144 bytes** for a page
and **62,245 / 98,304 bytes** for resolve; not parser-allocation/heap/latency guarantees.

Corrections included actual factory registration/Host forwarding, C#/TypeScript errors, literal historical
assertions and exact removed-comment restoration, retained-page authority, and fixture defects. Empty
`SkillCatalog([])` selects defaults: runtime fixtures instead supply a nonempty no-root provider. A borrowed
metadata catalog uses the explicit locator; the ask seed uses the isolated global directory so real recovery
and the actual restricted-ask answer transaction execute. Cleanup accounting requires successful original
stop completion and preserves cancellation-shaped cleanup failures instead of filtering them out.

Actual Agent qualification uses a fixture-owned rejecting HTTP client, scripted executor, cached models,
precomposed instructions and explicit store paths; the session does not dispose the borrowed client.
Both actual-Agent methods and all three inert-provider runtime routes pass. The five selected SQLite
managed/native output assets match cached packages; runtime admission includes isolated SQLite/journal
work, not configured providers or a native shutdown guarantee. Retain all roots, including successes:

- `%TEMP%/CodeAlta-owned-input-agent-10ed9d2e0cee4b31bd72031adf98a779`
- `%TEMP%/CodeAlta-owned-input-agent-4d6d2e43c31340628784a2d1019b52be`
- `%TEMP%/CodeAlta-owned-input-runtime-595224301c6a45f29a3f62c28e8fd6df`
- `%TEMP%/CodeAlta-owned-input-runtime-707209d4106e4d8ca09c17e25d234869`
- `%TEMP%/CodeAlta-owned-input-runtime-cfada128e14a43f598c19e258e3211ca`

**Remaining gaps:** the external cancellation-traversal test does not suspend/prove the service's real
registration `DisposeAsync`; no timing seam was added. Production fallback-client ownership, existing
schema-document retention, arbitrary constructor failure rollback, queue failure-path dependency retention
and deferred queue interleavings remain open. No old-binary forwarding, durable restart authority, broad
effect/recovery stress, mounted/native UI, configured-provider/auth/network, full suite/solution or website
qualification is claimed. Website theme acquisition remains outside this session's no-acquisition boundary.

### M4 bounded frontend observation ownership checkpoint

Parent accepted and independently audited a ten-path correction after provider-input integration
`d8cd1f44`. Display now retains one original observation plus one latest desired selection; scoped
detach cannot cancel a successor, and opening a successor requires successful terminal iterator
return. Failed/unavailable/incomplete cleanup retains and blocks the old owner, without treating
cancellation or renderer reload as proof of host cleanup. Current-state reads retain one original
frontend invocation plus one latest explicitly pending refresh; displaced requests settle explicitly.
Neither the waiter bound nor its drain completion establishes a bound on outstanding backend work.

Both readers validate correlated identity before obsolete-presentation fencing and revoke the captured
shared mutation capability on valid late replacement evidence. Capability subscribers are isolated and
observe committed monotonic denial. Existing action guards/intent/uncertainty protocols remain unchanged.
Display revisions are exact canonical nonnegative Int64 strings. History, raw-event readers, runtime
effects, main composition, input/ask/notes protocols and generated contracts were not changed.

Parent found an omitted current-state worker join in the initial handoff. A two-file correction now
retains the drain before scheduled invocation and before loading notifications, exposes `settled()`,
and joins explicit successors independently of refresh outcomes. Tests cover notification reentrancy
and actual synchronous invoke failure without converting it into an asynchronous failure fixture.
Fixture cleanup records original task/stage/outcome identities, includes late acquisitions, releases
gates and starts cancellation before joining, and preserves primary plus cancellation-shaped cleanup
failures. Intentional failed Display return asserts the exact retained blocked owner and does not
unsubscribe/release dependents; its diagnostic output is expected, not a failing test or proof of
successful cleanup. Deadlines remain permanent failures.

Independent parent verification passed **45 selected frontend tests**: 23 Display, 14 current-state,
one new capability-latch regression, and seven unchanged input regressions. TypeScript checking passed
before and after the cached targeted Desktop test build. That Release build passed with zero warnings
or errors and reported the generated contract current. **Eleven managed tests** passed: the two updated
current-source wiring guards and nine previously admitted input RPC/source regressions, including all
eighteen frozen input originals. No source hashes were rebased or inverse machinery added. The preexisting
runtime-state wiring assertion drift was corrected only at its frontend call site; backend/registration
and generated-contract guards remain intact.

These are helper/state-machine and source-wiring checks, **not mounted React/native qualification**.
Separate missing/unavailable/incomplete iterator-return and late-epoch-after-supersession cases are not
all independently exercised by this selection; rejection, detach, held cleanup and scoped supersession
are covered. No full suite/solution, Vite bundle, website/theme acquisition, dependencies, configured
provider/auth/network or default-profile execution was admitted. Shared history/live reconciliation
and original-effect ownership remain open: TUI still combines history/plugin projection and effects,
and asynchronous history rebuild has no shared live watermark. Partial Display and separate current
state/history reads must not be presented as recovery. Prior input disposal/client/queue limitations,
durable outcomes and M4–M6 remain open.

### M4 runtime-owned file-search cache checkpoint

The cache-only effect now runs in existing runtime-owned provider forwarding and CodeAlta-authored
append operations. Host supplies one concrete cache to runtime and search. Exact projected event
references and the source directory are captured independently of UI lookup; append captures its
effect directory before asynchronous admission. Invalidation is outside actor/publisher locks and
precedes forwarding's later notification/queue tails, without changing projection-use release or
host-close order. All file-change phases and diff updates mark the cache dirty, including when the
original stream is full. No worker, event reader, callback interface or plugin invocation was added.

All three TUI invalidation calls are removed, intentionally removing cache effects from history
rebuild as well as live rendering. Plugin observation still runs during history replay; its context,
failure handling and lifetime are not migrated. The unused, validated search-service constructor
parameter remains for composition compatibility. Cache invalidation remains best effort and is not
a filesystem scan, write-success acknowledgment, durable effect ledger or history/live watermark.

Parent verification so far: nine exact inert-runtime methods, seven new source methods, the
frontend fire-and-forget guard, and the actual cached-history rebuild regression passed. The
Orchestration, main and Desktop test projects build with cached assets and zero warnings/errors;
the Desktop generated RPC contract remains current. Five
selected SQLite managed/native artifacts matched their cached package inputs before runtime
admission. Runtime/history fixture roots remain even on success. The main build initially found
a missing required permission handler in the new history fixture; its explicit deny handler and
inverse literal were corrected. One positive source-representation case incorrectly supplied a BOM;
it now tests BOM-free LF/CRLF/mixed input and separately verifies the unchanged decoder rejects BOMs.
Twelve complete original source anchors and all eighteen input anchors passed restoration checks.

Selected Reminder and Models historical preservation methods also passed. The selected Plugin UI
frozen-boundary method failed earlier on the HostOptions restoration route, before reaching the
new architecture/reminder mappings. Independent source review identifies an inherited gap: older
restoration expects receipt-capacity and plugin-environment declarations to be adjacent, but the
permission-review block remains between them. The cache delta is a no-op for that Options route;
neither its production source nor historical assertions were changed to hide the failure. This
does not establish that every subsequent legacy assertion passes.
The existing gateway regression separately exercises the exact outer PluginUI MCP→profile chain
on the original decoded architecture/reminder inputs. Parent audit, cached main rebuild and its
exact-method rerun passed against the unchanged canonical UTF-8 SHA-256 values, retaining inner
full-parent-blob checks and second-restoration rejection. This qualifies those two fallback inputs
only; the inherited full-method failure remains recorded and unrepaired.
The renamed legacy live-handler regression was not executed because it starts an existing
unretained deferred plugin-projection task. The cached-history regression suppresses that task and
joins its actual rebuild. These checks do not qualify mounted/native UI, real providers/plugins,
the full suite, shared plugin lifetime/effects or history/live consistency. M4–M6 remain open.

### M4 plugin-effect migration: parent lifecycle audit

The cache prerequisite is integrated as `861a2f97`. It does not authorize moving arbitrary
plugin callbacks into runtime forwarding. Independent parent source review found these remaining
contract gaps; no plugin production behavior was changed or executed by this audit:

| Current boundary | Evidence and consequence |
| --- | --- |
| Agent-event dispatch | `PluginContributionAdapters.cs:626–654` enters callbacks directly, without an activation lease. Ordinary callback exceptions become diagnostics; cancellation escapes to callers. Context invalidation occurs only on success. Another `Task.Run` would not provide finite admission or a lifetime lease. |
| Activation teardown | `PluginRuntimeLifecycle.cs:81–119` cancels tracked work, then clears the context/instance in `finally`, including failure/timeout paths. Event callbacks are not part of that tracked-work join. An active-plugin snapshot therefore does not retain a usable activation through callback completion. |
| Manager teardown | `PluginRuntimeManager.cs:306–320` clears active handles before awaiting deactivation. Retention and repeated/reentrant deactivation need an explicit contract, not an assumption that removal means termination. |
| Existing task tracker | `PluginTaskTracking.cs:51–77,83–111` launches before registration and has no finite admission/closed gate; cancellation of an idle waiter does not terminate its originals. It cannot simply be reused as the missing event owner. |
| Context and service borrowing | `PluginHostBridge.cs:305–323,535–553` uses event RunId/provider identity, TUI project-path resolution and managed-provider classification. `SessionPluginEventObserver` instead supplies headless context. Host cleanup orders runtime/hub/providers before plugins (`CodeAltaHost.cs:60–67`); an event-specific plugin lease alone does not prove all borrowed service lifetimes. |

The next contract must specify finite overload handling, reentrant callback-initiated deactivation,
retained original outcomes, and dependency release only after actual callback termination. Silent
coalescing/dropping, unbounded pending waiters and timeout-authorized disposal are not equivalent
to that contract. Any required shutdown/deactivation policy change needs explicit scope approval
against the standing close-order constraint. The candidate first correction is at the existing
activation/adapter/manager boundary; plugin history replay, other callback classes and shared
history/live recovery must not be silently included or claimed fixed.

The sole child's independent audit completed on retry and agrees with these gaps. Parent also
confirmed that TUI disposes frontend resources before owned services (`ShellFrontendHost.cs:97–124`)
and that `PluginAltaServiceBridge.InvokeAsync` retains/calls its dispatcher with plugin identity
(`PluginAltaServiceBridge.cs:23–82`). A production lease therefore needs dependency-release barriers,
not merely a retained activation object. Rejecting callback-originated self-dependent shutdown also
requires an exact close-capable command audit and propagated attempt identity; that coverage is open.

The proposed policy is **64 outstanding event attempts per activation, no queued waiters**, explicit
Capacity/Closing rejection, bounded rejection reporting, and bounded retained retiring generations.
It is **not approved or implemented**: it adds a delivery-loss boundary. Retaining live dependencies
after a shutdown timeout likewise changes production semantics even without changing its duration.
Both decisions must be resolved before the proposed activation/adapter/manager prerequisite can be
wired; lossless observation would require a different producer/backpressure or durable-delivery
contract. No new runtime/plugin tests were executed for this read-only audit.
