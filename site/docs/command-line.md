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
- `-m provider:model[@reasoning]` chooses the model, `--tier complex|standard|quick` a [model tier](model-tiers.md) (`--tier adaptive` puts the session on [Adaptive](model-tiers.md#adaptive-experimental)), `--reasoning` the effort, `--prompt-id` the [agent prompt](prompts.md) (`default`, `plan`...).
- The prompt is the words given. `-` reads it from standard input; when standard input is piped and words are given too, it is added after them in a `<stdin>...</stdin>` block. `-f FILE` attaches a file or an image.
- `--resume ID` continues a session; `--session-id ID` opens the session `ID`, or creates it with this id.
- `--timeout DURATION` (seconds, or `hh:mm:ss`) stops the run when the time is up. The agent is told when the run ends, and its background jobs are stopped a little before then, so that their results still reach it.
- `altacli models` lists the models of the providers that are ready; `altacli auth check --provider ID` tells whether one is ready.
- `altacli --help` lists the commands; `altacli <command> --help` gives all a command does, takes and writes, with examples: enough for an agent that uses `altacli` on its own.

The run ends when the session and the sessions it started have nothing left to do: the answers of [delegated agents](sessions/readme.md) and the background jobs of the run are waited for. A run stopped by Ctrl+C or `--timeout` is waited for 30 seconds at most, then `altacli` exits all the same; a second Ctrl+C stops at once.

### Full trust

Nobody is there to answer, so every tool runs without asking, and the agents are told so: they decide instead of asking. The commands of `alta` that need a user or a window (`ask`, `review`, `reminder`, the UI tools...) are not offered. Run `altacli` where an agent may change files: a checkout, a container, a throwaway folder.

`--sandbox PROFILE` runs the tools of the sessions in a [sandbox](sessions/sandbox.md) (`workspace`, `strict` or a profile of yours), whatever the settings say; `--sandbox off` runs them without one. A command never runs outside the sandbox there: nobody is there to allow it.

`--disable-tools PATTERN` turns tools off (`web` turns off every tool that reaches the web, `mcp__*` the tools of MCP servers), for the session of the run and the sub-agents it starts; `--no-mcp`, `--no-plugins` and `--no-skills` start none of them. Only the [plugins](plugins/readme.md) made for the command line run there: Git, Jira, MCP and ACP among the built-in ones.

### What the agent is asked to do

A run uses the same [agent prompt](prompts.md) as CodeAlta Desktop, without the parts that only matter to somebody who watches the session. Each part has a name:

| Part | What the agent is asked | `altacli run` |
| --- | --- | --- |
| `scratchpad` | Copy a long request to a file, to keep it through a compaction | off |
| `notes` | Keep the progress in the sticky notes of the session | off |
| `tasks` | Propose follow-up tasks for what it notices beside the request | off |
| `review` | Ask you to review the changes before they are committed | off |
| `reminders` | Set a reminder that brings it back later | off |
| `ask` | Ask you a question when something is unclear | off |
| `mode-handoff` | Switch to Plan mode; the list of the agent prompts | off |
| `session-title` | Give the session a title | off with `--ephemeral` |

`--enable NAMES` keeps parts and `--disable NAMES` leaves them out, as names separated by commas, or `all`: `--enable tasks,notes`, `--enable all` for the prompt of CodeAlta Desktop. The first line of `--format jsonl` lists the parts left out. Git branches, plan files, background jobs, skills and the user guide stay in the prompt.

The agent can [delegate](sessions/delegation.md) parts of the work to sessions of its own. `--no-sub-sessions` creates the session without them: it is not told about them, and the tools that start them (`alta session create`, `agent()` in [Code Mode](sessions/code-mode.md)) are not offered. A session resumed with it loses them; one resumed without it keeps what it was created with.

## Output and exit codes

By default, standard output holds the final answer only, so `altacli run "..." > answer.md` works; what the agent does goes to standard error (`-q` to hide it). `--output-last-message FILE` also writes the answer to a file; a run whose answer cannot be written there fails (exit code 1). `--output-schema FILE` asks for an answer that is a JSON value matching a JSON Schema, and checks it. The schema can use `type`, `properties`, `required`, `additionalProperties`, `items`, `enum`, `const`, `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `minLength`, `maxLength`, `pattern`, `minItems`, `maxItems`, and annotations such as `title` and `description`; a schema with another keyword (`$ref`, `allOf`, `format`...) is refused with exit code 2.

`--format jsonl` writes what happens as it happens, one JSON line each, in the format of [ACP](https://agentclientprotocol.com): the notifications an ACP agent sends to its editor.

| Line | What it says |
| --- | --- |
| `_codealta/session` | First line: the session, its folder, provider and its type (`anthropic`, `codex`, `claude-code`...), model, storage, the parts of the agent prompt left out (`disabledFeatures`), and its options (`options.subSessions`, false with `--no-sub-sessions`). |
| `session/update` | Messages, thinking, tool calls and their results, plans and token use, as in ACP. The lines of a delegated agent carry its own `sessionId`. |
| `_codealta/session_started`, `_codealta/compaction`, `_codealta/retry`, `_codealta/fallback`, `_codealta/error` | What ACP has no update for: a delegated agent starts, a compaction, a retry, a [fallback](model-tiers.md) to another model, an error (`events_dropped` when updates were lost: the whole answer is in the result). |
| `_codealta/operation` | One request to a model: the model and effort, the input tokens with those read from and written to the prompt cache, the output and thinking tokens, and the cost when the provider gives one. They add up to the tokens of the result. |
| `_codealta/result` | Last line: the status, exit code, final answer, tokens, cost, and duration. Tokens and cost the provider does not give are null, never 0; a cost it does not give is estimated in US dollars from the public price of the model, at its base rate, and marked `"_meta": {"codealta": {"estimated": true}}`. |

| Exit code | The run |
| --- | --- |
| 0 | completed |
| 1 | failed: an error of the provider or of a tool, or an answer that could not be written to `--output-last-message` |
| 2 | could not start: an option, the configuration, a session that does not exist, a required plugin or MCP server that did not start |
| 3 | was stopped (Ctrl+C, a signal) |
| 4 | ran out of time (`--timeout`) |
| 5 | found its session in use by CodeAlta Desktop, TUI or another `altacli` |
| 6 | found its provider not ready: no provider, a model it does not offer, a sign-in needed |
| 7 | gave an answer that does not match `--output-schema` |

## Sessions and storage

By default, the sessions of `altacli` are those of CodeAlta Desktop and TUI: they are listed there as they are written. While `altacli` runs a session, the app shows it with a mark, **Running in altacli**, and follows it without letting you change it; once the run is over, you can continue it in the app. `altacli` never writes the settings, the projects, the spaces, the agent prompts of your profile or the database of the apps; its agents can still write the agent prompts of a project. A session in a folder that is not a project of yours is listed once you add the project.

A session is run by one program at a time: `altacli --resume` of a session that is open in a tab of CodeAlta Desktop, or running there, stops with exit code 5. Close the tab first.

| Option | Where the sessions go |
| --- | --- |
| *(none)* | The sessions of `~/.alta`, shared with the apps. |
| `--isolated` | `~/.alta/cli`: apart from the apps, with your settings, providers and skills. Several `altacli` can run at once. |
| `--profile FOLDER` or `CODEALTA_HOME` | A whole separate profile: its own settings, credentials, MCP servers (`mcp.json`) and sessions. The MCP servers, skills and agents that other tools keep in your home folder are not read. The folder must exist. |
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

A run that must not go on with less than it was set up with marks what it needs as required: `required = true` in `[plugins.<id>]` for a plugin, in `[plugins.mcp.servers.<key>]` for an [MCP server](plugins/mcp.md). `altacli run` and `altacli acp` then start the required MCP servers before the session, and stop with exit code 2, naming what did not start and why. `altacli acp` checks the folder of each session it opens too, and refuses one whose project requires what does not start (error `required_unavailable`). `--no-plugins` and `--no-mcp` turn the check off with what they turn off.

The commands an agent runs get `CODEALTA_SESSION_ID`, `CODEALTA_MODEL` and `AI_AGENT=codealta` in their environment, in the apps too.

## Traces (OpenTelemetry)

CodeAlta Desktop, CodeAlta TUI and `altacli` send traces of their runs to an OpenTelemetry collector when the environment names one with `OTEL_EXPORTER_OTLP_ENDPOINT` (or `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`), as a CI job often does:

```sh
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
altacli run "Fix the failing test"
```

- The spans follow the OpenTelemetry conventions of generative AI: `invoke_agent CodeAlta` for each run (a prompt to its end), with the session id as `gen_ai.conversation.id`, the provider, the model and the tokens of the run; `chat <model>` for each request to the model whose usage the provider reports, with its tokens; `execute_tool <tool>` for each tool call, in error when it fails.
- They hold names, ids, models, durations, token counts and kinds of failures. They hold the content only when `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` (or `span_only`) asks for it: the prompts and the last answer of each run, the arguments and the result of each tool call, each cut at 32 KB. Turn it on only for a collector that may keep them.
- The other standard variables apply: `OTEL_EXPORTER_OTLP_PROTOCOL` (`grpc` by default), `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_SERVICE_NAME` (else `alta`, `altatui` or `altacli`), `OTEL_RESOURCE_ATTRIBUTES`. `OTEL_SDK_DISABLED=true` turns the traces off.
- `altacli` sends its last spans before it exits.
- The agents CodeAlta starts (Claude Code, Codex CLI, ACP agents) do not get the `OTEL_*` variables, so that they do not report the same runs twice. The commands of the agents, the jobs and the MCP servers keep them.

Without these variables, nothing is sent.

## The alta commands

`altacli alta ARGS...` runs the commands of the `alta` tool, as the agents do: `altacli alta session list`, `altacli alta project list`, `altacli alta model list`. `altacli alta --help` lists those of the command line. They see the sessions of the storage they are given, such as `--isolated`: those of the folder of the command (`-C`, else the current folder) and of your projects are listed, and any session is found by its id, as `altacli run --resume` finds it. The commands that would change the configuration files, such as `alta mcp server add` or `alta mcp server disable`, fail there: `altacli` leaves them as they are.

## Editors (ACP)

`altacli acp` serves CodeAlta to an editor or a tool that speaks [ACP](https://agentclientprotocol.com), over its standard input and output. In Zed, for example, add an agent server to the settings:

```json
"agent_servers": {
  "CodeAlta": { "command": "altacli", "args": ["acp"] }
}
```

The sessions it opens are those of CodeAlta Desktop and TUI, as with `altacli run`; the storage and configuration options above apply. The editor chooses the [agent prompt](prompts.md) as the mode, and the model and reasoning effort as options of the session; it can load an earlier session with its history. The updates are those of `--format jsonl`.

You work with the agent in the editor, so its prompt keeps the [parts](#what-the-agent-is-asked-to-do) of CodeAlta Desktop, except `review`, `reminders` and `ask`, whose commands `altacli` does not offer, and `session-title` with `--ephemeral`. `--enable` and `--disable` change them as for `altacli run`, and `--no-sub-sessions` creates its sessions without sub-sessions.
