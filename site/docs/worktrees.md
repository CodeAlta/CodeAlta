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

The [Changes](workspace.md#changes-desktop) tab lists the worktrees of the project above the files.

- Click a worktree to see its changes and its commits.
- Click the trash button of a worktree to remove it. CodeAlta asks first, and asks again when the worktree has changes that are not committed.
- Click the branch, there or under the prompt, to switch to another branch or to create one.

You cannot remove a worktree, or change its branch, while a session is working in it.

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
- The code editor shows the files of the project folder. Use the Changes tab to read the files of a worktree.
- The branch of a removed worktree is kept when it has commits that are in no other branch.
- A session started by an agent that works in a worktree works in the same worktree.
- An agent can start sessions in worktrees. Ask in your own words, for example:

```text
Start two sessions, each in its own worktree: one adds the tests, the other updates the documentation.
```
