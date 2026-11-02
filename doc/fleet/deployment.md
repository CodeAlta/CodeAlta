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

On every OS: **one node per OS user**; the server and a node can share a machine, as two processes.

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
4. **The sandbox** with a network profile that reaches only the services it uses.
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

- **Server first, then nodes**: a node one minor version behind keeps working (N-1 window,
  [protocol](protocol.md#versions)).
- Nodes follow the fleet's update policy (**automatic**, **pinned** or **manual**), update when idle, and an offline
  node updates when it reconnects. The desktop shows versions and offers **Update all machines**.
- Containers update by image tag.

## Backup, restore, leave

- The server's database and blobs back up on a schedule (built in) and can stream to object storage; **Restore** is a
  documented, tested command.
- Nodes need no backup beyond today's: journals are the truth on each machine, replicas cover lost laptops.
- **Leave a fleet**: unlink the space (sessions stay local) or uninstall the node (`altanode uninstall` keeps the
  profile). Removing a machine from the fleet revokes its device.
