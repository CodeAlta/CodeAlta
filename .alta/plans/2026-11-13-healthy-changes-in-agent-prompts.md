---
title: Guide agents to fix causes and edit in place, with a measured benefit
status: done
created: 2026-11-13
summary: The built-in prompts asked for the smallest diff, so agents patched symptoms, copied workarounds and left obsolete code. A short "Healthy changes" section and a few rewordings replace that, and a benchmark of seven probes shows what they change, before and after, on four models.
---

# Guide agents to fix causes and edit in place, with a measured benefit

Agents driven by CodeAlta add more easily than they remove: a guard at the caller instead of a fix in the helper, a second copy of a workaround, a note appended to a paragraph. The built-in prompts encouraged it: six of their statements asked for the smallest diff, and none asked to fix a cause. The prompts now ask for the change that leaves the simplest result, within a stated limit, and a benchmark that anyone can run again shows what that changes.

## Result

Final round, 182 sessions, the prompts of `main` against the new ones on the same tasks ([report](../../benchmarks/2026-11-13-prompt-health.md)):

| Probe | Healthy outcome | Before | After |
| --- | --- | ---: | ---: |
| `cause` | Shared helper fixed, old workaround of a caller removed | 2/13 | **12/13** |
| `vendor` | One workaround of a vendored library, in one place | 0/13 | **10/13** |
| `leftover` | Obsolete code and doc sentence removed | 7/13 | 7/13 |
| `scope` | The rename alone | 13/13 | 13/13 |
| `text`, `longline`, `rules` | Text edited in place | 13/13 each | 13/13 each |
| All | | 61/91 | **81/91** |

- No session changed a file its task did not need, and none left a failing test, in either version.
- The prompt of a session grows by 290 tokens on Codex and 373 on Anthropic, about 2%. The system prompt keeps its size.
- The sessions write 12% more output tokens: a fix at the cause is more work than a patch.
- With the new agent prompt alone, 29 of 30 Codex sessions are healthy: the section does the work, also for the providers that never get the system prompt.

## What changed

### The section, in the default agent prompt

The system prompt is not sent to Claude Code, Codex CLI and ACP sessions: they get the agent prompt on top of the prompt of their own tool. So the guidance is in `src/CodeAlta.Orchestration/content/prompts/agents/default.prompt.md`, after "Working loop":

```markdown
## Healthy changes
The user owns the project's direction; you maintain what your task touches. Judge a change by how the code or text reads after it, not by the size of the diff.
- Cause: find why before changing anything, and fix it there: a fault in a shared helper is fixed and tested in the helper, not worked around at a caller.
- Workarounds: never stack or copy them. If the code already works around the problem, find why (`git log`, its tests), fix what it hides and remove it. If the cause is out of reach, keep the workaround in one shared place, with a comment: why, and what removes it.
- Remove: prefer changing, merging or deleting to adding. What your change makes unused or untrue goes with it (workarounds, old paths, parameters, doc sentences, debug output), except what others rely on, such as a public API.
- Text: edit in place. Rewrite the sentence or section that covers the fact so it reads as written once: one fact in one place, one idea per sentence or bullet.
- Proportion: tidy only what your change touches. Restructure first, as its own step and smaller than the task, only what the change needs to be done right; leave the rest.
```

A rule stayed only where a probe could show that it changes something, or where it limits the others ("Proportion"). Removed for that reason: a rule on instruction files, "comments say why, not what changed", "split a line rather than extend it", "report the rest in one line". Never added, for lack of a probe: "a failing check is evidence" and "after two failed fixes, question the diagnosis".

### The system prompt stops asking for the opposite

`src/CodeAlta.Orchestration/content/prompts/system/default.system-prompt.md`, at the size it had:

| Before | Now |
| --- | --- |
| Existing files … are user-owned unless created this turn. | User work is preserved; code and text in the scope of the task are the agent's to change, restructure or delete. |
| inspect → implement → verify → self-review diff → report. Do not stop at analysis/proposal… | Adds "diagnose", and: if asked for analysis or a proposal, deliver that and stop. |
| Make the smallest coherent change that solves the task. | Make the change that leaves the simplest correct result, not the smallest diff. |
| Avoid broad refactors, new abstractions… | Avoid new abstractions…; refactor only what the task needs. |
| Prefer local duplication over premature abstraction; abstract only when… | Reuse before writing: search for an existing helper or pattern; abstract only when… |
| Three bullets on tool output and context | One: "tool output is evidence" is already in Trust and in Tool Guidance. |
| Add/update tests/docs… | Update tests/docs in place…; add only what none covers. |
| Reviews: … test gaps | … test gaps, and needless complexity. |

### Three small edits

- Follow-up tasks (`default.prompt.md`): work that is related to the request but larger than it can be proposed as a task.
- Plan mode (`plan.prompt.md`): "the simplest safe approach that fixes the cause, not the smallest patch".
- The project's adaptive prompts drop the sentence on shared helpers, which they now inherit.

### The benchmark

`benchmarks/prompt-health/` builds a small Node project with planted problems, runs the same task with agents that differ only by their prompts, and scores what each one left in its worktree. It compares two versions of the prompt files: a git revision and the working tree. It runs `altacli` in isolated storage, so that its sessions are never listed in the apps.

## Steps

