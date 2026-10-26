---
title: "Word the refusals of the write guard for a sandboxed session"
kind: problem
status: pending
created: 2026-10-26
summary: "In a sandboxed session, a refused write_file tells the agent that shell_command is turned off, which is false there, and the refusal text is repeated twice in the result."
---

## Why

Seen on macOS on 2026-10-26, from the worker of a `workspace` sandbox (`altacli --sandbox-worker`), and the same on every platform since the worker runs the same guard:

```text
ERROR: write_file was refused: '<project>/.vscode/tasks.json' is in '.vscode', which git, CodeAlta or a coding agent reads as settings or code (hooks, plugins, MCP servers, prompts). `shell_command` is turned off for this session: it does not write there.write_file was refused: ...(same text again)
```

The session has `shell_command`, so the agent is told something false, and may stop using the shell. The text also appears twice: once as the error and once as the text item of the result.

## What to do

- Give `AgentWriteGuard` the reason it applies (no shell, or the sandbox) and word its refusals for each, for example "the sandbox keeps it read-only" for a sandboxed session.
- Keep the refusal once in the result of the worker.
- A test that a sandboxed `write_file` to `.vscode/tasks.json` is refused without naming `shell_command`.

## Where

`src/CodeAlta.Agent/Runtime/Tools/AgentWriteGuard.cs` (lines 100-162), `src/CodeAlta.Sandbox/Worker/SandboxWorker.cs` (the guard it builds, line 122), the result of a refused tool in the worker.
