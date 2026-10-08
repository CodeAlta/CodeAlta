---
title: Sessions and Delegation
---

# Sessions and Delegation

A CodeAlta session is a durable work unit with provider/model state, prompt history, queue state, a session journal, and timeline projections. Sessions can be global or project-scoped.

## Global vs project sessions

- **Global sessions** are useful for coordination across projects: planning, triage, comparing work, or creating project-specific child sessions. CodeAlta Desktop calls them **Chats** and lists them first in the sidebar.
- **Project sessions** are scoped to one project. They can attach project files, see same-project session context, and use project-local configuration from `<project>/.alta/config.toml`. A project session works in the project folder, or in its own [git worktree](worktrees.md).

The sidebar keeps running sessions visible even when their tab is closed. Closing a tab does not stop active work, and unsent session drafts are saved under `~/.alta/saved_prompts/`.

> [!NOTE]
> Closing a tab only closes that view. Check the sidebar for running sessions before assuming work has stopped.

## Starting and reopening work

Open a project with `Ctrl+O`, select a provider/model/reasoning combination, and send a prompt. CodeAlta stores provider-independent session journals under `~/.alta/sessions/yyyy/mm/dd/<session-id>.jsonl`.

When reopening an existing session, CodeAlta restores local history before provider initialization has to finish. A ready compatible provider can resume or switch a CodeAlta-owned local session while it is idle; provider-native continuation state is reused only when it is safe.

In CodeAlta Desktop, click a web link in an assistant message to open it in your default browser. You can also focus the link with Tab and press Enter, or use Cmd-click on macOS, Ctrl-click, or middle click.

## Busy sessions and queues

If a session is busy, `Enter` queues your prompt instead of losing it. The waiting list appears above the status line.

Queued prompts can be:

- edited before they run;
- repeated;
- deleted;
- cleared with `F10`;
- converted to live steering when the provider supports it.

Use `Ctrl+Enter` with an empty draft to steer the first queued prompt immediately when queued work exists.

## Steering

Steering lets you adjust a running turn without waiting for completion.

```markdown
Focus on the parser only. Do not refactor unrelated files.
```

If no provider run is active, CodeAlta falls back to a normal send. If the provider does not support live steering, CodeAlta preserves the prompt and queues it for the next turn.

## Compaction

Press `Ctrl+F11`, use `/compact`, or click the compact button beside the provider/model/reasoning selectors to compact an idle started session. Manual compaction uses the session's current provider/model/reasoning configuration and emits visible start/completion notices in the timeline.

Local compaction targets a smaller post-compaction context by default so long sessions can continue without immediately hitting the context limit.

## Long instructions

When you give an agent many instructions at once, such as a list of tasks or numbered steps, it first writes them down in a file under `~/.alta/scratchpad/`. A long session compacts its context; the file keeps your instructions as you wrote them, and the agent reads it again after a compaction and before it reports the work as done.

The file is the agent's own record. It is not part of your project and is never committed.

## Branches

In a git repository, an agent that is about to commit on the main branch first creates a branch named for the work, such as `fix/quoted-keys`, and commits there. The main branch keeps its history, and the branch is ready for a pull request.

