---
title: Model Tiers and Fallback
---

# Model Tiers and Fallback

Instead of a model, a session can run on a tier: **Complex** for hard work, **Standard** for everyday work, and **Quick** for searches and simple tasks. Each tier is a list of models you choose, the preferred first. A session on a tier takes the first of them whose provider is ready.

When a provider runs out of quota or stays unavailable, the fallback can move a session to the next model of its tier, so the work goes on with another model or another account. When every model of the tier has reached its limit, the session can resume by itself once a limit resets.

## Set the tiers

In CodeAlta Desktop, open **Settings > Models**: the **Model tiers** section comes first.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-model-tiers.webp" alt="The Model tiers section of Settings > Models: the Complex, Standard and Quick tiers, each with its models in order and the model it gives now, and the switch of the fallback" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings > Models > Model tiers: the models of each tier in order, and the fallback.</figcaption>
</figure>

- Each row of a tier is a provider, a model and the reasoning efforts to use, the preferred first. **Add a model** adds a row, and its `×` removes it. An [agent of the ACP registry](model-providers/acp-agents.md) is a provider like the others, with the models it lists once tested.
- Drag a row by its grip to move it in its tier or into another tier. On the grip, `Alt+Up` and `Alt+Down` move it in its tier, `Alt+Left` and `Alt+Right` to the tier beside it.
- Click the model name of a row to choose another model: the row keeps its place, and the efforts the new model offers.
- The heading of a tier says the model it gives now, or **No model ready** when none of its providers is ready.
- A row whose provider is no longer configured is shown in red. A provider deleted in **Settings > Providers** leaves every tier by itself; a provider given another key keeps its rows under the new key.
- A provider that is only in some [spaces](spaces.md#providers-of-a-space) is named with them, as **Only in Work**: its models count only in the sessions of those spaces.

Every change is saved at once.

## Use a tier

Once tiers are set:

- the model list of the **Agent** chip below the prompt shows **Complex**, **Standard** and **Quick** first, each with the model it gives now. Choosing one puts the session on that tier (see [Agent prompt, model and permissions](workspace/prompt.md#agent-and-model));
- a new session starts on **Standard**, also one an agent creates without naming a model;
- agents give a tier to the sessions they create, **Quick** for a search and **Complex** for a hard problem, with `alta session create --tier quick`. The instructions of a session list the tiers, and `alta model resolve --tier` answers which model a tier gives now.

## Adaptive (experimental)

With **Adaptive**, CodeAlta chooses the model of each turn for you, from your tiers:

- the turns you send run on the tier of the turns, **Quick** unless you choose another;
- a turn that changed code gets one more message from CodeAlta, on the same model: check the change against each requirement of the request, with a test or a run for each rule, and fix what is missing. The timeline shows it as **Adaptive recheck**;
- a turn that ends in visible trouble (no answer, an answer that says it cannot use its tools though it called none, or a last test run that failed) continues once on the escalation tier, **Complex** unless you choose another. The timeline shows it as **Adaptive escalation**, with the reason and the model. The next turn runs on the tier of the turns again.

The model chip below the prompt says **Adaptive** and the model of the next turn. A test run counts as failed when the summary of the test runner says so (node, Jest, Vitest, Mocha, pytest, `dotnet test`, Go, Cargo), even when the command piped its output.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-adaptive.webp" alt="The Adaptive section of Settings > Models, tagged Experimental: Set up Adaptive turned on, turns on Quick, a turn in trouble continues on Complex, the recheck ticked, and how to choose the models" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings > Models > Adaptive: the tier of the turns, the tier a turn in trouble continues on, and the recheck.</figcaption>
</figure>

Set it up in **Settings > Models**, in the **Adaptive** section under the tiers: turn on **Set up Adaptive**, then choose **Turns run on**, **A turn in trouble continues on**, and whether to **Recheck each change of code against the request**. The tiers it names need models; the section says when one has none, and how to choose the models.

Then choose **Adaptive** in the model list of the **Agent** chip, after the tiers. While it is not set up, choosing it says so and offers **Open Settings**. Choosing a model or a tier yourself takes the session off Adaptive.

- Whether a turn changed code is read from git: CodeAlta compares the files of the project before and after the turn, so a turn that only edits documentation or commits what was there is not rechecked. Without git, it goes by the files the tool calls say they changed, which not every provider reports.
- Adaptive chooses the model of the turns you send. A prompt that comes from elsewhere (a reminder, a background job, another agent) runs on the model the session has.
- The turns run on a model of the tier that the provider of the session has; an escalation can move the session to another provider, and it comes back after.

Combinations tested in October 2026:

| Models from | Quick (turns) | Complex (on trouble) |
| --- | --- | --- |
| Anthropic (API or Claude Code) | Claude Haiku 5.5 | Claude Opus 5.5 |
| Codex | GPT-6.1 Terra | GPT-6.1 Sol |

On 51 hard tasks of real repositories (SWE-bench Pro), Haiku with the recheck and escalation to Opus solved 44 to 48 of them, against 50 for Opus alone, for 6 to 9% of the cost; the patches were judged close to those of Opus. GPT-6 Luna wrote clearly weaker code than GPT-6.1 Terra in the same tests, so Terra is the model of the turns for Codex. To compare other models by their results on coding tasks and their price, see independent leaderboards such as [Artificial Analysis](https://artificialanalysis.ai/).

## Tiers of a space

The tiers and the fallback of **Settings > Models** are the usual settings: those of the Default space and of every space that has none of its own.

A [space](spaces.md) can have tiers and a fallback of its own: in **Settings > Spaces**, open the space and set **Model tiers** to **Customize** in its **New sessions** section.

- The tiers start as a copy of the global tiers and replace them for the sessions of that space, also for a tier they leave empty.
- **Add a model** offers the providers of the space, so a provider you keep for that space alone, such as a work account, goes in its tiers. A row whose provider is not in the space says **Not in this space**.
- The fallback switch, its two boxes and the resume when the limit resets are the space's own. Until you change them there, the space follows the fallback of Settings > Models.

With **Use global**, the space has the usual settings again, and the section says what each global tier gives.

Wherever its tiers come from, a session never takes a model whose provider is not available in its space (see [Providers of a space](spaces.md#providers-of-a-space)).

## Fallback when a provider fails

The fallback is off until you turn on **Fall back to the next model of the tier when a provider fails**, in the **Model tiers** section or in the **New sessions** section of a space with tiers of its own. Its two boxes choose the failures it acts on, both by default:

- **Quota reached**: the quota, the usage limit or the credits of the account are used up, or the provider limits requests for more than a minute;
- **Provider unavailable**: the provider stays unavailable (outage, overload, network) after its own retries, or the account cannot use the model (unknown to the provider, or not in the plan of the account). For a model the account cannot use, the session goes on with the next other model of the tier, which can be another model of the same provider.

Any other failure stops the session: a sign-in problem, a tool or a test that fails, a conversation too long for the model, a run you stopped.

- A session on a model you chose yourself falls back along the tier where that model comes first.
- The fallback only uses the models you listed: listing a model of a provider is what allows CodeAlta to use that account.
- A provider whose limit is reached is skipped, with all its models, until the limit resets: at the time the provider gives, or 15 minutes later when it gives none.
- The session keeps its permission mode, or takes a narrower one when the new provider does not offer it: a fallback never lets a session do more without asking you.

When a session falls back:

- its timeline shows a **Model changed after a failure** card, with the model it left and the model it now runs on;
- a notification says so, with **Show** to open the session;
- the session receives a message from CodeAlta that asks the agent to continue the task, and to check what an interrupted command or edit already did before it runs it again;
- a session that delegated the work gets the answer of the run that continues, not the failure.

When no other model of the tier can take over, a warning card says so: choose another model, or send again once the limit resets. With **Resume when the limit resets** on, the session waits for the reset instead (below).

## Resume when the limit resets

Turn on **Resume when the limit resets**, under the fallback switch, and a session that stopped on a limit, with no other model of its tier to take over, goes on by itself when the earliest limit of its tier resets. It acts on quota and usage limits only, so it is offered while the fallback acts on **Quota reached**.

- The warning card says when and with which provider: **Resumes at 14:05 with Claude Code**. It offers **Resume now**, which tries the tier at once (useful once you bought credits or the limit reset early), and **Cancel**.
- The resume time is the reset time the provider gives, or 15 minutes later when it gives none.
- At that time, the session takes its tier again in order: the first model whose provider is available, the preferred one first. It receives the same message as after a fallback, which asks the agent to continue the task and to check what an interrupted command or edit already did. A **Session resumed** card says which model it took.
- If the limits are reached again, the next reset is waited for, at most the number of times in a row you set in **At most … times in a row** (5 by default). A prompt you send yourself starts the count again.
- A prompt sent to the session while it waits, by you or by an agent, cancels the resume: the session works on that prompt instead. So does any other run of the session meanwhile, such as the result of a background job or the message of a sub-agent. **Automatic resume cancelled** says so in the timeline.
- Only a run that stopped on a limit is resumed: a session that ended its task never is.
- A session that delegated the work waits as for a fallback: it gets the answer of the run that resumes, and hears of the failure only when the resume is cancelled or the resumes in a row are used up.
- A resume that waits is kept when CodeAlta closes: it starts at its time after CodeAlta starts again, or at once when that time has passed.

## In the file

The tiers and the fallback are the `[models]` table of `~/.alta/config.toml`. Each entry is `provider:model`, followed by `@` and the reasoning efforts in order; the provider is the key of a provider of the Providers page:

```toml
[models.tiers]
complex  = ["claude-code:opus@high,xhigh", "codex:gpt-6.1@xhigh", "copilot:claude-opus-4.6@high"]
standard = ["claude-code:opus@medium", "codex:gpt-6.1@medium"]
quick    = ["claude-code:haiku", "codex:gpt-6.1-mini@low"]

[models.fallback]
enabled = true
on = ["quota", "unavailable"]   # the failures to fall back on; both by default
resume = true                   # resume by itself when the limit resets; off by default
max_resumes = 5                 # automatic resumes in a row, from 1 to 50; 5 by default
```

The tiers of a space are in the file of the space, under `new_sessions.tiers`, written the same way, and its fallback under `new_sessions.fallback` (see [Files](spaces.md#files)):

```yaml
new_sessions:
  tiers:
    standard: ["work-claude:opus@medium", "codex:gpt-6.1@medium"]
  fallback: { enabled: true, on: [quota], resume: true, max_resumes: 3 }
```

A space without `tiers` takes the tiers and the fallback of `config.toml`; one with `tiers` and no `fallback` takes the fallback of `config.toml`.

Adaptive is the `[models.adaptive]` table, for every space: it takes its models from the tiers of the space of each session.

```toml
[models.adaptive]
code = "quick"       # the tier of the turns; quick by default
escalate = "complex" # the tier a turn in trouble continues on; complex by default
recheck = true       # recheck a turn that changed code; on by default
```

## CodeAlta TUI

CodeAlta TUI has no page for the tiers and does not offer them, nor Adaptive, in its model list. Its agents still use the tiers of the file with `alta session create --tier`.
