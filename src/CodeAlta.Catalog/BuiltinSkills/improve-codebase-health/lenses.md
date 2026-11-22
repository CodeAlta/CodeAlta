# Lenses: what to look for

Ways of looking, not a checklist to fill. Start from the signals and from what the user said hurts, follow the evidence, and skip a lens that shows no cost in this scope. Each lens says what a real problem looks like, how to find it cheaply, and what only looks like one. [judgment.md](judgment.md) decides what becomes a finding.

For tests see [tests.md](tests.md), and [flaky-tests.md](flaky-tests.md) when they fail at random; for docs, comments and agent instructions see [docs.md](docs.md).

## Where to look first

`signals.md` ranks the files and areas. Read it as a map of where change is expensive:

- **Hotspots** (changed often and large): where a simplification pays every week. Look at what the many changes had in common.
- **Fixed most often**: where the design lets defects in. Read five of the fix commits: the same kind of mistake repeated is a finding. A fix is counted from the subject of its commit: where the project's convention makes that word mean something else, give the script another pattern (`--fix-pattern <regex>`) or `--fix-pattern off`, and never report the count as a defect rate.
- **Change together across areas**: a decision known in several places. Either the areas are one, or something leaks. Read the commits first: code that changes with its own test or its own documentation is healthy.
- **Largest files**: a lead only together with one of the above.

False leads to set aside: generated files, lock files, translations and other append-only registries, snapshots, a file that many features merely register themselves in. Exclude them and run the script again when they crowd the lists.

Without the script:

```sh
git log --since="12 months ago" --no-merges --format= --name-only | sort | uniq -c | sort -rn | head -40   # changed most
git log --since="12 months ago" --no-merges -i --grep="fix\|bug\|revert" --format= --name-only | sort | uniq -c | sort -rn | head -30   # fixed most
git log --since="12 months ago" --no-merges --format="%h %s" -- <file>   # what the changes of a hotspot were
```

## Structure and boundaries

| Real problem | How it shows |
| --- | --- |
| A module that only passes through: its interface is about as large as what it does | Deletion test; callers that reach around it; a change repeated at each layer |
| A decision known in several modules (a format, an order of calls, a default) | Files of different areas that change together; the same literal or shape in several places; a fix applied in one place and missed in another |
| A boundary that leaks: callers must know the inside to call correctly | Calls that only work in a given order; flags that select internal behavior; a caller that repairs what the callee returns |
| A dependency that points the wrong way, or a cycle | The import graph of the language (`dotnet list reference`, `madge`, `go list`, `pydeps`, or grep of imports per folder); a low-level module that imports a feature |
| A file with several reasons to change | Its commits fall into unrelated groups; several authors of unrelated features collide in it |
| Two ways of doing one thing, one of them a migration never finished | The old and the new pattern side by side; "legacy", "old", "v2", "new" in names; a helper used by a shrinking set of callers |

Not a problem: a large module with one job and few fixes; a layering that differs from a textbook; folders you would have named otherwise.

To judge a boundary, ask what a caller must know to use the module correctly: types, but also order, error cases, configuration, timing. The less, for the more behavior, the better. A proposal that adds a module must leave that sum smaller.

## Duplication

Find it by what drifts, not by what looks alike. Leads: a fix whose message names one place while a twin exists (search the fixed lines elsewhere), copied blocks with small differences, parallel classes that grow the same members, the same constant or rule restated. A clone detector helps when one is at hand (`jscpd`, the IDE's, `dotnet` analyzers); searching a distinctive line of a hotspot finds most of what matters.

Real: three or more copies of one rule; two copies of which one was fixed and not the other; duplicated knowledge between code and configuration or between client and server that no test ties together.

Not a problem: two similar helpers in modules that evolve apart; set-up code repeated in tests that reads better in place; what the language forces.

The cure is the smallest that makes the rule exist once.

## Dead and speculative code

Leads: exports and public members without a caller (the compiler, `knip`, `ts-prune`, `vulture`, IDE analysis, or a grep of the name); settings, flags and parameters that no caller or user sets; compatibility paths for versions no longer supported; commented-out code; `TODO` and "for now" older than a year (`git blame`); dependencies nothing imports; extension points with one user.

Check before calling it dead: reflection, serialization, plugin discovery, public API used outside the repository, entry points named in configuration. Say which of these you ruled out.

## Complexity inside a module

Real, when it sits in a hotspot or a fix-dense file: special cases stacked on special cases, each from a fix; a workaround on top of a workaround, where the cause could be fixed instead; boolean parameters that switch behavior; state spread over fields that must agree, with no single place that keeps them so; errors handled three different ways in one flow; a function that needs a comment to say what each block does.

The cure is rarely "extract methods". Look for the missing idea that makes the cases one, the cause the workarounds hide, the state that can be derived instead of stored.

## Dependencies, build and the development loop

Count what slows every change: a build or test loop that takes minutes where seconds would do, steps done by hand, scripts that duplicate each other, a CI that fails for reasons unrelated to the change, several libraries for one job, a dependency kept for one function. Measure before claiming: time the loop, count the failed runs.
