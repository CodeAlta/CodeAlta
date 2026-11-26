---
name: improve-codebase-health
description: Use this skill to review the health of a codebase and prepare the changes that improve it - what to delete, merge or simplify in its code and structure, tests that check the wrong thing or fail at random, docs that grew too long. The models the user chooses analyze alone and challenge each other; the result is a report under `.alta/report/` with evidence, an order of work and a first step per finding. Also to implement the findings of an earlier report.
---

# Improve codebase health

A healthy codebase is cheap to change: little code, few concepts, boundaries that hold, tests that guard what matters and fail only for a reason, docs a newcomer reads to the end. This skill finds what stands in the way, proves it, and prepares each change so that another session can make it.

Three rules shape every step:

1. **Subtract first.** Prefer deleting, merging and simplifying to adding. Every recommendation says what it removes and what it adds. See [judgment.md](judgment.md).
2. **Evidence or nothing.** A finding cites code, a number or the result of a command, and you checked it yourself.
3. **Confront.** Each model analyzes alone, then tries to refute the others. What they found independently is a signal; what they still dispute is shown, not averaged.

**Yours to decide:** where to look and in what order, how to split a large codebase, which lenses matter here, how to group findings into themes, how many findings the report keeps. **The user's to decide:** which models review, and what is done with the findings. **Fixed:** the three rules, the files of a report, the contract of `report.json`.

## Kinds of run

| The user asks for | Do |
| --- | --- |
| A health review (the default) | Steps 1 to 7 on the whole repository, or on the area they name |
| One concern: the tests, the docs, duplication, one module | The same steps, with only the reference file of that concern |
| A small request: one page to tighten, one test to repair | Do it with the rules of that reference file: no panel, no signals, no report |
| Implementing findings of a report | [landing.md](landing.md) |
| A follow-up after changes landed | Collect the signals again, compare with the baselines of the ledger, update it |

A review changes nothing in the project outside `.alta/report/`. Fixing while diagnosing hides what was found and skips the user's choice.

## Files of a report

```
.alta/report/
  ledger.md                        decisions and baselines that outlive a report
  yyyy-mm-dd-<name>/               <name>: health, tests, docs, <area>...
    brief.md                       scope, what hurts, constraints: the same text for every analyst
    signals/                       signals.json, signals.md, and any other raw evidence (CI runs, timings)
    analysis/<model>.md            what each analyst found alone
    analysis/candidates.md         every finding of every analyst, merged, without authors
    analysis/<model>.review.md     each analyst's verdicts on the candidates
    report.json                    the findings you kept: the one source of the report
    index.html, report.md          built from report.json, never edited by hand
```

You give each analyst its file name: the id of its model without provider and effort, with a suffix when two analysts would share it. A folder that exists is never reused: add `-2` to the name. Whether the folder is committed is the user's choice: never commit it yourself.

The scripts of this skill are .NET programs of one file each, run with the .NET SDK that CodeAlta runs on: `dotnet run <name>.cs -- <arguments>`. Run them with `<skill>/scripts` as the working folder, where `<skill>` is the folder of this file, and give them full paths: started inside the project, `dotnet` would follow the `global.json` of the project, which can name an SDK that does not run them. The first run of each compiles it.

## Steps

### 1. Frame

- Read the project's own rules first: agent instructions, architecture documents, decision records, contribution guide. They are decisions, not findings: a review reopens one only with new evidence, and says so.
- Read `.alta/report/ledger.md` and the latest report if they exist. Do not propose again what was rejected unless its reason no longer holds.
- **Ask the user which models review with you**, once, with `alta ask`: [panel.md](panel.md) has the question. The answer is a list of models, each with its provider and reasoning effort; an empty list means that your own model reviews alone. Do not ask when the request already names the models or says to work alone. Never choose other models yourself.
- Write `brief.md`: the scope, what the user said hurts, what is out of scope (by default: security, performance, formatting, a rewrite in another technology), the constraints of the project, and the commit the review reads. The analysts and the scripts read the working folder: when it has uncommitted changes, say so here and in the report, since a cited line may not be in that commit.
- Say in one message what you will do: scope, panel, number of sessions. Then proceed.

### 2. Collect signals

Objective numbers come before reading, so that every analyst starts from the same facts and attention goes where the code changes:

```sh
dotnet run signals.cs -- --repo <project> --out <project>/.alta/report/<run>/signals --since "12 months ago"
```

It ranks files, areas, tests and documents by what the history and the text say of them ([lenses.md](lenses.md) reads the lists). Choose `--since` so that the window holds about 500 to 2,000 commits (the script prints the count). Add `--scope <folder>` for an area and `--exclude <glob>` for what crowds the lists without being code people write. Add what the project offers: CI history, test timings, a coverage or duplication report, the dependency graph. Signals are leads, not findings.

### 3. Analyze, each model alone

Start a session for each model of the panel, then write your own analysis **before you read theirs**: [panel.md](panel.md) has the commands, the brief and the format of an analysis. Each analyst reads [judgment.md](judgment.md) and [lenses.md](lenses.md), then [tests.md](tests.md) or [docs.md](docs.md) when the scope has tests or docs worth a look, and chooses where to dig. A model that cannot be started is left out and named in the report; do not replace it by another.

### 4. Confront

Merge every finding of every analysis into `analysis/candidates.md`: duplicates joined, each candidate in the same short form, no author. Drop nothing at this point. Send it to the other analysts, in the sessions they already have, with the task of refuting each candidate. Up to 12 candidates, send them all. Beyond, name the ones to review: those found by one analyst only, those whose cure is disputed, the large or risky ones, and every one that adds something.

When you review alone, a fresh session of your own model does the refuting: it has not read the code your way, which is what the confrontation needs.

### 5. Verify and decide

You decide; a vote does not.

- Run `dotnet run check_refs.cs -- <project>/.alta/report/<run>/analysis/*.md`: a cited file or line that does not exist marks a claim to distrust.
- Open the code of every candidate you intend to keep. A question of fact is settled by reading or running, never by counting opinions.
- Apply [judgment.md](judgment.md) to each one, and grade what passes as it says: strong, worth exploring or speculative.
- Keep a disagreement about a trade-off as a disagreement, with both positions.
- What you drop goes to the list of dropped ideas with its reason, whoever raised it, yours included.

### 6. Write the report

Write `report.json` ([report.md](report.md) has the contract and the diagrams), then build:

```sh
dotnet run build_report.cs -- <project>/.alta/report/<run>
```

The script checks the contract and the cited files, and writes `index.html` (one self-contained file, auto, light and dark themes) and `report.md`. Correct what it refuses; resolve each warning or be able to justify it. About a dozen findings is what a reader acts on: the weakest go to the dropped ideas or wait for a later review.

The report follows the rules it applies to docs: verdict on the first screen, one level of detail per finding, a fact in every sentence.

### 7. Hand over

Show the report (`alta browser open <path>/index.html`, or its path) and give the verdict and the first three changes in a few lines. The user chooses what to do, defer or reject. Then, and only for what they chose: record the decisions in the ledger, and create the tasks or plans as [landing.md](landing.md) says.

The research behind these rules is in [sources.md](sources.md), for when a rule is disputed.
