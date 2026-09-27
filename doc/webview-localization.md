# WebView shell language

Settings → Appearance offers Auto, English, Español, Français, Deutsch, 日本語,
and 简体中文. This is a WebView-local preference, not host/global configuration.
The selected shell text updates immediately; restarting does not translate the
remaining English surfaces. Auto examines at most eight browser language tags,
uses the first supported base language (Chinese maps to `zh-CN`), and otherwise
falls back to English, including server-side rendering.

`localization.ts` contains frozen typed plain-text dictionaries and preference
validation. `shellLanguage.tsx` owns the App context and persists explicit choices
under `codealta.desktop.language.v1`. Invalid or inaccessible storage falls back
to English with a notice; it is not automatically overwritten. Failed writes
still apply to this window and display a translated notice. The HTML `lang`
attribute reflects the resolved shell language. Diagnostic identities remain
stable and untranslated. No dependencies, network translation or HTML injection
are involved.

This bounded slice covers primary shell navigation, Settings navigation/title/
close names, General appearance and navigator preferences, recent-count labels,
storage notices and shortcut Help descriptions. It also covers regular owned,
catalog-only and local-draft composer controls, expanded-editor controls, static
image preview/count/removal/help text, the implemented action palette, and session
tab draft/fallback/close/reopen labels. Palette search matches both translated
labels and canonical English descriptions; dispatch always uses the original
action ID. User-provided titles resembling English defaults are still literal.
Saved-session browsing, Session Info and the usage inspector also translate their
static labels, counts, scope/sort controls, explanations, refresh/Copy controls and
accessible names. Runtime badges translate static unknown/stale presentation and
the explicit refresh button, not controller-produced observations or summaries.
The existing Skills raw-candidate inspection, create-only agent-prompt form and
sequential batch-deletion panel also translate static headings, controls, help,
field/metadata labels, review/discard/confirmation explanations and accessible
names. Closed batch outcome discriminants and known earlier prompt phases have
translated presentation; raw controller messages and backend codes remain literal.
Reminder and caller-ask panels translate static labels, help, actions, accessible
names, retained-original controls, finite local validation/read notices and explicit
known UI states. Caller asks are not provider user-input or permission approvals.
Reminder payloads/previews, schedules, invariant duration input, caller questions,
choices/free text, IDs and original JSON remain literal, as do controller messages.
Shortcut keys and action IDs
remain unchanged. Counts use the selected surface's singular/plural messages.
Workspace and operation-owner trees are not keyed by locale. Language changes
do not request provider/configuration/catalog data, submit work or unlock writes.

Provider-input/permission and timeline/history/live static presentation now have
six-language implementation and a verified bounded scroll correction, independently
accepted after the parent's exact 205-test run. See the evidence below; unchanged
owner identity alone is not proof of scroll retention.

The Providers, Models, Agent prompts and MCP Settings inventory bodies also translate
static headings, fields, search/selection controls, scope/omission help, accessibility
and finite local notices. This inventory batch awaits independent acceptance.
Configuration names/values, IDs, descriptions, prompt source, availability/status/source
codes remain literal, including English-like values. Configured state is not readiness,
connection or authorization; existing explicit probe/next-Send controls retain their owners.

Not translated here: remaining management page bodies, other session dialogs
and operation feedback (including the outer batch-status notice),
reference-picker/retained-request/advanced diagnostics, controller-produced
observation/usage outcomes, scope warnings and runtime summaries/details,
raw retry-request diagnostic tooltips and other
unmigrated tooltips/accessibility labels. User text, file/tool content, paths,
identities and raw backend diagnostic codes are never translated. Exact timestamp
sources remain literal; the bounded time presentation described below localizes labels only.
Typed `DELETE N` is always the exact English protocol token, not translated input.
Prompt mode/scope IDs, authored text, skill metadata and retained request JSON are
literal. Locale switching neither trims these values nor invalidates review,
clears a confirmation/discard choice, dispatches work, or unlocks uncertainty.
Translations are agent-authored; native-speaker review and native WebView visual/
keyboard qualification remain outstanding. This is not full translation parity.

