---
title: Storage management and durable session statistics
status: done
created: 2026-10-10
summary: Add a safe Storage settings page, startup scratchpad cleanup and age-based session retention that preserves historical Statistics. Reorganize Appearance, General and Permissions without changing existing preferences.
---

# Storage management and durable session statistics

## Goal

Show where the space in the user's CodeAlta profile goes, provide narrowly scoped automatic cleanup, and retain the Statistics of removed sessions through resets and future metric versions. Make Settings easier to navigate by taking behavior and confirmation preferences out of Appearance.

Approved by the user on 2026-10-10; implementation is in progress. Automatic deletion remains subject to the safety gates below. No real profile cleanup, configuration changes or source changes were made during research.

On 2026-10-11 the user revised the default in the delegated UI session to **3 calendar months**, requested a larger native inspection window, and improved `AGENTS.md` instructions for driving that window. The final 16-file UI/default patch is integrated in the parent working tree. The user subsequently authorized committing Opus's finished work: `f5e59cf8` on `alta/feisty-flare-zdkc`, without a co-author trailer. Separate parent backend/status integration remains uncommitted.

The later 04:21 UTC visual revision supersedes the donut-below choice: the user now prefers donut and table **side by side** to use the available space and keep the donut larger. Cleanup and distribution cards remain stacked, with cleanup first.

The user subsequently requested completing the feature, committing all scoped work and merging it into `main`. Parent integration is committed as `1fea256a`; Opus's actual commit is merged by `c29e27a1`. Unrelated local MCP/task files remain untouched. Completion work now qualifies the outstanding admission owners instead of bypassing their gates. The shared `ISessionRetentionAdmission` contract and reference-counted session/unscoped leases have three passing regression tests; host/hub/plugin/live-tool/desktop integration follows before readiness is enabled.

The final-history instruction is **one squash feature commit on `main`**, without rewriting existing `main` history and without pushing. Hub admission (`d64cc496`, 30 focused tests) and plugin original-lifetime ownership (`db56ea81`, 30 focused backend tests) are integrated. Source review found hidden aggregate dependency-retention failures requiring additional Hub/plugin/LiveTool qualification. Model preparation now retains original refresh admission independently of canceled waiters, including complete original fault aggregates; new parent regressions await the coordinated test slot. Desktop startup now reuses the original host registry and waits for first complete restoration instead of permanently skipping before the page exists. No unqualified gate has been enabled.

Final integration includes the Hub/runtime/skill/LiveTool/plugin aggregate and strong-root follow-ups plus Desktop's authenticated watch, all-space restoration and deferred prompt admission (`c794b707`). Provider preparation pins prevent premature replacement/removal/disposal, retaining actual dependencies on unconfirmed release. Parent integrated Release qualification passed all .NET suites, TypeScript, all 1,371 serial frontend tests and Lunet (`job-c19530c8`), after fixing two test-only compile errors and a Statistics startup-admission fixture. No production safety gate was weakened.

The urgent follow-up also fixes explicit peer `send`/`message`/`request`: steer a running recipient, send when idle, and durably queue if live steering is unsupported. Explicit queuing remains deliberate. All 150 LiveTool/retention tests passed. Rebuilt native verification through activated `codealta-dev` proved a scripted peer message reached the held recipient in its original run; a real Codex interface smoke test and live peer-request test also passed. The real request acknowledged steering in about 20 ms and was consumed after the current shell tool finished, in the same run. No implementation/review was delegated after the user's no-sub-session instruction; native test sessions were bounded scripted fixtures or provider smoke tests only.

## Scope and defaults

