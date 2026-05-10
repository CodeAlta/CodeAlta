# Plugin Abstractions

CodeAlta exposes a public plugin authoring package in `src/CodeAlta.Plugins.Abstractions` and foundational runtime services in `src/CodeAlta.Plugins`. The runtime currently provides source package discovery, generated plugin-root build files, explicit enablement resolution, structured `dotnet build plugin.cs` orchestration, collectible assembly-load-context loading, descriptor discovery, contribution ownership, built-in plugin metadata, lifecycle helpers, file-system change monitoring, runtime diagnostics, startup planning, concrete contribution adapters, management view-model data/dialog routing, and source-change notifications.

A simple plugin usually references only `CodeAlta.Plugins.Abstractions`, inherits from `PluginBase`, and overrides the contribution methods it needs:

```csharp
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI.Controls;
using CliCommand = XenoAtom.CommandLine.Command;

[Plugin(DisplayName = "Hello Plugin", Description = "Adds a command, prompt guidance, and status row.")]
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
        yield return PluginUi.Visual(PluginUiRegion.ThreadFooter, static _ => new Markup("[dim]Hello plugin[/]"));
    }
}
```

## v1 authoring model

- A plugin is a concrete `PluginBase` subclass with a public parameterless constructor.
- `PluginAttribute` metadata is optional; a runtime can derive descriptor data from the assembly and type.
- `readme.md` package documentation is optional, not required for discovery or activation.
- A single assembly can contain multiple plugin classes.
- `PluginDiscovery` exposes helper predicates for the runtime rule: visible, concrete, non-generic `PluginBase` subclasses with public parameterless constructors.
- `PluginScope` is assigned by the runtime from the load location, not by plugin code: `~/.alta/plugins` produces global plugins, while `{project}/.alta/plugins` produces project-scoped plugins.
- Project-scoped plugins expose `ScopeProjectId` / `ScopeProjectPath` through `PluginRuntimeContext` and operation contexts so the runtime can restrict prompt injections, commands, resources, and other contributions to the matching project.
- Contributions are declarative objects returned from virtual methods; the runtime owns registration and removal.
- Simple contributions do not require author-supplied IDs. The runtime creates contribution handles from plugin identity, contribution point, natural names, and ordinals.
- Plugins receive a direct `XenoAtom.Logging.Logger` through `PluginRuntimeContext` and `PluginBase.Logger`.

## Source plugin runtime layout

Dynamic source plugins are discovered under these roots:

- `~/.alta/plugins/<package-id>/plugin.cs` for global plugins;
- `<project>/.alta/plugins/<package-id>/plugin.cs` for project-scoped plugins.

For each plugin root, CodeAlta owns generated root-level build files: `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, and `global.json`. The generated `global.json` selects the .NET 10 SDK required for native file-based builds; generated props force file-based plugins to build as `net10.0` libraries with `EnableDynamicLoading=true`; generated targets reference host CodeAlta assemblies from the running executable folder with `<Private>false</Private>`, shared authoring packages with explicit versions and `<ExcludeAssets>runtime;native</ExcludeAssets>`, and a deterministic `CodeAltaPluginTargetPath` message for output discovery; generated package props disable central package management so `#:package Package@Version` directives work normally. CodeAlta invokes `dotnet build plugin.cs` directly from the plugin package directory; the plugin-root `global.json` is still found by normal SDK ancestor lookup. CodeAlta lets the .NET SDK choose its own file-based build output/cache location; the resolved output assembly is recorded in CodeAlta's plugin build manifest for fast subsequent loads. Source plugin packages should not contain their own `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, or `global.json`; the runtime diagnoses those files as unsupported for v1 source-folder plugins.

Dynamic source plugins are trusted code. Building a plugin can execute SDK/NuGet/MSBuild logic, and loading it executes .NET code in the CodeAlta process. Source plugins are enabled by default when discovered; disable a plugin through TOML configuration when you do not want it built or loaded:

```toml
[plugins.HelloWorld]
enabled = false
```

Use `--no-plugins`, `--plugin-safe-mode`, or `CODEALTA_DISABLE_PLUGINS=1` to bypass plugin discovery/build/load when a plugin is broken. These switches are host-owned and are recognized before plugin-contributed command-line options.

Plugin diagnostics are stored and displayed separately from normal conversation history. Runtime status includes config/discovery/build/load/activation/contribution/callback/source-change/unload diagnostics, structured build summaries, contribution summaries, and unknown config entries. Use `/plugins` or `/plugin` (or the command palette entry) to open plugin management for the current scope; the dialog shows a plugin list on the left and selected-plugin enablement, diagnostics, properties, contributions, and source/README open actions on the right. Use `--plugins-status` for a headless discovery/config summary suitable for CI troubleshooting.

