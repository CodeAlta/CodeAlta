# SWE-bench Pro v2 HARD-51: CodeAlta against OpenCode on DeepSeek V4.1 Flash

2026-11-07. The 51 tasks of HARD-51 run with the [reproduction kit](swe-bench-pro/README.md) (Harbor 0.24.0, locked
network, a 3,000 s agent budget), after a user reported that CodeAlta did much worse than OpenCode with DeepSeek V4.1
Flash. OpenCode ran once. CodeAlta ran first at `1179e0e6f`, then, after the fixes this run led to, at `22b292217` with
Code Mode on and with Code Mode off.

| | CodeAlta | OpenCode |
|---|---|---|
| Harness | `altacli`, `openai-chat` provider, no sub-agents | OpenCode 1.19.5, Harbor's `opencode` agent, no sub-agents (`task` denied) |
| Model | `deepseek-flash` (DeepSeek-V4.1-Flash), DeepSeek API | the same |
| Request | thinking on, effort `high`, reasoning of earlier turns replayed in `reasoning_content`, `max_tokens` 32,000, no web tools | the same: `@ai-sdk/openai-compatible` with interleaved `reasoning_content` |

OpenCode's own `@ai-sdk/deepseek` provider package was not used: in an earlier check it sent every earlier turn back with
an empty `reasoning_content`, which made the model explore again and again.

## Results

| | CodeAlta `1179e0e6f` | CodeAlta `22b292217`, Code Mode on | CodeAlta `22b292217`, Code Mode off | OpenCode |
|---|---:|---:|---:|---:|
| Resolved tasks | **47/51** | **47/51** | 46/51 | 46/51 |
| Required tests passed | **3,826** / 3,852 | 3,808 / 3,852 | **3,826** / 3,852 | **3,826** / 3,852 |
| Errored trials, failed runs | 0, 0 | 0, 0 | 0, 0 | 0, 0 |
| Requests | 2,636 | 2,398 | 2,591 | 2,320 |
| Input tokens (cache read) | 186.0M (97.7%) | 160.1M (98.3%) | 168.5M (98.3%) | 138.5M (98.2%) |
| Input not read from the cache | 4.33M | 2.79M | 2.81M | 2.45M |
| Output tokens, reasoning included | 2.33M | 2.24M | 2.12M | 1.83M |
| of which reasoning | 1.66M | 1.60M | 1.47M | 1.31M |
| Estimated cost | $2.59 | $2.24 | $2.19 | **$1.87** |
| Agent time: total, median, P90 | 7.39 h, 8.4 min, 14.1 min | 8.18 h, 8.2 min, 15.0 min | 7.75 h, 6.7 min, 14.7 min | 6.48 h, 6.4 min, 13.5 min |

- **Scores:** the four runs solve the same 42 tasks. The others: ansible `de5858f4` (Code Mode on), flipt `c1728053`
  (all but the first run), teleport `7744f72c` and protonmail `e65cc5f3` (the first run and Code Mode on), openlibrary
  `6e889f4a` (Code Mode on and OpenCode), protonmail `715dbd4e` and `c5a2089c` (all but Code Mode on), qutebrowser
  `c09e1439` (the first run and Code Mode off); none solves openlibrary `b67138b3`. One or two tasks is the run-to-run
  variation of HARD-51: the scores are a tie.
- **Required tests:** a task is resolved when all its required tests pass. Three runs each miss 26 of the 3,852;
  Code Mode on misses 44, for its four unsolved tasks: openlibrary `b67138b3` (30 of 64 tests, against 50 for the
  others), protonmail `715dbd4e` (1 of 4) and `c5a2089c` (7 of 13), and qutebrowser `c09e1439` (104 of 105, as
  OpenCode). The near misses of the other runs: openlibrary `6e889f4a` (16 of 17) and ansible `de5858f4` (49 of 58).
