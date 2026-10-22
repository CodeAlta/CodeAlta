---
title: Issues and pull requests
---

# Issues and pull requests

CodeAlta Desktop shows the issues and the pull requests of a project where you work, and starts a session on one of them in a click.

It finds the service from the git remote of the project, as the remote is written: an `insteadOf` rule of your Git configuration that sends `github.com` to an SSH host alias, for a second account, does not hide it. Nothing has to be configured, except for Jira.

{.table}
| Service | Issues | Pull requests | Sign-in |
|---|---|---|---|
| GitHub | Issues | Pull requests | `gh auth login`, or `GITHUB_TOKEN` |
| GitLab | Issues | Merge requests | `glab auth login`, or `GITLAB_TOKEN` |
| Azure DevOps | Work items | Pull requests | `az login`, or `AZURE_DEVOPS_EXT_PAT` |
| Bitbucket | Issues | Pull requests | `BITBUCKET_ACCESS_TOKEN` |
| Jira | Issues | | The [Jira plugin](plugins/jira.md) |

A public repository is read without signing in. A private one needs the sign-in of its service.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-issues.webp" alt="The Issues tab of CodeAlta Desktop with the open issues of a GitHub repository and one issue open" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Issues tab: the issues and the pull requests of a project, and a session started on one of them in a click.</figcaption>
</figure>

## The Issues tab

Open it with **Issues** in the title bar (in its **…** menu when the window is narrow), or with `Ctrl+G Ctrl+B`.

With no projects, the tab shows **No projects in this snapshot.** Open a project first.

- Choose the project, then **Issues** or **Pull requests**. The list has the projects of the [space](spaces.md) the window shows.
- **Open**, **Closed**, **Merged** and **All** are separate lists, the most recently updated first.
- Type words or a number to filter the list.
- Select an item to read its description and its comments. Double-click it, or use the expand button, to read it in a larger window.

