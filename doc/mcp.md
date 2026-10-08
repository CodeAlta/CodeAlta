# MCP support

CodeAlta ships MCP support as a trusted built-in plugin (`CodeAlta.Plugin.Mcp`). The surface is configuration discovery, policy overlay, session-activated MCP agent tools, prompt guidance, `alta mcp ...` commands, explicit runtime calls through `alta mcp tool ...`, and the TUI MCP Servers dialog. Dynamic agent-tool registration exposes enabled/config-controlled MCP tools only for MCP servers activated in the current session.

## Configuration files and overlay

CodeAlta writes MCP server connection fields to one JSON file per scope:

| Scope | Path | Notes |
| --- | --- | --- |
| Global | `~/.alta/mcp.json` | For every project. |
| Project | `<project>/.alta/mcp.json` | Read only when a project directory is active; comes before global. |

It also reads the files other tools keep, where they are, and never writes them:

| Scope | Path | Kept by | Origin |
| --- | --- | --- | --- |
| Project | `<project>/.mcp.json` | GitHub Copilot CLI, Visual Studio Code, Claude Code | `Common` |
| Project | `<project>/.github/mcp.json` | GitHub Copilot CLI | `Copilot` |
| Project | `<project>/.vscode/mcp.json` | Visual Studio Code | `Vscode` |
| Global | `~/.copilot/mcp-config.json` | GitHub Copilot CLI, Visual Studio Code | `Copilot` |

Effective servers are keyed by the raw server key, and the first definition of a key is the one in effect: a project comes before the user, and in each scope the file of CodeAlta comes before the files of other tools, in the order of the table. The other definitions of the key are reported as shadowed, with the file that comes first, instead of producing a duplicate connection or duplicate tools.

`McpConfigSnapshot.Sources` are the two files CodeAlta writes, as before. `ExternalSources` are the files of other tools that exist (a missing one is not a source: CodeAlta does not create it). Every definition has a `SourceOrigin`, and `McpManagementServerSnapshot.IsReadOnly` is true for a server of another tool:

- its enablement and its tool policy are in `config.toml`, as for any server, and it is tested, activated and authorized the same way;
- its definition is never changed or removed. Saving it from a dialog writes a definition of the same key to the file of CodeAlta of the chosen scope, which then comes first; removing that one puts the other back in effect. `alta mcp server remove` refuses a key that only a file of another tool defines (`read_only_source`).

A file of another tool is read with more tolerance than a file of CodeAlta, because it is not CodeAlta's to fix:

- comments and trailing commas are accepted (Visual Studio Code writes JSON with comments);
- the root can be the map of the servers itself, without `mcpServers` (GitHub Copilot CLI accepts it in a project);
- the value of an environment variable can be a number, and `null` leaves the variable out;
- a server that is not valid is left out and the others are read. A file that cannot be parsed is an invalid source (`InvalidConfig` row), and the other files stay in effect;
- a server of a project starts in the folder of the project when it has no `cwd`, and a relative `cwd` is resolved from it, as in the tools the file is written for.

Variables of such a file are brought to what CodeAlta resolves (`McpExternalVariables`):

| Written | Becomes |
| --- | --- |
| `${env:NAME}` | `${NAME}`, resolved when the server starts |
| `${NAME}`, `${NAME:-default}` | Kept in an `mcpServers` file; an unknown variable in a `servers` file, where Visual Studio Code would not resolve it |
| `${workspaceFolder}`, `${workspaceFolderBasename}` | The folder of the project and its name |
| `${userHome}`, `${pathSeparator}`, `${/}` | The home of the user and the separator of the platform |
| `${input:id}` | The server is left out (`input_variable`): only Visual Studio Code can ask for the value |
| Any other `${...}` | The server is left out (`unknown_variable`) |

A server with an `envFile` is left out too (`environment_file`). A server that is left out is an `Unsupported` row of the management snapshot (`UnsupportedReason`, and a message that names the variable, never a value), an `alta.mcp.config.skipped_server` record of `alta mcp status` and `alta mcp config sources`, and a **Not supported** card of the desktop page.

The `tools` list of a GitHub Copilot file is not used: the tools of a server are filtered by the TOML policy.

The default write scope is project when a project directory is active and global otherwise. New files are created in CodeAlta's default `mcpServers` format:

