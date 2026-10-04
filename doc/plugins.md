# Plugins

CodeAlta plugins are trusted local .NET code loaded into the CodeAlta process. The public authoring API lives in `src/CodeAlta.Plugins.Abstractions`; discovery, source builds, loading, activation, contribution registration, and diagnostics live in `src/CodeAlta.Plugins`.

## Authoring surface

A plugin usually references only `CodeAlta.Plugins.Abstractions`, inherits from `PluginBase`, and overrides the contribution methods it needs.

```csharp
using CodeAlta.Plugins.Abstractions;
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
        yield return PluginUi.SessionStatus("Hello", "active");
        yield return new PluginContentContribution
        {
            Region = PluginUiRegion.SessionFooter,
            CreateContent = static _ => new PluginRenderResult { Text = "Hello plugin" },
        };
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

## Portable region content and terminal rendering (pre-release migration)

`PluginContentContribution` supplies a required `CreateContent` callback returning a portable `PluginRenderResult` (`Markdown` or `Text`). It uses the existing `CommandBar`, `SessionFooter`, and `SessionStatus` regions and the existing `GetUiContributions` registration, scope, and ordering rules. `PluginUi.Content` is the convenience factory. This is not a new workspace-panel or navigation framework.

Native visual authoring moves to the optional `CodeAlta.Plugins.Tui` assembly and namespace:

- Replace `PluginUi.Visual` with `PluginTui.Visual`, or construct `PluginVisualContribution` from the optional assembly. Supply a nonnull portable `CreateContent` callback as well as the direct visual or visual factory.
- `PluginRenderResult.Visual` and `PluginRenderResult.FromVisual` are removed. Shared `PluginRenderer` callbacks, including `PluginAgentToolContribution.Renderer`, now return only portable content.
- For native renderer contributions, use `PluginTerminalRendererContribution` / `PluginTui.Renderer`, with both a portable `Renderer` and a terminal callback returning `PluginTerminalRenderResult`.
- Shared adapter callers replace `CreateVisuals` with `CreateContent`; portable `RenderAsync` remains available. Terminal materialization belongs to the borrowing frontend adapter, not the shared runtime.

`PluginAdapterOperationOptions.SupportsTerminalVisuals` is a call-scoped presentation capability, defaulting to false. The TUI bridge explicitly enables it. It is not a permission grant, and does not imply support for custom dialogs or prompt-editor attachments. Headless and explicitly noninteractive calls retain their UI bypass.

Without terminal support, only the portable callback runs. With terminal support, a direct visual takes precedence over its factory, without constructing a visual context. A selected native callback returning null means no item; a native exception does not trigger fallback. A portable callback may intentionally return null for absence, but terminal-only actions should provide explanatory text rather than claim unsupported functionality succeeded. MCP's portable status uses its existing plain status label; the native management button/dialog remains terminal-specific.

Content callbacks remain synchronous and their exceptions escape. Renderer ordinary failures are diagnosed and traversal continues; cancellation exceptions escape. Renderer contexts are invalidated only after successful callback completion, including a null result, not in a new finally block. These adapters borrow the shared runtime and add no lifetime owner.

Source-plugin compilation and loading use an explicit authoring profile, described below. Assembly compatibility is independent of call-scoped presentation support: allowing terminal references does not authorize a native callback or make an unsupported dialog work.

**Dependency boundary:** Abstractions no longer exposes terminal types or references terminal packages. Native dialog content/layout, prompt-editor anchors, shortcuts and session-event factories belong in the optional authoring assembly. Builtin plugins still mix backend and terminal code. Contract and source-profile separation do not establish backend-only builtin loading, desktop panel parity or installed/runtime qualification.

## Source-plugin authoring profiles (pre-release migration)

`CodeAlta.Plugins.PluginAuthoringProfile` has two values:

| Profile | Host-generated authoring references | Managed dependency policy |
| --- | --- | --- |
| `Neutral` (reusable API default) | Existing neutral CodeAlta assemblies and nonterminal shared packages; no optional TUI assembly or terminal packages | Normal plugin loading preflights reachable managed metadata and refuses `CodeAlta.Plugins.Tui` and the `XenoAtom.Terminal` assembly family. |
| `Terminal` | The neutral references, optional `CodeAlta.Plugins.Tui` with `Private=false`, and all five existing terminal authoring packages | Terminal identities resolve through the host/default context, never a private plugin copy. |

Custom hosts preserving rich source plugins must set `PluginRuntimeManagerOptions.AuthoringProfile` or `CodeAltaHostOptions.PluginAuthoringProfile` to `Terminal`. Direct generator callers set `PluginRootBuildFileOptions.AuthoringProfile`; direct loader/context callers use the explicit-profile constructor overloads. Keep generation and loading on the same profile. The TUI explicitly selects Terminal in both its early plugin bootstrap and shared-host composition, including noninteractive/CLI usage. Neither `IsHeadless`, a selected tab nor the presence of a stale DLL chooses a profile. A borrowed `PrestartedPluginRuntime` is neither restarted nor reprofiled by host options.

`HostAssemblyNames` and `SharedPackageNames` on generation options are now nullable **additional** lists: null selects the profile defaults, and explicit entries extend rather than replace them. Loader/context shared-name lists are likewise additive; mandatory identities cannot be removed. Neutral rejects explicit terminal entries. The existing `DefaultSharedPackageNames` and `DefaultHostSharedAssemblyNames` properties remain Terminal compatibility catalogs, not the effective Neutral defaults. Select Terminal rather than passing those full catalogs to a Neutral host.

Generated properties stamp the profile, authoring-policy version and host API version. The existing four generated-file hashes invalidate build manifests when these inputs change; no new cache schema or cleanup is required. A root whose generation reports failure is excluded from scheduling and cached loading, with its diagnostics retained; successfully generated roots continue. Do not edit marker-protected generated files or copy a terminal assembly into a neutral root as a workaround.

The normal loader checks the main artifact's declared identity before loading it for either profile. Neutral additionally inspects reachable nonplatform dependency metadata without constructing plugin objects or enumerating types. It does not grant platform status to arbitrary `System.*` names or all trusted-platform-assembly entries. Unknown/uninspectable dependencies and dependencies requiring executable resolution callbacks are refused rather than guessed portable. This does not change general discovery/activation/unload guarantees, and low-level ALC APIs are not a sandbox.

**Trusted-code and qualification limits:** Profiles remove host-injected terminal dependencies; they do not police arbitrary packages or transitive restore graphs requested by trusted plugin source. Asset exclusions are not a pre-restore acquisition policy. Builtin separation, real metadata/CLR behavior, regenerated rich source builds, packaged contents and default-profile startup still require their own qualification. No desktop plugin host or renderer-sidecar discovery is introduced by this migration.

## Typed key bindings (pre-release migration)

`PluginCommandContribution.KeyBinding` now uses neutral `PluginKeyBinding` definitions. Construct a binding from one to four `PluginKeyGesture` strokes, each containing either a `PluginKey` named key or one Unicode scalar, plus explicit `PluginKeyModifiers` (`Ctrl`, `Alt`, `Shift`, `Meta`). For example, MCP's shortcut becomes:

```csharp
new PluginKeyBinding(
    new PluginKeyGesture('G', PluginKeyModifiers.Ctrl),
    new PluginKeyGesture('Y', PluginKeyModifiers.Ctrl))