- Work that is not committed gets no branch.
- On another branch already, or in a [worktree](worktrees.md), the agent stays on that branch.
- The agent does not merge the branch, push it or open a pull request by itself. Ask for it, or use the [pull request button](issues.md#create-a-pull-request).

Say "commit on main" or "no branch" in your prompt to keep the work where it is. The instructions of the repository, such as `AGENTS.md`, can say it for every session.

## Agent delegation and self-inspection

CodeAlta-managed agent-runtime sessions include an in-process `alta` live tool for any configured provider, but it is not a command surface that you normally type into the terminal. It is a tool the selected agent can use when your prompt asks it to inspect CodeAlta state, coordinate with other sessions, or delegate work.

Think of it as CodeAlta giving the agent a safe, scoped way to ask the host questions such as:

> [!NOTE]
> This page covers daily session and delegation workflows. For the full command-group atlas and prompt recipes, see [Advanced Agent Workflows](advanced-agent-workflows.md).

- which projects are known or currently open;
- what sessions already exist for this project;
- which model providers and model refs are available;
- whether a related session has finished and what its final result was;
- how to create a child session for another project, provider, model, or reasoning effort;
- how to schedule a later reminder prompt for itself or another session;
- how to update sticky Markdown notes in the sidebar while it works.

## Sub-agents

An agent decides by itself when sub-agents help. A sub-agent is a child session with a context of its own, which works in parallel with its parent.

- When the work has independent parts (several modules to look into, a research beside an implementation, a review of a diff), the agent gives each part to a sub-agent and keeps the decisions and the final answer.
- It works directly when the task is small or does not split.
- It gives simple work a lower reasoning effort.
- A sub-agent does its work itself: it does not pass it on to a single sub-agent of its own.

Say so when you want it otherwise: "do not use sub-agents", or "use a sub-agent for each folder".

In CodeAlta Desktop:

- The sub-agents of a session are listed under it in the sidebar, and the session shows how many it started.
- A sub-agent is an ordinary session: open it to follow its work, or to write to it.
- What agents send each other is shown as an **Agent message**, in its own color: the prompt a sub-agent receives, and the answer it gives back. The message names the session it comes from. Click the name to open that session.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-sub-agents.webp" alt="A session with three sub-agents listed under it in the sidebar and the answer of one of them in its timeline" loading="lazy">
  <figcaption class="small text-secondary mt-2">Three sub-agents under their session in the sidebar, and the answer of one of them as an agent message.</figcaption>
</figure>

## Prompting for delegated work

You can also ask the current agent to create and coordinate child sessions for you.

Examples:

```markdown
Create two child sessions for the current project with the same prompt.
Use Codex high reasoning for one and Anthropic Sonnet for the other.
Ask both to propose the smallest safe fix, then compare their final
answers when they report back.
```

```markdown
Start a low-reasoning child session for the current project to inspect
the latest test failure logs while you continue reading the parser code
here. Have the child report only the likely failing assertion and
relevant files.
```

```markdown
Create a project-scoped session for `../OtherRepo` using the fastest
available model. Ask it to summarize the public API shape and send the
summary back here.
```

For parent/child delegated work, CodeAlta uses a notification-based pattern: the child final reply or child-run error is forwarded back to the parent session automatically. The parent agent should yield instead of repeatedly polling while waiting.

In CodeAlta Desktop, an agent can show you what changed with `alta diff show`: it opens the [Changes tab](workspace.md#changes-desktop) of the project, and `--file <path>` selects a file in it. It can also show you a file with `alta editor open --file <path> --line <n>`, which opens it in the [code editor](workspace.md#code-editor) of the project.

Agents can also ask the live tool to schedule in-process reminder prompts with `alta reminder create --duration <seconds> --content ...`. Reminders default to the calling session, can target another session with `--session <session-id>`, can repeat with `--repeat <count>`, and can be inspected or removed with `alta reminder list` and `alta reminder delete <reminder-id>`. In the TUI, use the compact clock button in the prompt bar or `/reminder` (`Ctrl+G Ctrl+D`) to create, delete, and edit reminder messages for the selected session.

Agents can update the Notes of the current session (the sidebar Notes panel in the TUI, the Notes window in the desktop app) with `alta notes set --stdin`, read it back with `alta notes get`, and clear it with `alta notes clear`. Notes are session-scoped sticky Markdown for plans, checklists, and progress summaries; switching tabs shows the selected session's notes, and reopening a session restores the latest notes set/clear event from that session's journal.

## Prompting for CodeAlta self-inspection

You can also ask a session to inspect CodeAlta-managed project/session state before acting. This is useful when you have many open sessions, want to recover prior context, or want to compare work across providers.

Examples:

```markdown
Look at the 10 most recent sessions for this project and tell me which
ones modified provider configuration docs.
```

```markdown
Find the child sessions created from this session and summarize their
final answers, grouped by model.
```

```markdown
Which enabled providers can run a high-reasoning model for this project?
Pick two good candidates for a comparison run and explain the tradeoff
before creating sessions.
```

```markdown
Before changing files, inspect the recent CodeAlta sessions for this
project and check whether a recent session already investigated the
plugin startup issue.
```

The agent may use the live tool to gather this information, starting with `alta session current` when it needs its own session id, but the important user workflow is the prompt: ask for the coordination outcome you want, not for individual low-level commands.

## Comparing models on the same task

A powerful workflow is to ask CodeAlta to fan out the same task to several providers or reasoning settings, then bring back a comparison.

```markdown
Run the same investigation in three child sessions: Codex high,
Copilot high, and Anthropic high. Give each child the same task and ask
them not to edit files. When all replies arrive, compare correctness,
risk, and recommended next step.
```

This works best when you ask for a bounded result from each child: summary, relevant files, proposed patch outline, test command, or risk assessment. Let one parent session synthesize the answers before you choose what to apply.

> [!TIP]
> Give delegated agents a narrow expected output, such as “do not edit files; return likely cause, files inspected, and one recommended test.” Bounded child results are easier for the parent session to compare.

## Scope, visibility, and provenance

Behind these workflows, CodeAlta records scope and provenance:

- global and project-scoped sessions can inspect reachable CodeAlta projects and sessions through host-managed live-tool APIs;
- project context is still used for defaults, child-session placement, and attribution;
- sessions created or controlled by agents/plugins carry `createdBy` and `submittedBy` metadata so sidebars and timelines can reconstruct parent/child relationships after restart.
