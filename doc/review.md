# Reviews

The user reviews what an agent produced: a file (a plan, a document), the uncommitted changes of a checkout, or what a
branch changed. Comments are threads on lines, kept until the user resolves them, and sent to an agent in **rounds**
that carry a verdict; the agent answers each thread by its id. Optionally, every commit of a session waits for an
approved review.

| Part | Where |
| --- | --- |
| Model, store, anchoring, rounds, prompt, snapshots | `src/CodeAlta.Catalog/Reviews/` (`ReviewService`, `ReviewAnchoring`, `ReviewPromptComposer`, `ReviewTextSource.cs`) |
| `alta review`, and the request contract (`AltaReviewTarget`) | `src/CodeAlta.LiveTool/` (`BuiltInAltaCommandContributor.Reviews.cs`, `AltaAskContracts.cs`) |
| The ask queue a request waits on | `OwnedSessionAskService` (`src/CodeAlta.Orchestration/Runtime`), `SessionAsksRpc.cs` |
| Review before commit | `SessionRuntimeService` (note, guard), `AgentCommandGuard`, the `codealta_command` hook of `ClaudeCodeSession` |
| The page's service and UI | `src/CodeAlta/Desktop/Rpc/ReviewsRpc.cs` (`reviews`), `frontend/src/review/` (`useDiffComments`), the Changes tab |

## Model

A **target** is reviewed in one **checkout** (the project folder or a worktree): `file` (one side), `changes` (the
work tree against `HEAD`, untracked files included) or `branch` (the work tree against where the branch left its
base). A **thread** has a file, a side, a line range, the text of those lines (its **anchor**), a body, an optional
suggestion and replies:

```text
draft ──submit──▶ open ──agent replies──▶ addressed ──user resolves──▶ resolved
  ▲                 ▲                         │                           │
  └ edit / delete   └──── user replies ───────┘           user reopens ───┘
```

Only the user resolves; an agent's reply can only mark a thread addressed, and a draft stays the user's until it is
submitted. A **round** records the verdict (`approved`, `changes_requested`, `commented`), the threads it sent, the
session it went to and, for a diff, a **snapshot** of the work tree. A **request** is a review an agent asked for; it
lives only as long as the ask that carries it, so a review is never shown as requested by an ask that ended.

## Store

`ReviewService` keeps one JSON file per checkout in the state folder of the instance
(`<state>/reviews/<project id>/`), never in the checkout: there it would show in the very diff under review and be
committed by an agent's `git add -A`. Writes replace the file under one lock and a reader rereads a file whose stamp
changed, so the window and the `alta` commands of several sessions share one state. A file of a newer version, or one
that cannot be read, is never overwritten: its reviews are read-only. Sizes are bounded (threads per checkout, text,
replies, rounds). Deleting a project deletes its reviews; deleting a review or its plan cancels the asks that waited
for it.

## Anchoring

The host places threads; the page and Monaco never keep anchors. At each read and before a round, `ReviewAnchoring`
looks for the anchor in the current text of its side (trailing white space ignored): at its lines, else at the nearest
match. When the text changed (an agent revised the lines), the thread is **outdated** but stays near its text, so the
user finds its replies where they commented: on the most similar window of lines (word overlap), else between the
non-blank lines that surrounded it, else on its old lines. An outdated thread is still sent until resolved; resolved
threads are not followed.

## Rounds

`PrepareRoundAsync` places the drafts and open threads, writes the snapshot and composes the prompt without recording
anything; the round is recorded before the send, so an agent that answers at once finds its comments open, and undone
when the session does not get it. A send whose outcome is unknown keeps the round, so comments are never sent twice.
The prompt starts with `# Review of` (which the timeline and agents recognize), lists each thread with its lines and
the threads still open from earlier rounds, ends with what to do for the verdict, and is capped at 32 KiB.

`alta review request file|changes|branch` puts a request on the session's ask queue, under the rules of asks (one per
run; the agent ends its turn). The user's verdict comes back as the answer of that ask. A round the user submits on
their own goes to the session that asked for it, else to another session (queued when busy), else to a new session in
the checkout (in Plan mode for a plan file).

## Review before commit

The setting `[review] before_commit` and a per-session switch decide whether an agent may commit without an approved
review. Nothing of it is in the system prompt, whose text stays the same so the provider's prompt cache is kept:

1. **Fixed guidance** in the Default prompt says what to do when a note says the review applies.
2. **A note in the prompt.** When a run starts and what applies differs from what the agent was last told, the host
   adds a `<codealta-context>` host-context item to the user message; a compaction or a provider change makes the next
   prompt tell it again.
3. **`alta review status`** says it at any time.
4. **A guard.** `AgentCommandGuard` checks every shell command before any permission, in every permission mode, and
   refuses `git … commit` unless the last diff round is approved and its snapshot is the work tree now
   (`ReviewService.IsApprovedAsync`). Approved work may be committed in several commits, since a commit does not change
   the work tree; an edit after the approval needs a new review. Claude Code is guarded by a `PreToolUse` hook that the
   CLI runs before its own permissions, bypass mode included.

**Snapshots** (`GitSnapshot`) copy the index to a temporary file and run `git add -A` and `git write-tree` against it:
the real index, files and references are untouched.

The guard is a guardrail, not a sandbox: an alias, a script or `git -C` elsewhere can get past it, and agents that run
their own shell without asking CodeAlta (ACP agents) get only the guidance and the note. It also errs the other way
(`echo git commit` counts as a commit).
