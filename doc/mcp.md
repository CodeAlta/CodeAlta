# MCP support

CodeAlta ships MCP support as a trusted built-in plugin (`CodeAlta.Plugin.Mcp`). The surface is configuration discovery, policy overlay, session-activated MCP agent tools, prompt guidance, `alta mcp ...` commands, explicit runtime calls through `alta mcp tool ...`, and the TUI MCP Servers dialog. Dynamic agent-tool registration exposes enabled/config-controlled MCP tools only for MCP servers activated in the current session.

## Configuration files and overlay

MCP server connection fields live only in fixed JSON MCP config files:

| Scope | Path | Notes |
| --- | --- | --- |
| Global | `~/.alta/mcp.json` | Loaded first. |
| Project | `<project>/.alta/mcp.json` | Loaded only when a project directory is active; overlays global. |

CodeAlta uses one JSON file per scope and does not scan editor/provider-specific MCP locations. Effective servers are keyed by the raw server key. When a project file contains the same key as a global file, the project definition wins and the global definition is reported as shadowed instead of producing a duplicate connection or duplicate tools.

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

A single file containing both `mcpServers` and `servers` is invalid because its flavor is ambiguous. A server definition must choose exactly one transport: `command` for stdio or `url` for HTTP/SSE. Stdio servers can include `args`, `cwd`, and `env`; HTTP/SSE servers can include `headers`. String values in stdio `env` entries and HTTP/SSE `headers` may reference process environment variables with `${NAME}` placeholders, for example `"Authorization": "Bearer ${GITHUB_PERSONAL_ACCESS_TOKEN}"`; missing variables produce a finite MCP diagnostic instead of sending the literal placeholder.

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

`activate` mutates only in-memory session activation state; on the next agent run, tools from activated servers are registered as `mcp__<server>__<tool>` after normal MCP policy filters. `server add` and `server remove` mutate only the selected JSON MCP file. `server enable` and `server disable` mutate TOML policy only and preserve JSON server definitions. Removing a global-only server from inside a project requires `--scope global` so a project context does not accidentally delete global user configuration.

Runtime tool commands:

```text
alta mcp tool search
alta mcp tool search --server <server> --query <text>
alta mcp tool describe --server <server> --tool <raw-tool-name>
alta mcp tool call --server <server> --tool <raw-tool-name> --arguments {"key":"value"}
```

`tool search`, `describe`, and `call` lazily connect to enabled effective servers, list tools, apply policy filters, and return diagnostics plus tool/call records. Tools are routed by raw MCP server key and raw MCP tool name. Returned records also include the same stable qualified alias used for direct agent-tool exposure, `mcp__<server>__<tool>`; alias parts are sanitized and receive a stable short hash suffix when sanitized names collide.

## Runtime behavior

`McpRuntimeService` owns finite MCP runtime state for direct MCP agent tools, the `alta mcp tool ...` commands, and the dialog's Test Server action. It supports:

- stdio servers launched from JSON `command`, `args`, optional `cwd`, and optional `env`;
- HTTP/SSE servers using absolute `http`/`https` URLs, SDK transport auto-detection, connection timeout, and JSON `headers` with optional `${NAME}` environment-variable expansion;
- bounded startup/tool discovery with effective global or per-server startup timeout;
- bounded tool calls with effective global or per-server tool timeout;
- disabled-server, missing-server, disabled-tool, missing-tool, invalid transport, invalid URL/header, timeout, unavailable, and authentication diagnostics.

Runtime diagnostics redact sensitive values before they reach command output or the dialog. Redaction covers configured URLs/headers, secret-like dictionary keys, arguments, exceptions, text output, and structured JSON output. HTTP 401/403 diagnostics explicitly direct the user to configure headers (including `${NAME}` environment-variable references when preferred) or complete auth outside CodeAlta; OAuth UX is not implemented.

Tool-call results preserve `isError` from MCP. Text content becomes `contentText` and `content` blocks; structured content is included after redaction when it fits the output character budget. Image, audio, embedded-resource, resource-link, and unknown non-text content are summarized instead of embedding raw payloads. Output beyond `max_tool_output_chars` is truncated and marked with `truncated = true`.

Servers that cannot connect or list tools are reported as unavailable for that runtime request and do not contribute enabled tools to activated-agent-tool enumeration or search/describe/call results. Runtime state is explicit and finite; activated tools are refreshed by enumerating policy-controlled tools at agent-run time. `tool-list-changed` notifications and a process-wide long-lived MCP connection manager are follow-up work only if tool freshness requires them.

## TUI dialog and status indicator

Open the MCP Servers dialog through any of these entry points:

- `/mcp` in the shell prompt;
- command palette entry **MCP Servers**;
- `Ctrl+G Ctrl+Y` (not `Ctrl+G Ctrl+M`, because some terminals report Enter as `Ctrl+M`);
- the clickable MCP status indicator when a cached MCP snapshot exists and MCP JSON/TOML configuration is present.

The dialog and status indicator share `McpManagementService`. `Refresh` reads the fixed JSON config files and TOML policy without connecting to servers. The server list includes effective servers, disabled servers, invalid/missing config rows, and global definitions shadowed by project config. Counts are cached snapshot counts; enabled configured servers with no cached test result start a background, cancellable tool discovery when selected so the **Tools (N)** tab is prefilled without blocking rendering, close, or application shutdown.

For a selected configured server, the dialog shows compact enablement/actions above the tab strip. The first **Config** tab contains editable JSON server fields. The **Tools (N)** tab contains discovered tool policy controls. The following **Details** tab contains normalized/redacted connection fields, source scope/path/format, policy values, and diagnostics. Rendering and refresh use cached/discovered config snapshots and do not synchronously connect to servers; background prefill and the explicit **Test** action connect to the selected stdio or HTTP/SSE server with configured timeouts, update cached exposed/total tool counts, and surface success, failure, timeout, cancellation, and auth/unavailable diagnostics without blocking normal config refresh/rendering. The top-level **Add**, **Save**, and **Remove** actions create/update/remove JSON server definitions in the selected project/global scope. Editable arguments use semicolon-separated values; env/header fields use semicolon-separated `KEY=VALUE` pairs and support `${NAME}` environment-variable placeholders. Redacted placeholders must be replaced before saving so the dialog does not overwrite secrets with `[redacted]`. The compact Enabled checkbox writes server `enabled` policy. The discovered tools table shows Enabled, raw MCP name/title, and policy status. Toggling a tool writes/removes that raw tool name in `disabled_tools`; it does not edit JSON server definitions.

Use `alta mcp server add/remove/...` or **Open JSON Config** for advanced JSON shapes not exposed by the dialog form.

## Plugin and prompt boundaries

The MCP implementation is a built-in trusted plugin, enabled by default through the same built-in plugin infrastructure as the GitHub and statistics plugins. It contributes:

- an `alta mcp` root command with mutating command policy;
- compact dynamic developer prompt guidance that advertises active/inactive configured MCP servers and the `alta mcp activate <id>*` path;
- direct `AgentToolDefinition` contributions for enabled/config-controlled MCP tools on session-activated servers;
- shared discovery/runtime services consumed by direct tools and `alta`, plus management snapshots consumed by the TUI dialog, status indicator, and prompt guidance.

Reusable MCP configuration, policy, runtime, and management code lives in `src/CodeAlta.Plugin.Mcp/`. TUI-specific composition, status indicator rendering, and the dialog live in `src/CodeAlta/`. Do not move reusable MCP runtime orchestration into frontend controls.

## Progressive MCP agent-tool behavior

Progressive dynamic `AgentToolDefinition` exposure is shipped behavior:

- the MCP plugin prompt contribution reads configured MCP servers without connecting and emits a compact active/inactive server inventory, for example: `MCP servers: Active memory; Inactive docs`;
- `alta mcp activate <server> [<server>...]` marks configured servers active for the current session (falling back to project scope when no session is available);
- before each agent run, the MCP plugin connects only to activated servers and lists their tools;
- every enabled tool on an activated server that passes global/server policy (`enabled`, `allowed_tools`, `disabled_tools`) is exposed as a direct agent tool;
- the deterministic `mcp__<server>__<tool>` alias is used as `AgentToolSpec.Name`, with the same sanitization and collision hashing used by runtime command output;
- the MCP `inputSchema` is passed through as the tool input schema;
- direct tool handlers delegate to `McpRuntimeService.CallToolAsync(serverKey, toolName, arguments, ...)`, preserving tool timeout, redaction, `isError`, structured content, and truncation behavior;
- startup/list-tools diagnostics are reported through per-run MCP prompt guidance without leaking headers, environment values, arguments, or tool output secrets;
- direct tools refresh at agent-run enumeration time. `tool-list-changed` notifications and a long-lived MCP connection manager are optional follow-ups only if finite refresh is insufficient.

## Deferred/future work

The following remain outside direct MCP tool exposure until separately implemented and tested:

- OAuth and interactive authentication UX for HTTP/SSE MCP servers;
- MCP resources, prompts, elicitation, and user-interaction flows unless required by direct tool invocation;
- timeline display integration beyond the existing generic direct-tool display;
- `tool-list-changed` notifications and automatic dynamic refresh beyond agent-run/plugin-tool enumeration;
- richer Add/Edit server dialog workflows beyond the basic JSON fields currently exposed;
- a process-wide long-lived MCP connection manager unless direct-tool performance/freshness data justifies it.
