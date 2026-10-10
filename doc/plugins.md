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

## Two applications: desktop and terminal

The desktop application (`src/CodeAlta`) and the terminal application (`src/CodeAlta.Tui`) host the same plugin runtime. The sections marked "pre-release migration" below describe how the contract was split; this section describes what both hosts do now.

**Supported applications.** `PluginAttribute.Frontends` (`PluginFrontends.Terminal`, `Desktop`, `All`; default `All`) is copied to `PluginDescriptor.Frontends`, and `BuiltInPluginDefinition.Frontends` does the same for built-ins. A host names itself with `PluginRuntimeManagerOptions.Frontend` (`CodeAltaHostOptions.PluginFrontend`), reported to plugins as `PluginHostInfo.Frontend`. The runtime does not activate a plugin whose descriptor does not support the host: it records an Info diagnostic with the metadata key `PluginRuntimeManager.UnsupportedFrontendMetadataKey` and registers no contribution. A host that names no application (`PluginFrontends.None`, the headless tools) starts every plugin. The check runs after the build, because the attribute is read from the built assembly.

**One authoring profile.** Both applications use `PluginAuthoringProfile.Terminal` and ship `CodeAlta.Plugins.Tui` and the `XenoAtom.Terminal.UI` assemblies, so a plugin that references terminal controls compiles and loads in the desktop application. The generated `Directory.Build.props` records the folder of the running executable, so starting the other application regenerates the root files and rebuilds the plugins once. An installed tool has no `Directory.Packages.props` to read package versions from: `PluginRuntimeManager.ResolveInstalledPackageVersions` reads them from the product version of the host assemblies.

**Portable results with an HTML form.** `PluginRenderResult.Html`, `PluginDerivedSessionEvent.Html`, `PluginDerivedSessionEventDetailSection.Html`, `PluginDynamicDerivedSessionEventContent.Html` and `PluginDialogRequest.Html` carry a fragment for the desktop application. The desktop shows the richest form (`Html`, then `Markdown`, then `Text`) and never a terminal visual; the terminal ignores `Html`. `PluginDialogRequest.OnAction` handles the actions of an HTML dialog and returns `PluginDialogActionResult` (keep open, new HTML, or close with a button name); `PluginDialogResponse.Values` holds the named fields.