Regression evidence is recorded under `tmp/localization-20260927/`. Tests cover
dictionary/parameter parity, plain React escaping, bounded fallback, denied and
malformed storage, actual App live switching, long/narrow labels, modal/focus/IME
and original image Send/publication retention. The follow-up composer correction
fixes automatic flex sizing and consistent resize layout. Literal editing tests
retain their explicit larger viewport and assert caret/selection preservation.
The subsequent [short-window layout](responsive-composer.md) adds real 750x485
and 390x500 editing regressions and scrollable content, addressing the earlier
clipping limitation. See the respective reports for retained red evidence and
independent acceptance status; native qualification remains outstanding.

Interaction-control evidence is under `tmp/interaction-localization-20260927/`.
The bounded run passes 89 tests (zero skips), including complete composer and
Settings browser files, palette/tab/localization units and image/operation owners;
strict TypeScript and production build pass. The actual App switches all six
locales with a pending image Send, preserves mounted identities and original
requests without bridge calls, and dispatches translated/canonical palette search
to the same focus action. German/Japanese/English editing at 390x500 in both
themes covers empty `/` palette and `?` Help, literal `/?` and Japanese text,
synthetic IME guards, expanded focus/Escape and dialog bounds. Existing 750x485
and pending/uncertain publication coverage remains included. Localized close/reopen
tab navigation also retains local draft text without Send or Create. These are finite
fake-bridge browser observations, not native IME or universal layout certification.

Browsing/inspection evidence is under `tmp/inspection-localization-20260927/`.
The final bounded run passes 130 tests, zero skips, plus strict TypeScript and
production build. Actual App browser search/selection in all six locales adds no
attachment/provider work and retains the pending image Send. A disposable context
driver around the real dialogs covers six-locale changes during pending Copy,
runtime observation and usage reads, unknown/error/stale presentation, revoked
authority, literal English-like titles/IDs, focus/Escape and 390x500 light/dark
bounds. Existing actual-App ownership, uncertainty and ABA tests remain included.
Explicit line heights on runtime badges/refresh buttons prevent a reproduced CJK
fallback-font metric shift from moving the retained timeline; this is separate
from the previously documented, still-open 15px scroll observation.

Session Info Copy remains canonical: English labels plus literal recorded/observed
data, exact ID Copy, unchanged 32,768-code-unit refusal limit and generation fences.
Only the button/help/feedback translate. The canonical serializer is independent
of translated DOM text and browser tests compare it with the previous English
Copy payload. Language changes never refresh observations or restore permission.

Management evidence is under `tmp/management-localization-20260927/`: 137 bounded
tests pass with zero skips, plus strict TypeScript and production build. Real
panels and owners in a disposable fake-transport/context harness switch all six
locales through Skills pending/success/uncertainty, dirty prompt editing,
discard/review/pending/created/earlier-original/uncertainty, and sequential deletion
review/pending/partial-success/uncertainty/explicit successor stop. Tests compare
original object identities, state snapshots, literal English-like metadata and
titles, exact `DELETE N` input and unchanged RPC counts. German/Japanese controls
fit 390×500 light/dark horizontal bounds with vertical scrolling available; native
Tab and synthetic composition retention are checked. Existing actual-App modal,
Escape/focus, composer and shared-capability regressions remain in the bounded
run. This harness is not native WebView/IME or full translation qualification.

Reminder/caller-ask evidence is under `tmp/reminder-ask-localization-20260927/`:
31 focused and 163 bounded tests pass, zero skips, with strict TypeScript and
production build. Real components and owners with disposable fake transports
switch all six locales during pending reads/create/Save, retained uncertain actions,
dirty drafts, confirmation and scope ABA. Original requests, captured choices/free
text, mounted controls and RPC counts are checked; language does not refresh,
retry, clear validation or restore revoked capability. Known pending/admitted/
completed/not-ready/uncertain presentation does not change action semantics.
Navigation shortcuts and selected-reminder styling use stable classes rather than
translated accessible names. German/Japanese short/narrow light/dark browser checks,
native Tab and synthetic IME guards supplement the complete existing keyboard and
operation regressions. Actual-App/settings/composer and original image/prompt
uncertainty regressions are included. Reminder navigation intentionally unmounts
the workspace; this does not claim workspace retention across that navigation.
Settings modal ownership remains separate. Parent independently accepted this
Reminder/caller-ask slice with 67/67 tests, strict TypeScript and production build
(`parent-{frontend,build}.log`). Native-speaker/native WebView review remains outstanding; see the report for exact
commands, retained red evidence and remaining English surfaces.

