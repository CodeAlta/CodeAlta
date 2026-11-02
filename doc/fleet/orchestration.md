# Automations, workflows and runs

How agents work without a person at the keyboard across a fleet: automations that react to time and events, workflow
scripts that fan out across machines, and the runs they produce, for a person, a team, a project or a whole
organization. Who owns what is in [access control](access-control.md).

## What exists today

CodeAlta Desktop already has the pieces, on one machine ([automations](../../site/docs/automations.md),
[Code Mode](../codemode.md)):

- **Automations**: triggers (schedules, cron, issues and pull requests, Jira issues, commands that succeed, **Run now**),
  a prompt or a library script, and what their sessions run with (provider, model, effort, permission mode, review,
  sandbox, sub-sessions). They live in the user's `config.toml` or in a project's `.alta/config.toml`, which travels
  with the repository and waits for **Allow** on each machine. Each run is a session.
- **Workflows**: Code Mode scripts of the library (project, user, samples) that start sub-agents (`agent()`,
  `parallel()`), record their steps, and resume after a restart.
- **Orchestration between sessions**: children, messages between agents, reminders and jobs.

What is missing: running them while the desktop is closed, organizing them by person, team, project and organization,
placing their work on the right machine, events from outside Git, credentials fit for unattended work, approvals
routed to the right person, and one view of every run.

## Scopes

| Scope | Owned by | Stored | Runs on |
| --- | --- | --- | --- |
| **Personal** | A person | Their profile (as today), or the server when published to their fleet | Their machines (**This machine**, a named machine, or any of theirs) |
| **Project** | The project (edited by its maintainers) | `.alta/config.toml` of the repository, as today | Machines that have the project and accept it; in an organization, team machines granted to the project |
| **Team** | A team | The server | The team's machines |
| **Organization** | The organization | The server | Machines the organization designates (pools, team machines that accept it) |

Today's automations keep working unchanged as personal ones on **This machine**. **Publish to…** gives one a scope
and an owner; a project automation stays in its repository, where code review covers its changes.

## The parts of an automation

