# SWE-bench Pro v2 HARD-51: CodeAlta against Claude Code and Codex

2026-10-28. The 51 tasks of HARD-51, a hard subset of [SWE-bench Pro](https://github.com/scaleapi/SWE-bench_Pro-os) v2, run under its locked protocol with [Harbor](https://github.com/laude-institute/harbor) 0.24.0, on one Windows machine (Docker on WSL 2, 32 CPUs, 8 sandboxes at a time). Each model job is followed by a pristine replay of its captured patches, which gives the score. The reproduction kit is in [`swe-bench-pro/`](swe-bench-pro/README.md).

| | CodeAlta / Opus | Claude Code / Opus | CodeAlta / Astra | Codex / Astra |
|---|---|---|---|---|
| Harness | CodeAlta `altacli` 2.2.2-alpha.0.45 (commit `bbd32c77`) | Claude Code 2.1.310 | CodeAlta `altacli` 2.2.2-alpha.0.45 (commit `bbd32c77`) | Codex CLI 0.155.1 |
| Model | Claude Opus 5.5, `xhigh` | Claude Opus 5.5, `xhigh` | GPT-6 Astra, `xhigh` | GPT-6 Astra, `xhigh` |
| Endpoint | Anthropic API | Anthropic API | OpenAI API | OpenAI API |
| Run | 2026-10-28 | 2026-10-26 (reference) | 2026-10-28 | 2026-10-26 (reference) |

CodeAlta ran with Code Mode on (its default) and without sub-agents (`--no-sub-sessions`, the kit's default): one model in one session, as the native harnesses.

## Results

| | CodeAlta / Opus | Claude Code / Opus | CodeAlta / Astra | Codex / Astra |
|---|---:|---:|---:|---:|
| Resolved tasks | **50/51** | **50/51** | **47/51** | **46/51** |
| Score | 98.04% | 98.04% | 92.16% | 90.20% |
| Required tests passed | 3,848/3,852 | 3,848/3,852 | 3,845/3,852 | 3,813/3,852 |
| Estimated cost | **$76.30** | $86.75 | **$136.33** | $131.22 |
| Input tokens | 96.96M | 117.93M | 51.69M | 50.66M |
| Cache writes | 4.51M | 5.26M | 3.36M | 3.71M |
| Output tokens | 1.76M | 1.90M | 0.92M | 0.76M |
| Cache-read ratio | 95.35% | 95.54% | 93.49% | 92.68% |
| Agent runtime, median | 8.6 min | 9.3 min | 6.7 min | 5.1 min |
| Agent runtime, P90 | 19.4 min | 21.3 min | 10.6 min | 8.5 min |
| Total agent-hours | 8.44 h | 9.65 h | 5.94 h | 4.64 h |
| Model-job wall time | 78.9 min | 102.9 min | 57.8 min | 46.2 min |

- **Opus:** CodeAlta solves the same 50 tasks as Claude Code (50 both, 1 neither), for 12% less: 18% fewer input tokens, 7% fewer output tokens, and a little less time per task.
- **Astra:** CodeAlta solves one task more than Codex (45 both, 2 CodeAlta only, 1 Codex only, 3 neither), for 4% more, with about the same input and 21% more output; a task takes 1.5 minutes longer at the median.
- Costs are estimated from the tokens at the same list prices for every run; they reproduce Claude Code's own `total_cost_usd` and Harbor's Codex `cost_usd`. The provider's billing is the final word.

## Before and after the changes of 2026-10-28

The same benchmark, CodeAlta only, before the changes (commit `bbd32c77` has them all):

| | Reference (10-26) | Code Mode off (10-27) | Code Mode on (10-27) | **Final (10-28)** |
|---|---:|---:|---:|---:|
| Opus: resolved | 49/51 | 48/51 | 50/51 | **50/51** |
| Opus: estimated cost | $96.36 | $91.84 | $96.80 | **$76.30** |
| Opus: input tokens | 138.58M | 135.42M | 143.24M | **96.96M** |
| Astra: resolved | 47/51 | 48/51 | 49/51 | **47/51** |
| Astra: estimated cost | $182.54 | $207.27 | $221.71 | **$136.33** |
| Astra: input tokens | 76.73M | 88.24M | 87.59M | **51.69M** |
| Errored trials (both models) | 0 | 0 | 8 | **1** |

Against the previous Code Mode round, the cost falls by 21% with Opus and by 38% with Astra, for the same scores within the variance of these runs (one or two tasks). The earlier rounds ran with sub-agents allowed: Astra delegated in almost every task, which explains part of its cost then.

What changed, all in `main` before the run:

- **Guidance of the agent:** sub-agents framed by purpose and size (delegate a large independent part, keep what you will need the details of), several independent reads, and several decided edits, in one `exec` script; what a script returns kept short.
- **Code Mode tools:** `exec` and `apply_patch` declared as freeform tools on OpenAI models (the model sends the script or the patch as text, not escaped in JSON); `wait` no longer polls in steps of a second; a long result says which of its lines it shows, and a JSON result keeps its long texts in files that read by line.
- **Output budgets:** 10,000 tokens for a script result, 40,000 characters for a file read, a guard against reading again a range that has not changed.
- **OpenAI requests:** a 272,000-token window and compaction at 90% for GPT-6 models, retries of connection failures and of streams cut before their end, no reasoning summaries in a headless run.
- **`apply_patch`:** every operation verified before anything is written, line endings and byte-order marks kept, the patch grammar and the matching of Codex.
- **Headless runs:** the answer and the usage of a Code Mode run no longer lost ("The run ended without an answer": the 8 errored trials of the previous Code Mode round, all with Astra), a run completed by a later run no longer reported failed, every request counted in the usage, the run's deadline told to the agent and the timeouts of its jobs capped to it.

## Errored trial

One CodeAlta / Astra trial (`gravitational/teleport`, `65438e6e`) ended on `Service request failed. Status: 520` from the OpenAI endpoint at its 14th request, after 4.7 minutes. Its edits were already made: the replay graded its patch, which solves the task. A 520 is a transient error of the Cloudflare edge in front of the endpoint; CodeAlta does not retry it yet.

## Tasks not resolved

| Task | CodeAlta / Opus | Claude Code / Opus | CodeAlta / Astra | Codex / Astra |
|---|:---:|:---:|:---:|:---:|
| `openlibrary` `7cbfb812` (4 tests of `test_observations_persistence.py`) | ✗ | ✗ | ✗ | ✗ |
| `ansible` `40ade1f8` (1 test of `test_mount_facts.py`) | | | ✗ | ✗ |
| `openlibrary` `0dc5b20f` (MARC parsing) | | | ✗ (1 test) | ✗ (18 tests) |
| `navidrome` `69e0a266` (`TestArtwork` missing) | | | ✗ | |
| `openlibrary` `b67138b3` (MARC parsing) | | | | ✗ |
| `protonmail/webclients` `e65cc5f3` (2 tests missing) | | | | ✗ |

## Limits

- One attempt per harness and model. Between attempts of the same harness, the score varied by one or two tasks: compare the costs and the patterns, not single tasks.
- The native harnesses ran on 2026-10-26, CodeAlta on 2026-10-28, on the same machine and benchmark commit; service speed and load differ between days.
- Harbor's token counts include cache reads; the estimated cost prices them as such.

## Reproduce

```powershell
cd benchmarks\swe-bench-pro
pwsh -File .\scripts\setup.ps1 -CodeAltaRevision bbd32c77
pwsh -File .\scripts\run-hard51.ps1 -ConfirmPaidRun -AllowErroredTrials -Runs codealta-opus,codealta-astra -RunId <run-id>
.\workspace\harbor-venv\Scripts\python.exe .\scripts\summarize-results.py --run-id <run-id> --baseline-run-id 20261026-162310 --allow-errored
```

`ANTHROPIC_API_KEY` and `OPENAI_API_KEY` must be set. The native jobs run with `-Runs claude-code-opus,codex-astra`; see the [kit](swe-bench-pro/README.md).
