---
title: Statistics plugin
---

# Statistics plugin

The built-in statistics plugin shows how you use CodeAlta. It adds a card of statistics to each turn of a session, and CodeAlta Desktop also keeps the numbers of all your sessions, over time: sessions, runs, active time, prompts, models, tokens, tools, and the cost where a provider gives one.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-plugin-statistics.png" alt="CodeAlta timeline statistics card contributed by a plugin" loading="lazy">
  <figcaption class="small text-secondary mt-2">The card of a turn is a plugin-owned timeline projection replayed from session events.</figcaption>
</figure>

## What it contributes

- A statistics card for each turn and session in the timeline.
- In CodeAlta Desktop, the statistics of every session, kept up to date while sessions run and built from the history the first time.
- The `alta statistics` commands, which agents use to read the numbers and to estimate the size of a text.

The card is not stored as assistant or user content. The session keeps its own records, and the plugin derives the card from them.

## Open the statistics

In CodeAlta Desktop the numbers have a page of their own, in a tab. Open it from:

| Where | How |
| --- | --- |
| The title bar | The **Statistics** button at the top right, before the space switch. It shows a small ring while your history is being read, and a dot while it waits for you to choose how much to read |
| The keyboard | `Ctrl+G`, then `C` |
| The search | **Statistics**, or `/statistics` |
| A project | **Statistics** in the menu of a project row opens the page for that project only, in a tab of its own |
| An agent | `alta canvas open statistics` |

Each space has its own Statistics tab, which starts on the space it shows; a space counts its projects and your chats (the sessions of no project), which the Explorer lists in every space. CodeAlta TUI has no page: it keeps the card of each turn and the `alta statistics` commands.

## The statistics of all your sessions

CodeAlta Desktop reads your sessions once, in the background, and keeps small counts: how many runs, how long they took, which models and tools were used, how many tokens, and what was changed in files. It keeps **numbers only**. The text of your prompts, the answers and the tool results are never copied, and a file is only counted by its extension. The numbers stay on your computer, in the database of CodeAlta, and are kept after a session is deleted unless you ask for them to be forgotten.

The first time, nothing is read until you choose how much of your history to use. The page says how many sessions can be read and since when, and offers:

| Choice | What happens |
| --- | --- |
| Read all the history | Every session is read, the most recent first. It usually takes well under a minute |
| Last 90 days | Only the sessions with activity in that time are read, and the statistics start at that day |
| Start from today | Nothing of the past is read; the statistics begin with what happens from now on |

The most recent sessions are read first, so today and this week are right within seconds, and the older days follow. The reading runs on one thread at a low priority and never slows a session. You can pause it, resume it, stop it where it is, and later read more history. If CodeAlta closes meanwhile, nothing is lost: each session that was read is kept, and the reading goes on at the next start. Sessions written by CodeAlta TUI are caught up by CodeAlta Desktop, at its next start and every few minutes while it runs.

When a new version of CodeAlta computes more from your sessions, they are read again, one by one, and what you see stays until each session's new numbers are ready.

The page asks you the first time and has the buttons to pause, resume and stop. You can also choose, pause or read more from a prompt, for example:

```text
Read my statistics history for the last 90 days.
```

```text
Pause the reading of my statistics history, and tell me how far it got.
```

## The statistics page

The page shows the numbers of your sessions as dashboards, one tab for each question:

| Page | The question it answers |
| --- | --- |
| Overview | How much did I use CodeAlta, and on what? |
| Activity | When do I work with it, and how long do runs take? |
| Models | Which providers, models and efforts do I use, for how many tokens and how long? |
| Cost | What did it cost, where a cost is known? |
| Tools | Which tools do agents use, how often, how long, and how often do they fail? |
| Prompts | How much do I write, and how much do agents write to each other? |
| Agents | How much work is given to sub-agents, automations and reminders? |
| Code | How many files and lines changed? |
| Projects | Which projects and spaces take the work? |
| Sessions | The sessions of the period, in a table that sorts on any number and opens the session |
| Health | Errors, interrupted runs, compactions, how full the context gets |

One bar at the top sets the period (today, the last 7, 30 or 90 days, this month, last month, this year, all time, or two dates), the frequency (hour, day, week, month, year, or automatic), a comparison with the period before or the same period a year before, and the filters: space, project, provider, model, reasoning effort, who started the work and the kind of tool. A click on a bar, a legend entry or a row adds the filter, a click on a day of the calendar shows that day, and a drag on the strip under the main chart narrows the period. The page opens on the space the window shows, and removing the filter shows every space.

Every chart can be shown as a table with the same numbers, and the legends and menus work with the keyboard. A cost is shown for each unit, dollars or AI credits, never as one total, and an estimate from public prices, when it is offered, is a separate block marked as an estimate.

While the history is read, the page is already in use: the most recent days are right first, the part not read yet is hatched, and a bar under the controls shows how many sessions are read, the date reached and the time left, with **Pause**, **Resume** and **Stop here**. The menu of the page reads more history, forgets the sessions that were deleted, and resets the statistics after asking.

## Ask for the numbers

Agents read the numbers with the `alta statistics` commands. Ask in a prompt; the answer comes from the same numbers every page of CodeAlta uses:

```text
How much did I use CodeAlta this week compared with the week before? Give the tokens and the active time.
```

```text
Which tools take the most time in the CodeAlta project over the last 30 days?
```

```text
Show my tokens per week for the last 90 days, by model.
```

| Command | What it answers |
| --- | --- |
| `alta statistics summary` | The numbers of a period at a glance, with the change against the period before |
| `alta statistics series <metric>` | One metric over time, by hour, day, week, month or year, optionally by provider, model, project or tool |
| `alta statistics top <tools\|models\|projects\|sessions>` | A ranking by tokens, time or calls |
| `alta statistics details <list>` | The programs of the shell commands agents ran, the commands of `alta` they called, the kinds of files they changed |
| `alta statistics session <session>` | The numbers of one session, with its sub-agents when asked |
| `alta statistics status` | Whether the history is read, how far, and what is left |
| `alta statistics history ...` | Choose how much history to read, pause, resume, stop, or forget the sessions that were deleted |
| `alta statistics estimate <text>` | The size of a text in bytes and approximate tokens |

A period is `today`, `7d`, `30d`, `90d`, `week`, `month`, `last-month`, `year`, `all`, or two dates. Days are your local days, in your time zone. A cost is given for each unit, dollars or AI credits, and is never added across units: only some providers report a cost. A space filter uses the projects the space has today.

Every answer says whether the history is read for the period, so an agent can tell you that the numbers before a day are not complete yet. For the broader model of agent commands, see [Advanced Agent Workflows](../advanced-agent-workflows.md).

## Disable it

Disable the statistics, the cards and the commands with:

```toml
[plugins.statistics]
enabled = false
```

Nothing is read or updated while the plugin is disabled. The numbers that were kept stay in the database, and the plugin picks them up again when you turn it on.