A project that keeps its code on one service and its issues in Jira shows both: choose the tracker beside the project. A project can name [several Jira projects](plugins/jira.md#several-jira-projects): their issues are listed together, and a list beside the tracker shows one of them alone.

## Filters

On Azure DevOps a project usually holds the work of several teams, sprint after sprint. The tab can show only the work items of your team, of a sprint, assigned to you, or with a value of any field of your work items. CodeAlta has four filters of its own, and you can add the fields of your project:

{.table}
| Filter | Its values |
|---|---|
| **Team** | The teams of the project, yours first. |
| **Sprint** | **Current sprint**, **Next sprint**, **Previous sprint**, the last sprints that ended and those to come, of the team that is chosen. |
| **Assigned to** | **Me** or **Nobody**. |
| **Type** | The types of work items of the project: Bug, User Story, Task... |
| **Order** | **Last changed first** (without a value), **Backlog order**, **Highest priority first**, **Newest first**. |
| A field you add | Priority, Severity, State, Tags, Area Path, a field of your own such as *Customer*... |

**Type** and the fields with a list of values (State, Priority, Tags, Area Path, a picklist) take several values at once: the list opens checkboxes, and the work items in any of the values checked are listed, for example the states *New*, *Ready* and *On Hold*. **Backlog order** is the order you give the work items on the boards of Azure DevOps by dragging them, which Azure DevOps keeps in the field *Stack Rank* (*Backlog Priority* in a Scrum project).

Not every team works with teams or sprints: a filter is used only when a view turns it on.

When the values of a filter cannot all be read (Azure DevOps does not answer, or asks to wait a moment), or a field has more than the 300 values the list offers, the filter says so, and a view that gives it a value still lists by that value as the view writes it: the tab then lists the work items of that value, or none, never every work item of the project.

### Views

The filters are kept in **views**: a view has a name, and lists the work items that match each filter it turns on, with the value it gives that filter. A project can have several views, for example *My sprint* and *Bugs*, and you switch between them in the tab. There are two kinds:

{.table}
| Group | Kept in | Who sees it |
|---|---|---|
| **For me** | `<project>/.alta/local/issues.toml` | You only: CodeAlta writes a `.gitignore` beside it, so git ignores the folder. |
| **For the team** | `<project>/.alta/issues.toml` | Everyone who has the repository, once you commit the file. |

The same files hold the settings of the [Jira plugin](plugins/jira.md) (`[jira]`): the site and the projects in the team's, your account in yours.

The view you chose is remembered for the project (in your file). Without a choice, the tab shows your first view, else the team's first; without any view, it lists every item.

### In the tab

Beside the project, the list of the views of the project switches to another view, and the tab lists what that view lists. The line under the toolbar shows the filters the view lets you change (**In the tab**, below), each with its values: change the sprint there for a while (its list gives the dates of each sprint), and ↺ gives back the value of the view. The choice is kept for that view, and the view itself does not change. The other filters of the view are counted after them (**+ 5 filters**): hover the count to see which they are, the reason an item is not listed, and click it, or the gear beside the views, to open **Manage views**. The text field still narrows the list by words or a number. Pull requests have no filters.

### Manage views

The gear beside the views (**Manage views…**) opens a window for the project and the tracker the tab shows. Choose the **View**, grouped as **For me** and **For the team**. **New view** makes one: give its name, whether it is for you or for the team, and whether it starts with the filters of the view shown or with none. **Rename** and **Remove** act on the view shown.

The window lists the filters of that view, one row each:

{.table}
| Column | What it is |
|---|---|
| **Active** | Whether the view lists by the filter. Turned off, the filter keeps its value and its name. |
| **Name** | The name the tab shows. Empty, it is the name of the field in Azure DevOps. |
| **Field** | The field in Azure DevOps, such as `Microsoft.VSTS.Common.Priority` or `Custom.Customer`. |
| **Field type** | What the field holds, as Azure DevOps says: a list, a number, a person, tags, a path, yes or no, a text. |
| **Value** | The value the view lists by. |
| **In the tab** | The tab offers to change the value for a while, in the line under its toolbar. Turn it on for the sprint, say, and leave the others fixed. |
| ✕ | Removes the filter from the view. |

**Add a filter** lists the filters of CodeAlta that the view does not have yet (Team, Sprint...), then the fields of your work items, with a search. A field with a list of values (Priority 1 to 4, a picklist) takes a value of the list; a person is **Me** or **Nobody**; a text field takes a text, and the work items that contain it are listed. A sprint is best given as **Current sprint**, which moves on with the sprints by itself; for another sprint, make a second view.

**Save** writes the view in its file, where you can also edit it. The name of the view is part of the name of each table, and a field is written by its name in Azure DevOps:

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

Each table is a filter the view lists by; `mode = "off"` keeps a filter with its value without using it, `pick = true` offers to change it in the tab, and a filter the view does not name is off. The views of the team have the same tables in `<project>/.alta/issues.toml`, without `current`. Write each filter as a table of its own, as above: CodeAlta reads a view written as an inline table or with dotted keys, but does not save, rename or remove it, and says so. A file holds 64 views at most. A file that is not valid TOML shows none of its views: the tab says which file, and lists every item until you fix it.

Several values are a list: `value = ["New", "Ready", "On Hold"]`. The order is the filter `sort`, with `value = "Backlog order"`. A `value` is the name of a team, of a sprint, of a type or a value of a field, or one of `@current`, `@next`, `@previous` (sprints), `@me` and `@none` (people). An empty value (`""`) lists every item. When the repository ignores the whole `.alta` folder, add an exception for `.alta/issues.toml` to share the views of the team.

The names of CodeAlta's own filters are in the language of the window; the names of fields, teams and sprints are those of your Azure DevOps.

The filters are those of Azure DevOps for now. On the other services the tab lists every issue, as before.

## Work on an item

An open item has two buttons:

{.table}
| Button | What happens |
|---|---|
| **Start in a new worktree** | A new session works on it in its own [worktree](worktrees.md), on a new branch. |
| **Start in a new session** | A new session works on it in the folder of the project. |

The session receives the title, the link and the description of the item. For a pull request it is asked to review it, not to merge it.

You can start several items at once: each one is marked **Starting…** in the list while its worktree is made. When its session is ready, it opens if you still read that item; otherwise a notification offers it with **Show**.

**Open on GitHub** (or the name of the service) opens the item in your browser, and **Copy link** copies its address.

## Create a pull request

When a session has finished its work, **Pull request** in the **Workspace** popover of its prompt (click the project or the branch above the editor) asks it to open a pull request. The session puts its work on a branch, commits it, pushes it and answers with the link. It never merges.

The button waits until the session is idle and the prompt is empty.

What the session is told is listed in **Settings > Pull requests**. CodeAlta ships one kind, **Default**. Add your own kinds there, for you or for one project: a release, a hotfix, the checklist of your team. With several kinds, the button lets you choose.

{.table}
| Kind | File |
|---|---|
| Yours, for every project | `~/.alta/prompts/pull-requests/<name>.pr.md` |
| Of one project | `<project>/.alta/prompts/pull-requests/<name>.pr.md` |

A file named `default.pr.md` replaces the built-in instructions. A kind of the project replaces one of yours with the same name.

<figure class="alta-figure my-4" style="max-width: 36rem;">
  <img src="{{site.basepath}}/img/alta-desktop-pull-request.webp" alt="The Pull request button in the Workspace popover above the prompt of a session, with its menu open" loading="lazy">
  <figcaption class="small text-secondary mt-2"><strong>Pull request</strong> in the Workspace popover: it asks the session to open a pull request for its work.</figcaption>
</figure>

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-pull-request-settings.webp" alt="The Pull requests page of Settings with the default instructions" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings > Pull requests: the instructions a session is given, and the kinds you add.</figcaption>
</figure>

## For agents

A session reads the same issues and pull requests with `alta issue`, wherever they are kept:

```text
Look at the open issues of this project and pick the ones we can close with the last release.
```

It uses the same filters: it can list what your Issues tab shows, or ask for a sprint, a team, states or any field you name.

```text
Take the work items of my current sprint that are Ready, in the order of the backlog, and start with the first.
```

It can also set the filters of the tab for you, as you do in the tab: in your current view, or in a copy of the team's view, unless you name a view. It can switch, make, rename and remove views, and changes a view of the team only when you ask for it. The tab shows what it set, and you change it in **Manage views** as anything else.

```text
In this project I only want to see the items assigned to me, of my team, in the current sprint. Then make a second view "Bugs" with only the bugs.
```

```text
In this project I only want to see the issues assigned to me, of my team, in the current sprint.
```

```text
Change the issue filter so that I only see Ready and Active items.
```

A value you changed in the line under the toolbar stays until ↺, whatever an agent sets in the view.

## In a prompt

Type `#` in a prompt to insert a link to an issue or a pull request of the project. See the [Git plugin](plugins/git.md).
