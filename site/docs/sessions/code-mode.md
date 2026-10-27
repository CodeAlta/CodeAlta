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
| `agent(prompt, { tier, model, title, schema })` | Start a session of its own for a part of the work, and get its final answer, or the JSON value it answered with `schema`. It is a sub-agent of the session, which you can open; 8 run at once, 50 per script. A [tier](../model-tiers.md) that is not set up runs it on the model of the session that runs the script, and the output says so once |
| `session({ tier, title })` | Keep a session for several prompts: `run(prompt)` sends one, `close()` ends it. Its tier works as for `agent()` |
| `ask(question, { choices, default })` | Ask you a question and get your answer. It shows at once where the session asks its questions, also while the turn runs, one question of a script at a time. The time you take counts neither toward the script's time limit nor toward its `yield_ms`: the turn waits for your answer. In an automation, nobody is asked: the script gets an `AskError` |
| `sleep(duration)` | Wait: milliseconds, or `"30s"`, `"5m"`, `"2h"`, `"1d"`, up to 7 days. A sleeping script goes on in the background, and the time it sleeps does not count toward its time limit. If CodeAlta closes meanwhile, the script goes on by itself when it wakes |
| `parallel(functions, { limit })`, `pipeline(items, ...stages)` | Run work at once: a failure is `null`, and a warning in the output |
| `workflow("name", args)`, `workflow({ scriptPath }, args)` | Run a [script of the library](#scripts-of-the-library), or a script file read with the session's own `read_file` |

A call has the permissions of the session: a tool turned off is missing, a command that needs your approval asks, and a [sandboxed session](sandbox.md) runs its calls in its sandbox. Commands that wait for your answer, such as `alta ask`, are not available to scripts: they use `ask()`.

A script that went on in the background still asks for its approvals in its session, as during the turn. Its card says that it waits for your answer or your approval, and **Open** shows where to give it.

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

## Resume a run

CodeAlta Desktop and CodeAlta TUI keep each run with the result of each `step()`. A script that ran when CodeAlta ended shows as **interrupted** on its card. The card of a run then offers:

- **Resume**, on an interrupted run: the script runs again, and the steps it completed give their recorded results without running. A step that changed, and everything after it, runs again.
- **Rerun with recorded results**, on a run that ended: every recorded step gives its result, and only the rest of the code runs. This fixes the end of a script without paying again for the sessions its steps started.
- **Rerun from this step**, in the menu of a step mark: the steps before it give their results, and that step and what follows run again.

Each one asks the session of the run to call `exec` again: it runs with that session's tools and permissions. From a terminal or another session, `alta workflow resume <run-id>` does the same, with `--all` or `--from <step>`.

A run that CodeAlta closed during a `sleep()` resumes by itself when it wakes, and its card says when. This is the only resume that happens without you, and only when nothing below stops it; otherwise the run waits for **Resume**. A resumed run sleeps only the rest of its sleep.

A run does not resume:

- when the script changed: rerun it with recorded results instead;
- when a call that may not run twice, such as a command, was in flight when it stopped: the agent asks you first, because its outcome is unknown.

Reads, listings and searches are run again without asking. A step whose value is not JSON, or is larger than 256 KB, always runs again.

The steps of a library script, such as those **Run…** and the automations start, are kept and replayed like the others. Their names start with the name of the script and the number of its call, from 0: `count-steps#0/count`. `alta workflow resume <run-id> --from count-steps/count` names it while the script was called once, and `--from count` while no other step has that name; a name that fits several steps is refused with their names.

## Scripts of the library

A script worth keeping goes in the library, where any session runs it by its name with `workflow("name", args)`:

- the samples that come with CodeAlta;
- your scripts, in `~/.alta/scripts/<name>.js`;
- the scripts of a project, in `.alta/scripts/<name>.js`, committed with it. A project script wins over yours of the same name, and yours over a sample.

The samples show what scripts do well, and are a start for your own (`alta script edit <name>` gives a copy to change):

| Sample | What it does |
| --- | --- |
| `review-pr` | Reviews a pull request from several angles at once, one session each, then merges the findings into one review |
| `compare-models` | Gives the same task to several models, then has a judge compare their answers |
| `fix-failing-tests` | Runs the tests, then gives each failing test to a session of its own. The sessions share the folder of the project: one works at a time unless `at_once` says more |
| `triage-issues` | Classifies each open issue of the project's tracker (kind, priority, summary) and reports them by priority |
| `research` | Splits a question into sub-questions, researches each in a session, then writes one answer |

A library script starts by describing itself. Its `params` are checked before it runs: a missing argument, an unknown one or one of the wrong type stops the call with a message that says which.

```js
export const meta = {
  name: "count-lines",
  description: "Counts the lines of files.",
  whenToUse: "To size the files of a change.",
  params: { files: { type: "array", description: "The paths" } },
};
const read = await Promise.all(args.files.map(path => tools.read_file({ path })));
return read.map((r, i) => ({ file: args.files[i], lines: r.content.split("\n").length }));
```

Agents find a script by what it does: `codemode.search()` in a script returns it as `workflow:<name>`, and `alta search --kind script` finds it by its name, its description and its `whenToUse`. With `tool: true` in its `meta`, a script is also a tool of the sessions of its project while Code Mode is on, named after it (`review_pr`), whose arguments are its `params`: the model loads it when it needs it, and calling it runs the script.

Agents manage the library with `alta script`:

| Command | What it does |
| --- | --- |
| `alta script list`, `alta script show <name>` | List the scripts, read one (`--declarations` for how to call it) |
| `alta script run <name> --arg <key>=<value>` | Run a script in a new session of the project, as **Run…** does. A value is JSON (`7`, `["a","b"]`) unless the parameter is a string |
| `alta script edit <name>` | Copy a script into the scratchpad of the session, where the agent changes it with its file tools |
| `alta script check <name-or-path>` | Check that a script parses and describes itself |
| `alta script save <name> --from <path>` | Publish a script, in the project (`--scope global` for yours). With the `--hash` that `edit` gave, a save is refused when the library copy changed since |
| `alta script delete <name>` | Delete a script |

A [sandboxed session](sandbox.md) reads the library but does not save in it. `alta script run` from a terminal needs a CodeAlta that keeps running: `altacli alta script run` refuses, since its CodeAlta ends with the command.

Your scripts and the samples are in every [space](../spaces.md) unless you limit them, as skills are, in `~/.alta/config.toml`. A script limited so is listed, found and run only in those spaces; the scripts of a project are in every space of its sessions.

```toml
[scripts.spaces]
review-pr = ["work"]
```

## The Workflows tab

In CodeAlta Desktop, the **Workflows** button of the title bar (or **Workflows** in the command palette) opens a tab with the library and the runs:

- **Library**: the scripts the project chosen at the top sees in the space the window shows, the project's, yours and the samples, each with its description, its parameters (`*` for a required one) and its steps drawn small. A step that runs once per item shows `×`.
- **Runs**: the scripts running in CodeAlta and those that ended in the last minutes, the running ones first. Click a run to open its session.

To run a script, click **Run…** on it, fill in its parameters, choose the project it runs in, then click **Run**. A list or an object is written as JSON. The script runs in a new session of that project, which opens. That session has `exec` even when Code Mode is off.

To keep a script that ran in a session, click **Save as script…** on its card in the timeline once it ended, and give it a name. The session is asked to turn it into a library script: the values it was written for become parameters, then it is checked and saved under that name.

## Limits

A script stops with a message that says which limit it reached:

- 1 hour of run time, 5,000 calls, 8 calls at once;
- the tokens of the sessions a script starts, when its first line sets `budget_tokens`;
- 256 MB of memory, and a guard against endless loops and deep recursion;
- 4 scripts at a time per session, 32 in CodeAlta.

Calls made before a failure are not undone: the result lists them.
