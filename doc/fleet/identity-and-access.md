# Identity and access

Who is who in a fleet, how people and machines prove it, and what each may do. Security mechanisms behind it (keys,
the device list, policies) are in [security and privacy](security-and-privacy.md).

## Principles

- **No account to stay local.** Identity appears when a person pairs a machine or links a space to a server, never
  before.
- **Devices are their keys.** Each device makes a key pair it never exports (ECDSA P-256, in the TPM, the Secure
  Enclave or the OS key store where it can), and proves possession of it on every request.
- **The server is its own small identity provider.** Microsoft, Google, GitHub or the company's SSO are ways to
  sign in, linked to an account; never the account's key, and never required at home.
- **The node decides what runs on it.** The server can ask; a node's local policy is the final authority.
- **Few roles, scoped; administration is not reading.** An admin manages people and machines without reading their
  sessions.

## Identities

| Identity | What it is | Proved by |
| --- | --- | --- |
| Person | A human of an organization | Their devices; a passkey; a linked sign-in (Microsoft, Google, GitHub, company SSO) |
| Device | A CodeAlta Desktop, TUI or Node instance, or the browser of a phone | Its device key, in the person's device list, signed in by another device of theirs (or by an admin device for team machines) |
| Node | The role of a device that runs sessions | Its device key; bound to an owner (a person) or a team |
| Organization | The tenant of a server: a household or a company | |
| Team | People of an organization who share projects and machines | |
| Session | An agent acting for its person | Its node; across machines, a delegated token naming the person, the session and the node |
| Service | An automation, an integration, a pool of cloud nodes | A service credential, scoped |

## Without a server

### The local person

CodeAlta gains a small profile of the person at the computer: a display name, emails (taken from the Git
configuration), an avatar. It needs no sign-in. Prompts record the person who sent them (prompt provenance gains a
person id), so a session that is later co-driven, moved or handed off keeps who wrote what.

### Paired machines

Pairing makes two devices trust each other's keys, with no account:

- **QR code**: `altanode pair`, or a desktop's **Pair a machine…**, shows a code holding the key fingerprint, the
  addresses and a one-time secret; the other device scans it.
- **Typed code**: a short one-time code (a few minutes, a few attempts) run through a password-authenticated key
  exchange (CPace or SPAKE2), so a code seen over a shoulder or through a relay gives an attacker one online guess;
  both screens then show the same short words to compare.
- **Over SSH**: **Add a machine…** in the desktop installs or finds the node through the person's own SSH access and
  reads the pairing payload over that authenticated channel: nothing to scan or type.

Each side records the other's key with what it may do (view, drive, start sessions, move sessions here), shows
**Paired machines** with **Revoke**, and announces a new pairing on its other paired devices.

## With a server

### Setting up the owner

1. The desktop installs CodeAlta Server (this computer, another one over SSH, or a cloud VM) or is given the address
   of one that runs.
2. At its first start the server makes a one-time setup code and accepts setup only on loopback or the tailnet,
   for a limited time: "first request wins" never applies to an exposed port.
