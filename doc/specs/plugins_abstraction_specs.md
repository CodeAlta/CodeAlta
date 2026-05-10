# CodeAlta Plugin Abstractions Specification

Status: **Draft**
Last updated: **2026-05-08**
Audience: implementers of `CodeAlta.Plugins.Abstractions`, future `CodeAlta.Plugins`, CodeAlta UI/orchestration/runtime surfaces, and plugin authors.

Primary inputs:

- current CodeAlta agent, skill, system-prompt, command, compaction, and TUI surfaces

## 1. Goal

Define the first public abstraction surface for CodeAlta plugins.

The desired authoring experience is intentionally lightweight: a plugin should often be a single C# file that feels close to scripting. The author should inherit from `PluginBase`, override only the members they need, and return simple objects or delegates. The runtime handles discovery, activation, registration, deactivation, diagnostics, and unloading.

Illustrative shape:

```csharp
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI.Controls;

[Plugin(DisplayName = "Hello Plugin", Description = "Adds a sample command, UI status, and prompt guidance.")]
public sealed class HelloPlugin : PluginBase
{
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Prompt(
            name: "hello",
            description: "Show a hello notification.",
            handler: static async (context, cancellationToken) =>
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

A plugin author should normally:

1. reference only `CodeAlta.Plugins.Abstractions`;
2. inherit from `CodeAlta.Plugins.Abstractions.PluginBase`;
3. optionally decorate the class with lightweight metadata;
4. override only the members they need;
5. return declarative contributions or callback delegates;
6. let the runtime register and unregister everything automatically.

The abstraction package should make plugin authoring easy without requiring knowledge of CodeAlta internals, explicit registration handles, contribution identifiers, or manual unregister logic.

## 2. Non-goals for this document

This specification intentionally does **not** define:

- how plugins are compiled or source-generated;
- the plugin package/install/update format;
- marketplace behavior;
- assembly probing, private dependency resolution, dependency restore, or build pipelines;
- runtime policy for downloading third-party code;
- sandboxing or a permission model;
- exact UI implementation work required in `src/CodeAlta`;
- the final `CodeAlta.Plugins` runtime implementation.

However, the abstractions must be compatible with those future runtime decisions.

## 3. Key v1 decisions

These decisions should guide the first abstraction cut.

### 3.1 Runtime-assigned plugin scope

A plugin is active once enabled and loaded, but the runtime assigns a `PluginScope` from the load location. Plugins loaded from the user plugin directory, for example `~/.alta/plugins`, are `Global`; plugins loaded from a project-local directory, for example `{project}/.alta/plugins`, are `Project` scoped.

Plugin authors do not choose their own scope. Scope is runtime metadata exposed through `PluginRuntimeContext`, `PluginBase`, and operation contexts so the runtime and plugin can restrict contributions to the matching project.

A global plugin may still contribute things that only appear or run in certain contexts:

- a command that is enabled only when a thread is selected;
- a prompt processor that runs only for project threads;
- a tool that only applies to CodeAlta-managed local/raw sessions;
- a UI visual that only appears in the TUI;
- a resource root that affects catalog loading globally.

This is contribution applicability, not plugin activation scope.

### 3.2 No author-facing contribution IDs by default

Contribution IDs are ceremony and should not be required from plugin authors.

The runtime can create internal handles using:

- plugin runtime key;
- plugin type;
- contribution point;
- natural names when they exist (`hello`, `repo_summary`, tool name, CLI option name);
- stable ordinal within a returned contribution list.

Advanced APIs may expose an optional `Key`/`Name` where useful, but simple plugins should not have to invent opaque identifiers. The author-facing API should prefer natural names and helper factories.

### 3.3 Direct plugin logger

The runtime should create at least one `XenoAtom.Logging.Logger` per plugin and expose it directly to the plugin.

Recommended category shape:

```text
CodeAlta.Plugin.<assembly-name>.<plugin-type-name>
```

No `IPluginLog` facade is required for v1. If CodeAlta later needs a bridge to `Microsoft.Extensions.Logging`, the runtime can add it without removing direct `XenoAtom.Logging.Logger` access.

### 3.4 Direct `Visual` contributions are allowed

A plugin should be able to return a `Visual` directly or return a `Func<Visual>`/factory so the runtime can rebuild it.

Recommended default for most UI regions:

- accept `Visual` for extremely simple cases;
- accept `Func<PluginVisualContext, Visual?>` for rebuildable/dynamic visuals;
- provide simple out-of-the-box helpers for common UI tasks: notifications, confirm dialogs, input text, selection dialogs, text editor dialogs, status rows, command palette entries, footer/header widgets.

The runtime still owns mounting/unmounting. A plugin can create visuals, but it should not directly mutate CodeAlta's existing visual tree.

### 3.5 Built-in overrides are allowed

A plugin can override built-in commands, tools, UI regions, prompt contributions, or other extensibility points where the runtime supports replacement.

CodeAlta itself is a powerful local tool; plugins are trusted code and can do anything the process can do. The abstraction should not block on a permission model. Overrides should still be diagnosed and visible so debugging is practical.

### 3.6 Plugins should load early

Plugins should be discovered and activated early enough to contribute to bootstrap behavior, including:

- command-line options;
- startup argument normalization;
- early resource roots;
- prompt/catalog/system-prompt resources;
- provider/backend setup hooks when those plugin points exist.
- agent/backend/provider registration hooks so a plugin can add a new protocol or harness early.

The future runtime may need a two-phase lifecycle:

1. **Bootstrap phase** with minimal services: logger, package path, raw arguments, environment, config paths, resource contribution collection.
2. **Runtime phase** with full services: UI, orchestration, threads, prompt runtime, agent sessions, state store.

### 3.7 Codex/Copilot are edge-integrated

For CodeAlta-managed local/raw runtimes, plugins can eventually integrate deeply into the harness/loop.

For Codex and Copilot native sessions, CodeAlta does not own the full harness/loop. Plugin support there should be edge-based:

- commands before prompt submission;
- prompt text/attachment transformation before send;
- UI contributions;
- normalized event observation;
- command palette/shortcuts;
- resource/catalog contributions used by CodeAlta UI;
- limited prompt/context additions only where the backend adapter can safely apply them.

CodeAlta should not pretend that every local-runtime plugin point can be applied to native Codex/Copilot sessions.

### 3.8 Plugin diagnostics are not thread history

Plugin diagnostics should be attributed to the plugin and easy to inspect, but plugin runtime failures should not be persisted into normal thread conversation history by default.

Diagnostics should be surfaced through:

- the plugin's logger;
- plugin management UI;
- app diagnostics/log viewer;
- transient user-visible status/errors when a callback affects the current action.

A plugin may intentionally write a user-visible message or custom event, but callback failure diagnostics are runtime diagnostics, not conversation history.

## 4. Extensibility surface goals

The plugin abstraction should cover a broad, useful extension surface:

- event subscriptions around agent/session/tool/model/input/resource lifecycle;
- slash commands, keyboard shortcuts, CLI flags, LLM-callable tools, message renderers, model providers;
- hooks for input transformation, context mutation, provider payload mutation, tool call/result interception, compaction, and tree navigation;
- UI services for dialogs, notifications, widgets, editor integration, and status/footer/header surfaces;
- resource contributions for skills, prompts, and themes;
- clear reload/session replacement behavior and stale-context protection;
- explicit trust warnings because extensions execute with process privileges.

CodeAlta should provide this **capability breadth** through a CodeAlta-native .NET authoring model.

Recommended CodeAlta adaptations:

1. Use a **single .NET base class** (`PluginBase`) as the primary authoring entry point.
2. Prefer **returned contribution descriptors** over imperative `RegisterXxx()`/`UnregisterXxx()` calls.
3. Avoid author-facing contribution identifiers unless the plugin point has a natural external name.
4. Make contribution ownership explicit internally so the runtime can remove all plugin-owned UI, commands, prompt parts, tools, and hooks on deactivation.
5. Keep runtime services behind context interfaces rather than exposing mutable CodeAlta internals.
6. Treat dynamic `AssemblyLoadContext` unload as a design constraint, while recognizing it is not a security sandbox.
7. Use CodeAlta's existing typed agent/event abstractions (`CodeAlta.Agent`) instead of raw backend-specific objects by default.

## 5. Core design principles

### 5.1 Single plugin entry point

A plugin is a concrete public class that derives from `PluginBase`.

The plugin class is the single place where an author discovers override points:

- metadata/self-description;
- lifecycle notifications;
- startup contributions;
- commands and shortcuts;
- prompt processors;
- agent tools and tool hooks;
- compaction hooks;
- system prompt contributions;
- UI visuals and renderers;
- resource roots;
- agent/runtime callbacks.

The plugin author should not need to implement many marker interfaces or call many registration services.

### 5.2 Runtime-owned registration

Plugin methods return contribution objects. The runtime owns:

- registering contributions in CodeAlta systems;
- resolving collisions and precedence;
- wiring callbacks;
- mounting UI visuals;
- refreshing prompt/resources/catalogs;
- disposing runtime handles;
- unregistering everything when the plugin is deactivated or unloaded.

Plugins should not directly mutate existing CodeAlta UI, command catalogs, prompt builders, tool registries, or orchestration state.

### 5.3 Low ceremony first, advanced power later

The v1 API should make the common case almost trivial:

- one file;
- no manifest required for local development;
- optional metadata attributes;
- helper factory methods on `PluginBase` or small static helper classes;
- default labels/descriptions where possible;
- no required contribution IDs;
- no explicit register/unregister calls.

Advanced plugins can still use richer DTOs, view models, custom visuals, custom tools, and async callbacks.

### 5.4 Easy unload

The abstraction surface must avoid patterns that make unloading hard:

- no constructor dependency injection into plugin classes;
- no requirement to subscribe to host static events;
- no requirement to retain host UI objects;
- no untracked background tasks; plugins should schedule plugin-owned background work through `IPluginTaskService`;
- no registration handles that plugin authors must dispose manually;
- runtime contexts should be invalidated after deactivation/reload.

A dynamic `AssemblyLoadContext` can only unload when no strong references remain. The runtime cannot guarantee unload if plugin code stores static references, starts non-cancelled tasks, leaves native handles open, or leaks objects into host static state, so the API should discourage those patterns. Long-running plugin work should be scheduled through the task service so the runtime can cancel it on deactivation and block unload while tracked work is still running. It should not, however, make normal UI/plugin authoring awkward just to avoid every possible leak.

### 5.5 Trusted code, not sandboxed code

A collectible `AssemblyLoadContext` is an unload/versioning boundary, not a security boundary.

Unless CodeAlta later introduces a separate sandbox, plugins execute trusted .NET code with the effective privileges of the CodeAlta process. The v1 abstraction should not attempt to enforce permissions. Documentation and management UI should still make plugin power clear before third-party plugin installation.

## 6. Package model

The package should be named:

```text
CodeAlta.Plugins.Abstractions
```

Primary namespace:

```csharp
namespace CodeAlta.Plugins.Abstractions;
```

Optional sub-namespaces may be added only when the root namespace becomes too large, for example:

- `CodeAlta.Plugins.Abstractions.Commands`
- `CodeAlta.Plugins.Abstractions.Prompting`
- `CodeAlta.Plugins.Abstractions.Compaction`
- `CodeAlta.Plugins.Abstractions.Ui`
- `CodeAlta.Plugins.Abstractions.Resources`

### 6.1 Dependency direction

The `CodeAlta` executable can reference `CodeAlta.Plugins.Abstractions` and `CodeAlta.Plugins`.

`CodeAlta.Plugins.Abstractions` must **not** reference the `CodeAlta` executable project. That direction is impossible and should not be designed around.

Allowed dependency direction:

```text
plugin assembly -> CodeAlta.Plugins.Abstractions <- CodeAlta executable / CodeAlta.Plugins runtime
```

The abstraction package can reference stable public library projects and NuGet packages, but not app internals.

### 6.1.1 Built-in plugin packaging

Built-in plugins should use the same public abstraction shape as external plugins. A built-in plugin is allowed to be registered by the host through a `BuiltInPluginDefinition`, but its implementation should still live in its own plugin assembly instead of inside the app or plugin runtime implementation.

Recommended project shape for first-party built-ins:

```text
src/CodeAlta.Plugin.<Name>/
  CodeAlta.Plugin.<Name>.csproj
  <Name>Plugin.cs
