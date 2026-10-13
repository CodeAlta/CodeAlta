---
status: done
---

# A permission mode change applies at once

Branch `fix/permission-mode-applies-at-once`, from `origin/main` a6cc1b3f (first written in a worktree from b062cf17, then moved here).

## Why

Today the mode chosen under the prompt only takes effect with the next Send, and the host refuses a change while a turn runs. A user who switches a session that keeps asking to Auto or Bypass still gets asked until the turn ends; the button already shows the new mode, so it looks broken.

## What changes for the user

- Choosing a mode under the prompt applies it to the session at once, also while a turn runs.
- The requests that wait for an answer when the mode changes are answered by the new mode: **Bypass** allows them all, **Accept edits** allows the file changes among them. Questions to the user (Claude's AskUserQuestion, `alta ask`) are never answered for them.
- From the change on, the running turn follows the new mode: what it asks next is allowed or asked by the new mode, in both directions (a turn that started in Bypass asks once the session is set to Ask).
- Claude Code is told at once (`set_permission_mode` on the live connection). If the CLI refuses it mid-turn, it is not restarted: CodeAlta answers what the CLI asks by the new mode, and the CLI gets the mode at the next turn as today.
- If the host cannot apply it (session not owned by the window, host closing), the button goes back to the mode in force and says why.

## Steps

- [x] Host: `SessionRuntimeService.SetOwnedPermissionModeAsync` accepts an active run (keeps the refusal for attachments it does not own and for transitions); saves the mode in the entry, the journal and the session as today.
- [x] Host: the decisions of a running send read the session's policy per request instead of the flags frozen at its start (`OwnedPermissionExecution`, `CreateSendDefaultPermissionHandler`); a send that started without a review execution reviews through the path of turns the owner did not send (`HandleOwnedDefaultAsync` / session card).
- [x] Host: on a change to a more permissive mode, answer the waiting owned permission requests of the session in the mailbox (`_ownedDeliveries`; Bypass all, Accept edits the `fileChange` ones); user-input forms and asks untouched; a concurrent user decision wins or loses cleanly.
- [x] Agent/Claude: `IAgentPermissionModeProvider` gets the change of a running run; `ClaudeCodeSession` sends `set_permission_mode` on the live connection mid-turn, no restart on refusal.
- [x] RPC `sessions.setPermissionMode` (epoch-checked, like `setRemoteControl`); the composer picker calls it at once, keeps the next-Send selection in step, refreshes the choices, rolls back and shows the refusal on failure.
- [x] Tests: replace those that lock "a run keeps the mode it started with" (`SessionOwnedRunBindingTests` 84/104, `AgentRuntimeTests` 1008) with the new rule; add tests for answering waiting requests (Bypass, Accept edits, user input untouched, race with a user decision), tightening mid-run, the Claude mid-turn switch and its refusal, the RPC, and the picker.
- [x] Docs: `doc/desktop.md` (composer button, Permission modes), `doc/runtime.md`, `doc/providers.md`, `site/docs/workspace.md`, `site/docs/model-providers.md`, `doc/desktop-ide-checkpoint.md`; statistics keep "the mode at the start of a run" (noted in `doc/statistics-facts.md`).

## Notes

- The tests that state "a run keeps the mode it started with" still hold: they cover the layers below, which keep it until the host applies a new mode (`ApplyPermissionModeNowAsync`, `FollowsSessionPolicy`).
- The choice notices of the composer are not shown anywhere; a refused mode is reported with a toast.
- Found in the end-to-end check: a turn of Claude Code is read in segments, and the segment after a tool call asked
  the CLI for the new mode again and, refused, restarted it, which lost the turn. A segment that sends nothing never
  restarts the CLI now (`EnsureConnectionAsync(mayRestartForMode)`); regression test
  `SegmentOfATurnThatRuns_DoesNotStartTheCliAgain_ForAModeItRefused`, checked to fail without the fix.
- Found while testing: a change made between turns was not sent to the CLI until the next prompt, so a turn the CLI starts by
  itself (a background command that ends) still asked in the old mode. The mode is now sent to the CLI whenever its process
  runs, during a turn or between turns (`ApplyPermissionModeNowAsync`).
- Found while testing: `ClaudeCodeTurnExecutor` had `SetRunningPermissionModeAsync` but did not declare
  `IAgentProviderPermissionMode`, so no mode chosen after a turn started ever reached the CLI (Bypass and Accept edits
  still worked, since the host answers by its policy; Auto did not). It now declares it, and its test calls it through
  the interface.
- Checked end to end with the protocol log: Ask first switched to Auto during a turn sends `set_permission_mode auto`,
  which the CLI accepts, and its next command runs without asking; Auto switched to Ask first between turns reaches the
  CLI at once, and the turn it starts after a background command asks; Bypass chosen while that request waits answers it.
- Checked end to end in the developer instance with Claude Code: a session in Ask first waiting on a `ping` permission,
  switched to Bypass mid-turn, had the question answered, ran the command and completed the turn.

## Not in this change

- Sessions created by agents following their creator's mode live (setting "Same as the session that creates them"): a separate change.
