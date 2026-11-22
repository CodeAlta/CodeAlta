---
title: Land the 16 findings of the health review of 5.0
status: in-progress
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
- Nothing is pushed until the owner asks.

## Wave 1

- [x] **P1 Automations and `alta` policies**: F01 (automation state within its read limit), F02 (claims granted only once written), F08 (remove the early return that drops credential policies). Merged `f9052d5ed`..`5d5d94eac`; review: one weak assertion, fixed.
- [x] **P2 Provider settings**: F03 (one locked raw provider edit in the store, 13 callers moved), F04 (delete the TUI's copy of a provider). Merged `796efb76e`..`45b02dc95`; review: ready, no problem.
- [x] **P3 Catalog**: F05 (session-list columns added at open from one list), F09 (delete the raw skill-scanning code), F13 (fallback review snapshot keeps the tracked files, failures keep their reason). Merged `cf1a8fd56`..`9aa74dde9`; review: the fallback failed during a merge conflict, fixed.
- [ ] Review of wave 1, fixes, merge, full tests, developer instance: provider form on an isolated profile, session list on the existing developer database, Changes review, automations page

## Wave 2

- [ ] **P4 Settings scope**: F06 (Skills, Prompts and Plugins of a sub-project read the inherited scope)
- [ ] **P5 Test time and CI**: F12 (33 deadlines through `TestTime`, 39 fixed delays replaced by waits), F14 (search kernel test on the platform runners)
- [ ] **P6 Desktop window state**: F11 (drafts of the rename, project rename, sidebar filter and Settings search out of `App`)
- [ ] Review of wave 2, fixes, merge, full tests, developer instance: Settings of a sub-project (isolated profile with a monorepo), typing in the four fields with render counts

## Wave 3

- [ ] **P7 Desktop RPC**: F10 (remove the browse mode without a host), then F07 (JSON types declared beside each RPC service, one stub helper in `demo-api.ts`)
- [ ] **P8 Agent instructions**: F15 (plugin-runtime skill to a workflow and an index), F16 (desktop-window procedure out of `AGENTS.md`, `doc/architecture.md` completed)
- [ ] Review of wave 3, fixes, merge, full tests, developer instance: a broad smoke test of the window (every Settings page, sessions, a send)

## Closing

- [ ] Each finding's `status` set to `done` in `report.json`, the report built again, the ledger updated with the commits and the measured results
- [ ] Signals measured again for the baselines of the ledger
- [ ] Note on #496
