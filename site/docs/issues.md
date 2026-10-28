---
title: Issues and pull requests
---

# Issues and pull requests

CodeAlta Desktop shows the issues and pull requests of your projects, and starts a session on one in a click.

The service is found from the project's git remote, as written: an `insteadOf` rule that sends `github.com` to an SSH host alias does not hide it. Nothing has to be configured, except Jira.

{.table}
| Service | Issues | Pull requests | Sign-in |
|---|---|---|---|
| GitHub | Issues | Pull requests | `gh auth login`, or `GITHUB_TOKEN` |
| GitLab | Issues | Merge requests | `glab auth login`, or `GITLAB_TOKEN` |
| Azure DevOps | Work items | Pull requests | `az login`, or `AZURE_DEVOPS_EXT_PAT` |
| Bitbucket | Issues | Pull requests | `BITBUCKET_ACCESS_TOKEN` |
| Jira | Issues | | The [Jira plugin](plugins/jira.md) |

Public repositories are read without signing in; private ones need the service's sign-in (see the [Git plugin](plugins/git.md#sign-in-for-issue-lookup)).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-issues.webp" alt="The Issues tab of CodeAlta Desktop with the open issues of a GitHub repository and one issue open" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Issues tab: the issues and the pull requests of a project, and a session started on one of them in a click.</figcaption>
</figure>

## The Issues tab

Open it with **Issues** in the title bar (in its **…** menu when the window is narrow), or `Ctrl+G Ctrl+B`.

- Choose the project, then **Issues** or **Pull requests**. The list has the projects of the window's [space](spaces.md).
- **Open**, **Closed**, **Merged** and **All** are separate lists, most recently updated first.
- Type words or a number to filter.
- Select an item to read its description and comments. Double-click it, or use the expand button, for a larger window.

