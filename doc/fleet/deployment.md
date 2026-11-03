# Deployment and setup

How a fleet comes to exist: from CodeAlta Desktop, one machine at a time, on any operating system, at home or in a
cloud, with no Kubernetes.

## Principles

- **It starts in CodeAlta Desktop.** A setup assistant explains, checks and does each step; the command line is there
  for scripts and servers, never required.
- **One step per machine.** Adding a machine is one command, one scan, or nothing at all when the desktop can reach
  it over SSH.
- **Any OS.** Windows, macOS and Linux, x64 and Arm64, for the node and the server. No container runtime required;
  containers when the platform wants them.
- **No Kubernetes.** A Helm chart can come for organizations that already run it.
- **Nothing hosted by CodeAlta is needed.** No relay, broker or account of the CodeAlta project in the path (a
  small vendor's hosted relay is a liability: several of the projects studied lost theirs).

## The setup assistant

The assistant is part of the console ([console](console.md)): **Settings > Fleet**, the **Fleet** canvas and the
search (`Ctrl+P`) open it. Each flow is a short checklist that shows what it checks and what it changes.

| Flow | Steps |
| --- | --- |
| **Pair a machine** | Choose how it is reached (same network, Tailscale, SSH); on the machine, `altanode install` and `altanode pair`, or **over SSH** (the desktop installs and pairs by itself); confirm the short words; choose what the machine may do. |
| **Add a machine to my fleet** | Same, then the node joins the server: a device code the desktop approves, or a one-line command with a single-use join link (`altanode join <link> --service`), or over SSH. Choose its spaces, its labels (`always-on`...), and whether it hosts sessions for others. |
| **Create my server** | Where: this computer, another machine over SSH, or a cloud (below). How it is reached: Tailscale (recommended at home), the local network, a domain name, a Cloudflare Tunnel. The desktop installs CodeAlta Server, claims it with its setup code, makes the owner account and the keys, shows the recovery key, then offers to add the machines it knows. |
| **Join an organization** | The server's address (or the configuration a company pushes to its machines); sign in with the company's SSO; choose the space it links to (a new **Work** space by default). |
| **Connect my phone** | Today's Mobile plugin, or, with a server, the phone page of the server. |

**Agentic setup.** The assistant can hand a flow to an agent session: a built-in skill knows the recipes below, the
session works with the person's own CLIs and space identity (`az`, `aws`, `gcloud`, `ssh`), shows each command for
approval, keeps a checklist in its notes, and never stores a secret. "Set up my CodeAlta server on my Hetzner VM"
becomes a session the person watches and approves.

## Home recipes

| Machine | Server | Node |
| --- | --- | --- |
| **Mac mini** (always on) | `altaserver install` as a LaunchDaemon, or as a LaunchAgent of the user | A LaunchAgent of the user who owns the credentials. The user must stay logged in (automatic login conflicts with FileVault: the guide explains the choice) |
| **Ubuntu server** | A systemd unit (`Type=notify`) as user `codealta`, behind Tailscale Serve or Caddy | `systemd --user` of the user who owns the credentials, with lingering on (`loginctl enable-linger`) |
| **Windows PC** | A Windows service | A task at logon of the user (not a Session 0 service, which does not see the user's credentials) |
| **Raspberry Pi** or small NAS (Linux Arm64) | Fine for the server alone | Fine for light nodes |

On every OS: **one node per OS user**; the server and a node can share a machine, as two OS users, so that the node's
sessions cannot read the server's data ([security](security-and-privacy.md#shared-machines)).

## How machines reach each other

| Choice | When | Notes |
| --- | --- | --- |
| **Tailscale** | Home fleets, the recommended path | `tailscale serve` gives the server a `*.ts.net` name with a certificate and tells it the tailnet user; direct paths between nodes come for free. Free plan for up to 6 users, non-commercial. Headscale is the self-hosted alternative. CodeAlta does not embed Tailscale (no .NET binding; it talks to the installed client). |
| **Local network only** | A house without remote access | Nodes found by mDNS as suggestions only (trust comes from pairing); a self-signed certificate pinned at pairing. |
| **A domain name** | A server in a cloud or reachable from the internet | Caddy in front with automatic Let's Encrypt certificates, or the platform's TLS. |
| **Cloudflare Tunnel** (+ Access) | No open port at home | WebSockets work; heartbeats keep them open. |

Nodes only ever dial out: none of these needs an inbound port on a node.

## Cloud recipes

| Who | Recipe | Why |
| --- | --- | --- |
| A person, a small team | **One small VM** (Hetzner, DigitalOcean, Lightsail, an Azure B-series, a GCP e2) with the server's container or binary, **Caddy**, SQLite on the local disk, Litestream or the built-in backup to object storage | Cheapest (a few dollars a month), a local disk makes SQLite safe, one machine to patch |
| A person who wants no VM | **Fly.io** with a volume and SQLite | Local volumes on a single machine fit SQLite |
| An organization on Azure | **Azure Container Apps** + **Azure Database for PostgreSQL**, from an `azd` template (Bicep) with Key Vault | Container Apps has no local persistent disk (Azure Files is SMB/NFS, wrong for SQLite) |
| An organization on AWS | **ECS Express Mode** (Fargate + load balancer) + **RDS PostgreSQL**, from a Terraform module | App Runner no longer takes new customers (since 2026-04-30) |
| An organization on GCP | **Cloud Run** + **Cloud SQL**, Terraform | Workable: WebSockets end after 60 minutes at most, which reconnection absorbs; billed while connections are open |
| An organization with Kubernetes | A Helm chart (later) | |

Shipped artifacts stay few: one image, one `compose.yaml`, one `azd` template, one Terraform module per cloud. Aspire
is used to run a server and several nodes together in development, not as the deployment path (its Azure deploy is
in preview and it has no AWS target).

## Nodes in containers and sandboxes

- **A container image of the node** for Linux, enrolling itself with a single-use token passed in its environment.
- **A host agent**: a node with the `host` capability on a Docker, Podman or Apple `container` machine starts node
  containers on request (for a session that needs a clean environment, or a burst of work). The server never gets
  the container runtime's socket.
- **Cloud sandboxes** (Azure Container Apps sandboxes, E2B, Daytona, Fly Machines) plug in later behind the same
  "provision a node" interface (milestone 5).
- The OS sandbox of CodeAlta (`mxc`) is not a deployment tool: it confines what a session runs on a machine, and stays
  the floor for sessions that others start there.

## Machines for unattended work

A machine that runs a team's or the organization's automations (an SRE machine inside the infrastructure, a release
machine) is set up for that one purpose, from the console:

1. **Its own OS user and node**, enrolled as a team machine with labels (`infra-prod`, `release`), accepting the
   automations of the scopes that own it and nothing else.
2. **Account credentials only**: the organization provider or gateway key sealed to it
   ([orchestration](orchestration.md#credentials-of-unattended-runs)); no person's subscription or login.
3. **Scoped tool credentials**: read-only access to monitoring (Grafana, the logs, Kubernetes `view`) and only the
   narrow write roles its allowed remediations need, each behind a tool with typed parameters.
4. **The sandbox**, kept off the private networks: the agent never reaches the infrastructure from its shell; only the
   host-side tools (MCP servers or plugin tools outside the sandbox) do, with their own credentials, and they check
   their targets and limits themselves ([orchestration](orchestration.md#what-unattended-runs-support)).
5. **Its trigger sources** (Slack Socket Mode, Jira or incident.io polling) configured on it with their own tokens,
   or the server's hooks ingress for the tools that only push.
6. **A check**: the console runs the machine's readiness for each automation it should accept (providers of account
   kind, tools, credentials, sandbox) before the first run.

## Packaging

| Artifact | For |
| --- | --- |
| .NET tools (`dotnet tool install -g`) | Developers, as today for the desktop, TUI and CLI |
| Self-contained archives per platform (`win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`) | Servers and machines without the .NET SDK |
| Container images (`ghcr.io/...`, `linux/amd64`, `linux/arm64`) | Clouds and container hosts |
| Installers (winget, Homebrew, apt) | Later |

All signed (Authenticode, Apple notarization, NuGet signatures, cosign for images), with an SBOM and build provenance.

## Updates

- **Nodes first, then the server**, as a campaign of the fleet (below). Any 4.x desktop, node and server talk to each
  other ([protocol](protocol.md#versions)), so machines that update at different times keep working.
- **Self-update, no SDK.** `altanode update` and `altaserver update` (`CodeAlta.Fleet.Update`, which references the
  protocol only, so the server keeps clear of the agent runtime) download the runtime package of the tool
  (`CodeAlta.Node.win-x64`...) from a NuGet v3 feed (nuget.org, a mirror, or a folder), check it against the feed's
  `packageHash` and against an expected hash when the caller gives one, and unpack its application side by side
  under the state root: `fleet/versions/<version>/` of a node, `versions/` of the server's data root. A version is
  available once both the tool package and the runtime package of the machine list it, as for CodeAlta Desktop's own
  update check: the packages of a release appear one after the other, and an unlisted tool package withdraws the
  release. A package from an HTTP feed that nothing hashes is refused. The owner restricts the feeds in `update.json`
  beside the versions; the fleet may only name the configured feed or nuget.org. NuGet signatures are not verified
  yet. The new version runs on the .NET runtime of the installed one: a self-contained copy without a shared runtime
  cannot switch (its trial fails and it stays as it is).
- **Pointer and launcher.** `state.json` of the versions selects one. The installed entry point (`altanode run`, what
  the service starts) starts the selected version as its child, gives it its arguments and ends with its exit code. A
  version that switches stops with exit code 75 and its launcher starts the next one; a process that ran the installed
  version itself becomes that launcher. The process the service manager started never exits during an update, so the
  switch does not depend on Task Scheduler, launchd or systemd restarting anything. One launcher at a time: `run`
  holds the lease of the folder of versions (`launcher.lock`) from its start to its end, across the selection, the
  version that runs, its confirmation and its rollback; a second `run` is refused at once, even before the version
  started has taken the profile or the data root. A version refused because another process holds them (exit code 3)
  did not run: it neither fails its trial nor counts a start.
- **Trial and rollback.** A new version confirms itself once healthy: a node when it listens and its connections to
  the servers that approved it are up, the server when it answers its own health request. One that exits first, or does
  not confirm within 2 minutes (over 2 starts), or cannot be started, is stopped, marked bad (an update skips it unless
  named) and the previous version starts again. A rollback with a backup is recorded with the move of the pointer and
  kept until the backup is restored: the launcher restores it before it starts anything, and when it cannot, it starts
  no version on data that version may not read (exit code 74) and tries again at its next start. Versions other than
  the selected, the previous and a pending one are pruned.
- **When.** A node switches when no session works: it refuses new work (`node.updating`, retried by callers) while it
  drains, 10 minutes at most, then takes work again and switches at the next idle moment; `--now` does not wait (its
  running turns stop and stay resumable). The server copies its database first (SQLite's backup API, `backups/`); a
  rollback restores that copy, losing what the new version wrote meanwhile. A switch asked while nothing runs is taken
  at the next start, the server's database copied first there too; no server version is selected without that copy.
- **The campaign.** An owner's controller device asks the server for an update of the fleet: a version (the latest
  both tool packages list, by default), a feed, now or a time (kept in UTC, with the time zone it was chosen in), the
  nodes (all by default). The server keeps it (`update_campaigns`), and at its time asks its nodes over the control
  connection (`update.instruction`), two at a time; it waits until each node online reports the version confirmed (one
  that runs it on trial may still roll back, and reports that too), failed or rolled back, then updates itself, last, with the same self-update. A node offline does not hold the campaign: it is pending,
  asked when it reconnects, and the campaign ends **partly done** until it is. A node that goes offline while it
  switches holds the server for ten minutes. A campaign is cancelled until the server's step; a node already asked is
  told, again at each connection, until it reports the campaign done (any final state). The node keeps the
  cancellation before it acknowledges it, also one that comes before the instruction, which then never starts there.
  The server's own step is done only once its new version confirmed its trial (it ends as rolled back otherwise), and
  the campaign stays active until then: one campaign at a time is active, whoever starts it. A server stopped during
  its own step takes that step again at its start unless its self-update holds the switch.
  States: scheduled, updating nodes, updating server, done, partly done, failed (the server did not update, or every
  node that tried failed), cancelled. Nodes report where they stand (`update.report`, and in their presence); the REST
  API (`/api/v1/updates`) and the change feed give the campaign to the desktop.
- **Trust.** The server alone cannot make a machine install anything. The controller device reads the feed and signs
  a manifest: organization, campaign and sequence, version, feed, schedule, machines, and the SHA-512 of the runtime
  package of each published runtime of each product. A node takes it only when a controller of the device list it
  anchored signed it, its sequence is newer than the last one it accepted from that server, it names the node, the
  version is newer than the one that runs (an older one only when the manifest says a person chose a rollback), the
  fleet's latest policy allows it, and its download matches the hash signed for its runtime. The server checks the
  same against the device list and the policy it holds before each application of its own step, one taken again after
  a stop included. The machine's `update.json` wins: its feeds, or
  `"fleet": false`, which refuses every update of the fleet; so does a switch its owner asked for (`altanode update`),
  which a campaign never replaces nor withdraws. A node keeps the instruction it accepted
  (`fleet/update-campaign.json`) and waits for its time itself, across restarts; nothing runs until that file is
  written. After a start, the campaign the file holds runs only once its server sent it again (at each connection,
  after its policy and the cancellations not acknowledged) and the state is kept: a write that failed before the
  restart leaves the file behind, and nothing on disk can say so. While the file does not read, the node refuses
  every update of the fleet (the owner fixes it, or deletes it to start again as a new node). A newer policy or a cancellation it cannot write holds in memory, and until the write
  succeeds (tried again) the node takes no instruction, runs no work of the fleet and acknowledges nothing. The server
  takes any final state as the acknowledgement of a cancellation: the node reports none, a refusal included, for a
  campaign whose cancellation it received and has not kept; it still refuses the others with the reason. A version
  is confirmed only from a self-update state that reads. A campaign it accepted never starts again: sent again, even after a refusal, it only gets
  where the node stands. A node follows one fleet at a time: another server's instruction is refused while a campaign
  runs, and each server keeps reading how its own last campaign ended. An automatic update waits on each machine, the
  server included, for the signed maintenance window (its time zone, daylight saving time included; open four hours),
  whenever it reaches it; a newer policy drops a campaign that waits for what it no longer allows.
- **Policy.** Signed the same way: **manual** (campaigns only), **pinned** (no campaign to another version), or
  **automatic**: the server reads the policy's feed daily and starts a campaign to its latest stable version at the
  maintenance window (a time of day in a time zone). No person signs the hash of that version, so each machine checks
  the feed itself: the version must be the latest stable one the feed lists, not older than the one it runs (a machine
  that runs it installs nothing, and is updated once that version confirmed), and the package must match the feed's
  own hash; the server reads the feed again before its own step. A version that failed
  is tried again at the next daily checks, three times at most; one that rolled back is not. The server sends the
  signed policy, whatever its mode, to its nodes (`update.policy`) when it changes and at each connection; a node keeps
  the newest and refuses what an older one allowed. A new policy withdraws the campaigns it no longer allows: the
  automatic ones of the policy it replaces, and those to another version than a pinned one, in the server's step too
  while the server's own switch waits (it is withdrawn).
- Containers update by image tag.

## Backup, restore, leave

- The server's database and blobs back up on a schedule (built in) and can stream to object storage; **Restore** is a
  documented, tested command.
- Nodes need no backup beyond today's: journals are the truth on each machine, replicas cover lost laptops.
- **Leave a fleet**: unlink the space (sessions stay local) or uninstall the node (`altanode uninstall` keeps the
  profile). Removing a machine from the fleet revokes its device.
