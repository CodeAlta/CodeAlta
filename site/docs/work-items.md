---
title: Work items
---

# Work items

Work items are what is left to do in a project: the **tasks** agents propose, the **plans** written in Plan mode, and the **reviews** in flight. CodeAlta Desktop shows them where you decide on them, and lists them all in one tab.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-work-items.webp" alt="The Work items tab of CodeAlta Desktop with the tasks of a project and one of them open" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Work items tab: the tasks and the plans of your projects, with what to do next for each.</figcaption>
</figure>

## Tasks

While an agent works, it can find something that is not part of what you asked: a gap, a problem, or an improvement. Before it ends its turn it proposes a follow-up task for it. The session shows the proposal at once, as a card beside Notes:

- a title, the kind of finding, and a short summary;
- **Details** opens the full description;
- with several proposals, the arrows go from one to the next.

The cards sit to the left of Notes, or below them in a narrow pane.

You decide on each card:

{.table}
| Choice | What happens |
|---|---|
| **Start in a new worktree** | A new session does the task in its own [worktree](worktrees.md), on a new branch. |
| **Do it in this session** | The session does it after its current work. |
| **Start in a new session** | A new session does it in the folder of the project. |
| **Later** | The task is kept, out of the way. |
| **Dismiss** | The task is not worth doing. |

- A new session started from a card uses the provider, model and reasoning effort of the session that shows the card.
- When you ask the proposing session to do the task itself, its card says **Task in progress**: **OK** puts it away, and it goes away by itself once the task is done.
- Agents propose a task only for something concrete they verified, and never start it themselves. A session has at most five proposals waiting.

## Plans

A plan is a document you review before the work starts. In [Plan mode](prompts.md) the agent researches, writes the plan and asks for your [review](reviews.md): you comment on its blocks, then approve it or ask for changes. A plan that is not done can also be reviewed from its details in the Work items tab.

Once you approve it, the session shows the plan as a card with the same choices as a task. Nothing starts until you choose.

## The Work items tab

Open it with **Work items** in the title bar (in its **…** menu when the window is narrow), with `Ctrl+G Ctrl+I`, or with the mark of a project in the Explorer.

- **To do**, **In progress**, **Later** and **Closed** separate what waits from what is done.
- **Runs with** is the [Agent chip](workspace/prompt.md#agent-and-model) of the new session. With **Use the defaults** on, it runs with the provider, model and reasoning effort of the session that proposed the item, else the [default provider](model-providers/readme.md) and its model. Turn it off to choose them, the agent prompt, the permission mode, review before commit and the sandbox, before you start.
- Filter by tasks, plans or [reviews](reviews.md#all-your-reviews), by project, or by text. **All projects** are those of the [space](spaces.md) the window shows.
- Select an item to read it, start it, mark it done, open its file, or remove it.
- Start several items at once: a spinner marks each while its worktree is made. A ready session opens if you still read its item; otherwise a notification offers it with **Show**.

In the Explorer, a project shows how many work items wait for you, and a dot while a session works on one.

## Files

Each work item is a Markdown file of its project, which you can read, edit and commit:

{.table}
| Kind | File | Status |
|---|---|---|
| Task | `.alta/tasks/yyyy-mm-dd-name-xxxx.md` | `pending`, `later` |
| Plan | `.alta/plans/yyyy-mm-dd-name.md` | `draft`, `approved`, `in-progress`, `done`, `blocked` |

The name of a task ends with four random characters, so that two branches of a project never create the same file.

```markdown
---
title: "Report why the model list is empty"
kind: gap
status: pending
created: 2026-10-07
summary: "The Models page shows nothing for a provider that failed. It should say why."
---

## Why
...
```

Plans written before plans had this header are listed too, from their title and their `- Status:` line.

A completed or dismissed task is deleted, and a completed plan is kept. **Settings > Work items** changes both, turns task proposals off, hides the cards, and chooses which way of starting comes first.

## From an agent

Agents use the same work items with `alta task` and `alta plan`, also in CodeAlta TUI, which has no cards and no tab:

```text
Show me the open tasks of this project and do the one about the model list.
```

```text
Mark the plan 2026-10-07-task-cards as done.
```