## Contribution areas

The abstraction package includes contracts for:

- early startup hooks and command-line contributions using `XenoAtom.CommandLine` nodes;
- shell, prompt, and thread commands with shortcut/presentation metadata;
- UI visuals, status rows, dialogs, and renderer hooks using XenoAtom `Visual` types;
- prompt processors, system/developer prompt parts, and before-agent-run hooks;
- LLM-callable agent tools and tool call/result interception;
- backend/provider factories returning `IAgentBackend`;
- plugin-lifetime background tasks through `IPluginTaskService` so the runtime can block unload until tracked work completes;
- resource roots such as skills, system prompts, templates, themes, MCP manifests, and agent definitions;
- compaction hooks for before/instruction/reducer/after participation;
- normalized agent event observation;
- transient thread event projections through `GetThreadEventProjections()`, used for plugin-owned timeline cards (with optional collapsed detail sections) that are replayed from canonical history but are not persisted into conversation history. Projections provide Markdown for fallback/copy behavior and can optionally provide XenoAtom `Visual` factories that replace the default Markdown card content or customize detail content/header rendering. Projections can also provide `PluginDynamicDerivedThreadEventContent` so expensive cards render immediately with placeholder Markdown/visuals and refresh in place when background computation completes;
- diagnostics, lifecycle states, context invalidation, and no-op/headless service implementations.

Low-ceremony factories are available for common authoring tasks: `Command`, `Startup`, `Prompt`, `Attachments`, `PluginUi`, `Resources`, `Tool`, and `PluginBackend`.
`PluginUi` also creates dialog requests for notifications, confirmations, input text, text editor dialogs, selections, and custom visuals.
Command-line contributions intentionally use plain `XenoAtom.CommandLine` objects such as `Command` and `CommandGroup`, with options, arguments, validation, completion, and callbacks added through the command-line API directly instead of a CodeAlta-specific wrapper.

## Alta live tool integration

Plugins can extend the in-session `alta` live tool by overriding `PluginBase.GetAltaCommands()` and returning `PluginAltaCommandContribution` records. Each contribution declares a command path, policy classification, and a factory that must create a fresh unattached `XenoAtom.CommandLine.CommandNode` for each registry build. The host merges those commands into `alta --help`, rejects collisions with core command roots or earlier plugin roots, and can summarize policy flags through `alta tool capability list`.

Plugin command policies should describe whether the command mutates CodeAlta/plugin state, is disruptive, requires the in-process runtime, or can run with catalog-only services. V1 plugins are trusted code, but mutating `alta` operations still carry plugin provenance for audit and timeline reconstruction.

Plugins can also call the built-in `alta` dispatcher through `Services.Alta.InvokeAsync(...)` without referencing `CodeAlta.LiveTool`. The service accepts argument tokens excluding the executable name, optional stdin, and `PluginAltaInvocationOptions` for source thread/session/project metadata. It returns `PluginAltaCommandResult` with the stable exit code, the same flat JSONL transcript used by agent live-tool calls, truncation status, and a short error summary. For example, a plugin can invoke `Services.Alta.InvokeAsync(["session", "create", "--project", projectId])` and the created session will record the runtime-owned plugin key in `createdBy` provenance.

Capabilities removed from the hardcoded 1.0 core—such as built-in MCP services, semantic or language-specific indexing, local model hosting, and multi-agent orchestration roles—should be reintroduced through focused plugins or reusable service packages rather than frontend-owned app code.

Built-in plugins follow the same abstraction model. The statistics plugin is packaged as `CodeAlta.Plugin.Statistics` and keeps its implementation in a single `StatisticsPlugin.cs` file so it stays close to what an external `plugin.cs` source plugin could author. It is enabled by default, can be disabled through `[plugins.statistics] enabled = false`, and projects transient per-turn/session statistics from normalized agent events without writing plugin messages to canonical thread history. Its per-turn statistics cards use dynamic projection content so thread loading can show a computing placeholder while the detailed statistics are calculated off the UI path and then updated in place; the final card supplies its own `Collapsible` visual with a `Markup` summary header and `WrapHStack` detail content while preserving Markdown for copy/fallback.

Plugins should schedule long-running background work through `Services.Tasks.Run(...)` or the `PluginBase.Tasks` shortcut instead of calling `Task.Run` directly. The runtime tracks these handles, cancels them during deactivation, and can delay unload while `PluginTaskHandle.Completion` is still running.