```

The old `DisplayText`, terminal `Gesture`, and terminal `Sequence` initializer properties are removed. Use `KeyBinding = null` for an unbound command; a display label alone no longer defines a shortcut. Author Ctrl letters as ordinary letters plus `Ctrl`, not raw terminal control characters. Definitions reject undefined named keys, unknown modifier bits, control characters, invalid surrogate input, default gestures, empty bindings and bindings longer than four strokes. Bindings copy their input, so subsequent array changes cannot mutate registered definitions.

Letter scalars are normalized using invariant uppercase; non-letter scalars are preserved. Named Space and character space remain distinct, as do all modifier masks. One stroke routes as an ordinary gesture, not as a one-element native sequence. Generated text includes Meta but is presentation only: there is no parser, physical-key/scan-code identity, platform-primary-modifier alias or grapheme/IME contract.

The optional `PluginTerminalKeyBindingMapper.TryMap` maps named keys and modifier bits explicitly, translating Ctrl+A–Z to terminal control-character encoding. Supplementary scalars remain valid neutral data but cannot fit the terminal character representation. An unsupported stroke rejects the entire mapping with both outputs null; the TUI retains the command unbound, with unchanged handlers, placement and visibility flags. It does not force palette visibility or manufacture a shortcut hint. Successful mapping establishes representability, not delivery by every OS, terminal protocol or keyboard. The existing native hint formatter omits Meta even though matching retains it, and may show a leading `+` for Meta-only gestures; this display limitation remains unfixed.

Shared conflict diagnostics now use one structural key per binding: `keybinding:<count>:<kind><six-digit-hex-identity>/<two-digit-hex-modifiers>;...`, with `N` for named keys and `U` for scalars. Stroke order, kind and complete modifiers participate; display text does not. Equivalent definitions produce one binding warning rather than separate label/gesture/sequence warnings. Command-name conflicts remain separate and first. Warnings do not remove commands or change native dispatch precedence; no prefix-conflict detection or override policy was added.

No desktop keyboard router, RPC projection, focus policy, sequence timer or command executor is introduced by this contract migration. Native gesture-before-prefix routing and sequence cancellation/timing remain frontend responsibilities. Other plugin contracts still carry terminal dependencies.

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

## Host-selected startup feedback (pre-release migration)

`PluginRuntimeManagerOptions.StartupFeedback` and `CodeAltaHostOptions.PluginStartupFeedback` now accept a borrowed `IPluginStartupFeedback`. Each options instance defaults to `SilentPluginStartupFeedback`, including nonheadless callers. Setting `IsHeadless = false` alone no longer selects terminal presentation. Null feedback is rejected before mutable startup, including host calls with prestarted or disabled plugins; the runtime/host never disposes the supplied port.

The shared port and `IPluginStartupProgress` live in `CodeAlta.Plugins`. Headless startup and empty build-request lists bypass presentation and invoke the original startup operation with null progress. For other routes, the selected port receives the original requests, operation, summary factory and cancellation token. Silent feedback does not invoke the summary factory or wait for acknowledgement. Discovery/build/activation order and scheduler progress subscription lifetime remain runtime responsibilities.

The terminal implementation is `CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback`. Both Program's prestart and the owned-services/host fallback explicitly inject it, preserving the existing TUI display, redirected-terminal fallback and `--plugins-wait-for-enter` behavior. The public `BuildWithInteractiveLiveAsync` helper moved from `PluginStartupFeedbackReporter` to this terminal adapter. This is a pre-release API move with no shared-to-TUI forwarding dependency; shared hosts and backend plugins should use the neutral port rather than reference the TUI.

`PluginChangeNotificationService` also requires an explicit `Action<string>` sink when `Interactive` is true; it no longer falls back to global `ToastService`. Headless defaults remain unchanged. The sink is borrowed and still executes under the existing gate after status publication. If it throws, the published status is retained and the exception escapes. This extraction does not add watcher/toast integration where none existed.

**Lifetime limits:** normal terminal-driver return joins the original startup operation, even if cancellation stopped the display. A live-driver or completion-summary exception still takes precedence and can escape without that join. Feedback is not a termination guarantee or a new cleanup owner. The remaining terminal types in plugin abstractions, contribution adapters and shared build/loading dependency lists are not removed by this feedback-only extraction.

## Contribution areas

`PluginBase` exposes virtual methods for:

- startup hooks and command-line contributions;
- shell, prompt, and selected-session commands;
- agent tools;
- `alta` live command roots;
- static and dynamic system/developer prompt parts;
- prompt processors, prompt-editor attachments, system/developer prompt parts, and final instruction processors;
- before-agent-run hooks;
- tool-call and tool-result hooks;
- normalized agent-event observers;
- compaction hooks;
- UI contributions such as status rows, visuals, and renderers;
- transient session/timeline projections (current APIs still use some legacy `Session` names);
- resource roots for skills, system prompts, templates, themes, and MCP manifests;
- plugin-lifetime background tasks through `IPluginTaskService`.

Plugin shell commands are no-argument frontend activations. A `PluginCommandContribution` declares its name, label/description, placement (`ShellRoot`, `PromptEditor`, and/or `WorkspaceRoot`), command-palette/search metadata, visibility flags, optional shortcut, and availability rule. CodeAlta adapts each active contribution into the same internal shell command registry used by built-ins, so plugin commands can appear in help, the command palette, command bars, and shortcuts without frontend-specific registration code.

`PluginCommandContext` intentionally exposes public services such as `Ui`, `Sessions`, `Prompts`, and `Workspace`; it does not expose raw slash-command text or argument tokens. Commands that need user input should use plugin UI services, prompt/session services, or a plugin-owned dialog/workflow contribution. Frontend state is mapped to public plugin operation options before the handler runs; plugins never receive internal `ShellCommandContext`, XenoAtom `Visual` targets, or frontend view models.

Low-ceremony factories are available through `Command`, `Startup`, `Prompt`, `PluginUi`, `Resources`, and `AgentTool`.

UI-only contributions remain frontend responsibilities. Headless hosts can ignore them or expose no-op services through `IPluginUiService.HasInteractiveUi == false`.

When a plugin constructs a `XenoAtom.Terminal.UI.Controls.Dialog` directly, use `CodeAlta.Plugins.Tui.PluginDialogLayout.ApplyResponsiveSize(...)` with a deferred bounds delegate (for example, `() => PluginDialogLayout.ResolveDialogBounds(anchor)`) so the dialog keeps the same centered, responsive sizing behavior as built-in dialogs, including cases where the dialog is sized after it is attached to the app.

`PluginDialogRequest` retains neutral text, selection, button and metadata fields. For native custom content, migrate `PluginUi.CustomDialog(title, visual)` to `PluginTui.CustomDialog(title, visual)` and `PluginDialogRequest.Content` to `PluginTerminalDialogRequest.Content`. These APIs describe requests; they do not install a dialog presenter. Currently the generic dialog operations have only the no-op service implementation: `HasInteractiveUi` is false, `ShowDialogAsync` validates/cancels but does not present anything, and `ShowDialogForResultAsync` returns null when unsupported. Completion is not proof of presentation. Existing MCP/GitHub native dialogs use their separate terminal paths.

## Prompt and instruction processing

`PluginBase.GetPromptProcessors()` is for user prompt text and attachment preparation before a turn is submitted. It must not be used to mutate built-in system/developer instructions.

Plugins that need to inspect, redact, or replace final system/developer instructions can override `PluginBase.GetInstructionProcessors()` and return `PluginInstructionProcessorContribution` records. The host runs these trusted in-process hooks after built-in prompt templates, runtime/project/tool/skill context, plugin prompt parts, and before-agent-run prompt additions are composed, and before provider submission, prompt hashing/statistics, and system-prompt journal/manifest events. Processors run in deterministic contribution order and receive a `PluginInstructionProcessingContext` with provider/model/session/project metadata, active tool names, audit-oriented part metadata, prior transformation records, and a final `PluginInstructionSnapshot` for the current system/developer channels.

Instruction processors return `PluginInstructionProcessingResult.Continue`, `Replace(...)`, or `Cancel(...)`. Replacement results can replace one channel while preserving the other, and should include only audit-safe `ChangeSummary`/`Metadata`; the host records plugin identity, order, changed channels, disposition, and post-transform hash in the prompt manifest without logging pre-filter secret-bearing text. This is an extensibility/audit mechanism for trusted local plugin code, not a security boundary.

The built-in `codealta-plugin-runtime` skill includes an `instruction-path-normalizer` sample that uses this hook to replace backslashes with forward slashes inside the generated `# Runtime Context` section. Copy `samples/instruction-path-normalizer/` into a plugin root to experiment with final instruction transforms.

