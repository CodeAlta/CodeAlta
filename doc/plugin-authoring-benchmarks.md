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

The second task is a separate project-scoped fixture report: C# reads `metrics.json` for the canvas's project (not the selected project or plugin directory), exposes the same data through detached `Describe`, and renders escaped HTML with Refresh and readable file/JSON errors. Two fixture projects have distinct values; one note contains literal `<b>not bold</b> & safe`. The baseline finished before any context API candidate was implemented or shipped. This tests project resolution and C#/HTML authoring rather than the script component reference.

Baseline session `01a12ce6-3bd7-745d-bfd2-a32a203b9204` completed in **319.183 seconds, 56 tool calls**, with 2,168,502 aggregate input tokens (2,070,784 cached) and 6,691 output tokens. First reload succeeded. Eight context/catalog discovery calls included `PluginCanvasContext`, the guessed `IPluginWorkspace`, `Project`, `IPluginServices`, project command/schema exploration and `IPluginAltaService`. The implementation resolves the canvas project through `Services.Alta.InvokeAsync(["project", "show", canvas.ProjectId])`, scans its JSONL records and extracts `projectPath`: correct, but unnecessary authoring work for a common operation.

Independent checks passed: detached Describe for both projects, literal HTML-looking note without a `<b>` element, Refresh after changing the driver's disposable fixture, inline missing-file and invalid-JSON errors, and recovery after restoring it. The other project retained its own data throughout. Both fixture hashes were restored and the browser console was clean.

**Prompt caveat:** this experiment mistakenly requests `canvas read`, but the supported command is `canvas show`. The baseline made three unsuccessful `read` calls (including help) before using `show`. It also corrected one qualified canvas reference and one disallowed UI snapshot path. The candidate received the same prompt; these differences are reported separately, not attributed to a project-path API.

**Candidate tested, not retained:** `PluginCanvasContext.GetProjectPathAsync` resolved the context's catalog project directly, including detached handlers. It returned null for no project or a missing catalog entry, never fell back to the selected UI project, and did not claim to return a session worktree. A short skill paragraph explained the method. No layout, process, persistence or JavaScript API changes were bundled with it.

| Measurement | Baseline | With project-path API |
| --- | ---: | ---: |
| Wall time | 319.183 s | 369.268 s |
| Tool calls | 56 | 57 |
| Aggregate input tokens | 2,168,502 | 1,689,129 |
| Cached input tokens | 2,070,784 | 1,589,888 |
| Output tokens | 6,691 | 8,306 |
| First plugin reload | Passed | Passed |

Candidate session: `01a12cf8-98fc-7038-96b3-c3fc9854f83a`, completed at 22:03:53.672 UTC.

**Discovery and correctness.** The candidate used the method directly and eliminated project-command discovery and JSONL parsing. However, it explored other canvas types: eight plugin API calls in total versus five in the baseline, which additionally made three project-command discovery calls. Both delivered correct plugins. Independent candidate checks repeated detached Describe for both projects, literal HTML, changed-file Refresh, missing/invalid JSON, recovery and fixture-hash restoration; all passed, with a clean console. The candidate also created standalone C# tests and corrected its test harness, work the baseline did not do. Its one failed `canvas read` call versus the baseline's three is a prompt-recovery difference, not evidence for the new API. Both hit a disallowed snapshot path once.

**Decision: remove the runtime candidate.** Aggregate input fell **22.1%** and the path lookup became simpler, but end-to-end time rose **15.7%** (50.085 seconds) and tool calls rose from 56 to 57. Extra testing and different exploration make the overall results mixed; this pair proves neither a general slowdown nor an end-to-end efficiency gain. Under the request to retain only demonstrated improvements, the runtime API, its tests and its skill/documentation additions were removed. The rejected patch remains with local experiment artifacts, not in the product. No API addition is justified solely because its implementation passed tests.

The candidate itself passed a red-then-green regression check, all 53 canvas tests, the full .NET solution suite and the website build before the benchmark. Neither arm overlapped other build/test jobs. The provider's reported context-window limit changed from 272,000 to 500,000 between arms despite the same model/effort; actual contexts were well below either limit, but this is another limitation of a single pair.

## Outcome

Retained: the bundled JavaScript contract reference, with **seven fewer tool calls and no bundle-inspection detours** in its paired dashboard run. Rejected: the project-path runtime API, pending stronger end-to-end evidence. These outcomes are not a measured speedup of the original 13-minute session: its task and model differ. No aggregate percentage across the two different challenges is meaningful.

The original session also exposed private-DOM layout workarounds and reported process/state/concurrency friction. Those were not benchmarked and no speculative APIs were added. The shared short-range heatmap month-label clipping was recorded as a separate deferred task. Final retained changes are documentation/skill content only; existing runtime behavior is unchanged. After removing the candidate, the source matched the previously verified retained state and the Release desktop rebuilt with zero warnings/errors. The task-owned developer instance was stopped; the user's normal instance was left running.

Local experiment prompts, tool indexes and screenshots are under `C:/temp/codealta-plugin-bench-1c8344`; this report records the portable results. Raw conversation reasoning and credentials are not checked in.
