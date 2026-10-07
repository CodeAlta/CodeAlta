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
> The plugin API can change between CodeAlta releases, and some CodeAlta
> capabilities are not exposed to plugins yet.

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
| [Git](git.md) | The issues and the pull requests of GitHub, GitLab, Azure DevOps and Bitbucket repositories, the `#` lookup in a prompt, and the `gh`, `glab` and `az` agent tools when these CLIs are installed. |
| [Jira](jira.md) | The issues of a Jira project, for the projects that name one in their configuration. |
| [MCP](mcp.md) | Model Context Protocol server configuration, `alta mcp` commands, session-activated MCP agent tools, and MCP server management. |
| [Statistics](statistics.md) | Transient per-turn/session statistics timeline cards and a `statistics estimate` live-tool command. |
| [UI tools](../ui-tools.md) | In CodeAlta Desktop: the tools an agent sees and drives the window with, when its session asks for them. |

## Manage plugins

Open plugin management with `Ctrl+G Ctrl+N`, `/plugins`, or `/plugin`.

{{ alta_shot "alta-desktop-plugins.webp" "alta-plugins.png" "Plugin management with the list of plugins" "Plugin management lists the plugins of the global and project scopes and lets you enable or disable them." }}

The desktop **Plugins** page of Settings lists the plugins with a switch to enable or disable each one. A source plugin starts or stops at once; a built-in plugin follows at the next start. A source plugin also has a button to build and reload it while CodeAlta runs and one to edit it in the code editor, and **New plugin** creates one. A plugin that failed to build shows the compiler errors there, and a plugin made for the TUI only is marked as not supported.

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

## Plugins in both apps

CodeAlta Desktop and CodeAlta TUI load the same built-in and source plugins. Agent tools, instructions for agents, `alta` commands, commands, keyboard shortcuts, status items, dialogs, prompt pickers and timeline cards work in both.

Each app shows plugin user interface its own way: the desktop app with its own components and HTML fragments from the plugin, the TUI with terminal controls. A plugin can also be made for one app only; the other app then does not start it.

A source plugin is built when CodeAlta starts. The desktop start-up screen names the plugin being built, and the TUI shows the build in the console before its interface. When a build fails, the desktop app shows a notice and the error in **Settings > Plugins**; the TUI prints it and lists it in `/plugins`.

CodeAlta Desktop also builds a source plugin again while it runs, from **Settings > Plugins** or from a session. An agent can write a plugin for you: see [Plugin development](developers.md#ask-an-agent-to-write-it).

## Source plugins

Dynamic source plugins are discovered from:

- `~/.alta/plugins/<package-id>/plugin.cs` for global plugins;
- `<project>/.alta/plugins/<package-id>/plugin.cs` for project-scoped plugins.

Project-scoped plugins apply only to the matching project, and are loaded when
CodeAlta is started in it. Global plugins apply
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
[plugins.git]
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
