---
title: Guide agents to fix causes and edit in place, with a measured benefit
status: draft
created: 2026-11-13
summary: The built-in prompts ask for the smallest diff, so agents patch symptoms, stack workarounds and append text. A short "Healthy changes" section and a few rewordings replace that, and a benchmark of six probes shows what they change, before and after, on four models.
---

# Guide agents to fix causes and edit in place, with a measured benefit

Agents driven by CodeAlta add more easily than they remove: a guard at the caller instead of a fix in the helper, a second workaround on a first one, a note appended to a paragraph. The built-in prompts encourage it: six of their statements ask for the smallest diff, and none asks to fix a cause or to rewrite a sentence. Once this work is done, the prompts ask for the change that leaves the simplest result, within a stated limit, and a benchmark that anyone can run again shows what that changes.

## What changes

### A benchmark first

A kit under `benchmarks/prompt-health/` builds a small Node project with planted problems, runs the same task in sessions that differ only by their prompts, and scores what each session left in its worktree. It compares two versions of the prompt files of this repository: a git revision and the working tree.

| Probe | Task given | Healthy outcome | What is counted |
| --- | --- | --- | --- |
| `cause` | A report shows a wrong total. The fault is in a shared helper, and another caller already works around it. | Helper fixed, old workaround gone. | Hidden test of the helper; workarounds left at callers. |
| `vendor` | A bug of a vendored library that one caller already works around shows at a second caller. | One workaround in one place, explained. | Places that hold the workaround; edits of the vendored file. |
| `text` | Document that an option accepts a new form, in a paragraph that already covers it. | The sentence rewritten. | Growth of the section; notes appended. |
| `rules` | "Remember" a rule that replaces one of `AGENTS.md`. | The rule changed. | Old rule still there; lines added. |
| `leftover` | Replace an implementation; a table and a helper become unused. | Both removed. | Unused symbols left. |
| `scope` | Rename an option in a file that also holds unrelated duplication. | The rename alone. | Lines changed that the rename does not need. |

Every run also records the files and lines changed, the tool calls, the tokens and the cost. A session that only answers "OK" gives the size of each prompt in the tokens of each vendor.

Models: `codex:gpt-6.1-sol` and `codex:gpt-6.1-terra` (three runs per probe and version), `anthropic:claude-sonnet-5-5` (two) and `anthropic:claude-opus-5-5` (one). The Anthropic runs stop at $40 of reported cost; the limit given is $50.

### The section, in the default agent prompt

The system prompt is not sent to Claude Code, Codex CLI and ACP sessions: they get the agent prompt on top of the prompt of their own tool. So the guidance goes in `src/CodeAlta.Orchestration/content/prompts/agents/default.prompt.md`, after "Working loop". The first draft, to be shortened or changed by what the benchmark shows:

```markdown
## Healthy changes
The user owns the project's direction; you maintain what your task touches. Judge a change by how the code or text reads after it, not by the size of the diff.
- Cause: find why before changing anything, and fix it there: a fault in a shared helper is fixed and tested in the helper, not worked around at a caller.
- Workarounds: never stack them. If the code already works around the problem, find why (`git log`, its tests), fix what it hides and remove it. If the cause is out of reach, keep one workaround in one place, with a comment: why, and what removes it.
- Remove: prefer changing, merging or deleting to adding. What your change makes unused goes with it (old paths, parameters, sentences, debug output), except what others rely on, such as a public API.
- Text: edit in place. Rewrite the sentence or section that covers the fact so it reads as written once: one fact in one place, one idea per sentence or bullet; split a line rather than extend it. In instruction files (AGENTS.md, skills, prompts, memory), change the existing rule instead of adding one beside it. Comments say why, not what changed.
- Proportion: tidy only what your change touches. Restructure first, as its own step and smaller than the task, only what the change needs to be done right. Report the rest in one line.
```

A rule stays only if a probe shows that it changes something. Two rules of the analysis have no probe and are left out: "a failing check is evidence" and "after two failed fixes, question the diagnosis".

### The system prompt stops asking for the opposite