## Prompt-editor attachments

Plugins can implement `PluginBase.GetPromptEditorContributions()` to attach plugin-owned behavior to prompt editors. The neutral `IPluginPromptEditorHost` exposes `Text`, `CaretIndex`, `ProjectPath`, editor-state/accepted events and focus. Native anchors now belong to `CodeAlta.Plugins.Tui.IPluginTerminalPromptEditorHost.Visual`; plugins own trigger detection, popup/dialog/control choices, insertion behavior and presentation.

For terminal attachments, use `PluginTui.PromptEditor(name, attach, placeholderText, order)`. It returns the existing neutral contribution and defers the callback until `Attach` receives an `IPluginTerminalPromptEditorHost`. Other hosts receive null without invoking the callback. Terminal callback null means declined attachment; exceptions propagate without fallback. The factory does not dispose returned attachments or change host attachment lifetimes. Neither `SupportsTerminalVisuals` nor `HasInteractiveUi` substitutes for this typed host check. GitHub uses this route; no desktop picker or prompt host is introduced by the migration.

Prompt-editor contributions can set `PluginPromptEditorContribution.PlaceholderText` to add a short segment to the ready prompt placeholder while the contribution applies. Phrase it like the built-in segments, for example `[#] to reference a GitHub issue`; CodeAlta inserts plugin segments after project-file guidance and before send/new-line/steer guidance.

