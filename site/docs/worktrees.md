---
title: Worktrees
---

# Worktrees

A session can work in a git worktree: a second folder of the same repository, on a branch of its own. The agent changes files there, and your project folder stays as it is. Several sessions can then work on the same project at the same time.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-worktrees.webp" alt="CodeAlta Desktop with a session that works in a worktree, and the Changes tab listing the worktrees of the project" loading="lazy">
  <figcaption class="small text-secondary mt-2">A session in its own worktree, and the worktrees of the project in the Changes tab.</figcaption>
</figure>

You create and remove worktrees in CodeAlta Desktop. A session that works in one also runs in CodeAlta TUI.

## Start a session in a worktree

1. Open a new session in a project that is a git repository.
2. Under the prompt, click **Project folder** and choose **New worktree**.
3. Keep **Current commit**, or choose the branch to start from.
4. Send your prompt.

<figure class="alta-figure my-4" style="max-width: 36rem;">
  <img src="{{site.basepath}}/img/alta-desktop-worktree-new.webp" alt="The choice under the prompt of a new session: Project folder or New worktree, the branch to start from and the folder of the worktree" loading="lazy">
  <figcaption class="small text-secondary mt-2">Where the next session works.</figcaption>
</figure>

CodeAlta creates the worktree on a new branch, `alta/<name>`, and starts the session in it. A worktree starts from a commit: changes you have not committed stay in the project folder.

The choice is kept for the project. Choose **Project folder** to go back.

## Know where a session works

- Under the prompt, the worktree, its folder and its branch are shown in turquoise.
- In the sidebar, a session that works in a worktree has a tree mark.
- **Session info** shows the folder of the worktree.

## Manage worktrees

Run `/worktree`, or right-click a project in the sidebar and choose **Worktrees…**. The window lists every worktree of the project that git knows, also the ones whose session you deleted.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-worktree-manager.webp" alt="The Worktrees window in CodeAlta Desktop, with the project folder, worktree branches, last-used sessions and actions for each checkout" loading="lazy">
  <figcaption class="small text-secondary mt-2">See which worktrees are still in use, inspect their files and changes, and select the ones to remove.</figcaption>
</figure>

{.table}
| You see | What it means |
| --- | --- |
| **Project folder** | The folder of the project. It is never removed. |
| **In use** | A session is working there now. Wait for it to finish, or stop it. |
| **Locked** | Git keeps this worktree. Run `git worktree unlock` to remove it. |
| **Folder gone** | Git still lists the worktree, and its folder was deleted. Removing it makes git forget it. |
| **Last used** | When the last session of the worktree was active. **Unknown** when none of its sessions is left. |

- To remove worktrees, tick one, several, or all of them with the box of the header, then click **Remove**. CodeAlta shows the list again before it removes anything.
- A worktree with changes that are not committed stays. CodeAlta lists it afterwards and asks about it on its own. Files that git ignores are deleted with the folder.
- **Outcome unknown** means CodeAlta could not confirm what happened. Check the refreshed list before trying again. **Stop** lets the removal in progress finish without starting the next group.
- Branches are kept. Tick the box of the confirmation to also delete the `alta/` branches that have no commit of their own.
- The buttons of a row open the worktree in the code editor, show its changes, and copy its path.

You cannot remove a worktree, or change its branch, while a session is working in it.

### In the Changes tab

The [Changes](workspace.md#changes-desktop) tab lists the worktrees of the project above the files.

- Click a worktree to see its changes and its commits.
- Click the trash button of a worktree to remove it. CodeAlta asks first, and asks again when the worktree has changes that are not committed. Its `alta/` branch goes with it when it has no commit of its own.
- Click the branch, there or under the prompt, to switch to another branch or to create one.

## Where worktrees are stored

Open **Settings**, then **Worktrees**.

{.table}
| Choice | Folder |
| --- | --- |
| In your CodeAlta folder | `~/.alta/worktrees/<project>/<name>` |
| Inside each project | `<project>/.alta/worktrees/<name>`, which git ignores |
| In a folder you choose | `<folder>/<project>/<name>` |

A worktree gets a name of two words and four random characters, such as `quiet-heron-7k2m` or `amber-denali-x4pq`. Its branch has the same name, after `alta/`. The characters keep two worktrees apart when they are created at the same time by agents that do not see each other, for example in two clones of the repository.

## Good to know

- When a worktree is removed, its sessions continue in the project folder.
- The code editor of a project shows the files of the project folder. To edit the files of a worktree, open it from the Worktrees window: it gets a tab of its own, with the name of the worktree.
- A branch that has commits that are in no other branch is always kept.
- A session started by an agent that works in a worktree works in the same worktree.
- An agent can start sessions in worktrees. Ask in your own words, for example:

```text
Start two sessions, each in its own worktree: one adds the tests, the other updates the documentation.
```
