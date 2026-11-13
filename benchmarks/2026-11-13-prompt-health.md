# Prompt health: the "Healthy changes" guidance, before and after

2026-11-13. The built-in prompts of CodeAlta got a short section that asks agents to fix causes, not to stack or copy workarounds, to remove what a change makes obsolete, and to edit text in place, within what the task touches ([plan](../.alta/plans/2026-11-13-healthy-changes-in-agent-prompts.md)). This is the measure of what it changes, with the [prompt-health kit](prompt-health/README.md): seven small tasks in a project with planted problems, given to agents that differ only by their prompts.

"Before" is the prompts of `main` at `6e96883f2`. "After" is the prompts of this change.

## Result

Healthy outcomes in the final round: 182 sessions, the two versions run in turn on the same tasks.

| Probe | Healthy outcome | Before | After |
| --- | --- | ---: | ---: |
| `cause` | The shared helper fixed, the old workaround of a caller removed | 2/13 | **12/13** |
| `vendor` | One workaround of the vendored library, in one place | 0/13 | **10/13** |
| `leftover` | The obsolete code and the sentence of the docs removed | 7/13 | 7/13 |
| `text` | The sentence rewritten, nothing appended | 13/13 | 13/13 |
| `longline` | The long line not made longer | 13/13 | 13/13 |
| `rules` | The rule of `AGENTS.md` changed in place | 13/13 | 13/13 |
| `scope` | The rename alone | 13/13 | 13/13 |
| All | | 61/91 | **81/91** |

- **The gain is on causes and workarounds.** Before, no session of thirteen removed the workaround that its fix made useless, unless it ran at high effort, and three patched the symptom at the caller. After, twelve of thirteen fixed the helper and removed the workaround.
- **Nothing got worse on scope.** In both versions every session kept the rename to the rename, no session changed a file its task did not need, and no session left a failing test.
- **Three probes were already healthy** and show no gain: these models rewrite a sentence, add a rule beside a long line and change a rule of `AGENTS.md` correctly without being told. The wording that only these probes covered was removed from the section.
- **`leftover` did not move.** The code is cleaned in both versions; the sentence of the docs that became false is still left by Sonnet 5.5 and by GPT-6.1 Sol at high effort.

## By model

| Model | Before | After |
| --- | ---: | ---: |
| `codex:gpt-6.1-sol@low`, 3 runs per probe | 15/21 | 20/21 |
| `codex:gpt-6.1-terra@low`, 3 runs | 15/21 | 21/21 |
| `codex:gpt-6.1-sol@high`, 2 runs | 11/14 | 12/14 |
| `anthropic:claude-sonnet-5-5@low`, 3 runs | 12/21 | 14/21 |
| `anthropic:claude-opus-5-5@low`, 2 runs | 8/14 | 14/14 |

At high effort GPT-6.1 Sol already removes the old workaround of `cause` by itself; the section still changes `vendor` there (0/2 to 2/2).

Sonnet 5.5 is the model that follows the section least. In the final round it copied the workaround of `vendor` in its three runs, and its answer said why: "That makes two copies of the workaround. If a third importer needs it, move it into one shared helper." That is the rule of three, which this change had first written into the system prompt ("Tolerate a second copy; extract at the third"), against its own section. The sentence was removed. The check of that correction is below.

## What the outcomes were

| Probe | Outcome | Before | After |
| --- | --- | ---: | ---: |
| `cause` | Helper fixed, workaround removed | 2 | 12 |
| `cause` | Helper fixed, old workaround left | 8 | 1 |
| `cause` | Symptom patched at the caller | 3 | 0 |
| `vendor` | One workaround, one place | 0 | 10 |
| `vendor` | Workaround copied to the second caller | 13 | 3 |
| `leftover` | Code and doc clean | 7 | 7 |
| `leftover` | Code clean, doc out of date | 6 | 5 |
| `leftover` | Old code left | 0 | 1 |

