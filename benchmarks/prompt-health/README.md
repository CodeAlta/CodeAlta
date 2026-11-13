# Prompt health: does a prompt change make agents leave healthier code and text?

A small benchmark for the built-in prompts of CodeAlta (`src/CodeAlta.Orchestration/content/prompts`). It gives the same tasks to sessions that differ only by their prompts, and scores what each session left in its worktree: a fix at the cause or at the symptom, a workaround removed or copied, a sentence rewritten or a note appended.

It runs in a CodeAlta that is already running, with the providers of its user. Results: [2026-11-13](../2026-11-13-prompt-health.md).

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

   `<folder>` is outside this repository, so that the sessions do not read its `AGENTS.md`. The project gets the agent prompts `bench-before` and `bench-after`, each with its system prompt.

2. Add `<folder>/shopkit` to CodeAlta: `alta project add <folder>/shopkit`.

3. From a session of this repository, run the matrix with Code Mode:

   ```js
   return await workflow({ scriptPath: "benchmarks/prompt-health/run.js" }, {
     project: "<folder>/shopkit",
     kit: "<this repository>/benchmarks/prompt-health",
     out: "<this repository>/tmp/prompt-bench/results.jsonl",
     variants: ["before", "after"],
     probes: ["cause", "vendor", "text", "longline", "rules", "leftover", "scope"],
     models: [{ ref: "codex:gpt-6.1-sol@low", runs: 3 }],
     limit: 4,        // sessions at once
     costLimit: 20,   // optional: stop starting sessions past this reported cost
   });
   ```

   Each session works in a worktree of its own, on a prompt of the list, and is not a child of the calling session.

4. Summarize: `node benchmarks/prompt-health/report.mjs tmp/prompt-bench/*.jsonl`.

5. Clean up: `alta project remove <folder>/shopkit --delete-sessions`, `git -C <folder>/shopkit worktree prune` after deleting its worktrees, then the folder.

## Read the results with care

- Three runs per cell show large effects only. Read counts, not percentages.
- The scorer reads the files: [`score.mjs`](score.mjs) says how each outcome is decided. Open the worktree of a surprising run before trusting its label.
- The probes are small. They show whether a rule is followed, not how a large codebase ages.