**HTML fragments are data, not code.** The page sanitizes a fragment with DOMPurify (`frontend/src/pluginHtmlSanitizer.ts`): an allow-list of text, structure and form elements, a per-element allow-list of attributes, `alta-*` classes only, and `http(s)` links that are shown and never followed. Scripts, styles, event handlers, images, frames and forms are removed. Buttons, inputs, tables, tags and callouts then get Blueprint classes. A fragment reaches the application only through attributes that `PluginHtml.tsx` handles by event delegation: `data-alta-command` runs a plugin command by name (the fragment's own plugin first), `data-alta-action` with `data-alta-value` raises a dialog action, and named fields are collected into `Values`. `PluginHtml` in the abstractions has the attribute and class names (rows and stacks, `alta-grow` for the element that takes the free width, `alta-field` for a label above its field, `alta-card`, tags, callouts, tones) and the `Encode`, `CommandButton` and `ActionButton` helpers. A plugin has no JavaScript in the window: the page runs the application's own scripts only, and what a fragment does goes through its plugin's C# handlers.

**What a fragment uses of the window.** An element of class `alta-markdown` (`PluginHtml.MarkdownClass`) holds Markdown as its text. `PluginHtml.tsx` takes that text out of the sanitized fragment (`pluginMarkdownSource` removes the indentation its lines share and the blank lines around it) and draws the element with `MarkdownContent`, the component of the timeline, through a React portal. A fragment therefore has what that component has, without a script of its own: the `markdown-it` renderer behind its own sanitizer, fenced code colored by highlight.js, and `mermaid` fences drawn as diagrams in the colors of the window. `PluginHtml.Markdown`, `PluginHtml.Code(code, language)` and `PluginHtml.Diagram(mermaid)` write such an element with the text encoded; `Code` and `Diagram` write a fence that the text cannot close. The same fragment given again leaves the drawn blocks in place. The other parts of the page (the Monaco editor, the terminal, Blueprint components that keep state such as tabs, trees and popovers, icons, images) are not reachable from a fragment. Region content, timeline cards and their detail sections also accept Markdown directly, which goes through the same component.

**Desktop services.** `DesktopPluginUi` implements `IPluginUiService`, `IPluginSessionService` and `IPluginPromptService`. It is a broker: a request becomes a `PluginUiEvent` that the page reads from the `pluginUi.watch` channel, and the page answers with `pluginUi.respond` or `pluginUi.dialogAction`. Notifications raised before the page watches are kept (32 at most) and open dialogs are replayed to a page that reloads. The pane a command was started from (project, session, busy state, prompt draft) is an `AsyncLocal` scope, so `SelectedSessionId`, `DraftText` and the prompt operations address that pane. Prompts are sent by the page's own composer, so they are shown, recorded and refused like prompts the user types. `PluginUiService` (`pluginUi` RPC) lists commands, pickers and region content, starts commands (`invokeCommand` answers `started`; the outcome arrives on the watch channel because a command can wait in a dialog) and searches pickers. `ComposerStatusService` returns status items with the command an item names, and `SessionPluginEventsService` adds the cards of other plugins to the Statistics cards. The MCP status content is left out of the regions because the status line already shows it with its link to Settings.

**Desktop startup.** Source plugins are built while the start-up screen is shown. `DesktopPluginStartupFeedback` writes a status line ("Building plugin X (2 of 3)…") to `DesktopStartupStatus`, which `DesktopStartupResources` serves as `app://codealta/startup-status.json`; the start-up document has no bridge to the host and polls it. Failures are logged, raise one notification (`DesktopPlugins.DescribeStartupFailures`) and are shown per plugin by the Plugins page (`PluginsService.RuntimeState`: `running`, `failed` with the first compiler error, `unsupported`, `stopped`). `PluginBuildService.FindFirstBuildError` puts the first error line of the build output in the "Plugin build failed" diagnostic.

**Terminal services.** `TerminalPluginUi` implements the same three services with native dialogs (`PluginRequestDialog`: message, confirmation, input, text editor, selection, or the `Content` of a `PluginTerminalDialogRequest`). Plugins start before the application has a window, so `CodeAltaPluginServices` (the `alta` bridge and `TerminalPluginUi`) is created with the runtime, kept in `PluginRuntimeManager.HostServices`, and `CodeAltaApp` attaches its window later; until then `HasInteractiveUi` is false and questions have no answer. A custom request that has only HTML shows its `Message`, and returns null without one. The plugin dialog (`/plugins`) receives the runtime diagnostics, so a failed build and a plugin made for the desktop only are visible there.

**Prompt pickers.** `PluginPromptPickerContribution` (`GetPromptPickers`, `PluginUi.PromptPicker`) is declarative: a trigger character and a search handler. Each application supplies the picker. The desktop shows `PluginPromptPicker.tsx`; the terminal turns each contribution into a prompt-editor attachment (`TerminalPromptPickerAttachment`) with a search dialog. Both use the same token rule (the trigger at a word start, then the query), allow one picker per character and refuse `@`, `#` and `/`.

**Key bindings on the desktop.** The page accepts one stroke, or `Ctrl+G` followed by a second stroke, and gives the window's own commands precedence. A binding it cannot route leaves the command in the palette without a shortcut.

**Limits.** Prompt attachments (`AddAttachmentAsync`) are not implemented in either application. Terminal prompt-editor attachments and native renderers written with `PluginTui` are terminal-only by design. Prompt processors (`GetPromptProcessors`, `OnPromptSubmittingAsync`), compaction contributions and command-line contributions are used by the terminal application only. `OnToolCallAsync` and `OnToolResultAsync` see the calls of the tools that plugins contribute, not those of the built-in tools. The desktop reads region content and status items every ten seconds, when the window gets the focus, when a command or a dialog action of a plugin ends (the `refresh` event of `pluginUi.watch`) and when plugins are started, replaced or stopped (the `plugins-changed` shell notice); it reads timeline cards when a turn ends or older history is loaded. The built-in MCP and Git plugins contribute no command to the desktop: its window has its own MCP page and issue picker.

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

The generated properties set `PublishAot` to `false`: a plugin is a library that is loaded as it is, and the analysis for trimmed applications would otherwise warn about ordinary code such as `System.Text.Json` serialization. CodeAlta also writes a `.gitignore` in the plugin root that names the generated files and its lock file, so a project keeps its `.alta/plugins/<package-id>/` sources in its repository and nothing else; a `.gitignore` that is already there and was not generated is left as it is.

A host loads the plugins of two folders: the global one, and the one of the project it was started in. The plugins of another project of the catalog are listed and edited, and are not loaded.

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
10. cancels tracked plugin tasks and unloads contexts on shutdown.

A contribution method is called once per activation: what it returns is what the plugin contributes until it is stopped or built again.

Runtime status separates plugin diagnostics from conversation history. Diagnostics include config, discovery, build, load, activation, contribution, callback and unload records plus structured build summaries and unknown config entries. The store keeps 2048 diagnostics at most (`PluginRuntimeDiagnosticStore.MaximumCount`): the oldest failures of callbacks go first, so a status item that fails each time it is read does not push out what the builds said.

In the terminal application, open plugin management with `Ctrl+G Ctrl+N` or the command palette (search for `plugins` or `plugin`). The dialog shows enablement, diagnostics, properties, contributions, and source/README actions. `altatui --plugins-status` provides a headless discovery/config summary. In the desktop application, the same is in Settings > Plugins; see `doc/desktop.md`.

## Changing plugins while the application runs

`PluginRuntimeManager` keeps the options it was started with (`StartOptions`, `Roots`) and changes one source package at a time afterwards (`PluginRuntimeReload.cs`): `ReloadPackageAsync` builds and replaces, `StopPackageAsync` stops a package that is about to be removed from disk, whatever the configuration says. A host started in the folder that holds the global root has one plugin folder, the global one. The desktop application uses it for its Plugins page and for the `alta plugin` commands of its sessions; the terminal application lists the packages and changes nothing.

| Member | What it does |
| --- | --- |
| `GetPackages()` | The source packages of the plugin folders as they are on disk now, each a `PluginPackageStatus`: its state (`Running`, `Disabled`, `Failed`, `Unsupported`, `Stopped`), whether configuration lets it start, the descriptors of its active plugins, its last build in this run, `SourceChanged` and its diagnostics. |
| `BuildPackageAsync(package, force)` | Builds without loading: the plugins that run keep running, and the result says whether the source on disk compiles. |
| `ReloadPackageAsync(package, force)` | Builds, then replaces the running plugins of the package by those of the new build, or starts them. The `PluginPackageChange` of the result is `Started`, `Reloaded`, `BuildFailed`, `StartFailed`, `Disabled`, `Stopped` or `Unchanged`. |
| `RefreshPackagesAsync()` | Applies what changed on disk and in the configuration since the plugins were started: starts the packages that are new or turned on, replaces those whose source changed, stops those that were removed or turned off. A package whose last build failed is built again only when its source changed. |
| `Changed` | Raised after plugins were started, replaced or stopped, with the ids of their packages, so a host reads again what it keeps of the contributions (commands, shortcuts, pickers). |

A reload keeps the application usable while a plugin is being written:

1. The package is built first. When the build fails nothing else happens: the version that runs keeps running, and the result carries what the compiler reported.
2. The running plugins of the package are deactivated and their contributions removed. They are given ten seconds; a version that does not stop is left to end by itself, with a warning, and the collection of its load context is not waited for, because the session that asked for the change may still hold one of its tools.
3. The new assembly is read into memory with its symbols, so the build output is never locked and the next build writes over it.
4. The plugins are activated, their startup hooks run, and the diagnostics of the package are replaced by those of this change. When none starts, the result is `StartFailed` and nothing of the package runs: the previous version was stopped at step 2.

Changes run one at a time and are owned like the start, so closing the runtime waits for a change that is running. Once the build ended a change runs to its end whatever happens to its caller, and the plugins it starts do not depend on the caller's cancellation. A plugin callback that asks for a change of plugins is refused rather than left to wait for itself. A package whose plugin key is the key of a plugin that already runs is not started, with a diagnostic that names both packages. A package without a plugin class says so (it needs a public class that inherits `PluginBase` and has a public constructor without parameters).

**Build diagnostics.** `PluginBuildResult.Diagnostics` holds the messages of the compiler as `PluginBuildDiagnostic` records (severity, code, message, file relative to the package, line, column), read from the MSBuild output by `PluginBuildService.ParseDiagnostics`. `CompletedAt` and `Duration` say when the build ended and how long it took. `PluginBuildManifestStore.ComputeSourceStamp` is the stamp of the sources that `SourceChanged` compares.

**Tools of a session.** A session keeps the tools it was given while they look the same (name, description, parameters), so the tool of a plugin is bound when it is called: `PluginOrchestrationBridge` calls the tool that the plugin has at that moment, and the tool of a plugin that was stopped answers that its plugin is not running. `PluginOrchestrationBridge.CreateAgentTools(options, pluginRuntimeKeys)` returns the tools of some plugins as a run gets them, and `AgentRunTools.Set` registers tools in a running turn in place of those of the same names. `alta plugin reload` and `alta plugin create` use both, so the session that builds a plugin calls its new or changed tools in the same turn.

**Creating a package.** `SourcePluginScaffold.Create(root, packageId, displayName, description)` writes `plugin.cs` (one command that says hello) and `README.md` in a new folder of a plugin root. A package id has 1 to 64 letters, digits, dots, dashes or underscores and starts with a letter or a digit. The plugin key is the id, and the class name is made from it (`my-notes` gives `MyNotesPlugin`).

**API reference.** `PluginApiReference` describes the authoring API by reflection: the public types of `CodeAlta.Plugins.Abstractions` and `CodeAlta.Plugins.Tui`, and the `CodeAlta.Agent` types their signatures use. Each `PluginApiType` has its declaration, its members (inherited ones included), its derived types and the summaries of the XML documentation files shipped beside the application. `Find(query)` answers a type by its name, then the types whose name contains the text, then the types that have a member of that name. `Suggest(query)` answers a name that is not in the API with the types that share its last word (`PluginUiContext` gives the types named `...Context`). `alta plugin api` prints both, so an agent reads the API of the running version instead of guessing it.

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
- canvases: tabs that the plugin provides in CodeAlta Desktop (see "Canvases");
- transient session/timeline projections (current APIs still use some legacy `Session` names);
- resource roots (the hosts read the skill roots; the other kinds have no consumer yet);
- plugin-lifetime background tasks through `IPluginTaskService`.

Plugin shell commands are no-argument frontend activations. A `PluginCommandContribution` declares its name, label/description, placement (`ShellRoot`, `PromptEditor`, and/or `WorkspaceRoot`), command-palette/search metadata, visibility flags, optional shortcut, and availability rule. CodeAlta adapts each active contribution into the same internal shell command registry used by built-ins, so plugin commands can appear in help, the command palette, command bars, and shortcuts without frontend-specific registration code.

`PluginCommandContext` intentionally exposes public services such as `Ui`, `Sessions`, `Prompts`, and `Workspace`; it does not expose raw slash-command text or argument tokens. Commands that need user input should use plugin UI services, prompt/session services, or a plugin-owned dialog/workflow contribution. Frontend state is mapped to public plugin operation options before the handler runs; plugins never receive internal `ShellCommandContext`, XenoAtom `Visual` targets, or frontend view models.

Low-ceremony factories are available through `Command`, `Startup`, `Prompt`, `PluginUi`, `Resources`, and `AgentTool`.

UI-only contributions remain frontend responsibilities. Headless hosts can ignore them or expose no-op services through `IPluginUiService.HasInteractiveUi == false`.

When a plugin constructs a `XenoAtom.Terminal.UI.Controls.Dialog` directly, use `CodeAlta.Plugins.Tui.PluginDialogLayout.ApplyResponsiveSize(...)` with a deferred bounds delegate (for example, `() => PluginDialogLayout.ResolveDialogBounds(anchor)`) so the dialog keeps the same centered, responsive sizing behavior as built-in dialogs, including cases where the dialog is sized after it is attached to the app.

`PluginDialogRequest` retains neutral text, selection, button and metadata fields. For native custom content, migrate `PluginUi.CustomDialog(title, visual)` to `PluginTui.CustomDialog(title, visual)` and `PluginDialogRequest.Content` to `PluginTerminalDialogRequest.Content`. These APIs describe requests; the application presents them (`DesktopPluginUi` and `TerminalPluginUi`, above). A host without a window has the no-op service: `HasInteractiveUi` is false, `ShowDialogAsync` validates and presents nothing, and `ShowDialogForResultAsync` returns null, so completion is not proof of presentation.

## Canvases

A canvas is a tab that a plugin provides. The plugin declares it and holds its state; the tab is a view of that state. Nothing is shown until someone opens the canvas (a person, a command, an agent, or the plugin itself); an open tab comes back when CodeAlta Desktop restarts, and closing it loses nothing, because the state is the plugin's. Canvases are for CodeAlta Desktop: the terminal application lists none, and `Services.Canvases.HasInteractiveUi` is false there.

```csharp
public override IEnumerable<PluginCanvasContribution> GetCanvases()
{
    yield return new PluginCanvasContribution
    {
        Id = "release",
        Title = "Release checklist",
        Description = "Tracks the steps of a release.",
        Icon = "star",
        Scope = PluginCanvasScope.Project,
        Open = (canvas, ct) => ValueTask.FromResult(PluginCanvasView.Rendered((c, _) => ValueTask.FromResult(Render(c)), OnActionAsync) with { Status = Summary(canvas) }),
    };
}
```

**Declaration.** `PluginBase.GetCanvases()` is read once per activation, like the other contributions. A `PluginCanvasContribution` has an `Id` (1 to 64 letters, digits, `-`, `_` or `.`, unique in the plugin), a `Title`, a `Description` for people and agents, an `Icon` (the name of one of the general icons of the window, such as `star`, `briefcase` or `bot`; another name shows the icon of a plugin), a `Scope`, an optional `InputSchema` (a JSON Schema as text), the `Open` handler, an optional `Describe` handler that answers in Markdown what the canvas shows now, an optional `Closed` handler, and `Actions` that an agent can run (`PluginCanvasActionContribution`: a name, a description, a JSON Schema for the input and a handler that returns JSON).

**Scope and identity.** The scope says what the canvas is about: `Application`, `Project` or `Session`. An instance is identified by its canvas, its space, its project (project scope) or its session (session scope) and an optional key, so the same canvas opened twice with the same identity is one tab. The space is where the tab is, since every space has its own tabs; the same canvas opened in two spaces gives two instances over the same state of the plugin. Use the key for the rare case of several instances in one context (one tab for each issue, each file).

**The instance.** `Open` receives a `PluginCanvasContext` and returns a `PluginCanvasView`. The context has `InstanceId`, `CanvasId`, `SpaceId`, `ProjectId`, `SessionId`, `Key`, `Input` (what the instance was opened with; it is not kept, so a tab restored at a restart has none), `IsVisible` and `VisibilityChanged` (false while the tab is behind another one or its space is not shown), `IsOpen`, and `Closed`, a token that is cancelled when the instance closes or the plugin stops. A plugin keeps the context to push to the tab: `UpdateAsync(html)` sends a new fragment, `SetTitleAsync` and `SetStatusAsync` change the title of the tab and the text beside it, and `InvalidateAsync()` has the host write the fragment again with the renderer. Pushes after the instance closed do nothing, and a push made while `Open` runs is not kept: give the first title and status in the view (`Title`, `Status`). Of several pushes to one tab the last is shown, and a tab that is hidden draws the latest one when it is shown.

**The view.** `PluginCanvasView.Html(fragment, onAction)` shows a fixed fragment; `PluginCanvasView.Rendered(renderer, onAction)` writes it with a handler, called when the instance opens and each time it is invalidated (`PluginCanvasContext.InvalidateAsync`, or `Services.Canvases.InvalidateAsync(canvasId)` for every open instance of a canvas). The fragment follows the rules of the HTML of plugin dialogs (see "Portable results with an HTML form" above and `PluginHtml`): it is sanitized and drawn with the components of the window, `data-alta-command` runs a command of the same plugin for the project and session of the tab, and `data-alta-action` calls the action handler with the name, the value and the current values of the named fields. The handler returns a `PluginCanvasActionResult` (keep the tab as it is, replace its content, or close it). An action that fails shows a notice and leaves the tab as it was; the text of a plugin failure never reaches the page. A fragment is cut at 256 KiB.

**Asking for a tab.** `Services.Canvases` is the service of the calling plugin: `OpenAsync(canvasId, options)` asks the window to open the tab, or to bring it to the front when it is open, and tells whether the request was taken (`PluginCanvasOpenResult`: the status, the instance, the space and whether that space is the one shown). Without options the project and the session are those of the operation that asks (the pane a command runs in); `PluginCanvasOpenOptions` names a space, a project, a session, a key, an input, and whether to take the focus. A request for a space that is not shown adds the tab to the tabs of that space and does not move the window; the tab is there when the space is shown. `CloseAsync(instanceId)` closes an instance and its tab, and `GetOpen()` lists the open instances of the plugin.

**Lifetime.** An instance exists from the first time a tab shows it, and stays while the tab is in the window or its space is not shown (the tab is taken out of the page then, and the plugin sees `IsVisible` false). Closing the tab closes the instance: `Closed` is cancelled, then the `Closed` handler runs. The window keeps at most 64 instances and closes the oldest hidden one beyond that. When a plugin is built again, each open instance is opened again by the new version and the tab shows the result; a build that fails leaves the version that runs and its tabs alone. A plugin that stops, or a new version without the canvas, leaves its tabs waiting: each says so and offers to build the plugin again, to open its source and to close, and comes back by itself when the plugin runs again. A canvas of a project plugin is listed everywhere the plugin is loaded.

**For agents.** The actions of a canvas and its `Describe` handler work whether or not a tab is open, since the state is the plugin's. The window keeps no state that the plugin does not have, so a restored tab asks the plugin for it again.

The `canvas-checklist` sample of the `codealta-plugin-runtime` skill is a complete canvas plugin: a checklist of the application, of a project and of a session, ticked from the page, a command or an agent.

## Prompt and instruction processing

`PluginBase.GetPromptProcessors()` is for user prompt text and attachment preparation before a turn is submitted. It must not be used to mutate built-in system/developer instructions.

Plugins that need to inspect, redact, or replace final system/developer instructions can override `PluginBase.GetInstructionProcessors()` and return `PluginInstructionProcessorContribution` records. The host runs these trusted in-process hooks after built-in prompt templates, runtime/project/tool/skill context, plugin prompt parts, and before-agent-run prompt additions are composed, and before provider submission, prompt hashing/statistics, and system-prompt journal/manifest events. Processors run in deterministic contribution order and receive a `PluginInstructionProcessingContext` with provider/model/session/project metadata, active tool names, audit-oriented part metadata, prior transformation records, and a final `PluginInstructionSnapshot` for the current system/developer channels.

Instruction processors return `PluginInstructionProcessingResult.Continue`, `Replace(...)`, or `Cancel(...)`. Replacement results can replace one channel while preserving the other, and should include only audit-safe `ChangeSummary`/`Metadata`; the host records plugin identity, order, changed channels, disposition, and post-transform hash in the prompt manifest without logging pre-filter secret-bearing text. This is an extensibility/audit mechanism for trusted local plugin code, not a security boundary.

The built-in `codealta-plugin-runtime` skill includes an `instruction-path-normalizer` sample that uses this hook to replace backslashes with forward slashes inside the generated `# Runtime Context` section. Copy `samples/instruction-path-normalizer/` into a plugin root to experiment with final instruction transforms.

## Prompt-editor attachments

Plugins can implement `PluginBase.GetPromptEditorContributions()` to attach plugin-owned behavior to prompt editors. The neutral `IPluginPromptEditorHost` exposes `Text`, `CaretIndex`, `ProjectPath`, editor-state/accepted events and focus. Native anchors now belong to `CodeAlta.Plugins.Tui.IPluginTerminalPromptEditorHost.Visual`; plugins own trigger detection, popup/dialog/control choices, insertion behavior and presentation.

For terminal attachments, use `PluginTui.PromptEditor(name, attach, placeholderText, order)`. It returns the existing neutral contribution and defers the callback until `Attach` receives an `IPluginTerminalPromptEditorHost`. Other hosts receive null without invoking the callback. Terminal callback null means declined attachment; exceptions propagate without fallback. The factory does not dispose returned attachments or change host attachment lifetimes. Neither `SupportsTerminalVisuals` nor `HasInteractiveUi` substitutes for this typed host check. The Git plugin uses this route; no desktop picker or prompt host is introduced by the migration.

Prompt-editor contributions can set `PluginPromptEditorContribution.PlaceholderText` to add a short segment to the ready prompt placeholder while the contribution applies. Phrase it like the built-in segments, for example `[#] to reference an issue`; CodeAlta inserts plugin segments after project-file guidance and before send/new-line/steer guidance.

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

The built-in `alta plugin` root is how a session works on plugins: `list`, `status` and `api` in every host, and `create`, `build`, `reload`, `refresh` and `open` in the desktop application. The built-in `codealta-plugin-runtime` skill tells an agent how to use them, with samples for each kind of contribution.

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

Projection output is not written to canonical conversation history. Store plugin-owned durable state through `IPluginStateStore` (`Services.State`) when a plugin needs persistence.

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

## Plugin data

`Services.State` (`IPluginStateStore`) reads, writes and deletes named JSON items in one of three scopes. Unless the host supplies its own store, the runtime gives each plugin a `PluginFileStateStore`:

| Scope | Folder |
| --- | --- |
| `PluginStateScope.User` | `~/.alta/plugin-data/<plugin key>/` |
| `PluginStateScope.Project` | `<project>/.alta/plugin-data/<plugin key>/`: the project of a project plugin, or the project the host has selected for a global plugin |
| `PluginStateScope.Session` | `~/.alta/plugin-data/<plugin key>/sessions/<session id>/`: the session the host has selected |

An item is the file `<name>.json` (a name with or without the extension is the same item). The plugin key is made a folder name (`plugin:notes` gives `plugin_notes`). A name that is not a file name is refused with `ArgumentException`, and a scope without its project or session with `InvalidOperationException`. A missing or unreadable item reads as the default value; reading creates nothing. A write goes to a temporary file that is moved over the item.

## Plugin database

`Services.Database` (`IPluginDatabase`) gives a plugin tables of its own in the SQLite database of the application (`<state root>/data/alta.sqlite3`, see `catalog-and-config.md`, "The application database"): the database of the instance the plugin runs in, so the developer instance has separate ones. Both applications and every built-in or source plugin get it; a host that has no database (a catalog-only tool, a test) gives a service whose `HasDatabase` is `false` and whose operations throw `InvalidOperationException`. `NoopPluginServices` is such a host.

| Member | Behavior |
| --- | --- |
| `TablePrefix` | The prefix of every table, index, view and trigger of the plugin, derived from its runtime key: `builtin:statistics` is `statistics_`. A source plugin gets its letters and digits (at most sixteen) followed by eight hexadecimal digits of the hash of the key, for example `notesdb1f2e3d4c_`. A prefix is lowercase, has one underscore (its last character) so that no prefix starts another one, and the same key gives the same prefix after every restart. A built-in id that the application keeps for itself (`session`, `app`, `plugin`, `alta`, `sqlite`) takes the hashed form. |
| `MigrateAsync(version, migrate)` | The host records the version of the tables of the plugin. When it is lower than `version`, `migrate(connection, fromVersion, toVersion, token)` runs once, in a write transaction, and the version is recorded in the same transaction. Write the steps as `if (fromVersion < 1) { create }`, `if (fromVersion < 2) { alter }`. The host compares the schema before and after the steps: an object that does not start with `TablePrefix` that was created, changed or dropped (a table of another plugin, one of the application) rolls the migration back with `InvalidOperationException`. A plugin is trusted code, so this is a convention checked at migration time, not a wall. A recorded version above `version` (the data was written by a newer build) is an error too. |
| `ReadAsync(read)` | Runs on a connection of its own that cannot write; it never waits for a writer, however long the writer takes. |
| `WriteAsync(write)` | One transaction (do not begin or end one inside it), in turn with every other writer of the application. Commands need no `Transaction` object. Keep a write short: the others wait for it, and the host logs a write that holds the queue for more than two seconds. A plugin that reads a long history commits every few megabytes, never once for the whole. A write that is still queued when the plugin is deactivated or the token is canceled never runs. |

```csharp
public override async ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
{
    var database = Context.Services.Database;
    if (!database.HasDatabase) return;
    var table = database.TablePrefix + "notes";
    await database.MigrateAsync(1, async (connection, from, to, token) =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE {table} (id INTEGER PRIMARY KEY, text TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(token);
    }, cancellationToken);
}
```

SQL is the interface, through `Microsoft.Data.Sqlite` (`SqliteConnection`, `SqliteCommand`), which source plugins reference like the other shared packages. **`Services.State` or `Services.Database`:** use `State` for settings and small documents that are read and written whole; use `Database` for rows that are queried, filtered or added up, and for anything that grows. The file is shared with the list of sessions and the other plugins, copied once a day and restored from the copy when it is damaged, but a plugin that cannot rebuild its data should still expect to lose what was written since the last copy in that case.

The data stays when a plugin is disabled. The host side removes it with `PluginRuntimeManager.ListPluginTablesAsync(key)` (the names for a confirmation that lists them) and `DropPluginTablesAsync(key)`, which drops every object that starts with the prefix and forgets the version; the plugin should be deactivated first. Open `ApplicationDatabase` is owned by the host: `PluginRuntimeManagerOptions.ApplicationDatabase` is borrowed, and a runtime given only `StateRoot` opens (and disposes) the database of that state root itself; `PluginActivationOptions.ApplicationDatabase` is what an activation test gives.

## Background tasks and unload

Long-running plugin work should use `Services.Tasks.Run(...)` or the `PluginBase.Tasks` shortcut instead of untracked `Task.Run`. The runtime tracks task handles, cancels them during deactivation, and can delay unload while tracked work completes.

Unload can still fail if plugin code keeps static references, host-static delegates, pinned native resources, or untracked background work alive.

## Built-in plugins

Built-ins use the same abstraction model. The Git backend is packaged as `CodeAlta.Plugin.Git` (plugin id `git`) and is enabled by default. It serves projects hosted on GitHub, GitLab, Azure DevOps and Bitbucket Cloud. Its HTTP client, authentication, repository/issue queries, agent tools and disposal have one backend owner. The TUI injects the existing `#` prompt contribution into that instance; the picker, attachment, prompt parser and terminal binding accessors belong to the TUI, not the backend assembly. A parameterless `GitPlugin` supplies no prompt presentation. Custom heads can use the constructor taking `Func<GitPlugin, IEnumerable<PluginPromptEditorContribution>>` to supply their own neutral contributions. The delegate is deferred until enumeration and receives the same backend instance; it does not transfer backend lifetime to an attachment. No desktop picker is implied.

The provider of a project is read from its git remotes by `GitRemoteUrl` (`https://`, `ssh://` and `user@host:path` forms):

| Provider | Recognized remotes | Issues | CLI tool |
| --- | --- | --- | --- |
| GitHub | `github.com` | Issues, through `api.github.com` | `gh` |
| GitLab | `gitlab.com`, hosts named `gitlab.*`, and the host of `GITLAB_HOST`/`GITLAB_URI`/`GL_HOST` | Issues of the project (nested groups included), through `https://<host>/api/v4` | `glab` |
| Azure DevOps | `dev.azure.com`, `ssh.dev.azure.com`, `<organization>.visualstudio.com`, `vs-ssh.visualstudio.com` | Work items of the project that owns the repository, through `dev.azure.com` (WIQL, then one field read) | `az` |
| Bitbucket | `bitbucket.org`, `altssh.bitbucket.org` | Issues of the repository, through `api.bitbucket.org/2.0` | none |

**Jira.** `CodeAlta.Plugin.Jira` (plugin id `jira`, enabled by default, in both applications) serves the projects that name a Jira project. It is used by a project only: `JiraSettings.Read` reads `[plugins.jira]` (`site`, `project`, and optionally `email` and `token_env`; `enabled = false` turns it off) from `<project>/.alta/config.toml`, and a project without that table costs nothing. Jira is reached through the Atlassian CLI (`acli`), run as a process with its arguments passed one by one (`JiraCli`, behind `IJiraCli` for tests):

- **The CLI** is the one `ACLI_PATH` names, the one in `~/.alta/cache/jira/acli/`, or the one on the `PATH`. When there is none it is downloaded from `https://acli.atlassian.com/<system>/latest/acli_<system>_<amd64|arm64>/acli[.exe]` (Windows, macOS and Linux, both processors) into the cache, through a temporary file that is moved into place, and made executable. Atlassian publishes no checksum: the size is the only check. The user is told by a notice when a download starts and when it ends.
- **Signing in** is the CLI's own (`JiraPlugin.EnsureReadyAsync`): `acli jira auth status` says who is signed in; an account of another site is switched to when the CLI has one for the site; without an account, an API token of the environment (`JIRA_API_TOKEN` or `ATLASSIAN_API_TOKEN`, or the variable `token_env` names, with `JIRA_EMAIL`, `ATLASSIAN_EMAIL` or `email`) is given to `acli jira auth login --token` on its standard input, never on its command line. Otherwise the tracker answers that signing in is needed, once as a notice. The command **Jira: Sign in** types `acli jira auth login --web` in a terminal of the window, where the CLI asks which site to use after the browser; an application without terminals runs it itself. What was checked is kept a minute. CodeAlta stores no token.
- **The tracker** (`JiraTracker`) lists with a JQL query of the project (`statusCategory != Done` for open, `text ~` for words, `key =` for a key or a number, `ORDER BY updated DESC`) and reads one issue with `workitem view`. The search of the CLI does not give dates: a row has none, the reading of an issue does. Descriptions and comments are Atlassian Document Format, turned into Markdown by `JiraDocument` (headings, lists, code, quotes and panels, tables, marks and links; a media file is left out).
- **Commands.** `alta jira status`, `login`, `create`, `comment`, `transition` and `assign` (see `doc/live-tool.md`); listing and reading are `alta issue`'s. A developer prompt part tells the agent of a project with Jira which project it is and which commands to use. Two commands of the window: **Jira: Sign in** and **Jira: Status**.
- **Events.** The plugin is an `IIssueEventSource`: asked what was created or updated during the last minutes (`created >= -Nm`, a relative time, since Jira reads a date in the time zone of the account), it answers the issues, each with a stamp that changes when the issue does; an updated issue is read to know when it changed, and one that was only created is not an update. The automations use it for the `jira` trigger (see `doc/desktop.md`).

The Git plugin is also an `IIssueTrackerSource`: `GitIssueLookup.GetTracker` gives the issues and the pull requests of the repository as an `IIssueTracker` (`GitHostTracker`, with one `GitHostApi` per provider), which the Issues tab of the desktop lists and reads (see `doc/desktop.md`). Any plugin can implement `IIssueTrackerSource` to add a tracker to a project; the types are in `PluginIssueTracking.cs`. The events of a Bitbucket repository are not read: its automation triggers start nothing.

A remote whose path does not spell an ordinary repository (an escaped separator, a query, a missing segment) names no repository, so nothing derived from it reaches a provider. Azure DevOps Server (on-premises) and GitLab instances under a URL prefix are not recognized.

The picker inserts links like `[#18](https://github.com/org/repo/issues/18)`, `[#18](https://gitlab.com/group/project/-/issues/18)` or `[#18](https://dev.azure.com/org/project/_workitems/edit/18)`. Each issue carries the provider's own state text and an `IsOpen` flag (GitLab's `opened` is shown as `open`; an Azure DevOps work item is closed in the `Closed`, `Done`, `Removed` and `Completed` states).