3. The desktop claims it with the code: the person becomes **Owner** of a new organization, the desktop's device key
   is the first device of the device list, and the desktop makes the person's keys and shows the **recovery key**
   once, to save or print ([security](security-and-privacy.md#keys)).
4. A passkey for the server's web pages is optional (it needs an HTTPS name; WebAuthn inside the desktop's web views
   is to be checked first).

### A new device

Three ways to sign a device in, one result: the device key in the person's device list, the keys it needs wrapped
to it.

| Way | Flow |
| --- | --- |
| From another device | The new device shows a code or QR with its key fingerprint; an existing device compares the short words, signs it into the list and wraps the keys to it. |
| Device code (headless) | `altanode join https://<server>` shows a code; the person approves it in the desktop (which signs the node in) or on the server's web page. |
| Linked sign-in or passkey | Proves the person on a new device; that device gets the keys of Private spaces only once another device of the person approves it (the device list never grows by the server alone). |

### Linked sign-in methods

- **Microsoft** (personal and work accounts through the common endpoint; the account is keyed on tenant and object
  id), **Google** (keyed on `sub`; `hd` names a Workspace domain), **GitHub** (OAuth only, no ID token: the server
  reads the numeric user id through the API with the token it is given), Apple later.
- A self-hosted server has its own host name, so it cannot use a redirect address registered once for everyone. The
  **desktop signs in natively** with public client ids owned by the CodeAlta project (system browser, PKCE,
  loopback redirect, with a nonce from the server) and gives the server the ID token, which the server checks
  (issuer, audience, nonce, signature). No registration per server.
- Sign-in on the server's own web pages (a browser, the phone) needs an application the admin registers with the
  provider, or a passkey.

### Company SSO

- An organization configures its OpenID Connect provider (Entra ID, Okta, Google Workspace, Keycloak...); SAML is not
  planned.
- **Enforcement** for the company's domains: no other way to sign in for those people, except one **break-glass
  Owner** kept offline.
- People are created at their first sign-in; groups map to roles by **group id** (Entra app roles rather than group
  claims, which overflow past 200 groups).
- **SCIM 2.0** (users, groups) deprovisions without waiting for a sign-in: the person is suspended, their devices and
  delegations revoked, their sessions on team machines stopped, the audit trail kept (milestone 5).

### Tailscale as identity at home

Behind `tailscale serve`, the server receives the tailnet user (`Tailscale-User-Login`); for a direct connection it
can ask the local Tailscale client (`WhoIs`). An owner may let tailnet users of their household sign in that way. It
authenticates only: what they may do is still CodeAlta's. Tailscale's free plan is for non-commercial use.

### The server as identity provider

- **ASP.NET Core Identity** keeps people, passkeys and recovery codes; **OpenIddict** (Apache 2.0, on .NET 10) is the
  OAuth and OpenID Connect server: authorization code with PKCE, device authorization (RFC 8628), refresh, token
  exchange (RFC 8693). Duende is excluded (licence cost and redistribution terms); Keycloak, Zitadel, Authentik or
  Pocket ID can be the *external* provider of a company, never a dependency of a home install.
- Access tokens live 5 to 15 minutes; refresh tokens are bound to the device key; every request carries a proof of
  possession of the device key (DPoP-style, RFC 9449). Mutual TLS is an option for organizations that control their
  network edge (Tailscale Serve, tunnels and cloud load balancers terminate TLS before the server).

### Enrolling nodes

- **Interactive**: the device code above, approved by the owner, who chooses the spaces, labels and owner (a person
  or a team).
- **Unattended** (a script, an image, a cloud pool): an **enrollment token**, single use, valid 10 minutes to 24 hours,
  bound to an owner, spaces and labels, shown once, optionally bound to the fingerprint of the server's key.
- Then always: the node makes its key; the server records the node and gives it a short credential it renews by
  proving possession of the key. No reusable secret remains.
- Revocation marks the node revoked **and** drops its connection. A node offline for longer than a set time needs a
  new approval.
- At connection, a node declares its capabilities (sandbox, tools, providers); **hosting sessions for others** is a
  separate consent.

## Roles and permissions

### Organization roles

| Role | Can | Cannot, unless granted |
| --- | --- | --- |
| **Owner** | Everything: people, roles, SSO, policies (with the policy key), the organization | Read sessions not shared with them |
| **Admin** | People, teams, team machines, spaces, policies (signed by a policy device), audit | Read sessions not shared with them |
| **Member** | Their devices and machines, their sessions, shares, the channels of their projects, sessions on the machines granted to them | Administer |
| **Auditor** | Read the audit log and the fleet's metadata | Read content, change anything |
| **Guest** | What is shared with them | Machines, channels they were not invited to |
| **Service** | The scoped capabilities of an integration or a node pool | |

### Grants

A grant is `(subject, capability, scope)`, denied unless granted:

- **Scopes**: organization, space, project, machine, session.
- **Capabilities**: `view`, `interact` (send, steer, stop), `approve` (answer permission requests), `start` (a session on
  a machine), `move`, `share`, `manage`.
- A team machine grants `start` to teams; a person's machine grants nothing to others unless its owner says so.

### What the node decides

Each node keeps a local policy, which the server cannot loosen:

- who may view and drive its sessions, and start sessions on it, per space;
- the strictest sandbox a remote start must use, and whether `bypassPermissions` is ever allowed for one (never by
  default);
- whether agent-started sessions from other machines are accepted (off by default);
- a remote start must be **signed by the device of the person who asks** (or by a delegation rooted in it): a server
  token alone is not enough, and the first start by a new person asks the node's owner.

### Confirming dangerous actions

Enrolling a machine, changing who may start sessions where, loosening a policy, adding a signing device, exporting
the audit log, changing SSO, revoking everything: each asks for a fresh confirmation (a passkey with user verification,
or an approval on another of the person's devices), valid 10 to 15 minutes. A household owner therefore keeps one
account: no separate administrator account, and a stolen browser session or a hijacked agent cannot reconfigure the
fleet.

### Agents acting for people

- A session acts for its person. When it reaches another machine (a child session, a message), it carries a
  **delegated token** (RFC 8693 token exchange): subject the person, actor the session on its node, audience the target
  node, the capabilities it needs, minutes of life, no refresh. A child's token is never wider than its parent's, and
  chains have a maximum depth.
- The audit log reads the chain: "session S on the Windows PC, for Alex, started session T on the Mac mini".
- Agents never answer a permission request or a question for a person, and a message from an agent never changes a
  setting ([security](security-and-privacy.md#remote-execution)).

## Spaces and servers

- A space links to at most one server (address, organization, person, device). Different spaces can link to
  different servers: Work to the company, Personal to the home server, Default to nothing.
- Each link has its own device key: the company and the home server cannot correlate the same laptop.
- A link publishes the space's projects, its machines' inventory for that space, and the metadata of its sessions
  that are not Private ([security](security-and-privacy.md#what-the-server-sees)).
- Unlinking removes the device from that server and its facts; local sessions stay as they are.
- In the file of the space (proposal):

```yaml
fleet:
  server: https://fleet.contoso.example
  organization: contoso
  sharing: managed        # private | managed, set by the organization
```

## Profiles and avatars

- A profile has a display name, verified emails, an avatar, a time zone and a presence preference.
- The avatar is, in order: one the person uploaded; the picture of a linked sign-in (Google `picture`, Microsoft
  photo, GitHub avatar); **Gravatar** (from the SHA-256 of the email, fetched and cached by the server so that
  Gravatar never sees who looks at it); the initials.
- Without a server, the local profile uses the initials unless the person turns Gravatar on: staying local means no
  call to any service.
