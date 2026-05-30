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

## Built-in plugins

CodeAlta ships trusted built-in plugins through the same plugin runtime used
for source plugins:

{.table}
| Plugin | What it adds |
|---|---|
| [GitHub](github.md) | `#` issue lookup in GitHub repositories and an optional `gh` agent tool when the GitHub CLI is installed. |
| [MCP](mcp.md) | Model Context Protocol server configuration, `alta mcp` commands, session-activated MCP agent tools, and the MCP Servers dialog. |
| [Statistics](statistics.md) | Transient per-turn/session statistics timeline cards and a `statistics estimate` live-tool command. |

## Manage plugins

Open plugin management with `Ctrl+G Ctrl+N`, `/plugins`, or `/plugin`.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-plugins.png" alt="CodeAlta plugin management dialog with plugin list, diagnostics, and selected plugin contributions" loading="lazy">
  <figcaption class="small text-secondary mt-2">Plugin management keeps discovered plugins, diagnostics, source actions, and contribution summaries visible in one dialog.</figcaption>
</figure>

The dialog shows:

- plugin scope and state;
- diagnostics from discovery, config, build, load, activation, contributions,
  callbacks, source changes, and unload;
- contribution summaries;
- unknown config entries;
- source and README open actions when available.

You can also use a headless status summary:

```sh
alta --plugins-status
```

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

When a plugin is broken, start CodeAlta with a bypass:

```sh
alta --no-plugins
alta --plugin-safe-mode
```

Or set:

```sh
CODEALTA_DISABLE_PLUGINS=1
```

These bypasses are recognized before plugin-contributed command-line options.
