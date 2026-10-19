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
- `McpPolicyWriter` (server and tool toggles in Settings, `alta mcp enable|disable`) changes `config.toml` through `CodeAltaConfigStore.EditConfigFile`, under the lock every save of the configuration stores takes, so a setting another page saves at the same time is kept. It edits the text with `ConfigTableSyntax`: only the `enabled` or `disabled_tools` line of `[plugins.mcp.servers.<server>]` changes (a missing table is added at the end of the file), and the comments and other settings stay as they are. Server keys keep their case, as the policy loader reads them. A server policy written as an inline table or with dotted keys is changed in the file's table model instead, which keeps the settings but not the comments;
- `startup_timeout_ms`, per-server `startup_timeout_ms`, `tool_timeout_ms`, per-server `tool_timeout_ms`, and `max_tool_output_chars` bound runtime operations and output;
- `discover_in_prompt`, `prompt_max_servers`, and `prompt_max_tools` bound MCP prompt guidance;
- `direct_exposure`, `direct_tool_threshold`, and per-server `direct_exposure`/`direct_tools` decide which tools stay loaded for a provider that defers tools (see "Deferred tools" below). Their meaning changed with deferral: they no longer select tools to expose without an activation, they select the tools of the list CodeAlta keeps that are sent loaded instead of deferred. `all` keeps every tool of a server loaded, `none` defers all of them, `allowlist` and `auto` (the default) keep the tools of `direct_tools` (or of `allowed_tools` when a server has no `direct_tools`), and `direct_tool_threshold` (40) is now the most tools that stay loaded in all, in the order of the servers and the tools. `auto` no longer loads every tool while there are few: deferred tools cost nothing in the context, loaded ones do. The older `ListDirectToolsAsync` path, used by tests only, keeps the former meaning. After a server is activated, every enabled MCP tool that passes server/tool policy is exposed as a loaded `AgentToolDefinition` using the stable alias described below.

### Deferred tools

A provider that defers tools (`AgentToolDeferral.IsSupported`: the Claude API with Claude 4.5 or later, or a profile with `supports_tool_deferral = true`) is offered the tools of every enabled MCP server of the session's space that CodeAlta listed before (`McpToolListCache`, read by the fingerprint of the definition, at any age), not only those of activated servers. `McpRuntimeService.ListCachedTools` reads them without starting any server; `McpPlugin.OnBeforeAgentRunAsync` adds them when `PluginBeforeAgentRunContext.ProviderDefersTools` is set (the orchestration asks `PluginOrchestrationBridge.DefersToolsOf`, which the host answers from the provider's runtime descriptor and the model of the run). Those the policy keeps loaded are plain tools; the others are `AgentToolSpec.Deferred`, with `Group` set to their server. The tools of activated servers keep their place before them. A deferred tool is an ordinary MCP direct tool: its first call connects its server through the session's connections. A server CodeAlta never listed is not offered: `alta mcp tool search`, a test or an activation lists it once. A provider that does not defer tools is offered none of these, as before.

### Spaces

`[plugins.mcp.servers.<server>].spaces` (a list of space ids, or one string) assigns a server to spaces, with the rule of `CodeAlta.Catalog.SpaceScope`: absent or empty is every space, a list is only those spaces, and the default space only when `default` is listed. The plugin reads its policy without `CodeAlta.Catalog`, so `McpPolicyOptions.ServerSpaces` reads the same rule itself; a test in `CodeAlta.Tests` checks it against `SpaceAssignments.Parse`.

