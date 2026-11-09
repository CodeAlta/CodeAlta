# Secrets, variables and environments

API keys of providers are one kind of secret among many: a node also needs tokens for Grafana, Jira, a registry, a
cloud CLI, an MCP server, and the host where it runs does not always offer them as environment variables. The fleet
handles all of them the same way, as GitHub Actions handles the secrets, variables and environments of a repository:
named values, scoped, write-only, given to a run only when it asks for them, and readable by the machines that use them,
never by the server.

## What exists today

- `~/.alta/config.toml` names the variable that holds a provider's key (`api_key_env`), or holds the key itself
  (`api_key`).
- Space settings refer to the environment with `${env:NAME}`.
- So a value lives in the environment of the process that starts CodeAlta, or in a configuration file in clear. A
  service (a node at logon, a LaunchAgent, a systemd unit) starts with almost no environment, and nothing names,
  scopes, rotates or audits these values.

## The model

| Kind | What it is | Example |
| --- | --- | --- |
| **Secret** | A named value that is written and used, never read back by a person or the API | `ANTHROPIC_API_KEY`, `GRAFANA_TOKEN` |
| **Variable** | A named value that is not secret, readable and editable | `GRAFANA_URL`, `AWS_REGION` |
| **Environment** | A named set of secrets and variables with protection rules | `production`, `staging` |

- **References**: wherever a value of CodeAlta can come from the environment, it can come from the store, by name:
  `${secret:NAME}` and `${var:NAME}` beside `${env:NAME}`, and `api_key_secret = "NAME"` beside `api_key_env`. A
  configuration file never holds a value again.
- **Scopes**, the narrowest winning: the run's environment, then the project, the space, the person, the team, the
  organization. A machine's own store comes first for the work of its owner; the work of another person there (a
  member of a team on a team machine), and a run nobody attends that is not positively the owner's (an automation of a
  team or of the organization, or one another person gave), recorded before its session attaches, and every session
  such a session starts there (its principal is inherited when it attaches), gets none of the owner's values. What is not
  recorded never becomes the owner's work: a record is kept while its session is on the machine, a session whose record
  cannot be written does not attach, and records that do not read make no session the owner's; only those of the environment it was admitted to; an organization's policy can require the managed
  value on its machines.
- **Write-only**: the desktop and the REST API set a secret, rotate it and delete it; they list its name, scope,
  version, who set it and when, and which machines hold it, never its value.

## Where values live