Provider/timeline evidence is under `tmp/provider-timeline-localization-20260927/`.
Static provider headings/actions/recovery instructions, finite notices, history
paging/follow/navigation labels, timeline disclosure/source Copy feedback and live
presentation use the same frozen dictionaries/context. Provider questions, choices,
commands, reasons, codes, messages, Markdown/code, identities and clipboard payloads
remain literal. Existing allow-once/deny/cancel tokens and permission authority are
unchanged; caller asks remain separate. Locale is not an operation/effect identity.
No history algorithm, parsing or renderer dependency changes were made. The correction
below explicitly separates outer versus inner native scroll anchoring.

The initial bounded run was **202/204 passing, zero skips**, with history and App
scroll failures independently reproduced by the parent. Real-owner fake-transport provider tests pass six-language
pending/rejected/uncertain/stale-scope checks, original payload/no-extra-RPC checks,
native Tab, synthetic composition and German/Japanese 390x500 light/dark bounds.
Literal/Copy and operation-owner regressions pass. History's viewport-relative anchor
and follow remained intact in initial diagnostics, but its outer scroller moved and did not
return to its original position after restoring English. Actual-App Japanese switching
also moved timeline scrollTop from 158 to 183; focus, mounted identities, draft,
original image Send and RPC count remained intact. These failures blocked later
browser assertions and are distinct from historical scroll observations.

The correction stabilizes timeline chrome line boxes (`line-height: 1.5`), gives
history title/action independent rows and reserves a two-line minimum read-notice
slot. Longer text still wraps and grows; Markdown/code retain their existing line
heights. The outer short-window access scroller disables native anchoring so it
does not compensate for reflow already anchored inside the independent timeline.
Inner native anchoring, user scrolling, paging/follow algorithms and owner effects
remain unchanged. The history fixture uses that production outer-scroller class;
no original assertions were weakened, and per-locale outer-scroll checks were added.

Both complete browser files now pass, including in-flight paging and later App
image/prompt uncertainty. Bounded verification passes **205/205, zero skips**, plus
strict TypeScript and production build; an uninstrumented two-file repeat passes
2/2. Containment-only and incomplete line-height experiments were not retained.
All red diagnostics remain available in `REPORT.md` and `CORRECTION.md` under the
evidence directory. Parent independently accepted the correction with 205/205 tests,
strict TypeScript/build and whitespace/index checks (`parent-correction-{frontend,build}.log`).
Full/native translation stays open. This does not establish invariant pixel positions for arbitrary window/font
sizes or fix the historical 15px discrepancy or separate process-exit flake.

Settings inventory evidence is under `tmp/inventory-localization-20260927/REPORT.md`.
Real production panels with fake transports cycle six locales through pending/failed/stale
reads, provider probes, filtered selections and next-Send validation without extra calls
or owner/control replacement. Tests preserve English-like literal values and 2,048-character
prompt source, reject oversized prompt/MCP responses and wrong MCP project scope, and retain
raw codes. German/Japanese 390x500 light/dark checks use production CSS and scroll-reachable
controls. Actual-App native-modal tests retain the original image Send, uncertain publication
and revoked capability; existing exact history/App scroll assertions remain unchanged.
The bounded 227-test set passes with zero skips, strict TypeScript and production build.
The report retains the initial missing-stylesheet fixture failure and a composer text-insertion
failure that passed unchanged on repeat; no composer flake fix is claimed. No production
CSS, owner, layout, disclosure or backend changes belong to this inventory batch. Independent
inventory acceptance is now recorded in `parent-{frontend,build}.log` (47 focused checks,
zero skips, strict TypeScript/build). Full/native/native-speaker qualification remains open.

### Static project/session workflow presentation (independent review pending)

The [new-session provider choice](new-session-provider.md) also translates its static
label, default semantics, enabled-not-ready help and uncertainty lock in all six
locales. Provider IDs remain canonical literal values. Changing locale does not
read inventory, probe a provider, change the original request or release uncertainty.

The six frozen dictionaries now cover project rail labels, project details, opening
saved projects and importing existing folders, archive/unarchive review, session action
menus and existing inline session creation/project and session rename/delete controls.
Local finite notices use `WorkflowNotice`; untagged controller/backend feedback stays
literal. Titles, IDs, paths, recent folders, provider/model/prompt selections, draft
values, confirmation values, revisions, epochs and original request JSON are not translated.
Locale is not an effect dependency, operation identity or authorization signal.

