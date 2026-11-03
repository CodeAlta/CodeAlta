---
title: Fleet
---

# Fleet

Your other computers can run sessions for you: a Mac mini that stays on, a build machine, a small cloud server. You install **CodeAlta Node** (`altanode`) there, pair it with CodeAlta Desktop once, and its sessions are listed beside yours: you read them, answer their questions, send them prompts and start new ones there.

Each machine keeps its own sessions, providers and sign-ins. Nothing is copied from one computer to another, and no server of ours is involved: your computers talk to each other directly.

## Install CodeAlta Node

On the machine that will run the sessions, install [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), then the node:

```sh
dotnet tool install -g CodeAlta.Node
altanode --help
```

The package runs on Windows, macOS and Linux (x64 and Arm64, Alpine included). `altanode update` updates it (see [Update it](#update-it)).

On a machine without the .NET SDK, publish a self-contained `altanode` from a checkout of CodeAlta on a computer that has it, for the system of that machine (`win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`), and copy the folder:

```sh
dotnet publish src/CodeAlta.Node -c Release -r linux-x64 --self-contained -o altanode-linux-x64
```

The node uses the same profile as CodeAlta Desktop and TUI, `~/.alta`: its providers, prompts, skills and projects. Set up its providers as you would for the desktop app (see [Model Providers](model-providers/readme.md)): the sessions of a machine run with the accounts of that machine. Only one CodeAlta runs on a profile at a time, so on a computer where you also use CodeAlta Desktop, run the node only while the desktop app is closed, or give it a profile of its own with `--data-root <folder>` (a full path).

The projects a session can start in on the machine are the projects of that profile: add them with `altanode projects` (see [Give it projects](#give-it-projects)).

## Run it

```sh
altanode run --listen-all
```

The node runs in the foreground and writes its log to the console. **Ctrl+C** stops it: running turns get a moment to finish.

| Option | What it does |
| --- | --- |
| `--listen-all` | Listens on every network of the machine (your LAN, a Tailscale network) |
| `--bind <address>` | Listens on one address only, such as the machine's Tailscale address |
| `--port <port>` | Another port than 2585 |
| `--name <name>` | The name your other computers see (the machine name by default) |
| `--no-listen` | Runs sessions without any connection from other computers |

Without `--listen-all` or `--bind`, the node listens on the machine itself only (`127.0.0.1`), and other computers cannot reach it.

`altanode status` tells whether the node runs and whether it will start again after a restart. `altanode logs` prints the end of its log; `altanode logs --follow` keeps printing it.

## Run it as a service

To have the node start by itself, install it as a service with the options it runs with:

```sh
altanode install --listen-all
```

| System | What `altanode install` sets up |
| --- | --- |
| Windows | A Task Scheduler task at your logon, without administrator rights |
| macOS | A LaunchAgent in `~/Library/LaunchAgents` |
| Linux | A `systemd --user` unit, with lingering so that it runs without a login |

The service runs as you, with your sign-ins. Run `altanode install` again to change its options, and `altanode uninstall` to remove it: your profile and sessions stay. `altanode install --dry-run` prints what it would set up without changing anything.

For a machine that should start the node at boot under an account of its own (one that uses deploy keys and tokens only), use `altanode install --system --account <name>`, with `sudo` on macOS and Linux.

## Update it

```sh
altanode update
```

The node downloads its latest version from NuGet and switches to it as soon as no session is working: meanwhile it takes no new work, for ten minutes at most, then waits for the next quiet moment. It restarts by itself as the new version, whether it runs as a service or in a terminal: nothing else to do on the machine, and no .NET SDK needed, only the .NET runtime (a self-contained copy is updated by copying a new one).

| Option | What it does |
| --- | --- |
| `--check` | Only tells whether an update is available |
| `--version <version>` | Installs that version, an older one too |
| `--now` | Switches without waiting: running turns stop and can be resumed |
| `--source <feed>` | Another NuGet feed, or a folder of packages |

If the new version does not start and connect within two minutes, the node goes back to the previous one by itself and does not take that version again unless you name it with `--version`. `altanode status` shows the version that runs and how the last update went.

`altaserver update` does the same for CodeAlta Server: it first copies its database to the `backups` folder of its data root, and puts that copy back if the new version has to be undone.

To allow only some feeds on a machine, list them in `fleet/update.json` of the profile (`update.json` of the data root for the server): `{ "source": "https://my-mirror/v3/index.json", "allowedSources": ["https://my-mirror/v3/index.json"] }`. Add `"fleet": false` to update that machine only by hand.

With a CodeAlta Server, update all your machines at once from CodeAlta Desktop: see [Update all your machines](#update-all-your-machines).

## Give it projects

The sessions of a node run in the projects of its catalog. On the machine of the node:

| Command | What it does |
| --- | --- |
| `altanode projects` | Lists its projects |
| `altanode projects add <path>` | Adds a folder of the machine |
| `altanode projects clone <url>` | Clones a repository into `~/code/<host>/<owner>/<repo>` and adds it; a checkout already there is added as it is |
| `altanode projects remove <project>` | Removes a project (its id, slug or name) from the catalog; its files stay |

`--space <space>` with `add` or `clone` puts the project in a space too. While the node runs, it makes the change, and keeps a project that still has sessions; otherwise the commands change the catalog of the profile directly.

## Pair it with CodeAlta Desktop

1. On the machine of the node, while it runs, type `altanode pair`. It prints a pairing link, `codealta-pair://…`, and six words, then waits.
2. In CodeAlta Desktop, open **Settings** > **Fleet** > **Machines** and click **Pair a machine…**.
3. Paste the link. CodeAlta shows the name of the machine and six words: check that they are the words `altanode pair` printed.
4. Click **Pair**. `altanode pair` says which device paired, and the machine is listed as **Online**.

The link works once and expires after 10 minutes (`altanode pair --minutes <n>` gives it more). Pairing again with a new link replaces the machine, for example after its address changed.

A paired computer may see the sessions of the machine, drive them and start new ones. Moving sessions to the machine stays off (see [Move a session](#move-a-session)).

## See and drive its sessions

The sessions of a paired machine are listed in the Explorer with your own, with a chip that names their machine:

- A session in a project you also have here, a checkout of the same git repository, is listed under that project, whatever its folder and its name on each computer.
- The other sessions of the machine are listed under a row named after the machine. That row is there as soon as the machine is paired, sessions or none, and is dimmed while the machine is offline; its tooltip says its state.

Open one: its tab is the tab of any session. You read its timeline as it works, answer its questions and permission requests, send it prompts, steer it and stop it. The provider and the model are the ones the machine gives it.

What works on the files of a project (the code editor, Changes, terminals, worktrees) is not offered for a session of another machine: its folder is on that machine. Renaming, forking, archiving and deleting it are not offered either. Pictures and files attached to a prompt are not sent to another machine.

While a machine is offline, its sessions stay listed with what was last known of them, and CodeAlta connects again by itself.

## Start a session on it

1. Open a new session in a project, or in **Chats**.
2. Beside the agent chip above the prompt, click **Run on** (it shows **This machine**) and choose the machine. Only machines that are online are offered.
3. Choose the project of that machine: when the machine has a checkout of the same repository as the project you are in, it is already chosen. **Chat (no project)** starts a session in no project.
4. Choose the provider and the model among the ones ready on that machine.
5. Write your prompt and send it. The session starts on the machine and opens in its tab.

The **+** of the row of a machine in the Explorer opens the same prompt, already set to run on that machine.

## Move a session

A session can move to another machine and go on working there: its conversation, and the work of its branch.

Moves are off until you allow them on the side that receives the session:

- **To a node**: on the machine of the node, give the computer the Move permission with `altanode permissions <device> --allow move`. The same permission lets that computer take sessions of the node back.
- **To this computer**: in **Settings** > **Fleet** > **Machines**, turn on **Receive moved sessions on this computer**.

To move a session:

1. In the menu of the session, click **Move to machine…**.
2. Choose the machine: a session of another machine can also come back to **This computer**.
3. Read what the move meets: a session at work moves once it is idle, a question waits for your answer, and the machine needs a project of the same git repository (**Clone the repository there** offers to make one).
4. Click **Move**. The session opens where it runs now.

Only a session of a project in a git repository with a remote moves. On the machine, it works in a worktree of its own, on its branch: its commits that are not pushed and its changes that are not committed go with it.

What stays behind: its background jobs, reminders and terminals, and the files that look like secrets (the dialog names them). A move that a lost connection stopped goes on when the machine is back.

## Manage the devices of a node

On the machine of the node:

| Command | What it does |
| --- | --- |
| `altanode devices` | Lists the computers paired with the node |
| `altanode revoke <device>` | Revokes a computer and disconnects it at once |
| `altanode permissions <device>` | Shows what a computer may do |
| `altanode permissions <device> --deny start` | Takes a permission away: `view`, `drive`, `start` or `move` (`--allow` gives one) |

In CodeAlta Desktop, **Forget** on the row of a machine in **Settings** > **Fleet** > **Machines** removes it from this computer; its sessions go on there. To make sure a lost or replaced computer can no longer reach a node, revoke it on the node.

From a terminal, `altanode connect <link>` pairs another profile with a node, and `altanode remote <machine> sessions`, `history`, `send`, `create` and `watch` read and drive the sessions of a paired machine. `altanode remote <machine> move <session>` moves one of them to the node of that profile.

## Connect to a CodeAlta Server

A CodeAlta Server shows the machines of all your computers, whether they are online, and the sessions they publish, in one place. It never reads their content: a session publishes its state, its provider and model, its times and its counts (turns, tool calls, tokens), never its prompts or answers. Its title travels encrypted with a key that only your approved computers and machines hold: the server cannot read it.

1. On the always-on machine, run `altaserver run`. It prints a one-time setup code (`altaserver setup-code` prints it again).
2. In CodeAlta Desktop on that machine, open **Settings** > **Fleet** > **Server**, click **Create my server…**, and give its address (`http://127.0.0.1:2586`), the setup code and your name. This computer becomes the owner's.
3. To add a node, click **Add a machine…** and run the command it shows on the machine: `altanode join <address> --token <code>`. The node appears under the server, **Waiting for approval**: click **Approve** when it shows the same six words as the node.
4. Another of your computers joins with **Join a server…** and an enrollment code; you approve it the same way from a computer already approved. Once approved, it can add and approve machines too. A node never approves anything.

A space publishes to a server only when you choose it: **Settings** > **Spaces** > the space > **General** > **Server**. `altanode join` links the default space of the node (`--space` chooses others). A session made **Private** in its options (the Agent chip, **More options**) is never published; one made Private later, or whose space you unlink, is removed from the server.

The **Fleet** canvas (`/fleet`) shows the machines of the server and their sessions, with their titles, as they change. A session of this computer, of a paired machine or of a node of the server opens with **Open**; the others are shown only. `altanode status` says whether the node is connected to its server.

### Reach machines through the server

The nodes of your server are reachable from CodeAlta Desktop without pairing and without opening a port on them: each machine only connects out to the server, which relays the session between them. They are listed in the Explorer like paired machines, and their sessions are read, watched and driven the same way. A node joined with `altanode join` and run with `--no-listen` has no inbound port at all.

- A paired machine is reached directly when one of its addresses answers, and through the server otherwise (on another network, say).
- The tab of a remote session says **Direct** or **Relayed** after its title.
- The connection is encrypted from one computer to the other: the server passes it on without being able to read it, and records only who reached which machine, when and how much data went through.
- Your own computers may see, drive and start sessions on your nodes, as a paired computer does. Other machines of the server may do nothing there yet.

### Send telemetry to a collector

Your server and your nodes can send their traces, metrics and logs to an OpenTelemetry collector (Grafana, Datadog, Azure Monitor, the Aspire Dashboard...), so that you follow their activity and their errors without connecting to them.

1. In CodeAlta Desktop, on the owner's computer, open **Settings** > **Fleet** > **Server**. The card of the server shows **Telemetry**: click **Change…**.
2. Turn on **Send to a collector** and give the address of the collector (`https://otlp.example:4318`), its protocol, and the signals to send: **Traces**, **Metrics**, **Logs**.
3. Choose the route:
   - **Each machine sends to the collector**: each machine needs to reach it; the **Headers** (an API key, say) go to every machine.
   - **Machines send through this server**: the machines send to the server, which passes everything on to the collector with the headers you give here. They never get these headers and need no other way out. The server keeps nothing of what it passes on.
4. Add the **Headers** the collector asks for and, if you want, an **Environment** (`production`). Click **Save**.

The server applies the setting at once and sends it to every machine; each machine checks that your computer signed it before it applies it, so the server alone cannot send your machines' telemetry elsewhere. A header you saved shows masked: leave it empty to keep it.

Nothing sent holds your prompts, answers, tool calls or session titles. **Logs** are the records of CodeAlta itself: they may hold file paths and error messages, so they are sent only when you choose them.

The owner of a machine decides last, with the `telemetry.json` file of the machine: `~/.alta/fleet/telemetry.json` for a node, `~/.altaserver/telemetry.json` for the server.

| `telemetry.json` | Effect |
| --- | --- |
| `{ "enabled": false }` | Sends nothing, whatever the fleet says |
| `{ "fleet": false }` | Ignores the fleet's setting |
| `{ "endpoint": "http://localhost:4318", "signals": ["traces", "metrics", "logs"] }` | Sends to this collector instead |

The standard `OTEL_EXPORTER_OTLP_ENDPOINT` and other `OTEL_*` variables of the service also work, and win over the fleet's setting; `OTEL_SDK_DISABLED=true` turns telemetry off.

### Update all your machines

1. In CodeAlta Desktop, on the owner's computer, open **Settings** > **Fleet** > **Server**. The card of the server shows **Updates**: the version of the server and the update policy.
2. Click **Update the fleet…**. Leave **Version** empty for the latest one, or type a version; **Feed** empty for nuget.org.
3. Choose **Now**, or **At a time** and the date and the hour, in the time zone of your computer.
4. Untick the machines that should not update, then click **Update**.

The machines update first, two at a time, each when no session is working, then the server updates itself. The card follows the campaign: each machine with its state, and **Cancel** until the server's turn comes. A machine that is offline updates when it connects again. The Fleet canvas shows the version of each machine.

Your computer signs what the machines install, with the hash of the package of each system: a machine installs only an update that a computer of the owner signed, never an older version unless you named it, and only the package that matches that hash. The server alone cannot make your machines install anything.

**Policy…** sets how the fleet updates:

| Policy | Effect |
| --- | --- |
| **Manual** | Machines update only when you click **Update the fleet…** |
| **Pinned** | The fleet stays on one version: an update to another version is refused until you change the policy |
| **Automatic** | The server reads the feed every day and updates the fleet to its latest stable version at the time of day you choose; each machine checks the package against the hash the feed publishes |

### See the statistics of all your machines

Each machine linked to your server counts the statistics of its sessions, a node as CodeAlta Desktop does, and sends them to the server: tokens, active time, runs, tool calls and estimated costs, by provider, model and quarter hour.

1. In CodeAlta Desktop, open the **Statistics** tab.
2. At the top, choose **My fleet** instead of **This machine**.
3. Pick the period: **Last 7 days**, **Last 30 days**, **Last 90 days** or **This month**.

**My fleet** shows the totals, then the numbers **By machine** (with when each machine last sent its statistics, so a machine that stopped sending shows), **By project** and **By model**. The days are those of your computer's time zone.

- A machine sends the statistics of the sessions it publishes to the server, from the day it was linked to it. A **Private** session sends nothing; a session made Private later, or whose space you unlink, has its statistics removed from the server.
- The server receives numbers and the names of providers, models and tools, never prompts, answers or file names. Projects travel as keys made with the key only your computers hold: the server cannot tell which project a key is, and your computer names the projects it has too (the same repository on two machines is one project). A project only another machine has shows as **Project** and the start of its key.
- A machine that was offline sends what it counted when it comes back, and sending the same statistics again changes nothing.
- **My fleet** shows the machines of your own fleet. **This machine** is the full Statistics page, with its filters and comparisons, for this computer.

A node starts counting with the day it first runs.

## Security

- **Remote sessions run code on the machine.** A paired computer can start agents there that use its tools, as you would sitting at it. Pair only your own computers, and revoke the ones you no longer use.
- **Keys are pinned.** Each computer has its own key. Pairing records the key of the other side, and every connection checks it: a machine that presents another key is refused. The six words let you check, at pairing, that the link comes from the machine you think. Through a server, the key comes from the list of devices your own computers signed, never from the server alone, and a device revoked there is refused.
- **Permission requests wait for a person.** The node never grants a permission request by itself: you answer it from a paired computer or on the machine.
- **Credentials stay where they are.** Each machine signs in to its providers itself; nothing is copied from your desktop.
- **Choose where the node listens.** By default it listens on the machine itself only. `--listen-all` opens it to every network the machine is on: prefer a private network you control.
- **Tailscale is the simplest safe network.** With [Tailscale](https://tailscale.com) on your computers, bind the node to the machine's Tailscale address (`altanode run --bind 100.x.y.z`) or use `--listen-all`: your computers reach it from anywhere, through an encrypted network only they are on, and nothing is exposed to the internet. Do not forward the node's port on your router.
