# Plugin authoring experiments — 2026-10-11

## Question and controls

Which general improvements help an agent build CodeAlta plugins, independently of the plugin's domain? Retain candidates only when a benchmark shows a concrete benefit. This is a small, sequential engineering experiment, not a statistically powered model comparison.

- Implementation stays on `main`. No implementation sub-agents.
- Fresh, independent experiment sessions in the Release **developer** desktop, using `codex:gpt-6.1-sol@high` (GPT 6.1 High).
- Disposable project and project-scoped plugins; no changes to shared providers, prompts or settings. Normal `--dev` shares the user's configuration and credentials. It is **not** a profile sandbox; the explicit-root host currently disables plugins and cannot run this experiment.
- Within a comparison, the prompt is identical except for the plugin identifier. No access to the CodeAlta source checkout, other experiment plugins or previous experiment conversations. Shipped skills, samples, API lookup and installed assets are allowed.
- Record wall time, tool calls, API-discovery detours, build/runtime corrections and correctness. Provider aggregate input tokens include repeated context: they are not unique context size or billing cost.
- Inspect delivered files and exercise the C#/JS boundary independently. A polished screenshot or an agent's success claim alone is insufficient.
- Warm caches, service load, model variation and experiment order affect timing. Report those limitations; do not attribute every elapsed-second difference to the candidate.

## Original session

Session `01a12c86-a9a0-732d-9876-9e9608fb4973` used Claude Opus at high effort, not the benchmark model. Its first creation turn ran from 19:53:05.653 to 20:06:21.191 UTC: **795.538 seconds (13m 15.5s), 93 tool calls**. Its later retrospective turn is excluded; session-wide elapsed time includes the idle gap and would be misleading.

Confirmed from the requested-tool journal and delivered artifact/retrospective:

- C# type lookup, creation and reload were available and useful.
- JavaScript names such as `CalendarHeatmap` were not covered by `plugin api`. The agent inspected minified shipped JS repeatedly for props, hooks, callbacks and host methods, then CSS for theme/layout details.
- Canvas context exposed a project ID, not a project directory; the plugin used an `alta project show` JSONL round trip.
- A full-pane layout required inspecting host ancestors and overriding the document-like canvas wrapper. This couples a plugin to private desktop DOM.
- Whole-window UI snapshots were noisy for a dense canvas. The agent switched to targeted DOM evaluation and screenshots.
- Process execution, per-tab persistent view state and bounded fan-out were additional friction points reported in the retrospective. These are hypotheses for future experiments, not automatically justified API additions.

The original artifact's reported >8-call "expand all" issue is domain-specific and is not a benchmark outcome or a harness fix in this experiment.

## Experiment 1: script API discoverability

The task is a small lab-readings dashboard, not a commits browser:

1. C# owns four dated observations with values 2, 4, 1, 3 and serves read/update RPCs.
2. C# rejects values outside 0..10 with a coded error.
3. Plain JS uses the host `CalendarHeatmap`, `StatTile`, `Code` and theme, plus an HTML editing form.
4. Heatmap selection updates the form. Changing the second value to 6 changes the total from 10 to 12. Submitting 11 shows the server error without mutating data.
5. Build, reload, open, exercise interactions, take a screenshot and inspect the console.

**Candidate:** a linked script API quick reference in the bundled plugin skill: exact component props and callback shapes, hook results, signal-vs-value distinction, host methods and RPC usage/limits. No runtime API change.

| Measurement | Baseline | With script reference |
| --- | ---: | ---: |
| Wall time | 286.083 s | 274.424 s |
| Tool calls | 44 | 37 |
| Shipped JS asset-inspection calls | 7 | 0 |
| Aggregate input tokens | 1,720,820 | 1,485,508 |
| Cached input tokens | 1,593,600 | 1,394,176 |
| Output tokens | 6,238 | 6,248 |

Baseline session: `01a12ccc-70fb-7a01-a071-09a7e5598ab7`.
Candidate session: `01a12cd6-bee7-7b44-9367-516eb803d672`, completed at 21:25:18.967 UTC.
Metrics cover the single creation turn, including the agent's verification and final answer, not subsequent independent checks.

**Discovery evidence.** The baseline tried `plugin api CalendarHeatmap` (the C# lookup), then made seven calls listing, reading or searching installed JS assets for component props, callbacks and theme contracts. The candidate read `references/script-api.md` once and made no asset-inspection calls. It still used C# API lookup for `PluginCanvasContribution`. Both arms initially tried an incorrect qualified canvas reference, then recovered with the unambiguous short name. Both built and reloaded on the first attempt; neither needed a C#/JS repair. The candidate also spent two calls recording the shared heatmap cosmetic problem as a follow-up; totals include them and are not normalized.

**Independent correctness.** Reviewed both C# and JS artifacts, then exercised each canvas in the dev window: reset total 10; select October 3 in the heatmap and observe date/input 1; set October 2 to 6 and observe total 12, heatmap label 6 and JSON 6; submit 11 and observe a coded C# validation error with saved state unchanged. Both consoles had no warnings/errors. Screenshots were inspected. Loading and read-error states exist in the implementations but were not separately fault-injected. Both share a clipped month label for a one-week heatmap; this is recorded separately, not treated as a candidate regression or fixed in the experiment.

**Decision: retain the reference.** Seven fewer tool calls (**15.9%**) and removal of the observed bundle reverse-engineering demonstrate a useful discovery improvement in this pair. The wall-time decrease is only **4.1%** (11.660 seconds). A full frontend test run overlapped the early candidate run, but not the baseline; model/service variability, caches and fixed ordering are further confounders. This is not evidence for a general 4.1% speedup. Aggregate input tokens fell 13.7%, but cache differences prevent translating that directly into cost savings.

The reference was checked against the frontend's exported types and implementations; its shipped copy matched the source used for the candidate. No runtime API or component behavior changed.

Verification before retaining the change: Release desktop build passed; full .NET solution tests passed (with the suite's reported skips), including the TUI build; all 1,395 frontend tests passed without skips; the Lunet website build and `git diff --check` passed. Final reference review clarified that navigation methods are called on `alta.host`.

## Experiment 2: C# project context and HTML

Next, test a separate project-scoped fixture report: C# reads `metrics.json` for the canvas's project (not the selected project or plugin directory), exposes the same data through detached `Describe`, and renders escaped HTML with Refresh and readable file/JSON errors. Two fixture projects have distinct values; one note contains literal `<b>not bold</b> & safe`. The baseline must finish before any context API candidate is implemented or shipped. This tests project resolution and C#/HTML authoring rather than the script component reference.

Local experiment prompts, tool indexes and screenshots are under `C:/temp/codealta-plugin-bench-1c8344`; this report records the portable results. Raw conversation reasoning and credentials are not checked in.