Credentials are resolved per provider host by `GitIssueLookup.ResolveCredentialAsync` and are only ever sent to the host they belong to: `GITHUB_TOKEN`/`GH_TOKEN` then `gh auth token`; `GITLAB_TOKEN`/`GITLAB_ACCESS_TOKEN` for glab's default host only, then `glab config get token --host <host>`; `AZURE_DEVOPS_EXT_PAT` (sent as Basic credentials) then `az account get-access-token` for the Azure DevOps resource. Without credentials the requests are unauthenticated.

A provider CLI is exposed as an agent tool of the same name only when it is found on the `PATH` (`GitCliLocator`; the CLIs are not started to detect them). Every tool schema accepts arguments as an array of strings and executes them through `ProcessStartInfo.ArgumentList`, not a shell command string. The `az` tool is limited to the Azure DevOps command groups (`devops`, `boards`, `repos`, `pipelines`, `artifacts`): the first argument must be one of them. On Windows a batch file is never started, because `cmd.exe` parses its arguments again; the Azure CLI's `az.cmd` is replaced by the Python it starts (`python.exe -IBm azure.cli`). When the CLI of the provider that hosts the current project is missing, this is logged and may be notified through the host UI without repeated launch spam; the CLIs of the other providers are not mentioned.

Repository detection and the issue query live in `GitIssueLookup`, which the plugin delegates to and other hosts can reuse without a plugin instance. It walks up for `.git`, prefers the `origin` remote, takes the first remote of a supported provider, never returns pull or merge requests and orders results by last update. An instance keeps the detected repository (per folder), the recent issue list (per repository) and the resolved credentials (per provider host, a missing credential included) for `CacheDuration` (60 seconds by default; zero disables the cache). The git remote reader, credential provider, GitLab host list, clock and HTTP handler are injectable. `QueryAsync` reports the first refused HTTP status beside the issues it could still gather; the per-provider REST calls are the internal `GitIssueSource` implementations.

