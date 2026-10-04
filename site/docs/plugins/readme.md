---
title: Plugins
---

# Plugins

CodeAlta plugins are trusted source packages and built-in extensions that can
extend the shell, prompt flow, agent runtime, timeline projections, and the
in-session `alta` live tool.

Plugins are for local automation you choose to run. Building a source plugin
can execute SDK/NuGet/MSBuild logic, and loading it executes .NET code inside
the CodeAlta process.

> [!WARNING]
> Install and enable only plugins you trust. Source plugins are built on your
> machine and loaded into the CodeAlta process, so a plugin has the same
> practical risk profile as running local code.

> [!IMPORTANT]
> Plugin APIs are preview surface area before CodeAlta `1.0`. Interfaces,
> contribution points, service exposure, and behavior can change between `0.x`
> releases; some CodeAlta capabilities may not be exposed yet, and some exposed
> capabilities may still be incomplete or incorrectly shaped.

## Extensibility layers

Plugins are one part of CodeAlta extensibility, not the first tool for every workflow.

{.table}
| Need | Prefer |
|---|---|
| A repeatable session workflow or mode | [Agent prompts](../prompts.md) |
| Agent coordination with sessions, asks, notes, reminders, prompts, providers, or skills | [Advanced agent workflows](../advanced-agent-workflows.md) |
| External tools from a standard protocol | [MCP servers](mcp.md) |
| Reusable context that does not execute code | Skills from the [workspace skills dialog](../workspace.md#skills-management) |
| Host UI, runtime, prompt, timeline, resource, or custom live-tool command extension | Trusted source plugins |

Use plugins when you need trusted .NET code loaded into CodeAlta. If a workflow can be expressed as an agent prompt, MCP configuration, or skill package, that path is usually easier to inspect and review.

## Built-in plugins

CodeAlta ships trusted built-in plugins through the same plugin runtime used
for source plugins:

{.table}
| Plugin | What it adds |
|---|---|
| [GitHub](github.md) | `#` issue lookup in GitHub repositories and an optional `gh` agent tool when the GitHub CLI is installed. |
| [MCP](mcp.md) | Model Context Protocol server configuration, `alta mcp` commands, session-activated MCP agent tools, and MCP server management. |
| [Statistics](statistics.md) | Transient per-turn/session statistics timeline cards and a `statistics estimate` live-tool command. |

## Manage plugins

Open plugin management with `Ctrl+G Ctrl+N`, `/plugins`, or `/plugin`.

{{ alta_shot "alta-desktop-plugins.webp" "alta-plugins.png" "Plugin management with the list of plugins" "Plugin management lists the plugins of the global and project scopes and lets you enable or disable them." }}

The desktop **Plugins** page of Settings lists the plugins with a switch to enable or disable each one. A change applies at the next start.

The TUI dialog shows:

- plugin scope and state;
- diagnostics from discovery, config, build, load, activation, contributions,
  callbacks, source changes, and unload;
- contribution summaries;
- unknown config entries;
- source and README open actions when available.

You can also use a headless status summary:

```sh
altatui --plugins-status
```

## Plugins in the desktop app

The desktop app loads the same built-in and source plugins as the TUI. Agent tools, agent instructions, `alta` live-tool commands, prompt input processing, session status items and the Statistics timeline cards work in both apps.

A plugin can also contribute user interface to the TUI. These contributions are not shown in the desktop app yet:

- dialogs and other custom visuals;
- commands and keyboard shortcuts;
- prompt editor attachments;
- timeline cards from source plugins.

Plugin build problems at startup are reported in **Settings > Application Logs**.

## Source plugins

Dynamic source plugins are discovered from:

- `~/.alta/plugins/<package-id>/plugin.cs` for global plugins;
- `<project>/.alta/plugins/<package-id>/plugin.cs` for project-scoped plugins.

Project-scoped plugins apply only to the matching project. Global plugins apply
across workspaces. For source-plugin layout, examples, contribution points,
resource roots, prompt editor attachments, `alta` command integration, and safe
authoring guidance, see [Plugin development](developers.md).

## Disable or bypass plugins

Source plugins and built-in plugins are enabled by default when discovered.
Disable a plugin in TOML when you do not want it built or loaded:

```toml
[plugins.HelloWorld]
enabled = false
```

Built-in plugin IDs are lowercase, for example:

```toml
[plugins.github]
enabled = false

[plugins.mcp]
enabled = false

[plugins.statistics]
enabled = false
```

When a plugin is broken, start the TUI with a bypass:

```sh
altatui --no-plugins
altatui --plugin-safe-mode
```

Or set this environment variable, which is also the way to start the desktop app without plugins:

```sh
CODEALTA_DISABLE_PLUGINS=1
```

These bypasses are recognized before plugin-contributed command-line options.
