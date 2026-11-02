# Security and privacy

What the fleet protects, from whom, and how. Identities are in [identity](identity.md), ownership and permissions in
[access control](access-control.md), the wire in [protocol](protocol.md).

## Goals

1. The server never reads a prompt, an answer, a tool output or a file unless a person shares it.
2. Nothing runs on a machine unless its owner or its policy allows that request.
3. A compromised server, machine or device is contained: it cannot read what it was not given, add itself to a
   person's devices, or start work elsewhere.
4. Every remote action names the person, the device, the machine and the session behind it, in an audit log without
   content.
5. Organizations get the evidence their own audits need.

## What the server sees

Two modes, chosen per space and shown wherever it matters:

- **Private** (the default for personal spaces and home servers): the server is blind to content and to descriptive
  metadata; it sees only an allow-list of facts.
- **Managed** (chosen by an organization for its spaces): what people **share** or post is readable by the
  organization's server (encrypted at rest with the organization's key), which enables search, web viewing, retention
  and inspection. The space says so: "your organization can read what you share here".

In both modes, **content that is not shared never leaves the machine that owns it** (a replica is encrypted with keys
the server does not hold).

| Data | Lives on | Private | Managed |
| --- | --- | --- | --- |
| Journal: prompts, answers, tool calls and output, file changes | The owner node | Never | Never |
| Replica of a journal | The server | Encrypted, unreadable | Encrypted, unreadable |
| Session facts: id, machine, state, times, provider type and model, token counts and cost estimate, numbers of turns and tool calls, sandbox profile, parent and children | The server | Readable | Readable |
| Title, project name, branch, paths, pull request link | The node; a sealed copy at the server | Sealed | Readable if the organization chooses, sealed otherwise |
| Repository identity (for matching across machines) | The server | A blind index, `HMAC(space metadata key, repository key)` | Readable |
| A shared snapshot | The server | Sealed for its recipients | Readable |
| A live share | Relayed | Ciphertext | Ciphertext |
| Channel messages | The server | Sealed with the channel's key | Readable |
| Automation definitions: the prompt, the script's arguments | The server | Sealed for the owner's devices and the machines that may run it | Readable by the scope's members |
| Deliveries: a child's answer, a message between agents, a queued command | Relayed and queued by the server | Sealed to the receiving device, always | Sealed to the receiving device, always |
| Trigger events (an incident, a Jira version) | What the source pushed to the server's ingress, or nothing when machines poll | Readable when pushed to the ingress; machines re-fetch or verify them | Same |
| Credentials | Each node | Never | Never |
| Organization provider keys | The server stores copies sealed to each allowed machine | Never in clear | Never in clear |
| Audit events | The server | Readable, no content | Readable, no content |
| Traces | The collector the organization names | Spans without content | Spans without content: content never leaves a machine through telemetry |
| Push notifications | Apple, Google, Web Push services | A wake-up without content | Same |

The session title answers the owner's question "what about titles?": it is descriptive metadata written from prompts,
so it is sealed by default; the person's own devices decrypt it, and an organization can choose to see titles in its
dashboards.

