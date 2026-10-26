---
title: Code Mode
---

# Code Mode

With Code Mode, an agent can write a short JavaScript program that calls its tools, instead of calling them one by one. Loops, filters and calls in parallel then run without a round trip to the model, and only what the script returns comes back to it: fewer turns, fewer tokens, and work that keeps going while the model waits.

Code Mode is off by default. When it is on, agents keep all their tools and get two more: `exec`, which runs a script, and `wait`, which waits for a script that goes on in the background.

## Turn it on

In CodeAlta Desktop, **Settings > Code Mode** has **Mode**:

- **Off**: agents call their tools one at a time, as usual.
- **On**: agents also get `exec` and `wait`.
- **Only**: agents get `exec` and `wait` in place of their tools, which they call from scripts. They still look at images and ask you questions directly.

It is saved in `~/.alta/config.toml`:

```toml
[code_mode]
mode = "on"
```

A session takes the change with its next prompt. It applies to the sessions of the window, to the sessions agents create, and to CodeAlta TUI. With `altacli`, `--code-mode` turns it on for `run` and `acp` (`--code-mode=only` for **Only**), whatever the file says.

Agents of Claude Code get `exec` and `wait` through CodeAlta's MCP server, beside `alta`.

## What a script can call

A script is the body of an async function: it uses `await` and `return`, in plain JavaScript (no TypeScript, no `import`). It has no file, network or timer access of its own. Everything it does is a call of a function of the session:

| Functions | What they are |
| --- | --- |
| `tools.<name>(args)` | The tools of the session, such as `tools.read_file({ path })` or `tools.shell_command({ command })` |
| `mcp.<server>.<tool>(args)` | The tools of the [MCP servers](../plugins/mcp.md) of the session |
| `alta.<group>.<command>(args)` | The [`alta` commands](delegation.md#what-agents-can-use), such as `alta.session.list({ limit: 20 })` |
| `codemode.help()`, `codemode.search({ query })`, `codemode.describe({ path })` | Find functions and read their TypeScript declarations |
| `agent(prompt, { tier, model, title, schema })` | Start a session of its own for a part of the work, and get its final answer, or the JSON value it answered with `schema`. It is a sub-agent of the session, which you can open; 8 run at once, 50 per script |
| `session({ tier, title })` | Keep a session for several prompts: `run(prompt)` sends one, `close()` ends it |
| `parallel(functions, { limit })`, `pipeline(items, ...stages)` | Run work at once: a failure is `null`, and a warning in the output |
| `workflow({ scriptPath }, args)` | Run a script file, read with the session's own `read_file` |

A call has the permissions of the session: a tool turned off is missing, a command that needs your approval asks, and a [sandboxed session](sandbox.md) runs its calls in its sandbox. Commands that wait for your answer, such as `alta ask`, are not available to scripts.

For example, an agent can count the lines of every file of a folder in one call:

```js
// @exec: {"title": "Count lines"}
const files = ["notes.txt", "readme.md"];
const read = await Promise.all(files.map(path => tools.read_file({ path })));
return read.map((r, i) => ({ file: files[i], lines: r.content.split("\n").length }));
```

## In the timeline

A script shows as one card: `exec`, its title (the `title` of its first line), its state, and the calls it made, with the running and failed ones first. Click the card for its window:

- **Console**: the calls, and the result and output the model read.
- **Script**: the code that ran.
- **Details**: the records of the call.

## Long scripts

`exec` waits 30 seconds for a script, or what its first line says (`yield_ms`). A script still running then goes on in the background: the agent can call `wait` for it, or end its turn. When it ends, its result comes back to the session as a **Workflow** message.

Another session, or you from a terminal, reads a run with `alta workflow list`, `alta workflow show <run-id>` (its steps and the sessions it started) and `alta workflow result <run-id>`, and stops it with `alta workflow cancel <run-id>`.

The source of each script is kept in the scratchpad of the session, and its result says where. An agent fixes a long script by editing that file and running it again with `workflow({ scriptPath })`.

## Limits

A script stops with a message that says which limit it reached:

- 1 hour of run time, 5,000 calls, 8 calls at once;
- the tokens of the sessions a script starts, when its first line sets `budget_tokens`;
- 256 MB of memory, and a guard against endless loops and deep recursion;
- 4 scripts at a time per session, 32 in CodeAlta.

Calls made before a failure are not undone: the result lists them.
