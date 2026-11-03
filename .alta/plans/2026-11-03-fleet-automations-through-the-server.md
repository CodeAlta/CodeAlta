---
title: Fleet automations through CodeAlta Server (M3b, personal scope)
status: in-progress
created: 2026-11-03
summary: What is left after the first part of fleet automations (engine library, automations on altanode, Runs on a paired machine): the server stores each signed automation version and assigns it to a machine linked to it, nodes verify the signature and keep their copies, runs are reported as facts, and the desktop places automations on machines it reaches only through a server.
---

# Fleet automations through CodeAlta Server

## Where it stands

Built on `feature/fleet` (design: `doc/fleet/orchestration.md`, `node.md` "Automations and statistics"):

- `CodeAlta.Automations` is the engine, run by CodeAlta Desktop and CodeAlta Node. Its hooks: a run gate
  (`IAutomationRunGate`), claims of due times (`IAutomationClaims`, `AutomationClaimStore`), the file of assigned
  automations, and `runs_on` with `IAutomationPlacement`.
- altanode runs the automations of its profile and those a paired device assigns through `automations.put` (the
  `automate` permission). Each due time is claimed once; before a run the node checks `fleet/automation-policy.json`,
  the credential kind, and that the assigning device keeps the permission and the definition is the one it assigned.
- CodeAlta Desktop: **Runs on** a paired machine copies a personal automation there (`DesktopAutomationPlacement`),
  runs it there by hand and lists its runs as the machine reports them.

The authority of an assignment is the authenticated, paired device on a direct channel. No document is signed yet.

## What is left

- [x] **Signed versions.** The desktop signs each version it places (the canonical TOML of the table, its id, a version
  number, the device id; ECDSA P-256 with the device key, as `FleetSignedRequest` signs REST calls). The node stores the
  signature beside the definition (`assigned-automations.json`) and checks it, not only the channel, before a run: the
  fingerprint check of `NodeAutomations.Check` becomes a signature check.
  - Done as `FleetAutomationVersion` (Protocol) and `FleetAutomationSigning` (Server: sign, seal for the machine and
    the person's controllers with `FleetKeyWrap`, open). The node keeps the highest number per automation, removals
    included, so a replayed or older version is refused; an unsigned `automations.put` is refused. The desktop numbers
    versions from the time in milliseconds, so a lost placement record still numbers after what a machine holds.
- [x] **Server resources.** `automations` and `runs` in the REST API v1 (`doc/fleet/protocol.md`): `PUT
  /api/v1/automations/{id}` stores a signed version (the content sealed to the owner's devices and the assigned machine
  in a Private space, so the server sees only the facts: id, owner, version, machine, schedule kinds, enabled), `GET` lists
  them for the owner, `DELETE` removes. Owner checks as for update campaigns (an owner's controller device).
  - Done: migration `0007_automations` (after `0006_statistics` of the fleet statistics). The server takes only sealed versions (never a definition in clear), one row per automation and
    machine. A removal is a signed version too (`PUT` with `removed`); `DELETE ?machine=` only forgets a removal the
    machine acknowledged. Personal scope: the caller is a controller device of the person who owns the machine (the node
    checks the same against its anchored list), not an organization owner. `GET /api/v1/automations/{id}/runs`.
- [x] **Assignment over the control connection.** A delivery `automations.assigned` (sealed version) and
  `automations.removed` to the machine; the node acknowledges, verifies the signature against the signed device list
  (`FleetDeviceListVerifier`), and writes its copy as it does for `automations.put`. Offline nodes get it on reconnect
  (the per-node delivery sequence).
  - Done with one event, `automations.assigned`, whose versions may be removals; answered by `automations.ack`. The
    server keeps each version pending until acknowledged and sends the pending ones on each connection (no separate
    delivery sequence). The node checks the signer again before each run against the list it last verified.
- [x] **Run facts.** The node publishes runs as facts (`facts.publish`: automation id, version, due time, state, times,
  tokens and cost, never the prompt or the answer); the server keeps them for the console and the desktop lists them
  for machines it reaches only through the server.
  - Done as `automations.runs` (not `facts.publish`, whose session facts another branch changes): run id, version,
    trigger, status, times, session id, a failure reason, and the tokens and cost of the session (summed from its usage
    records into the engine's run record when the run ends; migration `0008`); resent on each connection.
- [x] **Reconciliation by (automation, due time).** The node's claim store already keys claims by occurrence: the server
  records the claims it learns and refuses a second run of the same occurrence after a takeover; `AutomationClaim.LeaseUntil`
  is the lease it reads.
  - Done with `automations.claim` before the run starts (`IAutomationClaims.ConfirmAsync`), with a lease; a server that
    does not answer leaves the node's own claim in force. While a run works, the node claims its due time again about
    once a minute, which renews its lease on the server; the reported end closes it.
- [x] **Desktop.** **Runs on** offers the machines of the linked server (`DesktopFleet.SyncOrganizationMachinesAsync`
  hosts, `IsPaired == false`), placed through the server rather than `automations.put` on a direct channel.
  - Done: `DesktopAutomationPlacement.ServerRoute` (`DesktopFleetServers.AutomationRoute`) seals each version with the
    server's anchored device list and `PUT`s it; where it stands (pending, taken, refused) and its runs come from the
    server; leaving the machine sends a signed removal. **Run now** goes through the server as a run request the desktop
    signs (`POST …/{id}/run`, event `automations.run`); the node verifies it and runs its run id once.
    Once the machine took a removal away, the desktop asks the server to forget it (`DELETE`).
- [x] **Tests.** The multi-host harness: desktop, server and node in one process; a server that is down while a
  schedule fires (the node fires it), a hostile server that alters a version (the node refuses it), a revoked device.
  - Done in `NodeServerAutomationTests` (server and node host, the desktop's client), `DesktopServerAutomationPlacementTests`
    (desktop placement, server, a node connector), `AutomationServerTests` and `FleetAutomationSigningTests`; checked end
    to end with altaserver, altanode (`--no-listen`) and an isolated CodeAlta Desktop.

## Smaller follow-ups of the first part

- [ ] The desktop shows the next due time of a placed automation as its machine reports it (`FleetAutomationInfo.NextDue`).
- [x] A remote run's session opens from the run list when the machine lists it (today: when it is among the sessions
  the machine host lists).
  - Done: a run of another machine with a session is always a link; opening it reads the sessions of the window again,
    and says so when the machine does not list that session now.
- [ ] Pausing all automations on the desktop does not pause those placed on machines; decide whether it should.
- [ ] `altanode automations` (list, runs, policy) for the owner on the node, instead of editing the policy file.
