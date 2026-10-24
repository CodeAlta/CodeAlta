# ACP integration

CodeAlta runs the agents of the [Agent Client Protocol](https://agentclientprotocol.com) (ACP) as model providers: GitHub Copilot, Gemini CLI, Cursor, Qwen Code and the others of the ACP registry. An ACP agent runs its own loop (its model, its tools, its context); CodeAlta is its client, over JSON-RPC on the standard streams of the agent process. A session of an ACP provider is a CodeAlta session like the others: its history, its tools, its permissions and its statistics are CodeAlta's, and it can go on with another provider (Codex, Claude Code...) and come back.

## Projects

| Project | What it holds |
| --- | --- |
| `src/CodeAlta.Acp` | The protocol: the generated types of the ACP schema (`generated/`), the JSON-RPC transport (`AcpJsonRpcTransport`), the client (`AcpClient`), the agent process (`AcpProcess`), the registry (`AcpRegistryClient`) and the installer of its distributions (`AcpInstallResolver`, `AcpInstaller`). No CodeAlta runtime type. |
| `src/CodeAlta.Acp.Generator` | Generates `src/CodeAlta.Acp/generated` from the JSON schema of the ACP repository. |
| `src/CodeAlta.Plugin.Acp` | The built-in plugin `acp`: the provider type `acp`, its runtime and turn executor, the catalog of the registry, the sign-in. It uses only the public plugin contracts (`PluginBase.GetModelProviders()`) and the harness toolkit of `CodeAlta.Agent` (`CodeAlta.Agent.Harness`). |

The plugin is registered in `DesktopPlugins.BuiltIns` and in the built-in plugins of the TUI. See [plugins.md](plugins.md#model-providers) for the contract of a provider type of a plugin.

## Configuration

An ACP provider is a provider of type `acp` in `~/.alta/config.toml`:

```toml
[providers.github-copilot-cli]
type = "acp"
enabled = true
display_name = "Copilot (ACP)"
agent = "github-copilot-cli"          # the id of the registry entry, when added from it
agent_version = "1.0.103"             # the version that was added
command = "npx"                       # an executable on PATH or a full path
arguments = ["--yes", "@github/copilot@1.0.103", "--acp"]
env = { }                             # environment variables of the agent process
permission_mode = "acceptEdits"       # default, acceptEdits or bypassPermissions
```

`command` and `arguments` are what starts the agent; `agent` and `agent_version` only say where they came from, for the update. A provider written by hand needs only `type`, `command` and `arguments`. The Catalog keeps these values as raw TOML for the plugin (`CodeAltaConfigStore.LoadGlobalExternalProviders()`); they are validated by the plugin (`PluginModelProviderContribution.Validate`).

On Windows a command without extension is looked for on `PATH` with the extensions of `PATHEXT` only (`AcpCommandLocator`), as the shell does: `npx` is `npx.cmd`, never the script for POSIX shells Node.js installs beside it.

## The registry

`AcpRegistryCatalog` reads `https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json` and keeps a copy under `~/.alta/cache/acp/` that is shown, with its date, when the registry cannot be read. Each entry gives one distribution per platform:

| Distribution | What adding does |
| --- | --- |
| `binary` | Downloads the archive of the current platform (`AcpInstallResolver.GetCurrentTargetId`), with its progress, into `~/.alta/cache/acp/agents/<id>/<version>/<platform>/`, and points `command` at the executable. |
| `npx` | Writes `npx --yes <package>@<version>` and its arguments; npx fetches the package at the first start. Needs Node.js. |
| `uvx` | Writes `uvx <package>@<version>`. Needs uv. |

An entry whose distribution cannot run here (no archive for the platform, no npx or uvx on `PATH`) is shown with the reason and cannot be added. Adding writes the provider, enabled, under the key the entry suggests (its id) or the first free one after it; when another provider has the name of the entry, the added one is named with the tag of the type, such as "Copilot (ACP)". The Update of a provider rewrites `command`, `arguments` and `agent_version` for the version the registry has now.

## A turn

`AcpTurnExecutor` implements `IModelProviderTurnExecutor`, like the other providers that run their own agent (Claude Code). One `AcpAgentSession` per CodeAlta session owns one agent process and one session of the agent:

1. The process starts in the working directory of the session, and is initialized with the capabilities of CodeAlta (`AcpClientInfo.CreateCapabilities`: no file system and no terminal of the client, sign-in in a terminal, notices and compactions).
2. The session of the agent is found again from the provider state of the journal (`AcpProviderState`: its id, how many user and assistant messages it holds, the hash of the instructions it was given). When the journal holds the same conversation, it is resumed (`session/resume`, else `session/load`). Otherwise, for a new session, a fork, a rewind or a conversation another provider went on with, a new session is created and the first prompt starts with `<codealta_previous_conversation>` (`HarnessHistory.CreatePreviousConversation`).
3. The instructions of CodeAlta go in the system prompt of the agent when it takes one (`sessionCapabilities.systemPrompt`); otherwise they open the first prompt and are given again when they change (`HarnessInstructions.CreateUpdate`). The channel is recorded in the journal (`IAgentProviderInstructionsChannel`).
4. The tools of CodeAlta (`alta`, the tools of plugins) reach the agent through an MCP server of the session over HTTP (`IHarnessToolEndpoints`, below), when the agent supports HTTP MCP servers. A call is resolved through `IAgentProviderToolHost`: the agent waits for the result while CodeAlta runs the tool, with its own permissions.
5. `session/prompt` runs; the `session/update` notifications become the events of the turn: text and thoughts, the tool calls of the agent (with their kind, their input, their output and the files they change), the plan, the usage, the commands, the models and the notices. Each `ExecuteTurnAsync` returns the next assistant segment, up to a tool call of CodeAlta or the end of the prompt.
6. A stopped turn sends `session/cancel`; a turn that does not end within ten seconds stops the process. A process idle for ten minutes is stopped; the next turn starts it again and resumes the session.

The models and the reasoning efforts are the config options of the agent (categories `model` and `thought_level`): read when the provider is tested, cached per provider under `~/.alta/cache/acp/models/`, and set with `session/set_config_option` before a prompt when the session chooses another one.

## Permissions

The agent asks before it acts (`session/request_permission`). The permission mode of the session answers without asking: reads, searches and thoughts always; edits, deletions and moves in `acceptEdits`; everything in `bypassPermissions`. Otherwise the request goes to the user as a command or a file change request, and the answer selects the matching option of the agent (allow once, allow always, reject). A request about a tool of CodeAlta is allowed: the tool asks its own permissions when the session runs it.

## Sign-in

An agent that needs a sign-in answers `session/new` with `auth_required`; the probe reports it as `acp-signed-out`. The form of the provider lists the methods of the agent (`authMethods`): a method of type `agent` is run with `authenticate`; a method of type `terminal` (or `_meta.terminal-auth`) is a command shown with **Run in a terminal**, which opens a terminal of the window with it, after which **Test provider** checks the sign-in.

## Tool endpoints

`LoopbackHarnessToolEndpoints` (in `CodeAlta.Agent.Harness`) serves one MCP endpoint per session at `http://127.0.0.1:<port>/mcp/<token>`, with a bearer secret of its own. It is a minimal HTTP/1.1 server on a `TcpListener` (no ASP.NET, so that the TUI does not load it) that answers MCP JSON-RPC with `HarnessMcpToolServer`, as the Streamable HTTP transport does: a POST gets one JSON body, and a GET that accepts `text/event-stream` opens a stream of the messages of the server. The endpoint lives as long as the session of the agent. A relay over stdio for agents that support only stdio MCP servers is not done yet.

The tools of a run can grow during it: `alta ui activate` and `alta mcp activate` register tools that the model calls in its next step. The next segment of the turn brings them to the ACP session, which tells the agent with `notifications/tools/list_changed` (`HarnessToolEndpoint.NotifyAsync`): on its stream of events when it opened one, else in the response of the request it waits for, sent as a stream of events when it accepts one (Copilot posts with `Accept: text/event-stream, application/json` and opens no stream). For that, the result of a tool call of CodeAlta waits for the next segment, two seconds at most, so that the agent learns of the tools with the result of the call that added them.

## Probe and status

**Test provider** starts the agent in a folder of its own (`~/.alta/cache/acp/probe/`), reads its capabilities, opens a session to read its models and closes it; no model is called. The failures are categorized for the window: `acp-not-installed` (the command is not found), `acp-signed-out`, `acp-unavailable` (the process did not start or did not answer). The capabilities are shown with the provider as tags: system prompt, CodeAlta tools, images, resume.

## Not done yet

- A relay of the tools of CodeAlta over stdio MCP, for agents without HTTP MCP.
- The file system and terminal capabilities of the client (`fs/*`, `terminal/*`): the agent uses its own tools.
- Importing the sessions an agent ran outside CodeAlta (`session/list`).

## Regenerating the protocol types

The types follow the schema of the ACP repository, cloned beside this one:

```sh
dotnet run --project src/CodeAlta.Acp.Generator -- --acp-repo-dir ../agent-client-protocol --surface unstable --source-ref v1.12.0
```

`--surface unstable` includes the unstable methods CodeAlta uses (`session/resume`, `session/close`, `systemPrompt`); `--source-ref` is recorded in the generated files. The schema is read from `schema/v1/` (older layouts from `schema/`).
