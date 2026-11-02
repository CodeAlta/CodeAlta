# Scenarios

The stories the fleet must make easy, from one person on one computer to an organization. Each says what the person
does and what must hold; the milestones of the [roadmap](roadmap.md) refer to them by number.

## One person

### S1. Stay local

Alex uses CodeAlta on one computer and never connects anything.

- Nothing changes: no account, no sign-in, no network call to a fleet, no new question at startup, the same speed.
- The fleet appears only as one entry in Settings and one in the help.

### S2. See and drive another machine (paired, no server)

Alex has a Windows PC and a Mac mini on the same tailnet. On the Mac mini, `altanode install` then `altanode pair`
shows a QR code; CodeAlta Desktop on the PC scans it (or types its code).

- The PC lists the Mac mini's sessions beside its own, marked with the machine; opening one shows its live timeline.
- From the PC, Alex sends a prompt, steers a turn, stops it, answers a question and a permission request of a session
  on the Mac mini, and starts a new session there in a project the Mac mini has.
- A lost connection never runs a command twice and never applies an answer to another run.
- The Mac mini keeps running its sessions when the PC is off.

### S3. Take over on another machine after a push

Alex works in session X on the PC, on branch `feature/parser`, pushed. **Move to machine… > MacBook**.

- The MacBook matches the repository by its remote, finds its checkout, and prepares a worktree on `feature/parser` at
  the same commit.
- Session X continues on the MacBook with its whole conversation; on the PC it shows as "moved to MacBook" and opens
  there through the fleet.
- If the PC had commits not pushed, or changes not committed, they arrive on the MacBook too (within a stated limit).

### S4. Close the laptop, let the server continue

Three sessions work on the laptop; Alex closes the lid. A home server is in the fleet.

- CodeAlta offers to move the sessions to the home server (automatic if Alex chose so for the space); they finish
  their turn and move.
- When one of them needs an approval, Alex's phone shows it, and Alex answers there.

### S5. Automations while every personal computer is off

A nightly automation updates dependencies and opens a pull request. Its **Runs on** is the home server.

- It runs at night with every laptop off; in the morning, any of Alex's devices shows its session and the pull
  request.
- If the home server was off at that hour, the run follows the automation's catch-up setting when the server returns.

### S6. Work through an outage

The server is down for an hour (or the house loses the internet).

- Every node keeps working: sessions run, automations fire, desktops show their local sessions.
- Machines that reach each other directly (same LAN or tailnet) still see each other's sessions.
- When the server returns, the fleet shows the right state; nothing is counted twice or lost.

### S7. An agent uses another machine

In a session on the PC: "run the macOS UI tests of this change on the Mac and fix what fails here".

- The agent starts a child session on the Mac (with Alex's prior permission for agent-started sessions there); it runs
  sandboxed, with no more tools than its parent.
- The child's final answer comes back to the parent on the PC as a message, as for a local child.
- Alex sees the child in the fleet and in the parent's sub-agents, marked with its machine.

### S8. Recover a session from a dead laptop

The laptop's disk failed while session Y was mid-task. Y's space keeps encrypted replicas on the home server.

- From the MacBook, Alex opens Y (offline), chooses **Continue on… > MacBook**: the replica and the last work in
  progress arrive, and Y continues.
- If the laptop comes back, it does not write Y again; what it wrote after the last replica is kept as a fork.

### S9. The phone as a fleet console

- Alex's phone shows every machine and session of the fleet, answers approvals and questions, and starts a session on
  any machine, from anywhere Alex's phone reaches the server.
- Today's Mobile plugin, one computer over Tailscale, keeps working unchanged.

## A household or a small team

### S10. Two people, one home server

Alex and Sam share the home server; each has their own account and their own sessions.

- Sam does not see Alex's sessions unless Alex shares one; the server shows each who is online.
- Both can use the home server as a node; each signs in their own providers there, or the node is a "shared node"
  with its own credentials and sandboxed sessions.

## An organization

### S11. Join the company fleet without mixing personal work

IT deployed CodeAlta Server with the company's SSO. Alex links the **Work** space of CodeAlta Desktop to it (by its
URL, or a configuration pushed to managed machines) and signs in with the company account.

- Only the sessions and projects of the Work space are known to the company server; Personal stays private, linked to
  the home server or to nothing.
