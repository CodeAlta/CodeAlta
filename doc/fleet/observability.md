# Observability

Seeing what the fleet does: which machines are up, which sessions work or wait, what they cost, what went wrong, and
who did what. Without content, unless a person shares it.

## What people see

| View | Who | Content |
| --- | --- | --- |
| **Fleet** | Everyone, for what they may see | Machines (online, asleep, offline; busy; version; readiness), sessions by state (working, waiting for an answer or an approval, failed, idle), first the ones that wait for *me* |
| **Activity** | Members of a project | Sessions started and ended, pull requests opened, automations' results, handoffs, moves ([collaboration](collaboration.md#activity-and-awareness)) |
| **Usage and cost** | Each person for themselves; teams and admins as [statistics](statistics.md#who-sees-what) allows | Tokens, time and estimated cost by person, team, machine, project, automation, model and period; budgets and their consumption |
| **Health** | Owners of machines; admins | A machine offline longer than usual, a provider's sign-in expiring or failed, disk space low, a version behind, an automation failing, a policy refusing runs |
| **Audit** | Owners, admins, auditors | The audit log ([security](security-and-privacy.md#audit-log)), filtered and exported |
| **A trace** | Whoever may see the sessions in it | One task across machines and sessions: a session, the children it started elsewhere, the moves, the tools, with their times and token counts |

In CodeAlta Desktop these are canvases of the console ([console](console.md#the-canvases)): Fleet (machines and
sessions, live), Statistics with a fleet scope, Automations, Audit; project activity is in the project's **Channel**
tab. The phone shows the Fleet view and what waits for its person.

## Facts, not content

The server's views come from the facts nodes publish ([protocol](protocol.md#durability)), never from journals:

- **Session facts**: state, times, provider type and model, numbers of turns and tool calls, token counts, cost
  estimate, sandbox profile, parent and children, epoch.
- **Statistics facts**: the facts the Statistics plugin keeps per session and quarter hour, published by each machine
  as versioned rows, so a fleet roll-up is a sum and a replay after an outage changes nothing
  ([statistics](statistics.md)).
- **Machine facts**: heartbeat state (in memory at the server), capabilities, readiness per space, version, free disk.
- **Descriptive metadata** (titles, project names, branches) is sealed in Private spaces: the person's own devices
  decrypt it to draw the views; the server's own pages show ids and states.

## Budgets and alerts

Surprise bills are the first complaint of agent users. So budgets ship with the fleet, for homes too:

- A budget is a limit of tokens or estimated cost per period, for a person, a machine, a project, a space, a team or an
  automation (its `chargeScope`, [access control](access-control.md#who-is-who-on-a-resource)).
- **Advisory or enforced.** An advisory budget warns from the facts as they arrive, so machines working at once or
  offline can pass it before anyone is told. An **enforced** budget is split into **allowances**: the server gives each
  machine a share it may spend without asking (also while offline), and a run is admitted only when its machine holds
  enough of it; a machine that runs out asks for more, and the server never hands out more than the budget. The
  console says which kind each budget is.
- When a budget is reached, the server pushes **budget reached** as a stricter policy, which applies immediately
  ([security](security-and-privacy.md#policies-and-the-kill-switch)).
- At 80% and 100%: a notification; at 100%, new runs in that scope wait for a person's **Continue anyway** (or stop,
  per the budget). A prompt sent through the node API that waits is kept by the node before it says so, protected as the
  secrets of the machine are, bounded (one more is refused, none is evicted), and forgotten only once its run started; a
  restart asks the same hold again once the budgets and policies of the node are set and its links read, and does not send
  twice a prompt its journal holds (a journal it cannot search to its start keeps the send held). A machine that keeps
  budgets it cannot verify yet (its device list not read) holds new runs until it can, never running them as unbudgeted.
- **Who signs what.** A budget is signed by a controller device of its owner, as policies are: a machine applies only a
  budget that verifies against the device list it anchored, so a server can drop one (deny service) but neither loosen
  nor invent one. Allowances are the server's bookkeeping. **Continue anyway** loosens, so it is signed too, for one run:
  bound to the budget and its version, the period, the machine, the run and a nonce the machine chose, valid an hour;
  **Stop** is stricter and is not signed.
- **Allowances**: each machine commits the larger of what it was given and what its facts count (a desktop that spends
  without allowances still takes its part); the server gives only what no machine committed, at least a quarter of the
  limit at once so that a machine asks seldom and spends offline, in one transaction with the facts. A machine admits a
  run while it holds a hundredth of the limit beyond what it spent, counted from its own statistics; the last run of a
  period can pass the limit by what it spends. A queued prompt is admitted when its turn starts, not when it is
  queued; a steer joins the turn that runs and is part of that turn's admission.
- A budget limits the facts of its owner's machines: the person, one machine, a space (by the opaque id the server knows
  it by, which statistics sets carry), a project (by its blind index). Teams, the organization and automations come with
  their roles (M4).
- Estimates come from the model catalog's prices (models.dev), as the Statistics plugin does; a subscription shows its
  plan usage instead, asked of the vendor's CLI as today.

## Traces

- Nodes trace with OpenTelemetry (`ActivitySource`, `Meter`) following the **GenAI semantic conventions**:
  `invoke_agent` for a session's run, `execute_tool` for its tools, inference spans for model calls,
  `gen_ai.conversation.id` = the session id, token usage attributes. The conventions are still marked "Development":
  CodeAlta pins a version and maps names in one adapter.
- **Content is off**: no prompt, message, tool argument or result in a span that leaves a machine, as CodeAlta does
  today (the conventions make these attributes opt-in, with `_ref` variants pointing to storage). Telemetry is never a
  way around "unshared content never leaves its machine": an organization that must keep prompts uses explicit shares
  in a Managed space. A person may turn content on for a local diagnosis, to a collector on their own machine.
- **One trace across machines**: every request carries `traceparent` ([protocol](protocol.md#envelopes)): a child
  session on another machine, a cross-machine message, a move, an automation started by the server's scheduler. A
  trace survives a move because the session's id stays.
- **Spans of the hosts of the fleet**: a node traces its session runs (above), each request of its node API and each
  move (the method, the caller's device id in short, the outcome and the duration; never the parameters); the server
  traces each REST request (ASP.NET Core's own spans, named after their route) and each request of a control
  connection. A relay ticket in a path and every query are removed before export.
- **At home**: the desktop can start the standalone Aspire Dashboard as a "view traces" window (development-grade, no
  persistence by default) and point the fleet's nodes at it for the time of a diagnosis.

## Where telemetry goes

Traces, metrics and logs go over OTLP to one collector (Grafana, Datadog, Azure Monitor, the Aspire Dashboard...),
which the hosts name at three levels; the first that names one wins:

| Level | Set by | Where |
| --- | --- | --- |
| The machine's own file | The owner of the machine | `telemetry.json` in the fleet folder of a node's state root, or in the server's data root. It can turn telemetry off whatever the fleet says, refuse the fleet's setting, or name a collector of its own |
| The environment | Whoever starts the process | The standard `OTEL_*` variables; `OTEL_SDK_DISABLED=true` turns everything off |
| The fleet's setting | An owner, from CodeAlta Desktop | Signed by the owner's controller device, stored by the server, applied by it at once and pushed to every machine over its control connection |

- **The server cannot redirect telemetry.** The fleet's setting is a signed document, as the device list's entries
  are: a machine applies it only once it verified the signature against the device list it anchored (a controller
  device of the organization, not removed), and only when it is newer than the one it holds. A server alone can
  neither forge one nor replay an older one.
- **Two routes.** *Direct*: each machine sends to the collector, with the headers (API keys) of the setting. *Through
  the server*: each machine sends OTLP/HTTP to the server's `/otlp/v1/{traces,metrics,logs}`, each request signed with
  its device key as the REST API's are; the server forwards the bytes to the collector with headers only it holds,
  which never reach a machine, are kept sealed, and come back to no one, the owner included: a change keeps them by
  their names. Machines behind NAT need no collector credential and no way out but the server. The
  server forwards; it never stores what it forwards.
- **Resource**: `service.name` (`altanode`, `altaserver`), `service.version`, `service.instance.id` (the device id of a
  node, a stable id of a server), `host.name`, `os.type`, `codealta.fleet.organization.id` once linked, and
  `deployment.environment.name` when a level names one.
- **Applied live**: a new setting rebuilds the export of each host; the previous one sends what it holds first.

## Logs

- Each node keeps its log as today. Its owner's devices can read it remotely (**Show log** on the machine), over the
  session channel.
- **To the collector, when asked**: logs are CodeAlta's own records; they hold no prompt, message or tool content, but
  they may hold paths and errors. So they go to the collector only when the level that names it turns logs on (the
  `logs` signal of a setting or of the machine's file, `OTEL_LOGS_EXPORTER=otlp` for the environment), with their
  category, level and exception. The server only forwards them, as the other signals.
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

- The server answers the probes of load balancers and container platforms without a device: `/health/live` (the process
  answers; it checks nothing else, so a slow store never restarts it) and `/health/ready`, also served as `/health`
  (the store is reachable and migrated; 503 otherwise). Each is a status word, cheap, never cached, and says nothing of
  the organization or the version; `/api/v1/server/health` stays the richer answer for CodeAlta's own clients.
- The server exposes its metrics to the collector: control connections and handshakes,
  publications and facts, directory watchers, relays (opened, refused, active, bytes, how they ended), enrollments and
  approvals, signed requests refused, store latency, the depth of its writer's queue, the OTLP it forwards; with ASP.NET
  Core's and the runtime's meters.
- A node's metrics: its node API's durations, moves, connections to servers (connects, failures, facts published, the
  backlog of its outbox), sessions working and devices connected.
- Machines report health in their heartbeat; the server turns lasting problems into notifications for the machine's
  owner (or the admins of team machines), at most once per problem.
  - A node reports what it sees (disk space low where it keeps its state, a provider whose sign-in failed, or whose
    stored sign-in tells its expiry and does not renew itself, three days ahead; an automation whose last runs failed;
    its policy refusing runs); the server sees for itself a node offline longer than
    a threshold and one behind its own version. Names and numbers, never content.
  - A problem is a kind and a subject (a provider, an automation) since it was first seen: told once it lasts (ten
    minutes by default), resolved when it ends; one that comes back is another one. The store is written when the
    problems of a machine change, not at every heartbeat.
  - Notifications are kept per person with a key that makes each one once (a budget, its period and threshold; a
    machine, its problem and since when), so a replay of facts or a restart tells nothing twice; the person's online
    controller devices are told at once, the others read them when they connect.