```

The first statistics plugin should therefore be authored as a separate `CodeAlta.Plugin.Statistics` project with the plugin implementation kept in a single C# source file, for example `StatisticsPlugin.cs`, so the code remains close to what an external source plugin author could write in `plugin.cs`.

The built-in plugin project should:

1. reference `CodeAlta.Plugins.Abstractions` as its primary host-facing dependency;
2. avoid referencing the `CodeAlta` executable, frontend, orchestration internals, or `CodeAlta.Plugins` runtime internals;
3. use only `PluginBase` overrides and contribution descriptors to integrate with the host;
4. keep implementation-specific helper types in the same source file when practical, unless a helper belongs in the public abstraction package;
5. be registered by host/runtime bootstrap code as a built-in plugin without bypassing normal enablement, diagnostics, contribution collection, or disable-by-config behavior.

Tests for a built-in plugin may live outside the plugin source file and may use test-only references to host/runtime projects where needed. The runtime registration code may reference the built-in plugin assembly to create the plugin instance, but the plugin implementation should not depend on host internals in the opposite direction.

### 6.2 Dependency policy

A plugin author should be able to reference only `CodeAlta.Plugins.Abstractions` and get common authoring dependencies transitively.

Recommended references from `CodeAlta.Plugins.Abstractions`:

| Dependency | Why plugin authors need it |
| --- | --- |
| `CodeAlta.Agent` | Agent events, tool definitions, tool results, model/session identifiers. |
| `CodeAlta.Catalog` | Project/thread/skill descriptors and provenance models when stable enough for public use. |
| `XenoAtom.CommandLine` | Direct command-line contribution objects (`Command`, `CommandGroup`, options, arguments, validation, completion). |
| `XenoAtom.Logging` | Direct per-plugin `Logger`. |
| `XenoAtom.Terminal.UI` | `Visual`, commands, input, dialogs, styling primitives. |
| `XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp` | File/editor UI contributions. |
| `XenoAtom.Terminal.UI.Extensions.Markdown` | Markdown rendering contributions. |
| `XenoAtom.Terminal.UI.Graphics` / Screenshot as needed | Visual rendering and image-capable plugin surfaces. |
| `Microsoft.Extensions.AI.Abstractions` | Useful for model/provider-adjacent plugins and already aligned with `CodeAlta.Agent`. |

If referencing a library would introduce a cycle, prefer neutral DTOs in `CodeAlta.Plugins.Abstractions` and have `CodeAlta.Plugins` adapt them to runtime internals.

### 6.3 Shared assembly identity

Because plugins are expected to load in their own collectible `AssemblyLoadContext`, core abstraction assemblies must be shared with the host rather than loaded independently per plugin.

The future loader should ensure these assemblies have one host-owned identity:

- `CodeAlta.Plugins.Abstractions`
- `CodeAlta.Agent`
- shared CodeAlta public model assemblies used in method signatures
- shared XenoAtom UI/logging assemblies used in contribution signatures

This document does not define the loader, but the abstraction design should not require type identity tricks to work.

## 7. Discovery and metadata

The future runtime should discover plugin classes by scanning plugin assemblies for types that satisfy all of the following:

1. derives from `CodeAlta.Plugins.Abstractions.PluginBase`;
2. is public;
3. is not abstract;
4. is not generic;
5. has a public parameterless constructor.

A single .NET assembly may contain multiple plugin classes.

The public parameterless constructor should be cheap and side-effect free. Plugins should perform work in lifecycle methods, not constructors.

This contract should be the same whether the assembly was precompiled before CodeAlta starts or produced by a future CodeAlta runtime compiler. A plugin package may carry private assembly/NuGet dependencies that the future loader resolves inside the plugin load context; the abstraction package should only model plugin-to-plugin dependencies and host-facing contributions.

### 7.1 Metadata should be lightweight

Do not over-formalize plugin identifiers in v1.

A runtime can derive an internal plugin key from:

- assembly name;
- plugin type full name;
- optional metadata attribute value;
- package manifest when a package format exists later.

A plugin author should not need to pick a reverse-DNS identifier for early experimentation. If they provide an explicit key, it should be accepted, but it should not dominate the authoring model.

Recommended attribute shape:

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    public PluginAttribute() { }
    public PluginAttribute(string key) { ... }

    public string? Key { get; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? License { get; init; }
    public string? ProjectUrl { get; init; }
    public string? ReadmeAnchor { get; init; }
    public string[] Tags { get; init; } = [];
}
```

