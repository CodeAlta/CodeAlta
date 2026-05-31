---
title: Sessions and Delegation
---

# Sessions and Delegation

A CodeAlta session is a durable work unit with provider/model state, prompt history, queue state, a session journal, and timeline projections. Sessions can be global or project-scoped.

## Global vs project sessions

- **Global sessions** are useful for coordination across projects: planning, triage, comparing work, or creating project-specific child sessions.
- **Project sessions** are scoped to one project. They can attach project files, see same-project session context, and use project-local configuration from `<project>/.alta/config.toml`.

The sidebar keeps running sessions visible even when their tab is closed. Closing a tab does not stop active work, and unsent session drafts are saved under `~/.alta/saved_prompts/`.

> [!NOTE]
> Closing a tab only closes that view. Check the sidebar for running sessions before assuming work has stopped.

## Starting and reopening work

Open a project with `Ctrl+O`, select a provider/model/reasoning combination, and send a prompt. CodeAlta stores provider-independent session journals under `~/.alta/sessions/yyyy/mm/dd/<session-id>.jsonl`.

When reopening an existing session, CodeAlta restores local history before provider initialization has to finish. A ready compatible provider can resume or switch a CodeAlta-owned local session while it is idle; provider-native continuation state is reused only when it is safe.

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

Press `F11` or click the compact button beside the provider/model/reasoning selectors to compact an idle started session. Manual compaction uses the session's current provider/model/reasoning configuration and emits visible start/completion notices in the timeline.

Local compaction targets a smaller post-compaction context by default so long sessions can continue without immediately hitting the context limit.

## Agent delegation and self-inspection

CodeAlta-managed agent-runtime sessions include an in-process `alta` live tool, but it is not a command surface that you normally type into the terminal. It is a tool the selected agent can use when your prompt asks it to inspect CodeAlta state, coordinate with other sessions, or delegate work.

Think of it as CodeAlta giving the agent a safe, scoped way to ask the host questions such as:

- which projects are known or currently open;
- what sessions already exist for this project;
- which model providers and model refs are available;
- whether a related session has finished and what its final result was;
- how to create a child session for another project, provider, model, or reasoning effort;
- how to schedule a later reminder prompt for itself or another session.

## Prompting for delegated work

Use the UI delegation shortcut (`F7`) for an explicit delegation flow, or ask the current agent to create and coordinate child sessions for you.

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

Agents can also ask the live tool to schedule in-process reminder prompts with `alta reminder create --duration <seconds> --content ...`. Reminders default to the calling session, can target another session with `--session <session-id>`, can repeat with `--repeat <count>`, and can be inspected or removed with `alta reminder list` and `alta reminder delete <reminder-id>`.

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

The agent may use the live tool to gather this information, but the important user workflow is the prompt: ask for the coordination outcome you want, not for individual low-level commands.

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