Keep attachments cancellable and avoid long synchronous work so typing in the prompt stays responsive. Headless hosts can skip prompt-editor attachments. Do not advertise a picker merely because contribution metadata exists when its attachment was declined.

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
    yield return new PluginResourceContribution
    {
        Kind = PluginResourceKind.SystemPromptRoot,
        Path = "prompts",
    };
}
```

Relative paths are resolved from the plugin package directory. Project-scoped plugin resources are visible only in matching project scope.

## Session-event projections

Plugins can contribute transient derived timeline cards through `GetSessionEventProjections()` (legacy API name). Projections are replayed from canonical normalized event history and can also run live as new events arrive. They may provide Markdown fallback content, XenoAtom visuals, collapsed detail sections, and dynamic content that starts with a placeholder and refreshes after background computation.

Projection output is not written to canonical conversation history. Store plugin-owned durable state through `IPluginStateStore` when a plugin needs persistence.

The neutral `PluginDerivedSessionEvent`, `PluginDerivedSessionEventDetailSection` and
`PluginDynamicDerivedSessionEventContent` expose Markdown, details, identities and change
notifications without native factories. For rich terminal cards, reference the optional
`CodeAlta.Plugins.Tui` assembly/namespace and use `PluginTerminalDerivedSessionEvent`,
`PluginTerminalDerivedSessionEventDetailSection` and
`PluginTerminalDynamicDerivedSessionEventContent`. The terminal variants retain
`VisualFactory`; the detail variant also has `HeaderVisualFactory`.
`PluginSessionEventVisualFactory` and `PluginSessionEventVisualContext` moved to that
optional namespace. This is a pre-release API break; migrate native initializers and
dynamic-content overrides, not the canonical projection handler or backend computation.

Keep meaningful Markdown/header fallback fields even when supplying native factories.
Factories are borrowed and invoked later by terminal rendering; a native failure does not
cause a second fallback invocation. Initial upsert prefers a dynamic native factory over
the event's static factory; a later dynamic refresh uses only the dynamic factory, so null
clears the previous native factory. This preserves existing behavior rather than changing
notification or resource ownership. `Payload` remains an opaque in-process object, **not a
desktop wire schema**. This contract split does not add desktop transient projection
rendering, terminal-free builtin loading, or background-task/unload guarantees.

## Background tasks and unload

Long-running plugin work should use `Services.Tasks.Run(...)` or the `PluginBase.Tasks` shortcut instead of untracked `Task.Run`. The runtime tracks task handles, cancels them during deactivation, and can delay unload while tracked work completes.

Unload can still fail if plugin code keeps static references, host-static delegates, pinned native resources, or untracked background work alive.

## Built-in plugins

Built-ins use the same abstraction model. The GitHub backend is packaged as `CodeAlta.Plugin.GitHub` and is enabled by default in the TUI. Its HTTP client, authentication, repository/issue queries, agent tool and disposal have one backend owner. The TUI injects the existing `#` prompt contribution into that instance; the picker, attachment, prompt parser and terminal binding accessors belong to the TUI, not the backend assembly. A parameterless `GitHubPlugin` supplies no prompt presentation. Custom heads can use the constructor taking `Func<GitHubPlugin, IEnumerable<PluginPromptEditorContribution>>` to supply their own neutral contributions. The delegate is deferred until enumeration and receives the same backend instance; it does not transfer backend lifetime to an attachment. No desktop picker is implied.

