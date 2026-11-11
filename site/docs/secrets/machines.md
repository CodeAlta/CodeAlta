---
title: Secrets of your machines
---

# Secrets of your machines

The secrets and variables of a machine that runs CodeAlta Node are its own, as those of this computer are (see [Secrets and Variables](readme.md)). They are set on the machine with `altanode secrets`, from a CodeAlta Desktop paired with it, or for all your machines at once through a CodeAlta Server.

## On a machine with altanode

`altanode secrets` manages the same store from a terminal, on the machine of the node:

```sh
altanode secrets                          # the secrets by name, and the variables
echo "$TOKEN" | altanode secrets set GRAFANA_TOKEN
altanode secrets set GRAFANA_TOKEN        # typed at a prompt that does not show it
altanode secrets rotate GRAFANA_TOKEN
altanode secrets delete GRAFANA_TOKEN
altanode secrets set-var GRAFANA_URL https://grafana.example
altanode secrets delete-var GRAFANA_URL
```

The value of a secret comes from the standard input or the prompt, never from an argument, which other processes of the machine can read. A node that runs uses a new value at its next use.

From CodeAlta Desktop, a computer paired with the machine that manages it changes the same secrets and variables in the **Secrets** section of the page of the machine (see [Manage a machine from CodeAlta Desktop](../fleet/machines.md#manage-a-machine-from-codealta-desktop)). A computer that reaches the machine only through a CodeAlta Server never does.

## Secrets of your machines with a server

With a [CodeAlta Server](../fleet/server.md), the computer that made it sets the secrets and variables of all your machines in one place: open **Settings > Fleet > Secrets and environments**. Without a server, that page sends you to **Settings > Secrets**, where the values of this computer are.

- **Add a value…** takes a secret or a variable, its name and value, where it applies, and the machines that get it.
- **Where it applies**: **All my work**, a space linked to the server, a project, or an [environment](#environments). A run takes the value of the narrowest: its environment, then its project, its space, then all your work. A secret of the machine itself (**Settings > Secrets** on it, `altanode secrets`) comes first.
- **Only for MCP servers and plugin tools, never the agent's shell**: the secret goes to providers, MCP servers and plugin tools, never to the agent's shell nor to the commands it runs. Its row says **MCP servers and plugin tools only**. A command line profile of a space that refers to it does not get it.
- Each row shows where the value applies, its version and the machines that hold it. **Rotate** gives it a new value: the machines take it at once when they are online, else before their next run. **Machines** gives it to another machine of yours, or takes it away: the machines that keep it then get it as a new version, which the machine taken away never gets. A variable shows its value.
- A machine removed from the server keeps nothing, and the rows of the values it held say **Rotate: a revoked machine held it** until they are rotated.

Your computer seals each value for the machines you choose and for your other computers: the server keeps only sealed copies, and never sees a value. A machine keeps its copies in its protected store, masks them as its own secrets, and the server records which run used which version of a value, never the value.

## Environments

An environment is a set of values with rules, such as `production`. Under **Environments**, give it a name, the tags of the machines it runs on (none for all your machines) and the people who approve its runs (none for no approval). Then set values **Where it applies** is that environment.

An automation uses an environment with `environment = "production"` in its table. Its run starts only on a machine with one of the tags, and, when the environment needs an approval, waits for it: **Runs that wait for you** lists it at the top of the page, under the name your computer verified, and **Approve…** confirms it beside its row; a person you name as approver finds it on their own page. An approval is for that run only. A name two environments share, such as two people's `production`, starts no run. Its session keeps the values of the environment when the machine restarts, until the rules of the environment change. The tags of a machine are set on its page in **Settings > Fleet > Machines**, or with `altanode tags` on it (see [Tag your machines](../fleet/machines.md#tag-your-machines)).
