# Reviews

The user reviews what an agent produced: a plan or any file, the uncommitted changes of a checkout, or what a branch changed. Comments are written on lines, kept until the user resolves them, and sent to an agent in **rounds** that carry a verdict. The agent answers each comment by its id. A setting and a per-session switch make every commit wait for an approved review.

| Part | Where |
| --- | --- |
| Model, store, anchoring, rounds, prompt, snapshots | `src/CodeAlta.Catalog/Reviews/` (`ReviewService`, `ReviewAnchoring`, `ReviewPromptComposer`, `GitReviewTextSource`, `GitSnapshot`) |
| `alta review`, `alta ask` without files | `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.Reviews.cs`, `BuiltInAltaCommandContributor.cs` |
| A request on the ask queue, its answer | `src/CodeAlta.Orchestration/Runtime/Prompts/AltaAskContracts.cs` (`AltaReviewTarget`), `OwnedSessionAskService`, `src/CodeAlta/Desktop/Rpc/SessionAsksRpc.cs` |
| The page's service | `src/CodeAlta/Desktop/Rpc/ReviewsRpc.cs` (`reviews`) |
| Review before commit | `SessionRuntimeService` (choice, note, guard), `AgentInputItem.HostContext`, `AgentCommandGuard`, `ClaudeCodeSession` (hook) |
| Window | `src/CodeAlta/frontend/src/review/`, the Changes tab, the editor, Work items, the ask panel; how comments live in the Monaco diff editor: [review-monaco-notes.md](review-monaco-notes.md) |

## Model

A **target** is what is reviewed in one **checkout** (the project folder, or a worktree of its repository):

| Target | Content | Sides |
| --- | --- | --- |
| `file` | One file, its path relative to the checkout folder | `new` only |
| `changes` | The work tree against `HEAD`, untracked files included; paths relative to the work tree, as the Changes tab lists them | `old` (`HEAD`), `new` (work tree) |
| `branch` | The work tree against where the branch left its base (its upstream, else the main branch of `origin` or of the repository) | `old` (the base), `new` |

A **thread** (`c<n>`, unique in its checkout, never reused) has a file, a side, a line range (`0-0` for the whole file), the text of those lines (its **anchor**), a Markdown body, an optional suggestion (the replacement of the lines; empty deletes them), replies, and a state:

```text
draft ──submit──▶ open ──agent replies──▶ addressed ──user resolves──▶ resolved
  ▲                 ▲                         │                           │
  └ edit / delete   └──── user replies ───────┘           user reopens ───┘
```

- Only the user resolves. An agent's reply marks a thread `addressed` (a reply with `addressed: false` answers without claiming a change). A user's reply, or an edit of the body, makes it `open` again.
- **Resolve addressed** resolves every addressed thread of a target; an **Approve** round does it for its target.
- A draft is the user's: `alta review list` does not show it, and an agent's reply leaves it a draft.

A **round** (`r<n>`) records the verdict (`approved`, `changes_requested`, `commented`), the overall comment, the session it went to, the threads it sent, the request it answered, and for a diff a **snapshot**: the git tree of the work tree when it was submitted (see below).

A **request** is a review an agent asked for (`alta review request`): the id of its ask, the session, the target. It is forgotten when a round answers it or the user cancels it.

## Store

`ReviewService` keeps one JSON file per checkout in the state folder of the instance: `<state>/reviews/<project id>/project.json` for the project folder, `worktree-<hash of the path>.json` for a worktree. Never in the checkout: the file would show in the very diff under review, and an agent's `git add -A` would commit it. The developer instance (`~/.alta/dev`) keeps reviews of its own.

- Writes replace the file (temporary file, then a move), under one lock of the service. A cached file is read again when its length or time stamp changes, so the window and the `alta` commands of several sessions share one state.
- A file of a newer version, or one that cannot be read, is never overwritten: its reviews are read-only (`ReviewState.ReadOnly`, `review.readOnly`). A hand edit is normalized on read (missing lists, ids below the ones in use).
- Limits: 500 threads per checkout (the oldest resolved one makes room first, else `limit`), 16 KiB per text, 50 replies per thread, 200 rounds kept, paths of plain segments inside the checkout.
- `Changed(projectId)` is raised after each change made through the service; `reviews.watch` turns it into one pending notice for the page.
- A checkout whose folder is gone keeps its file: `ListCheckouts` and `Overview` mark it missing, and the user deletes it (`DeleteCheckout`, from the reviews of Work items). Deleting a project deletes its reviews (`WorkspaceService.ProjectDeleted`). The Storage page counts the folder as `reviews`.

