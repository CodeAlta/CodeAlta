# Prompt health: does a prompt change make agents leave healthier code and text?

A small benchmark for the built-in prompts of CodeAlta (`src/CodeAlta.Orchestration/content/prompts`). It gives the same tasks to agents that differ only by their prompts, and scores what each one left in its worktree: a fix at the cause or at the symptom, a workaround removed or copied, a sentence rewritten or a note appended.

The agents run with `altacli`, with the providers of its user. Results: [2026-11-13](../2026-11-13-prompt-health.md).

## The project and the probes

[`fixture/`](fixture) is `shopkit`, a Node.js project without dependencies, with planted problems. [`probes.json`](probes.json) holds the task of each probe and the files it may change.

| Probe | Task | Healthy outcome |
| --- | --- | --- |
| `cause` | A report shows a wrong total; the fault is in a shared helper that another caller works around. | Helper fixed, old workaround removed. |
| `vendor` | A bug of a vendored library, worked around at one caller, shows at a second one. | One workaround in one place. |
| `text` | Document a new form of an option in the paragraph that covers it. | The sentence rewritten, no note appended. |
| `longline` | Add a rule to a policy whose first bullet is already one sentence of 330 characters. | The long line not made longer. |
| `rules` | "Remember" a rule that replaces one of `AGENTS.md`. | The rule changed in place. |
| `leftover` | Replace an implementation; a table, a helper and a sentence of the docs become obsolete. | All three gone. |
| `scope` | Rename an option in a file that also holds unrelated duplication. | The rename alone. |
| `size` | Answer "OK". | Gives the size of the prompts in the tokens the provider counts. |

## Run it

1. Create the project and the prompts to compare, each from a git revision or from the working tree:

   ```sh
   node benchmarks/prompt-health/setup.mjs <folder> before=main after=WORKTREE
   ```

   `<folder>` is outside this repository, so that the agents do not read its `AGENTS.md`. The project gets the agent prompts `bench-before` and `bench-after`, each with its system prompt. `name=<agent revision>,<system revision>` mixes two versions, to see what each file contributes. Run it again after a change of the prompts or of the fixture.

2. Copy a build of `altacli` (`src/CodeAlta.Cli/bin/Release/net10.0`) into `<folder>/altacli`, so that the runs do not lock the files of a build.

3. Run the matrix:

   ```sh
   node benchmarks/prompt-health/run.mjs --project <folder>/shopkit --altacli <folder>/altacli/altacli \
     --out tmp/prompt-bench/results.jsonl --variants before,after \
     --probes cause,vendor,text,longline,rules,leftover,scope \
     --models codex:gpt-6.1-sol@low=3,anthropic:claude-sonnet-5-5@low=2 --limit 4 --cost-limit 20
   ```

   Each run gets a git worktree under `<folder>/runs` and one `altacli run --isolated --enable all --no-sub-sessions`. `--models` gives the number of runs of each model; `--limit` is how many run at once; `--cost-limit` stops starting runs once the cost they reported passes it. A result is written as soon as its run ends.

   The sessions go to the isolated storage of `altacli` (`~/.alta/cli`): CodeAlta Desktop and TUI never list them. `altacli --isolated alta session ...` reads them.

   Check the providers first with `altacli models --isolated`: a provider whose key is a secret of CodeAlta (`api_key_secret`) is not offered, and its runs end at once with `provider_not_ready`.

4. Summarize: `node benchmarks/prompt-health/report.mjs tmp/prompt-bench/results.jsonl`.

5. Clean up: delete `<folder>`.

## Read the results with care

- Three runs per cell show large effects only. Read counts, not percentages.
- Compare versions run the same way. A run of `altacli` and a session of CodeAlta Desktop do not get the same tools or the same prompt parts: do not mix them in one table.
- The scorer reads the files: [`score.mjs`](score.mjs) says how each outcome is decided. Open the worktree of a surprising run before trusting its label.
- The probes are small. They show whether a rule is followed, not how a large codebase ages.
