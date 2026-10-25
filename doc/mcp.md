# MCP support

MCP support is the built-in plugin `mcp` (`src/CodeAlta.Plugin.Mcp`), on the public plugin contracts like any other
(see [plugins](plugins.md)). It reads the configured servers, gives agents their tools progressively, runs the calls,
and contributes `alta mcp`. The window's pages (`mcpServers`, `mcpApps` and `skills` RPC services in
`src/CodeAlta/Desktop`) and the TUI dialog (`src/CodeAlta.Tui`) are presentations over its `McpManagementService` and
`McpRuntimeService`; reusable MCP code stays in the plugin, never in a frontend.

## Configuration: definitions and policy

Server definitions (how to start or reach a server) and policy (whether and how CodeAlta uses it) are kept apart:

| What | Files |
| --- | --- |
| Definitions written by CodeAlta | `<project>/.alta/mcp.json`, then `~/.alta/mcp.json` |
| Definitions of other tools, read only | `<project>/.mcp.json`, `.github/mcp.json`, `.vscode/mcp.json`; `~/.copilot/mcp-config.json` |
| Policy | `[plugins.mcp]` of `~/.alta/config.toml`, overlaid by the project's `config.toml` |

- **One definition per key.** A project comes before the user, and in a scope CodeAlta's file before the files of other
  tools. Other definitions of the key are reported as shadowed, never connected twice.
- **Other tools' files are theirs.** CodeAlta reads them leniently (comments, the bare `servers` root, VS Code
  variables translated by `McpExternalVariables`) and never writes them: saving such a server writes a definition of
  the same key to CodeAlta's file, which then comes first. A server whose value only VS Code can supply (`${input:…}`,
  an `envFile`, an unknown variable) is listed as not supported rather than started with a wrong value.
- **Policy never rewrites definitions.** Enabling a server or a tool changes only `config.toml`, through
  `McpPolicyWriter`, which edits the one line it owns and keeps comments. Definitions stay shareable with other tools.
- **Variables.** `${NAME}` and `${NAME:-default}` in command, arguments, folder, URL, environment and headers are
  resolved when the server starts; a missing one is a diagnostic, never the literal placeholder sent to a server.
- **Spaces.** `[plugins.mcp.servers.<key>] spaces` is read from the global file only, so a repository cannot widen a
  space. The space is checked before enablement and before any cached connection (`server_not_in_space`), for tools,
  activations, prompt and calls alike.

`McpConfigCache` keeps the parsed files and policy under the stamp of each file (existence, size, write time), shared
by runtimes, connections, commands and the prompt: every tool call and run start used to read and parse eight files.
A file written in the last three seconds is read again at each request, because coarse file times could hide a
second write of the same size; a file that cannot be read is never cached.

## Tools for agents

Exposure is progressive, so a session pays in context only for the servers it uses:

1. The prompt lists the configured servers as active or inactive, without connecting to any.
2. `alta mcp activate <server>|<server>/<tool>` activates a server, or one tool of it, for the session. The tools are
   listed at once and registered into the running turn (`AgentRunTools`): the model calls them in its next step.
   The activation is journaled (`AgentRunTools.KeepLoaded`, `local.toolLoad`) and restored at every run, also after a
   restart.
3. Each tool that passes policy (`enabled`, `allowed_tools`, `disabled_tools`) is an agent tool named
   `mcp__<server>__<tool>`, sanitized with a stable hash suffix on collision. Its MCP `inputSchema` is passed through
   when CodeAlta's strict tool schema can express it, else as an `arguments_json` string.

**Deferred tools.** A provider that supports tool search (see [providers](providers.md)) is offered the tools of every
enabled server of the space, not only activated ones: deferred, grouped by server, from the tool-list cache, without
starting any server. `direct_exposure`, `direct_tools` and `direct_tool_threshold` decide which stay loaded. A deferred
tool connects its server on first call. A server CodeAlta never listed is not offered.

