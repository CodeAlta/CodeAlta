# The report

`report.json` is the one source: you write it, `build_report.cs` checks it and builds `index.html` (the template of the skill with the data inside) and `report.md`. Never edit the built files; change `report.json` and build again. A complete example is [assets/example/report.json](assets/example/report.json): copy its shape.

```sh
dotnet run build_report.cs -- <project>/.alta/report/<run>            # check and build, from <skill>/scripts
dotnet run build_report.cs -- <project>/.alta/report/<run> --check    # check only
```

## What the reader sees

| Layer | Content | Rule |
| --- | --- | --- |
| First screen | `verdict`, `metrics`, the first wave of `roadmap` as "Start here" | A reader who stops here knows the state and the first three changes |
| Overview | Impact and effort grid, findings per area and lens (both drawn from the findings) | Nothing to write |
| Findings | One line each, grouped in your `themes` | Title and claim are enough to decide whether to open |
| One finding, opened | Before and after, evidence, change, balance, first step, verification, risks | One level down, never a second |
| On demand | Dropped ideas, what is healthy, signals, the analyses, the method | Linked or folded |

## The report

| Field | Content |
| --- | --- |
| `title`, `repo`, `commit`, `date`, `scope` | What was reviewed and when; `commit` is the short hash the evidence was read at |
| `verdict` | Two or three sentences, 60 words at most: the state, where the cost is, what the findings change. No list of lenses, no praise |
| `metrics` | Up to five numbers that carry the verdict: `label`, `value`, optional `note` and `tone` (`good`, `warn`, `bad`). Measured today, never a forecast of what a change will bring; the next review measures them again. None is better than an invented one |
| `analysts` | `id`, `name`, `model` (as `provider:model@effort`), `role` (`driver`, `analyst`, or `challenger` for the fresh session of a review done alone), `analysis` and `review` (paths inside the report folder) |
| `themes` | Your grouping: `id`, `title`, optional `thesis` of one sentence, `findings` (ids in reading order). A theme names what its findings have in common ("One decision known in five places"), not a lens |
| `findings` | Below |
| `roadmap` | Waves in order: `title`, optional `note`, `items` of `{finding, note}`. The first wave is what to do now; a last wave "When this code is next changed" holds real problems in code that rarely changes. Every finding in it has a `firstStep`, and comes after the findings it depends on |
| `healthy` | `{title, note}`: what works and should be left alone |
| `dropped` | `{title, reason, raisedBy}`: candidates left out, with the reason. Feeds the ledger |
| `signals` | Optional rows copied from `signals.json`: `hotspots` (`path`, `commits`, `loc`, `fixes`, optional `tone`), `coupling`, a `note` |
| `method` | `text`: who analyzed what and how it was decided; `notExamined`: what the review did not look at |
| `sections` | Optional extra parts of the last section, `{title, text}`: the flaky tests of a test review, the documents by size of a docs review |
| `links` | `root`: path from the report folder to the repository root (`../../..`); `blob`: a URL prefix such as `https://github.com/org/repo/blob/<sha>/` to link files online instead |

## A finding

