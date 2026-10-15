---
title: Spaces
---

# Spaces

A space is a named group of projects that you work on together: your job, your personal projects, the open source you contribute to. CodeAlta Desktop shows one space at a time, with its projects, its sessions and its tabs.

A space is also a setup: the providers, MCP servers, skills, agent prompts and plugins its sessions use, what its new sessions start with, the tools they do without, and the Git, GitHub and Jira identity they work under. Switch from Work to Personal and all of it switches with the space.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-spaces.webp" alt="CodeAlta Desktop showing the Open source space with its four projects, and the list of the space switch open: Default, Work, Personal and Open source, each with its key, then New space and Organize spaces" loading="lazy">
  <figcaption class="small text-secondary mt-2">The window shows the Open source space. The list of the space switch has the other spaces, with marks for a session that waits in Work and one that runs in Personal.</figcaption>
</figure>

- The **Default** space holds every project. It is always there and cannot be deleted.
- You create the other spaces. A project can be in several spaces at once.
- A space has a name, an icon, a color and a line that says what it is for.
- Every session belongs to one space: the one you created it in. A **chat**, a session that belongs to no project, is listed in its own space only, and so is a session of a project, also after its project was moved to another space: the project is then shown in the session's space too, with the sessions of that space. A session of an earlier version of CodeAlta is listed in every space of its project.
- The **Default** space shows the projects and the chats that belong to no other space. To see everything there, turn on **Show what belongs to other spaces** in **Settings > Spaces**.

The first time CodeAlta Desktop starts, it creates two empty spaces, **Work** and **Personal**. Fill them, rename them or delete them: a space you delete does not come back.

## The space switch

The space switch is in the title bar, before the zoom and the theme switch. It shows the icon and the name of the space the window shows. With the Default space alone, it is an icon.

Click it, or press `Ctrl+G Ctrl+V`, to open the list of your spaces:

- choose a space to show it. Each space has its key beside it, from `Ctrl+G 1` to `Ctrl+G 9`, and marks that say what its sessions are doing;
- **New space…** creates a space;
- **Organize spaces…** opens **Settings > Spaces**.

When you show another space, the Explorer lists the projects of that space, and the tabs change to the ones you left there: sessions, code editors, Changes, terminals, and the Work items, Issues and Automations tabs, with their layout. Each space has its own tabs, and they come back when you show the space again, also after a restart.

- Sessions keep running while their space is not shown.
- A prompt you were typing is kept.
- If code editors hold unsaved changes, CodeAlta asks first: **Save**, **Discard** or **Cancel**.

## Create a space

Choose **New space…** in the space switch, or run `/new_space`.

- A row of ready-made spaces sets a name, an icon and a color in one click: **Work**, **Personal**, **Open source**, **Experiments**, **Learning** and **Clients**. A name you already use is not offered.
- Or type a **Name** and choose an **Icon** and a **Color**. The icons are the general icons and the brand logos.
- **What it is for** is a line for you and for the agents, for example "Projects of the Contoso contract".
- **Start from** is **Empty** unless you choose **Copy the setup of** a space: the new space then gets the resources of that space, the agent prompt and the model tiers of its new sessions, and the tools it turns off. Its identity and its projects are not copied.
- Tick the projects of the space in the list.

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-space-new.webp" alt="The New space window with the ready-made space Experiments chosen, its icon and color, a description, and two projects ticked" loading="lazy">
  <figcaption class="small text-secondary mt-2">A new space from the ready-made Experiments, with two projects ticked.</figcaption>
</figure>

A space created from the title bar is shown at once.

An empty space says **No project in this space yet.** Its **Add projects…** button opens the folder picker, just like `+` in the Projects sidebar. To manage spaces, use **Organize spaces…** in the space switch.

### Add a project folder to a space

