# Fleet statistics

The numbers of every agent, machine and CodeAlta instance of a fleet in one place: time, tokens, cost, runs, tool
calls, failures, automations, by person, team, project, machine, provider and model, over any period. Without content,
and without counting anything twice or losing it through an outage.

## Built on the Statistics plugin

The plugin already turns journals into facts on each instance ([statistics](../statistics.md)), and its choices are
the ones a fleet needs:

- **Facts are additive**, keyed by session, quarter hour (UTC) and family; corrections are negative additions; costs are
  integers in millionths. A few measures merge with `MAX` (the fullest context, the longest call), some are derived
  when asked (sessions at once), and units never mix (dollars, AI credits). Merging across machines keeps each
  measure's own operator, which keeps totals exact.
- **Names, never content**: counts and the names of providers, models, tools, skills and MCP servers; no prompt, answer,
  argument, result or path.
- **Work is counted where it was done**: forks count from their own records, external turns count nothing.
- **The canvas talks to a `StatisticsApi` interface**, not to a transport: a second binding to the fleet gives the same
  charts over fleet-wide numbers.

What the fleet adds: every machine computes and publishes its facts, the server keeps and rolls them up, and the
console queries them by scope.

## Every machine counts its own

- Each CodeAlta instance that runs sessions (Desktop, TUI through the desktop that follows it, CodeAlta Node) computes
  the facts of its sessions from its journals, as the desktop does. CodeAlta Node runs the statistics engine too
  (`StatisticsPlugin.CreateForHost`, built in M0).
- **A machine is the single writer of its facts.** After a move, the new owner counts the session's work from the move
  record on (as a fork counts from `counted_from`), so the journal it imported is never counted again; the facts the
  old machine counted stay its own, and it keeps publishing them whoever owns the session now (work done offline before
  a move still reaches the server), and a session that comes back counts there again what that machine did before. The
  move records name the machines by device id (and by name, for people), so each part of a journal is counted by the
  machine that wrote it, whatever the machines are called by then; a record written before the ids were recorded is
  read by names. Facts are keyed by
  machine as well, so a quarter hour split by a move is the merge of both.

## Publication: rows, not increments

- A machine publishes the **current value of the rows** of a session, never an increment: the rows of the session
  (keyed by quarter hour, family and dimension) as **one set** with the session's `(sessionId, epoch, version)`, a new
  version each time its rows change. The server keeps the set of the highest version per machine and session and
  replaces every row of it; a set of an older epoch is refused.
- So a replay after an outage changes nothing, a correction (a provenance written late, a session reread after a new
  definition version) simply replaces the set, a row that disappeared goes with it, and the server's numbers converge
  to the machines' own. A session whose rows are all gone sends an empty set.
- A machine reads the rows of a session again only when the revision of its facts in the store changed (where its
  reading stopped, the version of the definitions, the floor), so publishing costs nothing while nothing is counted.
- Published today: the families a fleet sums (`activity`, `usage`, `cost`, `tool`), keyed by provider, model and the
  other keys of the family. The others (content, details, histograms, scripts) stay on the machine for now. One run is on one
  model: its start and its time go on the model its own first request reports (the facts move them there from the
  alias in force before), a request or a cost keeps the model it reported, and tool calls, which the facts keep without a
  model, take the session's model of their provider when the session used one then.
- **Generations**: a machine that lost its publication outbox (a new disk, a reinstall) starts a new publication
  generation; the server replaces everything that machine published before, instead of keeping old sets whose versions
  look newer.
- **Definition versions**: each row names the version of the definitions that produced it (today 10). A total that
  would mix versions while machines catch up is marked as such in its coverage, never presented as exact.
