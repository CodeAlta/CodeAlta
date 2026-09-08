---
title: For developers
---

# Plugin development

CodeAlta source plugins are trusted local .NET packages loaded into the
CodeAlta process. The public authoring surface lives in
`CodeAlta.Plugins.Abstractions`; source builds, loading, contribution
registration, diagnostics, and unload are handled by the plugin runtime.

> [!IMPORTANT]
> Plugin APIs are preview surface area before CodeAlta `1.0`. Keep plugins
> small, avoid depending on undocumented host internals, and expect interfaces
> or contribution records to change between `0.x` releases.

> [!WARNING]
> Build and load only source plugins you trust. Building can execute SDK,
> NuGet, and MSBuild logic; loading executes .NET code in the CodeAlta process.

## Custom host startup feedback

If you embed the plugin runtime, startup presentation is now explicit:
`PluginRuntimeManagerOptions.StartupFeedback` (or
`CodeAltaHostOptions.PluginStartupFeedback`) defaults to silent feedback, even
when the host is nonheadless. Supply a borrowed `IPluginStartupFeedback` to
present progress. The normal TUI already injects its terminal adapter on both
startup routes, so its startup display and Enter acknowledgement are unchanged.

The pre-release `BuildWithInteractiveLiveAsync` helper moved from
`PluginStartupFeedbackReporter` to
`CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback`. Keep backend plugins and
shared hosts on the neutral port rather than adding a TUI dependency.
Interactive `PluginChangeNotificationService` construction now requires an
explicit notification sink; there is no implicit global toast fallback.
Ordinary source-plugin contribution methods do not need changes for this step.

Feedback does not own the runtime or guarantee termination: a terminal-display
or completion-summary failure can still escape before the startup operation is
joined. This change does not make every plugin contract frontend-neutral.

## Portable content and terminal visuals

Region content can now use `PluginContentContribution` (or `PluginUi.Content`)
with a required `CreateContent` callback returning `PluginRenderResult` text or
Markdown. Contributions still use `GetUiContributions` and the existing
command-bar, session-footer, and session-status regions.

```csharp
yield return new PluginContentContribution
{
    Region = PluginUiRegion.SessionFooter,
    CreateContent = static _ => new PluginRenderResult { Text = "Hello plugin" },
};
```

For terminal visuals, reference the optional `CodeAlta.Plugins.Tui` assembly
and import its namespace. Replace `PluginUi.Visual` with `PluginTui.Visual`.
`PluginVisualContribution` now lives in that assembly and also requires a
portable `CreateContent` callback. For terminal renderer contributions, use
`PluginTerminalRendererContribution` or `PluginTui.Renderer`, supplying both
the portable renderer and the native callback. Shared `PluginRenderResult`
no longer has `Visual` or `FromVisual`; use `PluginTerminalRenderResult` for
native renderer output. Agent-tool renderer callbacks remain portable.

The TUI explicitly enables the call-scoped `SupportsTerminalVisuals`
capability. Other callers default to portable callbacks; this capability is
not a permission grant or a claim that dialogs and prompt attachments work.
Headless and noninteractive UI bypasses remain in place. Direct native
visuals take precedence over factories. Selected native null results mean
no item, and native failures do not silently switch to the portable callback.
Provide useful text explaining terminal-only actions rather than pretending
an unsupported action succeeded. A portable callback may return null when
absence is intentional, but the callback itself must not be null.

Generated source-plugin targets conditionally reference the optional assembly
when it exists beside the host executable, with `Private=false`. Do not add a
reference to the TUI executable or edit generated root files to author visuals.
This is a pre-release migration, not a claim of complete terminal-free plugin
dependencies or desktop rendering parity; other plugin contracts and builtin
implementations still contain terminal-specific code.

## Typed keyboard shortcuts

Plugin command shortcuts now use neutral key definitions rather than terminal
structs or display strings. Set `PluginCommandContribution.KeyBinding` to a
binding with one to four strokes, for example:

```csharp
new PluginKeyBinding(
    new PluginKeyGesture('G', PluginKeyModifiers.Ctrl),
    new PluginKeyGesture('Y', PluginKeyModifiers.Ctrl))
```

Use `PluginKey` for named keys, or a character / `System.Text.Rune` for one
Unicode scalar. Modifiers are explicit `Ctrl`, `Alt`, `Shift`, and `Meta` flags;
Ctrl is not automatically translated to Command/Super. Letter case is
normalized invariantly, while non-letter scalars and named-key identities stay
distinct. Input arrays are copied. Invalid keys/modifiers, raw control
characters, invalid surrogates, default strokes, and empty or overlong bindings
are rejected when constructing the definition.

This replaces the old `DisplayText`, `Gesture`, and `Sequence` initializer API.
Use `KeyBinding = null` for no shortcut, and ordinary letters plus `Ctrl` rather
than terminal control-character constants. One stroke is a single gesture;
two to four form a sequence. Display text no longer determines conflicts:
diagnostics compare typed strokes, order and all modifiers, without resolving
collisions or removing commands.

The TUI maps these definitions through the optional terminal assembly. If any
stroke is unsupported, such as a supplementary Unicode scalar, the whole
binding is left unbound; the command and its existing visibility rules remain.
Mapping success does not guarantee the terminal or operating system delivers
the shortcut. Meta participates in matching, but the current native shortcut
hint formatter omits it. This migration does not add desktop keyboard routing,
IME handling, or a new sequence-timing policy.

## Source plugin layout

Create one package directory per plugin under a global or project plugin root:

{.table}
| Scope | Entry file |
|---|---|
| Global | `~/.alta/plugins/<package-id>/plugin.cs` |
| Project | `<project>/.alta/plugins/<package-id>/plugin.cs` |

Project plugins apply only to that project. Global plugins apply across
workspaces. Package ids may contain letters, digits, `.`, `_`, and `-`, and
must start with a letter or digit.

A package may include an optional `readme.md` or `README.md`; plugin management
can open it. Other files can sit beside `plugin.cs`, and source can include
additional files with file-based C# `#:include` directives.

Do not add these root-level files to a source plugin package:

- `Directory.Build.props`
- `Directory.Build.targets`
- `Directory.Packages.props`
- `global.json`

CodeAlta generates those files for the package root, selects the supported
.NET 10 SDK, references host assemblies such as
`CodeAlta.Plugins.Abstractions`, and runs:

```sh
dotnet build plugin.cs
```

Use file-based C# `#:package` directives for plugin-owned NuGet packages. The
SDK copies dynamic dependencies when needed; plugin-owned dependencies must be
available in the plugin output folder for the collectible plugin load context.

## Minimal plugin

Create `~/.alta/plugins/HelloWorld/plugin.cs`:

```csharp
using CodeAlta.Plugins.Abstractions;

[Plugin(DisplayName = "Hello Plugin", Description = "Adds a command and status row.")]
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
        yield return Prompt.Developer(
            "When the user asks about the hello plugin, explain that it is installed.");
    }

    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        yield return PluginUi.SessionStatus("Hello", "active");
    }
}
```

Restart CodeAlta or refresh plugin state from plugin management. If the package
builds and loads, plugin management shows the plugin, its diagnostics, and its
contribution summary.

## Plugin class and metadata

A source plugin assembly can contain one or more plugin classes. Each plugin
class must be a visible concrete, non-generic `PluginBase` subclass with a
public parameterless constructor.

`PluginAttribute` is optional. Use it to provide display metadata or an
explicit key hint:

```csharp
[Plugin(
    "hello-world",
    DisplayName = "Hello Plugin",
    Description = "Local hello workflow.",
    Version = "0.1.0",
    Author = "Me",
    Tags = ["example"])]
public sealed class HelloPlugin : PluginBase
{
}
```

A plugin can also override `Describe()` for descriptor data. The source package
id is used for TOML enablement. The runtime owns the final runtime key used in
diagnostics, contribution handles, and provenance.

## Enablement and safe mode