```json
{
  "mcpServers": {
    "memory": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-memory"],
      "cwd": "C:/work/project",
      "env": {
        "MEMORY_TOKEN": "value"
      }
    },
    "docs": {
      "type": "http",
      "url": "https://example.test/mcp",
      "headers": {
        "Authorization": "Bearer value"
      }
    }
  }
}
```

Existing files are parsed and written back using the detected root/flavor where supported:

- CodeAlta/Claude/IntelliJ-style `mcpServers` files;
- GitHub Copilot-style `mcpServers` files, detected by `tools` entries and preserved when writing;
- Visual Studio Code-style `servers` files, including `stdio`, `http`, and `sse` transport type values.

A single file containing both `mcpServers` and `servers` is invalid because its flavor is ambiguous. A server definition must choose exactly one transport: `command` for stdio or `url` for HTTP/SSE. The `type` of a stdio server is `stdio` or `local`, as GitHub Copilot CLI writes it. Stdio servers can include `args`, `cwd`, and `env`; HTTP/SSE servers can include `headers`. The `command`, `args`, `cwd` and `url` of a server, its `env` values and its `headers` values may reference process environment variables with `${NAME}` placeholders, for example `"Authorization": "Bearer ${GITHUB_PERSONAL_ACCESS_TOKEN}"`, and `${NAME:-default}` gives a value for a variable that is not set. A missing variable produces a finite MCP diagnostic (`environment_variable_not_found`) instead of sending the literal placeholder. The tokens of an HTTP server are kept under its `url` as the file writes it, also when a variable is part of it.

## TOML policy overlay

Runtime policy lives in the normal CodeAlta TOML config files, loaded global first and then project-local when a project scope is active:

| Scope | Path |
| --- | --- |
| Global | `~/.alta/config.toml` |
| Project | `<project>/.alta/config.toml` |

MCP policy is a policy overlay; it does not store server connection fields and should not rewrite JSON server definitions for enablement-only changes.

```toml
[plugins.mcp]
enabled = true
startup_timeout_ms = 30000
tool_timeout_ms = 60000
max_tool_output_chars = 120000
discover_in_prompt = true
prompt_max_servers = 10
prompt_max_tools = 20
direct_exposure = "auto"
direct_tool_threshold = 40

[plugins.mcp.servers.memory]
enabled = true
allowed_tools = ["read_graph"]
disabled_tools = ["delete_graph"]
startup_timeout_ms = 15000
tool_timeout_ms = 30000
direct_exposure = "allowlist"
direct_tools = ["read_graph"]
```

Implemented policy behavior:

- global policy is applied first and project policy overlays it;
- `[plugins.mcp].enabled = false` disables MCP runtime exposure through direct tools and command paths;
- `[plugins.mcp.servers.<server>].enabled = false` disables one server without deleting its JSON definition;
- `allowed_tools` allow-lists a server's tools, and `disabled_tools` disables named raw MCP tools;
- the dialog's per-tool enablement table writes/removes `disabled_tools` entries only;
- `startup_timeout_ms`, per-server `startup_timeout_ms`, `tool_timeout_ms`, per-server `tool_timeout_ms`, and `max_tool_output_chars` bound runtime operations and output;
- `discover_in_prompt`, `prompt_max_servers`, and `prompt_max_tools` bound MCP prompt guidance;
- `direct_exposure`, `direct_tool_threshold`, and per-server `direct_exposure`/`direct_tools` are retained policy controls for non-progressive direct-tool selection paths. Progressive session activation does not activate servers automatically from these settings; after a server is activated, every enabled/config-controlled MCP tool that passes server/tool policy is exposed as a first-class `AgentToolDefinition` using the stable alias described below.

## `alta mcp` commands

The MCP plugin contributes the `mcp` root to the in-process `alta` command registry. Commands resolve project scope from the selected workspace/project or invocation working directory and emit the normal finite JSONL transcript.

Read-only/config inspection commands:

```text
alta mcp list
alta mcp status
alta mcp status --server <server>
alta mcp config sources
alta mcp config sources --include-missing
alta mcp config sources --scope project
```

Mutation commands:

```text
alta mcp activate <server> [<server>...]
alta mcp server add <server> --command <command> --arg <arg> --env KEY=VALUE --cwd <dir> --scope project
alta mcp server add <server> --url https://example.test/mcp --header Authorization=Bearer... --scope global
alta mcp server remove <server> --scope project
alta mcp server enable <server> --scope project
alta mcp server disable <server> --global
```