| Choice | Behavior |
| --- | --- |
| New page | **Settings > Advanced > Storage** |
| Scratchpad | **Clear scratchpad on startup**, **on by default**; users can turn it off |
| Scratchpad ownership | Per-instance `<StateRoot>/scratchpad`: normal `~/.alta/scratchpad`, developer `~/.alta/dev/scratchpad`; no shared scratchpad required |
| Session retention | **Keep inactive sessions for:** an editable positive integer plus **days, weeks, months or years**; increment/decrement buttons and manual input; explicit **Never** option disables retention |
| Meaning of age | Time since last meaningful activity, strictly before one captured UTC cutoff calculated using the selected amount and unit; not creation date, file mtime or inspection time |
| Configuration | Global-only `[storage]` in `~/.alta/config.toml`; omit default values and remove an empty table |
| When changes apply | Next startup; editing the amount, unit or enabled state does not immediately delete data |
| Session cleanup | One background pass after startup and restoration/protection readiness; no concurrent deletion workers |
| Default period | **3 calendar months**, with retention enabled, as most recently requested by the user |
| Desktop/TUI | Desktop settings and cleanup feature. Shared lifecycle safety also covers TUI; no new TUI settings screen |

Suggested nondefault configuration:

```toml
[storage]
clear_scratchpad_on_startup = false

[storage.session_retention]
amount = 45
unit = "days" # days, weeks, months, years
```

For **Never**, persist `enabled = false` in `[storage.session_retention]`. The defaults for that table are `enabled = true`, `amount = 3`, `unit = "months"`. Preserve any nondefault amount/unit when disabling retention so re-enabling restores the selected duration. Omit default-valued fields and empty tables when saving. No fixed duration-preset strings.

Absent configuration means **scratchpad cleanup on / retention enabled for 3 months**. An invalid persisted cleanup setting disables that cleanup and reports a configuration error; it must not silently become a destructive default. Preserve unknown TOML keys with the existing merge writer. These are the user-selected defaults; safety gates determine whether an individual cleanup can run, not a different default policy.

Validate retention as a positive whole-number amount and a known unit. Reject blank, zero, negative, fractional, nonfinite or overflowing values rather than silently converting/clamping them to a shorter duration. Days use UTC day arithmetic, weeks use checked multiples of seven days, and months/years use UTC calendar arithmetic (`AddMonths`/`AddYears`), not 30/365-day approximations. If a cutoff cannot be represented, report invalid configuration and skip retention. Capture now once per pass; retain sessions exactly on the cutoff.

## Non-negotiable deletion boundary

- **Storage classification is not deletion authorization.** The chart may measure everything, but only the owning instance's scratchpad contents when cleanup is enabled (the default) and individually verified expired-session artifacts are cleanup candidates.
- Configuration, credentials, provider definitions, prompts, skills, plugins, plugin data, projects, spaces, worktrees/checkouts, unsent drafts, databases/backups, saved Statistics, caches/logs, unknown files and the peer instance's state are informational-only. Do not offer a generic delete-folder, cache purge or "free all space" action.
- Never remove a session's project or working directory. Never delete a date directory or the sessions root recursively. Remove empty date directories only after confirmed session-file removal.
- Resolve paths from trusted host layout and verified session identity, never a renderer-supplied path. Reject linked/reparse roots and ancestors; do not follow descendant links/junctions. Leave unsafe entries and report them. No ACL changes, read-only-attribute clearing or unbounded retry.
- Treat recursive scratchpad clearing as best-effort removal of ordinary files/directories while preserving its root. "Never fail" means no unhandled startup failure: locked/inaccessible/unsafe items produce a truthful partial/skipped outcome, not a false success.
- No cleanup against the user's real `~/.alta` during implementation or verification. Native feature checks require explicitly isolated test storage; stock `--dev` still shares real configuration and, before this change, scratchpads.

## Research that shapes the design