`src/CodeAlta.Orchestration/content/prompts/system/default.system-prompt.md`, for no more tokens than today:

| Today | Becomes |
| --- | --- |
| Existing files … are user-owned unless created this turn. | User work is preserved; code and text in the scope of the task are the agent's to change, restructure or delete. |
| inspect → implement → verify → self-review diff → report. Do not stop at analysis/proposal… | Adds "diagnose", and: when asked for analysis or a proposal, deliver that and stop. |
| Make the smallest coherent change that solves the task. | Make the change that leaves the simplest correct result, not the smallest diff. |
| Avoid broad refactors, new abstractions… | Avoid new abstractions…; refactor only what the task needs, report the rest. |
| Prefer local duplication over premature abstraction… | Reuse before writing; tolerate a second copy, extract at the third. |
| Three bullets on tool output and context | Two: "tool output is evidence" is already in Trust and in Tool Guidance. |
| Reviews: … test gaps | … test gaps, and needless complexity. |

### Three small edits

- Follow-up tasks (`default.prompt.md`): a restructuring that the task showed to be needed and that is larger than it can be proposed as a task.
- Plan mode (`plan.prompt.md`): "the simplest approach that fixes the cause", not "the smallest safe approach".
- The project's adaptive prompts drop the sentence on shared helpers, which they now inherit.

## Steps

- [x] 1. Write this plan from `tmp/improving-system-prompt-analysis.md`, on the branch `enhance/healthy-prompts`
- [ ] 2. Build the kit: fixture project, the six probes, the scorer, the runner (`benchmarks/prompt-health/`)
- [ ] 3. Pilot: one probe on one model, to check that sessions use the prompts of the benchmark and that the scores read right
- [ ] 4. Measure "before": the prompts of `main`
- [ ] 5. Change the three prompt files and the adaptive prompts
- [ ] 6. Measure "after"; change or remove what shows no benefit, and measure again what changed
- [ ] 7. Write the report `benchmarks/2026-11-13-prompt-health.md` and record the result here
- [ ] 8. Remove the benchmark project from the catalog, its worktrees, and the analysis file in `tmp/`

## How it is verified

- [ ] The report shows, per probe and model, the healthy outcomes before and after, and no probe gets worse
- [ ] `scope` stays clean: the section must not make agents refactor what the task does not need
- [ ] Prompt sizes, in the tokens the vendors count: the system prompt is not larger, and the growth of the agent prompt is stated
- [ ] `dotnet test -c Debug --filter BuiltInPromptContentTests` passes, then the whole .NET suite
- [ ] Anthropic spending of the runs is under $50, from the cost the sessions report

## Decisions

| Decision | Choice | Why |
| --- | --- | --- |
| Where the section lives | Default agent prompt | The only prompt every provider receives. |
| Ownership wording | "You maintain what your task touches" | Anthropic measured that "you own the codebase" merges duplicated code in 75% of runs instead of under 10%, for 70 to 80% more output tokens and unrequested changes. This is the middle; `scope` checks that it is not too strong. |
| Rules without a probe | Left out | The request is a benefit that is measured, for the fewest tokens. |
| Tests of the wording | None added | The benchmark checks behavior; a test that pins sentences only freezes them. |
| Long bullets of the agent prompt | Not rewritten here | Each was tuned on a failure that these probes do not cover. |

## Risks

- A small sample: three runs per cell show large effects only. The report gives counts, not percentages, and says where a difference is within noise.
- The probes are small and written by the author of the section. They show that the rules are followed, not that a large codebase ages better; the report says so.
- Sessions run on the real profile, in a temporary project that step 8 removes.

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
- The balance: [Opportunistic Refactoring](https://martinfowler.com/bliki/OpportunisticRefactoring.html), [preparatory refactoring](https://martinfowler.com/articles/preparatory-refactoring-example.html), [Small CLs](https://google.github.io/eng-practices/review/developer/small-cls.html), [Working Code Isn't Enough](https://web.stanford.edu/~ouster/cgi-bin/cs190-winter18/lecture.php?topic=working), the [rule of three](https://en.wikipedia.org/wiki/Rule_of_three_(computer_programming)).

</details>