Add a folder with the `+` button of the Explorer, or with [Open project](workspace.md#open-project-dialog), while a space is shown: the new project joins that space. A folder that is already a project of another space joins the shown one too.

## Organize spaces

**Spaces** is a group of Settings of its own: its **Overview**, then a page for each space. Open it with **Organize spaces…** in the space switch, or with `/spaces`.

The overview is a map of your setups:

- On the left, the Default space lists every project, each with the icons of the spaces it is in. Its switch **Show what belongs to other spaces** makes the Default space show everything again.
- On the right, each space has a card: its icon, its name, **What it is for**, its projects, and one line that sums up its setup, such as `2 projects · Claude Business · 3 MCP · alex@contoso.example`: its projects, what it has by name (its first provider, then how many MCP servers, skills, agent prompts and plugins), and the email it works under.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-space-settings.webp" alt="The Spaces page of Settings: the Default space with every project on the left, and the cards of the Work, Personal and Open source spaces with their descriptions and projects" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings > Spaces: every project on the left, and a card for each space with what it is for and its projects.</figcaption>
</figure>

Click a card, or a space in the navigation, to open the page of that space. **Spaces**, above its title, brings you back to the cards. The page has a tab per section, and each tab says what the section sets, so you see the whole setup before you open one:

{.table}
| Section | What you set there | What its tab says |
|---|---|---|
| **General** | Its name, icon, color and **What it is for** | |
| **Projects** | A checklist of all the projects, to tick the ones the space holds | How many |
| **Resources** | The providers, MCP servers, skills, agent prompts and plugins its sessions can use (see [What a space has](#what-a-space-has)) | What it has by name, or **All shared** |
| **New sessions** | The agent prompt and the model tiers of its new sessions | The prompt, **Own model tiers**, or **Usual settings** |
| **Tools** | The built-in tools its sessions do without | How many are off |
| **Identity** | The Git, GitHub and Jira identity of its sessions (see [Identity](#identity)) | The email, or **Usual settings** |

**Show** shows that space in the window, and **Delete** asks before it deletes the space. The Default space has a page too, with every project in it. Every change is saved at once.

To choose the projects of a space with the mouse, on the cards:

{.table}
| Drag a project | What happens |
|---|---|
| From the list onto a card | The project joins that space. |
| From a card onto another card | The project moves to the other space. With `Ctrl` or `Alt` held, it stays in both. |
| From a card back to the list | The project leaves that space. |

The `×` of a project on a card removes it from that space. A card also has **Show**, and two arrows that move the space before or after its neighbor in the list.

Deleting a space leaves its projects, their folders and their sessions alone. The projects stay in the Default space and in their other spaces, and the chats of the space go to the Default space: the question before the deletion says how many.

## Move a session to another space

**Move to space…** in the menu of a session lists the other spaces: choose one and the session goes there, with the sessions it started. Its next turn uses what the new space sets up. A session that is running or waits for an answer cannot move until it is idle.

## What a space has

Each session belongs to one space. It uses the providers, MCP servers, skills, agent prompts and plugins of that space, whatever space the window shows. A Business session keeps the tools of Business when you switch to Personal.

A resource is in every space until you choose its spaces. Choose them in either place:

- on its page of Settings (**Providers**, **MCP Servers**, **Skills**, **Agent prompts**, **Plugins**): the chip on its row says **All spaces**, or **Only:** and its spaces. Click it to pick **All spaces** or some spaces, Default among them. The form of a provider has the same chip, under **Spaces**;
- on the page of a space, in **Resources**: one tab per kind, with a switch per resource. Shared items are in every space: turn one off to keep it out of this space. A small popover asks first: the other spaces keep it, and a space you create later will not get it.

When a resource would be left in no space, the popover offers to turn it off instead. A resource you give every space again, on the page of a space or in its chip, is shared again: a space you create later gets it too. A kind you have nothing of yet links to its page, such as **Add an MCP server…**.

{.table}
| Spaces of a resource | Available in |
|---|---|
| All spaces | every space, the ones you create later too |
| Business | Business only, not Default |
| Business, Default | Business and Default |

What a session cannot use in its space:

- is not in the instructions of its agent: its MCP servers, skills and agent prompts are not listed, and what its plugins add (tools, buttons, prompt text) is left out;
- cannot be reached by its agent: `alta mcp activate`, `alta skill activate` and `alta session set_agent` refuse it and say why. No `alta` command changes the spaces of a resource: only you do, in the window or in the files;
- is dropped from the session when you take it out of the space: an MCP server it activated is gone at its next turn.

An agent also forks and sends prompts only to sessions of its own space. You reach every session.

### Providers of a space

A space can have its own provider: a Claude Code account for Business and another one for Personal, for example (see [several accounts](model-providers.md#claude-code)).

- A new session, a task or an issue you start, and an automation run start with a provider of their space: the default provider when it is in the space, else the first provider of the space. The provider chooser of a new session lists the providers of the space the window shows.
- A provider of another space is refused, also when an agent asks for it with `alta session create`.
- A session whose provider you take out of its space sends nothing more: its status line says why and the provider chooser opens when you open the session, to pick a provider of the space. A run that fails does not fall back to a provider of another space.
- The composer of a session reports the errors of the providers of its space only.

A resource of a project (in its `.alta` folder) follows its project, which is already in its spaces. A project cannot bring into a space what the space does not have: a skill or an MCP server assigned to Business stays out of Personal, also when a project of Personal defines one with the same name.

**Session info** shows the space of a session and what it can use there.

### New sessions

In **New sessions**, choose the agent prompt a new session of the space starts with: from the window, from an agent with `alta session create`, from a work item, an automation or a question about the guide. A prompt you or the agent choose still wins. **Model tiers** uses the global tiers of Settings > Models, or tiers of the space's own with **Customize** (see [Model tiers and fallback](model-providers.md#model-tiers-and-fallback)).

### Tools

In **Tools**, turn off built-in tools for the sessions of the space, such as `shell_command`. What Settings > Permissions turns off for every session is shown beside them. See [Tools turned off](workspace.md#tools-turned-off).

## Identity

A space can have its own Git, GitHub and Jira identity, so that the sessions of Work commit and sign as you do at work, and those of Personal as you do at home. Open the page of the space in **Settings > Spaces**: its **Identity** section has three cards.

| Card | Fields |
| --- | --- |
| **Git** | Name and email of your commits, the SSH key to fetch and push with, how commits are signed (SSH or GPG, with the key), and **Signed-off-by** |
| **GitHub** | The folder of a profile of the GitHub CLI (`gh`), with the command that signs it in |
| **Jira** | The site, the email of your account, and the name of the environment variable that holds your API token |

A field you leave empty keeps your usual settings, which it shows in gray: a space without identity changes nothing. The Default space can have an identity too. Each card is saved when you leave a field.

**Check** on a card tries it: who Git commits as and whether the keys exist, which GitHub account the profile is signed in to, and whether Jira accepts your account. For a new GitHub profile, copy the command of the card, run it in a terminal, and follow `gh auth login`.

A session works under the identity of its space: its commands, the Claude Code CLI, its background jobs, the `gh` tool, the MCP servers it starts and the Jira commands. A session that runs keeps its identity when you show another space or change the identity; it takes the new one the next time it starts. Terminals take the identity of the space the window shows. **Session info** shows the identity of a session.

In the file of the space, the identity looks like this:

```markdown
---
id: work
kind: space
name: Work
identity:
  git: { name: A. Mutel, email: alex@contoso.example, ssh_key: ~/.ssh/contoso, sign: ssh, signing_key: ~/.ssh/contoso.pub, signoff: true }
  github: { config_dir: ~/.config/gh-contoso }
  jira: { site: contoso.atlassian.net, email: alex@contoso.example, token_env: CONTOSO_JIRA_TOKEN }
---
```

Only paths and names of variables are kept, never a token. A project that names its Jira in its `.alta/config.toml` keeps what it writes there: the space gives the site, the email and the token variable it leaves out.

What an identity does not change:

- the settings a repository writes in its own `.git/config`;
- an `env` block in the `settings.json` of a Claude Code profile, which can replace what CodeAlta gives the CLI;
- a `GITHUB_TOKEN` or `GH_TOKEN` of your environment, which comes before the GitHub profile;
- the HTTPS accounts of Git Credential Manager;
- the issues and pull requests an [automation](automations.md) event trigger watches, which are read with your usual GitHub profile;
- the Atlassian CLI keeps one login for your computer: when two spaces use two Jira accounts, their sessions take turns and the CLI switches between them.

## What follows the shown space

Everything the window lists is what the shown space has:

- the Explorer, with the sessions, the terminals and the [work item](work-items.md) marks of its projects;
- the search (`Ctrl+P`): projects, sessions and files;
- **Open project** and the browser of saved sessions;
- **All projects** in the [Work items](work-items.md) tab, and the project list of the [Issues](issues.md) tab;
- the previous and next project keys.

[Automations](automations.md) belong to the application: their tab lists every project in every space.

When you open a session whose project is not in the shown space, from a link, a work item or an automation, the window shows the Default space first.

## What happens in the other spaces

With more than the Default space, the foot of the Explorer has one small button for each space, with its icon. The shown space is marked. Click a button to show its space.

A mark on a button says what the sessions of that space are doing:

{.table}
| Mark | Meaning |
|---|---|
| Waiting | A session waits for you: a question, a command to review, or a form to fill. |
| Failed | The last run of a session failed. |
| Running | Sessions are running. |
| Background | A provider works in the background. |

When a session waits for you or failed in a space that is not shown:

- a line above the buttons names the space and the session. Click it to show that space and open that session;
- a dot appears on the space switch of the title bar;
- a message says so once when a session starts to wait, with a **Show** button.

<figure class="alta-figure my-4" style="max-width: 30rem;">
  <img src="{{site.basepath}}/img/alta-desktop-space-activity.webp" alt="The foot of the Explorer: a line that says the session Draft the release notes waits in the Work space, above the buttons of the four spaces with their marks" loading="lazy">
  <figcaption class="small text-secondary mt-2">The foot of the Explorer: a session waits for you in Work, sessions run in Personal, and Open source is the space shown.</figcaption>
</figure>

## Keys and commands

{.table}
| Action | Shortcut or command |
|---|---|
| Open the space switch | `Ctrl+G Ctrl+V` or `/space` |
| Show the space at a place of the list (Default is 1) | `Ctrl+G 1` to `Ctrl+G 9` |
| Previous space / next space | `Ctrl+Alt+PageUp` / `Ctrl+Alt+PageDown`, or `/space_prev` / `/space_next` |
| Create a space | `/new_space` |
| Organize spaces | `/spaces` |

The previous and next space keys also work while a terminal has the keyboard. All of these commands are in the search (`Ctrl+P`) and in the help (`F1`).

## Agents and spaces

Sessions read and change spaces with `alta space`. Ask in your own words:

```text
Which projects are in my Work space?
```

```text
Create a space named Clients for the two Contoso projects, and show it.
```

An agent reads **What it is for** of each space, so a clear description helps it choose the right projects. When a session lists the projects, it gets those of the space the window shows, unless you ask for another space or for all of them.

An agent shows another space in the window only when you ask for it.

A session keeps working while its space is not shown. When its agent wants to show you a file or the changes of a project that the shown space does not have, the window stays where you are: a message says in which space it opens, with a **Show** button. Click it to show that space with the editor or the Changes tab open. The agent is told that nothing was opened, so it can say so in its answer.

## Files

Each space is a Markdown file in `~/.alta/spaces/`, named after the id of the space. The id comes from the first name of the space, in lower case with `-` between words, and stays the same when you rename the space.

```markdown
---
id: open-source
kind: space
name: Open source
icon: globe
color: "#9d3f9d"
order: 3
new_sessions:
  agent_prompt: reviewer
tools:
  disabled: [shell_command]
---

Libraries I maintain and the projects I contribute to.
```

The text under the header is **What it is for**. `order` is the place of the space in the list. `new_sessions.agent_prompt` is the agent prompt of **New sessions**, and `tools.disabled` the tools of **Tools**. The Default space has a file, `default.md`, only once you change its name, its icon, its color or its description.

The spaces of a project are written in the file of the project, `~/.alta/projects/<name>.md`, as one line of its header that lists the ids of its spaces:

```yaml
spaces: [work, open-source]
```

A project that is only in the Default space has no `spaces` line. An id that no space has is ignored.

The spaces of the providers, MCP servers, skills, agent prompts and plugins are in `~/.alta/config.toml`, beside their settings. A resource without a `spaces` list is in every space:

```toml
[providers.claude-business]
type = "claude-code"
config_dir = "~/.claude-business"
spaces = ["business"]

[plugins.mcp.servers.atlassian]
spaces = ["business"]

[plugins.jira]
spaces = ["business"]

[skills.spaces]
terraform = ["business", "default"]

[prompts.spaces]
reviewer = ["business"]
```

Only the global `config.toml` assigns spaces: a `spaces` list in the `config.toml` of a project is ignored.

Which space the window shows, and the tabs of each space, are kept with the window, like its theme.

## CodeAlta TUI

CodeAlta TUI does not show spaces: it lists every project. Its agents still have the `alta space` commands, to read and organize the spaces that CodeAlta Desktop shows.