**The tool-list cache** (`McpToolListCache`, `~/.alta/cache/mcp/tools/`) keeps each listing under the fingerprint of
the definition and a variant of its start. It keeps the names of variables and headers, never their values. Search
and deferred tools read it without starting servers.

## Runtime

`McpRuntimeService` runs stdio servers (the command with its environment) and HTTP/SSE servers, with startup and tool
timeouts, output bounds and finite diagnostics. Every request is bounded: no server can hang a turn or a command.

- **Connections belong to a session** (`McpSessionConnections`). A server may keep state between calls (a browser
  snapshot whose element ids the next call uses), and starting one costs seconds, so a session keeps its servers
  connected across calls and runs, one call at a time, closed after 15 idle minutes, after a call that failed to
  complete, and when the plugin stops. Sessions never share a server process: a stdio server gets the environment of
  the identity of its session's space, and that identity is part of the connection key. Commands without a session
  use a connection that ends with them.
- **Redaction.** Diagnostics, command output and dialogs redact URLs, headers, secret-like keys and arguments,
  exceptions and output. A 401 or 403 points to browser authorization or static headers.
- **OAuth.** HTTP servers that follow MCP authorization sign in through the browser with a loopback callback. Tokens
  live in `~/.alta/auth/mcp/`, never in `mcp.json` or policy, and no output shows a token. Agent runs and ordinary
  commands only use cached or refreshable tokens: they never open a browser by surprise.
- **Results.** Text and structured content are redacted and bounded (`max_tool_output_chars`). Images of a result
  reach the model through the session (see [runtime](runtime.md)); other non-text content is summarized.

## MCP Apps (CodeAlta Desktop)

A tool may name a `ui://` resource: an HTML view its server shows for its calls. A host that shows views
(`McpPluginOptions.ShowsApps`, the desktop only) declares the extension in `initialize`.

- A tool whose visibility lacks `model` is for its view only: it is left out of every list the model gets and a model
  call of it is refused, in every host.
- The view's data (structured content and `_meta` of a result) is kept on the call's activity record for the view,
  not redacted, and never sent to the model.
- The desktop serves the view on the `mcpapp` scheme (`DesktopMcpApps.cs`) in a sandboxed frame, with a content
  security policy built from the view's declaration and widened only by checked origins (`DesktopMcpAppPolicy`).
  Requests of the view run on the session's connection with the checks of a model call, and a view may only call tools
  whose visibility includes `app`.
- A view may send a prompt only while the user is interacting with it (it had the keyboard in the last 15 seconds),
  rate limited: a view never speaks on its own.

## MCP skills (CodeAlta Desktop)

Servers may publish skills (the `io.modelcontextprotocol/skills` extension, `Skills/`). They are a second origin
beside the skill folders (see [skills](skills.md)): named by server and URI, never shadowing a local skill.

- **Untrusted until checked.** An entry is kept only when it is well formed (`McpSkillValidator`: name, paths inside
  the skill, `sha256` digests, bounded size); the bytes of every file read must match its size and digest, and the
  frontmatter of `SKILL.md` must equal the entry (`McpSkillVerifier`). Anything else is refused, never shown to the
  model.
- **Listing never reads files**: entries are cached under `~/.alta/cache/mcp/skills/`, and the prompt and Settings read
  that cache without starting a server. The prompt marks them as untrusted instructions from servers.
- **Loading always asks the user**, in every permission mode: a server can change what it serves, and its skill is
  instructions to the agent. **Allow this skill** is remembered bound to the digests of all its files
  (`McpSkillApprovalStore`), so any change asks again; a skill without digests is allowed once only.
- **Nothing of a skill is written to disk**: checked content is cached in memory (`McpSkillContentCache`). The loaded
  entry lives per session for the life of the process; after a compaction the instructions say how to load it again
  instead of carrying its text.

## Not supported

MCP resources other than skill files, prompts, elicitation, sampling, `tools/list_changed` from servers (tools are
listed again at each run), a server's `instructions` in the prompt, and connections shared between sessions.