| Finding | Source |
| --- | --- |
| Deleted-session facts already survive a missing journal, but Forget Deleted removes them | `src/CodeAlta.Plugin.Statistics/Store/StatisticsStore.cs:174,239` |
| Reset empties every table with the Statistics prefix | `src/CodeAlta.Plugin.Statistics/Store/StatisticsStore.cs:268` |
| Application DB can recover to an older backup or an empty generation; its normal writes use WAL/NORMAL | `src/CodeAlta.Catalog/ApplicationDatabase.cs:38,983`; `doc/catalog-and-config.md:204` |
| Full preservation needs quarter-hour facts, histograms, extrema, runs and session lineage, not just totals | `src/CodeAlta.Plugin.Statistics/Store/FactTables.cs:169`; `doc/statistics.md:27,154` |
| End-of-read can still have pending bytes; current incremental fingerprint is not a whole-file digest | `src/CodeAlta.Plugin.Statistics/Journal/JournalScanner.cs:104,245` |
| Scratchpads use GlobalRoot while normal/dev instance locks and journals use separate StateRoots | `src/CodeAlta.Orchestration/Runtime/SystemPrompts/SystemPromptBuilder.cs:617`; `src/CodeAlta.Hosting/CodeAltaInstanceProfile.cs:45` |
| Existing owned deletion reserves operations and rechecks actors/children, but has no archival handshake | `src/CodeAlta.Orchestration/Runtime/OwnedSessionCommandService.Delete.cs:13`; `SessionRuntimeService.Delete.cs:13` |
| Current file-store deletion removes the journal, not its attachments/trace | `src/CodeAlta.Agent/Runtime/FileSystemAgentSessionStore.cs:719`; `AgentRuntimePathLayout.cs:59` |
| Metadata UpdatedAt can advance on inspection; it is not a retention-age contract | `src/CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs:3595,3854,4304` |
| Desktop open tabs and drafts live in browser-local state, including inactive spaces | `src/CodeAlta/frontend/src/main.tsx:335,854`; `OwnedSessionPanel.tsx:110` |
| Existing charts support lazy-loaded pie/donut, themes and accessible alternatives | `src/CodeAlta/frontend/src/charts/chartEngine.ts:6`; `Chart.tsx:127,174` |

### 1. Startup scratchpad cleanup

Create one tracked, instance-owned startup operation before exposing runnable session services. Scratchpad clearing runs by default unless the user disables it or a safety/configuration check prevents that pass. Run synchronous filesystem enumeration/removal on a background worker. Keep an immutable status snapshot available to the startup window and, later, Storage.

**Admission:** new/resumed sessions and every execution route must wait until the original cleanup worker has actually stopped. Cover direct hub calls, provider-autonomous turns, restored queues, automations, reminders, jobs, peer/child delivery, live tools and plugin orchestration, not only the Send button. Wait before acquiring actor/mailbox ownership to avoid deadlocks. The profile-size scan is unrelated and must never block session admission.

**Instance isolation (user clarification):** normal and developer scratchpads need not be shared. Resolve scratchpads from the owning **StateRoot**, not GlobalRoot: normal `~/.alta/scratchpad`, developer `~/.alta/dev/scratchpad`. The existing per-StateRoot instance guard plus the local startup barrier protects updated hosts; do not introduce a global shared/exclusive lifetime lease for isolated scratchpads. Each host may clear only its own root, including a developer host. Shared configuration stays read-only at developer startup.

Pass a separate instance-state-root input from `AgentInstructionTemplateProvider` into `SystemPromptBuildRequest`/`SystemPromptBuilder.ScratchpadFile`. Do not repurpose `UserCodeAltaRoot`: prompt overrides, skills and other user resources must remain shared. Keep the same session hash/filename format and support normal-profile fallback for callers without an explicit state root. Cover desktop/TUI and normal/developer prompt generation in regression tests.

Do not move, copy or remove existing shared scratchpads merely to separate the instances, and do not rewrite paths in historical journals/checkpoints. The normal root remains unchanged; new developer prompts use the developer root. Confirm behavior of resumed developer sessions with old scratchpad references. Document the path change and update the normal/dev shared-versus-private storage tables.

**Mixed-version transition:** an older developer binary still writes the normal scratchpad path. Isolation of new binaries alone does not protect that legacy case. Retain a narrow compatibility test/gate for clearing the legacy-shared normal root; if exclusive safety cannot be established, skip that cleanup rather than relying on a racy PID/file-existence check. This does not require a shared lifetime lease between updated instances and must not block cleanup of the new isolated developer root.

**Feedback:** indeterminate "Clearing scratchpad…", optionally with removed-item count, rather than a made-up percentage or a preliminary whole-tree count. Replace one keyed toast with the completion/partial/skipped outcome; retain details in Storage. Late frontend subscribers receive the latest status. Cancellation/shutdown joins the actual worker; a canceled waiter never releases the gate while deletion is still running.

