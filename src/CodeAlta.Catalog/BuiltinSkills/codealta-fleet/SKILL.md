---
name: codealta-fleet
description: Use this skill to work with the CodeAlta fleet - the machines this computer is paired with or reaches through a CodeAlta Server - to pick a machine for a task (capabilities, labels, readiness, load), start or steer a session on another machine, move a session, place or run an automation on a machine, read the statistics, updates, health and devices of the fleet, and read people and access (roles, teams, grants, owners) to propose a change for a person to apply.
---

# The CodeAlta fleet

The fleet is this computer and the machines it reaches: paired machines, and the machines of an organization through its CodeAlta Server. Use the `alta` tool. Each command answers with JSON records, one per line.

`alta fleet --help` lists the commands of the fleet, and `alta fleet <command> --help` gives the options, the fields of the records and examples of each. This page explains the important ones.

## Pick a machine for a task

1. `alta fleet machine list --ready` lists the machines that take a new session from this computer now. Each record has:
   - `readiness`: `ready`, `not_allowed` (this computer may not start sessions there), `no_provider`, `updating`, or `offline`, `connecting`, `unauthorized`;
   - `working`: how many of its sessions work now, its load;
   - `os`, `capabilities` (`sandbox`, `moves`), `labels` (given by its owner, such as `gpu`), `providers`, `permissions`.
2. Choose by what the task needs first, then by load: the system (`os` is `macos` for an iOS build), a label (`--label gpu`), a capability (`--capability sandbox`), a provider (`--provider <name>`); among those, the lowest `working`, and `channel` `direct` before `relayed`. Prefer this computer when nothing requires another machine.
3. `alta fleet machine show <machine>` asks the machine for its projects and its ready providers: the project `id` and the provider `key` are what a session there names. A project whose `repository` is the one you work in is the same repository there.

A machine that is not ready is not chosen. Say why to the user when no machine fits: its owner gives a permission, configures a provider, or brings it online.

## Start and steer work elsewhere

- `alta session create --machine <machine> --project <project-id> --stdin` starts a session there, the prompt on stdin. It is your child: its final answer comes back to you, so do not poll it. Without `--project`, the machine takes its project of the same repository as yours. Name a model with `--model-ref <provider>:<model>`; the model tiers of this computer mean nothing there.
- `alta session send <session-id> --stdin` prompts it again; `alta session report <session-id>` reads where it stands and its answer.
- `alta fleet machine sessions <machine>` lists the sessions of a machine; `yours` marks your children.

## Move a session

- `alta fleet move status <session-id> --machine <machine>` says what a move would meet: `busy`, `targetStatus`, `targetCode`, what stays behind.
- `alta fleet move start <session-id> --machine <machine>` moves it with its conversation and its work in progress; add `--clone` when the machine has no project of its repository and `canClone` is true. Your own session moves when your turn ends: end your turn.

## Automations on machines

- `alta fleet automation list` says where each automation runs, and where its copy stands on its machine.
- `alta fleet automation place <automation-id> --machine <machine>` makes a disabled automation run on that machine; `--here` brings it back. A person enables it.
- `alta fleet automation run <automation-id>` runs it now where it runs; `alta fleet automation runs <automation-id>` reads its runs, those its machine reported included.

## Events of other tools

- An automation starts on Jira (a version released, an issue of a JQL query), Slack (a message or a mention, over Socket Mode) or a hook of a server (incident.io, a signed webhook) when its triggers say so (`alta automation create --help`). Their credentials are `${secret:NAME}` references to secrets of the machine that runs it: check them with `alta fleet secret list <machine>`.
- `alta fleet hook list` lists the hooks of the person on a server, with the address each gives a tool and its machine; `--events <hook-id>` says what the machine did with its latest events (`accepted`, `refused` with why, such as a signature that does not verify, `expired`). The server holds no signing secret: the machine checks each event.
- A person makes a hook, tests the connection, looks at a sample event and tries the automation in the Automations tab.

## Statistics, updates and health

- `alta fleet statistics --days 30 --group-by machine,provider` reads the sums of what the person's machines did (runs, tokens, tool calls, estimated costs) from the server.
- `alta fleet update status` reads the versions of the server and of its machines, the update policy and the recent campaigns.
- `alta fleet health` says how each server connection stands, and which machines do not take sessions now, with why.
- `alta fleet machine devices <machine>` lists the devices of a machine this computer manages and what each may do there.
- `alta fleet secret list <machine>` lists the secrets and variables of a machine this computer manages by name (without a machine, of this computer): check that a machine has what a task there names as `${secret:NAME}` before you start it.

## People and access

At a CodeAlta Server, `alta fleet access` reads who is who and who may do what, as far as the person of this computer may see:

- `alta fleet access show` says who the person is there (`role`, `teams`, `capabilities`), whether they sign the access document, and the changes that wait for a signer (`pending`): machines see those once an owner or an admin signs them.
- `alta fleet access people`, `alta fleet access teams`, `alta fleet access grants` list people (role, status, teams), teams (maintainers, members, team machines) and grants.
- `alta fleet access owners` lists machines and automations with their owners: those `paused` wait for their owner to resume them, those whose `ownerSuspended` is true were owned by someone who left and need a new owner.
- `alta fleet access transfers` lists the transfers that concern the person; `alta fleet access review` lists who can do what (for owners, admins and auditors).
- `alta fleet access propose <kind> --summary "<why>" --stdin` records a change for a person to apply (`role`, `team-member`, `grant`, `transfer`, `team`), the request as a JSON object on stdin (`alta fleet access propose --help` gives each). It changes nothing by itself: tell the user it waits for them in Settings > Fleet > People and access.

A team machine runs the sessions of its team's members, each seeing only their own, and the automations of its maintainers. When a machine refuses work with `not_allowed`, read `alta fleet access show` and `owners` to say who can help.

## Rules

- **Never more power than the person.** Every command has the access of the person of this computer, and a session you start has your permission mode and your sandbox at most.
- **The fleet is never a way out of a sandbox.** Your sandbox and your permission mode are the floor of what you start or drive on another machine; a sandboxed session reads the fleet and starts, moves or places nothing elsewhere, and one whose sandbox does not allow the fleet (`sandbox.fleetDenied`, as `strict`) does not even read it.
- **Access is read only.** Devices, permissions, roles, teams, grants, transfers, pairing and policies are changed by a person, in Settings > Fleet: propose the change with `alta fleet access propose`, do not look for another way to make it.
- **Secrets are names.** You never read, set or send the value of a secret, nor put one in a prompt: the user sets it, here or on the machine.
- **What runs unattended waits for a person.** An automation you create stays disabled, you enable none (`automation.needsPerson`), and you place a disabled one only: a person enables it; a person starts an update campaign (`alta fleet update start` is refused with `fleet.needsPerson`).
- On `fleet.needsPerson` or `fleet.notAllowed`, stop that step and tell the user what you would do and why. A Private session reaches no other machine.
