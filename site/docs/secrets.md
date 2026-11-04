---
title: Secrets and Variables
---

# Secrets and Variables

A secret is a named value that CodeAlta uses but never shows again: an API key, a token for Grafana, Jira or a registry. A variable is a named value that is not secret, such as an address. Both belong to the computer: CodeAlta Desktop, CodeAlta TUI, `altacli` and `altanode` on the same profile share them.

The configuration names them instead of holding them: `${secret:NAME}` and `${var:NAME}`, wherever `${env:NAME}` works, and `api_key_secret` for the key of a provider.

## Settings > Secrets

In CodeAlta Desktop, open **Settings > Secrets**.

- **Secrets** lists each secret by name, with the reference to write in the configuration, its version, and who set it and when. A value is never shown.
- **Add a secret…** takes a name and a value. A name is letters, digits and `_`, not starting with a digit, like an environment variable.
- **Rotate** gives a secret a new value and a new version.
- The trash button deletes a secret, after a confirmation beside its row. What refers to it stops working until it is set again.
- **Variables** shows each variable with its value: change it and **Save**, or add one at the bottom.

## Keep the key of a provider as a secret

In **Settings > Providers**, a provider whose key is in the configuration file, or in the environment variable `api_key_env` names, offers **Keep as a secret…** under its key. Choose a name (`OPENAI_API_KEY` for `openai`) and **Keep**: the key moves into a secret of this computer, and the configuration file names it with `api_key_secret` instead of holding it.

A provider whose key is a secret says so there, and warns when that secret is not set on this computer.

## In the configuration

{.table}
| Where | What to write |
|---|---|
| A provider (`config.toml`) | `api_key_secret = "OPENAI_API_KEY"`, or `api_key = "${secret:OPENAI_API_KEY}"` |
| The `env` of a `claude-code` provider | `CLAUDE_CODE_OAUTH_TOKEN = "${secret:CLAUDE_TOKEN}"` |
| The `env` and headers of an [MCP server](plugins/mcp.md#servers-of-other-tools) | `"Authorization": "Bearer ${secret:GRAFANA_TOKEN}"`, `"GRAFANA_URL": "${var:GRAFANA_URL}"` |
| The [command line tools of a space](spaces.md#command-line-tools) | `VAULT_TOKEN` = `${secret:CONTOSO_VAULT_TOKEN}` |

A provider takes its key from `api_key` first, then from the secret `api_key_secret` names, then from the variable `api_key_env` names. A secret or a variable that is named but not set is an error that names it: the provider is left out (the log says which secret is missing), and an MCP server does not start (its diagnostic names it). CodeAlta never uses an empty value instead.

## Masking

The value of every secret of the computer is replaced by `***` before it is written or shown: in what tools return to the model, in session histories and their files, in what the window shows, in telemetry, and in the logs of CodeAlta Desktop and `altanode`. A secret a tool prints, or that an agent repeats, therefore never lands in a session file. Values shorter than four characters are not masked.

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

From CodeAlta Desktop, a computer paired with the machine that manages it changes the same secrets and variables in the **Secrets** section of the page of the machine (see [Manage a machine from CodeAlta Desktop](fleet.md#manage-a-machine-from-codealta-desktop)). A computer that reaches the machine only through a CodeAlta Server never does.

## Secrets of your machines with a server

With a [CodeAlta Server](fleet.md#connect-to-a-codealta-server), the computer that made it sets the secrets and variables of all your machines in one place: open **Settings > Fleet > Secrets and environments**.

- **Add a value…** takes a secret or a variable, its name and value, where it applies, and the machines that get it.
- **Where it applies**: **All my work**, a space linked to the server, a project, or an [environment](#environments). A run takes the value of the narrowest: its environment, then its project, its space, then all your work. A secret of the machine itself (**Settings > Secrets** on it, `altanode secrets`) comes first.
- **Only for MCP servers and plugin tools, never the agent's shell**: the secret goes to providers, MCP servers and plugin tools, never to the agent's shell nor to the commands it runs. Its row says **MCP servers and plugin tools only**. A command line profile of a space that refers to it does not get it.
- Each row shows where the value applies, its version and the machines that hold it. **Rotate** gives it a new value: the machines take it at once when they are online, else before their next run. **Machines** gives it to another machine of yours, or takes it away. A variable shows its value.
- A machine removed from the server keeps nothing, and the rows of the values it held say **Rotate: a revoked machine held it** until they are rotated.

Your computer seals each value for the machines you choose and for your other computers: the server keeps only sealed copies, and never sees a value. A machine keeps its copies in its protected store, masks them as its own secrets, and the server records which run used which version of a value, never the value.

### Environments

An environment is a set of values with rules, such as `production`. Under **Environments**, give it a name, the labels of the machines it runs on (none for all your machines) and the people who approve its runs (none for no approval). Then set values **Where it applies** is that environment.

An automation uses an environment with `environment = "production"` in its table. Its run starts only on a machine with one of the labels, and, when the environment needs an approval, waits for it: **Runs that wait for you** lists it at the top of the page, and **Approve…** confirms it beside its row. An approval is for that run only. Its session keeps the values of the environment when the machine restarts, until the rules of the environment change. The labels of a machine are set on its page in **Settings > Fleet > Machines**, or with `altanode labels` on it (see [Label your machines](fleet.md#label-your-machines)).

## Agents

Agents list the names with `alta secret list`, to write `${secret:NAME}` in a configuration. They never read a value, and `alta secret set` is refused: ask the user to set it.

## Where values are kept

Secrets live in the `secrets` folder of the profile (`~/.alta/secrets`): the names, versions and dates in `store.json`, the values encrypted with a key of the user, in the protected store of the system: Windows data protection (DPAPI), the macOS Keychain, or the keyring of the Linux desktop (libsecret). Where none is available, such as a Linux server without a desktop session, the key is in a file only the user can read. A store made with one of them is opened with the same one later.
