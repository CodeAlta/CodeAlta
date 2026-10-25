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
- **Read-only**: the folders of the developer tools and of CodeAlta's own program.
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
  entries denied, and the volume root, which does not cascade in the read-write list, is granted there.
- **No telemetry.** No request enables it.
- **The UI stays enabled** (a disabled one stops tools at start); the clipboard stays closed.