- **The fixes cost 13–15% less** at the same score, nearly all of it from the input not read from the cache (−35%):
  see [the reasoning sent back](#the-reasoning-sent-back).
- **Against OpenCode, CodeAlta still costs 17–20% more**, for 16–22% more output: see
  [where CodeAlta spends more](#where-codealta-spends-more).
- **Reliability:** no CodeAlta trial failed or was run again in the three runs. In the two runs of `22b292217`, DeepSeek's
  stream was cut 6 times and ended without an answer 8 times (in 11 trials, the hours after an outage of DeepSeek): each
  time the request was sent again, and the task went on. The first run saw neither.
- **Code Mode:** on solves one task more than off for 2% more cost. It stays on for DeepSeek Flash.

The first two jobs ran 8 sandboxes at a time, one after the other; the two jobs of `22b292217` ran at the same time, 4
sandboxes each, a few hours later. The times of the three CodeAlta runs are not comparable to better than DeepSeek's
service speed of the moment.

Costs are estimated at the list prices of DeepSeek V4.1 Flash (per million tokens: $0.15 input, $0.003 cache read,
$0.60 output); OpenCode's own figure, $1.87, matches. DeepSeek bills reasoning as output: Harbor's `opencode` agent
counts its output without the reasoning tokens (0.52M), which `summarize-results.py` and this report add back.

## The reasoning sent back

DeepSeek reads a request from its cache up to the first byte that differs from an earlier request. In the first run,
only 12% of what each request added to the previous one was read from the cache, against 39% for OpenCode: the turn the
model had just written came back different. `OpenAIChatTurnExecutor` dropped the chunks of reasoning made of a space or a
newline alone, so the reasoning it sent back was not the one the model wrote (526 of the 527 differences found in the
traces). Fixed in `fa7af14d9`, with the same audit of the other turn executors; with the fix, 41–44% of what a request
adds is read from the cache, and the input not read from it is that of OpenCode within 15%.

`22b292217` also asks again when a stream ends without an error before its answer: after reasoning only, in the middle of
a tool call, or empty, which ended the turn before. It did 8 times in the runs of `22b292217`.

## Where CodeAlta spends more

Per task, on the 51 tasks:

| | CodeAlta, Code Mode on | CodeAlta, Code Mode off | OpenCode |
|---|---:|---:|---:|
| Requests | 47.0 | 50.8 | 45.5 |
| Reasoning per request | 667 | 568 | 564 |
| Tasks with edits of test files | 37 | 34 | 9 |
| Characters edited in test files | 5.9k | 6.0k | 0.7k |
| Characters edited in other files | 15.1k | 15.6k | 11.6k |
| Test and build runs | 8.6 | 9.0 | 6.8 |
| Characters of tool results | 151k | 146k | 143k |

- **Tests.** CodeAlta adds or changes tests in two thirds of the tasks, as its prompt asks, OpenCode in one in six: about
  1.5k output tokens more per task, and more requests to run them.
- **Larger edits of the code** (+30–35% characters), and twice as long a final summary (1.1k characters against 0.55k).
- **Not the reasoning of a step.** With Code Mode off, CodeAlta reasons as much per request as OpenCode; the extra
  reasoning is that of its extra requests. With Code Mode on, a request reasons 18% more, and there are 7% fewer than with it off.
- **Not the reading.** Both read about as much: 146–151k characters of tool results per task against 143k.
- **The context of a request** is 9–12% larger than OpenCode's (65–67k tokens against 60k): CodeAlta's system prompt and
  tool descriptions are about 1k tokens longer, and the tests it writes and runs stay in context.

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

- One attempt per configuration. The other limits of [the earlier report](2026-10-28-swe-bench-pro-v2-hard51-final.md)
  apply.
- **OpenCode's score is not a pristine replay.** Harbor's `opencode` agent captured no patch when it ran, so its replay
  job had nothing to apply (it reports 0/51, which is not a score). Both harnesses are compared on the verifier of each
  trial's own sandbox; for each CodeAlta run it gives the same tasks as its pristine replay. The kit now runs OpenCode
  through `captured_opencode.py`, which captures its patch: a check on NodeBB `8168c6c4` replayed it to the same reward.
- Without OpenCode's patches, the size of its changes is measured from its edits, not from a diff.
- OpenCode is not pinned in the image like the locked adapters: Harbor installs it from npm during setup, at the pinned
  version.
- The jobs ran at different times: DeepSeek's service speed may have differed.

## Reproduce

```powershell
cd benchmarks\swe-bench-pro
pwsh -File .\scripts\setup.ps1 -CodeAltaRevision 22b292217
$env:DEEPSEEK_API_KEY = "<key>"   # in the terminal only
pwsh -File .\scripts\run-hard51.ps1 -ConfirmPaidRun -AllowErroredTrials -Runs codealta-deepseek-flash,opencode-deepseek-flash -RunId <run-id>
# Code Mode off: -Runs codealta-deepseek-flash -CodeAltaArgs "--code-mode=off"
```
