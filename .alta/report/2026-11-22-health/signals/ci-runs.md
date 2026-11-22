# CI runs of CodeAlta (GitHub Actions, workflow `ci`)

The last 300 runs, from 2026-11-07 to 2026-11-22, all branches (`gh run list --limit 300`). Leads, not findings.

| Conclusion | Runs |
| --- | --- |
| success | 161 |
| failure | 67 |
| cancelled | 69 |
| in progress | 3 |

`main` has 170 of the 300 runs. 20 runs were run again (attempt 2 or 3); 10 of them are on `main` and passed on the later attempt.

## What failed in the first attempt of the 10 runs of `main` that passed later

Read with `gh api repos/CodeAlta/CodeAlta/actions/runs/<id>/attempts/1/jobs` and the log of each failed job.

| Run | Date | First attempt |
| --- | --- | --- |
| 44574308664 | 2026-11-19 | job `Tests (macos-latest, platform-specific)` cancelled after 15 minutes, no step failed |
| 44327876124 | 2026-11-18 | job `Tests (macos-latest, platform-specific)` cancelled after 15 minutes, no step failed |
| 43453313598 | 2026-11-13 | job `Tests (macos-latest, platform-specific)` cancelled after 15 minutes, no step failed |
| 43619496376 | 2026-11-14 | Windows: `RecoveryWaitsForAnAdmittedBackupBeforeReplacingItsSource` failed (6 s) |
| 43323247389 | 2026-11-12 | Ubuntu: `ARunCodeAltaClosesWhileItSleeps_IsInterrupted_AndResumesByItselfOnceItWakes_Once` failed on `Assert.AreEqual(("interrupted", kept.SleepingUntil), (ended.Status, ended.WakeAt))` |
| 43251505041 | 2026-11-12 | Windows: `ASnapshot_WhoseIndexGitCannotReplace_IsWrittenThroughAnIndexGitCreates` failed on `Assert.IsNotNull(expected)`; `Frontend tests (3/6)` failed too (test not identified in the log) |
| 43103592542 | 2026-11-11 | job `Pack (CodeAlta)` cancelled after 15 minutes |
| 42492304593 | 2026-11-08 | job `build` cancelled at its start (older single-job workflow) |
| 42401506019 | 2026-11-07 | job `build` cancelled after 61 minutes (older single-job workflow) |
| 43719124489 | 2026-11-15 | no job failed in the first attempt (the run was started again by hand) |

Three .NET tests failed once and passed on the same commit: each is a lead for a flaky test. An open work item already covers the first one (`.alta/tasks/2026-11-14-make-the-database-recovery-test-independent-of-the-speed-of-6zpc.md`).

## The workflow

`.github/workflows/ci.yml`: Linux runs every test of the solution; Windows and macOS run only the test classes that name an operating system (`OperatingSystem.IsWindows()`, `IsMacOS()`, `IsLinux()`, selected by a `grep` in the workflow); the frontend tests run in 6 shards. Job limits: 60 minutes for the .NET test matrix, 20, 30 and 45 for the other jobs.
