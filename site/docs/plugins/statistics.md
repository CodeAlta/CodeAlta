---
title: Statistics plugin
---

# Statistics plugin

The built-in Statistics plugin shows how you use CodeAlta. It adds a statistics card to each turn, and CodeAlta Desktop keeps the numbers of all your sessions over time: sessions, runs, active time, prompts, models, tokens, tools, and the cost where a provider gives one.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-statistics-overview.webp" alt="The Statistics tab of CodeAlta Desktop on its Overview page: the period and the filters at the top, tiles for sessions, runs, active time, prompts, tokens and cost, the active time of each week by project, and a calendar of the year" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Overview of the last 90 days for a space of open-source projects.</figcaption>
</figure>

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-plugin-statistics.png" alt="CodeAlta timeline statistics card contributed by a plugin" loading="lazy">
  <figcaption class="small text-secondary mt-2">The card of a turn is a plugin-owned timeline projection replayed from session events.</figcaption>
</figure>

## What it adds

- A statistics card for each turn and session in the timeline. The card is derived from the session's records, not stored in the conversation.
- In CodeAlta Desktop, the Statistics page with the numbers of every session, kept up to date as sessions run.
- The `alta statistics` commands, which agents use to read the numbers and estimate the size of a text.

CodeAlta TUI has the cards and the commands, not the page.

## Open the statistics

In CodeAlta Desktop the Statistics page opens in a tab:

| Where | How |
| --- | --- |
| Title bar | The **Statistics** button, before the space switch. A small ring while history is read; a dot while it waits for your choice |
| Keyboard | `Ctrl+G`, then `C` |
| Search | **Statistics**, or `/statistics` |
| A project | **Statistics of this project** in the project's menu: that project only, in its own tab |
| A session | **Statistics of this session** in the session's menu, or **Session statistics** at the foot of a turn card's details: that session and its sub-agents |
| An agent | `alta canvas open statistics` |
| Welcome page | The **Statistics** card shows the last seven days; **Open Statistics** opens the page |

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-statistics-session-menu.webp" alt="The menu of a Markdig session in CodeAlta Desktop, with Statistics of this session below its session actions" loading="lazy">
  <figcaption class="small text-secondary mt-2">Open the statistics of one session and its sub-agents straight from the Explorer.</figcaption>
</figure>

- Each space has its own Statistics tab. A space counts its projects and its own chats.
- A session's page covers its whole life, with the sessions it created. Its name in the bar opens the session; **Reset** comes back to it after filtering.
- Open tabs refresh every **10 minutes** by default. **Refresh statistics** updates now; **Refresh every…** in the Statistics menu chooses 1, 5, 10, 30 or 60 minutes, per tab. Recording never waits for a refresh.
- **Last data update**, above the Overview tiles, is the time of the stored data shown (**Unavailable** when unknown).

## Reading your history

CodeAlta Desktop reads your sessions once, in the background, and keeps small counts: runs and their duration, models, tools, tokens, and file changes.