`activate` mutates only in-memory session activation state and immediately enumerates tools from active servers so the UI can show whether activation took effect. When an agent run calls it, the enumerated tools are also registered in that run (`AgentToolInvocation.RunTools`, see below): its result says `toolsAvailable: "now"` and the model calls them in its next step. Called from a terminal, or when a server gave no tool, the result says `"next_run"`. On every later agent run, the same activated servers are refreshed and registered as `mcp__<server>__<tool>` after normal MCP policy filters. `server add` and `server remove` mutate only the selected JSON MCP file of CodeAlta: adding a server that a file of another tool defines overrides it, and removing a key that only such a file defines is refused (`read_only_source`). `server enable` and `server disable` mutate TOML policy only and preserve JSON server definitions, whose file they are in. `list`, `status` and `config sources` say where a definition comes from (`sourceOrigin`: `codealta`, `common`, `copilot` or `vscode`; `readOnly`), `config sources` also lists the files of other tools that exist, and a shadowed definition has its reason (`project-overrides-global` or `same-scope-source-comes-first`) and `overriddenByPath`. Removing a global-only server from inside a project requires `--scope global` so a project context does not accidentally delete global user configuration.

Runtime tool commands:

```text
alta mcp tool search
alta mcp tool search --server <server> --query <text>
alta mcp tool describe --server <server> --tool <raw-tool-name>
alta mcp tool call --server <server> --tool <raw-tool-name> --arguments {"key":"value"}
alta mcp auth status [--server <server>]
alta mcp auth login <server>
alta mcp auth logout <server>
```

`tool search`, `describe`, and `call` lazily connect to enabled effective servers, list tools, apply policy filters, and return diagnostics plus tool/call records. Tools are routed by raw MCP server key and raw MCP tool name. Returned records also include the same stable qualified alias used for direct agent-tool exposure, `mcp__<server>__<tool>`; alias parts are sanitized and receive a stable short hash suffix when sanitized names collide.

## Runtime behavior

`McpRuntimeService` owns finite MCP runtime state for direct MCP agent tools, the `alta mcp tool ...` commands, and the dialog's Test Server action. It supports:

- stdio servers launched from JSON `command`, `args`, optional `cwd`, and optional `env`;
- HTTP/SSE servers using absolute `http`/`https` URLs, SDK transport auto-detection, connection timeout, JSON `headers` with optional `${NAME}` environment-variable expansion, and CodeAlta-managed OAuth/Authv2 tokens when authorized;
- bounded startup/tool discovery with effective global or per-server startup timeout;
- bounded tool calls with effective global or per-server tool timeout;
- disabled-server, missing-server, disabled-tool, missing-tool, invalid transport, invalid URL/header, timeout, unavailable, and authentication diagnostics.

An image block of a tool result is kept for a direct agent tool: the tool returns it with the text of the result, and the session checks it, scales it down when needed, saves it beside the journal and gives it to the model (see "Images in tool results" in `runtime.md`). An image over 32 MB is left out. `alta mcp tool call` output names an image by its media type and size and never holds its bytes. Audio blocks and embedded resources are still summarized as text.

Runtime diagnostics redact sensitive values before they reach command output or the dialog. Redaction covers configured URLs/headers, secret-like dictionary keys, arguments, exceptions, text output, and structured JSON output. HTTP 401/403 diagnostics explicitly direct the user to use the dialog **Authorize/Login** action or `alta mcp auth login <server>` for browser OAuth, or to configure static headers (including `${NAME}` environment-variable references when preferred) when appropriate.

## HTTP OAuth/Authv2 browser login

Remote HTTP/SSE MCP servers that follow the MCP authorization flow can use browser login instead of long-lived bearer headers in MCP JSON. Add an optional per-server JSON auth block when client details are needed; for servers that support dynamic client registration, the URL plus `"auth": { "type": "oauth" }` is usually enough:

```json
{
  "mcpServers": {
    "docs": {
      "url": "https://example.test/mcp",
      "auth": {
        "type": "oauth",
        "clientId": "optional-client-id",
        "scopes": ["read", "search"]
      }
    }
  }
}
```

CodeAlta stores access/refresh tokens in local user MCP plugin state under `~/.alta/auth/mcp/`, not in `.alta/mcp.json` or TOML policy. The token cache uses per-server files, creates parent directories for the current user, writes through a temporary file, and command/dialog output reports only status and expiry, never token values, authorization codes, or client secrets. Browser login uses a loopback callback with a per-login state value and an ephemeral port by default; set `redirectUri` only when an authorization server requires a pre-registered fixed callback. Non-interactive agent runs and ordinary `alta mcp tool ...` commands use cached/refreshable tokens only; they do not surprise-open a browser.