On `text`, every session documented the new form, and the paragraph grew by a median of 144 characters before and 132 after. Five sessions of thirteen turned the paragraph into several lines before, two after.

## Cost

| | Before | After |
| --- | ---: | ---: |
| Prompt of a session, tokens counted by Codex (GPT-6.1 Sol) | 15,763 | 16,053 |
| Prompt of a session, tokens counted by Anthropic (Sonnet 5.5) | 18,182 | 18,555 |
| System prompt, characters | 6,403 | 6,417 |
| Default agent prompt, characters | 14,950 | 16,143 |
| Requests, 91 sessions | 442 | 448 |
| Tool calls | 370 | 387 |
| Output tokens | 53,185 | 59,697 |
| Lines added and deleted by the sessions | +495 −260 | +622 −338 |
| Median time of a session | 23.9 s | 24.1 s |

The prompt grows by 290 to 373 tokens, about 2% of what a session sends with its tools. The sessions write 12% more output tokens: a fix at the cause with its test, or a shared helper for both importers, is more work than a patch. They also delete 30% more lines.

## Where the effect comes from

Thirty sessions on Codex ran with the new agent prompt and the system prompt of `main`. On the same five probes and models:

| | Healthy |
| --- | ---: |
| Before | 18/30 |
| New agent prompt, system prompt of `main` | 29/30 |
| After (both) | 29/30 |

The section does the work. This matters for sessions driven through Claude Code, Codex CLI and ACP agents, which receive the agent prompt and not the system prompt.

## The correction of the system prompt

After the final round, "Tolerate a second copy; extract at the third" left the system prompt, which says again "abstract only when it reduces current complexity/risk or follows a stable pattern". Twelve runs of `altacli` checked the two probes it touches, on GPT-6.1 Sol at low effort:

| Probe | Before | After |
| --- | ---: | ---: |
| `vendor` | 0/3 | 3/3 |
| `cause` | 1/3 | 3/3 |

**Not measured on Sonnet 5.5**, the model the correction is for. Its twelve runs did not start: the key of the Anthropic provider of this machine is a secret of CodeAlta (`api_key_secret`), and `altacli` does not offer such a provider ("not configured"). Whether Sonnet 5.5 now keeps one workaround in one place is open.

## How the wording got there

| Round | Sessions | Before | After | What it led to |
| --- | ---: | ---: | ---: | --- |
| 1, Codex | 72 | 21/36 | 33/36 | GPT-6.1 Terra copied the workaround of `vendor` in 3 runs of 3: "never stack them" became "never stack or copy them", in "one shared place". No comment that narrates a change in either version, and `rules` at its ceiling: two sentences removed. |
| 2, Codex and Anthropic | 126 | 41/63 | 58/63 | `longline` added and at its ceiling: "split a line rather than extend it" removed. "workarounds" and "doc sentences" named in what a change removes. |
| Final | 182 | 61/91 | 81/91 | The rule of three removed from the system prompt. |
| Agent prompt alone | 30 | | 29/30 | |

## Limits

- **Small samples.** Two or three runs per cell show large effects only. `cause` and `vendor` moved in every round and on every model but Sonnet 5.5; the difference of one session on `leftover` or between two models is noise.
- **Small tasks, written by the author of the section.** The probes show that the rules are followed when a task meets them. They do not show how a large codebase ages over months of sessions; the research the plan cites says that guidance lowers the start of that curve and not its slope.
- **Low effort for most runs.** Higher effort already fixes part of `cause` without the section.
- **Two ways of running.** The rounds above ran as sessions of CodeAlta Desktop. The kit now runs `altacli` in isolated storage, which does not give the same tools: its results are compared with each other only.
- **Nothing kept.** The benchmark project, its sessions and its worktrees were removed after the runs. The numbers of this report cannot be traced back to a session; the kit reproduces them.
- **Cost.** 415 sessions for the rounds, then 13 runs of `altacli`. The Anthropic part is about $3.60 at list prices, computed from the tokens the sessions reported.