The TUI picker works for projects whose Git remotes point at `github.com` and inserts links like `[#18](https://github.com/org/repo/issues/18)`. The backend uses the GitHub REST API through `HttpClient` and exposes the `gh` agent tool only when the GitHub CLI is installed. The `gh` tool schema accepts arguments as an array of strings and executes them through `ProcessStartInfo.ArgumentList`, not a shell command string. Missing `gh` is logged and may be notified through the host UI without repeated launch spam.

Repository detection and the issue query live in `GitHubIssueLookup`, which the plugin delegates to and other hosts can reuse without a plugin instance. It walks up for `.git`, prefers the `origin` remote, accepts only `github.com` remotes, never returns pull requests and orders results by last update. An instance can keep the detected repository (per folder), the recent issue list (per repository) and the resolved token for `CacheDuration` (60 seconds by default); the TUI plugin passes zero, so the terminal picker still asks on every query. The git remote reader, token provider, clock and HTTP handler are injectable. `QueryAsync` reports the first refused HTTP status beside the issues it could still gather.

The desktop host runs the GitHub plugin as a backend (the `gh` agent tool), without the terminal picker; its window has its own. Its owned-host `githubIssues.search` RPC resolves the folder from a catalog project id (never from a path sent by the page), answers `not_github` without a network request when the project has no `github.com` remote, and returns bounded, control-character-free titles and `https://github.com` links only. Failures are reported as `failed` with a generic message: exception text, paths and credentials do not cross the bridge. The service is not registered for catalog-only launches.

