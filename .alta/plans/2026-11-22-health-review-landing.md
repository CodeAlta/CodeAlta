---
title: Land the 16 findings of the health review of 5.0
status: done
created: 2026-11-22
summary: The findings of `.alta/report/2026-11-22-health` (issue #496), implemented by child sessions in worktrees (GPT 6.2 Sol, max), reviewed in groups by Gemini 4 Neon, merged into `feature50` one by one, tested in full and in a developer instance.
---

# Land the 16 findings of the health review of 5.0

The report is [`.alta/report/2026-11-22-health/report.md`](../report/2026-11-22-health/report.md); each finding there has its evidence, change, first step and verification. The owner kept all 16 and asked for the whole plan.

## How the work runs

- **Work packages** group findings by area, so that two packages running at once rarely touch the same files. Each runs in a child session of its own, on `codex:gpt-6.2-sol@max`, in its own worktree (`alta/...` branch from `feature50`), committing each step there.
- **At most three packages at once.** Children build in Release in their worktree, run only the tests of what they touch (two test processes at most), and never start the desktop application.
- **Review:** when a package is done, a read-only session on `antigravity-acp:gemini-4-neon@high` reviews its diff; one reviewer session per wave reviews all the packages of that wave. The child fixes what holds up.
- **Integration**, by the driving session, one package at a time: rebase the branch on `feature50`, fast-forward, build Debug in the main checkout, then after each wave the whole .NET suite and `npm test`, and the areas touched in a developer instance (`alta --dev`, Debug build; the user's CodeAlta runs from `bin/Release` of this checkout). The worktree is removed right after its merge.
- Nothing is pushed until the owner asks; the note on #496 comes with that push.

## Wave 1

- [x] **P1 Automations and `alta` policies**: F01 (automation state within its read limit), F02 (claims granted only once written), F08 (remove the early return that drops credential policies). Merged `f9052d5ed`..`5d5d94eac`; review: one weak assertion, fixed.
- [x] **P2 Provider settings**: F03 (one locked raw provider edit in the store, 13 callers moved), F04 (delete the TUI's copy of a provider). Merged `796efb76e`..`45b02dc95`; review: ready, no problem.
- [x] **P3 Catalog**: F05 (session-list columns added at open from one list), F09 (delete the raw skill-scanning code), F13 (fallback review snapshot keeps the tracked files, failures keep their reason). Merged `cf1a8fd56`..`9aa74dde9`; review: the fallback failed during a merge conflict, fixed.
- [x] Review of wave 1, fixes, merge, full tests, developer instance: provider form on an isolated profile, session list on the existing developer database, Changes review, automations page. Instance checks passed (edit of one provider, stale form refused then saved after reload, review approved, automations page, 333 sessions of the developer database listed). Full tests: all .NET tests pass after `3d2cfd9d1` (a missing translation of P2's new TUI message), `npm test` 3271/3271.

## Wave 2

- [x] **P4 Settings scope**: F06 (Skills, Prompts and Plugins of a sub-project read the inherited scope). Merged `4eadf247e`..`4cafd030f`; review: four defects fixed (built-in plugin switches, prompt scopes in previews, a broken parent config blocking local toggles, Global bulk toggles), one claim did not hold.
- [x] **P5 Test time and CI**: F12 (36 deadlines through `TestTime`, 45 fixed delays replaced by waits), F14 (search kernel test on the platform runners). Merged `18725408f`..`bce11c85e`; review: absence checks raced the delivery of CDP events, fixed and proven by breaking the cancel on purpose. Browser files 181 s to 144-158 s per run.
- [x] **P6 Desktop window state**: F11 (drafts of the rename, project rename, sidebar filter and Settings search out of `App`). Merged `3bb1ead88`..`ff211b621`; review: ready. Developer instance: sidebar filter and Escape, Settings search and `alta settings open` during it, session rename.
- [x] Review of wave 2, fixes, merge, full tests, developer instance: Settings of a sub-project (isolated profile with a monorepo), typing in the four fields with render counts. Full tests: all .NET tests pass, `npm test` 3284/3284. Instance checks: inherited skill and prompt of a sub-project listed read-only; filter, renames and Settings search in the developer instance.

## Wave 3

- [x] **P7 Desktop RPC**: F10 (remove the browse mode without a host), then F07 (JSON types declared beside each RPC service, one stub helper in `demo-api.ts`). Merged `d090089dd`..`5d53c5bda`. F10 landed: explicit roots require `--allow-owned-host`, 102 lines and 55 registrations gone. F07 only in part: the JSON source generator refuses `[JsonSerializable]` spread over partial declarations (CS8785, duplicate hint name `DesktopJsonContext.Boolean.g.cs`), and NeoAstra takes one context, so `BootRpc.cs` keeps its list; the demo stubs go through one helper. Review: two weak startup tests and an unused branch, fixed.
- [x] **P8 Agent instructions**: F15 (plugin-runtime skill to a workflow and an index), F16 (desktop-window procedure out of `AGENTS.md`, `doc/architecture.md` completed). Merged `7b69770ee`..`1fc10d0e8`: skill 8,958 to 1,335 words, `AGENTS.md` 3,041 to 830; reader test 8/8; review: a stale link and the plugin services restored.
- [x] **P9 Desktop window state, second step of F11**: the state of the dialogs that no other surface reads (session delete, archive, move; project delete) out of `App`, one dialog per commit. Merged `d61602f01`..`a2fb148a0`: `useState` in `App` 130 to 117; review: ready.
- [x] Review of wave 3, fixes, merge, full tests, developer instance: a broad smoke test of the window (every Settings page, sessions, a send). Checks passed: the retired command refused before writing, recovery from a broken `config.toml`, 41 Settings pages without error, a send answered; P9's delete, archive and move dialogs; the terminal title reset of `sw3q`. Full tests after P7 and P8: all .NET tests pass, `npm test` 3285/3285. Final run on `f014e8493`: all .NET tests pass but `HostFollower_StartsWithTheHost_AndReadsTheMarkersWrittenBefore`, which passed 3 of 3 alone (a failure under load); `npm test` 3292/3293, the one failure being `editMenu.editors.browser.test.ts`, flaky before this work (task `x9n8`), whose fixture none of these changes touch.

## Closing

- [x] Each finding's `status` set in `report.json` (15 done, F07 deferred), three numbers of the report corrected, the report built again, the ledger updated with the commits and the measured results
- [x] The baselines of the ledger measured again at the end, the same way
- [x] Task `sw3q`, found while landing F11 (a blank terminal title could not restore the terminal's own title), fixed and completed (`f014e8493`): the work leaves no task behind
- [x] Branches, worktrees and temporary profiles removed; the developer session archived by a check restored