### 2. Durable saved Statistics

Keep a durable store under the explicit instance-owned name:

`<StateRoot>/saved_statistics_after_deleted_sessions/`

Use a small dedicated SQLite archive (`archives.sqlite3`) with immutable versioned per-session payloads and transactional receipt/projection-generation metadata. Reuse the existing SQLite dependency; use a durability mode appropriate to irreplaceable records. Unlike the recoverable application database, this store **must not recreate itself empty or restore an older generation silently** after corruption. A corrupt/unsupported archive stops retention and surfaces a repair/restore error. Document this narrowly justified durable-store exception to the application-database ownership rule.

Keep filesystem/database persistence in Catalog, statistics DTO/reduction/import logic in Statistics, and retirement orchestration in the existing headless runtime. Wire a narrow archival capability in desktop composition rather than making orchestration depend on the Statistics UI or starting a second plugin engine. The archive survives Statistics disable/unload and generic projection-table removal.

Archive the seven quarter-hour families, extrema, runs (including skipped time), sanitized session/project identity and lineage, independent archive/facts versions, metric availability, coverage and source/payload digests. Do not copy prompts, answers, tool arguments/results or the entire reducer checkpoint. Rollups are derived and are rebuilt from the base facts.

**Existing deleted sessions:** before the first destructive reset of the upgraded Statistics engine, preserve any already-retained facts whose journals are gone in this store. Record their historical facts version and available coverage honestly; do not invent full-source verification when the source has already disappeared. This migration does not issue permission to delete another journal.

**Limited history:** do not silently override a user's disabled Statistics plugin or limited historical-read choice. Retention skips candidates without complete, current-version, full-journal coverage and reports how to complete Statistics history. An already-complete candidate can be preserved without restarting an unrelated paused history job. No archive participant means no automatic session deletion.

### 3. Archive-first session retirement

1. Derive a stable **last meaningful activity** value from accepted prompt/work/run events, excluding observation, metadata refresh and Statistics reads. Persist a separately named projection and derive legacy values by streaming the journal. Unknown/unreadable/future-inconsistent activity protects the session; creation-time fallback applies only to proven-empty journals.
2. Wait for a host-visible protection snapshot of open sessions and drafts across all desktop spaces/windows. Claim protection before opening a session so opening and retention cannot race. If restoration/protection is unknown, postpone deletion.
3. Select expired candidates in the current owned StateRoot, excluding live/starting/queued work, pending permission/input, remote control, provider background work, jobs, reminders and outstanding delivery. Do not equate an idle-looking UI row with safe deletion.
4. Respect lineage: protect families with any retained/live member; for a wholly eligible family, reserve its members through the existing ownership boundary and retire leaves before parents. Invalid/cyclic/ambiguous graphs are skipped. Keep archived lineage for Statistics.
5. Under retirement ownership, produce/verify a full-session statistics snapshot and exact source identity/digest. Serialize preparation with history, flow, reset and forget. Incomplete tails, parse/coverage uncertainty or source changes invalidate eligibility.
6. Commit the archive payload and deletion intent durably. Reopen and verify payload rows/digest, coverage and representative invariants. A typed receipt binds the exact journal generation to that archive; no receipt means no deletion.
7. Revalidate source identity, age and exclusions while still owning retirement. Delete only the exact journal and its proven-owned `<id>.attachments` and optional trace from the owning layout. Do not sweep orphan-looking files or paths found inside the transcript.
8. Reconcile catalog/cache/projections and record partial artifact cleanup idempotently. A locked attachment can remain and be reported; no later retry may target new files at a reused path without revalidation.

Extend `OwnedSessionCommandService`/`SessionRuntimeService` retirement, not the weaker direct `DeleteSessionAsync` route. Keep filesystem and archive operations recoverable through ordering and receipts; do not attempt a transaction spanning SQLite and filesystem deletion.

Recovery cases: no archive => no delete; archive with unchanged source => revalidate before retry; archive with changed source => invalidate that deletion attempt; archive with absent source => rehydrate and finish reconciliation. Keep at most one effective generation per session in queries, even if an immutable earlier archive remains after a failed attempt.

