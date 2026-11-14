---
title: Adaptive reviews started by the session, not behind its back (#448)
status: done
created: 2026-11-14
summary: A session on Adaptive is told that critics review its code changes, starts the review itself with `alta adaptive review` when a change is complete and before any push or merge, and gets the verdict back as a prompt; CodeAlta only reminds a session that ends a turn with code nobody reviewed.
---

# Adaptive reviews started by the session, not behind its back (#448)

Today CodeAlta starts the critics of a session on Adaptive by itself, when the first run of a user turn ends, and the session is told nothing. On real work that goes wrong (session `01a1d9c7`, the review of a pull request of `xoofx/ultra`, 2026-11-14):

- the turn had only paused for a sub-agent, so the critics reviewed work that was not finished;
- the session had checked out a branch, and the critics were given that branch as "the change";
- the session pushed two commits while the findings waited, because nothing told it a review was running;
- the three commits it really wrote were merged without any review: a run started by a sub-agent's answer or a background job is never reviewed.

The pattern that was measured stays: critics of another model family, read-only with tools, given the request and the diff, one revision (48 of 51 HARD-51 tasks for about 40% of the cost of Opus, November 2026). What changes is who starts it and when: the session does, knowingly, and sequences it with its own sub-agents, jobs, pushes and merges.

When this is done:

- a session on Adaptive knows it: its level, its critics, and that it must have its code reviewed before it reports it or takes it out of the folder;
- it starts the review with one command and gets the verdict back as a prompt, approval included;
- CodeAlta starts no review by itself; it reminds, once, a session that ends a turn with code that was not reviewed;
- the work of every run of a request is covered, not only its first run.

## What changes

### 1. The session is told, with its prompt

A note of the prompt (`<codealta-context>`, as "Review before commit" is told), given when the session goes on Adaptive, when its level or critics change, and when it leaves Adaptive. Not the system prompt: its text stays the same for the cache of the provider, and the state of a session changes during its life.

For a level with critics:

> Adaptive review: on, level `high`. Critics of another model (`claude-code:sonnet@high`) review the code you change. You start that review and you decide when: CodeAlta starts none by itself.
> - Have every change of code reviewed before you report it, and before any step that takes it out of this folder or is hard to undo: a push, a merge, a release, a deployment, the deletion of a branch. Documentation alone, a question, or a commit need no review.
> - When the change is complete and its checks pass, run `alta adaptive review`. The critics get the request of the user and the diff of your change, read the code and run tests, read-only. The command returns at once.
> - Their verdict comes back to you as a prompt that starts with `[CodeAlta Adaptive]`, approval or findings. Until then, do not push, merge or publish the change, and do not edit the files under review; end your turn when nothing else is left to do: that prompt starts you again.
> - Check each finding yourself, fix the real ones and keep the tests passing. These fixes need no second review. Then do what waited for the review.
> - Sub-agents you start yourself are not the critics: their answer does not replace this review.

For a level without critics, the same rule with a recheck by the session itself (each requirement of the request, with a test or a run) in place of the review.

### 2. `alta adaptive review` and `alta adaptive status`

- `alta adaptive review [--base <commit>] [--message <text> | --stdin]`, for the calling session:
  - the diff goes from the last reviewed state of the request, else from the start of the request, to the files as they are now; `--base` names the commit the change starts from, when the folder also holds changes that are not the session's (a branch it checked out, a pull);
  - the notes say what the change is for, when the request of the user does not (a fix found while reviewing someone else's pull request);
  - it starts the critics of the level, as today (`AdaptiveCritics`), and answers at once with the critics and the files under review, so that the session sees what is reviewed;
  - it refuses, with the reason, a session that is not on Adaptive, a level without critics, a change without code, and a second review while one runs.
- The verdict is one host prompt, steered into a running turn or queued, never dropped: the findings of each critic, or the approval, or why no critic could review (then the session rechecks itself).
- `alta adaptive status`: the level, the critics, the review that runs, and the code that changed since the last review.
- The brief of a critic says that the review comes before the rest of the request (a commit, a push, a report), which is then no problem to report.

### 3. CodeAlta reminds, and starts nothing

When a run of a session on Adaptive ends:

- nothing happens while a review runs, while a prompt waits in its queue, or while work of its own goes on (a background job, a sub-agent): the session is not done;
- when code changed since the last review, CodeAlta sends one prompt, **Adaptive reminder**: the files, `alta adaptive review` (or `--base`), and "say in one line why" when nothing of it needs a review. Once per state of the files;
- a level without critics gets the recheck prompt at that point, as today.

A request is followed over all its runs, from the prompt of the user to the next one: a run started by a sub-agent's answer, a job or a reminder continues it, with the same request and the same starting state of the files.

### 4. Measure with the user's subscriptions

Codex and Claude Code, through `altacli run --isolated`, a few runs each (the number is said before they start). Two scenarios with a local bare git remote, on a level whose turns run on Codex with a Claude Code critic, then the reverse:

- `fix-then-push`: fix a bug, commit, push. Passes when the review was requested and its verdict received before the push.
- `wait-then-fix`: start a background job, end the turn, fix when it reports. Passes when the review covers the fix and no reminder was needed.

Counted per run: review requested (yes/no), before or after the push, reminders, findings fixed, the hidden test of the fix. A full HARD-51 is out of reach on subscriptions (its sandboxes cannot use the sign-ins); a few of its tasks are run locally only if the two scenarios leave a doubt on quality.

## Steps

- [x] 1. Branch `feature/adaptive-review-by-the-session` from `main`.
- [x] 2. `AdaptiveTurnCoordinator`: a request followed over all its runs; the review as a request (`RequestReviewAsync`), its verdict as a host prompt; the reminder at the end of a run; no review started by the host. Tests in `AdaptiveTurnTests`.
- [x] 3. The note of the prompt (`AdaptiveNoted` in the local state of the session), told when it changes. Tests.
- [x] 4. `alta adaptive review` and `alta adaptive status` (`BuiltInAltaCommandContributor.Adaptive.cs`), allowed in a sandbox like `alta job`. Tests.
- [x] 5. The **Adaptive reminder** provenance in the timelines (desktop, statistics names).
- [x] 6. Documentation: `doc/runtime.md` (Adaptive turns), `site/docs/model-tiers.md` (Adaptive).
- [x] 7. Build, `dotnet test`, `npm test`, `lunet build`.
- [x] 8. The two scenarios on Codex and Claude Code; results below; adjust the note where the runs show it is not followed.

## How it is verified

- [x] Unit tests: a turn that waits for a job is not reminded; the run of the job's result is; a review asked covers the runs since the request; `--base` narrows the diff; the verdict reaches a busy session; approval and failure are told.
- [x] `fix-then-push` and `wait-then-fix` on both provider orders: the review is asked before the push in every run, or the misses are explained and the note changed.
- [x] Every .NET test, `npm test` and `lunet build` pass. *One test of `CodeAlta.Browser.Tests` needs Firefox closed, and it was open; two tests failed only in the full run and pass alone (`CredentialBatchReadTests`, `ExternalSessionFollowerTests`).*

## Decisions

| Decision | Choice | Why |
| --- | --- | --- |
| Who starts a review | The session, with a command | It knows when its change is complete and what comes next (a push, a merge); the host guessed wrong on the first real multi-run work |
| What stays the host's | The critics (models, read-only tools, brief, verdict line), the delivery of the verdict, a reminder | That part was measured in November 2026; a brief each model writes itself would drift from it |
| Where the session is told | A note of the prompt, not the system prompt | The state changes during the life of a session, and the system prompt stays cached |
| A turn that ends with unreviewed code | One reminder prompt; never a review started by the host | The user asked that nothing be coordinated without the session knowing; the October trial showed that a prompt alone can go unfollowed, so the reminder is the net |
| Approval | Told as a prompt too | The session waits for the verdict before it pushes |
| What "the change" is | Since the last review or the start of the request; `--base` when the session says otherwise | A checkout is not the session's change, and only the session knows |
| `--read-only` on `alta session create` | Not in this plan | Not needed for this; a follow-up task if wanted |

## Risks

- **The note is not followed:** the October trial found that agents rarely act on a prompt that asks them to change their own model. Asking for a review is closer to what they already do (sub-agents, jobs), and the reminder catches a miss, but only after the turn: a push made in that turn is not undone. Step 8 measures it.
- **A verdict that costs a turn:** an approval now starts a run. It is short, on a warm cache.
- **A provider that runs its own loop** tells the host no prompt of a run it starts: the end of such a run is taken as the continuation of the request.

## Results

Six runs of `altacli run --isolated --tier adaptive:medium`, on 2026-11-14, on a small JavaScript project with a local bare remote (scripts and events in the scratchpad of the session, not in the repository). Two orders: GPT-6.1 Sol `medium` for the turns with Claude Sonnet 5.5 as the critic (the user's level), and Sonnet 5.5 for the turns with Sol `low` as the critic.

| Run | Turns | Review asked by the session | Before the commit or push | Reminders | What the critic said | Time |
| --- | --- | --- | --- | --- | --- | --- |
| `fix-then-push` 1 | Sol | yes | yes | 1, wrong | a real defect (half-cent rounding), and "not committed or pushed" | 2 min 17 s |
| `fix-then-push` 2 | Sol | yes | yes | 0 | the same defect, nothing else | 1 min 45 s |
| `fix-then-push` 1 | Sonnet | yes | committed first, pushed after | 0 | approved | 29 s |
| `fix-then-push` 2 | Sonnet | yes | committed first, pushed after | 0 | approved | 28 s |
| `wait-then-fix` | Sol | yes | yes | 0 | approved | 1 min 58 s |
| `wait-then-fix` | Sonnet | yes | yes | 0 | approved | 1 min 58 s |

- **The note is followed.** Every session asked for the review itself, waited for the verdict, and pushed after it. Sol's first words were "The required adaptive review must finish before I push".
- **The first run changed three things**, before the five others:
  - the critic reported "the change is not committed or pushed" as a problem: its brief now says the review comes before those steps;
  - Sol gave the work to a sub-agent, which revised the change after the verdict, and the session was then reminded of that revision: a revision now counts once the sub-agents and jobs of the session have ended;
  - the sub-agent tried `alta adaptive review` itself: the note says a sub-agent cannot, and the refusal tells it to report to its parent.
- **`wait-then-fix` is the shape of the session that started this work**: a turn that ends while a job runs is not reminded, the run of the job's result makes the change, has it reviewed, and pushes after the approval.
- **Not measured:** the quality on HARD-51 (its sandboxes cannot use the sign-ins of the subscriptions), a session that ignores the note (none did in six runs, so the reminder was only seen through the wrong one of run 1), and long sessions after a compaction (the note is told again then).
