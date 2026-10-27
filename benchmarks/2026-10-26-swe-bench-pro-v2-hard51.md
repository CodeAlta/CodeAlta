# SWE-bench Pro v2 (hard51): CodeAlta against Claude Code and Codex

Run of 2026-10-26, analysed on 2026-10-27. CodeAlta `2.0.1-alpha.0.113` (commit `b8dc1afb`), Claude Code `2.1.310`,
Codex CLI `0.155.1`. The 51 tasks of HARD-51, run with [Harbor](https://github.com/laude-institute/harbor) and the
[CodeAlta adapter](harbor/README.md).

## Summary

- **Same solve rate.**
  - Claude Opus 5.5: CodeAlta solves 49 of 51 tasks, Claude Code 50.
  - GPT-6 Astra: CodeAlta solves 47, Codex 46.
  - No failure is attributable to a harness. The failures are model-level design mistakes, plus one under-specified task that all four runs fail. In two CodeAlta / Astra failures a sub-agent the model had planned was refused, with an unknown effect.
- **CodeAlta spends more:**

  | Pair | Input tokens | Output tokens | Cost | Agent time |
  |---|---:|---:|---:|---:|
  | CodeAlta vs Claude Code, Opus | +17.5% | +7.1% | +11% | +3% |
  | CodeAlta vs Codex, Astra | +51% | +29% | +39% | +35% |
- **More requests, not larger ones.** CodeAlta makes 18% more model requests on Opus and 37% more on Astra. Each request is about the size of the native harness's: ×0.99 on Opus, ×1.11 on Astra.
- **On Astra, bookkeeping is the largest avoidable cause.** CodeAlta's default agent prompt asks for work that only matters to an interactive user: a scratchpad copy of the request, sticky notes, follow-up tasks and sub-agents.
  - Requests that did nothing but this bookkeeping carry 14% of Astra's input.
  - The scratchpad text alone is about half of the extra output against Codex.
  - On Opus, bookkeeping-only requests carry 4% of the input. There CodeAlta mostly takes more, smaller steps than Claude Code.
- **Not the causes:**
  - the size of the fixed prompt, which is smaller than Claude Code's and Codex's;
  - tool outputs per call, no larger than the native harnesses';
  - the long-context price, which no request reached;
  - compaction, retries and errors, which did not happen.
- **First fix:** give headless runs (`altacli run`) a prompt without the interactive workflow ([recommendations](#recommendations)).

## What ran

| Run | Harness | Model access | Command |
|---|---|---|---|
| CodeAlta / Opus | CodeAlta's own agent loop, `anthropic` provider | Anthropic Messages API, API key | `altacli run --ephemeral --ignore-user-config --offline --disable-tools web --no-plugins --no-mcp -m anthropic:claude-opus-5-5@xhigh` |
| Claude Code / Opus | Claude Code CLI | Anthropic API, API key | `claude --print --output-format=stream-json --effort xhigh --disallowedTools WebFetch,WebSearch --permission-mode=bypassPermissions` |
| CodeAlta / Astra | CodeAlta's own agent loop, `openai-responses` provider | `api.openai.com`, API key | `altacli run ... -m openai:gpt-6-astra@xhigh` |
| Codex / Astra | Codex CLI, code mode | `api.openai.com`, API key | `codex exec --dangerously-bypass-approvals-and-sandbox --model gpt-6-astra --json --enable unified_exec -c model_reasoning_effort=xhigh -c web_search=disabled` |

All four runs used the default agent prompt of their harness and the same Harbor settings: 8 trials at a time, a 3,000 s agent budget, and no network but the model's host. A replay of every captured patch on a pristine image gave the same result in all 204 trials.

**CodeAlta / Opus did not use the Claude Code CLI.** The adapter maps `-m anthropic/<model>` to CodeAlta's `anthropic` provider ([locked_codealta.py](harbor/locked_codealta.py#L49)), and the job passed no `provider_type`. Three facts show it in all 51 trials:

- Every `trial.log` shows `-c 'providers.anthropic.type="anthropic"'`.
- The adapter installs no `claude` CLI.
- The traces show only CodeAlta's own tools: `shell_command` 1,376, `replace_in_file` 342, `read_file` 202, `write_file` 85, `grep` 60, `alta` 46, `delete_file_or_dir` 3. CodeAlta never gives these tools to the Claude Code CLI, which keeps its own `Bash`, `Read` and `Edit`, and none of those appear.

So both pairs compare CodeAlta's own harness with a native one. Measuring CodeAlta on the Claude Code CLI needs `--ak provider_type=claude-code`, a `claude` CLI installed in the image during setup (the adapter has no step for it yet), and a model alias such as `opus`.

## Results

### Scores

| | CodeAlta / Opus | Claude Code / Opus | CodeAlta / Astra | Codex / Astra |
|---|---:|---:|---:|---:|
| Solved | **49/51** | **50/51** | **47/51** | **46/51** |

Tasks with at least one failure:

| Task | CodeAlta / Opus | Claude Code / Opus | CodeAlta / Astra | Codex / Astra |
|---|:---:|:---:|:---:|:---:|
| `element-web-66d0b318` | ✗ | ✓ | ✓ | ✓ |
| `ansible-c1f2df47` | ✓ | ✓ | ✗ | ✓ |
| `openlibrary-b67138b3` | ✓ | ✓ | ✓ | ✗ |
| `webclients-e65cc5f3` | ✓ | ✓ | ✓ | ✗ |
| `ansible-40ade1f8` | ✓ | ✓ | ✗ | ✗ |
| `openlibrary-0dc5b20f` | ✓ | ✓ | ✗ | ✗ |
| `openlibrary-7cbfb812` | ✗ | ✗ | ✗ | ✗ |

### Efficiency

| | CodeAlta / Opus | Claude Code / Opus | CodeAlta / Astra | Codex / Astra |
|---|---:|---:|---:|---:|
| Model requests | 1,788 | 1,513 | 1,335 | 978 |
| Requests per trial (median) | 33 | 26 | 25 | 19 |
| Input tokens | 138.6M | 117.9M | 76.7M | 50.7M |
| Cached share of input | 95.8% | 95.5% | 93.6% | 92.7% |
| Cache writes | 5.83M | 5.26M | 4.93M | 3.71M |
| Output tokens | 2.03M | 1.90M | 0.98M | 0.76M |
| Input per request | 77.5k | 77.9k | 57.5k | 51.8k |
| Tool calls | 2,114 | 1,607 | 3,167 | 1,888 (+927 `exec`/`wait`) |
| Tool calls per request | 1.18 | 1.06 | 2.37 | 1.93 (2.88 with `exec`/`wait`) |
| Test commands | 283 | 283 | 304 | 279 |
| Agent time | 596 min | 579 min | 376 min | 278 min |
| **Cost** | **$96.36** | **$86.75** | **$182.54** | **$131.22** |

- **Prices.**
  - Opus: $4 per million uncached input tokens, $0.20 cache read, $5 cache write, $20 output. These prices reproduce Claude Code's own `total_cost_usd` exactly in every trial.
  - GPT-6 Astra: $10, $1, $12.50 and $50. These reproduce Harbor's Codex `cost_usd` exactly in every trial.
  - CodeAlta reports no cost, so its cost is computed with the same prices.
  - Harbor shows $84.41 for Claude Code because it misses the restarted segments of two trials.
- **Where the extra cost goes:**
  - Opus +$9.60: cache reads +$4.01, cache writes +$2.88, output +$2.70.
  - Astra +$51.31: cache reads +$24.84, cache writes +$15.34, output +$11.11, uncached input +$0.02.
- **Codex's tool calls:** they are the calls made inside its `exec` scripts. The 927 `exec` and `wait` calls that carry them are counted apart.
- **Per task:**
  - Opus: CodeAlta's input is a median 1.22× Claude Code's, with quartiles 0.95 and 1.43. It is higher on 38 of 51 tasks, and one task, `tutanota-f373ac38`, holds 26% of the excess.
  - Astra: the median is 1.50× (quartiles 1.25 and 1.94), and CodeAlta is higher on 44 of 51 tasks. Output is higher on 48. This excess is systematic.

## Where CodeAlta's extra tokens come from

### More requests

| Pair | Input | = requests | × input per request |
|---|---:|---:|---:|
| CodeAlta vs Claude Code, Opus | ×1.175 | ×1.182 (more in 40 of 51 tasks) | ×0.994 |
| CodeAlta vs Codex, Astra | ×1.515 | ×1.365 (more in 46 of 51 tasks) | ×1.110 |

Every request resends the conversation, so the input of a run grows with the number of requests times the size of the context.

The fixed prompt is not what makes CodeAlta heavier. The first request is a median 13.3k tokens for CodeAlta against 18.5k for Claude Code. For GPT-6 Astra it is about 7.7k tokens (fitted, see [Method](#method-and-limits)) against Codex's 11.4k.

Most of CodeAlta's input is earlier content read again by every later request (estimated):

| | First prompt resent | Tool outputs read again | Model output read again |
|---|---:|---:|---:|
| Opus | 17.5% | 53% | 27% |
| Astra | 14% | 66% | 20% |

### Bookkeeping that the default agent prompt asks for

| In the 51 trials | CodeAlta / Opus | CodeAlta / Astra |
|---|---:|---:|
| Scratchpad copy of the request ([prompt](../src/CodeAlta.Orchestration/content/prompts/agents/default.prompt.md#L11)) | 21 writes in 21 trials | 141 writes in **51** trials, 59 reads |
| `alta task list` / `create` before the end ([prompt](../src/CodeAlta.Orchestration/content/prompts/agents/default.prompt.md#L38)) | 21 / 23, in 21 trials | 15 / 15, in 14 trials |
| `alta notes set` / `clear` ([prompt](../src/CodeAlta.Orchestration/content/prompts/agents/default.prompt.md#L15-L16)) | 0 / 0 | 11 / 46 |
| `alta --help` ([tool description](../src/CodeAlta.LiveTool/AltaSessionToolFactory.cs#L23)) | 0 | 51, in **51** trials |
| `alta session create` ([prompt](../src/CodeAlta.Orchestration/content/prompts/agents/default.prompt.md#L59)) | 0 | 55, **all refused**, plus 7 `--help` |
| Requests that did only bookkeeping | 57 (4.0% of input) | 191 (**14.4%** of input) |
| Bookkeeping-only requests, as a share of the extra requests against the native harness | 21% | 54% |
| `alta job start` ([system prompt](../src/CodeAlta.Orchestration/content/prompts/system/default.system-prompt.md#L30)), not counted as bookkeeping | 2 | 108 |

- **The output.** On Astra, the scratchpad text alone is about 114k output tokens (estimated), half of the 222k extra output tokens against Codex. The arguments of the other `alta` calls add about 13k.
- **The scratchpad.** It is triggered by the "many instructions at once" rule. SWE-bench Pro statements list requirements and interfaces, so GPT-6 Astra copies the whole task, several thousand characters, into the scratchpad at its first request ("User requirements (verbatim)").
- **The refused sub-agents.** Each attempt was refused with `session.widerTools`. The run disables the web tools, a child would have them, and the refusal's advice (`--prompt-id` with an agent prompt without them) has no answer in an `--ignore-user-config` run. Task `2026-10-27-let-a-session-that-does-without-tools-create-sub-agents-that-49fj` records it.
- **Jobs.** When a job ended after the final answer, it started a new run: 12 requests and about 1.0M input tokens in all (1 request in 1 Opus trial, 11 in 3 Astra trials). One of these runs repeated its answer. Claude Code has the same effect with background commands, in 2 trials.
- **Commits.** Neither CodeAlta run committed or created a branch. The git instructions of the prompt cost nothing but their size.

### The work itself

- **Opus: more, smaller steps.**
  - CodeAlta made 2,114 tool calls against Claude Code's 1,607, at the same output per call: 2.6k against 2.7k characters.
  - Both work mostly through the shell: `shell_command` is 65% of CodeAlta's calls, `Bash` 92% of Claude Code's.
  - Claude Code chains commands in one call and pipes 51% of them to `head` or `tail`. CodeAlta makes more calls and more rounds of parallel calls (299 against 141).
  - CodeAlta's Opus writes 3.2× more text between tool calls: 216k against 68k characters. The final answers are the same length (median 2,952 against 3,014 characters).
- **Astra: batching is not the gap.** Codex runs this model in code mode: one `exec` script per request, which makes 1.93 tool calls on average. CodeAlta's parallel tool calls give 2.37 per request. Output per call is 3.0k characters for CodeAlta and 4.4k for Codex, whose figure is inflated by 10–20% by the JSON escaping of its results.
- **The end of the run is expensive.**
  - After the last code edit, CodeAlta made 269 requests on Opus (19.8% of its input) and 338 on Astra (34.5%). These requests run at full context.
  - On Astra this is a median of 6 requests per trial against 4 for Codex (means 6.6 and 5.0).
  - That is where the scratchpad updates, `alta notes clear` and the follow-up tasks fall.
- **The heaviest trials read whole files early.** Everything later requests read again:
  - In `tutanota-f373ac38` (Opus: 115 requests, 17.85M input), the files read in the first requests were read again by about 112 requests, about 4.4M tokens.
  - One `grep` that matched lines of MARC test data returned about 87k characters, read again for about 1.2M tokens. CodeAlta's `grep` adds whole lines with no length limit ([AgentBuiltInToolFactory.cs](../src/CodeAlta.Agent/Runtime/Tools/AgentBuiltInToolFactory.cs#L1497)).
  - Such outputs are rare: 16 results over 30k characters on Opus and 4 on Astra.
- **Verbosity.** CodeAlta's final answers on Astra have a median of 716 characters, against 340 for Codex.
  - Codex's model catalog gives GPT-6 Astra `default_verbosity: low` (`codex-rs/models-manager/models.json`), and a capture of Codex 0.161 sends it. The run's trace does not record it.
  - CodeAlta sends no verbosity on `openai-responses`. The effect is small.

### What the data rules out

- **Long-context price.** Only GPT-6 Astra has one, above 272k input tokens, and no request reached it. The largest CodeAlta / Astra request is about 147k tokens (fitted; CodeAlta's own gauge bounds it at 224k). Codex's largest is 118k. Opus 5.5 has a flat price: Claude Code's costs reproduce with requests up to 207k.
- **Compaction, errors, timeouts.** No compaction in any run. There was one recovered provider retry (CodeAlta / Astra) and no timeout or error.
- **Tests.** CodeAlta ran as many as the native harnesses: 283 vs 283 on Opus, 304 vs 279 on Astra.
- **Prompt caching.** The cached shares are similar: 95.8% vs 95.5%, and 93.6% vs 92.7%.
- **Sub-agents.** No child session ran in any of the four runs.

## Failures

| Task | Failed run | What went wrong | Harness role |
|---|---|---|---|
| `element-web-66d0b318` | CodeAlta / Opus | It wrote the `setDuration` call the fix needed, then removed it, guessing at the hidden tests. | None: it checked its work with tsc, jest and eslint, all green. |
| `ansible-c1f2df47` | CodeAlta / Astra | `exec_module` makes a live REST call (`module_provisioned`) that the hidden test does not mock. | None visible. Its sub-agent request was refused; whether a child would have changed the design is unknown. |
| `openlibrary-b67138b3` | Codex / Astra | It rewrote the contribution line and dropped a comma, then called the 14 failing fixtures "stale expectations". | None |
| `webclients-e65cc5f3` | Codex / Astra | Both the retry timer and an effect request the retry, and the duplicate is sent later. | None |
| `ansible-40ade1f8` | both Astra | The 0-second timeout also wraps `realpath`, which expires before the mount command. | None: the same design in both harnesses. |
| `openlibrary-0dc5b20f` | both Astra | CodeAlta changed a function signature and edited the existing test to match. Codex made every 700/720 person an author and regenerated 19 expected files. | None. CodeAlta's reviewer sub-agent was refused; whether a review would have caught it is unknown. |
| `openlibrary-7cbfb812` | all four | Every patch wraps the writes in `db.transaction()`, which the hidden fake store lacks. The statement never mentions a database. | None: a flaw of the task. |

- **Discordant pairs.** In each discordant pair of the same model, the decisive difference was a design choice of the model, and both sides ran tests on their work.
- **Tests changed or explained away.** In 4 of the 12 failed trials, all on GPT-6 Astra, the agent changed or explained away tests:
  - CodeAlta rewrote an existing test to fit its new signature before running it (`openlibrary-0dc5b20f`).
  - Codex regenerated expected files (`openlibrary-0dc5b20f`), called failures stale (`openlibrary-b67138b3`), and deleted failing assertions of its own (`webclients-e65cc5f3`).

  The verifier runs the original tests and the hidden ones, so none of these changes helped.

## Recommendations

Ordered by expected effect. The estimates come from this run only.

1. **A prompt without the interactive workflow for headless runs.**
   - *Change.* In `altacli run`, leave out:
     - the scratchpad;
     - follow-up tasks and sticky notes;
     - sub-agents, unless the run can create them;
     - plan files, review by the user, branches and worktree cleanup;
     - mode handoff, the list of agent prompts, the session title and file links.

     Keep the rules, the working loop, verification and the `Headless run` section.
   - *How.* The prompt templates already have tool conditions ([PromptToolConditions.cs](../src/CodeAlta.Orchestration/Runtime/SystemPrompts/PromptToolConditions.cs)). A headless condition beside them can wrap the interactive parts of [default.prompt.md](../src/CodeAlta.Orchestration/content/prompts/agents/default.prompt.md). Another way is a lean built-in agent prompt that `altacli run` takes by default.
   - *Effect, on input.* Removing the bookkeeping-only requests alone brings Astra from ×1.51 to about ×1.30 against Codex, and Opus from ×1.18 to about ×1.13 against Claude Code.
   - *Effect, on output.* Without the scratchpad text and the `alta` arguments, Astra's output goes from ×1.29 to about ×1.13.
   - *On top of that.* The re-reads of the bookkeeping content, and a prompt about 3k tokens shorter per request (estimated).
2. **Do not make the model read `alta --help`.**
   - The tool description says "Start with args `["--help"]`" ([AltaSessionToolFactory.cs](../src/CodeAlta.LiveTool/AltaSessionToolFactory.cs#L23)). GPT-6 Astra did it in every trial.
   - The 4.2k characters of help (about 1k tokens) then stay in the context: about 1.5M tokens read again over the run (estimated).
   - In a headless run, name the few commands it has in the description, or keep that help short.
3. **Offer sub-agents only where they can be created.**
   - All 51 Astra trials tried, and all 55 attempts were refused.
   - Either let a session that does without some tools create children that do without them too (the task above), or leave the sub-agent section out when `alta session create` would be refused.
   - Whether sub-agents help on these tasks is a separate question: neither native harness used one.
4. **Background jobs in headless runs.**
   - Tell the model to wait for its jobs before the final answer, or to run tests in the foreground when they fit the shell timeout ([system prompt](../src/CodeAlta.Orchestration/content/prompts/system/default.system-prompt.md#L30)).
   - A job that ends after the answer starts a new run at full context and can repeat the answer.
5. **Less narration between tool calls.**
   - On Opus, CodeAlta's text between tool calls is 3.2× Claude Code's. The difference is about 37k output tokens (estimated at 4 characters per token), a quarter of the extra output, read again by every later request.
   - In a headless run nobody reads progress messages: say so in the `Headless run` section.
6. **Bound what one tool result adds to the context.**
   - It rarely mattered here, but nothing stops it: a result is cut only above about 370–400k characters ([AgentSession.cs](../src/CodeAlta.Agent/Runtime/AgentSession.cs#L4111-L4140)). For comparison, Claude Code saves a large output to a file and shows a short preview; Codex cuts each result at 10k tokens.
   - `grep`: cap the length of each matching line and the total ([AgentBuiltInToolFactory.cs](../src/CodeAlta.Agent/Runtime/Tools/AgentBuiltInToolFactory.cs#L1497)).
   - `read_file`: cap the characters as well as the lines ([AgentBuiltInToolOptions.cs](../src/CodeAlta.Agent/Runtime/Tools/AgentBuiltInToolOptions.cs#L74-L84)).
   - `shell_command`: keep the head and the tail of a long output and save the whole of it to a file the model can read.
7. **Fix CodeAlta's context gauge on GPT models.**
   - The gauge was above the real count in all 51 Astra trials, by a median 44%. The largest gauge value of a trial is a median 1.57× its largest real input.
   - The cause: after a response, `SelectUsageForConversationEstimate` keeps the previous, larger local estimate when the provider reports a smaller count ([AgentSession.cs](../src/CodeAlta.Agent/Runtime/AgentSession.cs#L1925)). That rule was made for providers that report only the new tail of a request.
   - Compaction (at 95% of the input limit, [AgentSession.cs](../src/CodeAlta.Agent/Runtime/AgentSession.cs#L2977-L2983)) and the tool-result budget both read this gauge. In a long GPT session, compaction would start at about 60% of the real limit, and outputs would be cut too early. That is an extrapolation: these runs stayed under 150k tokens, and it had no effect here.
8. **Send the model's verbosity and a cache key on `openai-responses`.**
   - Codex sends the model's default verbosity: `low` for GPT-6 Astra in its catalog.
   - CodeAlta sends none on `openai-responses`. On the `codex` provider it sends the provider's `text_verbosity` setting, `medium` by default, although the model list gives each model's own default ([OpenAIResponsesTurnExecutor.cs](../src/CodeAlta.Agent.OpenAI/OpenAIResponsesTurnExecutor.cs#L1549-L1555)).
   - A `prompt_cache_key` had no visible effect here, but keeps the cache routing stable when many sessions share a prefix.
9. **Measure Code Mode on the same tasks.**
   - CodeAlta's Code Mode is being built on the `feature/codemode` branch, off by default.
   - Once it is merged, a rerun with `--ak extra_args="--code-mode=only"` would show what it changes on GPT-6 Astra, also with `exec` as a freeform tool, as Codex sends it.
   - The data suggests batching gives less than the bookkeeping fix: CodeAlta's parallel calls already make 2.37 tool calls per request, against 1.93 inside Codex's scripts.
10. **Benchmark setup.**
    - Record the provider *type* in the adapter's `codealta_session` metadata, not only the provider id `anthropic`.
    - Add the install step and the documentation for running on the Claude Code CLI.
    - Put a cost computed from the bundled models.dev prices in the `_codealta/result` line, so Harbor's `cost_usd` is filled for CodeAlta.
    - Run each harness at least twice: the per-task ratios vary a lot, as the Opus quartiles of 0.95 and 1.43 show.

## Method and limits

- **Sources.**
  - Tokens and results come from each trial's `result.json`.
  - Requests, tool calls and their outputs come from the traces: `altacli.jsonl`, the Claude Code stream and session files, and the Codex rollout.
  - For Claude Code and Codex, the per-request token sums of the traces match `result.json` exactly in every trial. For CodeAlta, the totals are those of the trace's `_codealta/result` line, which Harbor reports.
- **CodeAlta requests.**
  - CodeAlta writes up to two `usage_update` lines per request: a prediction before it, after tool results, and the count after the response. Requests are counted from the latter, exactly.
  - On Opus, the input of each request is the update after the response minus an estimated share of its output.
  - On Astra, CodeAlta's update holds its own over-estimate of the context (recommendation 7), so the input per request is fitted to the exact total.
  - CodeAlta's trace keeps tool outputs up to 8,192 characters. Longer ones (168 on Opus, 336 on Astra) are sized from the growth of the context, within about ±15%.
- **Classification.**
  - Calls counted as bookkeeping are the `alta` commands other than `alta job`, and the writes and reads of the scratchpad.
  - "Bookkeeping-only" requests made no other tool call.
- **Codex counts.** Codex's tool calls are the calls inside its `exec` scripts; the `exec` and `wait` calls that carry them are counted apart.
- **Single run.** There is one run per harness.
- **Raw data.** The job folders are not part of the repository.

## Per-task results

Each cell: solved (✓) or not (✗), model requests, input tokens, agent time.

| Task | CodeAlta / Opus | Claude Code / Opus | CodeAlta / Astra | Codex / Astra |
|---|---|---|---|---|
| `NodeBB-8168c6c4` | ✓ 33 · 2.18M · 6 min | ✓ 35 · 2.46M · 6 min | ✓ 26 · 1.91M · 9 min | ✓ 18 · 1.11M · 6 min |
| `ansible-40ade1f8` | ✓ 41 · 5.20M · 19 min | ✓ 25 · 2.56M · 14 min | ✗ 23 · 1.04M · 10 min | ✗ 19 · 0.85M · 7 min |
| `ansible-b748edea` | ✓ 20 · 0.95M · 6 min | ✓ 14 · 0.72M · 5 min | ✓ 19 · 0.63M · 4 min | ✓ 11 · 0.33M · 3 min |
| `ansible-c1f2df47` | ✓ 25 · 1.05M · 5 min | ✓ 26 · 1.21M · 4 min | ✗ 19 · 0.87M · 5 min | ✓ 16 · 0.71M · 4 min |
| `ansible-d33bedc4` | ✓ 42 · 2.70M · 8 min | ✓ 38 · 2.17M · 9 min | ✓ 22 · 1.03M · 6 min | ✓ 14 · 0.55M · 3 min |
| `ansible-d62496fe` | ✓ 15 · 0.47M · 3 min | ✓ 13 · 0.42M · 2 min | ✓ 17 · 0.53M · 4 min | ✓ 9 · 0.22M · 2 min |
| `ansible-d72025be` | ✓ 41 · 4.01M · 14 min | ✓ 25 · 2.23M · 10 min | ✓ 23 · 1.08M · 7 min | ✓ 19 · 1.03M · 5 min |
| `ansible-de5858f4` | ✓ 54 · 4.96M · 13 min | ✓ 41 · 4.04M · 13 min | ✓ 23 · 1.30M · 7 min | ✓ 19 · 0.92M · 5 min |
| `ansible-e9e60012` | ✓ 18 · 0.68M · 2 min | ✓ 14 · 0.52M · 2 min | ✓ 16 · 0.60M · 5 min | ✓ 12 · 0.44M · 3 min |
| `element-web-5dfde12c` | ✓ 41 · 2.16M · 25 min | ✓ 35 · 1.96M · 31 min | ✓ 26 · 1.50M · 7 min | ✓ 20 · 1.01M · 4 min |
| `element-web-66d0b318` | ✗ 36 · 3.38M · 26 min | ✓ 41 · 4.46M · 38 min | ✓ 25 · 1.37M · 5 min | ✓ 23 · 1.19M · 6 min |
| `element-web-772df302` | ✓ 27 · 1.45M · 7 min | ✓ 26 · 1.57M · 12 min | ✓ 35 · 2.40M · 10 min | ✓ 17 · 0.88M · 4 min |
| `element-web-ecfd1736` | ✓ 22 · 0.97M · 7 min | ✓ 19 · 0.93M · 6 min | ✓ 25 · 1.05M · 5 min | ✓ 18 · 0.75M · 4 min |
| `flipt-2ca5dfb3` | ✓ 55 · 4.51M · 22 min | ✓ 44 · 3.30M · 19 min | ✓ 29 · 2.21M · 10 min | ✓ 28 · 1.74M · 8 min |
| `flipt-3d5a345f` | ✓ 38 · 2.91M · 16 min | ✓ 42 · 3.29M · 15 min | ✓ 39 · 2.92M · 11 min | ✓ 23 · 1.29M · 7 min |
| `flipt-524f2773` | ✓ 60 · 4.47M · 14 min | ✓ 48 · 4.13M · 10 min | ✓ 31 · 2.04M · 10 min | ✓ 19 · 1.14M · 6 min |
| `flipt-c1728053` | ✓ 22 · 0.61M · 4 min | ✓ 21 · 0.68M · 4 min | ✓ 24 · 0.86M · 8 min | ✓ 16 · 0.58M · 4 min |
| `flipt-c188284f` | ✓ 34 · 2.20M · 16 min | ✓ 35 · 2.17M · 21 min | ✓ 21 · 1.19M · 6 min | ✓ 22 · 1.26M · 7 min |
| `flipt-cd18e54a` | ✓ 32 · 2.49M · 12 min | ✓ 37 · 3.38M · 11 min | ✓ 29 · 1.90M · 8 min | ✓ 23 · 1.36M · 6 min |
| `flipt-e2bd19da` | ✓ 31 · 1.99M · 10 min | ✓ 21 · 1.16M · 8 min | ✓ 26 · 1.48M · 7 min | ✓ 21 · 1.00M · 6 min |
| `navidrome-69e0a266` | ✓ 19 · 0.63M · 4 min | ✓ 21 · 0.74M · 5 min | ✓ 28 · 1.31M · 6 min | ✓ 16 · 0.68M · 4 min |
| `navidrome-874b17b8` | ✓ 44 · 2.17M · 8 min | ✓ 36 · 1.77M · 7 min | ✓ 37 · 2.27M · 7 min | ✓ 25 · 1.52M · 7 min |
| `navidrome-bf2bcb12` | ✓ 27 · 1.26M · 6 min | ✓ 26 · 1.04M · 5 min | ✓ 30 · 2.08M · 8 min | ✓ 21 · 1.18M · 5 min |
| `openlibrary-0dc5b20f` | ✓ 33 · 3.07M · 5 min | ✓ 19 · 1.05M · 4 min | ✗ 20 · 0.92M · 7 min | ✗ 26 · 1.44M · 9 min |
| `openlibrary-322d7a46` | ✓ 42 · 4.79M · 11 min | ✓ 40 · 5.01M · 14 min | ✓ 22 · 1.35M · 10 min | ✓ 20 · 1.10M · 8 min |
| `openlibrary-53d376b1` | ✓ 44 · 2.53M · 8 min | ✓ 25 · 1.55M · 8 min | ✓ 33 · 1.71M · 6 min | ✓ 18 · 0.79M · 5 min |
| `openlibrary-630221ab` | ✓ 34 · 2.71M · 7 min | ✓ 25 · 1.74M · 6 min | ✓ 22 · 1.26M · 5 min | ✓ 16 · 0.83M · 5 min |
| `openlibrary-6e889f4a` | ✓ 27 · 1.12M · 3 min | ✓ 13 · 0.35M · 2 min | ✓ 17 · 0.60M · 3 min | ✓ 19 · 0.70M · 4 min |
| `openlibrary-7cbfb812` | ✗ 16 · 0.57M · 4 min | ✗ 12 · 0.40M · 2 min | ✗ 18 · 0.61M · 4 min | ✗ 11 · 0.36M · 2 min |
| `openlibrary-a48fd6ba` | ✓ 23 · 1.48M · 4 min | ✓ 14 · 0.72M · 3 min | ✓ 26 · 1.35M · 6 min | ✓ 15 · 0.59M · 4 min |
| `openlibrary-b67138b3` | ✓ 58 · 6.90M · 13 min | ✓ 52 · 6.46M · 15 min | ✓ 31 · 2.30M · 10 min | ✗ 19 · 1.07M · 9 min |
| `qutebrowser-3e21c821` | ✓ 37 · 3.19M · 9 min | ✓ 25 · 1.63M · 8 min | ✓ 23 · 1.35M · 7 min | ✓ 15 · 0.67M · 4 min |
| `qutebrowser-c09e1439` | ✓ 25 · 0.97M · 4 min | ✓ 20 · 0.75M · 3 min | ✓ 19 · 0.82M · 4 min | ✓ 11 · 0.38M · 2 min |
| `qutebrowser-deeb15d6` | ✓ 30 · 1.58M · 6 min | ✓ 27 · 1.31M · 5 min | ✓ 20 · 0.76M · 5 min | ✓ 16 · 0.61M · 3 min |
| `qutebrowser-e70f5b03` | ✓ 26 · 1.12M · 3 min | ✓ 17 · 0.71M · 2 min | ✓ 20 · 1.08M · 4 min | ✓ 22 · 1.20M · 5 min |
| `teleport-1316e672` | ✓ 34 · 2.88M · 14 min | ✓ 39 · 3.65M · 17 min | ✓ 30 · 1.50M · 8 min | ✓ 16 · 0.61M · 6 min |
| `teleport-2b15263e` | ✓ 31 · 1.86M · 44 min | ✓ 42 · 3.32M · 31 min | ✓ 36 · 2.17M · 16 min | ✓ 31 · 2.33M · 14 min |
| `teleport-65438e6e` | ✓ 23 · 1.14M · 12 min | ✓ 16 · 0.85M · 13 min | ✓ 22 · 1.05M · 9 min | ✓ 31 · 1.63M · 9 min |
| `teleport-7744f72c` | ✓ 22 · 0.97M · 7 min | ✓ 18 · 0.86M · 6 min | ✓ 14 · 0.53M · 6 min | ✓ 10 · 0.40M · 3 min |
| `teleport-eda668c3` | ✓ 20 · 1.63M · 13 min | ✓ 19 · 1.21M · 14 min | ✓ 25 · 1.61M · 12 min | ✓ 16 · 0.83M · 4 min |
| `teleport-f432a71a` | ✓ 17 · 0.62M · 4 min | ✓ 15 · 0.60M · 4 min | ✓ 17 · 0.68M · 4 min | ✓ 12 · 0.47M · 2 min |
| `tutanota-f373ac38` | ✓ 115 · 17.85M · 16 min | ✓ 91 · 12.52M · 15 min | ✓ 45 · 4.53M · 9 min | ✓ 33 · 2.59M · 6 min |
| `vuls-5af1a227` | ✓ 21 · 1.08M · 15 min | ✓ 19 · 0.96M · 15 min | ✓ 28 · 1.22M · 9 min | ✓ 27 · 1.52M · 7 min |
| `vuls-ca3f6b1d` | ✓ 32 · 1.98M · 9 min | ✓ 35 · 2.12M · 9 min | ✓ 27 · 1.60M · 8 min | ✓ 18 · 1.05M · 5 min |
| `vuls-e1fab805` | ✓ 36 · 3.50M · 19 min | ✓ 33 · 2.90M · 17 min | ✓ 29 · 1.89M · 12 min | ✓ 21 · 1.27M · 8 min |
| `vuls-ef2be3d6` | ✓ 37 · 3.08M · 13 min | ✓ 35 · 2.52M · 20 min | ✓ 19 · 1.12M · 5 min | ✓ 13 · 0.62M · 4 min |
| `webclients-08bb0991` | ✓ 42 · 2.28M · 12 min | ✓ 22 · 1.29M · 10 min | ✓ 24 · 0.97M · 6 min | ✓ 16 · 0.53M · 5 min |
| `webclients-5f0745dd` | ✓ 38 · 2.29M · 12 min | ✓ 30 · 1.79M · 12 min | ✓ 36 · 1.96M · 7 min | ✓ 24 · 1.13M · 6 min |
| `webclients-715dbd4e` | ✓ 65 · 7.31M · 13 min | ✓ 50 · 5.52M · 23 min | ✓ 43 · 3.55M · 10 min | ✓ 26 · 1.65M · 6 min |
| `webclients-c5a2089c` | ✓ 28 · 1.35M · 37 min | ✓ 15 · 0.61M · 9 min | ✓ 33 · 1.39M · 5 min | ✓ 21 · 0.93M · 4 min |
| `webclients-e65cc5f3` | ✓ 55 · 5.33M · 22 min | ✓ 62 · 8.61M · 39 min | ✓ 43 · 3.27M · 15 min | ✗ 26 · 1.61M · 11 min |