There is no standalone project-creation form: importing an existing folder is the
available UI. This change adds no lifecycle operation, durable reconciliation or
provider-free creation guarantee. Archive metadata remains source-preserving; creating
a session remains effectful admission rather than proof of run completion.

Evidence: `tmp/workflow-localization-20260927/REPORT.md`. All-six fake-transport tests
retain drafts, confirmation, controls/focus, exact captured requests and pending/uncertain
owners without additional locale RPCs. Existing scope/host replacement, cancellation,
shared-capability, actual-App image/prompt uncertainty and exact history/scroll assertions
remain in the bounded run. German/Japanese 390x500 light/dark dialog checks cover fit,
scroll-reachable controls, keyboard focus and synthetic composing Escape (not native IME
qualification). The complete bounded set passed **251/251, zero skips**, strict TypeScript
and production build. An old direct invocation of the now-context-reading rail toggle
was corrected to run inside a React renderer without dropping its callback assertions.
Composer insertion failed in an earlier broad run and an isolated repeat, then passed
unchanged in the complete green run; retained red evidence is not a claimed flake fix.

Controller/backend diagnostics, lifecycle/status codes and inventory-derived evidence
remain English or literal data. Advanced static presentation is described below;
full UI/native and native-speaker translation qualification remain open.

### Support/reference presentation (scoped independent acceptance)

About, current-process application logs and project reference picker/metadata-preview
chrome use the same six-language context and frozen dictionaries. Recorded host
identity/build values, log rows/severity/logger names, reference paths, raw preview
text and backend status codes remain literal. Clear-log confirmation still requires
the exact `CLEAR CAPTURED LOGS` bytes. Reference resolution remains paths/ranges on
normal Send, not captured file contents; Queue/Steer remain literal.

These surfaces have no Copy controls; About has no links/update/install actions in
the current implementation. Logs are bounded in-memory capture, not logfile browsing
or tailing. Localization adds none of these missing features. Reads and clear requests
remain explicit; query debounce and expected scope/input/focus/ABA guards are unchanged.

The original pending-preview regression remains in `settingsShell.test.ts` at its
original order. Native child-dialog `beforetoggle` invalidation previously changed
only the synchronous generation ref; a later unrelated App render could first publish
that older lifetime to reference consumers and cancel a newer preview. The authorized
correction keeps synchronous invalidation and every existing current/capture check,
then schedules a normal App state update at the native transition. No forced flush,
locale key, read, timer, test warm-up or weakened lifetime guard is used.

The complete App test now reaches the original pending-preview check and later
reference/short-window/image/prompt scenarios. Added Info, project Details and expanded
editor tests cover open/close/reopen ABA, replies completed inside `beforetoggle`,
stale search results, held insertion callbacks and post-transition all-six retention.
The implementation correction run passed **187/187, zero skips**. Parent independently
accepted the scoped presentation/publication correction, but its exact bounded run was
**186/187, zero skips**, not green: the unchanged split-layout harness reported
`root exit unavailable` at line 287 after its assertions. Parent strict TypeScript,
build and tracked whitespace passed; the index was empty. No cleanup qualification or
rerun-to-green is claimed. Evidence and original red logs:
`tmp/support-localization-20260927/CORRECTION.md`, `REPORT.md`, and
`parent-correction-{frontend,build}.log`.

About/log all-six retention and German/Japanese short/narrow checks pass with fake data.
Full/native/native-speaker qualification remains open. The separately parked composer
insertion/growth investigation was not resumed or claimed fixed.

### Advanced session and archived recovery presentation (independent review pending)

The existing six-language dictionaries cover advanced session headings, diagnostic
field labels, manual refresh/retry controls, captured-target explanations, accessibility
help, retained Queue/Steer review and Copy feedback, and archived action/interaction
recovery headings and labels. Read-only composer restrictions and provider-context
explanations are translated; a supplied reason is still caller-owned text.
`ArchivedScopeGates` has no presentation text and its mounting/authority logic is unchanged.

This is presentation only: original Send Abort remains distinct from signalling an
observed run; host queue reservation is not insertion, durability or execution;
steering admission is not completion and observed idleness permits only a compaction
attempt. Busy remains a permanent outcome. Reads confer no authority and archive
metadata does not delete or detach. No new retry, refresh, mutation or recovery route
is added. Exact original manual retries retain the same guards, request and target.