- **On every host**, a local store under the root of the profile (`~/.alta/secrets`, shared by its developer instance
  as the configuration that names the secrets is): names and metadata in a file, values encrypted per value by DPAPI on
  Windows, or with a key the OS protected store keeps (the Keychain on macOS, libsecret on Linux, a file only its user
  can read where none is available). It is what the desktop's **Settings > Secrets** page, `altanode secrets` and
  `alta secret` manage, on that machine and on a paired machine its owner manages from the desktop (the node API's
  `control.secrets.*`: the owner's local control, and a device the node trusts by its own pairing with the `manage`
  permission; never a device trusted only through an organization's device list). Resolution asks a
  chain of sources, the narrowest scope first: the machine's store is the last of them, and the only one until the
  fleet distributes values.
- **In a fleet**, values are sealed end to end: the desktop of the person who sets a secret encrypts it to the keys of
  the machines allowed to use it (taken from the signed device list, as metadata keys are) and to the devices of the
  people who manage it. The server stores and relays only these sealed copies and their metadata: a server, or a
  backup of it, never holds a value.
- **A machine allowed later** gets its copy when a person who manages the secret approves it ("give `GRAFANA_TOKEN` to
  `build-mac`"): their device seals it, as it approves a new device.
- **Each version has a key of its own**, wrapped to each device with a grant the sealing device signs (as metadata keys
  are), and the value is bound to its id, kind, name, scope, version and whether it is host-only: a server can neither
  seal a copy a machine takes, nor move a value to another name, scope or version, nor make a host-only value
  shell-visible. Names, scopes and holders are what the server reads; a space is the opaque id of its metadata key and a
  project the blind index of its repository, so the server does not learn them.
- **The server nudges** the machines concerned when a value, an environment or an approval of theirs changes; a machine
  also reads its copies before each run, and drops those of a server that revoked it.
- **No rollback**: a machine keeps, per organization and across restarts, the newest version it took of each environment
  (with whether it was taken away) and of each value, and the version of each value it saw taken away (deleted, or
  withdrawn from it), and refuses an older one, another definition of the same version, or a value taken away at that
  version, so that a server replaying what it held before loosens no rule and brings back no superseded, deleted or
  withdrawn value. An answer that names one twice gives its newest version; what the checkpoint cannot record (it is
  full) is not taken, and an environment is used only once its version is recorded.
- **Rotation reaches only the machines a controller gave the value to**: a controller device seals the next version for
  the holders whose copy carries a grant a controller device signed, never for a holder the server lists by itself. A
  withdrawal seals the value again as its next version for the machines that keep it, so that the grant of the machine
  withdrawn is of an older version, which no rotation takes. The controller device keeps, with its link, the newest version
  and key it saw of each value and the machines it withdrew: a record the server answers older than that seals nothing, and
  no machine it withdrew is given a version again unless it gives the value back.

## How a run gets them

- A session, an automation or a tool declares the names it uses (`secrets = ["GRAFANA_TOKEN"]`, or a provider, an MCP
  server or a tool bound to a secret); nothing else reaches it. An automation names its environment.
- **Host-only secrets** go to host-side tools (MCP servers, plugin tools) and never to the agent's shell, which is how
  an unattended run reaches infrastructure without the agent holding its credentials
  ([orchestration](orchestration.md#what-unattended-runs-support)).
- **Masking**: a secret's value is replaced by `***` in tool outputs, journals, logs and telemetry before they are
  written; a value is never sent to the server, never published in facts, never moved with a session. A value taken
  away (rotated, deleted, withdrawn, or of a link that is gone) is never given again but stays masked: the store keeps it
  for 30 days (256 values at most), and each process masks every value it read or gave (from the moment it gives it, before
  the session takes it) for as long as it runs, since a run that took it may still print it.
- **Environments protect themselves**: an environment can require the approval of named people or teams before a run
  that uses it starts, limit it to machines with tags, to automations, or to branches. Its rules are signed by a
  controller device of its person, so that a server cannot drop one. The approval is a person's device signature, as
  for every remote action, bound to the environment and the version of its rules, the machine, the run and a nonce the
  machine chose, valid for a while; the machine verifies it against its anchored device list and takes it once. The
  nonce carries the machine's signature of what it asks (environment, version, run, automation), and the approver's
  device signs only a request that verifies and is the one the person reviewed, so a server can neither make up nor
  change the run approved. What the person reviews is named by the definition their device verified at the version the
  machine asked, never by the name the server lists, and its machine by the words of its key in the verified device list
  beside the name the server gives it; an approver of another person verifies the signed definition the
  server gives with the approval, which gives them neither its values nor its management. The approval and a budget
  hold name the run, never its title. The values of an environment are its own, kept under the organization and the id
  its signed definition names; a run names an environment by its name, and a name two environments share admits no run.
  The machine keeps the admission of the session it started, bound to the environment, the version of its rules and the
  approval taken: a restart keeps it, and new rules end it.

## Logins are not secrets of the store

A person's logins stay on their machine and never travel: subscriptions signed in by OAuth (Claude, Codex, Copilot),
SSH keys, Git credential helpers, cloud CLI sessions. The fleet helps each machine sign in by itself
([node](node.md#credentials-on-a-node)). The store holds what a person deliberately puts in it: keys, tokens, and
values for unattended work. Organization providers ([orchestration](orchestration.md#credentials-of-unattended-runs))
are providers whose key is a secret of the organization.

## Audit and rotation

- Setting, rotating, deleting, granting a machine and approving an environment are audited at the server.
- A machine records each use (which run, which secret, which version, never the value) and publishes it as a fact.
- Rotation writes a new version; machines take it at their next run and drop the old one; a revoked machine's copies
  are refused and the secrets it held are flagged for rotation.

## In the desktop

**Settings > Secrets** for this machine and the machines its owner manages; with a server, the **Providers and
secrets** canvas for the secrets, variables and environments of each scope, with who may use them, the machines that
hold them, the approvals that wait, and **Rotate**. Each row says where its value comes from (this machine, a space, the
organization) and what uses it.
