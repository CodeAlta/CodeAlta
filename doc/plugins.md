# Plugin Abstractions

CodeAlta exposes a public plugin authoring package in `src/CodeAlta.Plugins.Abstractions`. The package defines the v1 contract surface only; plugin discovery, package installation, loading, and runtime registration will be implemented by a future plugin runtime.

A simple plugin usually references only `CodeAlta.Plugins.Abstractions`, inherits from `PluginBase`, and overrides the contribution methods it needs:

```csharp
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI.Controls;

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

## Contribution areas

The abstraction package includes contracts for:

- early startup and command-line options;
- shell, prompt, and thread commands with shortcut/presentation metadata;
- UI visuals, status rows, dialogs, and renderer hooks using XenoAtom `Visual` types;
- prompt processors, system/developer prompt parts, and before-agent-run hooks;
- LLM-callable agent tools and tool call/result interception;
- backend/provider factories returning `IAgentBackend`;
- resource roots such as skills, system prompts, templates, themes, MCP manifests, and agent definitions;
- compaction hooks for before/instruction/reducer/after participation;
- normalized agent event observation;
- diagnostics, lifecycle states, context invalidation, and no-op/headless service implementations.

Low-ceremony factories are available for common authoring tasks: `Command`, `CommandLine`, `Startup`, `Prompt`, `Attachments`, `PluginUi`, `Resources`, `Tool`, and `PluginBackend`.
`PluginUi` also creates dialog requests for notifications, confirmations, input text, text editor dialogs, selections, and custom visuals.

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
