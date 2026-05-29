# Plugins

CodeAlta plugins are trusted local .NET code loaded into the CodeAlta process. The public authoring API lives in `src/CodeAlta.Plugins.Abstractions`; discovery, source builds, loading, activation, contribution registration, and diagnostics live in `src/CodeAlta.Plugins`.

## Authoring surface

A plugin usually references only `CodeAlta.Plugins.Abstractions`, inherits from `PluginBase`, and overrides the contribution methods it needs.

```csharp
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI.Controls;
using CliCommand = XenoAtom.CommandLine.Command;

[Plugin(DisplayName = "Hello Plugin", Description = "Adds a command, prompt note, and status row.")]
public sealed class HelloPlugin : PluginBase
{
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Prompt(
            "hello",
            "Show a hello notification.",
            static async (context, cancellationToken) =>
            {
                await context.Ui.NotifyAsync("Hello from a plugin.", cancellationToken);
                return PluginCommandResult.Handled;
            });
    }

    public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
    {
        yield return Prompt.Developer("When the user asks about the hello plugin, explain that it is installed.");
    }

    public override IEnumerable<XenoAtom.CommandLine.CommandNode> GetCommandLineContributions()
    {
        yield return new CliCommand("hello", "Run hello plugin command-line actions.");
    }

    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        yield return PluginUi.Status("Hello", static _ => "hello plugin active");
        yield return PluginUi.Visual(PluginUiRegion.SessionFooter, static _ => new Markup("[dim]Hello plugin[/]"));
    }
}
```

Authoring rules:

- A plugin is a visible concrete non-generic `PluginBase` subclass with a public parameterless constructor.
- `PluginAttribute` metadata is optional; the runtime can derive descriptor data from the assembly and type.
- A single assembly may contain multiple plugin classes.
- `readme.md` package documentation is optional and is not required for discovery or activation.
- `PluginScope` is assigned by the runtime from the load location: user root packages are global; project root packages are project-scoped.
- Contributions are declarative records returned by virtual methods. The runtime owns contribution handles, registration, removal, diagnostics, and ordering.
- Plugins receive `PluginRuntimeContext`, `IPluginServices`, and a `XenoAtom.Logging.Logger` owned by the host.

## Source plugin layout

Source plugins are discovered from:

- `~/.alta/plugins/<package-id>/plugin.cs` for global plugins;
- `<project>/.alta/plugins/<package-id>/plugin.cs` for project-scoped plugins.

For each plugin root, CodeAlta owns generated root-level build files:

- `Directory.Build.props`
- `Directory.Build.targets`
- `Directory.Packages.props`
- `global.json`

The generated `global.json` selects the .NET 10 SDK used for file-based C# builds. CodeAlta invokes `dotnet build plugin.cs` from the plugin package directory and records the resolved output assembly in a plugin build manifest for later loads. Source plugin packages should not contain their own root-level build files with the names above; the runtime diagnoses those files as unsupported for source-folder plugins.

Source plugins are trusted code. Building a plugin can execute SDK, NuGet, and MSBuild logic. Loading a plugin executes .NET code in the CodeAlta process through a collectible `AssemblyLoadContext`.

## Enablement and safe mode

Discovered source plugins are enabled by default. Disable a plugin in TOML when it should not build or load:

```toml
[plugins.HelloWorld]
enabled = false
```

Safe mode disables source plugin discovery/build/load before plugin-contributed command-line options run. Use one of:

```text
--no-plugins
--plugin-safe-mode
CODEALTA_DISABLE_PLUGINS=1
```

`CODEALTA_DISABLE_PLUGINS=true` is also accepted by the runtime config parser.

## Runtime lifecycle

The plugin runtime:

1. resolves global and project plugin roots;
2. reads global/project config;
3. discovers source packages and built-in plugin definitions;
4. generates plugin-root build files;
5. builds source packages when needed;
6. loads plugin assemblies into collectible contexts;
7. creates plugin instances;
8. initializes and activates plugins;
9. materializes contribution records with runtime-owned handles;
10. monitors source/config changes and emits diagnostics;
11. cancels tracked plugin tasks and unloads contexts on shutdown or reload.