- The Work space's Git identity can be filled from the company directory.

### S12. Hand off a session to a colleague

Alice opened a pull request with session Z and has to leave. She posts in the project's channel: "@bob can you take
over? the tests on Linux still fail" with **Hand off session**.

- Bob gets a notification; the handoff card shows the session's summary written at handoff, the branch and the pull
  request.
- Bob opens the session read-only, then **Continue in my fork**: a fork of Z arrives on Bob's machine and continues
  under Bob's identity. The channel shows the progress.

### S13. Team build machines

The team has two Mac build nodes. Developers start sessions on "any team Mac".

- Sessions run sandboxed, with the team's credentials, and each developer sees only their own sessions there.
- Admins see the machines' load and usage, not the content of the sessions.

### S14. Audit

Security asks: who ran sessions on the `infra-prod` project last week, from which machines, with which models, and
what they pushed.

- An auditor exports the audit log of that project for the week: people, devices, machines, sessions, commands, pushes
  and approvals, without prompts or answers.

### S15. Policies

The organization requires the sandbox on shared machines, allows two providers, and sets a monthly token budget per
team.

- Nodes apply the policies of the organization for its spaces; a policy can only narrow what a session may do, never
  widen it.
- A session that would break a policy is refused with the reason, before it runs.

### S16. Organize automations by owner

Alex's local automations (issue triage, a nightly dependency update) work today on Alex's desktop. The team wants the
triage for the whole project, run on team machines.

- Alex **publishes** the triage to the team: it becomes a team automation, owned by the team, edited by its maintainers,
  running on the team's machines; Alex's personal ones stay personal.
- On team machines it runs with the team's API key provider, never with Alex's subscription; a run that would need a
  personal subscription is refused before it starts, with the reason.
- The console lists automations by scope (mine, project, team, organization), with their owners and next runs.

### S17. Release notes from Jira

When a Jira version is released, a team automation on a release machine reads the version's tickets and linked pull
requests, writes the release notes, opens a draft, and waits.

- The release manager approves from the console or the phone; only then is anything published.
- The run, the Jira version that started it and the approval are in the audit log.

### S18. Diagnose an incident

incident.io declares an incident. An organization automation on a machine labeled `infra-prod` reads the alert,
queries Grafana and Kubernetes with read-only credentials, and posts its diagnosis, with evidence and a confidence
level, to the incident's timeline and Slack thread.

- When the diagnosis is confident and the fix is one of its allowed tools (restart a pod, roll back a deployment,
  scale up), the run applies it and reports what it did.
- Anything else asks the person on call, found from the rota; they approve from their phone, with the diagnosis
  attached, or take over the session live.
- The server received the webhook but could not have started the run: the machine checked the automation's signed
  version.

### S19. Statistics across the fleet

- Alex sees all of Alex's own numbers across every machine, desktop and node, in the Statistics canvas with the scope
  **My fleet**.
- A team maintainer sees the team's cost by automation, machine and model, and each automation's runs and failures;
  numbers per person only with the grant the organization gives and the audit log records.
- After a machine was offline for a day, the totals are exact once it reconnects.

### S20. Manage the fleet from the console

- An admin enrolls a machine, grants a team's use of it, sets a budget and pauses an automation from the console's
  canvases; each of these is a REST API call that a script could make too.
- A team adds a plugin with its own incident board canvas, built on the same API through `Services.Fleet`.
- An agent asked to "pause the night automations of the build Macs" does it with `alta fleet`, within its person's
  access; asked to grant someone access, it proposes, and the person applies.

### S21. Someone leaves

Bob leaves the company; an admin suspends him (SCIM does it by itself from milestone 5).

- His devices and delegations are revoked at once; his sessions on team machines stop.
- The team automations he owned go to the team, paused until a maintainer resumes them; nothing is orphaned, and the
  audit log shows each transfer.

## What must hold everywhere

- **Local-first**: every scenario but the sharing ones works on a node alone, and degrades gracefully without the
  server.
- **Private by default**: the server never sees a prompt, an answer, a tool output or a file, unless a person shares
  it; a session marked Private is never published.
- **No surprise execution**: nothing runs on a machine unless its owner or its policy allows that kind of request.
- **Attributable**: every remote action names the person, the device, the machine and the session behind it.
