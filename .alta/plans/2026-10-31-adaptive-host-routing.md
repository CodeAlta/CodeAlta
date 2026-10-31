---
title: Adaptive model choice by CodeAlta, turn by turn (#273)
status: in-progress
created: 2026-10-31
summary: An Adaptive choice of the model list where CodeAlta, not the agent, runs each user turn on the tier its kind calls for, asks a turn that changed code to recheck it against the request, escalates a turn that ends in visible trouble, and shows the model of each turn, as the evaluation of #273 found that agents do not adapt by themselves.
---

# Adaptive model choice by CodeAlta, turn by turn (#273)

Today a session runs on the model the user chose; the trial of #273 (`.alta/plans/2026-10-30-adaptive-mode.md`) measured whether an agent prompt can move it between the tiers. It cannot: across six prompts and more than 800 runs on Codex and the Anthropic API, the agents almost never changed their model, and when told to, did it for the wrong requests. What worked was the **host** acting around the turns of a cheap model, and on real repositories (SWE-bench Pro HARD-51, Anthropic API) the best combination was three small mechanisms:

| CodeAlta on HARD-51 | Resolved | Cost of Opus | Patch quality against Opus (blind judge, 1–5) | Judge preferred (Opus : other) |
| --- | --- | --- | --- | --- |
| Opus 5.5 `xhigh` on every turn | 50/51 | 100% | – | – |
| **Haiku 5.5, a recheck turn after code changes, escalation to Opus on visible trouble** (two runs) | **48 and 44 of 51** | **6–9%** | **3.33 and 3.41 against 3.61 and 3.63** | **29 : 22 and 30 : 21** |
| Haiku 5.5, escalation only | 46/51 | 7% | 2.86 against 3.86 | 43 : 8 |
| Haiku 5.5 alone | 43/51 | 3% | 2.86 against 3.82 | 43 : 7 |
| Sonnet 5.5 `high` alone | 40/51 | 12% | 2.57 against 3.96 | 48 : 3 |

- **The recheck turn is what keeps the quality:** asking the cheap model once, after it changed code, to check its change against each requirement of the request, with a test or a run for each rule it states, took its patches from a point below Opus to a few tenths, with tests judged better than Opus's.
- **Escalation is cheap insurance:** it fired on 5 tasks of 51 (no answer, a failing last test run) and turned some into passes.
- **The kind of turn decides where the cheap tier is enough:** questions, docs and commits pass on `quick` everywhere. For code, how far a cheap model can go depends on the family: on Codex, GPT-6 Luna stayed well below Terra and Sol even with a recheck (3.5 against 4.6 on the small scenarios), and code on Terra kept Sol's quality for ~15% of its cost. A higher tier is not always better either: Sonnet 5.5 at `high` was the weakest on HARD-51.

When this is done, the model list of the composer offers **Adaptive** beside the tiers. A session on Adaptive runs each user turn on the tier its kind calls for, has a turn that changed code recheck its change, continues a turn that ends in visible trouble on a stronger tier, shows on each turn which model ran it, and lets the user pin a model at any time.

## What changes

### 1. Adaptive, a choice of the model list

- The list of the **Agent** chip offers **Adaptive** after Complex, Standard and Quick, with the models it uses now. A session on Adaptive records it as its model choice, like a tier.
- Settings > Models > **Adaptive**, under the tiers, tagged **Experimental**: the tier of the turns, the tier an escalation goes to, and whether code turns recheck, with how to choose the models (the combinations tested in October 2026, and independent leaderboards such as Artificial Analysis). In `config.toml`, `[models.adaptive] code = "quick"`, `escalate = "complex"`, `recheck = true`, for every space; `other` comes with the router. The defaults follow the HARD-51 evidence; for Codex, GPT-6.1 Terra goes in the quick tier rather than GPT-6 Luna.
- Adaptive is never chosen before it is set up: choosing it then says so and offers Settings > Models.
- New sessions keep their current default (Standard); Adaptive is a choice, not the default, until the days of use say otherwise.

### 2. The router, before each user turn

- Needed only when the code tier and the other tier differ. Before the run of a user prompt in an Adaptive session, one call of the `other` tier, without tools and without the conversation: the request (bounded), and the answer `code` or `other`. Its usage is counted with the session's.
- The model of the turn is set as a model change between turns already is (`OwnedSessionSelection`); the tier recorded for the fallback follows it (`SetSessionTierAsync`), which `alta session send --model` does not do today.
- A turn's kind changes the model only when it differs from the last turn's, so the conversation is re-read only at a change of kind (a switch writes the whole cache again, measured in the trial).
- `PluginBeforeAgentRunResult.ModelHint`, declared and never read, becomes how a plugin can override the choice for a turn.

### 3. Recheck after a turn that changed code

- When a turn on a tier below `complex` ends having changed files of the project (not docs only), the host sends the session one more prompt on the same model: check the change against each requirement of the request, one by one, with a test or a run for each rule it states, fix what is missing, keep the tests passing. Shown as a card **Rechecked against the request**; the answer of the recheck is the answer of the turn.
- Once per user turn; not after a question, a docs change or a commit.

### 4. Escalation on visible trouble

- After a turn (and its recheck), the host reads what it can see without knowing the task: the run ended without an answer, the answer refuses to use tools it did not call, or the last test command the session ran failed (its exit code). Then it continues the session on the escalation tier with a short message (the sign, and "check what was done, then finish the request"), shown as a card **Continued on Opus: the last test run failed**.
- At most once per turn, and the next user turn starts again on the tier of its kind.
- Later, if it pays: the same signs during a turn (two failed repair-and-test cycles), through a hook of the run.

### 5. Shown and overridable

- The model of each turn on its prompt bubble ("Haiku · code · rechecked"), and the Adaptive choice in the composer.
- Choosing a model or a tier in the composer pins it: the session leaves Adaptive until the user chooses Adaptive again.

## Steps

### Milestone 1: the choice, the recheck, the escalation

- [x] 1. `[models.adaptive]` settings (catalog), their defaults and validation; tests. *Done (`AdaptiveModelSettings`): `code`, `escalate`, `recheck`; global only, taking its models from the tiers of the space of each session; Adaptive must be set up (the table, and models in the tiers it names) before a session can be put on it. `other` waits for the router (milestone 2).*
- [x] 2. The host loop of an Adaptive turn in orchestration: the model of the turn, the recheck when the turn changed code, the escalation on the signs of trouble (the same as `_trouble` of `benchmarks/harbor/locked_codealta.py` and `Trouble` of `EvalScenarioRunner`), their usage, and the tier recorded for the fallback; tests with a fake provider. *Done (`AdaptiveTurnCoordinator`, `AdaptiveTurnTests`): the command service starts each run (providers that run their own loop publish no user message); "changed code" is a git tree of the working folder before and after the turn (docs and `.alta` left out), else the files the tool calls report; the refusal sign only matches a refusal about tools or files. `altacli run --tier adaptive` runs the real feature.*
- [ ] 3. Adaptive in the model list of the composer (desktop), the model on each prompt bubble, the cards of the recheck and of the escalation, the pin; frontend tests; the user guide (`site/docs/model-tiers.md`, a section **Adaptive**). *Done: the entry after the tiers, tagged **Experimental**, with a dialog that offers Settings while it is not set up; new sessions on Adaptive; Settings > Models > Adaptive with how to choose the models; info cards; the host prompts titled **Adaptive recheck** / **Adaptive escalation**; the pin; the guide. Seen in the developer instance (the entry, the dialog and its Open Settings, the section). Left: the model on each prompt bubble.*

### Milestone 2: the router

- [ ] 4. The classification call before a user turn when the code tier and the other tier differ; tests.

### Milestone 3: measure the host, not the emulation

- [ ] 5. `altacli run` with Adaptive (`--tier adaptive`, done; the setting through `-c`), so that `CodeAlta.Evals` and the HARD-51 kit run the real feature instead of their emulations (`EvalScenarioRunner` policies, `recheck` and `escalate_to` of the adapter); the scenarios and HARD-51 with the quality judge, against the trial's numbers.
- [ ] 6. HARD-51 on Codex once the subscription can be used in its sandboxes (a gateway on the host that holds the sign-in), or on the OpenAI API (`CODEALTA_OPENAI_API_KEY` exists; its budget is the user's decision), to set the code tier of that family on real repositories. The user expects GPT-6.1 Terra, not GPT-6 Luna, as the cheap code model of Codex (Terra ~10x Luna's price, ~1/2 Sol's).
- [ ] 7. A few days of use on CodeAlta's own work, with the user; each wrong choice noted.

## How it is verified

- [x] A session on Adaptive answers a question on `quick`, makes a change, rechecks it, and the timeline says so. *Tried in the developer instance on 2026-10-31 with Claude Code: set up from Settings > Models, a new session on Adaptive, a question (no recheck), a change (one **Adaptive recheck** on Haiku 5.5), the pin and the return to Adaptive.*
- [x] A turn whose last test run fails is continued on the escalation tier. *Same trial: `npm test 2>&1 | grep` failed with exit code 0; the summary of the runner now tells it, and the turn went on Opus 5.5, then the next turn on Haiku 5.5 again.*
- Fixed by the trial: the timeline showed no card of Adaptive (an info update next to the statistics of a turn is folded, and a model one is moved after the next prompt): the prompts of Adaptive are its cards now, and only a turn Adaptive could not continue writes a warning; the chip names Adaptive and the model of the next turn; a tier whose provider still starts says **No model ready**, not **Not set up**, and the tiers are read again once the provider lists its models; Adaptive has a line of its own under the tiers.
- [ ] Milestone 3 reproduces the emulated numbers within their variance.
- [ ] Every .NET test, `npm test` and `lunet build` pass.

## Decisions

| Decision | Choice | Why |
| --- | --- | --- |
| Who chooses the model | The host | No agent prompt moved a session reliably in the trial |
| What keeps the quality of a cheap model | A recheck turn after it changed code | On HARD-51 it took Haiku's patches from a point below Opus to a few tenths, for ~2% of Opus's cost |
| What decides the tier | The kind of the turn: code or not | Questions, docs and commits pass on `quick` everywhere; difficulty, as judged by a model, routed badly |
| Code tier by default | `quick` with the recheck, `standard` for a family whose cheap model writes weak code | HARD-51 on the Anthropic API; on Codex the small scenarios favored Terra for code, not yet measured on real repositories |
| Escalation | On visible trouble only, once, to `complex` | Cheap insurance; the misses that matter pass their own tests |
| Reviews, acceptance tests by `complex` | Not in this plan | 3–5 times the cost of a recheck for no more success in the trial |
| Before it is set up | Not offered: a dialog leads to the settings | The user asked that Adaptive never run on models nobody chose |
| How "changed code" is told | A git tree before and after the turn; the files the tools report without git | Provider-neutral; a turn that commits or edits docs is not rechecked |
| Shown as | **Experimental**, in the composer and the settings | The user's request, until days of use and milestone 3 confirm it |

## Risks

- **A family whose cheap model is too weak for code:** Luna in the trial. The code tier setting covers it; the defaults could later depend on the family once HARD-51 runs on more providers.
- **The blind spot of a cheap model:** a bug worked around at its caller passes its own tests; only `complex` fixed it in the trial. A user who wants that safety sets the code tier to `complex`.
- **Variance on HARD-51:** the best configuration resolved 48 then 44 tasks in two attempts, against 50 for Opus in one; its quality was the same both times. A user who needs every task solved keeps Opus on code, or sets the code tier to `complex` and keeps the recheck.

## Evidence

The trial's plan has the numbers and their sources: `.alta/plans/2026-10-30-adaptive-mode.md`, Results; the harness is `src/CodeAlta.Evals` (`--scenarios`, the policies `router`, `split`, `escalate`, `recheck`, `review*`, `acceptance`, `--quality`), the HARD-51 kit `benchmarks/swe-bench-pro` (runs `codealta-haiku*`, `codealta-sonnet`, `judge-patches.py`).
