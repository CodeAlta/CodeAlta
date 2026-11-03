# CodeAlta Server

The coordinator of a fleet. It knows who is who, what is online and who may reach what; it routes and relays; it keeps
what people share. It runs no agent and reads no content it was not given. Its names (`CodeAlta Server`,
`altaserver`) are decided ([readme](readme.md#naming)).

## What it does

| Responsibility | Content |
| --- | --- |
| REST API | The complete management API, its OpenAPI document and its change feed: everything the console does ([protocol](protocol.md#the-servers-rest-api), [console](console.md)) |
| Identity | People, sign-in methods, devices and the signed device list, enrollment, tokens ([identity](identity.md)) |
| Access control | Owners, roles, grants, service identities, transfers, enforced on every answer ([access control](access-control.md)) |
| Directory and presence | Machines and their inventory, published sessions (facts and sealed metadata), people online. Presence lives in memory |
| Routing and relay | Splices session channels between devices; delivers child answers, cross-machine messages and queued commands |
| Ownership | Which node owns each published session, and its epoch ([sessions across machines](sessions-across-machines.md#ownership-and-fencing)) |
| Replicas | Stores the encrypted replicas of journals it cannot read |
| Collaboration | Project channels, shares, handoffs, notifications ([collaboration](collaboration.md)) |
| Policies | Stores and distributes the signed policy bundles; enforces what is its own (who may reach whom) |
| Automations and runs | Stores automations and their signed versions, fires schedules, receives inbound events at its hooks ingress, places runs on eligible nodes, keeps the records of runs ([orchestration](orchestration.md)) |
| Statistics | Keeps the facts machines publish, rolls them up, answers queries by scope ([statistics](statistics.md)) |
| Audit | The append-only, chained audit log and its exports |
| Web pages | Setup, sign-in (passkeys, SSO), device approval, the phone's page; no administration interface |

What it does not do: run an agent, hold a model credential in clear (organization providers' keys are sealed to the
machines that use them), read a journal, clone a repository (unless an organization turns on the GitHub token broker,
which mints tokens and never clones).

## Stack

- **.NET 10, ASP.NET Core minimal APIs and WebSockets on Kestrel.** One process.
- **Packaged** as a self-contained single file per platform (ReadyToRun) and a container image (`linux/amd64`,
  `linux/arm64`). Not Native AOT: the OpenID Connect authentication it needs is not supported under AOT, and SignalR is
  only partly.
- **Identity**: ASP.NET Core Identity and OpenIddict.
- **Storage**: a small store interface with two implementations, SQLite (`Microsoft.Data.Sqlite`, the default) and
  PostgreSQL (`Npgsql`), SQL with Dapper or ADO.NET, numbered SQL migrations, one contract test suite run against both.
  No EF Core (its AOT and precompiled-query support is still experimental, and two dialects stay honest in plain SQL).
- **Blobs** (snapshots, replicas, attachments of messages): the file system by default; S3, Azure Blob Storage or
  Google Cloud Storage behind an interface.
- **JSON** with source-generated contracts shared with the nodes (`CodeAlta.Fleet.Protocol`).
- **OpenTelemetry** for its own traces, metrics and logs; an OTLP endpoint that forwards what nodes send, without storing
  it ([observability](observability.md#where-telemetry-goes)).
- It references `CodeAlta.Fleet.Protocol` only: never the agent runtime (a guardrail test checks it).

## Inside

- **One writer per thing**, as in the rest of CodeAlta: a mailbox per organization for membership and policies, per
  node for its connection and deliveries, per session for ownership, so the server needs no locks spread over its
  state.
- **Presence and routes in memory**; only state changes are persisted. A heartbeat is never a database write.
- **Backpressure everywhere**: bounded channels per connection, a write budget, and refusal (`server_busy`, with a
  delay) rather than unbounded queues.

## Data

| Table | Content |
| --- | --- |
| `organizations`, `people`, `logins`, `passkeys` | Accounts and their sign-in methods (logins keyed on issuer and subject) |
| `devices`, `device_list` | Devices, their public keys, status, and the signed list entries |
| `teams`, `memberships`, `grants`, `service_identities` | Teams, grants `(subject, capability, scope)`, the identities of unattended work |
| `automations`, `automation_versions`, `runs`, `trigger_events` | Automations, their signed versions, runs and the events that started them |
| `providers` | Organization providers and their keys sealed to machines |
| `machines`, `machine_inventory` | Node records (owner, labels, capabilities) and their last inventory |
| `projects` | Repository keys or blind indexes, per space |
| `sessions` | Published session facts and the sealed metadata blob |
| `ownership` | Owner node and epoch per session |
| `deliveries` | Queued messages per node, with sequence and time to live |
| `replicas` | Per session: epoch, acknowledged offset, blob reference |
| `channels`, `messages`, `shares` | Collaboration |
| `policies`, `wrapped_keys` | Signed bundles; keys wrapped to devices |
| `facts`, `rollups` | Statistics rows published by machines (versioned, keyed by machine, session, quarter hour, family and dimension) and their roll-ups |
| `audit` | The audit log, append-only, chained |

Portable by construction: text ids (ULID or GUID), UTC timestamps as 64-bit integers, JSON as text, `ON CONFLICT`
upserts, no engine-specific functions.

## SQLite or PostgreSQL

SQLite is not the limit; the topology is. Measured for this plan (SQLite 3.50 in WAL mode, an NVMe disk, a Ryzen 9
9950X, one run; a cloud VM with network-attached disks flushes much more slowly):

| Workload | SQLite |
| --- | --- |
| One-row transactions, `synchronous=NORMAL` | about 45,000 commits per second |
| One-row transactions, `synchronous=FULL` | about 850 commits per second (one disk flush each) |
| 100-row batches | about 245,000 rows per second |
| Heartbeat upserts for 1,000 nodes | about 49,000 per second |

| Scale | Nodes | Writes per second (heartbeats every 15 s in memory, a batch per second per active node) | SQLite |
| --- | --- | --- | --- |
| A household, a small team | up to ~100 | ≤ 100 | Trivial |
| Hundreds of people | ~1,000 | ≤ 1,000 | Comfortable |
| Thousands | ~10,000 | ≤ 10,000 | Feasible with batching, but one machine, no high availability, long restores |

**PostgreSQL is needed for**: more than one server instance, updates without downtime, managed point-in-time recovery,
or a platform without a local disk (Azure Container Apps, Cloud Run). Not for throughput.

- SQLite's WAL **does not work on a network file system**: never on Azure Files, EFS or a bucket mounted with FUSE.
- **Backups**: the server copies its database with SQLite's backup API on a schedule (as CodeAlta's application
  database does) and can stream it with Litestream to object storage; Litestream is asynchronous backup, not failover.
- `altaserver migrate --to postgres` copies a SQLite database into PostgreSQL and checks the counts.
- PostgreSQL is small too (a 1 vCPU, 1 to 2 GB machine is plenty for a coordinator); the worry is operations, not size.
  Managed PostgreSQL starts around 10 to 15 dollars a month on the three clouds.

## Availability

Nodes are autonomous: a server that is down stops visibility, relay and sharing, not work
([architecture](architecture.md#offline-and-failures)). So:

- **One instance** with backups is the default, also for most organizations: restart in seconds, nodes reconnect and
  replay.
- **High availability** (milestone 5) runs two or more instances on PostgreSQL; a channel between two devices
  connected to different instances needs cross-instance routing (PostgreSQL `LISTEN/NOTIFY` for small fleets, a
  message bus beyond). Orleans would model it elegantly but is not worth its weight before then.

## Restore and upgrades

- **Upgrades** go from one minor version to the next (N-1 to N) directly. The server takes a backup, then runs its
  numbered migrations at start under a migration lock, each in a transaction; a failed migration leaves the previous
  version in place. Going back means restoring the backup taken before the upgrade. `altaserver update` does both: the
  running server copies its database to `backups/` (SQLite's backup API, the last five kept), then switches; a new
  version that does not serve within its trial is rolled back and that copy restored
  ([deployment](deployment.md#updates)). In a campaign of the fleet the server takes this step last, after its nodes,
  and a campaign that a stop interrupted goes on at the next start. Until `altaserver install` exists, the switch works for a server run in the
  foreground: the process stays and runs the new version as its child.
- **A backup is a consistent set**: the database, the blobs it references and the server's own keys (TLS, token
  signing), the keys encrypted. The server holds no key that decrypts Private data, so a backup holds none either.
- **Restore epochs**: a restored server starts a new epoch. Nodes notice that it is behind them and publish again what
  it lacks (facts, inventories); revocations are signed device-list entries that devices also keep, so a restored
  server cannot bring a revoked device back; nodes refuse a run they already recorded, so a restored queue does not
  repeat work.
- **Deletion**: unlinking a space removes its facts and sealed copies from the server; a published session made Private
  later has its facts, sealed metadata and replica deleted there; retention deletes the same way, and backups expire
  after the retention period so that deleted data does not live on in them. Deleting ciphertext at the server does not
  take back copies that recipients already have.
- **Restores are tested**: the release checks restore a backup into a fresh server and reconnect nodes to it.

## Administration

- **From CodeAlta Desktop**: the console's canvases (Server, People and access, Machines, Policies and budgets, Audit)
  for the owner and admins ([console](console.md)), through the REST API like any other client.
- **The REST API** for everything else: `altacli fleet`, plugins, scripts and integrations reach every operation the
  console reaches.
- **`altaserver` commands** on the machine: `run`, `install` (service), `status` (with its versions; `GET
  /api/v1/server/update` reads them), `update [--version <v>] [--source <feed>] [--check]`, `backup`, `restore`,
  `migrate --to postgres`, `audit export`, `recover-owner` (on the server's own terminal only: it gives the Owner role
  back, never needs the recovery key of encrypted data). Today: `run`, `status`, `setup-code`, `update`.
- **Web pages**: setup, sign-in, device approval, the phone's page. No administration interface: that is a choice, not
  a gap ([console](console.md#why-the-desktop-and-what-it-costs)).

## One organization per server

A server hosts one organization (a household, a company). It is not a multi-tenant service: organizations that must
not trust each other run separate servers. CodeAlta offers no hosted service: the server is self-hosted only
([security](security-and-privacy.md#compliance)).

## Licence

CodeAlta Server is open source under CodeAlta's licence (BSD-2-Clause), in one edition: the features of organizations
(SSO, access control, SCIM, high availability, brokered credentials) are part of it. Its dependencies keep to
licences compatible with that (MIT, Apache 2.0, BSD, PostgreSQL's): no AGPL or GPL component inside.