**Builtin factory migration:** registered `BuiltInPluginDefinition.Factory` callbacks now construct the actual activated instance. A supplied factory runs once per activation attempt, without reflection fallback after a null result or exception. Its returned concrete type must match the discovered type; validation occurs after retaining the instance inside the existing failure/disposal boundary. Dynamic source plugins retain reflection construction. Supply `PluginType` when known: the legacy type-resolution path can separately invoke a factory for metadata, so this is not a single-call guarantee across discovery and activation. All three TUI registrations specify their type explicitly. Direct factory exceptions do not acquire reflection's constructor-exception wrapper; the existing cancellation exclusion and cleanup policy remain unchanged. This route does not strengthen general activation/unload guarantees.

The statistics plugin is packaged as `CodeAlta.Plugin.Statistics`, is enabled by default, can be disabled with:

```toml
[plugins.statistics]
enabled = false
```

It contributes transient per-turn/session statistics projections and a `statistics` live-tool command root without writing plugin messages into canonical session history.

The MCP plugin is packaged as `CodeAlta.Plugin.Mcp`, is enabled by default, and contributes the `alta mcp` command root plus compact dynamic developer prompt guidance for active/inactive configured MCP servers. It reads fixed MCP JSON server files (`~/.alta/mcp.json` and project `.alta/mcp.json`) and TOML policy under `[plugins.mcp]`; connection fields stay in JSON, while enablement, `disabled_tools`, prompt caps, timeouts, and direct-exposure policy stay in TOML. The plugin also provides reusable `McpManagementService`/runtime services consumed by session-activated MCP agent tools, the TUI MCP Servers dialog, and the status indicator. Dynamic MCP `AgentToolDefinition` exposure is progressive: `alta mcp activate <id>*` marks servers active for the session, and tools from active servers are registered on agent runs where policy allows them. Automatic refresh on `tool-list-changed` remains follow-up work unless direct-tool freshness requires it. See [MCP support](mcp.md).

