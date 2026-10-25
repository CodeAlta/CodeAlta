---
title: Command Line
---

# Command Line

**CodeAlta CLI** (`altacli`) runs the agents of CodeAlta without a window: the same providers, tools, agent prompts, skills, MCP servers, plugins and sessions as CodeAlta Desktop and TUI, on the same `~/.alta` profile. It runs a prompt to its end and exits with a code that tells how it went, so scripts, CI jobs and benchmarks can use it. It also serves CodeAlta to editors as an [ACP](https://agentclientprotocol.com) agent.

## Install

```sh
dotnet tool install -g CodeAlta.Cli
```

It needs the same .NET SDK as the two apps (see [Getting Started](getting-started.md#install)). It uses the providers you set up in CodeAlta Desktop or TUI; on a computer without them, see [Configuration](#configuration).

## Run a prompt

```sh
altacli run "Fix the failing test in Parser.Tests"
altacli run -C ~/code/my-project -m anthropic:claude-opus-5-5@high "Review the last commit"
git diff | altacli run "Review this diff"
altacli run - < task.md
```

- The session works in the current folder, or the folder of `-C`.
- `-m provider:model[@reasoning]` chooses the model, `--tier complex|standard|quick` a [model tier](model-tiers.md), `--reasoning` the effort, `--prompt-id` the [agent prompt](prompts.md) (`default`, `plan`...).
- The prompt is the words given. `-` reads it from standard input; when standard input is piped and words are given too, it is added after them in a `<stdin>...</stdin>` block. `-f FILE` attaches a file or an image.
- `--resume ID` continues a session; `--session-id ID` opens the session `ID`, or creates it with this id.
- `--timeout DURATION` (seconds, or `hh:mm:ss`) stops the run when the time is up.
- `altacli models` lists the models of the providers that are ready; `altacli auth check --provider ID` tells whether one is ready.
- `altacli --help` lists the commands; `altacli <command> --help` gives all a command does, takes and writes, with examples: enough for an agent that uses `altacli` on its own.

The run ends when the session and the sessions it started have nothing left to do: the answers of [delegated agents](sessions/readme.md) and the background jobs of the run are waited for. A second Ctrl+C stops at once.

### Full trust

Nobody is there to answer, so every tool runs without asking, and the agents are told so: they decide instead of asking. The commands of `alta` that need a user or a window (`ask`, `review`, `reminder`, the UI tools...) are not offered. Run `altacli` where an agent may change files: a checkout, a container, a throwaway folder.

`--disable-tools PATTERN` turns tools off (`web` turns off every tool that reaches the web, `mcp__*` the tools of MCP servers); `--no-mcp`, `--no-plugins` and `--no-skills` start none of them. Only the [plugins](plugins/readme.md) made for the command line run there: Git, Jira, MCP and ACP among the built-in ones.

## Output and exit codes

By default, standard output holds the final answer only, so `altacli run "..." > answer.md` works; what the agent does goes to standard error (`-q` to hide it). `--output-last-message FILE` also writes the answer to a file. `--output-schema FILE` asks for an answer that is a JSON value matching a JSON Schema, and checks it.

`--format jsonl` writes what happens as it happens, one JSON line each, in the format of [ACP](https://agentclientprotocol.com): the notifications an ACP agent sends to its editor.

| Line | What it says |
| --- | --- |
| `_codealta/session` | First line: the session, its folder, provider, model and storage. |
| `session/update` | Messages, thinking, tool calls and their results, plans and token use, as in ACP. The lines of a delegated agent carry its own `sessionId`. |
| `_codealta/session_started`, `_codealta/compaction`, `_codealta/retry`, `_codealta/fallback`, `_codealta/error` | What ACP has no update for: a delegated agent starts, a compaction, a retry, a [fallback](model-tiers.md) to another model, an error. |
| `_codealta/result` | Last line: the status, exit code, final answer, tokens, cost when the provider gives one, and duration. |

| Exit code | The run |
| --- | --- |
| 0 | completed |
| 1 | failed: an error of the provider or of a tool |
| 2 | could not start: an option, the configuration, a session that does not exist, a required plugin or MCP server that did not start |
| 3 | was stopped (Ctrl+C, a signal) |
| 4 | ran out of time (`--timeout`) |
| 5 | found its session in use by CodeAlta Desktop, TUI or another `altacli` |
| 6 | found its provider not ready: no provider, a model it does not offer, a sign-in needed |
| 7 | gave an answer that does not match `--output-schema` |

## Sessions and storage

By default, the sessions of `altacli` are those of CodeAlta Desktop and TUI: they are listed there as they are written. While `altacli` runs a session, the app shows it with a mark, **Running in altacli**, and follows it without letting you change it; once the run is over, you can continue it in the app. `altacli` never writes the settings, the projects or the database of the apps: a session in a folder that is not a project of yours is listed once you add the project.

A session is run by one program at a time: `altacli --resume` of a session that is open in a tab of CodeAlta Desktop, or running there, stops with exit code 5. Close the tab first.

| Option | Where the sessions go |
| --- | --- |
| *(none)* | The sessions of `~/.alta`, shared with the apps. |
| `--isolated` | `~/.alta/cli`: apart from the apps, with your settings, providers and skills. Several `altacli` can run at once. |
| `--profile FOLDER` or `CODEALTA_HOME` | A whole separate profile: its own settings, credentials and sessions. The folder must exist. |
| `--ephemeral` | A temporary folder deleted at the end: nothing is kept. |
| `--dev` | The sessions of the developer instance of CodeAlta (`alta --dev`), for those who develop CodeAlta. |

## Configuration

`altacli` reads the settings of `~/.alta/config.toml` and never writes them. A run can change them for itself:

- `-c KEY=VALUE` sets one setting, as in `config.toml`: `-c chat.default_provider=anthropic`, `-c providers.anthropic.api_key_env=MY_KEY`, `-c plugins.mcp.servers.docs.enabled=false`. Repeat it for several.
- The variable `CODEALTA_CONFIG_CONTENT` holds settings in TOML, read after `config.toml` and before `-c`.
- `--ignore-user-config` does without `config.toml`: only the two above count.
- `--strict-config` refuses the settings CodeAlta does not know.

On a computer without CodeAlta set up, such as a container, give the provider in the configuration and its key in a variable:

```sh
export CODEALTA_CONFIG_CONTENT='[chat]
default_provider = "anthropic"

[providers.anthropic]
type = "anthropic"
api_key_env = "ANTHROPIC_API_KEY"'
altacli run --ephemeral "Fix the failing test"
```

A run that must not go on with less than it was set up with marks what it needs as required: `required = true` in `[plugins.<id>]` for a plugin, in `[plugins.mcp.servers.<key>]` for an [MCP server](plugins/mcp.md). `altacli run` and `altacli acp` then start the required MCP servers before the session, and stop with exit code 2, naming what did not start and why. `--no-plugins` and `--no-mcp` turn the check off with what they turn off.

The commands an agent runs get `CODEALTA_SESSION_ID`, `CODEALTA_MODEL` and `AI_AGENT=codealta` in their environment, in the apps too.

## The alta commands

`altacli alta ARGS...` runs the commands of the `alta` tool, as the agents do: `altacli alta session list`, `altacli alta project list`, `altacli alta model list`. `altacli alta --help` lists those of the command line. The commands that would change the configuration files, such as `alta mcp server add` or `alta mcp server disable`, fail there: `altacli` leaves them as they are.

## Editors (ACP)

`altacli acp` serves CodeAlta to an editor or a tool that speaks [ACP](https://agentclientprotocol.com), over its standard input and output. In Zed, for example, add an agent server to the settings:

```json
"agent_servers": {
  "CodeAlta": { "command": "altacli", "args": ["acp"] }
}
```

The sessions it opens are those of CodeAlta Desktop and TUI, as with `altacli run`; the storage and configuration options above apply. The editor chooses the [agent prompt](prompts.md) as the mode, and the model and reasoning effort as options of the session; it can load an earlier session with its history. The updates are those of `--format jsonl`.