**What is kept.** Numbers, and a few names to tell them apart: providers, models, tools, skills, MCP tools, scripts and session titles. Never the text of prompts, answers or tool results. A file is counted by its extension; a command by its program name, never its arguments. A title is the first sentence of the first prompt, at most 80 characters, without words holding `/` or `\`. The numbers stay on your computer, and remain after a session is deleted.

**The first time**, nothing is read until you choose:

| Choice | What happens |
| --- | --- |
| Read all the history | Every session, the most recent first. Usually well under a minute |
| Last 90 days | Sessions active in that time; the statistics start that day |
| Start from today | Nothing of the past; the statistics start now |

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-statistics-history.webp" alt="The first card of the Statistics tab: the number of sessions that can be read and since when, with the buttons Read all the history, Last 90 days and Start from today" loading="lazy">
  <figcaption class="small text-secondary mt-2">The first time, the page asks how much of your history to read.</figcaption>
</figure>

- Recent sessions come first, so today and this week are right within seconds.
- Reading runs on one low-priority thread and never slows a session. You can pause, resume, stop, and later read more.
- If CodeAlta closes, reading resumes at the next start; nothing read is lost.
- Sessions of CodeAlta TUI are caught up at start and every few minutes.
- When a new version computes more, sessions are read again one by one, and the old numbers stay until the new ones are ready. A deleted session keeps its recorded values.
- Coverage says when older sessions did not record a metric, rather than showing zero.

You can also ask from a prompt:

```text
Read my statistics history for the last 90 days.
```

```text
Pause the reading of my statistics history, and tell me how far it got.
```

## Saved statistics

The facts of deleted sessions are kept in a separate archive, `saved_statistics_after_deleted_sessions`, in the state folder (`~/.alta/`, or `~/.alta/dev/` for a developer instance), beside a `saved_statistics_after_deleted_sessions.initialized` file.

- **Reset statistics** keeps this archive: it clears the reading choice and rebuilds the charts from saved facts.
- **Forget deleted sessions** removes only facts not yet saved in the archive. There is no button to purge the archive.
- Keep the folder and the `.initialized` file in your backups, and never delete the file to work around a problem.
- If the archive is missing, damaged or not writable, CodeAlta refuses to reset statistics or to allow automatic session cleanup until it is repaired.

## The Statistics page

One tab for each question:

| Page | The question it answers |
| --- | --- |
| Overview | How much did I use CodeAlta, and on what? |
| Activity | When do I work, how long do runs take, how many sessions run at once? |
| Models | Which providers, models and efforts, for how many tokens, and how full does the context get? |
| Cost | What did it cost, where a cost is known? |
| Tools | Which tools, skills and MCP tools are used, how long do they take, how often do they fail? |
| Prompts | How much do I write, and how much do agents write to each other? |
| Agents | How much work goes to sub-agents, how deep, and what do automations, reminders and CodeAlta itself start? |
| Scripts | How much work runs as [Code Mode](../sessions/code-mode.md) scripts, which scripts, how they end, and what their sessions cost? |
| Code | How many files and lines changed? |
| Projects | Which projects and spaces take the work? |
| Sessions | The sessions of the period, sortable, each opening its session |
| Health | Errors, interrupted runs, compactions, context fill |

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-statistics-activity.webp" alt="The Activity page of the Statistics tab: sessions started and active for each week, runs by outcome, active time, and a grid of the hours of each day of the week" loading="lazy">
  <figcaption class="small text-secondary mt-2">Activity: sessions, runs by outcome, active time, and when in the week the work happens.</figcaption>
</figure>

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-statistics-models.webp" alt="The Models page of the Statistics tab: tokens for each week by model, what the tokens of each model are made of, and the share of cached input over time" loading="lazy">
  <figcaption class="small text-secondary mt-2">Models: tokens by model, fresh and cached input, output, and how much the cache is used.</figcaption>
</figure>

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-statistics-tools.webp" alt="The Tools page of the Statistics tab: tool calls for each week by kind of tool, and a table of the tools with their calls, failures, total time, median and 90th percentile" loading="lazy">
  <figcaption class="small text-secondary mt-2">Tools: calls by kind, then each tool with its failures and how long it takes.</figcaption>
</figure>

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-statistics-tools-time.webp" alt="The end of the Tools page: the time of each tool as areas, the duration of the most called tools on a scale from a millisecond to hours, the programs run in the shell, the alta commands, and the tools of the MCP servers" loading="lazy">
  <figcaption class="small text-secondary mt-2">Tools, further down: where the time goes, how long one call takes, the programs of the shell, the <code>alta</code> commands and the MCP servers.</figcaption>
</figure>

At the end of the Tools page, **Skills** lists each skill used in the period: its origin (project, yours, CodeAlta, a plugin, Claude Code, or `MCP · <server>`), its activations, the prompts it was attached to, the reads of its `SKILL.md`, where it was used and when last. **MCP servers** counts every MCP tool call, including those through `alta mcp tool call`. The skills used most recently are listed first in session prompts (see [Skills](../workspace/settings.md#skills-management)).

The **Scripts** page counts the runs of [Code Mode](../sessions/code-mode.md) scripts:

- A run is named by the library script it loads with `workflow("name")`. **Inline scripts** ran only the code sent to `exec`; **Unknown scripts** ran before CodeAlta recorded which script it was.
- The table gives each script its runs, failures, median and 90th-percentile duration, the sessions it started, and the tokens and cost of those sessions.
- **Failing steps** and **Why runs fail** name the step and the error. Only names are kept, never a message.
- **Waiting for you** is the time scripts waited for your answer or approval.
- **Scripts by model** and **A turn, with a script and without** compare the turns that sent a script with the others.
- **Recent runs** opens the session of a run.

The Tools page counts `exec` and `wait` as **Scripts** and shows how many calls of each tool scripts made. The Agents page shows who started the sessions, scripts included.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-statistics-projects.webp" alt="The Projects page of the Statistics tab: a table of the projects with their sessions, runs, active time, tokens, tool calls and cost, and a line of their activity" loading="lazy">
  <figcaption class="small text-secondary mt-2">Projects: what each project took, sorted on any column.</figcaption>
</figure>

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-statistics-sessions.webp" alt="The Sessions page of the Statistics tab: a table of sessions with their project, model and provider, runs, active time, tokens, tool calls, sub-agents and last activity" loading="lazy">
  <figcaption class="small text-secondary mt-2">Sessions: a click on a title opens the session.</figcaption>
</figure>

The bar at the top sets:

- the period: today, the last 7, 30 or 90 days, this month, last month, this year, all time, or two dates;
- the frequency: hour, day, week, month, year, or automatic;
- a comparison with the period before, or the same period a year before;
- filters: space, project, provider, model, reasoning effort, who started the work, kind of tool.

A click on a bar, legend entry or row adds a filter; a click on a calendar day shows that day; a drag on the strip under the main chart narrows the period. The page opens on the window's space; removing that filter shows all spaces.

<figure class="alta-figure my-4" style="max-width: 52rem;">
  <img src="{{site.basepath}}/img/alta-desktop-statistics-filters.webp" alt="The bar of the Statistics tab with the period, the frequency, the comparison and a filter on a space, and the Filter menu open: Project, Provider, Model, Reasoning effort, Started by, Kind of tool" loading="lazy">
  <figcaption class="small text-secondary mt-2">The bar of the page, with the menu that adds a filter.</figcaption>
</figure>

- Every chart can be shown as a table, and legends and menus work with the keyboard.
- Costs are shown per unit (dollars or AI credits), never added together. An estimate from public prices is a separate block, marked as such.
- Context fill shows a measured 0%; a gap means no window size was reported. When some parents are unknown, sub-agent depths read **at least**.

While history is read, the page is usable: recent days come first, unread days are hatched, and a bar shows progress and time left, with **Pause**, **Resume** and **Stop here**. The page menu reads more history, forgets deleted sessions, and resets the statistics after asking.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-statistics-history-reading.webp" alt="The Statistics tab while the history is read: a bar says 20 of 137 sessions are read, back to which day and how long is left, with a Pause button, and the days not read yet are hatched in the chart" loading="lazy">
  <figcaption class="small text-secondary mt-2">While the history is read: the progress, <strong>Pause</strong>, and the days not read yet.</figcaption>
</figure>

## Ask an agent

Agents read the same numbers with `alta statistics`:

```text
How much did I use CodeAlta this week compared with the week before? Give the tokens and the active time.
```

```text
Which tools take the most time in the CodeAlta project over the last 30 days?
```

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-canvas-agent.webp" alt="A session of CodeAlta Desktop where an agent ran alta canvas list, alta canvas open, alta canvas show and alta statistics summary, and answered with the numbers of the last 7 days in a table" loading="lazy">
  <figcaption class="small text-secondary mt-2">An agent reads a canvas and the statistics with <code>alta</code>, and answers with the numbers.</figcaption>
</figure>

| Command | What it answers |
| --- | --- |
| `alta statistics summary` | A period at a glance, compared with the period before |
| `alta statistics series <metric>` | One metric over time, optionally by provider, model, project or tool. Includes `sessions-at-once` and `context-fill` |
| `alta statistics top <tools\|models\|projects\|sessions>` | A ranking by tokens, time or calls |
| `alta statistics details <list>` | Shell programs, `alta` commands, file kinds changed, `sub-agent-depth`, `prompt-provenance` |
| `alta statistics skills` | Skills used, with their uses and sessions |
| `alta statistics mcp` | MCP tools called, and calls per server |
| `alta statistics scripts` | Runs of Code Mode scripts: outcomes, each script, adoption by model, latest runs. `details script-step-failed` and `details script-error` name what failed |
| `alta statistics session <session>` | One session, with its sub-agents when asked. `--session` (and `--with-children`) works on `summary`, `series`, `top` and `details` |
| `alta statistics status` | How far the history is read |
| `alta statistics history ...` | Choose, pause, resume or stop reading history, or forget deleted sessions |
| `alta statistics estimate <text>` | Size of a text in bytes and approximate tokens |

- A period is `today`, `yesterday`, a number of days (`7d`, `30d`, `90d`, today included), `week`, `month`, `last-month`, `year`, `all`, or two dates.
- Days are local; a week starts on the first day of your regional settings.
- A space filter uses the projects the space has today.
- A run spans its first to last record, including time waiting for you; sessions at once are those with a run open at the same moment.
- Every answer says whether the history is fully read for the period.

## Disable it

```toml
[plugins.statistics]
enabled = false
```

Nothing is read or updated while the plugin is off. The kept numbers stay in the database and are used again when you turn it back on.
