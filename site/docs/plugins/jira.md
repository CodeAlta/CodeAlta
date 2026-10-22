---
title: Jira plugin
---

# Jira plugin

Many projects keep their code on GitHub or GitLab and their issues in Jira. The built-in Jira plugin brings the issues of a Jira project into CodeAlta: in the [Issues tab](../issues.md), for agents, and as a trigger of [automations](../automations.md).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-issues-jira.webp" alt="The Issues tab of CodeAlta Desktop with the issues of three Jira projects, the choice of one project and one issue open" loading="lazy">
  <figcaption class="small text-secondary mt-2">The issues of three Jira projects in the Issues tab, beside those of the repository. The list next to **Jira** chooses one project.</figcaption>
</figure>

## Turn it on for a project

Jira is used by a project, never by all of them. Add this to the `.alta/issues.toml` of the project, the file the [views of the Issues tab](../issues.md#views) of your team are kept in, and commit it:

```toml
[jira]
site = "example.atlassian.net"
project = "ALTA"
```

{.table}
| Key | What it is |
|---|---|
| `site` | The address of your Atlassian site. |
| `project` | The key of the Jira project: the letters before the number of an issue, as in `ALTA-12`. |

That is all. The first time the project needs Jira, CodeAlta downloads the Atlassian CLI (`acli`) into `~/.alta/cache/jira` and tells you so in a notice. Nothing is downloaded for a project that does not turn Jira on.

### Several Jira projects

A repository can hold the work of several teams or products, each with its own Jira project. Name the others in `projects`:

```toml
[jira]
site = "example.atlassian.net"
project = "ALTA"
projects = ["PLAT", "TEAM"]
```

- The issues of all these projects are listed together, the most recently updated first. The key of an issue says which project it is in: `PLAT-7`.
- In the Issues tab, the list beside **Jira** shows **All projects** or one of them.
- A new issue goes to the project of `project`, here `ALTA`. An agent creates an issue in another one only when you name it, and never in a project that is not in the list.
- Without `project`, the first key of `projects` is the one new issues go to.
- All the projects are on the same site, and 20 at most are read.

When your account does not see one of the projects, the Issues tab lists the others and says which one is missing.

## Sign in

CodeAlta uses the sign-in of the Atlassian CLI. There are two ways.

**In the browser.** Open the search of the window (`Ctrl+P`) and run **Jira: Sign in**. A terminal opens, your browser asks you to allow the Atlassian CLI, and the terminal asks which site to use. Then reload the Issues tab.

**With an API token**, when you cannot use a browser. Create a token in your [Atlassian account](https://id.atlassian.com/manage-profile/security/api-tokens) and set two environment variables before you start CodeAlta:

```sh
export JIRA_EMAIL=me@example.com
export JIRA_API_TOKEN=...
```

CodeAlta signs in by itself with them. To use other names, add `email` and `token_env` to `[jira]` in the `.alta/local/issues.toml` of the project, which is yours alone and kept out of git:

```toml
[jira]
email = "me@example.com"
token_env = "MY_JIRA_TOKEN"
```

A key of your file wins over the one of the team's file. CodeAlta keeps the `.alta/local` folder out of git with a `.gitignore` of `*`, which it writes the first time it saves a view of yours; when you write this file first, create `.alta/local/.gitignore` with the line `*` too, so that your account is never committed.

When one of these files cannot be read or is not valid TOML, Jira is off for the project rather than set by the other file, and **Jira: Status** says which file to fix.

The token stays in your environment: CodeAlta does not write it anywhere.

**Jira: Status**, in the same search, says whether Jira is ready and what is missing.

### The Jira account of a space

The **Jira** card in the **Identity** of a [space](../spaces.md#identity) gives the site, the email and the token variable that a project of that space leaves out: such a project needs only `project` in `[jira]`. What the project writes wins. The Atlassian CLI keeps one login for your computer, so when two spaces use two Jira accounts, their sessions take turns and the CLI switches between them.

`spaces` in the `[plugins.jira]` table of `~/.alta/config.toml` keeps the plugin to some spaces (see [What a space has](../spaces.md#what-a-space-has)).

## What you get

- **The Issues tab** shows Jira beside the service of the repository. Choose **Jira** next to the project to list its issues, filter them, read them with their comments, open one in Jira, or start a session on it.
- **Agents** read the issues with `alta issue`, as for any other tracker, and change them with `alta jira` when you ask:

```text
List the open issues of this project and tell me which ones are about the Explorer.
```

```text
Create a Jira bug for the crash we just found, then move ALTA-12 to In Progress.
```

```text
Create a Jira story in PLAT for the new build cache.
```

- **Automations** can start when an issue of the project is created or updated, in any of its Jira projects: choose the **Jira issue** trigger. CodeAlta looks at Jira every five minutes.

## Turn it off

Remove the `[jira]` table of the project, or set `enabled = false` in it. `enabled = false` in your own `.alta/local/issues.toml` turns Jira off for you alone.

## Earlier configuration

Before `.alta/issues.toml`, a project named its Jira in `[plugins.jira]` of its `.alta/config.toml`. That table is still read while `.alta/issues.toml` has no `[jira]` settings (a `[jira]` that holds views only does not count, and your own `.alta/local/issues.toml` already completes it), and **Jira: Status** says where it belongs now: move `site`, `project` and `projects` to `[jira]` of `.alta/issues.toml`, and `email` and `token_env` to `[jira]` of `.alta/local/issues.toml`. CodeAlta does not move it for you, since it would change a file of the repository.
