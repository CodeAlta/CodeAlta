---
title: Issues and pull requests
---

# Issues and pull requests

CodeAlta Desktop shows the issues and the pull requests of a project where you work, and starts a session on one of them in a click.

It finds the service from the git remote of the project. Nothing has to be configured.

{.table}
| Service | Issues | Pull requests | Sign-in |
|---|---|---|---|
| GitHub | Issues | Pull requests | `gh auth login`, or `GITHUB_TOKEN` |
| GitLab | Issues | Merge requests | `glab auth login`, or `GITLAB_TOKEN` |
| Azure DevOps | Work items | Pull requests | `az login`, or `AZURE_DEVOPS_EXT_PAT` |
| Bitbucket | Issues | Pull requests | `BITBUCKET_ACCESS_TOKEN` |

A public repository is read without signing in. A private one needs the sign-in of its service.

## The Issues tab

Open it with the issue icon of the activity bar, or with `Ctrl+G Ctrl+B`.

- Choose the project, then **Issues** or **Pull requests**.
- **Open**, **Closed**, **Merged** and **All** are separate lists, the most recently updated first.
- Type words or a number to filter the list.
- Select an item to read its description and its comments. Double-click it, or use the expand button, to read it in a larger window.

A project can have more than one tracker, when a plugin adds one: choose the tracker beside the project.

## Work on an item

An open item has two buttons:

{.table}
| Button | What happens |
|---|---|
| **Start in a new worktree** | A new session works on it in its own [worktree](worktrees.md), on a new branch. |
| **Start in a new session** | A new session works on it in the folder of the project. |

The session receives the title, the link and the description of the item. For a pull request it is asked to review it, not to merge it.

**Open on GitHub** (or the name of the service) opens the item in your browser, and **Copy link** copies its address.

## In a prompt

Type `#` in a prompt to insert a link to an issue or a pull request of the project. See the [Git plugin](plugins/git.md).
