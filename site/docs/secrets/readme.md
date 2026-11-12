---
title: Secrets and Variables
---

# Secrets and Variables

A secret is a named value that CodeAlta uses but never shows again: an API key, a token for Grafana, Jira or a registry. A variable is a named value that is not secret, such as an address. Both belong to the computer: CodeAlta Desktop, CodeAlta TUI, `altacli` and `altanode` on the same profile share them.

The configuration names them instead of holding them: `${secret:NAME}` and `${var:NAME}`, wherever `${env:NAME}` works, and `api_key_secret` for the key of a provider.

The value of a secret is kept on this computer, or in an item of a vault such as 1Password, Bitwarden or KeePassXC: see [Vaults](vaults.md). The configuration names the secret the same way in both cases.

## Settings > Secrets

In CodeAlta Desktop, open **Settings > Secrets**. The page shows, from the top:

- **Secrets**: each secret by name, with the reference to write in the configuration (a click on it copies it) and where its value is: **This computer** with its version, who set it and when, or the vault and the item it is kept in, with the state of that vault. A value is never shown.
  - **Add a secret…** takes a name and a value. A name is letters, digits and `_`, not starting with a digit, like an environment variable.
  - **Add from a vault…** makes a secret of an item of a vault, whose value stays in the vault (see [Use a vault item as a secret](vaults.md#use-a-vault-item-as-a-secret)).
  - **Rotate** gives a secret of this computer a new value and a new version; the button with the logo of your vault gives it an item of the vault instead.
  - The trash button deletes a secret, after a confirmation beside its row. What refers to it stops working until it is set again. A secret kept in a vault leaves the item as it is.
- **Vaults**: where values are kept, **This computer** first, then each [vault](vaults.md) you added, with its logo, its account and the action its state needs (**Unlock**, **Sign in**, how to install its program).
- **Sign-ins of websites**, which the browser of a session types for you (see [Sign in with a vault item](../browser.md#sign-in-with-a-vault-item)).
- **Variables**: each variable with its reference and its value: change it and **Save**, or add one at the bottom.
- **Recent uses** of the secrets, once a secret was asked for. A use that [waits for your confirmation](#ask-before-use) is shown at the top of the page.

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

Turn on **Ask before use** on a secret, kept on this computer or in a vault, and CodeAlta gives its value only after the system checked that you are there: Windows Hello (PIN, fingerprint, face) on Windows, Touch ID or the password of your account on macOS. The prompt names the secret and what asks for it, such as `the provider anthropic`, `the MCP server grafana of C:\code\shop\.alta\mcp.json` or `the shell of the session 01a1…`.

- Your answer is kept for that secret and what uses it, for the time you choose: each use, 15 minutes, 1 hour, or until CodeAlta closes. Something else that asks for the same secret asks you again: another provider, an MCP server of another file or space, the shell of another session, the automations of another project.
- Where nobody can answer, such as an automation at night, `altacli`, `altanode` or the developer instance unless you [let it unlock the vaults](vaults.md#when-a-vault-is-asked), the secret is not given, and what needs it says that it waits for your confirmation. When the window is open, **Confirm** at the top of **Settings > Secrets** asks you again.
- **Recent uses**, at the bottom of the page, lists which secret was asked for, by what, when, and whether it was given. `alta secret uses` gives agents the same list.
- Where the system cannot check you (Windows Hello not set up, Linux), the switch is off and says why.

> [!NOTE]
> Ask before use is a check CodeAlta makes before it gives a value. A program that runs as you, such as a command of a session without the [sandbox](../sessions/sandbox.md), can still read what you can read.

## Masking

The value of every secret of the computer is replaced by `***` before it is written or shown: in what tools return to the model, in session histories and their files, in what the window shows, in telemetry, and in the logs of CodeAlta Desktop and `altanode`. A secret a tool prints, or that an agent repeats, therefore never lands in a session file. A value read from a vault is masked from its first read. Values shorter than four characters are not masked.

> [!WARNING]
> A secret given to the shell of a session, through the [command line tools of a space](../spaces.md#command-line-tools), can be printed by a command the model runs. CodeAlta masks the output of the tools it runs itself. An agent that runs its own tools, such as Claude Code, Codex or GitHub Copilot, gets the variables in the environment of its program: what its own shell prints goes to its model and into its own files without passing through CodeAlta. Masking catches a value shown by mistake, not a command that encodes it: give a shell only the secrets its work needs, and use the [sandbox](../sessions/sandbox.md) and [Ask before use](#ask-before-use) for the others.
>
> The key of a provider, the `env` of a `claude-code` provider, and the `env` and headers of an MCP server go to their program, never to a shell the model drives.

## Agents

Agents list the names with `alta secret list`, to write `${secret:NAME}` in a configuration, with where each value is kept and whether it can be given now (`alta secret test NAME`). They never read a value, and `alta secret set` is refused: ask the user to set it. What agents can do with vaults is in [Agents and vaults](vaults.md#agents-and-vaults).

A [sandboxed](../sessions/sandbox.md) session gets none of the `alta secret` and `alta credential` commands.

## Secrets of your machines

A machine that runs CodeAlta Node has secrets and variables of its own. Set them with `altanode secrets` on it, from the page of the machine in CodeAlta Desktop, or for all your machines through a CodeAlta Server, with environments that need an approval: see [Secrets of your machines](machines.md).

## Where values are kept

Secrets of this computer live in the `secrets` folder of the profile (`~/.alta/secrets`): the names, versions and dates in `store.json`, the values encrypted with a key of the user, in the protected store of the system: Windows data protection (DPAPI), the macOS Keychain, or the keyring of the Linux desktop (libsecret). Where none is available, such as a Linux server without a desktop session, the key is in a file only the user can read. A store made with one of them is opened with the same one later.

A secret kept in a vault has no value there: `bindings.json`, beside `store.json`, names the vault and the item of each one, and which secrets ask before use. The values CodeAlta read from a vault stay in its memory only.