## Anchoring

The host places threads; the page and Monaco never keep anchors. At each read (`ListAsync`) and before a round is composed, `ReviewAnchoring.Relocate` looks for the anchor in the current text of its side (`IReviewTextSource`: the file for `new`, `git show <HEAD or base>:<path>` for `old`), with white space at the end of lines ignored:

1. The anchor is at its lines: the thread stays.
2. Else the match nearest its old place wins (the earlier one on a tie), and the thread moves there.
3. Else the thread keeps its lines and is **outdated**; it is still sent until resolved, with the text it was about.

Resolved threads are history and are not followed. New places are saved, unless the thread changed meanwhile.

## Rounds and the prompt

`PrepareRoundAsync` takes every draft and every open thread of the target, places them, writes a snapshot for a diff, and composes the prompt (`ReviewPromptComposer`) without recording anything; `RecordRound` records it before the send, so that an agent that answers at once finds its comments open: drafts become open, an approval resolves what was addressed, the request is forgotten. It returns what it changed, and `RevertRound` undoes it when the session does not get the round; a send whose outcome is not known keeps it, so the comments are not sent twice. A round for a new session is recorded before the session exists, and `SetRoundSession` names it once started. `CommitRound` records a round without the undo.

The prompt starts with `# Review of your changes: changes requested` (or `the changes of your branch`, or `` `<path>` ``), then the round number, the counts and the snapshot, the overall comment, one section per file with each thread (`### c12 · lines 15–18 (new) · suggestion`), its lines (a `diff` fence for a diff, with `+` for lines not in the old side; a quote for a file), its body, its suggestion and its replies, then the threads still open from earlier rounds, and a footer that says what to do for the verdict (answer with `alta review reply`; commit only what was approved; request the next review). It is capped at 32 KiB: snippets are shortened first, then threads are listed by id for `alta review list`. The timeline recognizes a prompt that starts with `# Review of`.

## Requests: `alta review` and the ask queue

`alta ask` asks questions only. `alta review request file|changes|branch` puts one request on the same queue of the session: an `AltaAskRequest` with a `Review` target (`AltaReviewTarget`: kind, path, title, message, project, checkout set by the host) and optional questions. The rules of asks apply: one per run, from a run of the window; the agent ends its turn.

When the user answers, the page sends the verdict (`SessionAskAction.Review`); `SessionAsksService` prepares the round from the store, passes its text to the owner (`OwnedAskAction.Review`), which writes the round then the answers in one prompt (`AltaAskAnswerMarkdownFormatter.Format(request, answers, review)`), and records the round when the dispatch is admitted (or its outcome is unknown). The owner forgets an ask once it is delivered or cancelled, so its capacity counts pending asks; the actions of an ask still pending are kept for replay.

A round the user submits from a review surface (`reviews.submit`) goes to:

1. the session that waits for this very review, as the answer of its request (`answer_in_session` when the request also has questions: the user answers them in the session);
2. another session, as a prompt queued when it is busy (`alta session send --queue-if-busy`);
3. a new session in the checkout, in Plan mode for a file under `.alta/plans/` (`SessionStarter` with `SessionStartModel.AgentPromptId` and `Worktree`).

The terminal app reviews a file request in its file view, with the verdict as one more question, and writes its line comments as threads; it has no diff view (`review.unavailable`).

## Review before commit

The setting `[review] before_commit` (Settings → Sessions) and the switch of each session (its local state, `review_before_commit`; null follows the setting; a child created by a session that reviews before commit gets `true`) decide whether the user reviews the changes of a session before an agent commits them. Nothing of it is in the system prompt or the instructions, which keep the same text, so the prompt cache of the provider is kept:

1. **Fixed guidance**: the *Review by the user* section of the Default prompt says what to do when a note says the review applies.
2. **A note of the prompt**: when a run starts (`SendOwnedBodyAsync`, which every owned send goes through), the host compares what applies with what the agent was last told (`RuntimeSessionEntry.ReviewNoted`, kept as `review_noted`) and, when they differ, adds an `AgentInputItem.HostContext("review", "<codealta-context>Review before commit: on. …</codealta-context>")`. The model reads it as text of the user message; `RenderUserInput` keeps it out of the user's text, and the raw input keeps it for the window, which shows a chip. A compaction (`CompactionCompleted`) and a change of provider make the next prompt tell it again. A switch flipped during a run is told with the next prompt: the guard applies at once.
3. **`alta review status`** reads it at any time.
4. **A guard**: `AgentCommandGuard` checks each shell command before any permission, in every permission mode. `SessionRuntimeService.CreateCommandGuard` refuses a command where `AgentGitCommands.IsCommit` sees `git … commit` while the review applies, unless `ReviewService.IsApprovedAsync`: the last diff round approved and its snapshot is the work tree now. The approved work may be committed in several commits (a commit does not change the work tree); an edit after the approval needs a new review. The built-in `shell_command` checks it, and Claude Code through a `PreToolUse` hook with the matcher `Bash|PowerShell` (`codealta_command`), which the CLI runs before its permissions, bypass included (live-tested).

**Snapshots** (`GitSnapshot.WriteTreeAsync`) copy the index of the checkout to a temporary file, then run `git add -A` and `git write-tree` with `GIT_INDEX_FILE` on it: the real index, the files and the references are untouched; git writes the objects of new contents, which its garbage collection removes later. Ignored files are not in a snapshot.

**Limits of the guard.** It is a guardrail, not a sandbox: an alias, a script, a `git -C` to another folder or a command that edits then commits can get past it, and providers that run their own shell without asking CodeAlta (the Codex CLI app server, ACP agents) get the guidance and the note only. It errs the other way too: `echo git commit` counts as a commit.

## The Changes tab

The **Review** toggle of the Changes tab turns review mode on for what the tab compares in the checkout it shows: the **Uncommitted changes** (`changes`) or the branch **Since** its base (`branch`); a commit has no review. It is on by itself while the review has comments that are not resolved or an agent asked for it, and **Review changes** (the command, the menu of a session, **Open review** of a request, **Open** of the composer status) opens the tab on the checkout of the session in review mode.

- Each side of a diff takes line comments through `useDiffComments` (`frontend/src/review/`), one line layer per side editor; in one column only the new side, and the old side's threads are listed above the diff with **Show side by side**. A file that is new or gone takes them on its one side, and the header of a file has **Comment on the whole file** and **Viewed** (the revision of the file; another revision clears it).
- The review bar says what is reviewed, where, how many files were viewed and who asked for the review; **Submit review** goes to the session that asked, else to one of the checkout's sessions or a new one. The side list has **Files** (with the number of comments of each file) and **Comments** (every thread, the cards of those on files the diff no longer shows).
- Keys, outside text fields: `c` comments the line of the caret, `n`/`p` go to the next or previous thread across files, `]`/`[` to the next or previous file, `v` toggles **Viewed**.
- The composer of a session that reviews before commit says **Changes waiting for your review** while its checkout has uncommitted changes, no request waits and no run is active.

## The page's service

`reviews` (`ReviewsService`): `list`, `add`, `update`, `delete`, `resolve`, `resolveAddressed`, `reply`, `viewed` (the revision a file of a diff was viewed at, which clears itself when the file changes), `submit`, `counts` (per checkout of a project), `overview` (the reviews of the projects of a space, for the **Reviews** kind of Work items, which the **Reviews** command and the eye of the review bar open through `ReviewsListOpenerContext`), `forgetCheckout`, and one `watch` channel (one pending notice; the page reads again). Every call names the epoch, the project id and the checkout (null for the project folder). `RpcChannelBudgetTests` counts the channels the services offer against the page's budget of 35.
