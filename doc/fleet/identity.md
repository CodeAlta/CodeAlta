# Identity

Who is who in a fleet, and how people and machines prove it. What each may do is in
[access control](access-control.md); the keys, the device list and the policies behind both are in
[security and privacy](security-and-privacy.md).

## Principles

- **No account to stay local.** Identity appears when a person pairs a machine or links a space to a server, never
  before.
- **Devices are their keys.** Each device makes a key pair it never exports (ECDSA P-256, in the TPM, the Secure
  Enclave or the OS key store where it can), and proves possession of it on every request.
- **The server is its own small identity provider.** Microsoft, Google, GitHub or the company's SSO are ways to
  sign in, linked to an account; never the account's key, and never required at home.
- **Proof here, permission elsewhere.** This page says who is who; [access control](access-control.md) says what each
  may do, and a node's own policy has the last word on what runs on it.

## Identities

| Identity | What it is | Proved by |
| --- | --- | --- |
| Person | A human of an organization | Their devices; a passkey; a linked sign-in (Microsoft, Google, GitHub, company SSO) |
| Device | A CodeAlta Desktop, TUI or Node instance, or the browser of a phone | Its device key, in the person's device list, signed in by another device of theirs (or by an admin device for team machines) |
| Node | The role of a device that runs sessions | Its device key; bound to an owner (a person) or a team |
| Organization | The tenant of a server: a household or a company | |
| Team | People of an organization who share projects and machines | |
| Session | An agent acting for its person | Its node; across machines, a delegated token naming the person, the session and the node |
| Service identity | A team's or the organization's identity for unattended work (team automations, node pools) | Grants and account credentials (API keys, gateway keys), never a password or a subscription |
| Integration | A tool outside CodeAlta (a CI job, a script) | A scoped, expiring service token |

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

Each side records the other's key with what it may do (view, drive, start sessions, move sessions here, give it automations), shows
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
| Linked sign-in or passkey | Proves the person on a new device, which then enrolls as a pending controller device of theirs with a single-use grant of a few minutes; another device of the person approves it, or their recovery key, or the owner for a person's first device (a person company sign-in created). The device list never grows by the server alone. |
| Recovery key | When every device of the person is lost: the words of the recovery key sign the new device in and give it the person's keys, on the device; it then removes the lost ones. |

### Linked sign-in methods

- **Microsoft** (personal and work accounts through the common endpoint; the account is keyed on tenant and object
  id), **Google** (keyed on `sub`; `hd` names a Workspace domain), **GitHub** (the device flow of the CodeAlta GitHub
  App, no ID token: the server reads the numeric user id and the name through the API with the user token, once, and
  keeps neither the token nor the login, which its person can change and give away), Apple later.
- A self-hosted server has its own host name, so it cannot use a redirect address registered once for everyone. The
  **desktop signs in through the system browser** with public client ids that the CodeAlta project registers and
  maintains, and a loopback redirect to itself. The server makes the state, the nonce and the PKCE verifier, and
  redeems the code the desktop brings back: the client secret that Google requires even for installed applications
  stays on the server, and no token of a provider reaches the desktop. The server checks the ID token (issuer,
  audience, nonce, signature, lifetime).
- **GitHub has no redirect and no secret**: the server asks GitHub for a device code with the client id of the GitHub
  App, the desktop shows the user code and opens `github.com/login/device`, and polls the server, which asks GitHub
  at most once per interval GitHub sets (`slow_down` lengthens it); the device code never leaves the server. A
  company's own GitHub Enterprise Server app is the same, at its address (`CODEALTA_SIGNIN_GITHUB_URL`).
- No registration per server: every release carries the project's ids (`PublicSignInClients`). A server may replace
  them (`CODEALTA_SIGNIN_<PROVIDER>_CLIENT_ID`, and `_CLIENT_SECRET` for Microsoft and Google), and a method whose id is
  missing is not offered. Google's client id and desktop secret ship in the packages but not in the source: the CI build
  writes them from repository secrets (`CODEALTA_SIGNIN_GOOGLE_CLIENT_ID`, `_CLIENT_SECRET`), so that secret scanners
  of the public repository have nothing to flag, and a build without them does not offer Google. Google documents that
  this secret is not confidential for an installed application; the code stays bound to its PKCE verifier.
- **Registering the GitHub App** is one confirmation in a browser: `pwsh src/CodeAlta.Server/Identity/SignIn/register-github-app.ps1`
  posts `github-app-manifest.json` (public, no permission, no webhook) to the CodeAlta organization, converts the code
  GitHub sends back (`POST /app-manifests/{code}/conversions`) and prints the app id, slug and client id. The owner
  then turns on **Enable Device Flow** in the app's settings, which a manifest cannot set, and puts the client id in
  `PublicSignInClients.GitHubClientId`. The client secret, webhook secret and private key of the conversion are not
  needed, and not kept unless `-SaveCredentialsTo` names a file.