## Headless orchestration behavior

Runtime-facing plugin contributions are not tied to the terminal frontend. A headless host can invoke prompt processors, `OnBeforeAgentRunAsync`, agent tools, agent-event observers, compaction hooks, resource roots, and provider factories through the same contribution registry and adapter services used by the TUI. UI-only contributions such as visuals, dialogs, renderers, and status items remain frontend responsibilities; headless hosts should ignore them or expose mode-aware no-op services through `IPluginUiService.HasInteractiveUi == false`.

Plugins that need to coordinate work threads should use explicit orchestration services and request DTOs rather than selected-thread UI state. Those requests must include project scope, prompt-session identity, model-provider identity, and either a thread id or a draft id for prompt/steer/queue operations.

## Terminology: model providers and backends

User-facing shell and configuration docs use **Model Provider** for the selectable LLM runtime plus endpoint/model settings shown in the provider picker and management UI. The remaining `Backend` names in plugin APIs, such as `PluginAgentBackendContribution` and `IAgentBackend`, refer to low-level runtime adapter contracts that existing provider implementations and extension code still use. New frontend/shell contracts should prefer `ModelProvider` names unless they are explicitly bridging to those legacy low-level adapter types.

## Backend/provider example

```csharp
public override IEnumerable<PluginAgentBackendContribution> GetAgentBackends()
{
    yield return PluginBackend.FromFactory(
        name: "example-backend",
        displayName: "Example Backend",
        description: "Adds a custom provider protocol.",
        capabilities: PluginAgentBackendCapabilities.Default | PluginAgentBackendCapabilities.Tools,
        factory: static async (context, cancellationToken) =>
        {
            await context.Services.State.WriteJsonAsync(PluginStateScope.User, "last-start.json", DateTimeOffset.UtcNow, cancellationToken);
            return new ExampleAgentBackend(context.Logger);
        });
}
```

The runtime will decide where contributed backends appear in provider selection and how collisions with built-in providers are diagnosed.

## Resource example

```csharp
public override IEnumerable<PluginResourceContribution> GetResources()
{
    yield return Resources.SkillRoot("skills");
    yield return Resources.SystemPromptRoot("prompts");
}
```

Relative resource paths are interpreted relative to the plugin package directory by the runtime.

## Troubleshooting runtime startup

- **Missing or unsupported SDK:** source plugins require a .NET 10 SDK and the plugin-root `global.json` generated by CodeAlta so `dotnet build plugin.cs` is handled as a native file-based C# build. If a build diagnostic says `dotnet build plugin.cs` was treated as a project file (`The project file could not be loaded`), install a supported .NET 10 SDK, restore the plugin root `global.json`, or start CodeAlta with `--plugin-safe-mode` / `--no-plugins`. CodeAlta does not generate a replacement project fallback for source plugins.
- **Build progress/failures:** interactive startup shows source plugin startup progress in a transient `Terminal.Live` region with one status line per plugin, colored state icons, and a built-in spinner while build/activation work is still running. Use `--plugins-wait-for-enter` to pause the live region after plugin startup finishes until Enter is pressed; the live region includes the concise `CodeAlta plugins: ...` build/activation timing summary before it is discarded. Without the wait option, the transient live region is discarded before command output or the fullscreen TUI takes over and the concise summary is printed afterward when source plugin packages are checked. Failures still print a concise failure list with source paths and the log file location. Full per-plugin runtime diagnostics and captured stdout/stderr tails are written to `~/.alta/logs/codealta.log`. CodeAlta does not pass MSBuild logger/node-reuse switches to `dotnet build plugin.cs` because the .NET 10 file-based build path treats those forwarded MSBuild switches as project-build mode in current SDKs; stdout parsing is limited to the deterministic `CodeAltaPluginTargetPath=` message emitted by CodeAlta's generated target for output assembly discovery.
- **Dependency load failures:** CodeAlta assemblies and shared authoring dependencies resolve from the host default load context. Plugin-owned package dependencies must be copied by the SDK (`EnableDynamicLoading=true`) so the plugin `AssemblyLoadContext` can resolve them from the plugin output folder.
- **Unload delays or failures:** tracked plugin tasks are cancelled during deactivation and unload waits are bounded. Unload can still fail if plugin code keeps static references, untracked background work, pinned native resources, or host-static delegates alive.
- **Safe mode:** `--no-plugins`, `--plugin-safe-mode`, and `CODEALTA_DISABLE_PLUGINS=1` are checked before dynamic plugins build or load, so they remain available even when a plugin is broken.