When a service cannot be read, the tab says so in place of the list, and the log says why: see [An issue tracker shows an error](troubleshooting.md#an-issue-tracker-shows-an-error).

A project with its code on one service and its issues in Jira shows both: choose the tracker beside the project. With [several Jira projects](plugins/jira.md#several-jira-projects), a list beside the tracker shows one alone.

## Filters

On Azure DevOps, the tab can show only the work items of your team, a sprint, assigned to you, or with any field value. Filters are those of Azure DevOps for now; on other services the tab lists every issue.

{.table}
| Filter | Its values |
|---|---|
| **Team** | The teams of the project, yours first. |
| **Sprint** | **Current sprint**, **Next sprint**, **Previous sprint**, and recent and upcoming sprints of the chosen team. |
| **Assigned to** | **Me** or **Nobody**. |
| **Type** | The work item types: Bug, User Story, Task... |
| **Order** | **Last changed first** (default), **Backlog order**, **Highest priority first**, **Newest first**. |
| A field you add | Priority, Severity, State, Tags, Area Path, a custom field such as *Customer*... |

- **Type** and fields with a list of values take several values: the work items in any of them are listed.
- **Backlog order** is the order you drag items into on Azure DevOps boards (*Stack Rank*, or *Backlog Priority* in Scrum).
- A filter is used only when a view turns it on.
- When values cannot all be read, or a field has more than 300, the filter says so; a view still lists by the value it writes, never everything.

### Views

Filters are kept in **views**: a named set of filters with their values, such as *My sprint* and *Bugs*. You switch views in the tab.

{.table}
| Group | Kept in | Who sees it |
|---|---|---|
| **For me** | `<project>/.alta/local/issues.toml` | You only: CodeAlta writes a `.gitignore` so git ignores the folder. |
| **For the team** | `<project>/.alta/issues.toml` | Everyone with the repository, once you commit it. |

The same files hold the [Jira plugin](plugins/jira.md) settings: the site and projects in the team's file, your account in yours.

The chosen view is remembered for the project. Without a choice, the tab shows your first view, else the team's first, else every item.

### In the tab

- The list beside the project switches views.
- The line under the toolbar shows the filters the view lets you change for a while (such as the sprint, with each sprint's dates). ↺ restores the view's value; the view itself is not changed.
- The other filters are counted (**+ 5 filters**): hover to see them, click (or the gear) to open **Manage views**.
- The text field still narrows by words or number. Pull requests have no filters.

### Manage views

The gear (**Manage views…**) opens a window for the project and tracker shown. Choose the **View** (**For me** or **For the team**). **New view** asks for a name, who it is for, and whether to start from the current filters. **Rename** and **Remove** act on the view shown.

Each filter is a row:

{.table}
| Column | What it is |
|---|---|
| **Active** | Whether the view uses the filter. Off keeps its value and name. |
| **Name** | The name shown; empty uses the Azure DevOps field name. |
| **Field** | The Azure DevOps field, such as `Microsoft.VSTS.Common.Priority` or `Custom.Customer`. |
| **Field type** | List, number, person, tags, path, yes/no, or text. |
| **Value** | The value the view lists by. |
| **In the tab** | Lets the tab change the value for a while. |
| ✕ | Removes the filter. |

**Add a filter** offers CodeAlta's filters the view lacks, then your work item fields, with a search. A list field takes one of its values; a person is **Me** or **Nobody**; a text field lists items that contain the text. Use **Current sprint**, which follows the sprints; for another sprint, make a second view.

**Save** writes the view to its file, where you can also edit it:

```toml
# <project>/.alta/local/issues.toml: your views
[azure_devops]
current = "My sprint"

[azure_devops.views."My sprint".team]
value = "Platform"

[azure_devops.views."My sprint".sprint]
value = "@current"
pick = true

[azure_devops.views."My sprint"."System.State"]
value = ["Ready", "Active"]

[azure_devops.views.Bugs.type]
value = "Bug"

[azure_devops.views.Bugs."Microsoft.VSTS.Common.Priority"]
mode = "off"
value = "1"
label = "Prio"
```

- Each table is a filter: `mode = "off"` keeps it unused, `pick = true` offers it in the tab, and an unnamed filter is off.
- Team views use the same tables in `<project>/.alta/issues.toml`, without `current`.
- A `value` is a team, sprint, type or field value, a list of them, or `@current`, `@next`, `@previous` (sprints), `@me`, `@none` (people). `""` lists everything. Order is the filter `sort`, as in `value = "Backlog order"`.
- Write each filter as its own table: views written inline or with dotted keys are read but cannot be saved, renamed or removed.
- A file holds at most 64 views. An invalid file shows none of its views: the tab names it and lists every item.
- When the repository ignores `.alta`, add an exception for `.alta/issues.toml` to share team views.

## Work on an item

An open item has two buttons:

{.table}
| Button | What happens |
|---|---|
| **Start in a new worktree** | A new session works on it in its own [worktree](worktrees.md), on a new branch. |
| **Start in a new session** | A new session works on it in the project folder. |

The session gets the item's title, link and description. For a pull request it is asked to review, not merge.

**Runs with**, above the buttons, is the [Agent chip](workspace/prompt.md#agent-and-model) of the new session. With **Use the defaults** on, it takes the [default provider](model-providers/readme.md) and your settings; turn it off to choose the agent prompt, the model, the permission mode, review before commit and the sandbox. **More options** turns [sub-sessions](sessions/delegation.md#sessions-without-sub-sessions) off for the new session.

You can start several items at once: each shows **Starting…** while its worktree is made. A ready session opens if you are still on that item; otherwise a notification offers it with **Show**.

**Open on GitHub** (or the service's name) opens the item in your browser; **Copy link** copies its address.

## Create a pull request

When a session is done, **Pull request** in the **Workspace** popover of its prompt (click the project or branch above the editor) asks it to open a pull request. The session commits its work on a branch, pushes it and answers with the link. It never merges. The button waits until the session is idle and the prompt is empty.

<figure class="alta-figure my-4" style="max-width: 36rem;">
  <img src="{{site.basepath}}/img/alta-desktop-pull-request.webp" alt="The Pull request button in the Workspace popover above the prompt of a session, with its menu open" loading="lazy">
  <figcaption class="small text-secondary mt-2"><strong>Pull request</strong> in the Workspace popover: it asks the session to open a pull request for its work.</figcaption>
</figure>

The instructions the session gets are in **Settings > Pull requests**. CodeAlta ships **Default**; add your own kinds (a release, a hotfix, a team checklist), and the button lets you choose:

{.table}
| Kind | File |
|---|---|
| Yours, for every project | `~/.alta/prompts/pull-requests/<name>.pr.md` |
| Of one project | `<project>/.alta/prompts/pull-requests/<name>.pr.md` |

`default.pr.md` replaces the built-in instructions, and a project kind replaces one of yours with the same name.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-pull-request-settings.webp" alt="The Pull requests page of Settings with the default instructions" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings > Pull requests: the instructions a session is given, and the kinds you add.</figcaption>
</figure>

## For agents

A session reads the same issues and pull requests with `alta issue`, wherever they are kept, with the same filters:

```text
Take the work items of my current sprint that are Ready, in the order of the backlog, and start with the first.
```

It can also set the tab's filters: in your current view, or in a copy of the team's view, unless you name one. It switches, makes, renames and removes views, and changes a team view only when asked. The tab shows what it set.

```text
In this project I only want to see the items assigned to me, of my team, in the current sprint. Then make a second view "Bugs" with only the bugs.
```

```text
Change the issue filter so that I only see Ready and Active items.
```

A value you changed under the toolbar stays until ↺, whatever an agent sets.

## In a prompt

Type `#` in a prompt to insert a link to an issue or pull request of the project. See the [Git plugin](plugins/git.md#issue-picker).