- A linked method signs in only once a person linked it from one of their devices; an account nobody linked signs
  nobody in.
- Sign-in on the server's own web pages (a browser, the phone) needs an application the admin registers with the
  provider, or a passkey.

### Company SSO

- An organization configures its OpenID Connect provider (Entra ID, Okta, Google Workspace, Keycloak...); SAML is not
  planned.
- The owner sets its issuer, client id and secret; the server reads its discovery document before keeping them, and
  never gives the secret back. The redirect registered at the provider is the loopback of the desktop
  (`http://127.0.0.1`, any port).
- **Enforcement**: the organization requires its company sign-in, and no other way signs a new device in, except
  the passkey of one **break-glass Owner**.
- People are created at their first sign-in, as members; the groups of each sign-in are kept with its login, and map
  to roles by **group id** in milestone 4 (Entra app roles rather than group claims, which overflow past 200 groups).
- **SCIM 2.0** (users, groups) deprovisions without waiting for a sign-in: the person is suspended, their devices and
  delegations revoked, their sessions on team machines stopped, the audit trail kept (milestone 5).

### Tailscale as identity at home

Behind `tailscale serve`, the server receives the tailnet user (`Tailscale-User-Login`); for a direct connection it
can ask the local Tailscale client (`WhoIs`). An owner may let tailnet users of their household sign in that way. It
authenticates only: what they may do is still CodeAlta's. Tailscale's free plan is for non-commercial use.

### The server as identity provider

- **ASP.NET Core Identity**'s passkey handler runs the WebAuthn ceremonies of the server's sign-in page, for
  discoverable credentials; the passkeys are kept with the people. **OpenIddict** (Apache 2.0, on .NET 10) is the OAuth
  and OpenID Connect server once the server issues tokens of its own: authorization code with PKCE, device
  authorization (RFC 8628), refresh, token exchange (RFC 8693). A sign-in issues none: its result is the single-use
  grant above, held in memory for minutes. Duende is excluded (licence cost and redistribution terms); Keycloak, Zitadel, Authentik or
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
  proving possession of the key. No reusable secret remains. The node is a member of the fleet only once a controller
  device signed its key into the device list: its owner's for a personal machine, a signer's for a team machine
  ([security](security-and-privacy.md#the-device-list)). A node is an execution device: it never signs another device,
  a delegation for others or a policy.
- Revocation marks the node revoked **and** drops its connection. A node offline for longer than a set time needs a
  new approval.
- At connection, a node declares its capabilities (sandbox, tools, providers); **hosting sessions for others** is a
  separate consent.

## Roles and permissions

Ownership, roles, grants and what each node decides are in [access control](access-control.md).

## Confirming dangerous actions

Enrolling a machine, changing who may start sessions where, loosening a policy, adding a signing device, exporting
the audit log, changing SSO, revoking everything: each asks for a fresh confirmation (a passkey with user verification,
or an approval on another of the person's devices), valid 10 to 15 minutes. A household owner therefore keeps one
account: no separate administrator account, and a stolen browser session or a hijacked agent cannot reconfigure the
fleet.

## Agents acting for people

- A session acts for its person. Its authority on another machine (a child session, a message) comes from a
  **delegation signed by a controller device of that person**: a standing one, made when the person allows
  agent-started sessions ("sessions of my Work space on the Windows PC may start sessions on machines labeled `macos`,
  sandboxed, until this date"), presented with the request that the requesting node signs. The target node checks the
  chain itself: the person's device in the signed device list, then the delegation's audience, capabilities, expiry
  and revocation generation, then the requesting node's signature. A child's delegation is never wider than its
  parent's, and chains have a maximum depth.
- **Server tokens** (RFC 8693 token exchange, subject the person, actor the session) attribute calls to the server's
  API and open server resources; they never carry the authority to run anything on a machine.
- The audit log reads the chain: "session S on the Windows PC, for Alex, started session T on the Mac mini".
- Agents never answer a permission request or a question for a person, and a message from an agent never changes a
  setting ([security](security-and-privacy.md#remote-execution)).

## Spaces and servers

- A space links to at most one server (address, organization, person, device). Different spaces can link to
  different servers: Work to the company, Personal to the home server, Default to nothing.
- Each link has its own device key: the company and the home server cannot correlate the same laptop.
- A link publishes the space's projects, its machines' inventory for that space, and the metadata of its sessions
  that are not Private ([security](security-and-privacy.md#what-the-server-sees)).
- Unlinking a space withdraws the facts and statistics of its sessions from that server, and its key seals nothing
  more there. Leaving a server withdraws everything the machine published there (facts with their sealed metadata,
  statistics, inventory, run reports), its copies of keys and the automations given to it through that server; a
  controller device that is not the last one also signs its own removal from the device list, so its keys get new
  epochs. Local sessions stay as they are.
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
