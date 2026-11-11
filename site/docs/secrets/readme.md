---
title: Secrets and Variables
---

# Secrets and Variables

A secret is a named value that CodeAlta uses but never shows again: an API key, a token for Grafana, Jira or a registry. A variable is a named value that is not secret, such as an address. Both belong to the computer: CodeAlta Desktop, CodeAlta TUI, `altacli` and `altanode` on the same profile share them.

The configuration names them instead of holding them: `${secret:NAME}` and `${var:NAME}`, wherever `${env:NAME}` works, and `api_key_secret` for the key of a provider.

The value of a secret is kept on this computer, or in an item of a vault such as 1Password, Bitwarden or KeePassXC: see [Vaults](vaults.md). The configuration names the secret the same way in both cases.

## Settings > Secrets

In CodeAlta Desktop, open **Settings > Secrets**.

- **Credential providers** lists where values are kept: **This computer** first, then each [vault](vaults.md) you added, with its logo, its account and the action its state needs (**Unlock**, **Sign in**, how to install its program).
- **Secrets** lists each secret by name, with the reference to write in the configuration and where its value is: **This computer** with its version, who set it and when, or the vault and the item it is kept in, with the state of that vault. A value is never shown.
- **Add a secret…** takes a name and a value. A name is letters, digits and `_`, not starting with a digit, like an environment variable.
- **Rotate** gives a secret of this computer a new value and a new version.
- **Keep in a vault…** moves a secret to an item of a vault (see [Keep a secret in a vault](vaults.md#keep-a-secret-in-a-vault)).
- The trash button deletes a secret, after a confirmation beside its row. What refers to it stops working until it is set again. A secret kept in a vault leaves the item as it is.
- **Variables** shows each variable with its value: change it and **Save**, or add one at the bottom.

## Keep the key of a provider as a secret

In **Settings > Providers**, a provider whose key is in the configuration file, or in the environment variable `api_key_env` names, offers **Keep as a secret…** under its key. Choose a name (`OPENAI_API_KEY` for `openai`) and **Keep**: the key moves into a secret of this computer, and the configuration file names it with `api_key_secret` instead of holding it.

**Use a vault item…** beside it takes the key from an item of a vault instead: choose the vault and the item, and the configuration names the secret the same way.

A provider whose key is a secret says so there, and warns when that secret is not set on this computer.

## In the configuration

{.table}
| Where | What to write |
|---|---|
| A provider (`config.toml`) | `api_key_secret = "OPENAI_API_KEY"`, or `api_key = "${secret:OPENAI_API_KEY}"` |
| The `env` of a `claude-code` provider | `CLAUDE_CODE_OAUTH_TOKEN = "${secret:CLAUDE_TOKEN}"` |
| The `env` and headers of an [MCP server](../plugins/mcp.md#servers-of-other-tools) | `"Authorization": "Bearer ${secret:GRAFANA_TOKEN}"`, `"GRAFANA_URL": "${var:GRAFANA_URL}"` |
| The [command line tools of a space](../spaces.md#command-line-tools) | `VAULT_TOKEN` = `${secret:CONTOSO_VAULT_TOKEN}` |

A provider takes its key from `api_key` first, then from the secret `api_key_secret` names, then from the variable `api_key_env` names. A secret or a variable that is named but not set is an error that names it: the provider is left out (the log says which secret is missing), and an MCP server does not start (its diagnostic names it). CodeAlta never uses an empty value instead.

A secret kept in a locked vault is the same kind of error, and names the vault: unlock it in **Settings > Secrets**, and the providers and MCP servers that need it start.

## Ask before use

Turn on **Ask before use** on a secret, kept on this computer or in a vault, and CodeAlta gives its value only after the system checked that you are there: Windows Hello (PIN, fingerprint, face) on Windows, Touch ID or the password of your account on macOS. The prompt names the secret and what asks for it, such as `the provider anthropic` or `the MCP server grafana`.

- Your answer is kept for that secret and what uses it, for the time you choose: each use, 15 minutes, 1 hour, or until CodeAlta closes. Something else that asks for the same secret asks you again.
- Where nobody can answer, such as an automation at night, `altacli`, `altanode` or the developer instance, the secret is not given, and what needs it says that it waits for your confirmation. When the window is open, **Confirm** at the top of **Settings > Secrets** asks you again.
- **Recent uses**, at the bottom of the page, lists which secret was asked for, by what, when, and whether it was given. `alta secret uses` gives agents the same list.
- Where the system cannot check you (Windows Hello not set up, Linux), the switch is off and says why.

> [!NOTE]
> Ask before use is a check CodeAlta makes before it gives a value. A program that runs as you, such as a command of a session without the [sandbox](../sessions/sandbox.md), can still read what you can read.

## Masking

The value of every secret of the computer is replaced by `***` before it is written or shown: in what tools return to the model, in session histories and their files, in what the window shows, in telemetry, and in the logs of CodeAlta Desktop and `altanode`. A secret a tool prints, or that an agent repeats, therefore never lands in a session file. A value read from a vault is masked from its first read. Values shorter than four characters are not masked.

## Agents

Agents list the names with `alta secret list`, to write `${secret:NAME}` in a configuration, with where each value is kept and whether it can be given now (`alta secret test NAME`). They never read a value, and `alta secret set` is refused: ask the user to set it. What agents can do with vaults is in [Agents and vaults](vaults.md#agents-and-vaults).

A [sandboxed](../sessions/sandbox.md) session gets none of the `alta secret` and `alta credential` commands.

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

### Environments

An environment is a set of values with rules, such as `production`. Under **Environments**, give it a name, the tags of the machines it runs on (none for all your machines) and the people who approve its runs (none for no approval). Then set values **Where it applies** is that environment.

An automation uses an environment with `environment = "production"` in its table. Its run starts only on a machine with one of the tags, and, when the environment needs an approval, waits for it: **Runs that wait for you** lists it at the top of the page, under the name your computer verified, and **Approve…** confirms it beside its row; a person you name as approver finds it on their own page. An approval is for that run only. A name two environments share, such as two people's `production`, starts no run. Its session keeps the values of the environment when the machine restarts, until the rules of the environment change. The tags of a machine are set on its page in **Settings > Fleet > Machines**, or with `altanode tags` on it (see [Tag your machines](../fleet/machines.md#tag-your-machines)).

## Where values are kept

Secrets of this computer live in the `secrets` folder of the profile (`~/.alta/secrets`): the names, versions and dates in `store.json`, the values encrypted with a key of the user, in the protected store of the system: Windows data protection (DPAPI), the macOS Keychain, or the keyring of the Linux desktop (libsecret). Where none is available, such as a Linux server without a desktop session, the key is in a file only the user can read. A store made with one of them is opened with the same one later.

A secret kept in a vault has no value there: `bindings.json`, beside `store.json`, names the vault and the item of each one, and which secrets ask before use. The values CodeAlta read from a vault stay in its memory only.
