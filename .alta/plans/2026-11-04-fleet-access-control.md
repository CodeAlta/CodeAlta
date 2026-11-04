---
title: Ownership and access control of the fleet (M4, 4.0)
status: in-progress
created: 2026-11-04
summary: Principals, organization and team roles, grants, owners of resources, transfers, team machines, a signed access document that nodes verify themselves, deprovisioning, agents and access, access and keys, audit, and the People and access page of CodeAlta Desktop.
---

# Ownership and access control of the fleet

The spec is [access control](../../doc/fleet/access-control.md); this plan cuts it into slices, each a commit with its tests.
The protocol only gains: new REST endpoints under `/api/v1/access`, `/api/v1/teams`, `/api/v1/transfers`, new optional
fields, a new signed document and one new control notification (`access.changed`). One SQLite migration: `0014_access.sql`.

## Decisions taken here

- **The server enforces roles and grants on its REST API; the node trusts only the signed access document.** The
  document (`codealta-access/1`) is a chain like the device list: each version names the hash of the one before and is
  signed by a controller device of a *signer*. Version 1 is signed by a controller device of the organization's owner
  (the person of the first device-list entry, which every device anchored). Signers are the owners and the admins the
  document lists; only an owner's device may change who the owners and signers are. A node pins the chain per server as
  it pins the device list, so a compromised server can neither add itself to a team nor take a suspension back.
- **What the document says**: owners, signers (admins), teams with maintainers and members, team machines (machine to
  team), whether a team machine runs automations of its members, and suspended people. Grants, transfers and roles
  other than owner/admin stay server-side: they decide REST answers, never what runs on a machine.
- **Team changes made by a maintainer who is not a signer wait** in the server's model until a signer's console signs
  the next version ("Changes to sign"); the server shows the difference between its model and the last signed version.
- **Team machines**: enrolled as today, then given to a team by a transfer that a signer signs into the document. On a
  team machine, team members may view, drive and start their *own* sessions (the node records who started each
  session); maintainers also manage it. The device-list owner of the machine gets nothing from that ownership once the
  machine is a team machine.
- **Suspension** (deprovisioning): the person is suspended on the server at once (their devices refused by the REST
  API and the relay), their devices revoked with entries a signer's console signs, their resources given to a successor
  paused, and the next signed document lists them as suspended: nodes refuse their devices, stop their sessions on team
  machines and refuse automations they signed.
- **Agents**: `alta fleet access` reads; `alta fleet access propose` records a proposal that a person applies or
  dismisses on the People and access page. No command applies an access change.
- **Access and keys**: a `view-content` grant on data of a Private space is recorded **pending** until a device that
  holds the keys confirms it wrapped them; the server never marks it active itself.

## Slices

- [x] 1. Access model: names (roles, team roles, capabilities, principals, resources), the evaluator (management
      inherited down the scopes, `view-content` never), migration 0014, tests of the evaluator.
- [x] 2. People, roles, teams, service identities and grants on the REST API, audited; the last owner cannot go;
      `/audit` for owners, admins and auditors only; "who can do what" export.
- [x] 3. Owners of resources and transfers: owner, author and pause of machines and automations, transfers offered and
      accepted, resources whose owner is suspended listed.
- [x] 4. The signed access document: protocol, chain verifier, server storage and drafts (done with slice 1–3), signing from the console.
- [x] 5. What the node decides: anchored access chain, team machines (own sessions only), suspended people refused,
      automations of team machines and of suspended signers refused, `access.changed` notification.
- [x] 6. Deprovisioning: suspend, revoke devices, transfer to a successor paused (server done with slice 1–3), stop work on
      team machines (end-to-end test).
- [x] 7. Agents and access (`alta fleet access` commands, proposals, the skill) and access and keys (pending grants).
- [x] 8. People and access in CodeAlta Desktop (Settings > Fleet): simple at home, Advanced for teams, grants and
      transfers, changes to sign, proposals, deprovisioning, export.
- [x] 9. User guide and the fleet docs.

## Not in this plan

SCIM (M5), shares and channels (collaboration after 4.0), team and organization automations running as service
identities with account credentials (their own M4 plan): this plan gives them owners, grants and the node checks they
build on.