### 4. Reset, upgrades and query behavior

- Reset validates archives before clearing its projection, then reseeds saved session facts/runs/identity and rebuilds rollups. Failure must leave existing readable data intact. The durable archive is never reset.
- Startup and application-DB recovery also rehydrate archived facts. Import replaces a session's effective projection; it never adds a second copy. A restored/reappearing journal supersedes that projection once verified, without erasing its durable archived version.
- Manual single-session deletion never removes saved archives. Existing "Forget deleted sessions" must exclude archived retention history and explain that limitation; do not add an archive-purge UI in this task.
- Keep old metric values as recorded. Newly introduced metrics are marked unavailable/not recorded for older archives, not silently represented as observed zeros; report mixed-version coverage where relevant.
- After Reset, archived charts remain available even if the user has not selected how much surviving journal history to read. Do not hide saved statistics behind today's `needsChoice` full-page card.
- Prove query equivalence for totals, periods, currencies, histograms/percentiles, records, projects/providers/models, session trees and sessions-at-once, before deletion, after deletion, after reset and after timezone changes.

## Settings UX

### Storage

Use the existing Settings page frame, typography and config-file location controls. Register Storage under Advanced; do not introduce a separate dashboard shell.

- Header: profile path, **file sizes** total, last-scan timestamp, Refresh.
- Compact existing ECharts donut beside a permanently available table where width permits: category, size, percentage. Stack cleanup and distribution cards, with cleanup first. Stable colors/order; percentages use the entire measured total even when chart legend visibility changes.
- Categories: sessions/attachments, scratchpads, saved Statistics, application database/backups, configuration/credentials, prompts/skills/plugins/plugin data, projects/working copies, cache/logs, other state, and separately counted developer data. Never double-count a subtree.
- Explain that the distribution is informational and configuration/personal files are protected. No trash icons, removable badges or per-category cleanup suggestions for protected data.
- Automatic cleanup section: an initially **on** scratchpad toggle and flexible retention controls, initially **3 months**, with compact inactivity/Statistics-preservation help. Use an explicit **Never** mode to disable automatic session deletion, not a magic zero or an oversized duration. A maintenance summary shows running, completed, partial and skipped outcomes with bounded reasons.
- Retention editor: use Blueprint **`NumericInput`** with visible increment/decrement buttons, manual entry and arrow-key support; `min={1}`, `stepSize={1}`, `minorStepSize={null}` prevents Alt-stepping by fractional amounts. Pair it with an **`HTMLSelect`** for days/weeks/months/years and a clearly labeled **After a duration / Never** mode control. Disable the amount/unit controls in Never mode without discarding their values. Keep controls compact and grouped, wrapping cleanly at narrow widths.
- Keep an editable string draft while typing and validate it independently of Blueprint's numeric-character filter (which allows floating-point characters). Commit a valid amount/unit together on blur/Enter or a completed step/unit change; never persist incomplete/invalid input or silently shorten it. Provide accessible labels and inline validation. Persist Never independently of an invalid in-progress amount so disabling cleanup is always possible.
- Read file metadata only. Count logical file length, including hidden files and WAL/backups; do not open credential or session contents to measure size. Explain that compression, sparse files and hard links can differ from allocated disk space.
- Missing/inaccessible/changing entries and skipped links produce partial coverage. Percentages are of measured bytes, not an invented full total. Empty profiles show a useful empty state.
- Scanning is cancellable and separate from cleanup. Supersede stale refresh results; closing Settings cancels its scan, never host-owned maintenance. Do not reuse the existing 20-second settings read timeout as a scan lifetime.
- Preserve keyboard access, text alternatives, reduced motion, light/dark themes and a narrow single-column layout.