**MCP presentation migration:** the parameterless `McpPlugin` now supplies backend contributions and portable status, without native revision state or the interactive management command. The TUI explicitly composes the existing command, status button, dialog, bindable rows and icons from `CodeAlta.Tui/Plugins/Mcp/`. Its presentation borrows the plugin's existing management and activation owners; it does not create another backend. This built-in wiring is internal, using friendship to the `altatui` assembly, not a supported public activation-state API or a backend-to-TUI project reference. Friendship is assembly-wide, not member-scoped.

Contribution enumeration does not read configuration or evaluate status content. Native decoration retains the exact portable callback and metadata, adding deferred visual rendering. Status snapshot timing, independent markup/tone refreshes and the existing revision dispatch/exception behavior are preserved; the extraction adds no unsubscribe/disposal policy. Backend commands, prompt discovery and explicit management/runtime operations are unchanged. This separation does not qualify native MCP execution, authentication, real activation/loading or desktop MCP management.

The parameterless `StatisticsPlugin` now emits neutral transient events with portable Markdown summaries/details and the existing opaque payload, without terminal UI dependencies. The TUI explicitly decorates each existing cache candidate with deferred card/detail factories. Terminal controls and ANSI summary styling live in the TUI; calculations, portable formatting, event identifiers, fingerprint/cache keys and live-tool commands remain in the backend. This internal built-in composition is not a public statistics-model API or a new renderer dispatch mechanism.

The presentation borrows callbacks over the same already-built turn. Portable summary/details/payload remain eager; native summary/table formatting and controls remain deferred until rendering. Sequential cache reuse returns the decorated event; concurrent `GetOrAdd` candidates can each decorate, so this is not an exactly-once guarantee. Decorator exceptions/cancellation propagate without fallback. This change does not add desktop Statistics cards, qualify native rendering, or establish real builtin loading/activation.

## Troubleshooting

- **Missing SDK:** source plugins require the .NET 10 SDK selected by the generated plugin-root `global.json`. If `dotnet build plugin.cs` is treated as a project build, install the required SDK or start with safe mode.
- **Build failures:** startup shows concise live build progress and writes detailed diagnostics plus stdout/stderr tails under `~/.alta/logs/`.
- **Dependency load failures:** CodeAlta assemblies and shared authoring dependencies resolve from the host load context. Plugin-owned package dependencies must be copied by the SDK so the plugin load context can resolve them from the plugin output folder.
- **Broken plugin:** start with `--no-plugins`, `--plugin-safe-mode`, or `CODEALTA_DISABLE_PLUGINS=1`, then disable or edit the plugin package.
- **Unload delays:** ensure the plugin cancels tracked work and does not keep static references or unmanaged resources alive.