- Only the **global** `config.toml` assigns spaces: `spaces` in a project `config.toml` is not read, so a repository cannot widen a space. Keys are compared without case, as the host compares them.
- The space applies to the server in effect for a key, whatever file defines it: a project server with the key of a Business-only server is out of Personal too (its OAuth tokens are cached under the key and the URL).
- The space comes before the enablement. `McpRuntimeRequest.SpaceId` is checked first in `GetServerStateAsync`, before the cache of connections: `server_not_in_space`. Tool search/describe/call, kept connections and direct tools go through it. Null applies no space (the user in a terminal).
- The prompt list (`CreatePromptDiscoveryAsync`) leaves out the servers of other spaces, and forgets an activation that is no longer in the space. `OnBeforeAgentRunAsync` drops and forgets such activations.
- `alta mcp activate` and `alta mcp server enable` refuse a server of another space to a session (`server_not_in_space`, nothing activated or written); `alta mcp list` lists the servers of its space. The space of the caller comes from `PluginAltaCommandContext.ResolveSourceSpaceIdAsync`.
- No `alta mcp` command writes `spaces`: `McpPolicyWriter` sets `enabled` or `disabled_tools` only and keeps the rest of the table. Only the window writes `spaces` (`spaces.assignResource`).

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
alta mcp activate <server>|<server>/<tool> [...]
alta mcp server add <server> --command <command> --arg <arg> --env KEY=VALUE --cwd <dir> --scope project
alta mcp server add <server> --url https://example.test/mcp --header Authorization=Bearer... --scope global
alta mcp server remove <server> --scope project
alta mcp server enable <server> --scope project
alta mcp server disable <server> --global
```

`activate` changes the activation state of the session and immediately enumerates tools from active servers so the UI can show whether activation took effect. An id is a server, activated as a whole, or `<server>/<tool>`, one tool of a server (a tool name of MCP has no `/`, a server key may): the activation state keeps servers and (server, tool) pairs, so a server of 50 tools does not bring 50 schemas for one of them. A tool the server does not give, or that the policy turns off, is refused with `tool_not_found` and nothing is activated. When an agent run calls it, the run also records the references `mcp:<server>` and `mcp:<server>/<tool>` with `AgentRunTools.KeepLoaded`: `AgentSession` journals them as a `local.toolLoad` raw event, keeps them in `AgentSessionState.LoadedTools` (rebuilt from the journal at resume, as loaded skills are), and the orchestration gives them to the plugins at each run (`PluginBeforeAgentRunContext.LoadedTools`, read by `SessionRuntimeService.GetLoadedToolsAsync`). The MCP plugin activates them again, so a resumed session, in another run of the application too, has the same tools. When an agent run calls it, the enumerated tools are also registered in that run (`AgentToolInvocation.RunTools`, see below): its result says `toolsAvailable: "now"` and the model calls them in its next step. Called from a terminal, or when a server gave no tool, the result says `"next_run"`. On every later agent run, the same activated servers are refreshed and registered as `mcp__<server>__<tool>` after normal MCP policy filters. `server add` and `server remove` mutate only the selected JSON MCP file of CodeAlta: adding a server that a file of another tool defines overrides it, and removing a key that only such a file defines is refused (`read_only_source`). `server enable` and `server disable` mutate TOML policy only and preserve JSON server definitions, whose file they are in. `list`, `status` and `config sources` say where a definition comes from (`sourceOrigin`: `codealta`, `common`, `copilot` or `vscode`; `readOnly`), `config sources` also lists the files of other tools that exist, and a shadowed definition has its reason (`project-overrides-global` or `same-scope-source-comes-first`) and `overriddenByPath`. Removing a global-only server from inside a project requires `--scope global` so a project context does not accidentally delete global user configuration.

Runtime tool commands:

```text
alta mcp tool search
alta mcp tool search --server <server> --query <text>
alta mcp tool search --refresh
alta mcp tool describe --server <server> --tool <raw-tool-name>
alta mcp tool call --server <server> --tool <raw-tool-name> --arguments {"key":"value"}
alta mcp auth status [--server <server>]
alta mcp auth login <server>
alta mcp auth logout <server>
```

`tool search`, `describe`, and `call` lazily connect to enabled effective servers, list tools, apply policy filters, and return diagnostics plus tool/call records.

**The tool-list cache.** Every listing of a server's tools (an activation, a test, `tool search`, a run) is kept in `~/.alta/cache/mcp/tools/<server>-<hash>.json` (`McpToolListCache`): the name, alias, title, description, arguments and input schema of each tool, under the fingerprint of the definition (command, arguments, folder, address and the names of the variables) and a variant, the SHA-256 of the whole start (the values of the variables and headers and the variables of the session). No value of a variable or header is kept. The search index of CodeAlta Desktop reads the lists by their fingerprint, so search finds the tools of a server without starting it. `tool search` answers from a list less than a day old whose variant is the one of the start it would make, without starting the server; `--refresh` asks every server again (`alta search reindex --kind mcp` does that). Tools are routed by raw MCP server key and raw MCP tool name. Returned records also include the same stable qualified alias used for direct agent-tool exposure, `mcp__<server>__<tool>`; alias parts are sanitized and receive a stable short hash suffix when sanitized names collide.

## Runtime behavior

`McpRuntimeService` owns finite MCP runtime state for direct MCP agent tools, the `alta mcp tool ...` commands, and the dialog's Test Server action. It supports:

- stdio servers launched from JSON `command`, `args`, optional `cwd`, and optional `env`. A server started for a session also gets the variables of the identity of its space (`McpRuntimeRequest.Environment`, see "The identity of a session" in `catalog-and-config.md`), under its own `env`, which wins; the identity is part of the key of the connection (`CreateServerCacheKey`, and the per-session entry of `McpSessionConnections`), so two identities never share a server process. An HTTP/SSE server is not started by CodeAlta and is not keyed on it;
- HTTP/SSE servers using absolute `http`/`https` URLs, SDK transport auto-detection, connection timeout, JSON `headers` with optional `${NAME}` environment-variable expansion, and CodeAlta-managed OAuth/Authv2 tokens when authorized;
- bounded startup/tool discovery with effective global or per-server startup timeout;
- bounded tool calls with effective global or per-server tool timeout;
- disabled-server, missing-server, disabled-tool, missing-tool, invalid transport, invalid URL/header, timeout, unavailable, and authentication diagnostics.

An image block of a tool result is kept for a direct agent tool: the tool returns it with the text of the result, and the session checks it, scales it down when needed, saves it beside the journal and gives it to the model (see "Images in tool results" in `runtime.md`). An image over 32 MB is left out. `alta mcp tool call` output names an image by its media type and size and never holds its bytes. Audio blocks and embedded resources are still summarized as text.

Runtime diagnostics redact sensitive values before they reach command output or the dialog. Redaction covers configured URLs/headers, secret-like dictionary keys, arguments, exceptions, text output, and structured JSON output. An argument is hidden after a flag that names a secret (`--api-key`, `--token`), as the value of such a key (`--token=...`), with a `Bearer`/`Basic` prefix, or as a long value of letters and digits; a path (from `/`, a drive, a UNC share, `~`, `./` or `../`) is shown however long, unless one of its segments reads as a token. An argument shown as `[redacted]` in the editor and saved unchanged keeps its stored value. HTTP 401/403 diagnostics explicitly direct the user to use the dialog **Authorize/Login** action or `alta mcp auth login <server>` for browser OAuth, or to configure static headers (including `${NAME}` environment-variable references when preferred) when appropriate.

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

The loopback callback waits `McpOAuthBrowserAuthorization.DefaultTimeout` (5 minutes) from the moment the address is reported; when it runs out the login ends with the `oauth_login_timeout` diagnostic (status `TimedOut`). The SDK runs the whole authorization inside the first request of the connection, the `server/discover` probe, which it bounds to 5 seconds by default: a browser login therefore passes `McpClientOptions` whose `InitializationTimeout` and `DiscoverProbeTimeout` are the startup timeout plus that wait, and the startup budget is extended by as much. Ordinary connections keep the SDK's defaults and the startup timeout. The address and its deadline reach callers as `McpOAuthBrowserPrompt` (`McpManagementService.LoginOAuthAsync` overload with a prompt callback); callers without one get the status text `Open MCP authorization in your browser within 5 minutes: <url>`, with the address last. `alta mcp auth login` writes the prompt as an `alta.mcp.auth.progress` record with `authorizationUrl`, `expiresAt` and `timeoutSeconds`.

Use the MCP Servers dialog **Authorize/Login** action for a configured HTTP server to open the browser flow. The action opens a small modal login dialog on top of the MCP dialog, shows/copies the login URL when available, and supports **Cancel Login** (`Esc` or `Ctrl+G Ctrl+C`) for stuck or unwanted browser flows. Use **Logout** to delete cached tokens. The CLI fallback is `alta mcp auth login <server>`, `alta mcp auth status`, and `alta mcp auth logout <server>`.

The desktop app has the same flow on the **MCP Servers** page of Settings. The form of a saved HTTP server ends with an **Authorization** block that says whether tokens are stored and until when, with **Authorize** (or **Authorize again**), and **Sign out** to remove the tokens. The `mcpServers` RPC service streams the login as events (`login`: a `prompt` with the address to open and `expiresAt`, which the page shows as a countdown, then `completed` with the number of listed tools or `failed` with a code and the plugin's redacted explanation); closing the channel, the page's **Cancel**, or closing the application cancels it. One login runs at a time, and only for the definition in effect: a definition that another one overrides is refused. The page receives the address and the token's presence and expiry, never a token.

Tool-call results preserve `isError` from MCP. Text content becomes `contentText` and `content` blocks; structured content is included after redaction when it fits the output character budget. Image, audio, embedded-resource, resource-link, and unknown non-text content are summarized instead of embedding raw payloads. Output beyond `max_tool_output_chars` is truncated and marked with `truncated = true`.

Servers that cannot connect or list tools are reported as unavailable for that runtime request and do not contribute enabled tools to activation-time status, activated-agent-tool enumeration, or search/describe/call results. Runtime state is explicit and finite; activated tools are eagerly enumerated when `alta mcp activate ...` runs for immediate status feedback, then refreshed again at agent-run time on the session's connections (see below). `tool-list-changed` notifications are follow-up work only if tool freshness requires them.

The tools a session calls keep their server connected (`McpSessionConnections`). A server can hold state from one call to the next, such as a browser snapshot whose element ids the next call uses, and starting it for every call costs seconds. Each session has its own connections, used by one call or listing at a time, opened by its first use. The tool listing at the start of each run of a session goes through them too (`McpSessionConnections.ListToolsForServersAsync`): the server started to list the tools is the one the calls of the run use, and a server the session keeps connected answers the next run without being started again (before, each run started every activated server once more only to list its tools). A kept connection answers a listing as a new start would (`McpRuntimeService.ReviseKeptStateAsync`): the tool flags come from the policy of now, a connection whose server exited (`McpClient.Completion`) or that the policy turned off is closed and the server started again or refused with its usual diagnostic, and a server that could not be started is forgotten after the listing (`ForgetFailedServers`), so the next call or run tries again. The list of tools stays the one the server gave when it connected, which is also what its calls are resolved against; a changed definition or identity is another key, so it never reuses a process of the old one. The connections are closed when a call could not be completed (the server did not start, the call timed out or the connection broke: the next call connects again), after fifteen minutes without a call or a listing, and when the plugin stops. `alta mcp tool ...` commands, activation and a run without a session still use a connection of their own that ends with them. Sessions never share a server process.

**Reading the configuration.** The plugin reads the MCP files and the MCP policy of `config.toml` through one `McpConfigCache`, shared by its runtimes, its session connections, its commands and its prompt: every tool call, run start and prompt build used to read and parse the two files of CodeAlta, the four files of other tools and the two `config.toml`, and to probe whether the files of CodeAlta could be written (creating and deleting a file next to a missing one). The cache keeps a discovery and a policy under the stamp of each of their files (whether it exists, its size, the time it was last written, and for a file of CodeAlta whether its folder exists), taken before the files are read: another stamp reads them again at the next request. A file written less than three seconds ago (`McpConfigCache.SettleTime`) is read at each request until it is older, because a file system that keeps coarse times could give a second write of the same size the same stamp. The writes of the plugin (its `alta mcp server ...` commands and the management service of its presentation) also invalidate the cache. A file that cannot be read or parsed is never kept: the next request reads it again and fails as before. The runtime never probes writability (it never writes the files) and reads the files of CodeAlta whole, as before; the prompt reads the servers and the policy as a management snapshot did, without its OAuth token reads. OAuth token status is read with synchronous file I/O where a snapshot is built synchronously (`McpOAuthTokenCache.GetStatus`) and awaited in the commands, never by blocking on the asynchronous read.

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
- `alta mcp activate <server>|<server>/<tool> [...]` marks configured servers, or single tools of them, active for the current session (falling back to project scope when no session is available) and immediately lists tools for active servers to update activation status. An agent's activation is kept in the session's journal and restored at each run, a resumed session included; `alta search load <ref>…` activates the MCP tools of search hits through it;
- a run that activates a server takes its tools at once. `AgentSession` gives every tool call the tools of its run (`AgentRunTools`); `alta mcp activate` adds the direct tools there, and the session offers them with the next model request of the same run, appended after the tools the run started with. No turn is ended and no follow-up run is started. Tools added this way last until the run ends;
- before each agent run, the MCP plugin lists the tools of the activated servers on the session's connections: it starts a server only when the session does not keep it connected, and applies the policy of now;
- every enabled tool on an activated server that passes global/server policy (`enabled`, `allowed_tools`, `disabled_tools`) is exposed as a direct agent tool; a server with only some tools activated gives only those. The prompt block lists the servers activated as a whole as active: loading single tools leaves its text as it is;
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