Runtime status separates plugin diagnostics from conversation history. Diagnostics include config, discovery, build, load, activation, contribution, callback, source-change, and unload records plus structured build summaries and unknown config entries.

Open plugin management with `Ctrl+G Ctrl+N` or the command palette (search for `plugins` or `plugin`). The dialog shows enablement, diagnostics, properties, contributions, and source/README actions. `--plugins-status` provides a headless discovery/config summary.

## Contribution areas

`PluginBase` exposes virtual methods for:

- startup hooks and command-line contributions;
- shell, prompt, and selected-session commands;
- agent tools;
- `alta` live command roots;
- static and dynamic system/developer prompt parts;
- prompt processors, prompt-editor attachments, and system/developer prompt parts;
- before-agent-run hooks;
- tool-call and tool-result hooks;
- normalized agent-event observers;
- compaction hooks;
- UI contributions such as status rows, visuals, dialogs, and renderers;
- transient session/timeline projections (current APIs still use some legacy `Session` names);
- resource roots for skills, system prompts, templates, themes, and MCP manifests;
- plugin-lifetime background tasks through `IPluginTaskService`.

Plugin shell commands are no-argument frontend activations. A `PluginCommandContribution` declares its name, label/description, placement (`ShellRoot`, `PromptEditor`, and/or `WorkspaceRoot`), command-palette/search metadata, visibility flags, optional shortcut, and availability rule. CodeAlta adapts each active contribution into the same internal shell command registry used by built-ins, so plugin commands can appear in help, the command palette, command bars, and shortcuts without frontend-specific registration code.

`PluginCommandContext` intentionally exposes public services such as `Ui`, `Sessions`, `Prompts`, and `Workspace`; it does not expose raw slash-command text or argument tokens. Commands that need user input should use plugin UI services, prompt/session services, or a plugin-owned dialog/workflow contribution. Frontend state is mapped to public plugin operation options before the handler runs; plugins never receive internal `ShellCommandContext`, XenoAtom `Visual` targets, or frontend view models.

Low-ceremony factories are available through `Command`, `Startup`, `Prompt`, `PluginUi`, `Resources`, and `AgentTool`.

UI-only contributions remain frontend responsibilities. Headless hosts can ignore them or expose no-op services through `IPluginUiService.HasInteractiveUi == false`.

## Prompt-editor attachments

Plugins can implement `PluginBase.GetPromptEditorContributions()` to attach plugin-owned behavior to prompt editors. The host exposes only a small editor host (`Text`, `CaretIndex`, `ProjectPath`, editor-state/accepted events, focus, and the editor visual as an anchor); the plugin owns trigger detection, popup/dialog/control choices, insertion behavior, and any plugin-specific presentation. This keeps CodeAlta from hardcoding a generic issue picker or recreating `XenoAtom.Terminal.UI` abstractions in the plugin API.

Prompt-editor contributions can set `PluginPromptEditorContribution.PlaceholderText` to add a short segment to the ready prompt placeholder while the contribution applies. Phrase it like the built-in segments, for example `[#] to reference a GitHub issue`; CodeAlta inserts plugin segments after project-file guidance and before send/new-line/steer guidance.

Keep attachments cancellable and avoid long synchronous work so typing in the prompt stays responsive. Headless hosts can skip prompt-editor attachments.

## `alta` live-tool integration

Plugins can extend the in-session `alta` tool by overriding `PluginBase.GetAltaCommands()` and returning `PluginAltaCommandContribution` records. Each contribution declares a command path, policy flags, ordering, and a factory that creates a fresh unattached `XenoAtom.CommandLine.CommandNode` for every registry build.

The host reserves core command roots and rejects collisions. Plugin command policies describe whether the command mutates state, is disruptive, requires the in-process runtime, or can run with catalog-only services. Mutating plugin-originated commands include plugin provenance.

Plugins can call built-in `alta` commands through `Services.Alta.InvokeAsync(...)`:

```csharp
var result = await Services.Alta.InvokeAsync(
    ["session", "create", "--project", projectId],
    cancellationToken: cancellationToken);
```

