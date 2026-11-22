# Health ledger

Decisions and numbers that outlive one report. Reports are in the folders beside this file.

## Decisions

| Date | Finding | Decision | Reason or result | Report |
| --- | --- | --- | --- | --- |
| 2026-11-22 | All 16 findings | kept for the plan | The owner reviews the report and sets the priorities from its order of work (#496) | `2026-11-22-health` |

## Baselines

| Date | Commit | Measure | Value | How it was measured |
| --- | --- | --- | --- | --- |
| 2026-11-22 | `2a3fd8388` | Callers outside the store of `SaveGlobalProviderDefinitions`, none through `UpdateGlobal` | 14 | `git grep -n 'SaveGlobalProviderDefinitions(' -- 'src/*.cs' ':!src/*Tests*'` |
| 2026-11-22 | `2a3fd8388` | `useState` in the `App` component of `main.tsx` (lines 550 to 7291) | 134 | `Select-String 'const \[(\w+), set\w+\] = useState'` within the lines of `App` |
| 2026-11-22 | `2a3fd8388` | Commits that changed `main.tsx` in the last four weeks | 144 of 1,702 | `signals/signals.json` of `2026-11-22-health` |
| 2026-11-22 | `2a3fd8388` | Hand-written column migrations of the session list | 9 | `Add…ColumnAsync` methods of `SessionJournalSqliteCache.cs` |
| 2026-11-22 | `2a3fd8388` | Words of the plugin-runtime skill | 8,958 | word count of `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md` |
| 2026-11-22 | `2a3fd8388` | Words of `AGENTS.md` | 3,041 | word count of `AGENTS.md` |
| 2026-11-22 | `2a3fd8388` | Fixed delays in three browser test files, outside polling loops | 39, 11.4 s | `Task.Delay(<ms>)` of `BrowserAnnotationsTests.cs`, `BrowserDrawingTests.cs`, `BrowserPluginTests.cs` not within three lines after a `while`, `for` or `do` (22 more are pauses of polling loops) |
| 2026-11-22 | `2a3fd8388` | Runs of `main` that passed only when run again | 10 of 170 | `gh run list --limit 300`, 2026-11-07 to 2026-11-22, `signals/ci-runs.md` |
