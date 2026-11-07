# SWE-bench Pro v2 HARD-51: CodeAlta against OpenCode on DeepSeek V4.1 Flash

2026-11-07. The 51 tasks of HARD-51 run with the [reproduction kit](swe-bench-pro/README.md) (Harbor 0.24.0, locked
network, 8 sandboxes at a time, a 3,000 s agent budget), after a user reported that CodeAlta did much worse than OpenCode
with DeepSeek V4.1 Flash. Each harness ran once, the same day, CodeAlta first.

| | CodeAlta | OpenCode |
|---|---|---|
| Harness | `altacli` at `1179e0e6f`, `openai-chat` provider, Code Mode on, no sub-agents | OpenCode 1.19.5, Harbor's `opencode` agent, no sub-agents (`task` denied) |
| Model | `deepseek-flash` (DeepSeek-V4.1-Flash), DeepSeek API | the same |
| Request | thinking on, effort `high`, reasoning of earlier turns replayed in `reasoning_content`, `max_tokens` 32,000, no web tools | the same: `@ai-sdk/openai-compatible` with interleaved `reasoning_content` |

OpenCode's own `@ai-sdk/deepseek` provider package was not used: in an earlier check it sent every earlier turn back with
an empty `reasoning_content`, which made the model explore again and again.

## Results

| | CodeAlta | OpenCode |
|---|---:|---:|
| Resolved tasks | **47/51** | 46/51 |
| Errored trials, failed runs | 0, 0 | 0, 0 |
| Requests | 2,636 | 2,320 |
| Input tokens (cache read) | 186.0M (97.7%) | 138.5M (98.2%) |
| Output tokens, reasoning included | 2.33M | 1.83M |
| of which reasoning | 1.66M | 1.31M |
| Estimated cost | $2.59 | **$1.87** |
| Agent time: total, median, P90 | 7.39 h, 8.4 min, 14.1 min | 6.48 h, 6.4 min, 13.5 min |

- **Scores:** both solve 44 tasks; CodeAlta alone teleport `7744f72c`, protonmail `e65cc5f3` and qutebrowser `c09e1439`;
  OpenCode alone flipt `c1728053` and openlibrary `6e889f4a`; neither ansible `de5858f4` and openlibrary `b67138b3`.
  One or two tasks is the run-to-run variation of HARD-51: the scores are a tie.
- **Cost and time:** on the 44 tasks both solved, CodeAlta uses 1.28× the output tokens, 1.37× the input tokens and
  1.14× the time of OpenCode, for 15% more requests. It used less output than OpenCode on 5 of them and less time on 14.
- **Reliability:** no CodeAlta trial failed or needed a retry.

Costs are estimated at the list prices of DeepSeek V4.1 Flash (per million tokens: $0.15 input, $0.003 cache read,
$0.60 output); OpenCode's own figure, $1.87, matches. DeepSeek bills reasoning as output: Harbor's `opencode` agent
counts its output without the reasoning tokens (0.52M), which `summarize-results.py` and this report add back.

## Where CodeAlta spends more

On the 44 tasks both solved:

| Per request | CodeAlta | OpenCode |
|---|---:|---:|
| Input | 69.8k | 58.7k |
| Output | 865 | 778 |
| of which reasoning | 613 | 552 |

- **Larger context per request (+19%).** CodeAlta's system prompt and tool descriptions are about 1k tokens longer than
  OpenCode's; the rest is the tool output it keeps in context: larger reads and command outputs, and the results of its
  scripts.
- **More reasoning in all (+28%)** and more requests (+15%).
- **Code Mode is a small part of it here:** CodeAlta made 1,469 `exec` calls (a quarter of its tool calls). The 11
  tasks with the most `exec` calls use 1.37× OpenCode's output, the 11 with the fewest (most without any) 1.25×.

## The same task, many times

Before this run, one task of HARD-51 (NodeBB `8168c6c4`) ran about 50 times with a runner of its own: CodeAlta against
OpenCode on DeepSeek Flash, then CodeAlta alone with prompt variants on DeepSeek Flash, GPT-6.1 Sol and Claude Sonnet 5.5
(the results are not kept in the repository: `swe-bench-pro/results/` is ignored). What it found:

- The first CodeAlta attempt failed on a response that DeepSeek's stream cut after HTTP 200: `openai-chat` turns did not
  retry a stream cut in the middle. Fixed in `c5a14e0a4` (five more requests over half a minute, the cut draft
  discarded), and seen working live in a later run.
- A DeepSeek run failed after 7 minutes of correct work because the model called `replace_file_or_dir`, a tool it was
  not given: the session ended the run. Fixed in `1179e0e6f`: the call gets an error naming the tools it has.
- Two prompt changes (read whole regions instead of slices, place new tests with existing ones, keep long content out of
  scripts) were tested against the current prompt, 4 attempts per model: each cost GPT and Sonnet 18–35% more, or made
  Sonnet miss a required behavior. The prompt was kept.
- With the current prompt, DeepSeek Flash did better with Code Mode on than off on that task ($0.048 against $0.057
  per attempt, 36k against 44k output tokens), so Code Mode stays on for it.

## Limits

- One attempt per harness. The other limits of [the earlier report](2026-10-28-swe-bench-pro-v2-hard51-final.md) apply.
- **OpenCode's score is not a pristine replay.** Harbor's `opencode` agent captures no patch, so its replay job had
  nothing to apply (it reports 0/51, which is not a score). Both harnesses are compared on the verifier of each trial's
  own sandbox; for CodeAlta it gives the same 47 tasks as its pristine replay.
- OpenCode is not pinned in the image like the locked adapters: Harbor installs it from npm during setup, at the pinned
  version.
- The two jobs ran one after the other: DeepSeek's service speed may have differed.

## Reproduce

```powershell
cd benchmarks\swe-bench-pro
pwsh -File .\scripts\setup.ps1 -CodeAltaRevision 1179e0e6f
$env:DEEPSEEK_API_KEY = "<key>"   # in the terminal only
pwsh -File .\scripts\run-hard51.ps1 -ConfirmPaidRun -AllowErroredTrials -Runs codealta-deepseek-flash,opencode-deepseek-flash -RunId <run-id>
```
