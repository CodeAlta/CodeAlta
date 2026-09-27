# Continue project-row context actions qualification

- Status: Parent accepted — commit and next-batch dispatch
- Plan file: `.alta/plans/2026-09-27-project-context-actions-continuation.md`
- Created: 2026-09-27
- Task: Continue the user-authorized whole-tracker execution with the sole child and independent parent acceptance.
- Git: Not ignored; commit this continuation plan with accepted related implementation.

## Objective
- Diagnose the current mounted menu failure, finish this bounded implementation, independently verify and commit accepted work, then dispatch one next substantial tracker batch.
- User has already authorized execution and commits, no push. This plan records continuation, not feature acceptance.

## Context and evidence
- Baseline accepted tool inspection: `29a11a83`; candidate project actions remain unaccepted and uncommitted.
- `tmp/project-context-actions-20260927/REPORT.md`: seven files28/28 passed; full App failed at `settingsShell.test.ts:2731` after Shift+F10, missing `.project-actions-menu`. Cause unknown. Final TS/build/whitespace and substantial mounted coverage unrun.
- `ProjectRowActions.tsx:47-57` checks row visibility/access and renders only current review; focus/dismissal at99-108. These are diagnostic candidates, not proven causes.
- Candidate files: ProjectRowActions, projectRowActionAccess, projectRowActions.test, ProjectRailRows, ProjectDetailsEntry, main, localization, style and settingsShell.test under `src/CodeAlta/frontend/src`.

## Assumptions and open decisions
- Existing user execution authorization remains in force. Only the existing child writes implementation; parent tests only after child stops.
- No new scope decision required. Unexpected new failures require bounded evidence before correction, never blind retries.

## Design notes
- Preserve selected-only mutation authority and original guarded workflows. Menu opening never selects/navigates/writes; nonselected Open/Details remain read-only entry points.
- No new backend contracts, deletion/config authority, native launcher or dependency changes.

## Risks and challenges
- Failure could be hidden rail, eligibility, event dispatch or dismissal; do not assume timing or keyboard harness cause.
- Preserve exact project identity, revision/admission owners, uncertain originals and catalog/host/scope/modal ABA fencing.
- Preserve original App scenarios/assertions and all red evidence.

## Implementation checklist
- [x] Read exact failing scenario, candidate access helper and App context wiring; confirmed sole child idle. Failure cause remains unestablished; hidden rail/access refusal and later dismissal must be distinguished.
- [x] Dispatch existing child `01a0dce6-0436-719a-a327-cdc6609ae16c` for one bounded diagnostic capturing event delivery, visibility, eligibility and opening/dismissal sequence without weakening assertions or waits. Continuation received; fixture-bundle-only instrumentation added. Original log SHA256: `F7530AB5B239C709FC2483A4C4CBCABFDA2C82B368C6FA7FCAD481185EC9CA43`.
- [x] Permit only evidence-supported minimal correction; stop if cause remains unknown or new authority is needed. One diagnostic conclusively found viewport 750x451, connected but hidden project rail, body retaining focus, delivered Shift+F10 entering `open()` then refusing before admission; context active=false. No menu mount/dismiss sequence. Removed temporary instrumentation; new scenarios now reveal the rail through the real Show projects UI. Production guards, original keyboard route, assertion and waits unchanged.
- [x] Authorized Details diagnostic established successful admission/open followed by StrictMode layout cleanup calling native close and synchronously retiring review. Removed redundant native close from dialog cleanup, preserving real DOM removal and all row/modal guards. Instrumentation removed. Existing selected Details complete file1/1 passes; actual-App selected/nonselected Details and four Copy/own-ABA/other-modal/removal regressions pass before next failure. Evidence in REPORT and `details-diagnostic-*`/`details-fix-*` logs.
- [x] Finish missing mounted keyboard/pointer, Open/Details, selected mutation confirmation/cancel/uncertainty, missing/duplicate/changed row, own-dialog ABA, locale/no-RPC and narrow-theme coverage. Authorized correction asserts one enabled Cancel (Escape) control and clicks it; original assertions/order/waits preserved. Complete App passes after remaining identity/Open/real ContextMenu+Enter/repeat/IME/390+1120 light/dark cases. Details exemption now accepts only its initial exact native opening generation, not later generation changes. End-user docs added; final related-file matrix next.
- [x] Update actual ignored tracker and report, leaving parent acceptance unchecked until independent verification. Diagnostic log/JSON and corrected full-App exit1 retained; original log hash unchanged. No temporary instrumentation remains.

## Verification checklist
- [x] Authorized test-only SessionActionMenu correction replaces invalid direct hook invocation with real React StrictMode mounted browser interactions. SSR test retained, runtime request/disabled/confirmation-boundary assertions preserved; still2 tests, new local fixture only. Prior red retained.
- [x] Child runs complete affected tests from `src/CodeAlta/frontend`, strict TS/build/scoped whitespace with durable unique exit logs. Fresh10 complete files34/34 pass, zero skips/failures, including corrected SessionActionMenu and final-tree App. Strict TS/build exit0; scoped whitespace checks pass. See REPORT verified-* table.
- [x] Supplemental preservation concern independently resolved: parent Node comparison reads Git stdout as bytes and explicitly decodes both sources UTF-8; removing only appended project scenarios produces exact original contents after CRLF normalization. Original failed PowerShell comparison retained, not relabeled passing.
- [x] Parent reviewed complete production/test/docs diff and independently reran all10 complete scoped files:34/34, zero skips/failures. Strict TS/build exit0, existing chunk advisory only; tracked whitespace clean. Evidence `tmp/project-context-actions-20260927/parent-*.log`.
- [ ] Check actual acceptance checkboxes, commit only accepted paths and this plan; exclude ignored evidence and unrelated files.
- [ ] Select and dispatch one substantial next implementable tracker batch, then yield without polling.

## Handoff notes
- Driving session `01a0c7ba-0044-77dd-b433-f9223181b86b`; preserve existing30-minute reminder without duplication.
- No full-suite/site/M7/native/live-provider/user-data mutations; parked renderer/dependency/source blockers remain parked.
- Preserve `.alta/config.toml`, `.alta/mcp.json`, archive and unrelated ProjectReferencePresentation EOF work. Never stage `tmp/`.
- Restore Default mode for execution; current Plan mode cannot authorize implementation through delegation.