Optional dependency metadata can exist, but it should not make simple plugins verbose:

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PluginDependencyAttribute : Attribute
{
    public PluginDependencyAttribute(string pluginKey) { ... }

    public string PluginKey { get; }
    public string? VersionRange { get; init; }
    public bool Optional { get; init; }
}
```

### 7.2 Descriptor model

The runtime should build a descriptor from attributes, assembly metadata, and sidecar files.

Recommended descriptor:

```csharp
public sealed record PluginDescriptor
{
    public required string RuntimeKey { get; init; }
    public required string TypeName { get; init; }
    public required string AssemblyName { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> Authors { get; init; } = [];
    public string? License { get; init; }
    public Uri? ProjectUri { get; init; }
    public string? ReadmePath { get; init; }
    public string? ReadmeAnchor { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
```

`RuntimeKey` is for diagnostics and runtime handles. It need not be something the author hand-crafted.

### 7.3 Sidecar documentation

A minimal plugin assembly directory can be as small as:

```text
<plugin-package>/
  MyPlugin.dll
```

`readme.md` is optional package-level documentation. A plugin package can be valid without one. If present and the assembly contains multiple plugins, the README should document all of them and may use headings/anchors per plugin.

Optional future conventions:

```text
<plugin-package>/
  readme.md
  sample-plugin.readme.md
  license.txt
  icon.png
  skills/
  prompts/
  themes/
  templates/
```

The abstraction model should allow descriptors to point at documentation sections or files when they exist, but documentation discovery belongs to the runtime and should not be mandatory.

Plugin packages may also contain resources such as skills, prompt resources, themes, templates, MCP manifests, or agent/provider configuration files. These resources become useful only when the plugin exposes them through resource or backend/provider contributions.

## 8. `PluginBase` candidate surface

`PluginBase` should be intentionally broad but low-friction: many virtual methods, all defaulting to empty/no-op/null.

The concrete API should expose a runtime-attached context and helper factories so simple overrides can be short.

Recommended shape:

```csharp
public abstract class PluginBase : IAsyncDisposable
{
    protected PluginRuntimeContext Context { get; }
    protected XenoAtom.Logging.Logger Logger { get; }
    protected IPluginServices Services { get; }
    protected IPluginUiService Ui { get; }
    protected IPluginTaskService Tasks { get; }
    protected PluginScope Scope { get; }
    protected string? ScopeProjectId { get; }
    protected string? ScopeProjectPath { get; }

    public virtual PluginDescriptor? Describe()
        => null;

    public virtual ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public virtual ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public virtual ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public virtual IEnumerable<PluginStartupContribution> GetStartupContributions()
        => [];

    public virtual IEnumerable<XenoAtom.CommandLine.CommandNode> GetCommandLineContributions()
        => [];

    public virtual IEnumerable<PluginCommandContribution> GetCommands()
        => [];

    public virtual IEnumerable<PluginAgentToolContribution> GetAgentTools()
        => [];

    public virtual IEnumerable<PluginAgentBackendContribution> GetAgentBackends()
        => [];

    public virtual IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
        => [];

    public virtual IEnumerable<PluginPromptProcessorContribution> GetPromptProcessors()
        => [];

    public virtual IEnumerable<PluginCompactionContribution> GetCompactionContributions()
        => [];

    public virtual IEnumerable<PluginUiContribution> GetUiContributions()
        => [];

    public virtual IEnumerable<PluginResourceContribution> GetResources()
        => [];

    public virtual ValueTask<PluginPromptResult?> OnPromptSubmittingAsync(
        PluginPromptSubmittingContext context,
        CancellationToken cancellationToken = default)
        => new((PluginPromptResult?)null);

    public virtual ValueTask<PluginBeforeAgentRunResult?> OnBeforeAgentRunAsync(
        PluginBeforeAgentRunContext context,
        CancellationToken cancellationToken = default)
        => new((PluginBeforeAgentRunResult?)null);

    public virtual ValueTask<PluginToolCallResult?> OnToolCallAsync(
        PluginToolCallContext context,
        CancellationToken cancellationToken = default)
        => new((PluginToolCallResult?)null);

    public virtual ValueTask<PluginToolResult?> OnToolResultAsync(
        PluginToolResultContext context,
        CancellationToken cancellationToken = default)
        => new((PluginToolResult?)null);

    public virtual ValueTask OnAgentEventAsync(
        PluginAgentEventContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public virtual ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}
```

Notes:

- The exact final method list can be refined, but the authoring pattern should remain: override a virtual method, return contributions, or return an optional callback result.
- Contribution methods are synchronous because they usually return descriptors. If a contribution needs I/O, its delegate should perform I/O asynchronously when invoked.
- The runtime attaches `Context`, `Logger`, and `Services` before `InitializeAsync` and contribution collection.
- `InitializeAsync` is for loading plugin-owned state and validating environment requirements.
- `OnActivatedAsync` is for starting plugin-owned resources that are necessary while active.
- Long-running plugin-owned work should be started with `Tasks.Run(...)` so the runtime can cancel it and delay unload until it completes.
- `OnDeactivatingAsync` is for stopping plugin-owned resources. CodeAlta-owned contribution removal is still the runtime's responsibility.
- Runtime-attached contexts should become invalid after deactivation/reload.

### 8.1 Helper factories

The abstraction package should include helper factories so plugin code does not require DTO ceremony.

Examples:

```csharp
yield return Command.Prompt("hello", "Say hello", async ctx => { ... });
yield return Command.Thread("compact-hard", "Run aggressive compaction", async ctx => { ... });
yield return Prompt.Developer("Always mention the current plugin is active.");
yield return Resources.SkillRoot("skills");
yield return PluginUi.Visual(PluginUiRegion.ThreadFooter, () => new Markup("[bold]Plugin[/]"));
yield return PluginUi.Status("Plugin", ctx => "ready");
```

These helpers can fill defaults for labels, descriptions, presentation, order, and runtime handles.

## 9. Runtime-owned contribution handles

Every contribution should get an internal runtime handle even when the author does not provide an ID.

Internal handle inputs:

- plugin runtime key;
- plugin type;
- plugin point;
- contribution natural name when present;
- contribution ordinal;
- contribution factory/source metadata;
- activation generation.

Illustrative internal shape:

```csharp
public sealed record PluginContributionHandle(
    string PluginRuntimeKey,
    string PluginTypeName,
    PluginPoint Point,
    string RuntimeContributionKey,
    int ActivationGeneration);
```

These handles are runtime-owned. They should not be exposed as something plugin authors must dispose to unregister.

When a plugin is deactivated, the runtime removes every handle associated with that plugin activation before unloading the assembly.

## 10. Core context and services

### 10.1 Runtime context

Recommended runtime context:

```csharp
public sealed class PluginRuntimeContext
{
    public required PluginDescriptor Plugin { get; init; }
    public required PluginHostInfo Host { get; init; }
    public required XenoAtom.Logging.Logger Logger { get; init; }
    public required IPluginServices Services { get; init; }
    public required string PackageDirectory { get; init; }
    public PluginScope Scope { get; init; } = PluginScope.Global;
    public string? ScopeProjectId { get; init; }
    public string? ScopeProjectPath { get; init; }
    public CancellationToken LifetimeCancellationToken { get; init; }
}

public enum PluginScope
{
    Global,
    Project
}
```

### 10.2 Host info

```csharp
public sealed record PluginHostInfo
{
    public required string ApplicationName { get; init; }
    public required string Version { get; init; }
    public required string UserDataDirectory { get; init; }
    public string? CurrentWorkingDirectory { get; init; }
    public bool HasInteractiveUi { get; init; }
    public bool IsHeadless { get; init; }
    public bool IsBootstrapPhase { get; init; }
}
```

### 10.3 Operation contexts

Operation callbacks should provide the current app/project/thread/run details as data, not as activation scope.

Examples:

```csharp
public abstract class PluginOperationContext
{
    public required PluginDescriptor Plugin { get; init; }
    public required IPluginServices Services { get; init; }
    public PluginScope Scope { get; init; } = PluginScope.Global;
    public string? ScopeProjectId { get; init; }
    public string? ScopeProjectPath { get; init; }
    public string? ProjectId { get; init; }
    public string? ProjectPath { get; init; }
    public string? ThreadId { get; init; }
    public string? RunId { get; init; }
}
```

This keeps plugin activation independent from app selection while still allowing plugins to adapt to the selected project/thread and allowing project-scoped plugins to check whether the current operation applies to their runtime-assigned project.

### 10.4 Services

`IPluginServices` should be a stable facade over host capabilities:

```csharp
public interface IPluginServices
{
    XenoAtom.Logging.Logger Logger { get; }
    IPluginUiService Ui { get; }
    IPluginStateStore State { get; }
    IPluginWorkspaceService Workspace { get; }
    IPluginThreadService Threads { get; }
    IPluginPromptService Prompts { get; }
    IPluginAgentService Agents { get; }
    IPluginTaskService Tasks { get; }
}
```

These services should be capability-oriented and narrow. They should not expose the full mutable `CodeAltaApp`, view models, or orchestration internals.

### 10.5 Plugin task service

Plugins should not use raw `Task.Run` for background work that may outlive the callback that started it. The runtime cannot safely unload a plugin assembly while plugin-owned work is still running, and it cannot track arbitrary tasks unless plugins schedule them through a host service.

Recommended shape:

```csharp
public interface IPluginTaskService
{
    bool HasRunningTasks { get; }
    int RunningTaskCount { get; }

    PluginTaskHandle Run(
        string name,
        Func<CancellationToken, ValueTask> work,
        PluginTaskOptions? options = null);

    ValueTask WhenIdleAsync(CancellationToken cancellationToken = default);
}

public sealed record PluginTaskOptions
{
    public string? Description { get; init; }
    public bool LongRunning { get; init; }
}

public sealed class PluginTaskHandle
{
    public string Name { get; }
    public string? Description { get; }
    public bool LongRunning { get; }
    public DateTimeOffset StartedAt { get; }
    public Task Completion { get; }
    public bool IsCompleted { get; }
    public void RequestCancellation();
}
```

Task service behavior:

- scheduled tasks are associated with the plugin activation that created them;
- the supplied cancellation token is cancelled during plugin deactivation or when the handle requests cancellation;
- deactivation should request cancellation and wait for tracked tasks to finish or until the runtime's unload timeout/policy is reached;
- plugin unload should be blocked while `HasRunningTasks` is true;
- task failures should be surfaced through diagnostics and through `PluginTaskHandle.Completion` rather than making unload tracking impossible.

## 11. Contribution categories

### 11.1 Startup and command-line contributions

Plugins should be able to plug into early startup.

Candidate contributions:

```csharp
public virtual IEnumerable<XenoAtom.CommandLine.CommandNode> GetCommandLineContributions()
    => [];
```

Command-line contributions must use plain `XenoAtom.CommandLine` objects directly, such as `Command`, `CommandGroup`, `Option`, arguments, validators, completions, and command callbacks. CodeAlta should not introduce a second command-line option abstraction because `XenoAtom.CommandLine` is already the public authoring model, matching the direct-use approach taken for `XenoAtom.Terminal.UI` visuals.

Startup plugin points:

- add `XenoAtom.CommandLine` nodes before unknown-option validation;
- inspect or normalize raw startup arguments;
- contribute early resource roots;
- contribute early config defaults/overlays when a future config hook exists;
- emit startup diagnostics with plugin attribution.

Bootstrap contexts should be minimal and should not assume UI, catalog, project, thread, or agent services are available.

### 11.2 Commands

Commands are user-invoked actions surfaced through prompt slash commands, thread commands, command palette entries, command bar buttons, and keyboard shortcuts.

Recommended classes:

```csharp
public sealed record PluginCommandContribution
{
    public required string Name { get; init; }
    public string? Label { get; init; }
    public string? Description { get; init; }
    public PluginCommandKind Kind { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public PluginKeyBinding? KeyBinding { get; init; }
    public PluginCommandPresentation Presentation { get; init; } = PluginCommandPresentation.Default;
    public PluginCommandAvailability Availability { get; init; } = PluginCommandAvailability.Always;
    public required PluginCommandHandler Handler { get; init; }
}

public enum PluginCommandKind
{
    Shell,
    Prompt,
    Thread
}
```

Command behavior:

- `Shell` commands are app/global commands.
- `Prompt` commands are textual prompt commands such as `/hello`.
- `Thread` commands require a selected thread unless the handler chooses to create one.
- The command handler receives a `PluginCommandContext` with selected project/thread/prompt data and UI services.
- Commands may show dialogs, enqueue prompts, send prompts, edit drafts, activate skills, or open plugin-owned views through host services.
- Built-in command names and keybindings may be overridden by plugins.
- Overrides and conflicts should be diagnosed and visible.

### 11.3 Agent tools

Plugins should be able to contribute LLM-callable tools to CodeAlta-managed agent sessions.

CodeAlta already has `AgentToolDefinition`, `AgentToolSpec`, `AgentToolInvocation`, and `AgentToolResult` in `CodeAlta.Agent`. The plugin abstraction should wrap or extend those without inventing an unrelated tool model.

Recommended wrapper:

```csharp
public sealed record PluginAgentToolContribution
{
    public required AgentToolDefinition Definition { get; init; }
    public string? PromptSnippet { get; init; }
    public string? PromptGuidance { get; init; }
    public PluginToolActivationPolicy ActivationPolicy { get; init; } = PluginToolActivationPolicy.Default;
    public PluginToolRenderer? Renderer { get; init; }
}
```

Runtime behavior:

- The runtime registers tools for sessions where the plugin is active and the backend supports CodeAlta-managed tools.
- Deactivation removes tools from future requests and active tool registries.
- Tool names are natural identifiers because provider protocols require tool names.
- Duplicate tool names and built-in overrides are allowed but diagnosed.
- Tool handlers receive cancellation and progress callbacks and should not assume UI availability.
- If a tool mutates files, the runtime should eventually expose shared file-mutation coordination so plugin tools do not race built-in edit/write tools.

### 11.4 Prompt input processors

Prompt processors can inspect, transform, block, or fully handle user input before it becomes an agent turn.

Recommended model:

```csharp
public sealed record PluginPromptProcessorContribution
{
    public int Order { get; init; } = 0;
    public required PluginPromptProcessorHandler Handler { get; init; }
}

public sealed record PluginPromptResult
{
    public PluginPromptDisposition Disposition { get; init; }
    public string? ReplacementText { get; init; }
    public IReadOnlyList<PluginPromptAttachment> ReplacementAttachments { get; init; } = [];
    public string? UserMessage { get; init; }
}

public enum PluginPromptDisposition
{
    Continue,
    Replace,
    Handled,
    Cancel
}
```

Use cases:

- implement custom slash-like syntaxes;
- convert prompt references into attachments;
- add structured metadata to a turn;
- block prompts with a user-visible explanation;
- handle a prompt entirely in UI without contacting a model;
- add edge behavior for Codex/Copilot before CodeAlta sends the prompt.

Transforming processors should chain in deterministic order. If a processor returns `Handled` or `Cancel`, later processors and normal send should stop.

### 11.5 System prompt contributions

Plugins should be able to contribute prompt parts without owning the whole prompt builder.

Recommended model:

```csharp
public sealed record PluginSystemPromptContribution
{
    public string? Title { get; init; }
    public required PluginPromptChannel Channel { get; init; }
    public PluginPromptPartKind Kind { get; init; } = PluginPromptPartKind.Guidance;
    public int Order { get; init; } = 0;
    public required PluginSystemPromptContentProvider Content { get; init; }
}

public enum PluginPromptChannel
{
    System,
    Developer
}

public enum PluginPromptPartKind
{
    Policy,
    Guidance,
    ToolGuidance,
    RuntimeContext,
    ProjectContext,
    SkillAdvertisement,
    Other
}
```

Rules:

- Contributions should become named manifest parts in system prompt diagnostics when CodeAlta owns prompt construction.
- The runtime should tag prompt parts with plugin runtime key, order, token/char estimate, and source path when available.
- Plugins can append or replace prompt sections where the runtime supports it.
- For local/raw runtimes, CodeAlta can apply the full prompt contribution model.
- For Codex/Copilot, these contributions are limited to edge adaptation unless their adapters expose a safe prompt-injection path.

### 11.6 Before-agent-run hooks

Prompt parts are static/declarative. Some plugins need per-turn dynamic behavior.

`OnBeforeAgentRunAsync` should support returning:

- additional context messages;
- temporary system/developer guidance for one turn;
- model/tool activation hints;
- cancellation with a user-visible reason.

Recommended result:

```csharp
public sealed record PluginBeforeAgentRunResult
{
    public bool Cancel { get; init; }
    public string? CancelReason { get; init; }
    public IReadOnlyList<PluginPromptMessage> AdditionalMessages { get; init; } = [];
    public IReadOnlyList<PluginSystemPromptContribution> TemporaryPromptContributions { get; init; } = [];
}
```

The runtime should record these effects in prompt diagnostics and runtime diagnostics, not normal thread conversation history unless the plugin intentionally emits a message.

### 11.7 Agent event observation

Plugins should be able to observe normalized agent events from `CodeAlta.Agent`.

Recommended method:

```csharp
public virtual ValueTask OnAgentEventAsync(
    PluginAgentEventContext context,
    CancellationToken cancellationToken = default)
```

`PluginAgentEventContext` should contain:

- project/thread/run identity when known;
- the `AgentEvent` instance;
- read-only thread/session metadata;
- plugin services.

Observation callbacks should not mutate event history directly. If a future plugin point allows event transformation, it should be a separate explicit contribution with strict ordering and diagnostics.

Implemented runtime slices expose this as a headless orchestration service rather than a frontend-owned bridge:

- `PluginOrchestrationBridge` adapts active `PluginContributions` to orchestration without referencing `src/CodeAlta` UI types.
- `WorkThreadPluginEventObserver` dispatches normalized `AgentEvent` values to plugins with project/thread/run scope and preserves deterministic plugin order.
- Observer failures are attributed through `PluginDiagnosticSink` and do not stop subsequent observers.

Plugins that want to affect UI from observed events should emit separate derived projections instead of mutating canonical event history.

### 11.7.1 Derived thread event projection

Plugins can project transient, plugin-owned timeline/status details from normalized agent events without appending to persisted user/agent transcript history.

The implemented abstraction is `PluginThreadEventProjectionContribution`, returned from `PluginBase.GetThreadEventProjections()`. Runtime behavior:

- projections carry the plugin id, event id, project/thread/run scope, compact Markdown for fallback/copy, timestamp, optional collapsed Markdown detail sections, optional card/detail/header visual factories, optional severity, optional details payload, and optional dynamic Markdown/visual content for cards that refresh after asynchronous background computation;
- `WorkThreadPluginDerivedEventProjector` maps projections to `PluginDerivedThreadEvent` orchestration events;
- replayed agent events and live agent events use the same projector path so renderers see parity between restored and newly observed derived events;
- projection failures are diagnosed and isolated per plugin;
- derived events are transient UI/runtime projections, not canonical transcript entries.

### 11.8 Tool call/result interception

Plugins may need to enforce policy, add audit, or normalize results around tool calls.

Recommended hooks:

```csharp
public virtual ValueTask<PluginToolCallResult?> OnToolCallAsync(
    PluginToolCallContext context,
    CancellationToken cancellationToken = default)

public virtual ValueTask<PluginToolResult?> OnToolResultAsync(
    PluginToolResultContext context,
    CancellationToken cancellationToken = default)
```

`OnToolCallAsync` may:

- allow unchanged;
- replace arguments before execution;
- block execution with a reason;
- show UI or ask the user a question through `IPluginUiService`.

`OnToolResultAsync` may:

- observe result;
- redact/transform content before it returns to the model;
- attach structured details for UI rendering;
- mark a result as failed.

Tool interception is safety-sensitive. Recommended runtime policy:

- tool-call hook errors should fail closed for the affected tool call;
- tool-result hook errors should be reported and leave the original result unchanged unless the plugin explicitly owns the tool;
- argument replacements should be revalidated against the tool schema before execution when practical.

This is primarily a local/raw runtime feature. Codex/Copilot native tool calls can only be observed or influenced where their adapters expose edge hooks.

### 11.9 Compaction

Plugins should be able to participate in compaction without owning the default compaction pipeline.

Recommended contribution:

```csharp
public sealed record PluginCompactionContribution
{
    public int Order { get; init; } = 0;
    public PluginCompactionCapabilities Capabilities { get; init; }
    public PluginBeforeCompactionHandler? BeforeCompaction { get; init; }
    public PluginCompactionInstructionProvider? Instructions { get; init; }
    public PluginCompactionReducer? Reducer { get; init; }
    public PluginAfterCompactionHandler? AfterCompaction { get; init; }
}
```

Initial compaction plugin points:

1. **Before compaction**: inspect plan and optionally cancel with a user-visible reason.
2. **Instruction contribution**: add concise summarization guidance for plugin-owned state/resources.
3. **Reducer**: provide compact representations for plugin-owned event/details payloads.
4. **After compaction**: observe result and update plugin state.

Advanced/deferred plugin points:

- full replacement compaction provider;
- custom retention policy;
- custom token estimator;
- custom checkpoint serializer.

Rules:

- The default runtime remains responsible for final prompt fit validation.
- Plugin reducers should only reduce plugin-owned state or clearly declared payload schemas.
- Cancellation should be explicit and diagnosed.
- Replacement compaction is powerful but should be allowed when the runtime supports it.
- Codex/Copilot native compaction can generally only be observed.

### 11.10 UI contributions

Plugins should interact with UI through two patterns:

1. return declarative UI contributions that the runtime mounts/unmounts;
2. use `IPluginUiService` for transient dialogs and notifications.

Recommended contribution types:

```csharp
public abstract record PluginUiContribution
{
    public required PluginUiRegion Region { get; init; }
    public int Order { get; init; } = 0;
}

public sealed record PluginVisualContribution : PluginUiContribution
{
    public Visual? Visual { get; init; }
    public Func<PluginVisualContext, Visual?>? CreateVisual { get; init; }
}

public sealed record PluginStatusContribution : PluginUiContribution
{
    public required Func<PluginStatusContext, PluginStatusItem?> GetStatus { get; init; }
}
```

Candidate UI regions:

- command palette;
- command bar;
- prompt toolbar;
- prompt autocomplete provider;
- prompt attachment renderer;
- thread footer/status;
- thread header;
- sidebar section;
- timeline message renderer;
- tool-call renderer;
- settings/management page;
- modal dialog factory;
- editor tab provider.

Rules:

- Plugins should not add/remove children from existing CodeAlta visuals directly.
- If a plugin returns a `Visual`, the runtime owns where it is mounted and when it is removed.
- A `Func<Visual>`/factory is preferred for dynamic visuals that may need to rebuild.
- The plugin may own the internal state of its visual, but it should not retain host parent controls after unmount.
- UI contributions must tolerate headless/no-UI mode. Runtime should either skip them or expose no-op services.

Plugin-owned tabs are implemented through `PluginShellTabService`, a frontend adapter over `IShellTabService`:

- plugin tabs use stable ids derived from plugin id and surface key;
- plugin-owned view models are stored in `TabPage.Data`/`ShellTabDescriptor.ViewModel`, while `TabPage.Content` remains a stable visual root owned by the shell projection;
- opening an already-open plugin surface returns the existing tab instead of replacing content or view-model ownership;
- close callbacks receive a `ShellTabCloseReason` so plugins can distinguish user close, plugin unload, and shell shutdown cleanup;
- headless mode is a no-op and ignores UI-only tab contributions.

### 11.11 UI services

`IPluginUiService` should offer safe, mode-aware operations that are easy to call:

```csharp
public interface IPluginUiService
{
    bool HasInteractiveUi { get; }

    ValueTask NotifyAsync(string message, CancellationToken cancellationToken = default);
    ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
    ValueTask<string?> InputAsync(string title, string? initialText = null, CancellationToken cancellationToken = default);
    ValueTask<string?> EditTextAsync(string title, string text, CancellationToken cancellationToken = default);
    ValueTask<T?> SelectAsync<T>(string title, IReadOnlyList<PluginSelectItem<T>> items, CancellationToken cancellationToken = default);
    ValueTask ShowDialogAsync(PluginDialogRequest request, CancellationToken cancellationToken = default);
}
```

The runtime can adapt these services to the current CodeAlta TUI. Future RPC/headless modes can support a subset or return no-op/unsupported results.

### 11.12 Resources

Plugins should be able to contribute resource roots or materialized resources that the runtime can add/remove automatically.

Recommended contribution:

```csharp
public sealed record PluginResourceContribution
{
    public required PluginResourceKind Kind { get; init; }
    public required string Path { get; init; }
    public int Precedence { get; init; } = 0;
}

public enum PluginResourceKind
{
    SkillRoot,
    SystemPromptRoot,
    TemplateRoot,
    ThemeRoot,
    McpServerManifest,
    AgentDefinitionRoot,
    Other
}
```

Initial recommended resources:

- `SkillRoot`: aligns with the skills spec's future plugin-contributed roots.
- `SystemPromptRoot`: contributes base/role/template prompt resources.
- `TemplateRoot`: contributes scaffold/enrichment templates.

Runtime behavior:

- resource roots should be tagged with plugin provenance;
- plugin resources can be read directly from plugin package/build output directories;
- materialization to cache directories can be a runtime option, not a requirement of the abstraction;
- catalogs should refresh when a plugin activates/deactivates;
- plugin-contributed resources should be inspectable in management UI;
- unloading a plugin should remove its contributed roots from effective catalogs.

Because plugins should load early, resource contributions should be available before normal catalog/system-prompt/skill loading when possible.

### 11.13 Message and timeline rendering

Plugins may need custom renderers for plugin-owned events, tool details, or custom messages.

Recommended contributions:

- `PluginTimelineRendererContribution`
- `PluginToolRenderer`
- `PluginMessageRendererContribution`

Rules:

- Renderers should target declared schemas, tool names, event kinds, or plugin-owned custom event types.
- The default renderer should be used if a plugin renderer fails or returns `null`.
- Renderers should be pure with respect to timeline state; long work should be async and cancellable if supported.

### 11.14 Agent/provider/backend contributions

It should be easy for a plugin to add support for a new agent, provider, or protocol. For example, a plugin might add a backend that speaks a new local process protocol, a remote HTTP protocol, an ACP-like protocol, or a provider-specific harness not built into CodeAlta.

The v1 abstraction should reserve a simple backend/provider contribution seam even if advanced provider management UI matures later.

Candidate minimal shape:

```csharp
public sealed record PluginAgentBackendContribution
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public PluginAgentBackendCapabilities Capabilities { get; init; }
    public required PluginAgentBackendFactory Factory { get; init; }
    public PluginProviderConfigurationContribution? Configuration { get; init; }
}

public delegate ValueTask<IAgentBackend> PluginAgentBackendFactory(
    PluginAgentBackendFactoryContext context,
    CancellationToken cancellationToken);
```

Rules:

- The contribution should be as easy as returning a backend factory.
- The plugin should not need to modify CodeAlta startup or provider registries directly.
- The runtime owns registration, selection UI integration, lifecycle, diagnostics, and deactivation.
- A plugin backend can use existing `CodeAlta.Agent` contracts such as `IAgentBackend`, `IAgentSession`, normalized `AgentEvent`, `AgentToolDefinition`, and model/session metadata.
- Optional configuration contributions can later provide provider settings UI, credential prompts, model discovery, OAuth/login, or TOML/default configuration integration.
- Built-in backends/providers can be overridden by plugins when names collide; the runtime should diagnose the effective winner.

Related advanced contributions that can grow from this seam:

- model catalog source;
- provider settings page;
- API key/credential UI page;
- OAuth/login provider;
- ACP backend manifest/provider;
- MCP server provider;
- provider payload hooks for CodeAlta-owned local/raw requests.

## 12. Potential v1 capability matrix

This table is meant to show the desired v1 plugin points and where support is expected to apply.

| Capability / plugin point | CodeAlta v1 abstraction target | Local/raw CodeAlta runtime | Codex/Copilot native sessions |
| --- | --- | --- | --- |
| Plugin discovery | `PluginBase` concrete classes, public parameterless constructor, optional metadata. | Full. | Full for CodeAlta-side features. |
| README/self-description | Optional sidecar `readme.md`; descriptor from attributes/type/assembly even when no README exists. | Full. | Full. |
| Early bootstrap | Startup and command-line contributions before normal option/resource loading. | Full if runtime loads plugins early. | Full for CodeAlta startup edge. |
| Command-line contributions | Plain `XenoAtom.CommandLine` nodes and startup argument handlers. | Full. | Full for host options. |
| Slash/prompt commands | `PluginCommandContribution` with natural names/aliases. | Full. | Full before prompt send. |
| Command palette / command bar | Same command contribution with presentation metadata. | Full TUI. | Full TUI. |
| Keyboard shortcuts | Command keybinding metadata. | Full TUI. | Full TUI. |
| UI dialogs / notifications | `IPluginUiService` helpers. | Full TUI. | Full TUI because UI is CodeAlta-owned. |
| Direct UI visuals | `Visual` or `Func<Visual>` contributions to known regions. | Full TUI. | Full TUI because UI is CodeAlta-owned. |
| Prompt autocomplete/editor integration | UI contribution regions and prompt services. | Full TUI. | Full TUI before send. |
| Prompt input transformation | Prompt processors and `OnPromptSubmittingAsync`. | Full. | Edge-only before send. |
| Prompt attachment/reference handling | Prompt processors and attachment DTOs. | Full. | Edge-only before send if adapter supports attachments. |
| System prompt parts | `PluginSystemPromptContribution`. | Full when CodeAlta owns prompt construction. | Limited/no-op unless adapter exposes safe injection. |
| Per-turn context additions | `OnBeforeAgentRunAsync`. | Full. | Limited edge-only prompt decoration. |
| Agent event observation | `OnAgentEventAsync` over normalized `AgentEvent`. | Full. | Full for events CodeAlta receives/maps. |
| LLM-callable tools | `PluginAgentToolContribution` wrapping `AgentToolDefinition`. | Full. | Generally not supported for native sessions unless backend adapter supports tool injection. |
| Tool call interception | `OnToolCallAsync`. | Full. | Limited/no native support. |
| Tool result interception | `OnToolResultAsync`. | Full. | Limited/no native support. |
| Tool/timeline rendering | renderer contributions for tool names/events/custom schemas. | Full TUI. | Full for CodeAlta timeline events. |
| Compaction hooks | before/instruction/reducer/after contributions. | Full local compaction. | Mostly observe native compaction events only. |
| Skill roots | `PluginResourceContribution.SkillRoot`. | Full catalog integration. | Full CodeAlta catalog/UI; native backend skill behavior remains separate. |
| System prompt/resource roots | `SystemPromptRoot`, `TemplateRoot`, `ThemeRoot`. | Full when CodeAlta owns resources. | UI/catalog only unless native adapter can use. |
| State store | `IPluginStateStore`, namespaced by plugin. | Full. | Full. |
| Background task service | `IPluginTaskService` for plugin-lifetime tracked work. | Full. | Full for CodeAlta-side plugin tasks. |
| Logging/diagnostics | Direct `XenoAtom.Logging.Logger` per plugin plus runtime attribution. | Full. | Full. |
| Agent/backend/provider registration | `PluginAgentBackendContribution` factory for a new `IAgentBackend` or protocol adapter. | Full v1 abstraction target; runtime integration can grow incrementally. | Full for CodeAlta-side backend selection; native sessions remain existing built-ins. |
| Provider settings/model catalog | Optional provider configuration/model catalog contributions. | Future/iterative. | Future/iterative. |
| Provider payload mutation | Deferred/advanced hook. | Future local/raw. | Unlikely for native sessions. |
| Session switch/fork/tree hooks | Deferred lifecycle hooks. | Future. | Limited native support. |
| Plugin install/build/load pipeline | Not in abstractions; future `CodeAlta.Plugins`. | Future. | Future. |

## 13. Activation lifecycle

Recommended lifecycle states:

| State | Meaning |
| --- | --- |
| Discovered | Assembly and plugin type were found. Metadata can be displayed. |
| Installed | Plugin package is known to CodeAlta. It may or may not be enabled. |
| Loaded | Plugin assembly is loaded in an ALC and the `PluginBase` instance exists. |
| Initialized | Runtime context and logger are attached; `InitializeAsync` completed. |
| Active | Plugin is globally active; contributions are materialized and callbacks are dispatchable. |
| Deactivating | Runtime is removing contributions and invoking cleanup callbacks. |
| Deactivated | Contributions removed; no new callbacks should be invoked. |
| Unloaded | Runtime released references and the ALC is eligible for unload. |
| Failed | Plugin failed load/init/activation or exceeded error policy. |

Activation should be cancellable. If activation fails, the runtime should remove any partially materialized contributions.

Deactivation order should be:

1. stop dispatching new callbacks for the plugin;
2. cancel plugin lifetime token;
3. remove runtime-owned contributions;
4. invoke `OnDeactivatingAsync` with a bounded timeout;
5. call `DisposeAsync`;
6. release plugin references and request ALC unload;
7. report diagnostics if unload cannot be confirmed.

## 14. Applicability model

Plugins are global. Contributions decide whether they apply.

Contribution applicability should support simple built-in predicates:

- always available;
- only when interactive UI exists;
- only when a project is selected;
- only when a thread is selected;
- only when a thread is idle/busy;
- only for local/raw CodeAlta-managed sessions;
- only for specific backend families;
- custom predicate delegate for advanced cases.

This avoids the complexity of per-thread plugin activation while keeping the UI/runtime clean.

## 15. Ordering, conflicts, overrides, and diagnostics

Every plugin contribution should have deterministic ordering.

Recommended ordering inputs:

1. plugin activation order;
2. explicit plugin dependency order when available;
3. contribution `Order` within a plugin;
4. plugin runtime key / contribution natural name / ordinal tie-breaker.

Conflict rules should be plugin-point-specific:

| Plugin point | Recommended default conflict policy |
| --- | --- |
| Plugin runtime keys | Runtime-derived; duplicates are resolved by package/type identity and diagnosed. |
| Command names/aliases | Built-ins and other plugins may be overridden; diagnose effective winner and shadowed commands. |
| Key bindings | Built-ins and other plugins may be overridden; diagnose effective winner. |
| Agent tool names | Built-ins and other plugins may be overridden; diagnose effective winner. |
| System prompt parts | Multiple allowed; ordered and visible in prompt manifest. |
| UI regions | Multiple allowed when region supports composition; exclusive regions use precedence and diagnostics. |
| Resource names | Use existing resource precedence and shadowing diagnostics. |
| Compaction replacement providers | Exclusive when implemented; precedence and diagnostics decide. |

Diagnostics should be first-class data surfaced in plugin management UI, logs, and app diagnostics.

## 16. Error handling and cancellation

Plugin failures should not bring down CodeAlta in normal cases, but failures must be clearly attributed to the plugin.

Every runtime-caught plugin error should include:

- plugin runtime key;
- plugin display name/type;
- callback or contribution point;
- natural contribution name when available;
- exception type/message/stack trace in logs;
- operation context when useful (project/thread/backend/run).

Recommended policy:

- metadata/discovery errors: plugin is not activatable; show diagnostic;
- initialization failure: plugin enters failed state; no contributions materialized;
- contribution creation failure: plugin activation fails or partially activates only if safe;
- command handler failure: show user-visible error and log;
- UI renderer failure: fall back to default rendering and log;
- observation callback failure: log and continue with other plugins;
- prompt transformation failure: log and continue or cancel based on plugin-point safety policy;
- tool-call interception failure: fail closed for that tool call;
- compaction hook failure: continue without that plugin unless the plugin owns the replacement compactor.

All async plugin callbacks should accept cancellation. Runtime should pass meaningful cancellation tokens:

- plugin lifetime cancellation for deactivation/reload;
- operation cancellation for prompt/send/tool/compaction operations;
- UI cancellation when dialogs close or the app exits.

Plugin failure diagnostics should not be persisted in normal thread conversation history by default.

## 17. Threading and UI affinity

Plugin callbacks may run on background or UI paths depending on the plugin point.

Abstraction guidance:

- Callbacks that create or update `Visual` objects should be invoked through UI-safe services or clearly documented as UI-thread callbacks.
- Background callbacks should not mutate UI objects directly.
- `IPluginUiService` should marshal to the UI dispatcher as needed.
- Plugin authors should prefer `await` and cancellation over blocking calls.
- The API should avoid exposing CodeAlta's internal dispatcher type unless it becomes a stable public abstraction.

## 18. State and files

Plugins often need durable state. The abstraction should provide a state service instead of requiring each plugin to invent paths.

Recommended state scopes:

```csharp
public enum PluginStateScope
{
    User,
    Project,
    Thread
}
```

Recommended service:

```csharp
public interface IPluginStateStore
{
    string GetDirectory(PluginStateScope scope);
    ValueTask<T?> ReadJsonAsync<T>(PluginStateScope scope, string name, CancellationToken cancellationToken = default);
    ValueTask WriteJsonAsync<T>(PluginStateScope scope, string name, T value, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(PluginStateScope scope, string name, CancellationToken cancellationToken = default);
}
```

Rules:

- Deactivating/removing a plugin should not automatically delete user state unless the user requests cleanup.
- State paths should be namespaced by plugin runtime key.
- Thread-state writes should be safe during thread close/reload.
- Plugins may still use arbitrary filesystem APIs because they are trusted .NET code, but CodeAlta-provided state paths should be the recommended path.

## 19. Security and trust

Plugin abstractions should be designed as a trusted extension API.

Minimum requirements for future runtime/UI:

- clearly mark plugins as executable code;
- show source/package path and README/documentation when present before activation;
- show plugin type, version, author, dependencies, and contribution summary when available;
- show diagnostics and failures;
- allow enable/disable/deactivate;
- treat configured plugin roots as an explicit user-selected trust boundary, and provide clear disable/safe-mode controls for unwanted plugins.

The v1 abstraction should not implement permission enforcement. Plugins can read/write files, execute processes, make network requests, override built-ins, and inspect/modify prompt/runtime behavior wherever CodeAlta exposes a plugin point.

Future capability declarations may still be useful as documentation or management UI hints, but they should not be a prerequisite for v1 authoring.

## 20. Versioning and compatibility

The plugin abstraction package is a public authoring surface even while CodeAlta is pre-release.

Recommended versioning rules:

- Include `HostApiVersion` in `PluginHostInfo` or `PluginRuntimeContext`.
- Include optional `MinCodeAltaVersion` / `MaxCodeAltaVersion` in descriptor/attribute metadata.
- Prefer adding virtual methods over breaking existing method signatures.
- Prefer immutable records with optional init-only properties for contribution DTOs.
- Mark deprecated APIs with `[Obsolete]` before removal once CodeAlta stabilizes.
- Keep plugin abstractions free of internal CodeAlta UI/view-model types.

Because plugins load dynamically, runtime diagnostics for version mismatch should be clear and actionable.

## 21. Example: command + tool + prompt plugin

Illustrative authoring shape:

```csharp
[Plugin(DisplayName = "Repository Summary", Description = "Adds repository summary helpers.")]
public sealed class RepoSummaryPlugin : PluginBase
{
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Prompt(
            name: "repo_summary",
            description: "Ask the selected thread to summarize the current repository.",
            aliases: ["summarize_repo"],
            handler: static async (context, cancellationToken) =>
            {
                await context.Threads.SendPromptAsync(
                    "Summarize the repository structure, major components, and likely test commands.",
                    cancellationToken);
                return PluginCommandResult.Handled;
            });
    }

    public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
    {
        yield return Prompt.Developer(
            "If the repository summary plugin is active, prefer concise architecture summaries with file references.");
    }

    public override IEnumerable<PluginAgentToolContribution> GetAgentTools()
    {
        yield return Tool.FromDefinition(
            RepoSummaryTool.Create(),
            promptSnippet: "Use repo_summary to get a compact repository inventory when the user asks for architecture context.");
    }
}
```

The plugin never calls `RegisterCommand`, `UnregisterCommand`, `AddVisual`, or `RemoveVisual`. It returns contributions; the runtime owns the rest.

## 22. Proposed v1 abstraction cut

To keep the first implementation achievable, `CodeAlta.Plugins.Abstractions` v1 should include:

1. `PluginBase` and runtime/operation contexts.
2. `PluginAttribute`, optional dependency metadata, and descriptor records.
3. Runtime-owned contribution handles, not author-required contribution IDs.
4. Direct `XenoAtom.Logging.Logger` access per plugin.
5. Startup and command-line contributions.
6. Command/shortcut contributions.
7. Agent tool contributions wrapping `CodeAlta.Agent.AgentToolDefinition`.
8. Agent/backend/provider contribution seam for adding new `IAgentBackend` implementations or protocol adapters.
9. System prompt contributions.
10. Prompt input processors and before-run hooks.
11. Basic compaction contribution hooks: before, instruction, reducer, after.
12. Basic UI service and UI contribution records, including direct `Visual`/factory support.
13. Resource contributions for skill roots and system prompt roots.
14. Agent event observation callback.
15. State/workspace/thread/prompt/agent service facades.
16. Diagnostics attribution models for plugin failures.

Defer until after the first runtime integration:

- package/install/update formats;
- compilation pipeline;
- detailed AssemblyLoadContext/dependency resolver design;
- advanced provider settings/model-catalog/OAuth management;
- provider payload mutation hooks;
- custom prompt-builder replacement;
- full compaction replacement providers;
- marketplace and signing.