**Private sessions.** Any session can be marked Private: it is never published (no facts, no replica, no remote
access, not listed). On a person's own machines this is always possible. Whether an organization may require the
facts (never the content) of sessions on the machines it manages is an [open question](readme.md#open-questions).

## Keys

The primitives come from the .NET 10 base library (`ECDsa`, `ECDiffieHellman`, `HKDF`, `AesGcm`), with keys that
hardware can hold. What the library lacks is taken from a vetted implementation, never written here: the PAKE of
pairing in particular.

Keys follow access boundaries: a key opens one kind of thing for one audience, so granting a project, a session or a
snapshot never grants a whole space.

| Key | Purpose |
| --- | --- |
| **Device key** (ECDSA/ECDH P-256, non-exportable where possible) | Signs proofs and requests; receives wrapped keys (ECDH-ES, HKDF, AES-256-GCM). Only controller devices sign device-list entries and delegations ([the device list](#the-device-list)) |
| **Space metadata key**, with epochs | Seals the space's descriptive metadata (titles, project names) and keys the blind index. A new epoch when a member leaves; new data uses it |
| **Channel key**, with epochs | Seals the messages of one project's channel, for its members |
| **Session replica key** (random 256 bits) | Encrypts one session's replica, in segments with derived sub-keys to stay far from AES-GCM nonce limits; wrapped only to the devices allowed to read that session |
| **Snapshot key** (random, one per snapshot) | Encrypts one shared snapshot; it opens that snapshot and nothing later |
| **Recovery key** (256 bits, shown once in base32) | Restores a person's keys on a new device when every device is lost, decrypting locally. Losing every device and this key loses the sealed data, which the UI says plainly |
| **Organization key** (Managed spaces) | Encrypts shared content at rest on the server; held by the server or a KMS |
| **Policy key** (ES256) | Signs policy bundles; held by the Owner's device at home, admin devices or an HSM in companies, never by the server |

- Revoking someone's access rotates the keys they held (a new epoch); what they already read cannot be taken back,
  and deleting ciphertext at the server does not revoke copies a recipient made.
- **Account recovery is not encryption recovery.** Regaining the Owner role on a server (`altaserver recover-owner`, on
  the server's own terminal) never needs the recovery key of encrypted data, which never goes to the server; that key
  restores keys on a device, locally.

Every encrypted blob carries an algorithm id, so X25519 (in .NET 11) and post-quantum hybrids (ML-KEM is in .NET 10)
can come later without a flag day. MLS (RFC 9420) suits large groups with constant membership change, but its only
.NET implementation is a beta: not in 4.0.

## The device list

The weak point of end-to-end encryption is not the cipher but who decides which devices get the keys (the 2023
analysis of Matrix showed a malicious server exploiting exactly that).

- Devices have classes, and only some may vouch for others:

  | Class | What it is | May sign |
  | --- | --- | --- |
  | **Controller device** | A person's desktop or phone, used by that person | New devices of the same person, delegations of that person, approvals, automation versions they edit |
  | **Execution node** | A headless node, a team machine, a container node | Its own requests and publications only: never a device-list entry, a delegation for others, or a policy |
  | **Signer** | A small designated set of controller devices (the Owner's at home, a few admins' in a company), shown with the initial trust state | Policy bundles, enrollment of team machines, the members of teams as nodes read them |

  A compromised execution node therefore acts only as itself, and the server, which signs nothing, cannot widen anyone's
  power (Tailnet Lock's signing nodes are the precedent).
- A person's devices form a **signed list**: each new device is signed in by an existing controller device of the
  same person, after both screens show the same short words. Team machines are signed in by a signer. An enrollment
  token only lets a machine register its public key; it becomes a member when a signer signs that key.
- Devices accept a new member only with a valid signature chain, so the server cannot add a device to receive keys
  (Tailscale's Tailnet Lock is the precedent).
- Every device of the person shows **New device: …** until one of them confirms it.
- Revocation is a signed removal, a new epoch of the keys the device held, and dropped connections.

## Channels

- **To the server**: TLS, and a proof of possession of the device key on every request.
- **Between devices** (a viewer and a node, a move, a live share): TLS 1.3 inside the direct connection or the relay,
  the node's key pinned from pairing or from the signed device list, and the viewer's device proof bound to the
  channel. The relay sees ciphertext.
- **The phone**: today, the Mobile plugin over Tailscale (unchanged). A page served by the server would let a
  compromised server serve code that reads decrypted content or uses the phone's keys, whatever the cryptography. So,
  in Private spaces, the phone's page comes from a machine the person owns (a node serving it, as the desktop does
  today), or from a signed mobile application if one comes; the server serves the phone's page only for Managed spaces,
  where trusting the organization's server is stated. Approvals from the phone follow the same rule.

## Credentials

CodeAlta holds a credential only where it signs in itself, and never hands one to anything else
([providers](../providers.md#credentials)). The fleet keeps that rule across machines:

- **Never copied between machines.** No private SSH key, token or credential file travels from one node to another.
  The one exception is deliberate: an organization provider's key, which an admin's console seals to the machines
  allowed to use it ([orchestration](orchestration.md#credentials-of-unattended-runs)).
- **Each node signs in by itself**: device code flows run on the node and show in the desktop
  ([node](node.md#credentials-on-a-node)).
- **What a person types for a node** (an API key) travels sealed to that node's key; the server relays ciphertext.
- **GitHub**: a CodeAlta GitHub App. Per node, a user token by device flow (8 hours, refreshed for up to 6 months).
  For organizations, optionally, the server mints 1-hour installation tokens scoped to the repositories of a session's
  project: a deliberate trust decision (the server can then reach those repositories), off by default.
- **SSH**: a key per node by default; short-lived SSH certificates from an organization's SSH CA later; no agent
  forwarding by default (whoever is root on the remote machine can use a forwarded agent).
- **Sandboxed sessions**: API providers' keys stay in CodeAlta, outside the sandbox. An agent with its own loop runs in
  a container of its own with its account (its folder or its variables), as the sandbox does today
  ([sandbox](../sandbox.md#the-worker-and-the-routing)): that credential is inside with the agent, which is why
  unattended runs give such agents only keys of their scope. Later, the sandbox's network proxy can attach a
  short-lived token to requests for allowed hosts (github.com), so the agent never holds it (the pattern of Claude's
  GitHub proxy and Codex's domain-bound secrets).
- **Model providers**: per node, as today. Organizations can point nodes at a model gateway (LiteLLM, Azure API
  Management...) with per-person virtual keys and budgets.
- **Subscriptions are personal.** A run that is not a person's own run on their own machine (any run of a team or
  organization automation, any run on a shared or cloud machine) uses account credentials only: API keys of the
  organization or the team, or a model gateway. Providers declare their credential kind, and both the scheduler and
  the node check it ([orchestration](orchestration.md#credentials-of-unattended-runs)).
- **Organization providers** keep their keys sealed to the keys of the machines allowed to use them; the server relays
  ciphertext and never holds a key in clear.

## Remote execution

Starting or driving a session on another machine is remote code execution, by design. The rules:

1. **The node's local policy is authoritative** ([access control](access-control.md#what-the-node-decides)): who may
   start sessions, in which spaces, with which minimum sandbox; `bypassPermissions` never for a remote start unless
   the owner allows it.
2. **Signed by a person's device.** A start, a prompt or an approval sent to a node is signed by the device of the
   person who sends it, or by a delegation rooted in it. A compromised server or a stolen server token cannot start or
   drive anything.
3. **First time asks.** The first start by a new person on a machine asks the machine's owner.
4. **Agents get no more elsewhere.** Agent-started sessions on other machines are off by default per machine and space;
   when on: same space, no more tools, a permission mode no wider, a sandbox no weaker, a rate limit, and an audit
   record with the delegation chain.
5. **Approvals belong to people.** A permission request reaches the session's person on any of their devices, bound to
   the exact attempt; no agent and no message can answer it.
6. **Messages between agents of different machines** cannot approve anything or change a setting, are held for the
   person when the receiving session bypasses permissions, are rate-limited and stopped when they loop, and a machine
   can refuse them altogether (the defaults of Claude Code's cross-machine messaging, a good precedent).
7. **Automations run on signed authority, on authentic events.** A run is authorized by the version of its automation
   that an editor's device signed; a trigger event (a schedule, a webhook received by the server, a message seen by a
   node) only selects it, and the machine authenticates the event itself (re-fetched from its source with its own
   credentials, or checked against the source's signature with a secret the server does not hold) before any of it
   reaches a prompt ([orchestration](orchestration.md#triggers)).
8. **Autonomy is a list of tools.** What an unattended run may do without a person is a list of tools with typed
   parameters, never shell patterns; everything else waits for an approval routed to a person
   ([orchestration](orchestration.md#autonomy-and-approvals)).

## Policies and the kill switch

- **Policy bundles**: `{ version, notBefore, notAfter, rules, signature }`, signed with the policy key. A node applies
  a stricter rule at once (even unsigned, such as **pause**), and a looser one only when it is signed and newer than
  the one it has. A node caches the last good bundle.
- **Rules**: allowed providers and models, minimum sandbox, tools turned off, network egress, who may start where,
  maximum session length, budgets, required approval modes, what must be published.
- **Kill switch**, by level: pause a session; pause a machine (no new remote work); revoke a machine; pause the
  fleet; revoke a person (every delegation and refresh token).
- **Dead man's switch**: a node that cannot renew its credential stops accepting remote work and keeps its local work.

## Threat model

| # | Threat | What contains it |
| --- | --- | --- |
| T1 | Prompt injection in a repository, a web page, an issue or another session's output | The OS sandbox by default for remote-started and shared-machine sessions ([sandbox](../sandbox.md)); text from tools and other sessions is data with its provenance; network egress limited for sessions that read untrusted content |
| T2 | A compromised node reaches the rest of the fleet | No node-to-node trust; it reads only what was wrapped to its key and acts only as itself; per-node revocation; alerts on unusual starts |
| T3 | Remote start as remote code execution (a server bug, a stolen token, an injected agent) | Node-local policy, starts signed by a person's device, first start asks, rate limits, audit |
| T4 | A compromised server or a malicious host administrator | Content end to end; the signed device list; signed policies; signed requests. It can still deny service, see the allowed facts and lie about presence |
| T5 | A stolen session or token of a person | Short tokens, refresh bound to the device key, confirmation for dangerous actions, **revoke all**, alerts on new devices |
| T6 | A careless or malicious admin | No content access by role; policies signed by the policy key; the audit log exported to a system the admin does not control |
| T7 | Credentials spread across machines | Per-node keys and tokens, short lifetimes, sealed distribution, scrubbed environments, no credential in a journal |
| T8 | Replay or rollback of requests and policies | Nonces, times, monotonic policy versions, per-device sequences |
| T9 | A lost or stolen laptop | Revoke the device from any other; its sessions keep running elsewhere; its local keys are protected by the OS |

## Audit log

- **Events**: sign-in and failures, passkey and sign-in method changes, role and policy changes, device and machine
  enrollment and revocation, remote session starts and stops, moves and takeovers, approvals and denials sent
  remotely, shares and revocations, token exchanges (with their chain), exports, administrative reads of Managed
  content, SCIM events, break-glass use.
- **Each record**: actor (person, device, machine, session), target, action, result, time, source address, request id,
  `traceparent`.
- **Tamper-evident**: records chained by hash, batches signed; exported to syslog, OpenTelemetry logs, a webhook or
  files; retention set by the organization. No content in Private spaces.
- The audit log is not telemetry: complete, retained, append-only; telemetry is sampled and disposable
  ([observability](observability.md)).

## Compliance

- **SOC 2** is an attestation by an independent auditor about an organization's controls (Type I: designed at a date;
  Type II: operating over 6 to 12 months). Software cannot "be SOC 2". A self-hosted CodeAlta Server is part of the
  customer's own audit; CodeAlta ships what that audit needs. A hosted CodeAlta service would need its own SOC 2 Type
  II and an organization behind it: not planned in 4.0.
- **What CodeAlta ships**: the audit log above; SSO enforcement, SCIM deprovisioning and passkey policies; role
  reviews (who can do what, exported); encryption in transit and at rest; retention and deletion by space, deletion by
  destroying keys (crypto-shredding); a documented and tested backup and restore; `SECURITY.md`, a disclosure address
  and patch commitments; an SBOM (CycloneDX) per release; signed releases (Authenticode, Apple notarization, NuGet
  signatures, cosign for images) with build provenance; a hardening guide and a shared-responsibility table.
- **GDPR**: a self-hosting organization is the controller; CodeAlta provides export and erasure (crypto-shredding).

## Shared machines

- **One node per OS user.** A machine several people use runs one node per person (or one container each), never one
  privileged node acting for all.
- **The server and a node on one machine run as two OS users**, so that what the node's sessions run cannot read the
  server's database, blobs or keys; two processes of one user would not be a boundary.
- **Plugins are code with the person's rights** in the desktop process ([plugins](../plugins.md)): what a plugin
  declares it uses is consent and audit, not confinement. An organization offers plugins; a person installs them
  knowingly, and a desktop that also holds personal spaces never receives one by policy alone.
- **Team machines** run sessions under the team's credentials, with the sandbox required by policy; each person sees
  only their own sessions there. Per-person credentials on shared machines (short tokens brokered per session) come in
  milestone 5.
- **Roles do not isolate hostile users.** Teams that must not trust each other get separate servers or separate
  machines; the documentation says so.
