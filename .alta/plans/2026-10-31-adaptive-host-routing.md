---
title: Adaptive model choice by CodeAlta, turn by turn (#273)
status: draft
created: 2026-10-31
summary: An Adaptive choice of the model list where CodeAlta, not the agent, picks the tier of each user turn by its kind (code on a code tier, the rest on quick), escalates a turn that ends in visible trouble, and shows the model of each turn, as the evaluation of #273 found that agents do not adapt by themselves.
---

# Adaptive model choice by CodeAlta, turn by turn (#273)

Today a session runs on the model the user chose; the trial of #273 (`.alta/plans/2026-10-30-adaptive-mode.md`) measured whether an agent prompt can move it between the tiers. It cannot: across six prompts and more than 800 runs on Codex and the Anthropic API, the agents almost never changed their model, and when told to, did it for the wrong requests. What did work was the **host** choosing the model of each turn, and the best rule was not the difficulty of a request but its **kind**:

| Codex, 10 scenarios | Success | Cost of Sol | Quality (blind judge, 1–5) |
| --- | --- | --- | --- |
| Sol on every turn | ~100% | 100% | 4.4 |
| **Code on Terra (`standard`), the rest on Luna (`quick`)** | **18/20** | **~15%** | **4.6** |
| Luna on every turn | 23/30 | ~3% | 3.4 |

Questions, docs and commits are done well by the cheapest tier everywhere. Code is where the tiers differ, and how much depends on the provider: GPT-6 Luna writes clearly weaker code than Terra and Sol, while Haiku 5.5 writes code judged as good as Opus 5.5. A call of the cheap model that only says whether a request changes code is reliable; one that judges difficulty is not.

When this is done, the model list of the composer offers **Adaptive** beside the tiers. A session on Adaptive runs each user turn on the tier its kind calls for, shows on the turn which model ran it, continues a turn that ends in visible trouble on the next tier, and lets the user pin a model at any time.

## What changes

### 1. Adaptive, a choice of the model list

- The list of the **Agent** chip offers **Adaptive** after Complex, Standard and Quick, with the two models it uses now ("Luna · Terra"). A session on Adaptive records it as its model choice, like a tier.
- Settings > Models > **Adaptive**, under the tiers: the tier of code turns (`standard` by default; `quick` suits a family whose cheap model writes good code, `complex` when quality matters most) and the tier of the other turns (`quick` by default). In `config.toml`, `[models.adaptive] code = "standard"`, `other = "quick"`; a space can have its own.
- New sessions keep their current default (Standard); Adaptive is a choice, not the default, until the days of use (step 10 of the trial) say otherwise.

### 2. The router, before each user turn

- Before the run of a user prompt in an Adaptive session, one call of the `other` tier, without tools and without the conversation: the request (bounded), and the answer `code` or `other`. Its usage is counted with the session's, its own line in the timeline's details.
- The model of the turn is set as a model change between turns already is (`OwnedSessionSelection`); the tier recorded for the fallback follows it (`SetSessionTierAsync`), which `alta session send --model` does not do today.
- A turn's kind changes the model only when it differs from the last turn's: a run of questions stays on `quick`, a run of code turns on the code tier, so the conversation is re-read only at a change of kind (a switch writes the whole cache again, measured in the trial).
- `PluginBeforeAgentRunResult.ModelHint`, declared and never read, becomes how a plugin can override the choice for a turn.

### 3. Escalation on visible trouble

- After a turn on Adaptive, the host reads what it can see without knowing the task: the run ended without an answer, the answer refuses to use tools it did not call, or the last test command the session ran failed (its exit code). Then it continues the session on the next tier with a short message (the sign, and "check what was done, then finish the request"), shown as a card **Continued on Terra: the last test run failed**.
- At most one step up per turn, and the next user turn starts again on the tier of its kind.
- Later, if the evaluation shows it pays: the same signs during a turn (two failed repair-and-test cycles), through a hook of the run.

### 4. Shown and overridable

- The model of each turn on its prompt bubble ("Terra · code"), and the Adaptive choice in the composer.
- Choosing a model or a tier in the composer pins it: the session leaves Adaptive until the user chooses Adaptive again.

## Steps

### Milestone 1: the choice and the router

- [ ] 1. `[models.adaptive]` settings (catalog and spaces), their defaults and validation; tests.
- [ ] 2. The router service in orchestration: the classification call, its usage, and the choice of the model of the turn with the tier recorded for the fallback; tests with a fake provider.
- [ ] 3. Adaptive in the model list of the composer (desktop), the model on each prompt bubble, the pin; frontend tests; the user guide (`site/docs/model-tiers.md`, a section **Adaptive**).

### Milestone 2: escalation

- [ ] 4. The signs of trouble from the events of a run (the same as `_trouble` of `benchmarks/harbor/locked_codealta.py` and `Trouble` of `EvalScenarioRunner`), the continued turn and its card; tests.

### Milestone 3: measure the host, not the emulation

- [ ] 5. `altacli run --model-choice adaptive` (or the setting through `-c`), so that `CodeAlta.Evals` and the HARD-51 kit run the real feature; the grid of the trial on Codex and the Anthropic API, with the quality judge, against its numbers.
- [ ] 6. HARD-51 with Adaptive on the Anthropic API, against the run of CodeAlta on Opus 5.5.
- [ ] 7. A few days of use on CodeAlta's own work, with the user; each wrong choice noted.

## How it is verified

- [ ] A session on Adaptive answers a question on `quick` and makes a change on the code tier, and the bubbles say so.
- [ ] A turn whose last test run fails is continued on the next tier, with its card.
- [ ] The grid of Milestone 3 reproduces the emulated numbers within their variance.
- [ ] Every .NET test, `npm test` and `lunet build` pass.

## Decisions

| Decision | Choice | Why |
| --- | --- | --- |
| Who chooses the model | The host | No agent prompt moved a session reliably in the trial |
| What decides the tier | The kind of the turn: code or not | It separated the tiers on success and quality; difficulty, as judged by a model, did not |
| Code tier by default | `standard` | Keeps quality at Sol's level on Codex for ~15% of its cost; a family whose cheap model writes good code can set `quick` |
| Escalation | On visible trouble only, one step | Cheap insurance; the misses that matter pass their own tests |
| Reviews, acceptance tests by `complex` | Not in this plan | 3–5 times the cost for no more success in the trial |

## Risks

- **A wrong classification:** a code request routed to `quick` gets the cheap model's quality. The router answered right on the scenarios; the days of use check real requests, and the bubble shows the choice.
- **The blind spot of the code tier:** a bug worked around at its caller passes its own tests on Terra; only `complex` fixed it in the trial. A user who wants that safety sets the code tier to `complex`.
- **Synthetic scenarios:** the numbers come from small fixtures; HARD-51 and the days of use complement them.

## Evidence

The trial's plan has the numbers and their sources: `.alta/plans/2026-10-30-adaptive-mode.md`, Results; the harness is `src/CodeAlta.Evals` (`--scenarios`, the policies `router`, `split`, `escalate`, `recheck`, `review*`, `acceptance`, `--quality`).
