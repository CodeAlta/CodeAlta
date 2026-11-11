---
title: Your Machines
---

# Your Machines

A machine is another computer of yours that runs sessions for you: your other laptop or desktop, a Mac mini that stays on, a build machine, a small cloud server. It runs **CodeAlta Desktop**, or **CodeAlta Node** (`altanode`) when it must run without a window, and you pair it with CodeAlta Desktop once. Its sessions are then listed beside yours ([Work on your machines](sessions.md)).

Two computers that both run CodeAlta Desktop pair in a minute, with nothing to install: see [Pair two CodeAlta Desktops](#pair-two-codealta-desktops).

Each machine keeps its own sessions, providers and sign-ins. Nothing is copied from one computer to another, and no server is needed: your computers talk to each other directly. A [CodeAlta Server](server.md) adds machines you reach from anywhere.

**Settings** > **Fleet** > **Machines** lists them in a table: their state, system, version and sessions, how this computer reaches them, and when they last answered. Click a column to sort them, narrow them with **Filter** and the **State**, **System**, **Reached** and **Access** menus, and click a row for its key, its address, **Open its page** and **Forget**.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-fleet-machines.webp" alt="Settings > Fleet > Machines: a table of 22 machines with their state, system, version, sessions, how they are reached and when they were last seen, filters above it, and the row of build-02 open on its details" loading="lazy">
  <figcaption class="small text-secondary mt-2">The machines as a table: filters, sorting, and the details of a row in place.</figcaption>
</figure>

## Add a machine

In CodeAlta Desktop, open **Settings** > **Fleet** > **Machines** and click **Add a machine…**. Choose how this computer reaches the machine:

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-fleet-add-machine.webp" alt="The Add a machine window of CodeAlta Desktop, over Settings > Fleet > Machines: Over SSH (recommended), With a pairing link, and Through my server" loading="lazy">
  <figcaption class="small text-secondary mt-2">Add a machine: over SSH, with a pairing link, or through your server.</figcaption>
</figure>

| Choice | When |
| --- | --- |
| **Over SSH** | A Linux or macOS computer you reach with `ssh`. CodeAlta does everything there; nothing to type on the machine |
| **With a pairing link** | CodeAlta Desktop runs there: paste the link of its **Pair another computer…** (see [Pair two CodeAlta Desktops](#pair-two-codealta-desktops)). Or CodeAlta Node runs there: paste the link of `altanode pair` (see [Pair it with CodeAlta Desktop](#pair-it-with-codealta-desktop)) |
| **Through my server** | The machine joins your CodeAlta Server with one command, and you approve it when it shows the same six words (see [Connect to a CodeAlta Server](server.md)) |

With a [private Dev Tunnel server](server.md#a-private-dev-tunnel), use the whole invitation code: it includes temporary network access, so `altanode` needs no GitHub sign-in and no inbound port or tunnel of its own. Approved devices renew it while online. After more than a token's lifetime offline, get a fresh invitation and join the same address again; this refreshes transport access without replacing an existing device link.

Direct pairing links, without a server, do not grant Dev Tunnel access. A browser sign-in is not a headless pairing credential: use a private network such as Tailscale for direct pairing, or an authenticated `devtunnel connect` port-forward set up separately. The managed Dev Tunnel option is for a server with outbound-connected nodes, not automatic publication of each node.

## Pair two CodeAlta Desktops

Your laptop and your desktop at home, a Windows PC and a Mac: each runs CodeAlta Desktop, and each can be a machine of the other. Nothing else is installed.

1. On the computer whose sessions you want to reach, open **Settings** > **Fleet** > **Machines**. Under **This computer**, turn on **Let my other computers pair with this one**.
2. Click **Pair another computer…**. CodeAlta shows a pairing link, six words, and waits.
3. On the other computer, open **Settings** > **Fleet** > **Machines** > **Add a machine…** > **With a pairing link**, and paste the link (**Copy link** copies it; send it to yourself as you like).
4. Check that the other computer shows the same six words. Leave **Let *name* reach this computer too** ticked to pair both ways, then click **Pair**. The first computer says which computer paired.

The link works once and expires after 10 minutes. Each computer is then listed as a machine of the other: its sessions show in the Explorer, and you read, answer, steer and start them as on any machine ([Work on your machines](sessions.md)). The same six words check both ways; there is no second step.

- **Both ways or one way.** Untick **Let *name* reach this computer too** to reach the first computer from the second only. When the second computer does not let your other computers pair with it yet, pairing both ways turns that on. On the first computer, turn off **Reach the computers that pair with this one too** to pair every computer one way only. A link of CodeAlta Node pairs one way: a node does not reach your desktops.

- **Through your server.** Two computers connected to the same [CodeAlta Server](server.md) need no pairing: turn on **Let my other computers reach this one through my server** instead, and the other computer reaches this one through the server, from anywhere and with no port open (see [Reach a CodeAlta Desktop through the server](server.md#reach-a-codealta-desktop-through-the-server)).
- **While CodeAlta runs.** A desktop is reached while CodeAlta Desktop runs there; once you quit it, the other computers see it **Offline**. A computer that must stay reachable runs [CodeAlta Node](#install-codealta-node-by-hand).
- **Away from home.** On a computer of a [Tailscale](https://tailscale.com) network, the link names it on the tailnet first, so your laptop reaches it from anywhere. **This computer** shows that name. Without Tailscale, the link names its addresses on your local network.
- **What each computer may do.** **Computers paired with this one** lists them, with a switch per permission: **View**, **Drive**, **Start** and **Models** (use its [model servers](model-servers.md)) are on after pairing, both ways. Turn on **Move** to let a computer move its sessions here (see [Move a session](sessions.md#move-a-session)). **Revoke** refuses a computer at once, in that direction only; pairing again trusts it again.
- **Off by default.** Until you turn it on, this computer listens on no network and makes no key. It listens on port 2587 of its networks (2585 and 2586 stay free for a node and a server of the same computer); the first time, Windows or macOS may ask whether CodeAlta may accept connections: allow it. Turning it off stops listening; the paired computers stay listed, and reach it again once you turn it back on.

### Over SSH

1. Type the computer as you would after `ssh`: `user@host`, `user@host:port`, or a host of your SSH configuration. **Name** is optional: the machine's own name by default. Tick **Manage it from this computer** to change its devices, projects, secrets and updates from here. Tick **Always on** to have the node start with the machine, with nobody logged in (see [Always on](#always-on)).
2. Click **Add the machine**. CodeAlta signs in with your SSH keys and agent (the OpenSSH client of your system, without asking for a password), then shows each step as it goes:
   - **Connect over SSH**, and **Check the system and .NET**: Linux or macOS, x64 or Arm64, and .NET 10 there, and `sudo` with **Always on**;
   - **Install CodeAlta Node**: the .NET tool in `~/.dotnet/tools`, or the package of its system where only the .NET runtime is;
   - **Start it as a service**: `altanode install --listen-all`, a `systemd --user` unit (with lingering) on Linux, a LaunchAgent on macOS; with **Always on**, a service that starts at boot;
   - **Pair it with this computer**: the node makes a pairing link for this computer, which pairs at once (and manages the machine, when you ticked it). The six words of its key are shown.
3. Click **Open its page** to give it projects and look at its providers (see [Manage a machine from CodeAlta Desktop](#manage-a-machine-from-codealta-desktop)).

When a step fails, it says why and what to do: add your key to the SSH agent (`ssh-add`) or copy it to the machine (`ssh-copy-id`), install .NET 10 there, open the port of the node in its firewall. On a Mac, macOS asks once to let CodeAlta reach your local network, and keeps it from your machines until you allow it (**System Settings** > **Privacy & Security** > **Local Network**). **Start over** runs every step again from the first and changes only what is missing. A computer whose SSH key this one does not know yet shows the fingerprint of that key (`SHA256:…`): compare it with the one of the machine, then click **Trust it and start over**.

Where CodeAlta Desktop runs on the machine too, the node gets a profile of its own, `~/.alta-node`. **Advanced** sets the port of the node (2585), its profile, the folder it is installed in, and a folder of this computer to install the packages from.

### Always on

Without it, the node starts when you log in on the machine, and after a restart nothing runs until you do. **Always on** makes the node come back by itself, and needs `sudo` there:

- **On a Mac**, a LaunchDaemon that runs as you replaces the LaunchAgent. A LaunchDaemon cannot read your login Keychain, so the setup first moves the key of the node's secrets into a file only you read (`altanode secrets key-file`). Logins kept in the Keychain are not available to the node: sign them in again from its page (see [Sign in on a machine](#sign-in-on-a-machine)).
- **On Linux**, lingering is turned on for your account, so that the `systemd --user` unit starts at boot and survives a logout.

CodeAlta asks for your password on the machine once, only when `sudo` needs it. It goes to `sudo` through the SSH connection and is never kept; a wrong one is asked again. When you have no `sudo` there, the step shows the command to run on the machine as an administrator. A Mac with FileVault waits for its disk to be unlocked after a restart, with or without **Always on**; macOS 26 lets an Apple Silicon Mac with Remote Login on be unlocked over SSH before it starts, and the node then comes back.

Without **Always on**, on a Mac nobody is logged in to, the setup writes the LaunchAgent and says that it starts at the next login. The page of the machine tells whether it starts at boot (see [Manage a machine from CodeAlta Desktop](#manage-a-machine-from-codealta-desktop)).

## Install CodeAlta Node by hand

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

The node uses the same profile as CodeAlta Desktop and TUI, `~/.alta`: its providers, prompts, skills and projects. Set up its providers as you would for the desktop app (see [Model Providers](../model-providers/readme.md)): the sessions of a machine run with the accounts of that machine. Only one CodeAlta runs on a profile at a time, so on a computer where you also use CodeAlta Desktop, run the node only while the desktop app is closed, or give it a profile of its own with `--data-root <folder>` (a full path).

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

| System | What `altanode install` sets up | What `altanode install --system` sets up |
| --- | --- | --- |
| Windows | A Task Scheduler task at your logon, without administrator rights | A Windows service, from a terminal run as administrator |
| macOS | A LaunchAgent in `~/Library/LaunchAgents` | A LaunchDaemon that runs as `--account`, with `sudo` |
| Linux | A `systemd --user` unit, with lingering so that it runs without a login | A system unit that runs as `--account`, with `sudo` |

The service runs as you, with your sign-ins. Run `altanode install` again to change its options, and `altanode uninstall` to remove it: your profile and sessions stay. `altanode install --dry-run` prints what it would set up without changing anything.

`--system` starts the node at boot, whoever is logged in, under an account of its own (a machine that uses deploy keys and tokens only) or as you. `altanode status` says which and whether it will still run after a restart.

- **macOS and Linux**: `sudo altanode install --system --account <name>`. When the account is a person whose secrets key is in the Keychain (macOS) or libsecret (Linux), that person first runs `altanode secrets key-file`, without `sudo`: it moves the key into a file only they read, which a service without a login can use. `install` says when.
- **Windows**: `altanode install --system` registers a Windows service that starts at boot and starts again when it stops. It runs as its own account, `NT SERVICE\altanode`, which needs no password, with its profile under `%ProgramData%\CodeAlta`: sign it in from its page in CodeAlta Desktop (see [Sign in on a machine](#sign-in-on-a-machine)). To run as you instead, with your own sign-ins, give your Windows password once on standard input: `altanode install --system --account <you> --password-stdin`. Windows keeps the password; CodeAlta does not. After you change your Windows password, run the same `install` again. Stopping the service lets running turns end first.
- Installed as a .NET tool, the Windows service runs the program of the tool: stop the service before `dotnet tool update` and run `install --system` again after it. `altanode update` keeps working under the service.

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

`dotnet tool update` works too: once the tool is newer than the version `altanode update` installed, the tool's version runs from the next start of the node.

To allow only some feeds on a machine, list them in `fleet/update.json` of the profile (`update.json` of the data root for the server): `{ "source": "https://my-mirror/v3/index.json", "allowedSources": ["https://my-mirror/v3/index.json"] }`. Add `"fleet": false` to update that machine only by hand.

From CodeAlta Desktop, the page of a machine you manage updates it with **Check for updates** (see [Manage a machine from CodeAlta Desktop](#manage-a-machine-from-codealta-desktop)). With a CodeAlta Server, update all your machines at once: see [Update all your machines](operations.md#update-all-your-machines).

## Give it projects

The sessions of a node run in the projects of its catalog. In CodeAlta Desktop, on a computer that manages the machine (see [Manage a machine from CodeAlta Desktop](#manage-a-machine-from-codealta-desktop)), open its page and, under **Projects**, type a repository URL and click **Clone**, or the full path of a folder of the machine and click **Add the folder**. The trash button of a project removes it from the machine's projects; its files stay.

On the machine of the node:

| Command | What it does |
| --- | --- |
| `altanode projects` | Lists its projects |
| `altanode projects add <path>` | Adds a folder of the machine |
| `altanode projects clone <url>` | Clones a repository into `~/code/<host>/<owner>/<repo>` and adds it; a checkout already there is added as it is |
| `altanode projects remove <project>` | Removes a project (its id, slug or name) from the catalog; its files stay |

`--space <space>` with `add` or `clone` puts the project in a space too. While the node runs, it makes the change, and keeps a project that still has sessions; otherwise the commands change the catalog of the profile directly.

## Tag your machines

Tags such as `gpu`, `prod` or `always-on` say what a machine is for. Its page in CodeAlta Desktop shows them under **Machine tags**: type one or more, separated by commas or spaces, and **Add**; the cross of a tag removes it. On the machine itself, `altanode tags` lists them, `altanode tags add gpu prod` and `altanode tags remove gpu` change them, and `altanode tags set` replaces them all.

A machine linked to a CodeAlta Server publishes its tags there: the **Fleet** canvas shows them on its card, agents find the machines with `alta fleet machine list --tag gpu`, and an [environment](../secrets.md#environments) that names tags runs only on the machines with one of them. A tag is letters, digits, `-`, `_`, `.` and `:`.

Tags were named labels before CodeAlta 4.2: `altanode labels` and `--label` still work.

## Limit the sessions at work

A machine that serves a few sessions well can say so: beyond its limit, the work of other machines waits for its turn instead of slowing every session down. On its page in CodeAlta Desktop, under **Limits**, type the number in **Sessions at work at once**, or leave it empty for no limit. On the machine itself, `altanode limits sessions 3` sets it, `altanode limits sessions off` removes it, and `altanode limits` shows it.

- A session and its sub-sessions count once, and a session that waits for you (a question, a command to allow) holds no seat.
- What waits is a prompt sent from another computer, a session an agent of another machine starts, a prompt queued for such a session, and a run of an automation. They start first in, first out. A prompt that waits still waits after the machine restarts; a run of an automation that waits is interrupted by the restart.
- The work started on the machine itself is counted, and never waits.
- **Limits** lists what waits, in order, each with what it is and when it joined the queue: **Start now** starts one at once, past the limit, and **Take out of the queue** drops it.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-fleet-machine-limits.webp" alt="The Machine tags and Limits sections of the page of Workstation: the tags gpu and always-on; one session at work at once, 1 of 1 at work, 1 queued, and the prompt of SharpYaml that waits, with Start now and Take out of the queue" loading="lazy">
  <figcaption class="small text-secondary mt-2">A limit of one session: the second waits for its turn.</figcaption>
</figure>

**Run on**, the **Fleet** canvas and `alta fleet machine list` show the load of a machine, such as `3 of 3 at work, 2 queued`.

## Pair it with CodeAlta Desktop

1. On the machine of the node, while it runs, type `altanode pair`. It prints a pairing link, `codealta-pair://…`, and six words, then waits.
2. In CodeAlta Desktop, open **Settings** > **Fleet** > **Machines**, click **Add a machine…**, then **With a pairing link**.
3. Paste the link. CodeAlta shows the name of the machine and six words: check that they are the words `altanode pair` printed.
4. Click **Pair**. `altanode pair` says which device paired, and the machine is listed as **Online**.

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-fleet-pair.webp" alt="The Pair a machine window with a pairing link pasted, the name and address of the machine, and the six words it shows" loading="lazy">
  <figcaption class="small text-secondary mt-2">Check that the machine shows the same six words, then pair.</figcaption>
</figure>

The link works once and expires after 10 minutes (`altanode pair --minutes <n>` gives it more). Pairing again with a new link replaces the machine, for example after its address changed.

A paired computer may see the sessions of the machine, drive them and start new ones. Moving sessions to the machine stays off (see [Move a session](sessions.md#move-a-session)). A computer also manages the machine only when you ask, the first one included: `altanode pair --manage` makes a link for that, and **Add a machine…** > **Over SSH** has **Manage it from this computer** (off until you tick it). Its row then says **Managed from here** (see [Manage a machine from CodeAlta Desktop](#manage-a-machine-from-codealta-desktop)).

## Manage a machine from CodeAlta Desktop

In **Settings** > **Fleet** > **Machines**, click the name of a machine to open its page. A computer that manages the machine changes everything there, without a terminal on the machine:

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-fleet-machine-page.webp" alt="The page of the machine Workstation in Settings > Fleet > Machines, managed from this computer: its devices with a switch per permission, its permissions, the agents of other machines, its people and the start of its machine tags" loading="lazy">
  <figcaption class="small text-secondary mt-2">The page of a machine you manage.</figcaption>
</figure>

| Section | What you do |
| --- | --- |
| **Devices** | Each computer paired with the machine, with a switch per permission: **View**, **Drive**, **Start**, **Move**, **Automate**, **Models** and **Manage**. **Revoke** (the cross at the end of the row, then **Revoke**) refuses that computer there at once. |
| **Permissions** | What a session of the machine without a mode of its own does before it runs a command or changes a file: **Bypass permissions** (the default) or **Ask first**; the sandbox its sessions run in, or why it cannot run there and what to install (see [What its sessions may do](sessions.md#what-its-sessions-may-do)) |
| **Agents of other machines** | The spaces where agents of other computers may start sessions there and send prompts to its sessions, the sandbox their sessions run in at least, and whether they may bypass permissions ([Let agents start sessions on it](sessions.md#let-agents-start-sessions-on-it)) |
| **People** | The people of your organization who may use it, each on their own sessions, and who asked to ([People and Access](people-and-access.md#people-on-your-own-machines)) |
| **Machine tags** | Its [tags](#tag-your-machines), which it publishes to its servers: add them, or remove one with its cross |
| **Limits** | How many sessions work there at once, and the runs that wait for their turn, with **Start now** and **Take out of the queue** ([Limit the sessions at work](#limit-the-sessions-at-work)) |
| **Projects** | Clone a repository there, add a folder, remove a project; how many [worktrees](sessions.md#start-a-session-on-it) its sessions work in, and what they take on its disk |
| **Providers** | Each provider of the machine and whether it is ready; when it is not, why: no key, the secret of the machine it waits for, not signed in. **Add a provider…** and **Add its key…** take its key here: it goes to the machine encrypted from end to end and is kept in its secrets (`PROVIDER_<KEY>_API_KEY`), never in its configuration. **Sign in…** signs in a provider such as GitHub Copilot or xAI on the machine: its code shows here with **Copy**; enter it on the provider's page in your browser |
| **Sign-ins** | What the machine signs in to by itself, and as whom: Codex and the other providers that sign in with an account, ACP agents, MCP servers with OAuth, and the tools of its spaces. **Sign in…** does it from here ([Sign in on a machine](#sign-in-on-a-machine)) |
| **Secrets** | Its [secrets and variables](../secrets.md), as **Settings** > **Secrets** shows those of this computer: **Add a secret…**, **Rotate** and **Delete** a secret, edit or delete a variable. A value goes to the machine and is never shown back |
| **Start at boot** | Whether the node starts when the machine does, with nobody logged in, in the words of `altanode status`; when it does not, the command to run on the machine that makes it so |
| **Updates** | The version it runs. **Check for updates**, then **Update to** the version found: it switches once no session works |
| **Log** | **Show the log** reads its last lines |

**Settings** > **Fleet** > **Machines** lists all your machines, whatever the way this computer reaches them: those paired with it, and those added through your server, marked **Through your server**. Both open their page; a machine of the server has nothing to forget here.

A computer manages a machine when its pairing asked for it (`altanode pair --manage`, or **Manage it from this computer** over SSH). Give another computer the **Manage** switch on its row to let it manage the machine too; turning it off for **This computer** asks first, since only the owner can give it back. A computer that does not manage the machine sees its providers, and how its owner gives it the permission. A machine this computer reaches only through your server is not paired with it: to manage it, run `altanode pair --manage` on it and paste the link in **Pair a machine…**.

On the machine itself, the same is done with:

| Command | What it does |
| --- | --- |
| `altanode devices` | Lists the computers paired with the node |
| `altanode revoke <device>` | Revokes a computer and disconnects it at once |
| `altanode permissions <device>` | Shows what a computer may do |
| `altanode permissions <device> --deny start` | Takes a permission away: `view`, `drive`, `start`, `move`, `automate` or `manage` (`--allow` gives one) |
| `altanode pair --manage` | A pairing link whose computer manages the node |
| `altanode permission-mode` | Shows whether its sessions bypass permissions or ask first; `bypass` or `ask` changes it |
| `altanode agents allow --space <id>` | Lets agents of other computers start sessions in a space (`deny` stops them; `--sandbox` and `--bypass` as on the page) |

**Forget** on the row of a machine removes it from this computer; its sessions go on there. To make sure a lost or replaced computer can no longer reach a node, revoke it on the page of the machine, from a computer that manages it.

From a terminal, `altanode connect <link>` pairs another profile with a node, and `altanode remote <machine> sessions`, `history`, `send`, `create` and `watch` read and drive the sessions of a paired machine. `altanode remote <machine> move <session>` moves one of them to the node of that profile.

## Sign in on a machine

A machine signs in to its providers, MCP servers and tools itself; nothing is copied from your desktop. Its page lists them under **Sign-ins**, with whether each is signed in and as whom, and **Sign in…** does it in one click, with no terminal and no SSH on the machine:

| What | How it signs in |
| --- | --- |
| Codex and MCP servers with OAuth | The page opens in the browser of this computer. When you have signed in, the browser sends its answer back to the machine, which finishes the sign-in |
| GitHub Copilot, xAI, Codex CLI | A code shows here with **Copy**: enter it on the page it names |
| `gh` of each space, and the tools whose login a space names in its CLI profiles (`gh`, `glab`, `az`, `aws`, `gcloud`) | The machine runs the login with the identity of the space and shows its page and code here, or asks for the code its page gives |
| Mistral Vibe | Its page opens in the browser of this computer. Sign in there and choose the workspace: the machine waits for the console of Mistral and keeps the key it gives |
| Claude Code | The machine runs `claude setup-token` (on Windows too): its page opens here, and you paste the code it shows. The machine keeps the token it gets as a secret and Claude Code uses it |
| ACP agents that take an API key | You type the key here. The machine keeps it as a secret, names it in the agent's environment, and it never comes back to this computer |
| Other ACP agents | They sign in on the machine itself: the list says how, such as the command to run in a terminal there. An agent that signs in with a browser it opens on its own machine cannot finish from another computer |

**Sign out** removes the sign-in of a provider or an MCP server. A tool keeps its own login on the machine. Claude Code's token and an ACP agent's key go with their **Sign out**.

The machine exchanges the code and keeps the token: this computer only opens the page and relays the code or the address the browser returns, which is useless without what the machine holds. If another program holds the port the browser comes back to, the sign-in says it cannot start and the machine keeps nothing half done. A sign-in waits 15 minutes at most for you, and closing its window cancels it.

Only a computer that manages the machine signs it in (see [Manage a machine from CodeAlta Desktop](#manage-a-machine-from-codealta-desktop)). A person reached through your server never does.
