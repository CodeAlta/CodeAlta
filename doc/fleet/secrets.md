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
- **Scopes**, the narrowest winning: the run's environment, then the project, the space, the team, the organization.
  A machine's own store comes first for the work of its owner; an organization's policy can require the managed
  value on its machines.
- **Write-only**: the desktop and the REST API set a secret, rotate it and delete it; they list its name, scope,
  version, who set it and when, and which machines hold it, never its value.

## Where values live

- **On every host**, a local store under the root of the profile (`~/.alta/secrets`, shared by its developer instance
  as the configuration that names the secrets is): names and metadata in a file, values encrypted per value by DPAPI on
  Windows, or with a key the OS protected store keeps (the Keychain on macOS, libsecret on Linux, a file only its user
  can read where none is available). It is what the desktop's **Settings > Secrets** page, `altanode secrets` and
  `alta secret` manage, on that machine and on a paired machine its owner manages from the desktop. Resolution asks a
  chain of sources, the narrowest scope first: the machine's store is the last of them, and the only one until the
  fleet distributes values.
- **In a fleet**, values are sealed end to end: the desktop of the person who sets a secret encrypts it to the keys of
  the machines allowed to use it (taken from the signed device list, as metadata keys are) and to the devices of the
  people who manage it. The server stores and relays only these sealed copies and their metadata: a server, or a
  backup of it, never holds a value.
- **A machine allowed later** gets its copy when a person who manages the secret approves it ("give `GRAFANA_TOKEN` to
  `build-mac`"): their device seals it, as it approves a new device.

## How a run gets them

- A session, an automation or a tool declares the names it uses (`secrets = ["GRAFANA_TOKEN"]`, or a provider, an MCP
  server or a tool bound to a secret); nothing else reaches it. An automation names its environment.
- **Host-only secrets** go to host-side tools (MCP servers, plugin tools) and never to the agent's shell, which is how
  an unattended run reaches infrastructure without the agent holding its credentials
  ([orchestration](orchestration.md#what-unattended-runs-support)).
- **Masking**: a secret's value is replaced by `***` in tool outputs, journals, logs and telemetry before they are
  written; a value is never sent to the server, never published in facts, never moved with a session.
- **Environments protect themselves**: an environment can require the approval of named people or teams before a run
  that uses it starts, limit it to machines with labels, to automations, or to branches. The approval is a person's
  device signature, as for every remote action.

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
