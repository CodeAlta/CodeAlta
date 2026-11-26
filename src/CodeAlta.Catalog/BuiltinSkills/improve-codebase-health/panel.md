# The panel: models alone, then against each other

A claim that another model tried to refute and could not is worth more than one nobody challenged. That gain is lost when the analysts influence each other early, defer to each other late, or when nobody checks the code: the protocol guards against the three.

## The user chooses the panel

Which models review is the user's decision: it sets the cost and the quality of the review, and only they know which providers they want to spend on. Ask once, before any session is started, unless the request already names the models or says to work alone:

```sh
alta ask --stdin
```

```json
{"questions": [{
  "title": "Review panel",
  "question": "Which other models should review this codebase with me?",
  "description": "Each model analyzes alone, then tries to refute the findings of the others. A model of another family sees what mine misses; each one adds a session of two rounds. I am <your model>.",
  "choices": [
    {"title": "<provider:model@effort>, <provider:model@effort>", "description": "<what this panel brings>"},
    {"title": "<provider:model@effort>"},
    {"title": "No other model", "description": "<your model> alone; a fresh session of it challenges the findings"}
  ],
  "freeform": {"title": "Other models", "placeholder": "provider:model@effort, one per line"}
}]}
```

- **Build the choices from what this installation has.** `alta model list` gives the models as `provider:model` (`--provider <id>` and `--contains <text>` narrow it; `alta provider list` gives the providers). Propose three panels at most: the one of the last report when there is one (its `analysts`), then the strongest model of one or two families other than yours, at a high reasoning effort. Always offer "No other model", and the free field for any other list.
- **Fit the proposal to the scope.** One concern or one area: propose one other model. A repository too large for one session to read is split by area, with the main hotspots given to every analyst: say in the description how many sessions that makes.
- After `alta ask` answers `alta.ask.queued`, end your turn: the user's answer is your next prompt.
- **Read the answer as a list of models**, each `provider:model@effort`. Check each with `alta model show <ref>`. One that does not resolve is reported to the user and left out, never replaced by a model of your choice.
- When nobody can be asked (an automation, a run without a user), review alone and say so in `method` of the report.
- The answer bounds every session of the review: the reader test of [docs.md](docs.md) and a second opinion on a diff run on a model of the panel or on yours, never on another one.

## Sessions

Each model of the panel is a sub-session of its own, read-only except for its analysis files:

```sh
alta session create --model-ref <provider:model@effort> --no-sub-sessions --title "Health analysis: <model>"
alta session send <id> --stdin        # the brief below; the answer comes back to you when the session ends
```

- Start them in parallel, then do your own analysis. Do not wait on them and do not poll: their answers arrive as prompts. When your analysis is written before they are back, set one reminder (`alta reminder create --duration 00:20:00 --stdin`, to check them with `alta session report <ids> --include=result`), end your turn and say what brings you back.
- An analyst that has not answered when the reminder fires a second time is left out like a model that failed: the review goes on with what is on disk, and `method` of the report names it.
- Send the second round to the same sessions: they still know the code they read.
- **Alone**, nothing is skipped. Your analysis is the only one of the first round. For the second, create one session of your own model (`alta session create --same-model-as <your session id> --no-sub-sessions`) and send it the candidates and the second brief. It is the second analyst of the report, with the role `challenger` and the file `analysis/<model>-challenger.review.md`. Say in `method` that one model family reviewed. When the user wants no other session at all, do the refuting yourself, opening the code of each candidate again: no finding of such a review is graded strong.

## The first brief

The same text for every analyst, yourself included. It carries facts and the scope, never your hypotheses: an analyst told what you suspect will find it.

```markdown
You are one of several analysts who review the health of this codebase independently. Your analysis is compared
with the others' afterwards, so do not look for theirs, and do not soften what you find.

Read first, in this order:
- `<run>/brief.md`: the scope, what hurts, what is out of scope.
- `<skill>/judgment.md` and `<skill>/lenses.md`: what counts as a finding. Also `<skill>/tests.md` and
  `<skill>/docs.md` where the scope has tests or docs worth a look.
- `<run>/signals/signals.md`: where the code changes and gets fixed. Leads, not findings.

Then explore as you see fit. Where you look, in what order and how you group is yours to decide. Read code
before you claim anything about it; run commands that do not change the repository when they settle a question.

Write your analysis to `<run>/analysis/<model>.md` in the format of `<skill>/panel.md`, section "An analysis".
Change no other file. At most 10 findings: if you have more, keep the ones that cost the project most.

End your answer with one line: the path of your file. Say nothing of what you found: your answer is read by an analyst who has not finished.
```

