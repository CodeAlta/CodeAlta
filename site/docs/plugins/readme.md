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
altatui --plugins-status
```

## Shutdown

When `altatui` exits normally, it attempts runtime, provider and model-metadata
cleanup before shutting down plugins started for the application. Runtime
cleanup is best-effort and may report multiple failures together. This is not
a guarantee that a failed or unresponsive plugin has stopped all of its work.

If a terminal frontend cleanup step fails, CodeAlta attempts the remaining
frontend cleanup steps before runtime cleanup and can report their failures
together. A step that does not finish can still delay the rest of shutdown.

For a successfully created terminal frontend, shutdown first stops new reminder
creation, edits and deletions, requests cancellation, and waits for its retained
reminder workers, including workers whose reminders were deleted. A terminal
reminder cleanup failure still allows the remaining cleanup to be attempted;
an unfinished worker can delay it indefinitely. This does not retract captured
sends, remove queued prompts, stop agent runs or drain queued interface actions.
It does not cover reminders created outside the frontend's owned service or
resources hidden by failed frontend construction.

Editor cleanup also attempts the remaining open editors if closing the file picker
or an earlier editor fails. This does not wait for every pending file load, save,
or search, and it does not roll back file changes already made.

If waiting to save a prompt draft fails, its work chain still waits for the
previous draft operation before finishing that failed attempt. This can delay
error reporting and shutdown while an earlier save is pending. It does not
guarantee that every draft is saved: failures while preparing a flush remain a
separate limitation, and file changes already made are not rolled back.

Runtime event-delivery cleanup also attempts its remaining stages after failures,
waiting for its worker before releasing cancellation resources. This does not
drain pending interface updates or stop plugins, and fatal runtime errors can
still terminate the process.

Shell initialization cleanup similarly attempts its remaining stages after errors.
It waits for the initialization task owned by the shell, not every provider
refresh, startup history load, or queued interface action. This is not a
complete-startup shutdown guarantee.

If shared runtime startup fails, CodeAlta also attempts to clean up resources
already acquired during that startup. This cleanup does not remove folders or
files already created, and it does not guarantee that all background work has
stopped.

If you exit the terminal while its shared services are still starting, `altatui`
requests startup cancellation and waits for that startup operation before
finishing cleanup. Services returned after exit are still cleaned up. Shutdown
can therefore take longer when startup or a plugin does not respond to
cancellation; there is no forced-termination timeout.

Normal terminal cleanup also requests cancellation of the background version
check and waits for its operation before releasing its resources. This happens
after application/startup cleanup; an unresponsive earlier cleanup stage can
delay that request. It does not add a forced shutdown deadline or install updates.

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
altatui --no-plugins
altatui --plugin-safe-mode
```

Or set:

```sh
CODEALTA_DISABLE_PLUGINS=1
```

These bypasses are recognized before plugin-contributed command-line options.
