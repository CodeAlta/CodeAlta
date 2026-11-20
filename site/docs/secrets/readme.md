---
title: Secrets and environment
---

# Secrets and environment

A secret is a named value that CodeAlta uses but never shows again: an API key, a token for Grafana, Jira or a registry. A variable is a named value that is not secret, such as an address. Both belong to the computer: CodeAlta Desktop, CodeAlta TUI, `altacli` and `altanode` on the same profile share them.

The configuration names them instead of holding them: `${secret:NAME}` and `${var:NAME}`, wherever `${env:NAME}` works, and `api_key_secret` for the key of a provider.

The value of a secret is kept on this computer, or in an item of a vault such as 1Password, Bitwarden or KeePassXC: see [Vaults](vaults.md). The configuration names the secret the same way in both cases.

## Settings tabs

In CodeAlta Desktop, open **Settings > Secrets and environment** under **Agent & models**. **Overview** shows what is set on this computer and what needs attention. The other tabs take you directly to what you want to change:

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-secrets-overview.webp" alt="Settings, Secrets and environment, Overview tab: the tabs Overview, Secrets, Variables, Environment, Vaults, Sign-ins and Activity with their counts, and a card for each of Secrets, Variables, Environment, Vaults and Sign-ins with what it is, its count, what needs attention such as a secret that is not set or a vault that is not installed, and its first action" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Overview: one card per kind of value, with what needs your attention.</figcaption>
</figure>

