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

## What must hold everywhere

- **Local-first**: every scenario but the sharing ones works on a node alone, and degrades gracefully without the
  server.
- **Private by default**: the server never sees a prompt, an answer, a tool output or a file, unless a person shares
  it; a session marked Private is never published.
- **No surprise execution**: nothing runs on a machine unless its owner or its policy allows that kind of request.
- **Attributable**: every remote action names the person, the device, the machine and the session behind it.