| Part | Content |
| --- | --- |
| Identity | Id, name, owner, scope, editors, operators, approvers, version |
| Triggers | Schedules, Git and tracker events, events of other tools, fleet events, by hand ([triggers](#triggers)) |
| Action | A prompt, or a workflow script (a pinned, approved version) with its arguments |
| Runs with | Agent prompt, provider or tier, model, effort, permission mode, sandbox, sub-sessions, as today, plus the credential rule ([below](#credentials-of-unattended-runs)) |
| Placement | **Runs on**: a machine, labels (`macos`, `infra-prod`), a team pool, with preferences ([placement](#placement-and-dispatch)) |
| Autonomy | What its runs may do without a person, and where approvals go ([autonomy](#autonomy-and-approvals)) |
| Concurrency | Maximum runs at once; when one is due while another runs: skip, queue or replace |
| Limits | Budget per run and per period, maximum duration |
| Outputs | Where results go: the event's source (a reply), a project channel, notifications |
| Signature | Each version is signed by the device of the editor who saved it |

## Triggers

| Trigger | Evaluated by | Notes |
| --- | --- | --- |
| Schedules (hourly, daily, weekly, cron) | The server's scheduler; the assigned machine keeps a copy and fires it itself while the server is unreachable | One run per (automation, due time), whoever fires it |
| Git hosts: issues, pull requests, CI runs | The assigned machine, polling with its own credentials (as today); or the server, from webhooks of the organization's Git host app | No repository token at the server unless the organization chose the GitHub App broker |
| Trackers and other tools: Jira (issue created or updated, version released, a JQL query), incident.io, PagerDuty, Slack (a message in a channel) | Trigger sources contributed by plugins, running on the assigned machine with its credentials (polling, Slack Socket Mode), or at the server for inbound webhooks | Each source says where it can run |
| Inbound webhooks | The server's ingress (`/api/v1/hooks/<id>`), which checks the sender's signature and hands the event to the assigned machine | For tools that only push |
| Commands that succeed | The machine that runs the command (as today) | |
| Fleet events | The server: a run ended or failed, a session waits, a machine went offline, a budget threshold | Chaining automations |
| By hand | The console, the phone, `alta automation run`, the REST API | Signed by the device of the person who asks |

**An event is not an authority.** Whatever received it (a node polling, the server's ingress), the run starts only on
a machine that checks the automation's signed version and that the event matches its triggers. The server can deliver
an event; it cannot make a machine run anything the owner did not sign. Every event has an id, so a run happens once
even when two machines see the same event.

## Placement and dispatch

- The server picks, among online machines, one that: matches the placement; has the project (or may clone it); has a
  provider allowed for this run ([credentials](#credentials-of-unattended-runs)); can hold the sandbox; accepts this
  owner's automations in its local policy; has room under its limits. It prefers the machine that ran it last (a warm
  worktree), then the least busy, then the cheapest (a person's idle machine before a paid cloud one).
- The machine **claims** the run with a lease and renews it while it works. A run whose machine disappears is marked
  **interrupted**; it starts again elsewhere only when the automation allows retries, because agents change things:
  runs are at most once by default.
- **Without a server** (paired machines), **Runs on** a paired machine copies the automation to it; it runs there and
  shows in the paired view.

## Who a run acts as

| Automation | Acts as | On | With the credentials of |
| --- | --- | --- | --- |
| Personal, on the person's own machine | The person | Their machine | Their own logins on that machine, subscriptions included, as today |
| Personal, on a machine that is not theirs (a team machine that accepts it) | The person | That machine | Account credentials only (below) |
| Team or organization | The scope's service identity | The scope's machines | Account credentials of the scope |
| Started by an agent | The agent's principal (a delegated token with its chain) | As its parent allows | As above |

### Credentials of unattended runs

Personal subscriptions (Claude Code with a Claude plan, the Codex subscription, Copilot seats) belong to one person for
their own use, under their vendor's terms. So:

- **A run that is not a person's own run on their own machine uses account credentials only**: API keys of the
  organization or the team (Anthropic, OpenAI, Azure OpenAI, Vertex, Bedrock through a gateway, Mistral, xAI...) or a
  model gateway with per-scope virtual keys. Never a person's subscription, never on a shared machine.
- Each provider declares its **credential kind**: *personal* (a plan signed in by a person) or *account* (a key or a
  cloud identity). Claude Code is *account* only when it runs with an API key. The kind is checked twice: by the
  scheduler before dispatch, and by the node before the run starts; model tiers and fallbacks of such a run resolve
  only to account providers. An organization's policy can require account credentials for every automation, personal
  ones included.
- **Organization providers**: an admin or a team maintainer defines a provider once at the server (type, endpoint,
  models, budget); its key is sealed to the keys of the machines allowed to use it, which keep it in their protected
  store; the server never holds it in clear. Rotation is a console action. With a gateway, each scope or automation
  gets its own virtual key, so costs and limits follow ownership.
- **Other credentials of a run** (Jira, Grafana, Kubernetes, Slack, Git) are the machine's own, scoped to what its runs
  may do: a machine inside the infrastructure holds read-only monitoring access and the narrow write role of its
  allowed remediations ([deployment](deployment.md#machines-for-unattended-work)).

## Autonomy and approvals

- **Graduated autonomy** is part of the automation and bounded by policy: what its runs may do without a person
  (read-only by default), and everything else held as an approval.
- **Allowed actions are tools, not command patterns.** "Restart a pod in namespace `shop`" is an MCP or plugin tool with
  typed parameters and a fixed scope; a shell pattern such as `kubectl rollout *` is too easy to bend. Commands and
  edits outside the allowed tools follow the run's permission mode, which an unattended run keeps strict.
- **Approval routing**: the automation names who answers its approvals: its owner, a team (the first allowed answer
  wins), or an **on-call resolver** contributed by a plugin (incident.io, PagerDuty, Opsgenie rotas) that names the
  person on call now. The approval reaches that person's phone with what the run found (its diagnostic, its evidence),
  bound to the exact request; they approve, deny, or **take over** the session live.
- **Timeouts**: an approval not answered in time stops the run or keeps it waiting, per the automation; either way the
  run says so.

## Outputs and reporting back

- A trigger source can **reply where the event came from**: a reply in the Slack thread, an update of the incident's
  timeline, a comment on the Jira issue or the pull request, written by the run through the source's plugin.
- Team and organization runs post a card in the project's channel (the run, its outcome, its pull request); failures
  notify the automation's owner or operators.
- The run's session holds the full record, readable by those who may see the run's content.

## Workflows across machines

- A workflow script places its sub-agents: `agent(prompt, { machine: "any", labels: ["macos"] })` starts the child on a
  machine that matches, through the server, with the parent's restrictions; `parallel()` fans out across machines.
- The workflow's run stays owned by the session that runs the script, on its machine; children elsewhere report back
  through deliveries, which wait while the parent's machine is offline.
- The run's graph (the Code Mode graph already drawn by the desktop) spans machines; one trace follows it
  ([observability](observability.md#traces)).
- Scripts used by unattended runs are **approved versions**: an automation pins the hash it was allowed with (as
  today), and an organization library publishes versions that its approvers signed.

## Runs

A run is a record at the server (facts only; content stays in its sessions): its automation and version, its trigger
event (source, id, a summary without content), its owner and initiator, its machine, its sessions, its state
(queued, running, waiting for approval, interrupted, succeeded, failed, cancelled), its times, tokens and cost, and its
outputs. Runs are what the console lists, what statistics count per automation, and what the audit log names with the
trigger behind every autonomous action.

## The two cases of issue #292

| | Release notes from Jira | Diagnosis of an incident |
| --- | --- | --- |
| Scope, owner | Team automation of the release team | Organization automation, owned by the SRE team |
| Trigger | Jira version released (plugin source on the machine), or a schedule | incident.io incident declared (webhook at the server), or a message in the incident's Slack channel (Socket Mode on the machine) |
| Runs on | Any team machine with the repositories | A machine labeled `infra-prod`, inside the infrastructure |
| Credentials | The team's API key provider; the machine's Jira and Git access | The organization's gateway key; read-only Grafana and Kubernetes access plus a narrow write role |
| Autonomy | Read only; publishing waits for an approval by the release manager | Read only, plus three remediation tools (restart a pod, roll back a deployment, scale up) allowed when the diagnosis is confident; anything else asks the on-call person |
| Outputs | A draft pull request or page; a card in the channel | The diagnosis with its evidence and confidence in the incident timeline and Slack thread; what it did, if anything |
| Audit | The run, the Jira version, who approved | The run, the incident id, each remediation, who approved |

## In the console

The **Automations** canvas of the console ([console](console.md)) lists every automation a person may see, by scope,
with the next runs across the fleet (the calendar of today's Automations tab, with a column per machine), the runs and
their states, and a run's page: its trigger, its graph across machines, its approvals, its sessions. **Run now on…**
picks a machine. Everything it shows and does is the REST API ([protocol](protocol.md#the-servers-rest-api)), so a
plugin or a script can do the same.

## Offline

- A machine keeps the schedules of the automations assigned to it and fires them while the server is unreachable; the
  server reconciles by (automation, due time) and counts nothing twice.
- Events seen by a machine (polling, commands) start runs locally the same way; events at the server's ingress wait
  for the machine, within the automation's tolerance, then follow its catch-up rule.
- Runs report their facts when the server returns.
