---
title: Sandbox
---

# Sandbox

A sandboxed session runs its commands and its file tools in a container of the operating system. What it can read, write and reach is what its sandbox allows, whatever its permission mode. A prompt slipped into a web page or an issue cannot then read your credentials, change files outside the project, or leave behind something that runs later on your computer.

The sandbox is off by default. Turn it on for every session, or only where you want it: a provider, a model, a space, a project, one session.

## What it protects

In a sandboxed session:

- **Commands, file tools and background jobs** run in the container. They write in the folder of the session, its worktree, its scratchpad and a temporary folder, and read the system and your developer tools.
- **Your home folder is out of reach**, except the developer tools. So are the files of CodeAlta (`~/.alta`), your SSH keys and the credentials of providers and clouds. The only files of CodeAlta a session reads are the long outputs of its own tools, saved with it, which it does not write.
- **Skill folders are read-only**: your own (`~/.alta/skills`, `~/.agents/skills`, `~/.copilot/skills`) and the [folders you added](../workspace/settings.md#skill-folders), so that an agent reads the files of a skill it uses. One inside the folder the session works in stays as it is; a folder of secrets, your home folder or a whole disk is never opened this way.
- **Files that run programs later stay read-only**, even inside the project: `.git/config`, git hooks, the `.alta` settings and plugins, the settings of editors and of other coding agents. On Windows, CodeAlta holds them open while the sandbox runs, so that nothing replaces, deletes or moves them: in that time, git cannot change the configuration of the repository either (`git config`, `git push -u`, a new branch that tracks a remote, a worktree created in a repository with submodules), nor a `git checkout` or `git pull` in the same folder change one of those files. CodeAlta's own settings are saved as usual.
- **The network** is what the profile says: the package registries only, nothing, or the internet. This computer and its local network are never reached.
- **The environment** has no secret of yours, unless the profile names it.

It does not protect against what you allow: a command you let run outside the sandbox runs with your rights. The model provider still receives what the session sends it.

## Turn it on

In CodeAlta Desktop, **Settings > Sandbox** has:

- **Status**, with **Check**, which starts a test sandbox and shows each step: the developer tools, a write in a test project, a file of CodeAlta that must stay out of reach, the network.
- **Every session**: not sandboxed, or sandboxed with a profile.
- **Profiles**, and **Where it applies**: the providers, models, spaces and projects that choose a sandbox, each with a link to where it is set.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-sandbox-settings.webp" alt="Settings > Sandbox: the status of the sandbox of Windows with each step of its check passed, the choice for every session, and the profiles" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings > Sandbox: <strong>Check</strong> starts a sandbox with a profile and shows what it reaches and what stays out of reach.</figcaption>
</figure>

A sandbox is also chosen:

- for a provider, in its form in [model providers](../model-providers/readme.md);
- for a space, in its **New sessions** card (see [spaces](../spaces.md));
- for a project, with **Sandbox…** in the actions of its row;
- for one session, in the **Sandbox** row of the [Agent chip](../workspace/prompt.md), or of the form of a new session.

When several say something, the most specific wins: the session, then its model, its provider, its project, its space, then every session. **Not sandboxed** can be chosen at each level, for example for a project you trust.

The chip shows a small mark on a sandboxed session; its tooltip names the profile and where it comes from.

The [policy of your organization](../model-providers/setup.md#policy-of-your-organization) for Copilot can require the sandbox: every session then runs in it, whatever these settings say, and **Every session** shows a lock.

In CodeAlta TUI, the sandbox follows the same settings; it is set in CodeAlta Desktop, in `config.toml`, or with `alta session`.

## Profiles

Three profiles come with CodeAlta:

| Profile | Network | Run outside | Other machines |
| --- | --- | --- | --- |
| `workspace` | The package registries: NuGet, npm, Yarn, PyPI and crates.io | Asked each time | Allowed |
| `strict` | None | Never | Never |
| `node` | The package registries, and GitHub, GitLab, Bitbucket and Azure DevOps | Asked each time | Allowed |

`node` is the one the machines of the fleet run their sessions in (see [On a machine of the fleet](#on-a-machine-of-the-fleet)).

**New profile** makes a profile of your own, which starts from the profile shown and adds to it:

- **Folders**: more folders to read and write, to read only, or never to reach.
- **Network**: **Allowed hosts** (`*.example.com` for the subdomains of a domain, `example.com` for the domain itself), **Off** or **On**.
- **Local ports**: ports of this computer it may reach, such as a local database.
- **MCP servers**: the servers a sandboxed session may use. Others are not offered to it.
- **Run outside**: **Ask me**, or **Never**.
- **Other machines**: **Allow** or **Never**: whether its agents start or drive sessions on the other machines of your [fleet](../fleet/readme.md).

In `~/.alta/config.toml`:

```toml
[sandbox]
all_sessions = "workspace"

[sandbox.profiles.company]
extends = "workspace"
hosts = ["pkgs.dev.azure.com", "*.company.com"]
local_ports = [5432]
mcp_servers = ["docs"]
fleet = "never"

[providers.ollama]
sandbox = "strict"
```

A `[sandbox]` table in the `.alta/config.toml` of a repository is ignored: a repository does not choose its own sandbox.

## Run a command outside

A command that cannot work in the sandbox, such as a publish that needs your API key, can ask to run outside it, with the reason. You are asked each time, in every permission mode, **Bypass permissions** included:

```text
Run outside the sandbox?
  dotnet nuget push bin/Lib.1.2.0.nupkg --source nuget.org            in C:\code\Lib
  "Publishing needs the API key in your NuGet.Config, which the sandbox cannot read."
  [Allow once]  [Deny]
```

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-sandbox-session.webp" alt="A sandboxed session of SharpYaml asks to run git fetch outside the sandbox, with its folder and its reason" loading="lazy">
  <figcaption class="small text-secondary mt-2">The sandbox keeps <code>git fetch</code> from reaching GitHub: the agent asks to run it outside, and says why.</figcaption>
</figure>

The timeline marks the commands that ran outside. With the `strict` profile, or **Never**, no command leaves the sandbox: the agent asks you to run it.

When the sandbox blocks a command, the agent is told what blocked it, a host or a file, so that it does not try again the same way.

## What the agent can do with CodeAlta

The tools of CodeAlta itself run outside the container, so a sandboxed agent uses only the ones that keep it inside:

- It reaches itself and the sessions it creates that run in its sandbox, never your other sessions nor another project. The sessions it creates are sandboxed as it is.
- It uses notes, plans, tasks, reviews, reminders, background jobs and questions to you as usual.
- It does not open terminals, drive the window, build plugins, run automations, add MCP servers, or change prompts, skills, spaces and projects.
- It uses the plugin tools made for a sandbox, and the MCP servers of its profile.

An agent can turn a sandbox on for a session it creates, never turn one off.

### On other machines

Another machine is never a way out of the sandbox. Where its profile allows **Other machines**, a sandboxed agent:

- starts a session elsewhere only in the project of its own repository, in a profile of that machine at least as strict as its own: same network or less, no host, folder, port, variable or MCP server more. The machine refuses it otherwise, even when its profile of the same name gives more;
- sends prompts only to the sessions it started there, which must run as sandboxed as itself and in a permission mode that asks no less;
- does not move sessions, nor run automations.

A session moved to another machine keeps its sandbox and its permission mode there; the machine refuses it when its profile of that name gives more.

## On a machine of the fleet

A [machine of the fleet](../fleet/readme.md) runs its sessions in the sandbox by default, where it can run, with the profile `node`: `workspace` and the hosts of GitHub, GitLab, Bitbucket and Azure DevOps. Its agents fetch, push and open pull requests without leaving the sandbox, and the machine needs nobody to allow them.

- Git reaches these hosts over HTTPS: a remote over SSH (`git@github.com:…`) does not go through the sandbox. To push, give the sessions a token: a variable of the environment of the node, such as `GH_TOKEN`, that the profile names in `env`:

  ```toml
  [sandbox.profiles.node]
  extends = "node"
  hosts = ["git.example.com"]
  env = ["GH_TOKEN"]
  ```

  With `extends = "node"`, the hosts and the variables are added to those of the profile; without it, each key it sets replaces the one of the profile.
- `all_sessions = "off"` in the `[sandbox]` of the machine runs its sessions without a sandbox, and `all_sessions = "strict"` in a stricter one.
- Where the sandbox cannot run, the sessions run without one: the page of the machine and `altanode status` say why, and what to install.

## Providers

A sandboxed session runs on the providers whose model CodeAlta drives with its own tools (the API providers, Codex, GitHub Copilot), on [Claude Code](claude-code.md), on [Codex CLI](../model-providers/setup.md#codex-cli) and on the ACP agents:

- **Claude Code** runs in the sandbox of the session. It writes in its account folder (its sign-in and its transcripts), so the agent can read its own sign-in, never the ones of CodeAlta. It reaches the hosts of Anthropic, even with a profile without network. On Windows its commands run with the shell of CodeAlta, in the same sandbox.
- **On macOS**, Claude Code keeps its sign-in in the Keychain, outside the sandbox: give it a token of its own: make one with `claude setup-token` in a terminal, and add `CLAUDE_CODE_OAUTH_TOKEN=<token>` to the **Environment** of the provider (Settings > Providers). Its variables reach the sandbox with their values.
- **Codex CLI** runs in the sandbox of the session, with its own sandbox off. It writes in the Codex folder of its provider (its sign-in and its threads), not in its settings, and reaches the hosts of OpenAI, even with a profile without network. It is signed in there as outside.
- **The ACP agents** (Copilot CLI, Cursor, Grok Build and the others) run in the sandbox of the session, in a home folder of their own that each provider keeps between sessions. They are not signed in there at first: give the provider a token or an API key in its **Environment** (Settings > Providers), such as `COPILOT_GITHUB_TOKEN`, `CURSOR_API_KEY` or `XAI_API_KEY`. Its variables reach the sandbox with their values.
- **An ACP agent CodeAlta does not know** reaches the hosts its provider lists in **Hosts in a sandbox**, besides those of the profile.

A [fallback](../model-providers/readme.md) never moves a sandboxed session to a provider it cannot run on.

## Platforms

The sandbox is built on [MXC](https://github.com/microsoft/mxc), Microsoft's policy-driven isolation and containment of processes, with the native sandbox of each system: the process container of Windows, Seatbelt on macOS and Bubblewrap on Linux. CodeAlta never falls back to a weaker sandbox of its own.

- **Windows 11**: built in, nothing to install. PowerShell 7 must be the installation of its MSI, not its Microsoft Store package, which no sandbox can start: winget installs that package by default, so install it with `winget install --id Microsoft.PowerShell --exact --installer-type wix` (see [troubleshooting](../troubleshooting.md#a-sandboxed-session-runs-nothing)). MSBuild builds there with one node (`-m:1`). Before build .9550, the tests of VSTest do not run with a profile that reaches hosts, such as `workspace` (see [troubleshooting](../troubleshooting.md#a-sandboxed-session-runs-nothing)). A Windows that can only let a sandbox list the root of a disk keeps it from creating folders there; on one that cannot, such as 25H2 at build 26200.9457, a sandbox can create a folder at the root of a disk and write in it (`C:\.git`), and programs that look in the parent folders of a project may read that folder. The files and folders already on the disk stay out of its reach.
- **macOS**: built in, on Apple silicon. The files that run programs later cannot be replaced, but a sandboxed command can still move a folder that holds them, such as `.git`, and change them there. The commands of Xcode or of its Command Line Tools (`git`, `python3`) and of Homebrew run there. MSBuild builds with one node (`-m:1`), and `dotnet test` does not run: no process of a sandbox listens on a port. A test project of Microsoft.Testing.Platform runs as a program: `dotnet build <tests> -m:1`, then `dotnet run --project <tests> --no-build` (see [troubleshooting](../troubleshooting.md#a-sandboxed-session-runs-nothing)).
- **Linux**: install `bubblewrap`, and `slirp4netns` for a network. On Ubuntu 24.04 and later, `bwrap` and `unshare` also need an AppArmor profile that allows user namespaces (see [troubleshooting](../troubleshooting.md#a-sandboxed-session-runs-nothing)).

On macOS and Linux, a sandbox with a network reaches it through a proxy of CodeAlta, which programs find in `HTTPS_PROXY`: a program that ignores that variable reaches no host. With a [manual proxy](../workspace/settings.md#network-proxy), that proxy of CodeAlta, and the `webget` of the session, reach the hosts the sandbox may reach through yours, at the addresses they checked, and sign in when your proxy asks (Negotiate, NTLM or Basic); the names must still resolve on this computer, and a plain `http://` request goes in a tunnel too (`CONNECT`), which a proxy that allows it on port 443 only refuses.

**Check** says what is missing. Where the sandbox cannot run, a sandboxed session runs no command at all: it never runs them without the sandbox.

## On the command line

```text
alta session create --project Lib --sandbox strict --title "Review PR #42"
alta session send <session-id> --sandbox workspace --stdin
altacli run --sandbox workspace "Fix the failing test"
```

`alta session choices`, `info`, `status` and `list` show the sandbox of a session. `alta sandbox` tells what the profiles allow, for you and for your agents:

```text
alta sandbox list                     the profiles, their network and folders
alta sandbox show workspace           what a profile allows: hosts, folders, ports, variables, MCP servers
alta sandbox status --session <id>    the sandbox of a session, whether it runs here, the hosts it refused last
```

A sandboxed agent reads its own profile and the sandbox of its own sessions only. See [command line](../command-line.md) for `altacli`.