Source plugins are enabled by default when discovered. Disable a plugin in
TOML when you do not want it built or loaded:

```toml
[plugins.HelloWorld]
enabled = false
```

If CodeAlta cannot start because a plugin is broken, bypass source plugins with
one of:

```sh
altatui --no-plugins
altatui --plugin-safe-mode
```

Or set:

```sh
CODEALTA_DISABLE_PLUGINS=1
```

`CODEALTA_DISABLE_PLUGINS=true` is also accepted.

## Lifecycle

The runtime:

1. resolves global and project plugin roots;
2. reads global/project plugin config;
3. discovers source packages and built-in plugins;
4. generates plugin-root build files;
5. builds source packages when needed;
6. loads assemblies into collectible contexts;
7. creates plugin instances;
8. attaches `PluginRuntimeContext` and initializes/activates plugins;
9. registers contribution records with runtime-owned handles;
10. monitors source/config changes and emits diagnostics;
11. cancels tracked tasks and unloads contexts on shutdown or reload.

Override `InitializeAsync`, `OnActivatedAsync`, `OnDeactivatingAsync`, and
`DisposeAsync` only when the plugin owns state that needs lifecycle work. Use
`Logger`, `Services`, `Ui`, `Tasks`, `Scope`, `ScopeProjectId`, and
`ScopeProjectPath` through `PluginBase` after the runtime context is attached.

## Contribution points

Override only the `PluginBase` methods your plugin needs:

{.table}
| Method | Contribution |
|---|---|
| `GetStartupContributions()` | Early startup hooks and early resources. |
| `GetCommandLineContributions()` | Raw `XenoAtom.CommandLine` command nodes. |
| `GetCommands()` | Shell, prompt-editor, and selected-session commands. |
| `GetAgentTools()` | LLM-callable agent tools. |
| `GetAltaCommands()` | Command roots under the in-session `alta` live tool. |
| `GetSystemPromptContributions()` | Static or dynamic system/developer prompt parts. |
| `GetPromptProcessors()` | Prompt submission processors. |
| `GetPromptEditorContributions()` | Plugin-owned prompt editor attachments. |
| `GetCompactionContributions()` | Compaction observe/instruction/reducer hooks. |
| `GetUiContributions()` | Status rows, visuals, and renderers. |
| `GetSessionEventProjections()` | Transient timeline/session event projections. |
| `GetResources()` | Plugin resource roots. |
| `OnPromptSubmittingAsync(...)` | Prompt submission observation/transformation. |
| `OnBeforeAgentRunAsync(...)` | Dynamic context before an agent run. |
| `OnToolCallAsync(...)` | Tool-call interception before execution. |
| `OnToolResultAsync(...)` | Tool-result replacement before model replay. |
| `OnAgentEventAsync(...)` | Normalized agent event observation. |

Low-ceremony factories are available through `Command`, `Startup`, `Prompt`,
`PluginUi`, `Resources`, and `AgentTool`.

## Commands and UI

Plugin shell commands are no-argument frontend activations. A
`PluginCommandContribution` declares its name, label, description, placement,
command-palette/search metadata, visibility flags, optional shortcut, and
availability rule. CodeAlta adapts active contributions into the same shell
command registry as built-in commands.

`PluginCommandContext` exposes public services such as `Ui`, `Sessions`,
`Prompts`, and `Workspace`. It intentionally does not expose raw slash-command
text, parsed argument tokens, internal frontend command contexts, frontend view
models, or `XenoAtom` render targets. Commands that need input should use
plugin UI services, prompt/session services, or a plugin-owned dialog/workflow.

UI contributions are optional frontend features. Headless hosts can ignore them
or provide no-op services through `IPluginUiService.HasInteractiveUi == false`.
When constructing a `XenoAtom.Terminal.UI.Controls.Dialog` directly, use
`PluginDialogLayout.ApplyResponsiveSize(...)` with a deferred bounds delegate so
custom dialogs keep CodeAlta's responsive sizing behavior.

## Prompt editor attachments

