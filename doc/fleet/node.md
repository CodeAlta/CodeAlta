# CodeAlta Node

The headless CodeAlta host: the always-on machine at home, the Mac that builds iOS, a cloud VM. It answers issue
[#284](https://github.com/CodeAlta/CodeAlta/issues/284)'s "headless mode". Its names (`CodeAlta Node`, `altanode`)
are decided ([readme](readme.md#naming)).

## What it is

`altanode` is `CodeAltaHost` without a window: the same runtime, catalog, providers, tools, plugins made for headless
hosts, sandbox, Code Mode, automations, jobs and reminders, plus the fleet connector and the node API
([architecture](architecture.md#inside-a-host)). It owns a state root and its lock, as Desktop and TUI do, so it is
**the** instance of its profile on that machine.

What it is not:

- not `altacli`, which runs one prompt beside an instance and never owns the state root;
- not a server: it runs agents; the coordinator is [CodeAlta Server](server.md), a separate process that can run
  on the same machine;
- not a UI: it is driven from CodeAlta Desktop, the phone, or `altanode` commands for the basics.

## Commands

| Command | What it does |
| --- | --- |
| `altanode run [--port <n>] [--bind <address> \| --listen-all \| --no-listen]` | Runs in the foreground (logs to the console). What a service starts. Listens for paired devices on port 2585 of loopback by default. |
| `altanode install [--user \| --system --account <name>] [run options] [--dry-run]` | Installs and starts the service of this OS (below), which runs `altanode run` with the options given. `--dry-run` prints the definition and the commands. |
| `altanode uninstall [--system --account <name>]` | Stops and removes the service; keeps the profile. |
| `altanode pair` | Shows a pairing code and QR code (in the terminal) for a desktop to scan. The first slice prints the `codealta-pair://` link only, and waits until a device redeems it or it expires. |
| `altanode join <server-url> [--token <enrollment>]` | Enrolls in a fleet: with an enrollment token, or by a device code a person approves. |
| `altanode status` | Where the node listens, its paired devices, the sessions at work, its versions and its update, the service, and whether it will still run after a restart or a logout. Later: the fleet connections, the providers' readiness. |
| `altanode logs [--follow] [--lines <n>]` | The node's log (`logs/altanode.log`), followed across its daily rolls. |
| `altanode devices`, `revoke <device>`, `permissions <device> [--allow <list>] [--deny <list>]` | The devices paired with the node: name, short fingerprint, permissions (`view`, `drive`, `start`, `move`, `automate`, `manage`), paired and revoked dates. A revoked device is disconnected at once; one that loses a permission is disconnected and reconnects with what it may do. Without a running node, they change `fleet/trusted-devices.json`. |
| `altanode projects [list]`, `add <path>`, `clone <url>`, `remove <project>` (`--space <space>`) | The projects of the node's catalog: an existing folder added, a repository cloned into `~/code/<host>/<owner>/<repo>` (the clone-on-demand of moves), a project removed from the catalog with its files left. Through the running node's local control (`control.projects.*`), which keeps a project that has sessions; otherwise on the catalog files of the profile. |
| `altanode update [--version <v>] [--source <feed>] [--check] [--now]` | Downloads a version (the latest release, a prerelease when one runs), checks its hash, and switches to it when the node is idle; the new version runs on trial and is rolled back if it does not confirm ([deployment](deployment.md#updates)). Later: the version the fleet recommends. |
| `altanode machines`, `connect <link>`, `remote <machine> info\|sessions\|history\|send\|create\|watch` | The client side, for testing until the desktop does it: the machines this profile paired with, pairing with a node from its link, and its sessions. They read only the device key and `fleet/paired-machines.json` (written under a lock file), so they run beside a desktop or a node on the same profile. |
| `altanode remote <machine> move <session> [--move-id <id>] [--clone]`, `altanode moves` | A session of a paired machine moves to the node of this profile (which runs, and which the machine lets this device move sessions to it): the machine gives it through `moves.start`, the command forwards the target's methods to the node over its local control. The same `--move-id` goes on with a move that stopped. `moves` lists the move records of the profile (`fleet/moves`). |

`altanode pair`, `status`, `devices`, `revoke`, `permissions` and `uninstall` are the owner's local control of the node
that runs on the same profile: the node writes where it listens in `fleet/node-listener.json` of its state root, and
they open a session channel to it with the node's own device key, read from the profile. The node accepts its own key
only from this machine, and only for the `control.*` methods (pairing offers, status, devices, a graceful stop): the key
file, which only the owner of the profile can read, is the credential, with no token or second endpoint.

**Managing from a desktop.** A paired device with the `manage` permission calls the same `control.*` methods from
elsewhere, pairing and stopping the node excepted: status, devices and their permissions, revocations, projects, the log
(`control.log`), the self-update (`control.update`, as `altanode update`, its switch recorded as asked by
`manage:<device>`), and joining a server (`control.servers.join`, as `altanode join`, with a token the managing device
made; the node enrolls with its own key, so that the device approves the node by the id the node gave it). It is what the machine page of CodeAlta Desktop uses, so that managing a node never needs its
terminal. `manage` is given only when asked: to the device a pairing link made with `altanode pair --manage` adds (the
desktop's **Add a machine** over SSH makes one when the person ticks **Manage it from this computer**, off by
default), and by a device that has it or the owner on the machine (`altanode permissions <device> --allow manage`); a
device paired again keeps it. Being the first device that pairs gives nothing more: a plain link seen by someone else
never hands over the node's devices, secrets and updates. Each call is
checked against the trusted devices at that moment, as every other method is; a member of the organization reached
through a server's relay never has it. The answer to a manager that reduces or revokes its own device goes first, then
its channel closes.

## Running as a service

Agents run as an OS user, and the credentials they use (Git, SSH, `gh`, the Claude Code login, cloud CLIs) live in
that user's home folder and keychain. So the node runs **as the user whose credentials it uses**, never as root or
LocalSystem.

| OS | A person's machine (their logins) | A machine that uses deploy keys and tokens only (a container, a team machine) |
| --- | --- | --- |
| Windows | A task at logon of the user (Task Scheduler). A Windows service would run in Session 0 and not see the user's Credential Manager, ssh-agent or Git Credential Manager | A Windows service (`Microsoft.Extensions.Hosting.WindowsServices`) under a dedicated account |
| macOS | A LaunchAgent of the user: it runs in the login session, with the keychain. An always-on Mac mini keeps that user logged in (automatic login conflicts with FileVault; the guide explains the choice) | A LaunchDaemon with `UserName` (no login keychain) |
| Linux | A `systemd --user` unit with lingering on (`loginctl enable-linger`), so it runs without a login; libsecret needs a session bus, so prefer tokens in files the node protects | A system unit with `User=codealta` (`Microsoft.Extensions.Hosting.Systemd`, `Type=notify`, watchdog) |

**One node per OS user.** A machine several people use runs one node per person, never one privileged node acting
for everyone. A dedicated machine gets its own OS user (`codealta`) with its own logins, set up from the desktop.

- The installer writes the unit with restart on failure, and a bounded log under the state root. It starts the
  executable from a stable path: a .NET tool from its shim (`~/.dotnet/tools/altanode`, a symbolic link on macOS and
  Linux, `altanode.cmd` on Windows), which `dotnet tool update` points at the new version, never from its versioned
  folder under `.store`, which the update deletes. That entry point runs the version `altanode update` selected as
  its child, so an update never rewrites the service ([deployment](deployment.md#updates)). launchd and systemd units get the installer's `PATH` and `DOTNET_ROOT`, since they start with almost none.
- On Windows the task runs `conhost.exe --headless` (no console window) at logon, with a time trigger that starts the
  node again within 5 minutes if it stopped (`conhost` hides the node's exit code from Task Scheduler). Windows runs
  the `.cmd` shim through `cmd.exe`, so `install` refuses an option `cmd.exe` would change (`"`, `%`, `&` outside a
  quoted value). `uninstall`
  deletes the task, then stops the node through its local control. `--system` is refused on Windows until the node
  runs as a Windows service.
- A node keeps the machine awake while sessions work (as `DesktopKeepAwake` does), and lets it sleep when idle
  unless the owner says it is always on.
- Exit is graceful: admission closes, running turns finish or stop after a bound, journals are flushed; a session
  interrupted by a stop is resumable, as after a crash today.

## What a node publishes

| Inventory | Content | Who sees it |
| --- | --- | --- |
| Identity | Node id, name (editable), owner or team, OS, architecture, CodeAlta version | The fleet |
| State | Online, busy (sessions at work), always-on, last seen | The fleet |
| Capabilities | Sandbox backend available, tools found on `PATH` (git, gh, dotnet, node, python, xcodebuild, docker...), CPU and memory class, free disk | The fleet |
| Labels | Set by the owner (`always-on`, `ios`, `gpu`...) | The fleet |
| Providers | Keys, types, readiness, models (never a credential) | The owner's devices; the fleet sees readiness only |
| Projects | Repository keys, branches checked out | The fleet sees the keys (for matching); paths and branches go to the owner's devices only |
| Spaces | The linked spaces whose sessions it publishes | The server of each space sees only its own |

## Credentials on a node

The fleet never copies a secret from one machine to another ([security](security-and-privacy.md#credentials)).
Each node signs in by itself; the desktop drives the sign-ins so that nobody has to sit at the server.

| Need | How the node gets it |
| --- | --- |
| Providers CodeAlta signs in (Codex subscription, Copilot, xAI) | The device code flow runs on the node; the desktop shows the code and the link; tokens stay in the node's `~/.alta/auth/` |
| Providers with an API key, and any other token a run needs | A secret of the node's store ([secrets](secrets.md)): typed by the person from the desktop and sent over the end-to-end channel, or a secret of a space, a team or the organization sealed to the node; kept in its protected store (DPAPI, Keychain, libsecret), or named as an environment variable of the service |
| Claude Code, Codex CLI, ACP agents | Their own login on that machine, done in a setup terminal of the node opened from the desktop (owner only, audited), or over SSH |
| Git over SSH | A key made on the node; its public key added to the Git host (`gh ssh-key add`, or copied by the person); the space identity names it. Organizations can sign short-lived SSH certificates instead (milestone 5) |
| GitHub | `gh auth login` with a device code, per space profile as today (`gh auth setup-git` for HTTPS); with a server, the CodeAlta GitHub App's device flow gives the node expiring user tokens (8 hours, refreshed for up to 6 months) |
| Cloud CLIs | Their device flows (`az login --use-device-code`, `aws sso login --no-browser`, `gcloud auth login --no-browser`), per space CLI profile |
| Organization providers (API keys or gateway keys for unattended runs) | Sealed by the server to this node's key when an admin allows the node to use them; kept in its protected store; lent only to runs of the scopes that may use them ([orchestration](orchestration.md#credentials-of-unattended-runs)) |

**Readiness.** For each linked space, the desktop shows whether the node is ready to run its sessions: the identity
**Check** of the space run on the node (who Git commits as, whether keys exist, which GitHub account), and the
providers of the space that are ready there, with **Set up…** beside what is missing. It also answers two questions
a household asks: **Will this still run after a restart or a logout?** (the kind of service, automatic login,
lingering) and, for an automation, **Why can't this machine run it?** (credential kind, tools, sandbox, project,
labels, its local policy).

## Automations and statistics

- **Automations run on nodes.** The automation engine leaves the desktop (where it runs today) for a library every
  host runs, so a node fires schedules, polls triggers with its credentials, claims runs with a lease, and checks each
  run's signed automation version, its credential kind and its own local policy before starting it
  ([orchestration](orchestration.md)). It keeps the schedules assigned to it, to fire them while the server is
  unreachable.
  - What a paired device assigns (`automations.put`, the `automate` permission, off at pairing) is kept apart from the
    profile's own automations (`fleet/assigned-automations.toml`), with who assigned which fingerprint. It runs while
    that device keeps the permission and only as it was assigned: a copy changed on the node by anything else does not
    run. A command trigger is never taken from another device.
  - Each due time is claimed once with a lease (`fleet/automation-claims.json`), so a time fired twice or caught up
    after a restart runs once; a claim is never taken again (at most once).
  - Before a run starts, the node reads its policy (`fleet/automation-policy.json`: automations on or off, credentials
    `any` or `account` only; a policy file that cannot be read runs nothing) and derives the credential kind from how
    the provider starts; `unknown` is refused, and a refused run is recorded with its reason, without a session.
- **Statistics are counted on nodes.** The statistics engine runs headless too, from the node's first day; the node
  publishes its facts as a versioned set per session ([statistics](statistics.md)).
- **The node's host can ask.** Unlike today's headless runs of `altacli`, which grant everything (`FullTrust`), a node
  routes permission requests and questions to people, bound to their exact attempts, and refuses what nobody may
  approve.

## Workspaces

- A node clones the repositories it is asked to work on into its workspace root (`~/code` by default, set per node),
  as `<host>/<owner>/<repo>`, and adds them to the space that asked.
- Moved sessions and sessions started remotely work in worktrees of their own, so a person's checkout on that
  machine is never changed under them.
- Disk use is shown in the fleet; worktrees of sessions archived or deleted are removed by the existing worktree
  cleanup.

## Limits and good citizenship

- At most N sessions at work at a time (set per node), the others queued, as a machine shared by a family or a team
  needs.
- Background work runs at a lower priority than the person using the machine.
- A node shared by several people (a team build Mac) runs their sessions in the sandbox, under the team's
  credentials, until per-person credentials on shared machines exist (milestone 5,
  [security](security-and-privacy.md#shared-machines)).

## Safety

- A node listens only where its owner allows (loopback, LAN, tailnet interface, or nowhere when a server relays).
- Every request is authenticated by a device key the node trusts (paired, or the same person in the fleet, or a share).
- Agent-started sessions from other machines are off until the owner turns them on, per space; they run sandboxed
  when the policy says so.
- The node's owner can stop everything it runs, from the desktop or with `altanode` locally, and can quarantine the
  node (refuse all remote requests) without uninstalling it.

## Desktop and TUI as nodes

A desktop is a node of the person's other computers while it runs, once they let it be (**Let my other computers pair
with this one**, off by default): it serves the same node API as `altanode` over its own runtime, on a port of its own
(2586, so that a node of the same machine keeps 2585), with the same key it pairs with its machines and its own list of
paired devices. Another desktop pairs with it from a link, exactly as with a node. Until it is turned on, it listens
nowhere and makes no key (S1). On a Tailscale network its links name it on the tailnet first, so that a laptop away
from home reaches it. When it exits, it is offline. A machine that must stay reachable runs `altanode`, and the desktop
opens it in client mode ([architecture](architecture.md#the-desktop-one-session-host-abstraction)). Reaching a desktop
through the relay of a server, as a machine of an organization, is not part of it yet.

## Packaging

- A .NET tool like the other executables (`dotnet tool install -g <package>`), for developers.
- Self-contained archives per platform (`win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`)
  for servers without the .NET SDK, signed.
- A container image (`linux/amd64`, `linux/arm64`) for cloud nodes, with the sandbox's Linux backend.
- Installers come later (winget, Homebrew, apt): see [deployment](deployment.md#packaging).
