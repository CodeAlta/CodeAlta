# Sandbox

A sandboxed session runs what it executes and reads on the computer in an OS container, so that a prompt injected
into it cannot read CodeAlta's credentials or the user's secrets, change files outside its folders, or plant what
runs later outside the sandbox. It is optional: a session that is not sandboxed works as it always did.

## The boundary

The agent loop, the journal and the provider credentials stay in CodeAlta. What the session runs and reads on the
computer goes to a worker inside a container built from the session's policy: the commands, the file tools and the
background jobs of the session. Their programs are children of the worker, so the container keeps them and they end
with it.

Why there, and not the whole session in a sandboxed process: the credentials would have to enter the sandbox, the
session would be owned by another process (principle 1), and the sandbox would write the journal the database
projects. Why the file tools too: a policy checked in software has to follow links, short names and streams exactly as
the system does; in the worker the system checks it.

`CodeAlta.Sandbox` knows the agent contracts and the worker frames only: the runtime reads the settings in the catalog
and gives it profiles, so that the sandbox never depends on the files it keeps out of reach.

## Profiles and the policy of a session

A profile (`SandboxProfile`, built in: `workspace`, `strict`) says what a sandbox allows beyond what every sandbox
gets. `SandboxScopes.Resolve` decides the profile of a session: the most specific scope that says something wins,
in the order session, model, provider, project, space, every session. `SandboxPolicyBuilder` turns the profile, the
folders of the session and the computer (`SandboxHost`) into a `SandboxPolicy`:

- **Read-write**: the working directory, the git folders a worktree writes to, the session's scratchpad, a private
  temporary folder, and the profile's home and caches under the sandbox data folder (`SandboxHostDiscovery.DefaultDataRoot`,
  outside `~/.alta`). Tools are pointed at them (`HOME`, `TEMP`, `NUGET_PACKAGES`...), so that nothing the sandbox
  downloads runs later in the user's own builds; NuGet reads the user's packages as a read-only fallback.
- **Read-only**: the folders of the developer tools and of CodeAlta's own program, and the session's own tool output
  (`<session>.attachments/tool-output`): CodeAlta writes there, outside the sandbox, the whole of a result too long
  for the model (`exec`, a command run outside the sandbox) and gives the agent its path. The runtime creates it before
  the sandbox starts, since a grant applies to a folder that exists then; nothing else of `~/.alta` is granted.
- **Protected**: the entries of the working tree that other programs run later (git hooks and configuration, the
  `.alta` settings and plugins, the folders of editors and coding agents). One that exists is read-only, one that
  does not is denied, which keeps it from being created.
- **Denied**: the user's secrets wherever a grant would reach them.
- **Environment**: built from scratch. Variables named like a secret pass only when the profile names them.

## The mxc backend

`MxcSandboxBackend` runs a policy with Microsoft's mxc: a ProcessContainer on Windows, Seatbelt on macOS,
Bubblewrap on Linux. Invariants:

- **Native only.** On Windows, the probe of every request must answer the BaseContainer tier without changing the
  permissions of folders; otherwise the sandbox does not start. A computer without a backend runs no commands for a
  sandboxed session, never commands without a sandbox.
- **The parents of a grant.** Tools resolve a path one folder at a time from the volume root. A read-only grant
  cascades to everything below it, so each parent folder of a read-write grant is granted read-only with its other
  entries denied. The volume root is granted only to be listed (`enumeratePaths`) where Windows reports that capability:
  read-only it opens the whole disk, and read-write it lets the sandbox create folders there and write in them, which a
  default Windows lets every user do at the root of a disk. A link to a folder the sandbox needs is not denied, since
  mxc denies what a denied link names and a Windows profile has such links (`Local Settings` names `AppData\Local`):
  a shell that walks through that folder could not reach a project below it, and Windows PowerShell would start at the
  root of the disk instead. On macOS, where every path can be looked up, nothing is granted: the other entries of the
  parents inside the home folder are denied, so that a file a build looks for above the project (`.editorconfig`) is
  not there rather than there and unreadable.
- **A limit on Windows without that capability.** Windows 11 25H2 at build 26200.9457 does not report it, and the volume
  root then stays in the read-write list, which is all that lets tools walk from it. A sandbox there cannot write files
  at the root or in the folders that exist, but it can create a folder at the root and write in that folder, such as
  `C:\.git`, which programs that look in the parent folders of a project may then read.
- **What tools need beside `PATH` on macOS.** The folders a Homebrew prefix links its commands to (not its `var`),
  the developer folder whose `xcrun` shims `/usr/bin/git` and `python3` load, and the Mach services of the names of
  users and of the check of certificates (.NET's TLS). mxc's `keychainAccess` is never set: the keychains stay out of
  reach. MSBuild and Roslyn put their pipes in `/tmp`, which the sandbox does not write, so a build needs one node.
- **No telemetry.** No request enables it.
- **The UI stays enabled** (a disabled one stops tools at start); the clipboard stays closed.

## The worker and the routing

The worker is the executable of the frontend started with `--sandbox-worker` (`alta`, `altatui` or `altacli`): it
speaks framed messages over its standard streams (`SandboxWorkerProtocol`) and runs the handlers of
`AgentBuiltInToolFactory` unchanged, with the session's folder and guards. What a tool asks while it runs (a
permission, the command guard, its progress) comes back to CodeAlta, which answers it as for a tool that runs here.

