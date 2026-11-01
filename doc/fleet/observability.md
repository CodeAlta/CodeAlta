# Observability

Seeing what the fleet does: which machines are up, which sessions work or wait, what they cost, what went wrong, and
who did what. Without content, unless a person shares it.

## What people see

| View | Who | Content |
| --- | --- | --- |
| **Fleet** | Everyone, for what they may see | Machines (online, asleep, offline; busy; version; readiness), sessions by state (working, waiting for an answer or an approval, failed, idle), first the ones that wait for *me* |
| **Activity** | Members of a project | Sessions started and ended, pull requests opened, automations' results, handoffs, moves ([collaboration](collaboration.md#activity-and-awareness)) |
| **Usage and cost** | Each person for themselves; admins for teams and machines | Tokens and estimated cost by person, machine, project, model and period; budgets and their consumption |
| **Health** | Owners of machines; admins | A machine offline longer than usual, a provider's sign-in expiring or failed, disk space low, a version behind, an automation failing, a policy refusing runs |
| **Audit** | Owners, admins, auditors | The audit log ([security](security-and-privacy.md#audit-log)), filtered and exported |
| **A trace** | Whoever may see the sessions in it | One task across machines and sessions: a session, the children it started elsewhere, the moves, the tools, with their times and token counts |

In CodeAlta Desktop these are the **Fleet** tab (machines and sessions, live), project activity in the project's
**Channel** tab, and dashboards built like the Statistics canvas (ECharts, the same facts). The phone shows the Fleet
view and what waits for its person.

## Facts, not content

The server's views come from the facts nodes publish ([protocol](protocol.md#durability)), never from journals:

- **Session facts**: state, times, provider type and model, numbers of turns and tool calls, token counts, cost
  estimate, sandbox profile, parent and children, epoch.
- **Usage facts**: additive counters per (person, machine, project, model, quarter hour): input, output and cache
  tokens, estimated cost, runs, failures. They are the same facts the Statistics plugin already keeps per session
  and quarter hour, so a fleet roll-up is a sum, and a replay after an outage is idempotent.
- **Machine facts**: heartbeat state (in memory at the server), capabilities, readiness per space, version, free disk.
- **Descriptive metadata** (titles, project names, branches) is sealed in Private spaces: the person's own devices
  decrypt it to draw the views; the server's own pages show ids and states.

## Budgets and alerts

Surprise bills are the first complaint of agent users. So budgets ship with the fleet, for homes too:

- A budget is a limit of tokens or estimated cost per period, for a person, a machine, a project, a space or a team.
- **Nodes enforce them**: they know their usage at once; the server sums across machines and pushes **budget reached**
  as a stricter policy, which applies immediately ([security](security-and-privacy.md#policies-and-the-kill-switch)).
- At 80% and 100%: a notification; at 100%, new runs in that scope wait for a person's **Continue anyway** (or stop,
  per the budget).
- Estimates come from the model catalog's prices (models.dev), as the Statistics plugin does; a subscription shows its
  plan usage instead, asked of the vendor's CLI as today.

## Traces

- Nodes trace with OpenTelemetry (`ActivitySource`, `Meter`) following the **GenAI semantic conventions**:
  `invoke_agent` for a session's run, `execute_tool` for its tools, inference spans for model calls,
  `gen_ai.conversation.id` = the session id, token usage attributes. The conventions are still marked "Development":
  CodeAlta pins a version and maps names in one adapter.
- **Content is off**: no prompt, message, tool argument or result in a span, as CodeAlta does today (the conventions
  make these attributes opt-in, with `_ref` variants pointing to storage). An organization in a Managed space can turn
  content on for its own collector; a person's Private space never.
- **One trace across machines**: every request carries `traceparent` ([protocol](protocol.md#envelopes)): a child
  session on another machine, a cross-machine message, a move, an automation started by the server's scheduler. A
  trace survives a move because the session's id stays.
- **Where traces go**: to the collector the organization names (Grafana Tempo, Azure Monitor, Datadog...), directly
  from each node or forwarded by the server's OTLP endpoint. The server forwards; it does not store traces.
- **At home**: the desktop can start the standalone Aspire Dashboard as a "view traces" window (development-grade, no
  persistence by default) and point the fleet's nodes at it for the time of a diagnosis.

## Logs

- Each node keeps its log as today. Its owner's devices can read it remotely (**Show log** on the machine), over the
  session channel; logs never go to the server, since they may hold paths and errors from sessions.
- What a remote action did is in the audit log and in the session's journal; logs are for diagnosis.

## Audit is not telemetry

| | Audit log | Telemetry |
| --- | --- | --- |
| Purpose | Who did what, for accountability and compliance | How the system behaves, for diagnosis |
| Completeness | Every security-relevant event | Sampled, lossy |
| Retention | Set by the organization, long | Short, disposable |
| Content | None in Private spaces | None by default |
| Where | The server, chained by hash, exported | The organization's collector |

## Health checks

- The server exposes `/health` (live, ready) and metrics (connections, relayed bytes, queue depths, store latency) for
  whatever watches it.
- Machines report health in their heartbeat; the server turns lasting problems into notifications for the machine's
  owner (or the admins of team machines), at most once per problem.