- [x] 1. Write this plan from `tmp/improving-system-prompt-analysis.md`, on the branch `enhance/healthy-prompts`
- [x] 2. Build the kit: fixture project, the probes, the scorer, the runner
- [x] 3. Pilot: one probe on one model
- [x] 4. Change the three prompt files and the adaptive prompts
- [x] 5. Measure before and after, three rounds; change or remove what shows no benefit
- [x] 6. Write the report `benchmarks/2026-11-13-prompt-health.md` and record the result here
- [x] 7. Move the runner to `altacli --isolated`, and archive the 415 sessions the rounds created in CodeAlta Desktop
- [x] 8. Measure the last correction of the system prompt: done on GPT-6.1 Sol, left unmeasured on Sonnet 5.5
- [x] 9. Remove the benchmark project from the catalog, with its 415 sessions, its worktrees and its folder

## How it is verified

- [x] The report shows, per probe and model, the healthy outcomes before and after, and no probe gets worse
- [x] `scope` stays clean: 13/13 before and after
- [x] Prompt sizes, in the tokens the vendors count: the system prompt is not larger, the agent prompt grows by about 300 tokens
- [x] `dotnet test -c Debug` passes: one test of `CodeAlta.Orchestration.Tests` on git diffs failed while the benchmark ran beside it, and passes alone
- [x] Anthropic spending of the runs is under $50: about $3.60 at list prices, from the tokens the sessions reported

## Decisions

| Decision | Choice | Why |
| --- | --- | --- |
| Where the section lives | Default agent prompt | The only prompt every provider receives. |
| Ownership wording | "You maintain what your task touches" | Anthropic measured that "you own the codebase" merges duplicated code in 75% of runs instead of under 10%, for 70 to 80% more output tokens and unrequested changes. This is the middle; `scope` shows that it is not too strong. |
| Rules without a probe | Left out | The request is a benefit that is measured, for the fewest tokens. |
| Tests of the wording | None added | The benchmark checks behavior; a test that pins sentences only freezes them. |
| Long bullets of the agent prompt | Not rewritten here | Each was tuned on a failure that these probes do not cover. |
| Rule of three | Tried in the system prompt, then removed | Sonnet 5.5 quoted it to justify a second copy of a workaround. |
| Last correction on Sonnet 5.5 | Not measured | GPT-6.1 Sol confirms the final wording (`vendor` 0/3 to 3/3, `cause` 1/3 to 3/3). The Sonnet runs could not start: `altacli` does not offer a provider whose key is a secret of CodeAlta (task `g6rm`). The user accepted the result of Sol as enough. |

## Risks

- A small sample: two or three runs per cell show large effects only. `cause` and `vendor` moved in every round; one session of difference elsewhere is noise.
- The probes are small and written by the author of the section. They show that the rules are followed, not that a large codebase ages better.
- `leftover` did not move: a doc sentence that a change makes false is still left by some models.

## Out of scope

- A second pass that shortens the long bullets of the agent prompt.
- Sending part of the system prompt to Claude Code, Codex CLI and ACP sessions.
- Cleanup skills, an audit of instruction files, a budget check on `max_tokens`.

<details><summary>Sources of the analysis</summary>

- Addition bias: [More is More](https://arxiv.org/abs/2409.02569); [additive strategies in humans and GPT-4](https://arxiv.org/abs/2404.16692), where "improve" adds more than "edit".
- Code written by agents over time: [SlopCodeBench](https://arxiv.org/abs/2603.24755): redundancy grows in 75.5% of runs, and quality guidance lowers the start by up to a third without changing the slope. [GitClear 2025](https://www.gitclear.com/ai_assistant_code_quality_2025_research).
- Passing is not fixed: [solved issues of SWE-bench](https://arxiv.org/abs/2503.15223): 29.6% of passing patches behave differently from the developer's.
- Ownership and piled-up cautions: [Prompting Claude Fable 5.2](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-fable-5-2).
- Vendor wording: [Anthropic prompting best practices](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/claude-prompting-best-practices), [Sonnet 5.5](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-sonnet-5-5), [Codex base instructions](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/prompts/base_instructions/default.md), [Codex Prompting Guide](https://developers.openai.com/cookbook/examples/gpt-5/codex_prompting_guide), [OpenAI guidance for GPT-5.6](https://developers.openai.com/api/docs/guides/prompt-guidance-gpt-5p6), [Gemini CLI prompt](https://github.com/google-gemini/gemini-cli/blob/main/packages/core/src/prompts/snippets.legacy.ts).
- Instruction files: [Claude Code best practices](https://code.claude.com/docs/en/best-practices), [Evaluating AGENTS.md](https://arxiv.org/abs/2602.11988), [Agent READMEs](https://arxiv.org/abs/2511.12884), [How many instructions can LLMs follow at once?](https://arxiv.org/abs/2507.11538), [context engineering](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents).
- The balance: [Opportunistic Refactoring](https://martinfowler.com/bliki/OpportunisticRefactoring.html), [preparatory refactoring](https://martinfowler.com/articles/preparatory-refactoring-example.html), [Small CLs](https://google.github.io/eng-practices/review/developer/small-cls.html), [Working Code Isn't Enough](https://web.stanford.edu/~ouster/cgi-bin/cs190-winter18/lecture.php?topic=working).

</details>