- **Chosen at each call.** `AgentSession` gives its built-in tools to an `IAgentBuiltInToolRouter`. The router of the
  runtime reads the sandbox of the session at each call: a session that becomes sandboxed during a turn runs its next
  call in the worker. `webget` runs in CodeAlta, on an `HttpClient` that follows the network of the sandbox.
- **One worker per session** (`SessionSandboxWorkers`), started on the first call, keyed by its policy: a profile that
  changes starts another container. It stops when the session's attachment goes, unless a background command holds it,
  and after ten idle minutes.
- **A run that cannot be held is refused.** `RefuseRunAsync` refuses the run of a sandboxed session whose profile
  cannot be used or whose sandbox is not available here, and the run of a provider that runs its own tools
  (`IAgentProviderToolHost`) unless the sandbox starts its process. The fallback of a tier skips the others.
- **The process of an agent.** Claude Code runs its tools in its CLI: the router gives the turn an
  `IAgentProcessHost`, and the CLI starts in a container of its own with the profile of the session, its account folder
  read-write, the folder of its executable read-only and the hosts of Anthropic. Everything it calls back is in-band
  on its standard streams, so it needs no port of CodeAlta. The key of the host is part of its launch key: another
  sandbox starts another process, which resumes the conversation. On Windows the shells of the CLI do not run in a
  container (msys2 needs a named object it refuses), so they are turned off and the CLI is offered `shell_command`,
  which runs in the worker of the session.
- **The process of Codex CLI** starts the same way, with Codex's own sandbox off: the CODEX_HOME of its provider
  read-write except the files that configure it (`config.toml`, hooks, rules, skills, plugins), the package of its
  executable read-only, the hosts of OpenAI, and the port of the harness MCP endpoint among the local ports of its
  proxy. Its commands and edits are children of its app-server, so they stay in the container.
- **The process of an ACP agent** starts the same way, for every agent alike: a home folder of its own per provider
  (`OwnHome`, under `agents/` of the sandbox data folder), kept between sessions and reached by no other process of a
  sandbox, since the user's sign-in stays out of reach; the variables of its provider with their values, where a token
  signs it in; the hosts its provider and CodeAlta know for it. It reaches the tools of CodeAlta at the harness MCP
  endpoint, opened before it starts so that its port is among the local ports of its proxy.

## The network

`network = "off"` gives the container no network at all. `hosts` starts a `SandboxNetworkProxy` on loopback for the
worker, the only address mxc lets the container reach, and sets it as the proxy of the container. The proxy tunnels
CONNECT and forwards plain HTTP (certificate revocation checks, the MCP endpoint of an agent) to the hosts of the
profile only, one request per connection, since only the first request of a connection is read. It resolves
names itself and refuses the addresses of this computer and of its networks, CodeAlta's own ports always, except the
`local_ports` of the profile; a refusal is a 403 that names the host, and the last ones are kept for the note of a
failed command. `on` reaches any host of the internet: on macOS and Linux by the same proxy, with the same refusals,
since Seatbelt filters no address but this computer and the rules of Bubblewrap leave no route; on Windows directly,
with the private, link-local and multicast networks denied by the container and the loopback of the host closed.

## What a sandboxed session does with CodeAlta

`alta`, plugins and MCP servers run in CodeAlta with the rights of the user, outside the container. A sandboxed caller
is therefore limited where they run:

- **`alta` is an allow list** (`AltaSandboxGate`), checked once in `AltaCommandRegistry` after parsing, which also
  runs the command in the folder of the caller's session: the commands of plugins are refused unless listed, and no
  plugin takes the name of a listed command. Sessions are reached only when the caller created them, directly or
  through its children (`ResolveSessionInfoAsync`, `fork`, `--parent`), and when they run in its profile, so that a
  child the user made less sandboxed is not driven by it; jobs, reminders and the conversations found by `alta search`
  are filtered the same way, and what belongs to another project (its sessions, skills, prompts, diffs) is refused. An
  agent turns a sandbox on, never off, and a sandboxed agent chooses none: what it creates or forks has its profile.
- **Plugin tools** reach a sandboxed session only when marked `SafeInSandbox`; the tools a before-run hook adds only
  when the hook says they follow the sandbox. The MCP plugin gives the tools of the servers the profile lists
  (`SandboxMcpServers`) and refuses the others in its commands.
- **The window** (`alta ui`, its tools) is refused.
- **The browser** (`alta browser`, its tools) is allowed in the isolated profile only: its context goes through a
  proxy of the network of the profile, and its files keep to the folders of the policy (`GetSandboxReachAsync`). See
  [browser](browser.md).

## The agent's side

The agent learns what its sandbox allows from a host-context note of its run, told when it changes and after a
compaction (`SandboxNoted`), never from the instructions, whose text the provider keeps in its cache. A command that
failed because of the sandbox gets a note that says what blocked it. With a profile whose `outside = "ask"`,
`shell_command` takes `outside_sandbox`: the command is asked as an `AgentCommandPermissionRequest` marked
`OutsideSandbox`, which no permission mode answers for the user (`AlwaysAsks`) and a host without a user refuses, then
runs in CodeAlta.