The desktop host runs the Git plugin as a backend (the CLI agent tools), without the terminal picker; its window has its own. Its owned-host `gitIssues.search` RPC resolves the folder from a catalog project id (never from a path sent by the page), answers `no_repository` without a network request when the project has no remote of a supported provider, names the provider (`github`, `gitlab`, `azure_devops`), and returns bounded, control-character-free titles and `https` links to the repository's own host only. Failures are reported as `failed` with a generic message: exception text, paths and credentials do not cross the bridge. The service is not registered for catalog-only launches.

**Builtin factory migration:** registered `BuiltInPluginDefinition.Factory` callbacks now construct the actual activated instance. A supplied factory runs once per activation attempt, without reflection fallback after a null result or exception. Its returned concrete type must match the discovered type; validation occurs after retaining the instance inside the existing failure/disposal boundary. Dynamic source plugins retain reflection construction. Supply `PluginType` when known: the legacy type-resolution path can separately invoke a factory for metadata, so this is not a single-call guarantee across discovery and activation. All three TUI registrations specify their type explicitly. Direct factory exceptions do not acquire reflection's constructor-exception wrapper; the existing cancellation exclusion and cleanup policy remain unchanged. This route does not strengthen general activation/unload guarantees.

The desktop application has one more built-in plugin, `ui` ("UI tools", `DesktopUiPlugin` in the desktop
project): on request it gives a session the tools that see and drive the window, with the `alta ui` command
root and a line of developer instructions that says whether the session has them. It is disabled with
`[plugins.ui]` and `enabled = false`; see `doc/desktop.md`, UI tools.

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
- **Build failures:** the terminal shows concise live build progress in the console; the desktop names the plugin on its start-up screen, raises a notice and shows the errors of the compiler under the plugin in Settings > Plugins. Both write detailed diagnostics plus stdout/stderr tails to the log. In a session, `alta plugin status <id>` returns the same errors with their file, line and column.
- **Dependency load failures:** CodeAlta assemblies and shared authoring dependencies resolve from the host load context. Plugin-owned package dependencies must be copied by the SDK so the plugin load context can resolve them from the plugin output folder.
- **Broken plugin:** start with `CODEALTA_DISABLE_PLUGINS=1` (both applications), or `--no-plugins` / `--plugin-safe-mode` (terminal), then disable or edit the plugin package.
- **Unload delays:** ensure the plugin cancels tracked work and does not keep static references or unmanaged resources alive.