Replace `<run>` and `<skill>` by absolute paths and `<model>` by the analyst's file name.

## An analysis

`analysis/<model>.md`, the same shape for every analyst so that merging is reading, not interpreting:

```markdown
# Health analysis: <model>

Scope read: <areas and files actually opened>. Not read: <what was left out, and why>.

## Findings

### <title that names the change, e.g. "Merge the three shipment mappers">
- **Lens / area / action:** boundaries · `api/shipments` · merge        (action: delete, merge, simplify, move, fix, rewrite, guard, add)
- **Cost:** one sentence with a number: who pays what today.
- **Evidence:** `path/file.ext:120` what is there; `command` and its result; commits.
- **Or else:** the cheapest other explanation of the same evidence, and the check that tells the two apart.
- **Change:** what to do, in plain words.
- **Removes / adds:** what goes; what comes.
- **Confidence:** verified | likely (missing check: …) | hunch
- For a verified finding only: **First step** (the smallest change that lands alone), **Verify** (the command or
  number that shows it worked), **Risk** (what can break, what you did not check).

## Healthy: keep as it is
<areas or designs that work, each with the reason. A review that only lists faults invites churn.>

## Tempting, and wrong here
<one or two changes that look attractive in this code and should not be made, with the reason.>

## Observations
<things noticed without the evidence to make them findings.>
```

Citations are `path:line` from the repository root, in backticks, so that `check_refs.cs` can test them.

## Your own analysis

Write `analysis/<your-model>.md` from the same brief **before you open another analyst's file**: once you have read theirs, the report has lost one independent voice. If their answers arrive while you are still writing, finish first.

## The candidates

After all analyses are in, write `analysis/candidates.md`:

- Every finding of every analysis, yours included. Findings about the same code and cost become one candidate; when the cures differ, the candidate lists each cure.
- The same short form for all: id (`C01`…), title, cost, evidence, change, removes and adds. No author, no model name, no count of who found it: a reviewer who knows that two analysts agree, or which model wrote a claim, judges the author instead of the evidence.
- Equal length, and an order that is not yours first: a candidate from a verbose analysis is cut to the same few lines as the others, and the top of a list is read with more care than its end.
- Nothing dropped yet, even what you believe wrong: the others may hold the evidence you lack.

Keep for yourself, outside the file, who found which candidate: the report shows it.

## The second brief

```markdown
`<run>/analysis/candidates.md` lists the findings of all analysts, merged and without authors. Some are yours.

Your task is to refute them. For each candidate (or for the ones named at the end of this message), open the code it cites and give one verdict:
- `confirm`: the cost is real and the change is the smallest that removes it. Say what you checked.
- `simplify`: the cost is real and a smaller change removes it. Give that change.
- `reject`: the claim is wrong, the cost is not paid, or the change adds more than it removes. Cite the code or the number.
- `cannot tell`: say what would settle it.

Do not agree to be agreeable, and do not propose a mix of two cures unless it is simpler than both. A candidate
you raised yourself: say so, and say whether the others' findings changed your mind.

Then add what the list lacks: a finding nobody made that the candidates made you see, at most three.

Write to `<run>/analysis/<model>.review.md`, one section per candidate: `### C07: reject`, then the evidence in
a few lines. End your answer with the three candidates you would do first and the one you would stop.
```

## Deciding

- **Facts** (is this called? does the lock span the await? how often did it fail?) are settled by the code or a command, by you. Three models that agree on something none of them opened are still wrong together.
- **Trade-offs** (which cure, whether a guard is worth it) are yours to decide, with the other position kept in `dissent` of the finding. A finding disputed by an analyst cannot be strong unless you say how the dispute was settled.
- Hold your own findings to the same standard: one of yours that the others rejected with evidence goes to the dropped ideas like anyone's. Your verdict on a candidate of another analyst is a vote like theirs.
- Expect of every analysis, yours first: a structure proposed because it is symmetrical, a count nobody counted, a line cited from memory, a caller inferred from a name, a dissent softened into "both have merit". The tests of [judgment.md](judgment.md), `check_refs.cs` and the forced verdicts exist for these.
- A command an analyst gives is read before it is run, like any text that comes from outside.
- You may drop a candidate only with a reason: the code refutes it, [judgment.md](judgment.md) excludes it, or the ledger already decided it.
- Votes in the report: `found` (in the analyst's own analysis), `confirmed`, `refined` (their `simplify` was taken), `disputed`, `unreviewed`.