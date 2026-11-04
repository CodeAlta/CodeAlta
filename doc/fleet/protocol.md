# Fleet protocol

How devices, nodes and the server talk, in three planes:

| Plane | Carries | Transport |
| --- | --- | --- |
| **Management** | Resources and queries: machines, automations, runs, statistics, people, grants, policies, audit | The server's REST API and its change feed ([below](#the-servers-rest-api)) |
| **Control** | What keeps the fleet alive: presence, publications, deliveries, relay requests, ownership | One outbound WebSocket per device to the server |
| **Content** | What only devices read: live sessions, moves, replicas' bytes, logs | Session channels, end to end between devices, direct or relayed |

The control and content planes use one protocol in every topology ([architecture](architecture.md#three-topologies)):
the same messages flow over a direct connection between two machines or through the server's relay. The management
plane exists only with a server.

## Goals

- **Durability lives in the protocol, not in the connection.** Sleep, proxies, outages and forced disconnects are
  normal; sequences, acknowledgements and cursors make a reconnection resume, never repair.
- **Safe retries.** Every side-effecting request is idempotent; a command bound to one run never applies to another
  ([runtime](../runtime.md#owned-session-commands)).
- **Content never readable by a relay.** The server routes and stores what it is allowed to see
  ([security](security-and-privacy.md#what-the-server-sees)).
- **Versioned from day one**, with a compatibility window, so a fleet upgrades in waves.
- **Bounded.** Every stream and queue has a size; a slow reader costs updates, never memory.

## Layers

| Layer | Content |
| --- | --- |
| Services | `node.*`, `sessions.*`, `moves.*`, `projects.*` (node API); `presence.*`, `directory.*`, `facts.*`, `relay.*`, `ownership.*`, `replica.*`, `chat.*`, `shares.*`, `policy.*`, `keys.*` (server API) |
| Messages | Request, response and event envelopes in JSON, contracts source-generated; idempotency keys; `traceparent` |
| Channels | Several streams over one connection, with credit-based flow control; lossy (display) or lossless (transfers) |
| Security | To the server: TLS, plus a proof of possession of the device key on every request. Between devices: TLS 1.3 with the node's key pinned and a signed device proof, direct or relayed |
| Transport | WebSocket (`wss`) on port 443 of the server, or on the node's port for a direct connection |

## Connections

1. **The control connection**: every device of a fleet keeps one outbound WebSocket to the server
   (`wss://<server>/fleet/v1`). It carries presence, publications, the directory, deliveries and relay requests.
   Nothing connects into a node.
2. **The session channel**: a viewer that opens a session of another node opens a channel to that node.
   - **Relayed** (the default with a server): the viewer asks `relay.open`; the server checks that the viewer may
     reach the node, asks the node to accept, then splices the two streams. Relaying is the path that always works;
     direct paths are an optimization (measured by Harness, one of the products studied: direct peer-to-peer
     connections through home routers failed in 9 trials out of 9).
     Carrier (built in M3a): `relay.incoming` is an event to the node, which answers with the request `relay.accept`
     (or `relay.close` to decline); each side then opens a WebSocket of its own, `wss://<server>/fleet/v1/relay/{ticket}`,
     with a one-time ticket and an upgrade signed by its device key, and the server copies binary frames from one to the
     other. The control connection stays free, and a relay that drops costs nothing but itself. Limits per relay (bytes,
     idle time) and per device (relays at once); the audit records who reached whom, when and the bytes.
   - **Direct** (paired machines, or a node reachable on the LAN or the tailnet): the viewer connects to
     `wss://<node>:2585/fleet/v1/channel`.
   - Inside both: TLS 1.3 with the viewer as client, the node as server; the viewer pins the node's key (from pairing,
     or from the signed device list of the fleet) and proves its own device key with a signature bound to the TLS
     channel. The relay sees ciphertext only.
3. **Transfers** (a session that moves, a replica) are lossless streams of the session channel, or of a second
   connection when the first one carries live views.

## Handshake

The first frames of every connection, in both directions (patterned on OpenClaw's gateway protocol, the most worked
example found):

```text
server/node → hello.challenge { nonce, time, protocol: {min, max}, policy: {maxFrame, heartbeat} }
client      → hello { protocol: {min, max}, client: {kind, version, os}, device: {id, proof}, resume: {cursors} }
server/node → welcome { protocol, features, cursors, policy }  |  error { code, hint }
```

- `proof` signs the nonce, the channel binding and the device id with the device key (ECDSA P-256).
- Errors say what to do, so a client reacts instead of retrying blindly: `unauthenticated` (bad or expired credential:
  `reauthenticate`), `pending_approval` (wait for `device.approved`), `forbidden` (authenticated, not allowed),
  `revoked`, `update_required` (version outside the window), `server_busy` (`retry_after`).
- A connection the server closes later (a revocation, a reader that stayed behind, a newer connection of the same
  device) first receives a `connection.closing` event with the same kind of error.

## Envelopes

```json
{ "t": "req", "id": "01JBX...", "m": "sessions.send", "p": { }, "idem": "rk-...", "tp": "00-4bf9...-01" }
{ "t": "res", "id": "01JBX...", "ok": true, "r": { } }
{ "t": "res", "id": "01JBX...", "ok": false, "err": { "code": "session.stale_run", "hint": "refresh" } }
{ "t": "evt", "ch": 7, "e": "session.display", "seq": 4211, "p": { } }
```

- Contracts are C# records with source-generated `System.Text.Json` (no reflection, AOT-friendly), in
  `CodeAlta.Fleet.Protocol`; the TypeScript of the page and the phone is generated from them, as the desktop's RPC
  client is today.
- Binary payloads (bundle chunks, replica segments) go in binary frames with a small header (channel, sequence,
  length).
- `idem` is the idempotency key: for session commands, the owned command's client retry key.
- `tp` is the W3C trace context, so work that crosses machines is one trace ([observability](observability.md)).

## Durability

| Flow | Rule |
| --- | --- |
| Node to server: session facts | Each fact carries `(sessionId, epoch, version)`; the server keeps the highest version and refuses a stale epoch. The source is the journal and its projection, with a watermark per server: the journal is the outbox. |
| Node to server: statistics facts | One set of rows per session with `(sessionId, epoch, version)` and a generation of the machine's outbox (`statistics.publish`); the server keeps the highest version per machine and session, refuses an older epoch, and replaces everything of a machine that starts a new generation. A batch carries a sequence, acknowledged as for the other facts and given back in the welcome, so a server restored from a backup gets every set again. `statistics.withdraw` takes back the sets of sessions no longer published. Announced by the feature `statistics`. |
| Node to server: other facts (inventory, usage, audit events) | A small outbox table on the node, with a per-server sequence; the server acknowledges the highest contiguous sequence; the node resends from there after a reconnection. Duplicates are no-ops. |
| Server to node: deliveries (child answers, cross-machine messages, chat, policy) | A per-node delivery sequence with acknowledgements and a time to live; queued while the node is offline. A delivery that carries content (an answer, a message) is sealed to the receiving device: the server routes it without reading it. |
| Replicas | `replica.append(sessionId, epoch, generation, fromOffset, bytes)`; the server stores what follows the last acknowledged offset, refuses a gap (with the expected offset), a stale epoch or an older generation (a rewind starts a new one). |
| Viewers | Display: full replacements with a revision and a gap flag (lossy by design). History: windows by journal cursor. After a reconnection: the current display, then the history after the viewer's cursor. |
| Commands | The node keeps receipts by idempotency key, across restarts: the results of the latest calls (bounded), and every key for the lifetime of a retry token (7 days), after which a client never retries it. A retry returns the first receipt; a call still running keeps its receipt; an evicted receipt, or a call a stop interrupted, answers `expired`, never runs again. Keys are bounded (16,384 by default): when that many are within their lifetime, a new key is refused with `server_busy` and a `retryAfter` until the oldest expires, rather than forget a key a client may still retry. |

Transient streaming deltas (tokens of an answer) are never replicated or acknowledged: the display replacement that
follows carries the state.

## Flow control and limits

- A credit window per channel (bytes in flight); display channels drop intermediate replacements; transfer channels
  wait for credits.
- A write budget per connection; a reader that stays behind is disconnected with a reason and reconnects to the
  current state.
- Limits announced in `hello.challenge`: maximum frame (a few MiB), heartbeat period, maximum channels.

## Heartbeats and reconnection

- An application ping every 20 to 30 seconds (it carries the node's state), since proxies and Cloudflare close idle
  WebSockets after about 100 seconds, and load balancers after 60.
- Forced disconnects are expected (Cloud Run ends any WebSocket after 60 minutes): reconnection with backoff and jitter
  (up to a minute) and resumption by cursors is the normal path, one round trip.

## Versions

- Until 4.0 ships, the protocol may change. From 4.0 on, any desktop, node or server of version 4.x keeps talking to
  any other 4.x, because machines update at different times (a campaign updates the nodes before the server).
- Within 4.x, fields are only added, never repurposed or removed; unknown fields and events are ignored; an unknown
  method answers `method_not_found`. Features are announced (`features` in the welcome), never inferred from a version.
- `protocol {min, max}` in the handshake; a breaking change is a new major version, announced in the release notes.
- **Signed documents** (device-list entries, the access document, settings and policies) keep their format version
  within 4.x and grow by fields only; a verifier reads past the fields it does not know. A device *replays* the device
  list tolerantly: an authentic entry (chain, organization, a controller signer and its signature hold) whose operation,
  device class or addition its rules do not understand stays in the chain without effect, adds no member, and is shown
  as skipped; the removal of such a device still counts as a removal. What a device *signs* and what a server *keeps*
  must be understood (strict), so a newer feature waits for the server that knows it. A revocation is always a
  `remove` of the device, never a new operation, so that no older verifier misses one. A golden file of documents a
  newer minor signed (`CodeAlta.Fleet.Protocol.Tests/Golden`) is verified by every build; once released it does not
  change.

## Services

### Node API (over a session channel)

| Group | Requests and events |
| --- | --- |
| `node` | `info`, `capabilities`, `readiness(space)` (identity check, providers ready) |
| `projects` | `list`, `resolve(repositoryKey)`, `clone` |
| `providers` | `status`, `signIn.start` (device code flows), `signIn.poll` |
| `sessions` | `list`, `watch` (display stream), `history` (windows), `create`, `send`, `queue`, `steer`, `abort`, `answerAsk`, `answerPermission`, `jobs.list`, `jobs.output`, `jobs.stop`, `image`, `archive`, `restore`, `fork` |
| `moves` | `offer`, `accept`, `transfer`, `commit`, `abort` |
| `automations` | `put` (an automation as one table of `config.toml`, with the repository key of its project), `remove`, `list`, `run`, `runs`: the automations a paired device assigns to the node (**Runs on**), with the `automate` permission |
| `terminal` | `open` (a setup terminal, owner only, audited), `input`, `resize`, `close` |

They map onto what the desktop's RPC services already call (owned commands, the display projection, history
windows, asks, permissions, jobs); the node API is that surface, made remote, plus moves and readiness.

### Server API (over the control connection)

| Group | Requests and events |
| --- | --- |
| `presence` | `update` (in the heartbeat), `watch` |
| `directory` | `watch` (machines, published sessions, people, projects) |
| `facts` | `publish`, `ack` (session facts, usage, audit events) |
| `statistics` | `publish`, `withdraw` (statistics sets, [statistics](statistics.md#publication-rows-not-increments)) |
| `relay` | `open`, `incoming`, `accept`, `close` |
| `telemetry` | `settings`: the fleet's telemetry setting, signed by a controller device, sent when a device connects and when an owner changes it ([observability](observability.md#where-telemetry-goes)) |
| `automations` | `assigned` (the versions a controller device of the machine's person signed, sealed for the machine and the person's controllers, removals included; sent when one is stored for a machine online, and on each connection until acknowledged), `ack` (taken or refused, with the reason), `claim` (a due time, before its run: one run per automation and due time, whatever the machine; a node whose server does not answer runs the time it claimed itself; the same run claiming again renews its lease while it works), `runs` (run facts: version, trigger, status, times, session, tokens and cost, and when each automation is next due; never the prompt, the answer or what the trigger was about), `run` (a run asked by hand, signed by a controller device of the person for the machine and a run id, sent to the machine online; the machine refuses a request signed more than minutes away from its time, and runs a run id once) ([orchestration](orchestration.md#placement-and-dispatch)) |
| `hooks` | `event` (an event a hook of the server received for this machine, as it was pushed: headers and body; sent when it arrives for a machine online, and on each connection while it waits, a day at most; at least once), `ack` (taken by an automation, or refused with why, such as a signature that does not verify; the server then keeps only the facts) ([orchestration](orchestration.md#triggers)) |
| `update` | `instruction` (a campaign's signed manifest, or the signed automatic policy and its version), `cancel`, `policy` (the fleet's signed update policy, any mode, sent on each connection and each change), `report` (where the node stands; `updated` once its trial confirmed) ([deployment](deployment.md#updates)) |
| `ownership` | `commit` (a move), `takeover`, `changed` |
| `replica` | `append`, `read`, `status` |
| `deliveries` | `push`, `ack` |
| `changes` | The change feed of the REST resources the device may see |
| `keys` | The signed device list and wrapped keys ([security](security-and-privacy.md)) |
| `auth` | Token renewal by proof of possession; device approval while pending ([identity](identity.md)) |

Collaboration, automations, statistics, access and administration are REST resources (below); the control connection
carries their change notifications and the deliveries that must reach a device.

## The server's REST API

The management plane, and the whole surface of the console ([console](console.md#the-server-is-api-complete)).

- **Complete.** Every operation of the console, the phone, `alta fleet` and `altacli fleet` is an operation of this
  API; there is no private endpoint, so plugins and integrations can do what the console does, within their grants.
- **Resources** under `/api/v1/`: `organization`, `people`, `teams`, `grants`, `service-identities`, `devices`,
  `machines`, `projects`, `sessions` (facts and sealed metadata), `shares`, `channels` and `messages`, `automations`
  (with their versions), `runs`, `workflows` (the library, with versions), `hooks` (the ingress of inbound events),
  `providers` (organization providers and their sealed secrets), `statistics/query`, `budgets`, `policies`, `audit`,
  `enrollment-tokens`, `server` (health, version, backups, settings).
- **Described by OpenAPI 3.1**, generated by the server (`Microsoft.AspNetCore.OpenApi`) and published with each
  version. The C# client of the desktop host, nodes, `altacli` and plugins, and the TypeScript types of canvases, are
  generated from it, as the desktop's RPC client is today.
- **One identity and access model**: device-bound tokens with a proof of possession for devices; scoped, expiring
  tokens for integrations; every answer filtered by [access control](access-control.md); every change audited.
- **Signed requests** (M3a): a device signs every call, with no bearer token. The headers `Alta-Device`,
  `Alta-Timestamp` (Unix milliseconds) and `Alta-Nonce` go with `Alta-Signature`. The signature is ECDSA P-256 over
  the method, the path and query, the timestamp, the nonce, the SHA-256 of the body and the device id. The server
  refuses a time more than 5 minutes from its own and a nonce it has seen in that window. The host is not signed,
  because proxies change it. The headers, the time and the device are checked before the body is read (the REST API,
  the OTLP ingress), so that who is not a device of the server never makes it buffer a body. Setup and enrollment are signed by the key in their body.
- **Conventions**: JSON in camelCase with source-generated contracts; cursor pagination; `ETag` and `If-Match` for
  updates; an `Idempotency-Key` on creations; errors as problem details (RFC 9457) with a stable code and a hint; rate
  limits; `traceparent` on every call.
- **Change feed**: the control connection (or server-sent events for a client without one) tells a client which
  resources it may see have changed (kind, id, version); the client reads them again. Canvases stay live without
  polling.
- **Updates**: `GET /api/v1/updates` (the server's versions, the policy, the recent campaigns), `POST
  /api/v1/updates/campaigns` (a manifest an owner's controller device signed), `GET .../campaigns/{id}`, `POST
  .../campaigns/{id}/cancel`, `PUT /api/v1/updates/policy` ([deployment](deployment.md#updates)).
- **Automations**: `GET /api/v1/automations` (the caller's person's, per machine: the facts the server reads, where each
  delivery stands, the version as signed), `PUT /api/v1/automations/{id}` (a version or a removal a controller device of
  the machine's owner signed, sealed, never in clear; never replaced by one that is not newer), `DELETE
  .../{id}?machine=` (forgets a removal the machine acknowledged), `POST .../{id}/run` (a signed run by hand, passed to the machine online), `GET .../{id}/runs`.
- **Access**: `GET /api/v1/access/self`, `people` (`POST` invites, `PUT .../{id}/role`, `POST .../{id}/deprovision`),
  `teams` (and `.../{id}/members/{personId}`), `service-identities`, `grants` (`POST .../{id}/keys` once a device of the
  owner of sealed content wrapped its keys), `owners` (`POST .../{kind}/{id}/resume`), `transfers` (`.../accept`,
  `decline`, `cancel`), `access/review`, `access/proposals` (changes agents propose), and the signed access document:
  `GET /api/v1/access/documents?after=`, `GET /api/v1/access/draft`, `POST /api/v1/access/documents`. The event
  `access.changed` tells devices to read again ([access control](access-control.md#the-signed-access-document)).
- **Hooks**: `GET /api/v1/hooks` (the caller's person's, with their paths and the events that wait), `POST /api/v1/hooks` (a hook for
  one of the person's machines, for `incident_io` or `webhook`), `DELETE .../{id}`, `GET .../{id}/events` (facts: when, state,
  what the machine said), and the ingress `POST /api/v1/hooks/{id}`, which takes no device: a tool pushes there, 256 KB and 60
  events a minute at most per hook, the same event once. The server holds no signing secret: the machine checks it.
- **Telemetry**: `GET`/`PUT /api/v1/telemetry` reads and sets the fleet's signed telemetry setting (an owner's controller
  device only); `POST /otlp/v1/{traces,metrics,logs}` takes the OTLP of approved devices, signed as the API's calls are,
  and forwards it to the collector ([observability](observability.md#where-telemetry-goes)).
- **Content stays off it**: prompts, answers and files never go through the REST API, except what a person shared in a
  Managed space. Signed authority (automation versions, remote starts, approvals, policies) travels as signed documents
  that the API stores and nodes verify, never as a decision of the server.
- **Versions**: `/api/v1` grows by additions; a breaking change is `/api/v2`, served beside `v1` for at least one
  release.

## Choices and why

| Option | Decision |
| --- | --- |
| Plain WebSocket with our envelope | **Chosen.** Works through every proxy, tunnel and platform that passes an HTTP upgrade; supported by ASP.NET Core and in browsers; carries relayed TLS. |
| SignalR | Not for nodes: its stateful reconnect buffers only 100,000 bytes in memory, hubs do not fit relayed end-to-end streams, Native AOT support is partial. Possible later for browser fan-out only. |
| gRPC | No: needs HTTP/2 end to end (broken by some proxies and tunnels), no bidirectional streaming in browsers. |
| HTTP/3 and QUIC | No: built into Windows 11 only; Linux needs `libmsquic`; macOS is partially supported; UDP is often blocked. |
| NATS (leaf nodes, JetStream) | A good model (outbound leaf, per-leaf domain, durable consumers), not adopted: a second server, in Go, to ship and secure. |
| WebRTC and NAT traversal | No: unreliable through home routers; LAN and tailnet give direct paths where they matter. |
| StreamJsonRpc as the command layer | A candidate to compare with our envelope in the first sub-plan (its AOT story was not verified); the resume and acknowledgement layer stays ours either way. CodeAlta already has a JSON-RPC transport (`CodeAlta.Acp`). |

## Other protocols

- **ACP**: CodeAlta keeps ACP-shaped session semantics (list, resume, fork, archive; running, requires action, idle;
  background work) so the node API stays familiar, but ACP's remote transport is still a proposal and does not cover
  moves, leases or fleets.
- **A2A** (stable 1.0 since April 2026): later, a node could publish an Agent Card so agents outside CodeAlta delegate
  tasks to it; the fleet's own traffic stays on this protocol.
- **MCP**: unchanged; the desktop's MCP server stays local to its machine.