Remaining English is intentional raw evidence, not a claim that the whole panel is
translated: existing admission/reconciliation/status narratives, controller diagnostics,
queue receipt phase descriptions, MCP plugin states, protocol kinds/outcomes/codes,
request/selection JSON, IDs, host epochs, attachment/run references, provider values,
commands, paths and user text remain literal. Only finite presentation labels and
locally owned Copy feedback translate. No arbitrary error-string matching is used to
translate raw evidence. Existing unrelated launch/image diagnostics are not expanded
into a new translation or lifecycle contract by this slice.

`tmp/advanced-localization-20260927/REPORT.md` records exact commands and all red/green
evidence. The complete bounded set passed **191/191, zero skips**, strict TypeScript
and production build. All-six context switches retain real-owner fake requests by
identity and JSON value, read/action counts, draft/caret/selection, controls and literal
evidence. Coverage includes pending original actions, late uncertainty, definite
compaction refusal, revoked capability, archive and replacement session/host scope.
German/Japanese 390×420 light/dark checks cover scroll-accessible controls, focus and
synthetic composing Enter. Existing full App/composer/history files remain in the run.
This is not an exhaustive locale-by-outcome matrix or native IME/screen-reader review.

The split-layout harness was deliberately excluded: the parent's recorded process-exit
failure remains open. The historical composer issue and 15px scroll observation remain
unresolved and parked. Settings retention, Reminders intentional unmount, backend/API
contracts and the responsive layout baseline are unchanged. Full/native/native-speaker
qualification and independent acceptance of this slice remain open.

### Selected-language time presentation (independent review pending)

Session-row relative labels now use the explicit selected `en`, `es`, `fr`, `de`,
`ja` or `zh-CN` language through `Intl.RelativeTimeFormat` (short, numeric auto).
Intl owns plural/unit/past/future forms and localized near-now/yesterday/tomorrow
phrases. The existing elapsed-duration calculation remains: rounded seconds,
near-now below five seconds, floored counts, 60-second minutes, 60-minute hours,
24-hour days, seven-day weeks, 30-day months and 365-day years. These are approximate
duration buckets, not calendar arithmetic or evidence of current activity.

Timeline headings were already absolute local dates, not relative times. They now
use the selected language with `Intl.DateTimeFormat` (medium date/short time),
retaining the existing local timezone and date parsing. Both presentations expose
the exact supplied source string in their tooltip, including its original offset
and fractional precision; valid machine-readable dates are also available in
`datetime`. Missing/invalid source strings remain as supplied without an invented
timestamp. An unusable clock, unavailable Intl API or unsupported selected locale
falls back to the literal source rather than silently using browser-default language.

Saved-session browser metadata, Info recorded dates, observed activity/usage dates
and canonical Info Copy remain literal. No CreatedAt/UpdatedAt/observation is promoted
to authoritative LastActiveAt. Sorting and date parsing authority are unchanged.
Formatters receive the language explicitly, have no cache or language singleton,
and introduce no timers or reads. Session rows retain the existing one-minute App
clock; timeline formatting needs no clock. Locale changes do not change effect keys,
owners, requests, selections or drafts. Time text is single-line/ellipsized within the
existing heading geometry; outer native anchor exclusion is untouched.

Initial qualification was **60/61, zero skips**, with a populated-history App assertion
failing on Spanish at 390×500/light. A bounded two-run correction established that the
existing live-order notice legitimately wraps from 64px to 82px. Timestamp height
stays 16.5px and message headings stay 26px. The existing follower correctly advances
scrollTop 1155→1173 as scrollHeight grows 1183→1201, keeping a zero bottom gap in the
unchanged 28px viewport. A reader established by the real Ctrl+F3 action remains
unfollowed at scrollTop 124 across the same transition.

The fixture now distinguishes these contracts: exact coordinates for readers and
exact bottom retention for followers, both with unchanged outer coordinates/native
anchor exclusion. It preserves the parent's failure diagnostic and all six languages,
light/dark, input/DOM/source/Copy/read and timestamp-layout checks. No production CSS,
content, owner or scrolling algorithm change was warranted by this evidence.
Final bounded verification is **61/61, zero skips**, with strict TypeScript/build and
whitespace passing; an independent parent run also passed all 61 tests and the build,
accepting this bounded presentation/correction scope. Both original failures
and the parent's reproduction remain retained in `tmp/time-localization-20260927/`.
See `CORRECTION.md` there for causal measurements, exact commands and scope limits.