- Publication follows the store's transactions: what a catch-up saved is what the machine sends, coalesced to a few
  seconds, through the outbox of the control connection ([protocol](protocol.md#durability)).
- **When publication starts**: from the day (UTC) the machine was linked to the server; publishing the past is a choice
  of the person (**Publish my history** with a starting date, to come), as reading history is today. A node, where
  nobody is asked, counts from its first day.
- **Projects** travel as a blind index (an HMAC of the person's metadata key) of what recognizes the project on every
  machine: its repository and its folder in it, or its id without a repository. The server groups by it and cannot
  name it; the person's devices name the projects they have by computing the same index. **Spaces** travel by the
  opaque id the server knows them by, which budgets of a space count by.
- **Deleted sessions** keep their published facts (as the local archive keeps them), until the organization's
  retention removes them.
- **Private sessions** publish nothing ([security](security-and-privacy.md#what-the-server-sees)): their numbers stay
  in the person's local statistics. On the machines an organization manages, its policy may require the facts of the
  sessions of its spaces, which then cannot be Private towards it.

## Dimensions

| Dimension | At the server |
| --- | --- |
| Time | Quarter hours, rolled up by hour, day, month in the organization's time zone (each person's own zone in their views) |
| Machine, CodeAlta instance kind | Ids |
| Person, team, service identity | From the owner of the session or the run |
| Space, project | Ids; in Private spaces a project is a blind index, named on the person's devices |
| Session | Id; its title only sealed |
| Automation, run, workflow script | Ids and script names (identifiers, as today) |
| Provider type, provider, model, effort, tier | Names |
| Agent prompt | Id |
| Tools, skills, MCP servers, command programs | Names, as the plugin keeps them |
| Outcome | Run outcome, failure kind, fallbacks, adaptive escalations |
| Provenance | Who sent a prompt: a person, an agent, an automation, a reminder |

## Measures

The plugin's measures (active time, runs, turns, tokens by kind, estimated cost, tool calls by kind, failures,
context fill, sessions at once), plus what only a fleet has:

- **Machines**: busy time, sessions at once, queue wait before a run started, time offline.
- **Automations and runs**: runs by outcome, duration, cost per run, approvals asked and their waiting time,
  interruptions and retries.
- **People in the loop**: approvals answered and how long they waited, prompts written by people versus sent by agents.
- **Budgets**: consumption per budget and period ([observability](observability.md#budgets-and-alerts)).

## Who sees what

Statistics about people are sensitive: in many countries per-person productivity numbers need a legal basis and a
works council's agreement. So:

| Who | Sees by default |
| --- | --- |
| Each person | All their own numbers, every dimension |
| Team maintainers | Their team's totals and its machines and automations; per person only with `view-per-person` |
| Admins, auditors | The organization's totals by team, project, machine, model and automation; per person only with `view-per-person`, which the organization grants explicitly and the audit log records |
| A household | The owner sees the household's machines and automations; members their own |

Hiding the person dimension is not enough, since a person's own machine or a project of one person tells the same. So
the rule is enforced on the rows before they are aggregated, not with an optional filter:

- for someone without `view-per-person`, a personal machine counts as a person, and any group of fewer than five
  people (a project, an automation, a team) shows no breakdown that would single someone out;
- such a viewer gets fixed breakdowns (team, project, model, automation, machine pool), not free combinations of
  filters that could isolate one person by difference;
- this is an access rule of CodeAlta, stated as such, not a promise of anonymity against a determined administrator
  who owns the server's database.

Sealed names stay sealed: in a Private space the server aggregates by ids and blind indexes, and the person's devices
put the names back when they draw the charts.

## Queries

- `POST /api/v1/statistics/query` (built in M3b): local days and their time zone, up to three groups among machine,
  project key, provider, model, day and month, and a **scope**, `mine` for now (the machines of the calling device's
  person); it answers the totals, the groups (the largest first) and each machine's last publication. Days are exact
  ranges of quarter hours in any time zone ([protocol](protocol.md#the-servers-rest-api)).
- To come: the plugin's own requests (frequency, comparison, filters) and its JSON, so the canvas and `alta statistics`
  work unchanged, and the scopes of teams and the organization with the rule below.
- Roll-ups by day and month are kept beside the quarter hours, rebuilt from them when the organization's time zone or
  a definition version changes, as the plugin does locally (today the server sums quarter hours when asked).
- Results are bounded and the caller's access filters them before they leave the server.

## In the console

The **Statistics** canvas gains a scope: **This machine** (today's numbers, local), **My fleet**, a **team**, the
**organization**, an **automation**, a **machine**. The charts, filters and comparisons are the same; only the binding of
`StatisticsApi` changes. `alta statistics --scope fleet` gives agents the same numbers within their person's access.

Built first: **My fleet**, offered once the desktop is linked to a server, as a page of its own (totals, by machine,
by project, by model, over a few periods) read through the desktop host (`fleetServer.statistics`), which signs the
request and names the machines and the projects it has.

## Exports

- CSV and JSON from the console and the API, for the periods and scopes a person may see.
- OpenTelemetry metrics of the server's aggregates for organizations that watch costs in their own tools.
- Retention by the organization: quarter hours for months, daily roll-ups for years.

## Offline and correctness

- A machine that cannot reach the server keeps counting; its outbox sends the rows when it returns; the versions make
  the result exact.
- The server's totals are, at any time, the sum of what machines published; the console shows each machine's last
  publication, so a gap is visible rather than silent.
- Tests: split invariance across machines (a session's facts cut at a move give the same totals), replay and
  reordering of publications, version rewrites, and access filtering (a team maintainer never receives per-person rows
  without the grant).
