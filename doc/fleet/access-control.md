# Ownership and access control

Who owns each thing of a fleet, and who may do what with it. Ownership is decided first, because everything else
follows from it: who may edit an automation, who sees its runs, whose credentials a run uses, whose budget it spends,
who answers its approvals, who is told when it fails, who inherits it when a person leaves. How people and devices
prove who they are is in [identity](identity.md); the keys that make some of these rules hold against the server
itself are in [security and privacy](security-and-privacy.md).

## Principles

1. **Everything has exactly one owner**: a person, a team or the organization. Ownership is recorded, visible, and
   transferable; it is never implied by who happened to create something.
2. **Authorship is kept apart from ownership.** `createdBy` (a person, or an agent session acting for one) is
   immutable history; the owner is who answers for the thing now.
3. **Denied unless granted.** A capability comes from a role in a scope or from an explicit grant.
4. **Managing is not reading.** Management capabilities are inherited down the scopes; reading content never is: it
   comes from ownership or from a share.
5. **The machine decides what runs on it.** The server's grants are necessary, never sufficient: a node checks its own
   policy and the signatures of the request ([what the node decides](#what-the-node-decides)).
6. **Authority to run things is signed.** Whatever makes a machine run an agent carries the signature of the device of
   a person entitled to ask, directly or through a definition they signed; the server delivers, it never authorizes
   alone.
7. **One model for the whole fleet.** The REST API, the change feed, the relay, deliveries, statistics, search and the
   console apply the same rules; the console hides what a person may not do, but never relies on hiding.

## Principals

| Principal | Acts |
| --- | --- |
| **Person** | For themselves, with their devices |
| **Team** | Through its maintainers; owns team machines, team automations, team budgets |
| **Service identity** | A team's or the organization's identity for unattended work (team automations, node pools): it has grants and account credentials, never a password or a subscription |
| **Device / node** | Only as itself: it publishes its facts and runs what it was validly asked to run |
| **Session** | For its principal (a person or a service identity), narrowed by its own restrictions, through delegated tokens across machines ([identity](identity.md#agents-acting-for-people)) |
| **Integration** | A scoped, expiring service token for a tool outside CodeAlta (a CI job, a script) |

## Resources and their owners

| Resource | Owned by | Lives in | Notes |
| --- | --- | --- | --- |
| Organization | Its Owners | — | A household or a company; one per server |
| Team | The organization, run by its maintainers | Organization | |
| Project | The organization | Organization, granted to teams | Keyed by its repository; a person's project in a Private space stays theirs |
| Machine | A person (personal machine) or a team (team machine) | Organization | Its owner sets its local policy |
| Session | Its person, or the service identity of the automation that started it | Project or chat, on a machine | Content is the owner's; others read it through a share |
| Share | The session's owner | Session | |
| Channel and messages | The project's teams; a message by its author | Project | |
| Automation | A person, a team or the organization | Personal, project, team or organization scope | Editors, operators and approvers are grants ([orchestration](orchestration.md)) |
| Workflow script | A project (in its repository) or a library owner (person, team, organization) | Project or library | Versioned; a version is approved before unattended runs use it |
| Run | The owner of its automation, or the person who started it by hand | Automation or session | Records its initiator and its trigger |
| Organization provider and its secret | A team or the organization | Organization | API keys or gateway keys for unattended work ([orchestration](orchestration.md#credentials-of-unattended-runs)) |
| Budget | The owner of the scope it limits | Person, machine, project, team, organization | |
| Policy bundle | The organization | Organization | Signed by the policy key ([security](security-and-privacy.md#policies-and-the-kill-switch)) |
| Statistics facts | The owner of the session or run they measure | Wherever the session was | Visibility by role and policy ([statistics](statistics.md#who-sees-what)) |
| Plugin distributed to the organization | The organization | Organization | Installed on members' instances by policy |
| Enrollment token | Its creator | Organization | Single use |
| Audit log | The organization | Organization | Read by auditors and owners |

## Roles and grants

### Organization roles

| Role | Can | Cannot, unless granted |
| --- | --- | --- |
| **Owner** | Everything: people, teams, roles, SSO, policies (with the policy key), the organization | Read content not owned by them or shared with them |
| **Admin** | People, teams, team machines, projects, organization automations and providers, budgets, audit; policies, signed by a policy device | Same |
| **Member** | Their devices and machines, their sessions and automations, shares, the channels of their projects, the team resources their team roles give | Administer |
| **Auditor** | Read the audit log, fleet facts and statistics as policy allows | Read content; change anything |
| **Guest** | What is shared with them | Machines, channels, automations they were not invited to |

### Team roles

A team has **maintainers** (manage the team's machines, automations, budgets, providers and members) and
**members** (use the team's machines and projects, see the team's runs, answer approvals routed to the team).

### Grants

A grant is `(subject, capability, scope)`. Scopes nest: organization, team, project, then the resource itself.

| Resource | Capabilities |
| --- | --- |
| Machine | `view`, `start` (per space), `manage` (configure, update, revoke), `logs` (owner only by default) |
| Session | `view-facts`, `view-content` (owner and shares only), `interact`, `approve`, `move`, `share`, `delete` |
| Automation | `view`, `run` (by hand), `pause`, `edit` (a new signed version), `approve` (its runs' approvals), `manage-access`, `transfer`, `delete` |
| Run | `view-facts`, `view-content` (as the automation's owner allows), `cancel`, `retry` |
| Workflow script | `use`, `publish`, `approve-version` |
| Statistics | `view-own`, `view-team`, `view-organization`, `view-per-person` (separate, see [statistics](statistics.md#who-sees-what)) |
| Channel | `read`, `post`, `moderate` |
| Provider and secret | `use` (by nodes and runs of the scope), `manage`, `rotate` |
| Budget, policy | `view`, `set`; policies also `sign` (policy-key holders) |
| Audit | `read`, `export` |

Management capabilities flow down the scopes (a team maintainer manages the team's automations); `view-content`
never does: an Admin sees that a session exists, where it runs and what it costs, not what it says.

**Content of team-owned work.** A session started by a team automation belongs to the team's service identity, so the
team's members see its content by their team role: in a Managed space through the server, in a Private space through
the team's space key wrapped to their devices.

## What the node decides

The server can ask; the node answers. Each node keeps a local policy that the server cannot loosen:

- who may view and drive its sessions, start sessions on it, and run automations on it, per space: its owner, people
  it names, or the members of a team as listed in the organization's **signed** policy bundle (so a compromised server
  cannot add itself to a team machine's users);
- the strictest sandbox a remote start must use; `bypassPermissions` never for a remote start unless its owner allows
  it;
- whether it accepts agent-started sessions from other machines (off by default) and automations of others (off by
  default for personal machines);
- which credentials it lends to which runs ([orchestration](orchestration.md#credentials-of-unattended-runs)).

A request to run something on a node is valid only when signed by an entitled person's device: a start or a prompt
directly; an automation's run through the version of the automation an editor signed, the trigger being only the
event that the signed definition accepts. The first start by a new person asks the node's owner.

## Ownership over time

- **Transfer**: an owner (or a maintainer of the scope) gives a resource to another person or team; the new owner
  accepts; the audit log records both.
- **A person leaves** (or is deprovisioned by SCIM): their devices and delegations are revoked; what they owned in team
  or organization scopes (automations, workflow scripts, organization providers) goes to the scope's team or to a named
  successor, paused until that owner resumes it; their personal machines leave the fleet; what they shared stays
  shared or goes, per the organization's policy.
- **A team is dissolved**: its resources go to the organization (owners decide), never vanish.
- **Orphans are impossible by construction**: a resource cannot lose its last owner; the console lists resources whose
  owner is suspended.

## Agents and access

- An agent acts with its principal's grants, narrowed by its session (space, tools, permission mode, sandbox). A child
  never gets more than its parent, on any machine.
- What an agent creates is owned by its principal and marked as created by that session. Anything that would run
  unattended (an automation, a schedule, a grant to a service identity) stays **disabled until a person enables it**,
  which signs it with their device.
- Changing access (grants, roles, transfers, policies) is read-only for agents, as protective settings are today
  ([live tool](../live-tool.md#safety-rules)): an agent can propose, a person applies.

## Access and keys

In a Private space, access to sealed data is a key operation, not only a flag:

- granting a person or a team `view-content` on sealed data wraps the needed keys to their devices; revoking it starts
  a new key epoch;
- the server cannot do either: a device that holds the keys does it (the console, when an authorized person approves);
  a grant made while no such device is online stays **pending** and says so.

In a Managed space the organization's key covers shared content, and access is enforced by the server.

## Where access matters

Access control reaches almost every part of the fleet; each document applies these rules:

| Area | What access decides | Enforced by |
| --- | --- | --- |
| Remote sessions and moves | Who sees, drives, approves, moves | The node, the server for routing |
| Shares and channels | Who reads and posts | The server; keys in Private spaces |
| Automations and runs | Who edits, runs, pauses, approves, sees runs; which machines run them; whose credentials | The server (scheduling), the node (signed definitions, local policy) |
| Workflow scripts | Who publishes and approves versions | The server; the node checks the approved hash |
| Statistics | Who sees which granularity, per-person numbers | The server's queries; sealed names on devices |
| Machines | Who starts sessions, manages, reads logs | The node |
| Organization providers and secrets | Which nodes and runs may use them | The server distributes sealed copies; the node keeps them |
| Budgets and policies | Who sets and signs them | The server; nodes verify signatures |
| Audit | Who reads and exports | The server |
| Search and the change feed | What each caller may see listed | The server filters per caller |
| Notifications | Who is told, and who may answer | The server; approvals bound to attempts |
| Plugins of the console | What a plugin may read or change in the fleet | The desktop host ([console](console.md#plugins)) |

## At home

A household has an Owner and its members, two roles that cover everything: the Owner's machines, sessions and
automations are theirs; members see what is shared with them. Grants, teams and transfers stay under **Advanced** in
the console until someone needs them.

## Audit

Every grant, role change, transfer, enablement of unattended work, policy signature and access to Managed content by
an administrator is an audit event ([security](security-and-privacy.md#audit-log)). Owners can export **who can do
what** for access reviews.
