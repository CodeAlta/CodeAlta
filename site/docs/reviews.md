---
title: Reviews
---

# Reviews

You review what an agent produced the way you review a pull request: comment on lines, then send all your comments at once with a verdict. The agent answers each comment, revises its work, and asks for your next review. You can review:

- a **plan** or any Markdown file, on its rendered blocks;
- any **text file**, on its lines;
- the **uncommitted changes** of the project folder or of a [worktree](worktrees.md), or what a branch changed since its base.

With **Review before commit** on, agents wait for your approval before they commit.

## Comment

- **On a plan or a Markdown file**: hover a block and click **+** in its margin. Shift-click another block for a range. **Suggest edit** proposes new text for the block, and **Delete block** proposes to remove it.
- **On the source of a file**: click **+** in the margin of a line, or drag over a range. In a diff, both the old and the new lines take comments.
- **Suggest**, in the comment editor, starts a suggestion with the text of the lines: edit it into the text you want, and the agent receives it exactly.
- `Ctrl+Enter` saves a comment and `Esc` cancels it.

A comment is a **draft** until you submit the review, and drafts survive a restart. A comment follows its lines when the file changes. When its lines themselves change, it stays on the lines that took their place and is marked **outdated** until the agent answers it.

The review bar above the file counts the comments, jumps to each one, and has **Submit review**.

In a rendered plan or Markdown file, `Ctrl+F` (`Cmd+F` on macOS) finds text: `Enter` and `Shift+Enter` go to the next and previous match, `Alt+C`, `Alt+W` and `Alt+R` match case, whole words or a regular expression, and `Esc` closes it.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-review-plan.webp" alt="A plan of SharpYaml under review in CodeAlta Desktop: a draft comment under a paragraph, a diagram of the plan, and below the question of the agent with the decision written, and Request changes chosen" loading="lazy">
  <figcaption class="small text-secondary mt-2">A plan asked for review in Plan mode: a draft comment on a block, the question of the agent with your answer, and the verdict.</figcaption>
</figure>

## Review the changes

In CodeAlta Desktop you can review the changes of a project or a worktree without waiting for an agent to ask. Open them in review mode:

- **Review** in the header of the [Changes tab](workspace/code.md#changes) reviews what the tab compares: the **Uncommitted changes**, or all the changes of the branch since its base (not a single commit of the history);
- **Review changes** in the menu of a session, or `/review_changes`, opens the Changes tab on the folder or worktree the session works in;
- **Open** beside **Changes waiting for your review**, above the prompt of a session (see [Review before commit](#review-before-commit)).

The Changes tab stays in review mode while comments are not resolved or an agent waits for your review. Click **Review** again to leave it.

In review mode:

- Comment on the lines of each side of a diff. Inline, only new lines take comments; comments on old lines are listed above the diff, with **Show side by side**.
- **Comment on the whole file**, in the header of a file, comments on the file itself.
- **Viewed**, in the same header, marks a file you have read; the review bar counts them (*3/7 files viewed*). A file that changes again loses the mark.
- Beside the files, **Files** lists the changed files with their number of comments, and **Comments** lists every comment, including those on files that no longer have changes.

{.table}
| Key (outside a text field) | What it does |
|---|---|
| `c` | Comments the line of the caret. |
| `n` / `p` | Goes to the next or previous comment, across files. |
| `]` / `[` | Goes to the next or previous file. |
| `v` | Marks the file **Viewed**, or not. |

The review bar says what is reviewed, where, how many files you viewed and which session asked for the review. **Submit review** sends it to that session; otherwise you choose one.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-review-changes.webp" alt="CodeAlta Desktop with a SharpYaml session at the left and its Changes tab in review mode at the right: a comment on a new line of the diff, marked addressed, with the answer of the agent under it" loading="lazy">
  <figcaption class="small text-secondary mt-2">The changes of a session in review mode: a comment on a line of the diff, and the answer of the agent under it.</figcaption>
</figure>

## Submit a review

**Submit review** sends every draft, and every comment still open from earlier rounds, as one message:

{.table}
| Verdict | What the agent does |
|---|---|
| **Approve** | Goes on: commits the approved changes, or carries the plan out. |
| **Request changes** | Revises its work for your comments, then asks for your next review. |
| **Comment** | Answers your comments, without a verdict. |

You can add an overall comment and choose the session that receives the review: a session waiting for it gets it at once, another gets it after its current work, and **New session** starts one (in Plan mode for a plan). The review shows in the session as a compact card.

## Answers and resolving

The agent answers each comment under it, and the comment is marked **addressed**. You decide when a comment is done:

- **Resolve** closes a comment; **Reopen** opens it again;
- **Resolve addressed** closes every comment the agent addressed;
- **Approve** closes the addressed comments of what you approve;
- a reply of yours, or an edit of your comment, asks the agent again.

To end a review whatever its state, click the trash of the review bar, or **Delete review** in Work items, and confirm: its comments, replies and rounds are deleted, and an agent waiting for it is told it was cancelled. A plan that is removed, or deleted when done, takes its review with it.

## Requests from an agent

An agent can ask for your review of a file or of its changes. In Plan mode it always does, for its plan:

- a **file** opens in place of the timeline, with the agent's questions, if any, and the verdict where you write the prompt;
- **changes** show a card with **Open review**, which opens the Changes tab on its checkout.

When you submit, the review and your answers go back to the agent in one message.

## Review before commit

When **Review before commit** is on, agents never commit by themselves. When their work is done, they ask for your review of the changes and stop. They commit once you approve, and only what you approved: a change made after your approval needs a new review. CodeAlta refuses an unapproved commit, whatever the permission mode of the session.

- **Settings > Sessions > Reviews > Review changes before commit** turns it on for every session.
- The form of the **Agent** chip under the prompt has a **Review before commit** row: **On**, **Off**, or the setting (marked **Default**). Change it at any time; an eye on the chip shows it is on. The agent is told with its next prompt.
- The form of a new session has the same row.

While it is on, the line above the prompt says **Changes waiting for your review** when the session has uncommitted changes, nothing runs and the agent has not asked yet: click **Open**. Whether it is on or not, the same line shows your unsubmitted draft comments on the session's folder or worktree (*2 draft comments*), with **Open**.

In CodeAlta TUI, agents do not commit either: they leave the changes uncommitted and tell you. Review them in CodeAlta Desktop.

## All your reviews

**Work items** lists the reviews beside the tasks and plans: choose **Reviews** there, run **Reviews** from the command palette, or click the eye of a review bar.

- **To do** holds what waits for you: a review an agent asked for, comments the agent answered, drafts you have not submitted, and reviews whose file or worktree is gone, waiting for you to delete them.
- **In progress** holds what waits for the agent: comments it has not answered yet.
- A plan under review shows an eye in the list.
- Select a review to see its request, comments and last verdict. **Open review** opens it, **Delete review** ends it, and **Reload** reads the plans and reviews again.

Reviews are kept by CodeAlta, outside your repository: they never show in the changes you review and are never committed.

## Agents and `alta review`

Ask in plain words, such as "Ask for my review of the changes before you commit them." Agents use `alta review`, in CodeAlta Desktop and CodeAlta TUI:

```text
alta review request file .alta/plans/2026-10-20-x.md --stdin
alta review request changes
alta review request branch
alta review status
alta review list
alta review reply --stdin
```

A review request can carry questions too; `alta ask` only asks questions. CodeAlta TUI reviews a file with line comments and a verdict question; reviews of changes need CodeAlta Desktop.