The service returns the same flattened JSONL transcript shape used by agent live-tool calls, plus exit code, truncation status, and error summary. Project-scoped plugin invocations inherit project scope and working directory by default.

See [`alta` live tool](live-tool.md) for command behavior.

## Resource contributions

Resource roots expose plugin package content to host services:

```csharp
public override IEnumerable<PluginResourceContribution> GetResources()
{
    yield return Resources.SkillRoot("skills");
    yield return Resources.SystemPromptRoot("prompts");
}
```

Relative paths are resolved from the plugin package directory. Project-scoped plugin resources are visible only in matching project scope.

## Session-event projections

Plugins can contribute transient derived timeline cards through `GetSessionEventProjections()` (legacy API name). Projections are replayed from canonical normalized event history and can also run live as new events arrive. They may provide Markdown fallback content, XenoAtom visuals, collapsed detail sections, and dynamic content that starts with a placeholder and refreshes after background computation.

Projection output is not written to canonical conversation history. Store plugin-owned durable state through `IPluginStateStore` when a plugin needs persistence.

## Background tasks and unload

Long-running plugin work should use `Services.Tasks.Run(...)` or the `PluginBase.Tasks` shortcut instead of untracked `Task.Run`. The runtime tracks task handles, cancels them during deactivation, and can delay unload while tracked work completes.

Unload can still fail if plugin code keeps static references, host-static delegates, pinned native resources, or untracked background work alive.

## Built-in plugins

Built-ins use the same abstraction model. The GitHub plugin is packaged as `CodeAlta.Plugin.GitHub`, is enabled by default, and adds `#` issue lookup for projects whose Git remotes point at `github.com`. It inserts links like `[#18](https://github.com/org/repo/issues/18)`, uses the GitHub REST API through `HttpClient`, and exposes the `gh` agent tool only when the GitHub CLI is installed. The `gh` tool schema accepts arguments as an array of strings and executes them through `ProcessStartInfo.ArgumentList`, not a shell command string. Missing `gh` is logged and may be notified through the host UI without repeated launch spam.

The statistics plugin is packaged as `CodeAlta.Plugin.Statistics`, is enabled by default, can be disabled with:

```toml
[plugins.statistics]
enabled = false
```

It contributes transient per-turn/session statistics projections and a `statistics` live-tool command root without writing plugin messages into canonical session history.

The MCP plugin is packaged as `CodeAlta.Plugin.Mcp`, is enabled by default, and contributes the `alta mcp` command root plus dynamic developer prompt guidance for configured MCP servers. It reads fixed MCP JSON server files (`~/.alta/mcp.json` and project `.alta/mcp.json`) and TOML policy under `[plugins.mcp]`; connection fields stay in JSON, while enablement, `disabled_tools`, prompt caps, timeouts, and direct-exposure policy stay in TOML. The plugin also provides reusable `McpManagementService`/runtime services consumed by direct MCP agent tools, the TUI MCP Servers dialog, and the status indicator. Direct dynamic MCP `AgentToolDefinition` exposure is enabled: every enabled/config-controlled MCP tool is exposed where policy allows it. Automatic refresh on `tool-list-changed` remains follow-up work unless direct-tool freshness requires it. See [MCP support](mcp.md).

## Troubleshooting

- **Missing SDK:** source plugins require the .NET 10 SDK selected by the generated plugin-root `global.json`. If `dotnet build plugin.cs` is treated as a project build, install the required SDK or start with safe mode.
- **Build failures:** startup shows concise live build progress and writes detailed diagnostics plus stdout/stderr tails under `~/.alta/logs/`.
- **Dependency load failures:** CodeAlta assemblies and shared authoring dependencies resolve from the host load context. Plugin-owned package dependencies must be copied by the SDK so the plugin load context can resolve them from the plugin output folder.
- **Broken plugin:** start with `--no-plugins`, `--plugin-safe-mode`, or `CODEALTA_DISABLE_PLUGINS=1`, then disable or edit the plugin package.
- **Unload delays:** ensure the plugin cancels tracked work and does not keep static references or unmanaged resources alive.