**Blueprint check:** the installed `@blueprintjs/core` **6.21.0** already includes `NumericInput`; its local `lib/esm/components/forms/numericInput.d.ts` documents button positioning, typing/arrow/button callbacks, step sizes, bounds and string values. `src/CodeAlta/frontend/src/automations/AutomationEditor.tsx` already uses it. This is the appropriate existing component; no dependency or custom stepper is needed. See [Blueprint NumericInput](https://blueprintjs.com/docs/#core/components/numeric-input).

### Appearance / General / Permissions

Add only one extra page besides Storage: **Personalization > General**. Preserve the current Settings-opening destination.

| Page | Sections and controls |
| --- | --- |
| General | Language; Welcome page (show at startup and animate together); Startup and closing (restore Remote Control, window-close behavior) |
| Appearance | Theme and colors (theme, darker dark, color-scheme editor); Explorer (project sort, recent-session count, sub-agent count, collapse rail); Conversation and changes (width/reset, diff layout) |
| Permissions | Agent permissions (existing modes); Confirmations (ask before deleting a session, ask before archiving a project) |

This is a presentation move, not a preference migration. Keep the existing state owners, callbacks, local-storage keys, defaults and capability conditions. The "do not ask again" actions must remain synchronized. Local confirmation toggles still work when host-backed agent permissions are unavailable. Clarify that manual-action confirmations do not govern automatic retention. Keep the existing color-scheme editor and its explicit authoring/removal controls; they are unrelated to Storage cleanup.

Account for all **16 existing top-level Appearance preferences** exactly once across these pages (excluding the color editor's nested controls). Keep `useWindowPreferences` in `main.tsx`, move `LandingSettings` intact, and retain shell-backed width/startup/closing preferences rather than moving them to TOML. Test owned and non-owned windows and navigation with seeded nondefault preferences; opening a different Settings page must not write or reset them.

## Implementation checklist

Each step includes focused regression tests and a self-review; use sequential implementation/integration steps, with independent read-only reviews where useful.

- [x] **1. Lock down contracts and regressions.** Add failing tests for reset preservation, deletion receipt requirements, inspection-safe activity age, all-space protection, instance-owned scratchpad paths and startup admission. Finalize the legacy-shared-root compatibility gate before enabling destructive code there.
- [x] **2. Add global storage settings and read-only measurement.** Extend `CodeAltaConfigDocument`, its TOML context/merge saves and Catalog settings tests for default-on scratchpad clearing and enabled/amount/unit retention, default omission, Never and validation. Add host-captured-root metadata scanning, non-overlapping categories, partial/error/cancel semantics and protected fixture sentinels. The later 3-month default change is integrated, with a regression proving an explicitly saved one-month policy persists.
  - Configuration and metadata scanner verified with 23 focused Catalog tests. Invalid semantic settings and inaccessible configuration are reported rather than silently defaulted. Fixtures cover hidden/WAL/unknown/developer accounting, byte-identical protected files, linked roots, missing/invalid roots, exclusive-content locks and cancellation. Measurements are explicitly non-atomic and grant no deletion authority.
- [x] **3. Implement durable archival and projection restoration.** Add the archive DTO/store/receipt contract; migrate already-deleted facts; add full-source preparation and idempotent import/reset/recovery; add metric-version availability and archive-visible reset behavior. Keep automatic deletion disabled until the preservation tests pass.
  - Integrated uncommitted archive implementation and flushed sibling-marker fix for whole-directory loss. Parent Release verification passed 31 archive/real-native-retirement regressions, including reset/reopen without duplication and disabled/locked archive preservation. The initialization marker must survive; losing both marker and archive cannot be distinguished from first use.
- [x] **4. Implement startup cleanup safety.** Separate scratchpad paths by StateRoot and add tracked background clearing under existing instance ownership; prove all execution routes wait for actual termination, normal/dev paths are isolated and legacy-shared-root cases fail safely. Add replayable maintenance status and shutdown joining. Do not introduce a cross-instance lifetime lease or migrate existing files.
  - Integrated exact-handle Windows fixed-local-NTFS cleanup, instance-guard lifetime pinning, legacy developer exclusion, default/invalid config handling, splash progress and replay RPC. Other platforms/filesystems fail closed. Isolated regressions cover cancellation joining, managed faults, locked/read-only files, symbolic links, real junction swaps, shared config byte preservation and host admission; child full solution/frontend/Lunet and parent focused Hosting/Orchestration/Desktop checks pass. Independent-process legacy exclusion was explicitly qualified in child fixtures.
- [x] **5. Implement retained-session cleanup.** Add meaningful-activity projection, open/draft protection handshake, existing-owner retirement reservations, archive-first family/leaf processing and bounded artifact cleanup/recovery. Verify the default three-month policy and arbitrary supported amount/unit combinations with fault-injection and archive equivalence before shipping automatic deletion.
  - Runtime/desktop infrastructure, all producer lifetimes and real Statistics/native retirement tests are integrated and pass the combined suite. The native host qualifies only `external-work` after installing the shared registry everywhere; each native window remains independently unknown until the authenticated watch/inventory handshake. Model preparation pins cached provider registrations against live reconfiguration and shutdown. Synthetic E2E proves committed archive bytes, writer exclusion and real host Hub/model admission refusal before exact-handle deletion, then repeated reset/reopen preservation. Startup/watch/navigation/borrowed-owner mismatch, original-worker cancellation and complete retained-fault graph regressions passed against the integrated composition, not just an archive test double.
- [x] **6. Add Storage RPC and UI.** Follow `WorkItemsRpc`, generated DTO/source context, owned/unavailable/epoch checks, demo/test bindings and Settings registration. Use shared `Chart` and Blueprint `NumericInput` with unit and Never controls; add scanning/status/save, duration editing/validation and accessible/partial-state tests.
  - Final redesign integrated: stacked cleanup/size cards, table as the sole legend, large side-by-side donut when the page is at least 600 px wide and stacked below on narrower pages. Separate translated scratchpad/session status lines receive main-owned replay subscriptions with keyed notifications; unknown host codes remain bounded. Parent Release build, 13 Desktop status/protection tests and both mounted Storage/observer tests pass. A subsequent review found host-liveness loss could erase known terminal results; fixed by retaining epoch identity independently of observation availability, with unit and mounted outage/recovery regressions. Re-review found no blockers. Post-fix Release rebuild, TypeScript and all 12 focused unit/mounted tests pass (`job-38af5358`).
- [x] **7. Reorganize Settings.** Move General behavior and confirmations without changing persistence. Split Appearance into the three sections. Update mounted layout/preferences/confirmation tests and all affected labels/localizations.
  - Integrated all 16 preferences with unchanged ownership, persistence and capability gating. Focused real-main browser tests cover navigation without writes, both confirmation round trips and closing synchronization; layout checks cover six locales/narrow windows. Parent placement tests and TypeScript check pass. Native screenshot refresh remains in step 8.
- [x] **8. Verify, document and review.** Inspect the actual built desktop UI in both themes and narrow/zoomed layouts, including running/partial cleanup and post-reset historical Statistics using isolated fixtures. Run focused tests first, then solution build/all .NET tests (including TUI), frontend tests/typecheck and Lunet. Update public/internal docs, inspect the full diff, and record any platform/verification limits.
  - Earlier native inspection covered light/dark at 1900×1150, 1200×800, 900×650 and 640×560; valid/invalid/Never saves and keyboard checks passed. Parent fresh rebuilt checks confirmed default-on/3-month controls, synthetic scratchpad removal, protected sentinels unchanged, narrow light/dark without overflow and truthful archive-unavailable skipping. Final large dark capture is 2400×1500, matching existing site screenshots; `AGENTS.md` now distinguishes this presentation baseline from narrow-only tests. Screenshots/evidence are under the isolated `CodeAltaDelivery-b0cb167d59a745e78cd911f435c19d30/browser/ui` root. The temporary encrypted Codex access copy excluded the refresh token and was deleted after tests; no normal-profile change or restart.
  - Verification limit: explicit-root native fixtures deliberately disable plugins, so native post-reset Statistics presentation was not exercised. Real-engine archive/deletion/reset/reopen equivalence is covered by passing synthetic integration tests instead. Unsupported platforms/filesystems and unqualified TUI owners remain fail-closed. The earlier overlapping-run Edge startup failures did not recur with serial frontend execution. Final peer-only source refinement keeps provenance/receipt errors outside routing fallback to avoid duplicate accepted input; final Release solution rebuild (zero warnings/errors), all 150 focused tests and Lunet passed (`job-31a6019e`).

## Verification gates

| Area | Required evidence |
| --- | --- |
| Protected storage | Config/auth/extensions/plugin data/projects/worktrees/drafts/database/archive/unknown sentinels stay byte-identical; paths cannot be supplied for arbitrary deletion |
| Scratchpad | Runs by default, explicit false skips it; deep recursion, empty/missing root, denied/locked files, links/junctions/cycles/path swaps, partial/canceled cleanup; no sessions admitted while worker remains active |
| Cross-process | Normal/dev/TUI simultaneous startup with distinct scratchpad roots, active peer unaffected, legacy shared-root behavior and existing instance-lock recovery; no real profiles involved |
| Eligibility | Default 3 calendar months; arbitrary positive amounts in days/weeks/months/years; UTC/month-end/leap boundaries, exact equality and overflow rejection; browsing/Statistics reads not aging or refreshing sessions, legacy malformed journals, open/draft/queue/job/reminder/provider work and linked families |
| Archive transaction | Disk-full/denied/corrupt/unsupported archive, source mutation, partial journal, faults immediately before/after durable commit and deletion, retry/idempotence, restored journals; fail closed |
| Statistics | All query families equal through deletion/reset/recovery; old/new metric availability; timezone rollups; no duplicate generations; disabled/limited-history behavior |
| Settings | Absent config resolves to scratchpad on and retention enabled for 3 months; default fields/tables omitted; custom amount/unit and Never round trips, explicit scratchpad false persists; invalid values/units/cutoffs skip cleanup; unknown TOML preserved; existing Appearance/Permissions values survive navigation without writes or resets |
| UI | Default-on toggle and 3-month editor; increment/decrement, manual input, keyboard, unit changes, Never/re-enable with preserved duration, invalid/partial entry with no unintended saves; actual component/browser tests plus native window checks: light/dark, narrow and zoom, screen-reader labels/table, reduced motion, empty/partial/error/loading, single keyed toast, late status subscriber |

Relevant suites include Catalog config/ApplicationDatabase tests, Statistics Store/Engine/Queries/SessionFilter/DerivedMetrics tests, Hosting single-instance process tests, Orchestration lifecycle/queue tests, Desktop startup/delete/RPC tests, file-store/attachment tests, and frontend appearance/charts/session-tabs/space/permission tests. Test double injection must cover real profile/auth/discovery dependencies; a temporary outer root alone is not proof of isolation.

Docs: `readme.md`, `site/docs/workspace.md`, `site/docs/sessions.md`, `site/docs/plugins/statistics.md`, `doc/catalog-and-config.md`, `doc/statistics.md`, `doc/statistics-facts.md`, `doc/desktop.md` and the lifecycle/storage boundaries in `doc/development-guide.md`. Update `AGENTS.md` and developer-instance storage tables for the now-private scratchpad. Update references to settings moved out of Appearance, and make current facts/schema versions and trace-root ownership agree with source.

## Review / delivery boundary

The latest user-selected policy is **scratchpad cleanup on by default** and **session retention enabled for 3 calendar months**, adjustable with a numeric amount and duration unit or disabled with **Never**. Review also covers archive-first/fail-closed behavior, informational-only protected categories, and the Settings organization above. No generic cleanup, archive purge, project/worktree deletion or broad provider-history cleanup is included. Safety verification is a prerequisite for execution, not authorization to alter these defaults.

Research established the risks and integration points, not the safety proofs. The first implementation gate must demonstrate scratchpad isolation and legacy-shared-root compatibility, host-visible open/draft protection, reliable activity age and durable source-bound archive receipts on isolated fixtures. Plan approval does not waive these gates; a failed gate leaves the corresponding automatic cleanup disabled.

Do not promise recovery from physical loss of the archive drive; this design preserves facts through normal deletion, process interruption, Statistics reset and application-database recovery. If legacy-instance exclusion, stable-source ownership or archive durability cannot be demonstrated, leave the affected cleanup disabled and report the exact blocker rather than weakening the contract.