- **Secrets** lists each name, where it is kept and any problem reading it. Copy the reference beside the name, or select a row to see what names it and change **Projects** or **Ask before use**. Use **Add a secret…** for a value on this computer, or **Add from a vault…** for an item in a vault. **Rotate** gives a local secret a new value and version. Deleting a secret does not change its vault item.
- **Variables** lists non-secret values of this computer. Use **Add a variable…**, or edit a value in the table and choose **Save** or press Enter. A variable supplies a `${var:NAME}` reference; it does not set an environment variable on its own.
- **Environment** lists the projects that give variables to their sessions, and the spaces and projects that set CLI profiles. See [Project environment](#project-environment).
- **Vaults** adds, checks and unlocks [vaults](vaults.md). **Sign-ins** manages [website sign-ins](../browser.md#sign-in-with-a-vault-item) the session browser can type.
- **Activity** shows recent secret uses and their outcomes. Uses [waiting for confirmation](#ask-before-use) appear above all tabs.

## Project environment

In the **Environment** tab, select a project or choose **Set the environment of a project…**. The tab lists only the projects that set variables, each with what is missing on this computer; **Set it…** beside a secret or a variable that is not set adds it.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-secrets-environment.webp" alt="Settings, Secrets and environment, Environment tab: the projects acme-api and acme-web all set, and contoso-portal open with GH_TOKEN from the secret CONTOSO_GH_TOKEN, NPM_TOKEN whose secret is not set with Set it, and ACME_TOKEN whose secret is limited to other projects, then the form that adds a variable and the file they are saved in" loading="lazy">
  <figcaption class="small text-secondary mt-2">The environment of contoso-portal: one secret is not set on this computer, another is limited to the acme projects.</figcaption>
</figure>

Set a variable to text, a home-relative path such as `~/.config/tool`, or a reference to the application's environment (`${env:NAME}`), a variable of this computer (`${var:NAME}`), or a secret of this computer (`${secret:NAME}`). A name such as `GH_TOKEN` or `OPENAI_API_KEY` must use a single secret or environment reference, not the credential itself.

The project stores these expressions in `.alta/config.toml`, so the team can commit them without committing secret values:

```toml
[env]
GH_TOKEN = "${secret:ACME_GH_TOKEN}"
GRAFANA_URL = "${var:GRAFANA_URL}"
NODE_ENV = "development"
```

In a monorepo, sub-projects inherit these settings and can override them. `[env]` takes precedence over the project's [CLI profiles](../spaces.md#command-line-tools) and the space identity. Shell commands, agent CLIs, jobs and terminals for the project use the resulting environment. A reference CodeAlta cannot resolve on this computer leaves the setting out; the session warning names it. An attached session keeps its environment until it attaches again.

## Limit a secret to projects

In **Secrets**, select the secret and edit **Projects**. **All projects** allows the same uses as an unrestricted secret. A project restriction also covers its declared sub-projects and sessions in its worktrees. Other projects and uses without a project, like a model provider key, cannot use that secret: the session starts without it, and **Activity** shows the refusal.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-secrets.webp" alt="Settings, Secrets and environment, Secrets tab: a table of the secrets with their name, the vault item or this computer they are kept in, their projects and the date they were set, and the details of ACME_GH_TOKEN open under its row with its reference, Ask before use, Projects limited to acme-api and acme-web, and Used by acme-api, acme-web and contoso-portal" loading="lazy">
  <figcaption class="small text-secondary mt-2">ACME_GH_TOKEN, kept in 1Password, limited to the two acme projects.</figcaption>
</figure>

To use a separate GitHub token in each repository:

1. Commit a `[env]` entry for `GH_TOKEN` in each repository, as above, with a different secret name for each repository.
2. On each machine that runs sessions for a repository, add the matching secret with that repository's token, or bind its name to a vault item. Limit **Projects** to that repository before using the secret. On a CodeAlta Node you can set both together with `altanode secrets set ACME_GH_TOKEN --project acme-web`, where `acme-web` is the catalog slug.
3. Start a new session in each project. **Session info** shows the project environment variable names, not the tokens.

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
| The [environment of a project](#project-environment) | `GH_TOKEN = "${secret:ACME_GH_TOKEN}"` under `[env]` |

A provider takes its key from `api_key` first, then from the secret `api_key_secret` names, then from the variable `api_key_env` names. A secret or a variable that is named but not set is an error that names it: the provider is left out (the log says which secret is missing), and an MCP server does not start (its diagnostic names it). CodeAlta never uses an empty value instead.

A secret kept in a locked vault is the same kind of error, and names the vault: unlock it in the **Vaults** tab, and the providers and MCP servers that need it start.

## Ask before use

Select a secret in **Secrets** and turn on **Ask before use**. CodeAlta gives its value only after the system checked that you are there: Windows Hello (PIN, fingerprint, face) on Windows, Touch ID or the password of your account on macOS. The prompt names the secret and what asks for it, such as a provider, an MCP server or the shell of a session.

When a use waits for you, CodeAlta Desktop asks at once: a prompt at the bottom right of the window, above Settings and the other windows, names the secret and what asks for it, a session by its title. **Confirm…** asks the system to check you, then the secret is given; **Deny** refuses it. While CodeAlta is in the background, a notification brings the window and its prompt back, as for a session that [waits for you](../workspace/readme.md#done-or-waiting), and follows the same setting.

- **The shell of a session** waits for your answer when the session starts (for its first prompt, or when it starts again after a change or a restart), for up to 2 minutes. The secrets it asks for are one prompt and one check. Confirmed, the shell starts with the values. Denied, not answered in time, or the session stopped meanwhile, it starts without them, and the session shows a warning that names them.
- Your answer decides whether the session gets the secret, not what its commands do with it: a session reads its variables once when it starts, and keeps them until it ends.
- **A session started by another session** takes the answer of that one: it gets the secrets that session was given and starts without the others, and you are not asked again.
- **A session nobody is in front of**, such as a run of an [automation](../automations.md) and the sessions it starts, is not asked for: it starts at once without the secret, with the same warning.
- Your answer is kept for that secret and what uses it, for the time you choose: each use, 15 minutes, 1 hour, or until CodeAlta closes. For the shell of a session, a use is a start of that session: with 15 minutes, the same session starting again within 15 minutes is not asked again. Something else that asks for the same secret asks you again: another provider, an MCP server of another file or space, the shell of another session, the automations of another project.
- Where nobody can answer, such as an automation at night, `altacli`, `altanode` or the developer instance unless you [let it unlock the vaults](vaults.md#when-a-vault-is-asked), the secret is not given, and what needs it says that it waits for your confirmation. The uses that still wait appear above the settings tabs, with **Confirm…** and **Decline**.
- **Activity** lists which secret was asked for, by what, when, and whether it was given. `alta secret uses` gives agents the same list.
- Where the system cannot check you (Windows Hello not set up, Linux), the switch is off and says why.

> [!NOTE]
> Ask before use is a check CodeAlta makes before it gives a value. A program that runs as you, such as a command of a session without the [sandbox](../sessions/sandbox.md), can still read what you can read.

## Masking

The value of every secret of the computer is replaced by `***` before it is written or shown: in what tools return to the model, in session histories and their files, in what the window shows, in telemetry, and in the logs of CodeAlta Desktop and `altanode`. A secret a tool prints, or that an agent repeats, therefore never lands in a session file. A value read from a vault is masked from its first read. Values shorter than four characters are not masked.

> [!WARNING]
> A secret given to a session's environment can be read by commands the agent runs. CodeAlta masks the output of tools it runs itself. An agent that runs its own tools, such as Claude Code, Codex CLI or an ACP agent, gets the variables in the environment of its program: what its own shell prints goes to its model and into its own files without passing through CodeAlta. Masking catches a value shown by mistake, not a command that encodes it.
>
> So give a profile only the secrets its work needs, in the space or the project that needs them. [Ask before use](#ask-before-use) lets you decide which sessions get one, and a [sandboxed](../sessions/sandbox.md) session gets no value kept in a vault.
>
> The key of a provider, the `env` of a `claude-code` provider, and the `env` and headers of an MCP server go to their program, never to a shell the model drives.

## Agents

Agents list the names and allowed projects with `alta secret list`, to write `${secret:NAME}` in a configuration. They never read a value, and `alta secret set` is refused: ask the user to set it. What agents can do with vaults is in [Agents and vaults](vaults.md#agents-and-vaults).

A [sandboxed](../sessions/sandbox.md) session gets none of the `alta secret` and `alta credential` commands.

## Secrets of your machines

A machine that runs CodeAlta Node has secrets and variables of its own. Set them with `altanode secrets` on it, from the page of the machine in CodeAlta Desktop, or for all your machines through a CodeAlta Server, with environments that need an approval: see [Secrets of your machines](machines.md).

## Where values are kept

Secrets of this computer live in the `secrets` folder of the profile (`~/.alta/secrets`): the names, versions and dates in `store.json`, the values encrypted with a key of the user, in the protected store of the system: Windows data protection (DPAPI), the macOS Keychain, or the keyring of the Linux desktop (libsecret). Where none is available, such as a Linux server without a desktop session, the key is in a file only the user can read. A store made with one of them is opened with the same one later.

A secret kept in a vault has no value there: `bindings.json`, beside `store.json`, names its vault item and holds the settings for **Ask before use** and **Projects**. The values CodeAlta reads from a vault stay in its memory only.