| Field | Content |
| --- | --- |
| `id` | `F01`… in the order of the themes |
| `title` | Names the change: "Merge the three shipment mappers into the repository" |
| `lens`, `area` | A few words each; the same spelling across findings, since the report groups by them |
| `action` | `delete`, `merge`, `simplify`, `move`, `fix`, `rewrite` (docs), `guard`, `add` |
| `strength` | `strong`, `worth-exploring`, `speculative` ([judgment.md](judgment.md)) |
| `impact`, `effort`, `risk` | `high`/`medium`/`low`; `S` (hours), `M` (days), `L` (a week or more); `low`/`medium`/`high` |
| `claim` | One sentence: who pays what today, with its number when there is one |
| `evidence` | Entries of `text`, with `file` and `line` (`"41"` or `"12-60"`) or the `command` that shows it |
| `change` | What to do, in plain words |
| `balance` | `removes` and `adds` (lists), optional `delta` with signed numbers: `lines`, `files`, `concepts`, `words`, `tests`, `dependencies`, `seconds` |
| `before`, `after` | A diagram each, or a list of diagrams (below) |
| `firstStep`, `steps` | The smallest change that lands alone; then the rest, each one leaving the code working |
| `verify` | Commands or numbers that show it worked |
| `risks`, `ifNot`, `outOfScope` | What can break; what it costs to leave it; what not to do while doing this |
| `votes` | Per analyst id: `found`, `confirmed`, `refined`, `disputed`, `unreviewed` |
| `dissent` | Who disagreed, on what, how it was settled or why it stays open |
| `dependsOn`, `files` | Findings to do first; the main files |
| `candidates` | The ids it comes from in `analysis/candidates.md`, to find what each analyst wrote |
| `status`, `task` | `proposed`, `accepted`, `deferred`, `rejected`, `done`; the task or plan that tracks it. Updated when the user decides |

Required: `id`, `title`, `lens`, `action`, `strength`, `impact`, `effort`, `claim`, `evidence`, `change`, `balance`. A strong finding also has evidence with a `file` or a `command`, `firstStep`, `verify`, and two analysts who found or confirmed it, the driver among them. A speculative finding is not in `roadmap`. The script refuses a report that breaks these rules and names the field. The report is data only: no field takes HTML, and the page loads nothing from the network. Text fields take `` `code` ``, `**bold**`, `[links](url)`, and lines that start with `- ` as a list.

## Diagrams

A before and after that shows the change beats a paragraph that describes it. Choose the type that fits; all follow the theme.

| `type` | Shows | Data |
| --- | --- | --- |
| `layers` | What a call passes through, top to bottom | `items` of `{label, note, tone}` |
| `flow` | A sequence in time: a test, a pipeline | the same |
| `boxes` | Modules side by side; `children` nests the parts a module absorbs | `items` of `{label, note, tone, children}` |
| `mass` | What a caller must know against what the module does for it | `items` of `{label, interface, implementation}` (relative sizes) |
| `bars` | Numbers: failures, lines, seconds, words | `items` of `{label, value, tone}`, optional `unit` |
| `text` | A passage of a document; the word count is added | `text` |
| `code` | A few lines of code | `code` |
| `edges` | Who depends on whom: a cycle, a dependency that points the wrong way | `items` of `{from, to, note, tone}` |

`tone` is `good`, `warn`, `bad`, `deep` (the module that owns the behavior after the change), `gone` (struck out) or `faded`. Add `note` to any diagram for a caption. Keep a diagram to what changes: six boxes read at a glance, twenty do not.

## The ledger

`.alta/report/ledger.md` holds what must survive the report: decisions and numbers. Create it with the first report and update it when the user decides and when a change lands.

```markdown
# Health ledger

## Decisions
| Date | Finding | Decision | Reason or result | Report |
| --- | --- | --- | --- | --- |
| 2026-03-14 | Merge the three shipment mappers | accepted, done 2026-03-20 (`a41c9e0`) | 1,870 lines removed | `2026-03-14-health` F03 |
| 2026-03-14 | Split `ShipmentService` into four | rejected | One reason to change, few fixes. Reopen if its fixes pass 10 a year | `2026-03-14-health` |

## Baselines
| Date | Commit | Measure | Value | How it was measured |
| --- | --- | --- | --- | --- |
| 2026-03-14 | `4f2a91c` | Runs of `main` whose first attempt failed in a test that passed in the second | 29 of 200 | `signals/ci-first-attempts.md` of `2026-03-14-health` |
```

A rejection records the reason and, when there is one, what would reopen it. The baselines are the `metrics` of each report, each with the command or the file that gave it and its denominator: the next review measures the same way, or says that the numbers do not compare.