`GetPromptEditorContributions()` attaches plugin-owned behavior to prompt
editors. The host exposes a small editor host: prompt text, caret index,
project path, editor-state events, accepted events, focus, and the editor
visual as an anchor. The plugin owns trigger detection, popup/dialog/control
choices, insertion behavior, and presentation.

A prompt-editor contribution can set
`PluginPromptEditorContribution.PlaceholderText` with a short placeholder
segment such as `[#] to reference a GitHub issue`. CodeAlta adds applicable
plugin segments to the ready prompt placeholder.

Keep prompt-editor callbacks cancellable and avoid long synchronous work so
typing stays responsive.

## Final instruction processors

`GetPromptProcessors()` handles user prompt text and attachments before a turn
is submitted. To inspect or replace final system/developer instructions, use
`GetInstructionProcessors()` instead. Instruction processors run after CodeAlta
has composed the system prompt, agent prompt, generated runtime/project/tool
context, and plugin prompt parts, but before provider submission, prompt
hashing, and prompt journal metadata.

Return `PluginInstructionProcessingResult.Continue`, `Replace(...)`, or
`Cancel(...)`. Keep `ChangeSummary` and metadata audit-safe; CodeAlta records
which trusted plugin changed instructions and which channels changed, not the
pre-transform text. This is trusted in-process customization, not a sandbox or
security boundary.

The built-in plugin-runtime skill includes an
`instruction-path-normalizer` sample that changes Windows-style backslashes to
forward slashes inside generated runtime-context paths.

## Agent tools and `alta` live-tool commands

`GetAgentTools()` contributes host-injected agent tools. The contribution wraps
an `AgentToolDefinition` and can include concise prompt snippets or guidance.
Tool calls and results can also be observed or transformed through
`OnToolCallAsync` and `OnToolResultAsync`.

`GetAltaCommands()` extends the in-session `alta` live tool with
`PluginAltaCommandContribution` records. Each contribution declares a command
path, policy flags, ordering, and a factory that creates a fresh unattached
`XenoAtom.CommandLine.CommandNode` for each registry build. Core command roots
are reserved and collisions are rejected.

Plugins can invoke built-in `alta` commands through `Services.Alta`:

```csharp
var result = await Services.Alta.InvokeAsync(
    ["session", "create", "--project", projectId],
    cancellationToken: cancellationToken);
```

The service returns the same flattened JSONL transcript shape used by agent
live-tool calls, plus exit code, truncation status, and an error summary.
Project-scoped invocations inherit project scope and working directory by
default.

## Resources and projections

Resource roots describe plugin package content for host services:

```csharp
public override IEnumerable<PluginResourceContribution> GetResources()
{
    yield return Resources.SkillRoot("skills");
    yield return new PluginResourceContribution
    {
        Kind = PluginResourceKind.SystemPromptRoot,
        Path = "prompts",
    };
}
```

Relative paths are resolved from the plugin package directory. Project-scoped
plugin resources are visible only for the matching project. The resource kind
enum includes skill roots, system prompt roots, template roots, theme roots,
MCP server manifests, agent definition roots, and other plugin resources; host
features consume only the kinds they explicitly support.

Session event projections are transient derived timeline cards replayed from
canonical normalized event history. Projection output is not written to
conversation history. Use `IPluginStateStore` when plugin-owned data needs
persistence.

## Safe authoring notes

- Use `Services.Tasks.Run(...)` or the `PluginBase.Tasks` shortcut for
  background work so CodeAlta can cancel and track plugin tasks during unload.
- Avoid untracked `Task.Run`, static references to host objects, host-static
  delegates, pinned native resources, or unmanaged background work.
- Keep package roots simple and let CodeAlta own generated build files.
- Keep prompt and agent-tool guidance concise to preserve model context.
- Do not store secrets in plugin source or committed project plugin files; use
  environment variables, local state, or OS secret storage instead.

> [!CAUTION]
> A plugin that starts unmanaged background work, captures host-static state, or
> pins native resources can keep running after you expect it to unload. Prefer
> CodeAlta-managed task services and cancellable operations.