Use the MCP Servers dialog **Authorize/Login** action for a configured HTTP server to open the browser flow. The action opens a small modal login dialog on top of the MCP dialog, shows/copies the login URL when available, and supports **Cancel Login** (`Esc` or `Ctrl+G Ctrl+C`) for stuck or unwanted browser flows. Use **Logout** to delete cached tokens. The CLI fallback is `alta mcp auth login <server>`, `alta mcp auth status`, and `alta mcp auth logout <server>`.

The desktop app has the same flow on the **MCP Servers** page of Settings. The form of a saved HTTP server ends with an **Authorization** block that says whether tokens are stored and until when, with **Authorize** (or **Authorize again**), and **Sign out** to remove the tokens. The `mcpServers` RPC service streams the login as events (`login`: the address to open, then `completed` with the number of listed tools or `failed` with a code and the plugin's redacted explanation); closing the channel, the page's **Cancel**, or closing the application cancels it. One login runs at a time, and only for the definition in effect: a definition that another one overrides is refused. The page receives the address and the token's presence and expiry, never a token.

Tool-call results preserve `isError` from MCP. Text content becomes `contentText` and `content` blocks; structured content is included after redaction when it fits the output character budget. Image, audio, embedded-resource, resource-link, and unknown non-text content are summarized instead of embedding raw payloads. Output beyond `max_tool_output_chars` is truncated and marked with `truncated = true`.

Servers that cannot connect or list tools are reported as unavailable for that runtime request and do not contribute enabled tools to activation-time status, activated-agent-tool enumeration, or search/describe/call results. Runtime state is explicit and finite; activated tools are eagerly enumerated when `alta mcp activate ...` runs for immediate status feedback, then refreshed again at agent-run time. `tool-list-changed` notifications are follow-up work only if tool freshness requires them.

The tools a session calls keep their server connected (`McpSessionConnections`). A server can hold state from one call to the next, such as a browser snapshot whose element ids the next call uses, and starting it for every call costs seconds. Each session has its own connections, used by one call at a time, opened at its first call. They are closed when a call could not be completed (the server did not start, the call timed out or the connection broke: the next call connects again), after fifteen minutes without a call, and when the plugin stops. `alta mcp tool ...` commands, activation and the per-run tool listing still use a connection of their own that ends with them.

## TUI dialog and status indicator

Open the MCP Servers dialog through any of these entry points:

- `/mcp` in the shell prompt;
- command palette entry **MCP Servers**;
- `Ctrl+G Ctrl+Y` (not `Ctrl+G Ctrl+M`, because some terminals report Enter as `Ctrl+M`);
- the clickable MCP status indicator when a cached MCP snapshot exists and MCP JSON/TOML configuration is present.

The dialog and status indicator share `McpManagementService`. `Refresh` reads the fixed JSON config files, the files of other tools and TOML policy without connecting to servers. The server list includes effective servers, disabled servers, invalid/missing config rows, shadowed definitions, and the servers of other tools that are left out (`not supported`). A server of another tool names its file in the list and in **Details** (**Source Owner**, **Shadowed By**); **Remove** is not offered for it, and **Save** writes the edited definition to the file of CodeAlta and leaves the other file as it is. Dialog counts are cached snapshot counts; the status indicator uses cached exposed/total counts when a dialog test has run, otherwise it reports session activation state as `tools pending`, `active tools N`, or `tools not loaded`; `tools pending` should be transient and is replaced after activation-time or agent-run tool enumeration updates the bindable status state. Enabled configured servers with no cached test result start a background, cancellable tool discovery when selected so the **Tools (N)** tab is prefilled without blocking rendering, close, or application shutdown.

For a selected configured server, the dialog shows compact enablement/actions above the tab strip. The first **Config** tab contains editable JSON server fields. The **Tools (N)** tab contains discovered tool policy controls. The following **Details** tab contains normalized/redacted connection fields, authorization status for HTTP servers, source scope/path/format, policy values, and diagnostics. Rendering and refresh use cached/discovered config snapshots and do not synchronously connect to servers; background prefill and the explicit **Test** action connect to the selected stdio or HTTP/SSE server with configured timeouts, update cached exposed/total tool counts, and surface success, failure, timeout, cancellation, and auth/unavailable diagnostics without blocking normal config refresh/rendering. The **Authorize/Login** action is explicit and may open a browser; automatic prefill and background agent-tool enumeration remain non-interactive. The top-level **Add**, **Save**, and **Remove** actions create/update/remove JSON server definitions in the selected project/global scope. Editable config fields show the unredacted JSON values because they are write-back fields. Editable arguments use semicolon-separated values; env/header fields use semicolon-separated `KEY=VALUE` pairs and support `${NAME}` environment-variable placeholders. The compact Enabled checkbox writes server `enabled` policy. The discovered tools table shows Enabled, raw MCP name/title, and policy status. Toggling a tool writes/removes that raw tool name in `disabled_tools`; it does not edit JSON server definitions.

Use `alta mcp server add/remove/...` or **Open JSON Config** for advanced JSON shapes not exposed by the dialog form.

## Plugin and prompt boundaries

The MCP implementation is a built-in trusted plugin, enabled by default through the same built-in plugin infrastructure as the Git and statistics plugins. It contributes:

- an `alta mcp` root command with mutating command policy;
- compact dynamic developer prompt guidance that advertises active/inactive configured MCP servers and the `alta mcp activate <id>*` path;
- direct `AgentToolDefinition` contributions for enabled/config-controlled MCP tools on session-activated servers;
- shared discovery/runtime services consumed by direct tools and `alta`, plus management snapshots consumed by the TUI dialog, status indicator, and prompt guidance.

Reusable MCP configuration, policy, runtime, and management code lives in `src/CodeAlta.Plugin.Mcp/`. TUI-specific composition, status indicator rendering, and the dialog live in `src/CodeAlta.Tui/`. Do not move reusable MCP runtime orchestration into frontend controls.

## Progressive MCP agent-tool behavior

Progressive dynamic `AgentToolDefinition` exposure is shipped behavior:

- the MCP plugin prompt contribution reads configured MCP servers without connecting and emits a compact active/inactive server inventory, for example: `MCP servers: Active memory; Inactive docs`;
- `alta mcp activate <server> [<server>...]` marks configured servers active for the current session (falling back to project scope when no session is available) and immediately lists tools for active servers to update activation status;
- a run that activates a server takes its tools at once. `AgentSession` gives every tool call the tools of its run (`AgentRunTools`); `alta mcp activate` adds the direct tools there, and the session offers them with the next model request of the same run, appended after the tools the run started with. No turn is ended and no follow-up run is started. Tools added this way last until the run ends;
- before each agent run, the MCP plugin connects only to activated servers and refreshes their tools;
- every enabled tool on an activated server that passes global/server policy (`enabled`, `allowed_tools`, `disabled_tools`) is exposed as a direct agent tool;
- the deterministic `mcp__<server>__<tool>` alias is used as `AgentToolSpec.Name`, with the same sanitization and collision hashing used by runtime command output;
- the MCP `inputSchema` is passed through as the tool input schema when it can be represented by CodeAlta's strict/OpenAI-compatible tool schema subset; schemas with dynamic object maps or required names outside local `properties` are exposed through an `arguments_json` compatibility string containing the raw MCP argument JSON object;
- direct tool handlers delegate to `McpRuntimeService.CallToolAsync(serverKey, toolName, arguments, ...)`, unwrapping `arguments_json` when needed and preserving tool timeout, redaction, `isError`, structured content, and truncation behavior;
- startup/list-tools diagnostics are reported through per-run MCP prompt guidance without leaking headers, environment values, arguments, or tool output secrets;
- direct tools are enumerated on activation for immediate status feedback and refreshed at agent-run enumeration time. `tool-list-changed` notifications are an optional follow-up only if finite refresh is insufficient;
- direct tool calls of a session share that session's connections, which stay open between calls (see above).

## Deferred/future work

The following remain outside direct MCP tool exposure until separately implemented and tested:

- MCP resources, prompts, elicitation, and user-interaction flows unless required by direct tool invocation;
- timeline display integration beyond the existing generic direct-tool display;
- `tool-list-changed` notifications and automatic dynamic refresh beyond agent-run/plugin-tool enumeration;
- richer Add/Edit server dialog workflows beyond the basic JSON fields currently exposed;
- a process-wide MCP connection manager shared by sessions: connections are kept per session, for its direct tool calls only.
