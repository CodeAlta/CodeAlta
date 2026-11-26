# Judgment: what counts as a finding

People and models both look for something to add when asked to improve. A health review that follows that pull recommends layers, interfaces, rules and pages, and leaves the codebase larger. These rules are the counterweight. Apply them to every candidate, yours first.

## A finding has four parts

1. **A cost someone pays.** A defect that came back, a change that took five files, a test that fails one run in six, a page nobody finishes. "Could be cleaner" is not a cost. Give its number when one exists, and never make one up: a contradiction between two files needs none.
2. **Evidence you can point to.** A file and line, a number from the history or the CI, a command and its result. "Used in many places" carries the count and the search that gave it. What a tool reports is a lead until you have read the code it points at.
3. **A change that removes the cost.** Named, with its first step.
4. **A balance.** What the change removes and what it adds: files, types, concepts a reader must hold, lines, settings, dependencies, words, seconds of test time.

A candidate without one of the four is an observation. It can go in an analysis; it does not go in the report.

## Prefer, in this order

1. **Delete**: code nobody calls, an option nobody sets, a test that checks nothing, a paragraph nobody needs.
2. **Merge or inline**: a layer that only passes through, two modules that always change together, a helper with one caller.
3. **Simplify in place**: fewer cases, fewer parameters, fewer states, one way instead of two.
4. **Move**: put a decision where it belongs, so that one module owns it.
5. **Add**: only when it removes more than it costs, and the finding says what.

Before a recommendation of kind 4 or 5, look for the one of kind 1 to 3 that solves the same problem.

## Tests to apply

- **Deletion test.** Imagine the module gone. If its complexity vanishes, it was a pass-through: merge it. If the complexity reappears in every caller, it earns its place.
- **Two adapters.** An interface with one implementation is indirection, not a seam. Something must really vary across it, and "a mock in the tests" counts only when the real thing cannot run in a test. The exception is a boundary others build on: a published extension point, a protocol, a compatibility promise.
- **Same reason to change.** Two pieces of code that look alike are duplication only when they change together for the same reason. Look at the history: copies that drifted apart or were fixed one without the other are the real case. Otherwise leave them: duplication is cheaper than the wrong abstraction.
- **The wrong abstraction.** A shared helper that grew flags and caller-specific branches is a candidate for inlining back into its callers, not for one more parameter.
- **Why is it there?** Before proposing to remove something odd, find why it exists: `git log -S`, `git blame`, its tests, the comment above it. No reason found after looking is evidence; not having looked is not.
- **Does it change?** Complex code that nobody touched for a year costs nothing today. Weight a finding by how often its code changes and gets fixed. Stable and ugly stays, unless it is code that must be right when it is finally read: recovery, migration, what runs during an incident.
- **What if we do nothing?** State the cost of leaving it. When that cost is small, say so and grade the finding accordingly.
- **Can it land in steps?** Each step leaves the code working and could be the last. A change that only works as one large rewrite is not a recommendation.

## Not findings

- Style, formatting, naming taste: tools and conventions own them.
- A file or function that is "too long", with no evidence that its length hurts. One reason to change and few fixes: leave it.
- "More generic", "more flexible", "more testable", "more consistent with the pattern", with no defect or repeated change behind it.
- An interface, injection or event bus added for one implementation.
- A coverage percentage. Missing tests are a finding where a defect or a risky change points at them.
- A new document. A doc finding removes or rewrites; it adds only what users see and nothing explains.
- A different library, framework or language.
- A new check, lint rule or process step, unless the same mistake was made several times and the check is cheap. A guard is an addition too.

## Grades

| Grade | Means |
| --- | --- |
| strong | The driver verified it in the code, another analyst found or confirmed it, and it passes the tests above |
| worth exploring | The problem is real; the cure, its size or its risk is still open, and the first step answers it |
| speculative | Plausible, not proven. Shown so that it is not rediscovered, never in the order of work |

Confidence words in an analysis mean the same for every analyst: **verified** (I read the code or ran the command, and cite it), **likely** (the evidence points there, one check is missing, named), **hunch** (worth a look).

## Words

Write what happens, with its number. "Adding a field takes five files" and "fails 34 runs in 200", not "tightly coupled", "hard to maintain", "cleaner", "more robust". A sentence that would fit any codebase says nothing about this one.
