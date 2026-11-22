# Health ledger

Decisions and numbers that outlive one report. Reports are in the folders beside this file.

## Decisions

| Date | Finding | Decision | Reason or result | Report |
| --- | --- | --- | --- | --- |
| 2026-11-22 | All 16 findings | accepted | The owner kept all 16 and asked for the whole plan (`.alta/plans/2026-11-22-health-review-landing.md`, #496) | `2026-11-22-health` |
| 2026-11-22 | F01 Keep the automation state within its read limit | done (`f9052d5ed`, `5d5d94eac`) | A save over 8 MiB drops the oldest completed history; the state reopens unpaused with its newest run, watermarks and approvals | `2026-11-22-health` F01 |
| 2026-11-22 | F02 Grant an occurrence only once its claim is written | done (`225b1b527`) | An unwritable claim file: 0 runner starts, a "storage unavailable" skip; a reopened store refuses the occurrence once it ran | `2026-11-22-health` F02 |
| 2026-11-22 | F03 Edit the providers of `config.toml` through one locked raw edit | done (`c07d4a9ea`..`45b02dc95`, `3d2cfd9d1`) | Callers of the whole-list save outside the store: 13 to 0; two writers from one revision: 2 `ok` to 1 `ok`, 1 `conflict`; checked in a real window | `2026-11-22-health` F03 |
| 2026-11-22 | F04 Delete the TUI's copy of a provider | done (`796efb76e`) | 64 lines removed; untouched providers keep `server` and `context_size` | `2026-11-22-health` F04 |
| 2026-11-22 | F05 Add the missing columns of the session list at each open | done (`65e391009`, `d8bcbf6da`) | 9 hand-written migrations to 10 declared columns, 120 lines fewer; a column added without a version bump no longer breaks the list | `2026-11-22-health` F05 |
| 2026-11-22 | F06 Give Settings the inherited scope of a sub-project | done (`4eadf247e`..`4cafd030f`) | Skills 1 of 3 to 3 of 3, inherited prompts and plugins listed read-only; checked in a real window | `2026-11-22-health` F06 |
| 2026-11-22 | F07 Let an RPC service declare its JSON types in its own file | deferred | The source generator refuses `[JsonSerializable]` spread over partial declarations (`CS8785`, duplicate hint name `DesktopJsonContext.Boolean.g.cs`) and NeoAstra takes one context: reopen when NeoAstra accepts several. The demo stubs go through one helper (`98fb7800a`) | `2026-11-22-health` F07 |
| 2026-11-22 | F08 Remove the early return that drops credential policies | done (`2ec6b3406`) | A host with the broker alone lists 7 credential policies, up from 0; the contributor was not split | `2026-11-22-health` F08 |
| 2026-11-22 | F09 Delete the raw skill-scanning code | done (`cf1a8fd56`, `f6ef02003`) | 10 files, 1,878 lines, 25 tests removed | `2026-11-22-health` F09 |
| 2026-11-22 | F10 Delete the desktop start without a host | done (`d090089dd`..`5d53c5bda`) | Explicit roots require `--allow-owned-host`; 102 lines and 55 registrations gone; the old form exits with code 2 before writing | `2026-11-22-health` F10 |
| 2026-11-22 | F11 Move the state of features out of `App` | done (`3bb1ead88`..`ff211b621`, `d61602f01`..`a2fb148a0`) | Typing in the two renames and the Settings search: 1 render of `App` per key to 0; the sidebar filter: 0 while typing, 1 after 200 ms; `useState` in `App` 134 to 117, the dialogs' state in their feature folders. A render of `App` at each tab switch stays with task `wtvm` | `2026-11-22-health` F11 |
| 2026-11-22 | F12 Give every test deadline the CI factor, replace fixed sleeps | done (`6dd1e7a3f`, `3ace59e32`, `bce11c85e`) | 36 unscaled deadlines to 0; 45 fixed delays to 0; the three browser files 181 s to 144-158 s | `2026-11-22-health` F12 |
| 2026-11-22 | F13 Keep the tracked files in the fallback review snapshot | done (`87e802f5e`, `9aa74dde9`) | Both attempts give the same tree, during a merge conflict too; failures keep their stage and reason | `2026-11-22-health` F13 |
| 2026-11-22 | F14 Run the search kernel test on the platform runners | done (`18725408f`) | The test is in the filter of the platform jobs; the ARM kernel is checked on the next macOS run | `2026-11-22-health` F14 |
| 2026-11-22 | F15 Cut the plugin-runtime skill | done (`b3b22a965`, `67bba2f38`) | 8,958 to 1,335 words; a reader test answered 8 of 8 questions | `2026-11-22-health` F15 |
| 2026-11-22 | F16 Move the desktop-window guide out of `AGENTS.md` | done (`7b69770ee`, `f2fa6fc39`, `1fc10d0e8`) | 3,041 to 830 words; the procedure is in `src/CodeAlta/README.md` | `2026-11-22-health` F16 |

## Baselines

| Date | Commit | Measure | Value | How it was measured |
| --- | --- | --- | --- | --- |
| 2026-11-22 | `2a3fd8388` | Callers outside the store of `SaveGlobalProviderDefinitions`, none through `UpdateGlobal` | 13 | `git grep -n 'SaveGlobalProviderDefinitions(' -- 'src/*.cs' ':!src/*Tests*'`, without the store's declaration |
| 2026-11-22 | `2a3fd8388` | `useState` in the `App` component of `main.tsx` (lines 550 to 7291) | 134 | `Select-String 'const \[(\w+), set\w+\] = useState'` within the lines of `App` |
| 2026-11-22 | `2a3fd8388` | Commits that changed `main.tsx` in the last four weeks | 144 of 1,702 | `signals/signals.json` of `2026-11-22-health` |
| 2026-11-22 | `2a3fd8388` | Hand-written column migrations of the session list | 9 | `Add…ColumnAsync` methods of `SessionJournalSqliteCache.cs` |
| 2026-11-22 | `2a3fd8388` | Words of the plugin-runtime skill | 8,958 | word count of `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md` |
| 2026-11-22 | `2a3fd8388` | Words of `AGENTS.md` | 3,041 | word count of `AGENTS.md` |
| 2026-11-22 | `2a3fd8388` | Fixed delays in three browser test files | 45, 12.7 s | `Task.Delay(<ms>)` of `BrowserAnnotationsTests.cs`, `BrowserDrawingTests.cs`, `BrowserPluginTests.cs`, read site by site (16 more are pauses of polling loops) |
| 2026-11-22 | `2a3fd8388` | Unscaled deadlines in tests | 36 | `git grep -E 'UtcNow\.Add(Seconds\|Milliseconds\|Minutes)\('` in the test projects, without the lines marked `// not a limit` |
| 2026-11-22 | `2a3fd8388` | Runs of `main` that passed only when run again | 10 of 170 | `gh run list --limit 300`, 2026-11-07 to 2026-11-22, `signals/ci-runs.md` |
| 2026-11-22 | `5d53c5bda` | Callers outside the store of `SaveGlobalProviderDefinitions` | 0 | as above |
| 2026-11-22 | `a2fb148a0` | `useState` in the `App` component of `main.tsx` (lines 555 to 7205) | 117 | as above |
| 2026-11-22 | `5d53c5bda` | Hand-written column migrations of the session list | 0 | as above |
| 2026-11-22 | `5d53c5bda` | Words of the plugin-runtime skill | 1,335 | as above |
| 2026-11-22 | `5d53c5bda` | Words of `AGENTS.md` | 830 | as above |
| 2026-11-22 | `5d53c5bda` | Fixed delays in three browser test files | 0 (16 polling pauses) | as above |
| 2026-11-22 | `5d53c5bda` | Unscaled deadlines in tests | 0 | as above |
