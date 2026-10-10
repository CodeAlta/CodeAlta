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

**HTML fragments are data, not code.** The page sanitizes a fragment with DOMPurify (`frontend/src/pluginHtmlSanitizer.ts`): an allow-list of text, structure and form elements, a per-element allow-list of attributes, `alta-*` classes only, and `http(s)` links that are shown and never followed. Scripts, styles, event handlers, images, frames and forms are removed. Buttons, inputs, tables, tags and callouts then get Blueprint classes. A fragment reaches the application only through attributes that `PluginHtml.tsx` handles by event delegation: `data-alta-command` runs a plugin command by name (the fragment's own plugin first), `data-alta-action` with `data-alta-value` raises a dialog action, and named fields are collected into `Values`. `PluginHtml` in the abstractions has the attribute and class names (rows and stacks, `alta-grow` for the element that takes the free width, `alta-field` for a label above its field, `alta-card`, tags, callouts, tones) and the `Encode`, `CommandButton` and `ActionButton` helpers. Nothing a fragment contains runs, whatever it is written as: a `<script>` or an `on...=` attribute in a string is removed, so text that comes from outside never becomes script. A plugin that wants JavaScript gives it next to the fragment, as a module the application serves from the plugin (see "Plugin script"): the page runs the application's own scripts and the modules of the plugins that are loaded, and what a plain fragment does goes through its plugin's C# handlers.

**What a fragment uses of the window.** An element of class `alta-markdown` (`PluginHtml.MarkdownClass`) holds Markdown as its text. `PluginHtml.tsx` takes that text out of the sanitized fragment (`pluginMarkdownSource` removes the indentation its lines share and the blank lines around it) and draws the element with `MarkdownContent`, the component of the timeline, through a React portal. A fragment therefore has what that component has, without a script of its own: the `markdown-it` renderer behind its own sanitizer, fenced code colored by highlight.js, and `mermaid` fences drawn as diagrams in the colors of the window. `PluginHtml.Markdown`, `PluginHtml.Code(code, language)` and `PluginHtml.Diagram(mermaid)` write such an element with the text encoded; `Code` and `Diagram` write a fence that the text cannot close. The same fragment given again leaves the drawn blocks in place. The other parts of the page (the Monaco editor, the terminal, Blueprint components that keep state such as tabs, trees and popovers, icons, images) are not reachable from a fragment. Region content, timeline cards and their detail sections also accept Markdown directly, which goes through the same component.

**Desktop services.** `DesktopPluginUi` implements `IPluginUiService`, `IPluginSessionService` and `IPluginPromptService`. It is a broker: a request becomes a `PluginUiEvent` that the page reads from the `pluginUi.watch` channel, and the page answers with `pluginUi.respond` or `pluginUi.dialogAction`. Notifications raised before the page watches are kept (32 at most) and open dialogs are replayed to a page that reloads. The pane a command was started from (project, session, busy state, prompt draft) is an `AsyncLocal` scope, so `SelectedSessionId`, `DraftText` and the prompt operations address that pane. Prompts are sent by the page's own composer, so they are shown, recorded and refused like prompts the user types. `PluginUiService` (`pluginUi` RPC) lists commands, pickers and region content, starts commands (`invokeCommand` answers `started`; the outcome arrives on the watch channel because a command can wait in a dialog) and searches pickers. `ComposerStatusService` returns status items with the command an item names, and `SessionPluginEventsService` adds the cards of other plugins to the Statistics cards. The MCP status content is left out of the regions because the status line already shows it with its link to Settings.

**Desktop startup.** Source plugins are built while the start-up screen is shown. `DesktopPluginStartupFeedback` writes a status line ("Building plugin X (2 of 3)…") to `DesktopStartupStatus`, which `DesktopStartupResources` serves as `app://codealta/startup-status.json`; the start-up document has no bridge to the host and polls it. Failures are logged, raise one notification (`DesktopPlugins.DescribeStartupFailures`) and are shown per plugin by the Plugins page (`PluginsService.RuntimeState`: `running`, `failed` with the first compiler error, `unsupported`, `stopped`). `PluginBuildService.FindFirstBuildError` puts the first error line of the build output in the "Plugin build failed" diagnostic.

**Terminal services.** `TerminalPluginUi` implements the same three services with native dialogs (`PluginRequestDialog`: message, confirmation, input, text editor, selection, or the `Content` of a `PluginTerminalDialogRequest`). Plugins start before the application has a window, so `CodeAltaPluginServices` (the `alta` bridge and `TerminalPluginUi`) is created with the runtime, kept in `PluginRuntimeManager.HostServices`, and `CodeAltaApp` attaches its window later; until then `HasInteractiveUi` is false and questions have no answer. A custom request that has only HTML shows its `Message`, and returns null without one. The plugin dialog (`/plugins`) receives the runtime diagnostics, so a failed build and a plugin made for the desktop only are visible there.

**Prompt pickers.** `PluginPromptPickerContribution` (`GetPromptPickers`, `PluginUi.PromptPicker`) is declarative: a trigger character and a search handler. Each application supplies the picker. The desktop shows `PluginPromptPicker.tsx`; the terminal turns each contribution into a prompt-editor attachment (`TerminalPromptPickerAttachment`) with a search dialog. Both use the same token rule (the trigger at a word start, then the query), allow one picker per character and refuse `@`, `#` and `/`.

**Buttons on the desktop.** `PluginButtonContribution` (`PluginUi.Button`) is desktop-only: the window lists it with `pluginUi.buttons` (a place or none, the space, project and session of the context) and the terminal ignores it. The adapter reads each button's state with `PluginContributionAdapterService.GetButtonEntries`, a callback that throws leaves the default state and one diagnostic. `Services.Ui.InvalidateButtons()` (`IPluginUiService`, a default member that does nothing in other hosts) sends a `buttons` event on `pluginUi.watch`. `pluginUi.invokeCommand` takes the space the window shows and `DesktopPluginScope.SpaceId` keeps it for the command (`IPluginWorkspaceService.SelectedSpaceId`). A file icon is read by `PluginIcons` and rebuilt as an SVG that only has shapes. See "Buttons" below.

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
- buttons that the plugin puts in the window of CodeAlta Desktop (see "Buttons"), returned with the other UI contributions;
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

**Declaration.** `PluginBase.GetCanvases()` is read once per activation, like the other contributions. A `PluginCanvasContribution` has an `Id` (1 to 64 letters, digits, `-`, `_` or `.`, unique in the plugin), a `Title`, a `Description` for people and agents, an `Icon` (what the icon of a button is, see "Icons" under "Buttons": the name of any icon of the Lucide library such as `star` or `list-checks`, the name of a brand logo, or an SVG file of the plugin package such as `icons/board.svg`; without one, or when it is not found, the tab has a neutral icon), a `Scope`, an optional `InputSchema` (a JSON Schema as text), the `Open` handler, an optional `Describe` handler that answers in Markdown what the canvas shows now, an optional `Closed` handler, and `Actions` that an agent can run (`PluginCanvasActionContribution`: a name, a description, a JSON Schema for the input and a handler that returns JSON).

**Scope and identity.** The scope says what the canvas is about: `Application`, `Project` or `Session`. An instance is identified by its canvas, its space, its project (project scope) or its session (session scope) and an optional key, so the same canvas opened twice with the same identity is one tab. The space is where the tab is, since every space has its own tabs; the same canvas opened in two spaces gives two instances over the same state of the plugin. Use the key for the rare case of several instances in one context (one tab for each issue, each file).

**The instance.** `Open` receives a `PluginCanvasContext` and returns a `PluginCanvasView`. The context has `InstanceId`, `CanvasId`, `SpaceId`, `ProjectId`, `SessionId`, `Key`, `Input` (what the instance was opened with; it is not kept, so a tab restored at a restart has none), `IsVisible` and `VisibilityChanged` (false while the tab is behind another one or its space is not shown), `IsOpen`, and `Closed`, a token that is cancelled when the instance closes or the plugin stops. A plugin keeps the context to push to the tab: `UpdateAsync(html)` sends a new fragment, `SetTitleAsync` and `SetStatusAsync` change the title of the tab and the text beside it, and `InvalidateAsync()` has the host write the fragment again with the renderer. Pushes after the instance closed do nothing, and a push made while `Open` runs is not kept: give the first title and status in the view (`Title`, `Status`). Of several pushes to one tab the last is shown, and a tab that is hidden draws the latest one when it is shown.

**The view.** `PluginCanvasView.Html(fragment, onAction)` shows a fixed fragment; `PluginCanvasView.Rendered(renderer, onAction)` writes it with a handler, called when the instance opens and each time it is invalidated (`PluginCanvasContext.InvalidateAsync`, or `Services.Canvases.InvalidateAsync(canvasId)` for every open instance of a canvas). The fragment follows the rules of the HTML of plugin dialogs (see "Portable results with an HTML form" above and `PluginHtml`): it is sanitized and drawn with the components of the window, `data-alta-command` runs a command of the same plugin for the project and session of the tab, and `data-alta-action` calls the action handler with the name, the value and the current values of the named fields. The handler returns a `PluginCanvasActionResult` (keep the tab as it is, replace its content, or close it). An action that fails shows a notice and leaves the tab as it was; the text of a plugin failure never reaches the page. A fragment is cut at 256 KiB.

**Asking for a tab.** `Services.Canvases` is the service of the calling plugin: `OpenAsync(canvasId, options)` asks the window to open the tab, or to bring it to the front when it is open, and tells whether the request was taken (`PluginCanvasOpenResult`: the status, the instance, the space and whether that space is the one shown). Without options the project and the session are those of the operation that asks (the pane a command runs in); `PluginCanvasOpenOptions` names a space, a project, a session, a key, an input, and whether to take the focus. A session canvas takes its session and its project from the same place: a session that the options name gets the project of the pane only when the pane is about that session, and the window gives the tab the project of the session otherwise. The session alone names the instance, so both mean the same one. A request for a space that is not shown adds the tab to the tabs of that space and does not move the window; the tab is there when the space is shown. `CloseAsync(instanceId)` closes an instance and its tab, and `GetOpen()` lists the open instances of the plugin.

**Where a canvas is found.** Nothing is shown until someone opens it, and the author writes none of what lists it: the Canvases page of CodeAlta Desktop has a card for each declared canvas, the search and the palette have a command "Open canvas: <title>" for each, the menu of a project row lists the canvases of scope Project and the menu of a session row those of scope Session, Settings > Plugins names them under their plugin, and agents use `alta canvas list`, `show`, `open`, `focus`, `close` and `invoke` (see `doc/live-tool.md`). They follow the plugins as they are built, started and stopped.

**Lifetime.** An instance exists from the first time a tab shows it, and stays while the tab is in the window or its space is not shown (the tab is taken out of the page then, and the plugin sees `IsVisible` false). Closing the tab closes the instance: `Closed` is cancelled, then the `Closed` handler runs. The window keeps at most 64 instances and closes the oldest hidden one beyond that: its plugin is told as for a tab that was closed (`Closed` is cancelled, then the `Closed` handler runs), its tab stays, and the tab opens the instance again when it is shown. When a plugin is built again, each open instance is opened again by the new version and the tab shows the result; a build that fails leaves the version that runs and its tabs alone. A plugin that stops, or a new version without the canvas, leaves its tabs waiting: each says so and offers to build the plugin again, to open its source and to close, and comes back by itself when the plugin runs again. A canvas of a project plugin is listed everywhere the plugin is loaded.

**For agents.** The actions of a canvas and its `Describe` handler work whether or not a tab is open, since the state is the plugin's. The window keeps no state that the plugin does not have, so a restored tab asks the plugin for it again.

The `canvas-checklist` sample of the `codealta-plugin-runtime` skill is a complete canvas plugin: a checklist of the application, of a project and of a session, ticked from the page, a command or an agent. `canvas-board` is a canvas drawn by a script (see "Plugin script").

## Plugin script

A plugin can give the desktop window a JavaScript module next to some HTML, and the window runs it. A canvas drawn by script is a board, a dashboard or a viewer that uses the same components as the window itself. The module is the plugin's: CodeAlta Desktop only; the terminal application ignores it.

**Where a script goes.** A script is a `PluginScript`, given beside the HTML and never inside it:

| Where | Member |
| --- | --- |
| The tab of a canvas | `PluginCanvasView.Script`, or `PluginCanvasView.ScriptSource` for a module given as text |
| A dialog | `PluginDialogRequest.Script` |
| What a plugin shows around the prompt | `PluginRenderResult.Script` |
| A card of the timeline | `PluginDerivedSessionEvent.Script`; the module starts when the card is first on the screen, since a session can have hundreds |

`PluginScript.App("statistics")` names a module of the application's own build, for a built-in plugin whose interface is written in the frontend of CodeAlta; the host refuses it for any other plugin (see "Application modules" in the development guide). `PluginScript.File("ui/board.js")` names a module of the package folder of a source plugin (a path with `/`, ending in `.js` or `.mjs`); `PluginScript.Inline(code)` (or `PluginHtml.Script(code)`) gives the module as text, so a plugin of one file stays one file, and `.WithModule("helper.js", code)` adds a module that the first one imports as `./helper.js`. A built-in plugin has no package folder: it names a module of the application's build with `App`, or gives a small script as text. The HTML given as a string stays sanitized, and a `<script>` in it stays removed.

**The module.** It has one of two forms, and the window tells them apart:

```js
// ui/board.js: a file of the package folder. No build step.
import { Button, Tab, Tabs } from "@blueprintjs/core";
import { Chart, html, useAlta, useVisible } from "codealta";

export default function Board() {            // 1. a React component, drawn by the window in its own tree
    const alta = useAlta();
    return html`<${Button} onClick=${() => alta.host.notify("Hello")}>Hello<//>`;
}

export async function mount(root, alta) {     // 2. or a function that fills the element that holds the fragment
    root.querySelector(".alta-board").append("drawn by script");
    return () => { /* called when the content goes away */ };
}
```

A default export that is a component takes the place of the fragment, inside an error boundary; a module with both forms is a component. `mount(root, alta)` gets the element that holds the sanitized fragment, which is the skeleton to fill; what it returns, if it is a function, is called when the content goes away, and the element is then put back to the fragment, so a script can be mounted again. When the plugin writes another fragment (`UpdateAsync`, a renderer that is invalidated), what `mount` made goes with the old one: the script is ended (`alta.closed` aborts, its function is called) and `mount` is called again on the new fragment, with another `alta` object; what the run that ended set of its tab (`alta.host.setTitle`, `setStatus`, `setBadge`) ends with it, so each run sets what it wants the tab to show. A component is not started again, since it draws in the place of the fragment, and what it set stays. The window runs React in strict mode: effects run twice for a new component, so they must be able to run again.

**Addresses and limits.** The application serves a module at `app://codealta/plugin/<plugin key>/<stamp>/<path>`, from its own origin, so the content security policy of the page (`script-src 'self'`, no inline script, no `eval`) is unchanged. The stamp changes when the plugin is reloaded or a file of the package changes: a new address is a new module that the window mounts, and the old one is let go (a browser never unloads a module; what matters is that it no longer runs, which `alta.closed` and the function `mount` returned ensure). Only files with a web extension are served (`.js`, `.mjs`, `.css`, `.json`, `.svg`, `.png`, `.jpg`, `.gif`, `.webp`, `.woff`, `.woff2`, `.txt`), at most 8 MiB, only below the package folder and never through a link. Modules given as text are at most 1 MiB each, 32 for a script, and a few versions of them are kept for each plugin. A plugin of a package folder can import its own files (`import "./helper.js"`) and keep a library as one module file (`vendor/d3.js`); only React must not come twice.

**The libraries the application lends.** A module imports these by their usual names, through an import map in the entry document of the page, and gets **the instances the application runs**, not copies: one React (two copies break hooks and contexts), and the Blueprint, FlexLayout and icons of the window, with its theme, color scheme, zoom and language. A menu or a popover of a plugin opens in the layer of the window, above the tabs, and is not cut at the edge of the canvas.

| Name | What it is | Version of this release |
| --- | --- | --- |
| `react`, `react/jsx-runtime` | React | 19.2.8 |
| `react-dom`, `react-dom/client` | `createPortal`, `flushSync`; `createRoot` for a `mount` that draws with React | 19.2.8 |
| `@blueprintjs/core`, `@blueprintjs/table` | The components of the window, and its tables (use `PopoverNext`: Blueprint warns about `Popover` under React 19) | 6.21.0, 6.3.0 |
| `flexlayout-react` | A dock of its own inside a tab. The dock of the window is not lent: open another canvas, a file or a session with `alta.host` | 0.11.0 |
| `lucide-react` | The whole icon library, loaded when a script first imports it | 0.511.0 |
| `codealta` | What the application wrote for itself, below | interface 1 |

The price is the versions: what is lent is the real library at the version CodeAlta ships, so a script that uses what a new major version changes has to be updated (`alta.versions` and the release notes say the versions). There are two tiers. The HTML vocabulary (`alta-*` classes, `alta-markdown`, `alta-chart`) and the `codealta` module are owned by CodeAlta and do not move with a library; a script that wants no surprise stays there. A script that wants everything takes the libraries and follows their versions. What is not lent is the inside of the application: its state, its RPC services and its own dock, which change without notice.

**The `alta` object** is the one argument of `mount` and what `useAlta()` returns, so a script touches no global:

| Member | Content |
| --- | --- |
| `alta.context` | `pluginKey`, `canvasId`, `instanceId`, `spaceId`, `projectId`, `sessionId`, `key` and `input` (the JSON the canvas was opened with) |
| `alta.visible` | `value` and `subscribe(listener)`: whether the content is shown; a script pauses its timers and reads while it is not |
| `alta.closed` | An `AbortSignal` that aborts when the content goes away: the tab closed, the plugin reloaded |
| `alta.host` | `openFile(path, { line })`, `openDiff()`, `openSession(id)`, `openCanvas(id, options)`, `openLink(url)`, `notify(message, { tone })`, `runCommand(name)`, `setTitle`, `setStatus`, `setBadge` (the last three are for the tab; a badge takes the place of the status while it is set). A request the window cannot serve does nothing |
| `alta.theme` | `value` and `subscribe`: the colors of the window as values (`text`, `muted`, `grid`, `surface`, `series`, `ramp`…), for what a script paints itself. `value` is the theme of now, whether or not the script listens |
| `alta.html(text)` | Cleans a string of HTML as fragments are cleaned; assign the result to `innerHTML`. Insert a string with this, never with `innerHTML` alone |
| `alta.rpc` | The calls, the streams and the events of the plugin's own C# handlers (see "Talking to the plugin"). Only the script of a canvas has it; elsewhere every call rejects with `rpc_unavailable` |
| `alta.versions` | The version of this interface and of the lent libraries |

**The `codealta` module** adds what no library has:

| Export | What it is |
| --- | --- |
| `html` | JSX without a build step: ``html`<${Button} intent="primary">Save<//>` `` (the syntax of `htm`; `class` and `for` on a page element are read as `className` and `htmlFor`) |
| `useAlta`, `useVisible`, `useTheme` | The `alta` object, and the two signals of it as hooks |
| `useRpc`, `useStream` | The calls and streams of the plugin as hooks (see "Talking to the plugin") |
| `Markdown`, `Code`, `Diagram` | The Markdown of the timeline, colored code, Mermaid diagrams in the colors of the window |
| `Icon`, `BrandIcon` | An icon of the window by its name, the logo of a provider or a service |
| `FileLink`, `SessionLink` | A link that opens a file in the code editor at a line, or a session |
| `Chart`, `Sparkline`, `StatTile`, `CalendarHeatmap`, `WeekdayHourHeatmap`, `histogram`, `histogramOption`, `boxPlotOption`, `boxStats`, `quantile` | The charts of the window (ECharts loads when the first one is drawn) |

**Charts in plain HTML.** A fragment, with or without script, can hold `PluginHtml.Chart(optionJson, label)`: a `div` of class `alta-chart` whose `data-option` is the JSON of the chart and whose `data-label` says what it shows. The window draws it with its colors and gives it a table view. An option that is not data (a function, a link, a toolbox, a formatter that is not a template of `{b}` and `{c}`) or not JSON shows a short message and nothing else, and so does an option that is data but that ECharts cannot draw (a kind of axis or of component it does not have): the rest of the tab is drawn, and the message has what ECharts said as its tooltip.

**What happens around a script.** A tab that is hidden stays mounted and is told (`alta.visible`); a tab keeps the script it drew until it is shown, so a plugin that reloads while its tab is hidden is not started on a skeleton the tab has not drawn. A script that fails to load, is neither form, or throws shows its error in its content with a button that copies it, and the window around it goes on. A script the host cannot serve (a file that is missing or too large) says so in the same place. The safe mode that starts CodeAlta without plugins starts it without their scripts. A script runs in the document of the application, so it can do what the page can; a plugin is trusted code that runs in the process anyway, and the policy of the page and the sanitizer keep the property that matters: text from outside never becomes script.

### Talking to the plugin

The script of a canvas reaches the C# of its own plugin with `alta.rpc`. It is the same NeoAstra RPC client that the window uses for the host (request ids, cancellation, timeouts, typed errors, streams read one item at a time), running on a connection that the window carries for it: a plugin writes handlers and a script calls them by name, and neither has to know how the frames travel.

The plugin registers its handlers in the `Open` handler of the canvas, before it returns the view. Each open instance has its own registry, so a handler keeps what it needs of the instance in the closure it was written with:

```csharp
Open = (canvas, _) =>
{
    canvas.Rpc.Handle<GetBoard, Board>("board.get", (request, ct) => ValueTask.FromResult(_board.Read(request.Project)));
    canvas.Rpc.Handle<MoveCard>("board.move", async (request, ct) => await _board.MoveAsync(request, ct));      // no result
    canvas.Rpc.Stream<WatchBoard, BoardChange>("board.watch", (request, ct) => _board.WatchAsync(request, ct)); // IAsyncEnumerable<BoardChange>
    return ValueTask.FromResult(PluginCanvasView.Html("<div id=\"board\"></div>") with { Script = PluginScript.File("ui/board.js") });
}

// Later, from anywhere in the plugin:
await canvas.Rpc.PublishAsync("board.changed", new BoardChanged(revision));                                    // an event
```

```js
const board = await alta.rpc.invoke("board.get", { project: alta.context.projectId });        // or useRpc("board.get", input)
for await (const change of await alta.rpc.stream("board.watch", {}, { signal: alta.closed })) draw(change);   // or useStream("board.watch")
const stop = await alta.rpc.subscribe("board.changed", change => refresh());                  // stop() ends it
```

| C# member of `canvas.Rpc` | What the script does |
| --- | --- |
| `Handle<TRequest, TResult>(name, handler)` | `await alta.rpc.invoke(name, input, { signal })` gives the result |
| `Handle<TRequest>(name, handler)` | The same, and the result is `null` |
| `Stream<TRequest, TItem>(name, handler)` | `await alta.rpc.stream(name, input, { signal })` gives an async iterable. The window takes the next item of the `IAsyncEnumerable` only when the script has taken the ones before, so a slow reader slows the iterator down and never fills a queue. Stopping the loop, or aborting the signal, cancels the token of the iterator |
| `PublishAsync<T>(name, value)` | `await alta.rpc.subscribe(name, handler)` listens and gives the function that stops it. An event is not kept: a script that is not subscribed yet does not get it, so it reads the state with a call after it subscribed |
| `JsonOptions` | The `JsonSerializerOptions` that read the requests and write the results, the items and the events: the web defaults (camelCase). A plugin with a source-generated context sets `new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = MyContext.Default }` in `Open` |

Names are 1 to 64 of `a-z`, `0-9`, `.`, `-` and `_`, starting with a letter or a digit (`PluginRpc.IsValidName`); registering one twice, or after `Open` has returned, throws, and a canvas registers at most 256. A request is any JSON value that its type reads; a call without input reads as `{}`, so a record whose members are optional needs no argument. Each handler gets a token that is cancelled when the script cancels the call, when the call times out, when the connection ends, and when the instance closes or the plugin stops or is reloaded.

**Errors.** A call that fails rejects with an `AltaError`, which has a stable `code`, a message and `retryable`. What a handler throws never crosses to the page: it is logged by the plugin's logger and the script gets `internal_error`, unless the handler throws `PluginRpcException(code, message)` (or with `retryable: true`), whose code (1 to 64 of `a-z`, `0-9`, `_`, starting with a letter) and message (one line, at most 400 characters, no stack trace or path) are made to be shown. A stream carries no code of its own: an exception in its iterator ends it with `internal_error`, so expected failures go in an item. The window adds its own codes:

| Code | Meaning | `retryable` |
| --- | --- | --- |
| `invalid_request` | The input does not match the type of the handler | no |
| `command_not_found` | The plugin registered no handler of that name | no |
| `internal_error` | The handler threw something else than a `PluginRpcException` | no |
| `too_many_requests` | A limit below was reached | yes |
| `payload_too_large` | The request or the result is above 1 MiB (4 MiB for a result) | no |
| `timeout` | The call took more than 30 seconds | yes |
| `operation_canceled` | The script cancelled the call | no |
| `connection_closed` | The connection to the plugin ended while the call ran (the plugin was reloaded, the window reconnected), or could not be made because the window does not reach its host now; the next call connects again | yes |
| `rpc_unavailable` | The canvas has no calls (the plugin registered none and its view has no script), or the script is not the one of a canvas | no |

A call is never replayed by the window: a mutation that failed with `timeout` or `connection_closed` may have been applied, and the script decides whether to ask again.

**Limits.** A frame of at most 1 MiB, 8 calls running at once, 8 streams and 8 subscriptions open, 8 items in flight on a stream, about 200 calls a second (a burst of 400), and 30 seconds for a call. A script that keeps being refused for calling too fast loses its connection and connects again with its next call. The window sends the frames of a script together, a few milliseconds apart, so a stream of a thousand items does not cost a thousand calls.

**Lifetime.** Nothing is opened until the script calls. The first call connects; a connection that ends (the plugin was reloaded, the window reloaded, the host closed the instance) fails what is pending with a retryable `connection_closed` and the next call connects again; `alta.rpc.generation` counts those reconnections, and `useRpc` reads again when it changes. `alta.rpc.connected` (`value`, `subscribe`) says whether the connection is open now: what `subscribe` gave ends with the connection, and no call of a script that only listens would tell it, so such a script subscribes again when `connected` turns false, after a short wait. The listeners of one script share one subscription of the plugin; the `signal` given to `subscribe` ends the listening of that caller only. A tab that is hidden pauses its streams: the window holds back their acknowledgements, so the plugin's iterator stops after a few items and goes on when the tab is shown. `useRpc` reads when the component is shown (and again when the tab is shown after it was hidden), and asks again a few times, after a wait that doubles from half a second, when the connection ended; `useStream` follows the stream while the component is shown and takes it again, after a wait that grows to 8 seconds, when the connection ended. A tab closed, a space deleted, a plugin reloaded or stopped, and a page that reloads all end the connection and cancel what the handlers were doing.

**What remains.** Only the script of a canvas has `alta.rpc`: a dialog, the content around the prompt and a card of the timeline have no instance to hold a registry, so their scripts get `rpc_unavailable`; they act through `alta.host` and the commands of the plugin. A plugin cannot yet generate a typed file for its calls: a script passes and reads JSON.

The `canvas-board` sample of the `codealta-plugin-runtime` skill is a React canvas with Blueprint tabs, a menu, a chart, Markdown, a file link and `alta.host`, whose board lives in the plugin: the script reads it with a call and a stream, changes it with calls, hears an event, and `alta board add --title ...` changes it from a terminal.

## Buttons

A plugin can put its own buttons in the window of CodeAlta Desktop: an icon and a label at a named place, which runs a command of the plugin or opens a canvas of the plugin. The terminal application draws no button; the command a button names stays in its palette. Buttons are contributions returned by `GetUiContributions()`, so they are registered, scoped and ordered like status items.

```csharp
public override IEnumerable<PluginUiContribution> GetUiContributions()
{
    // One click from the top right of the title bar to a canvas: nothing else to write.
    yield return PluginUi.Button(PluginButtonPlace.TitleBar, "statistics", icon: "chart-column", label: "Statistics") with
    {
        Canvas = "statistics",
    };

    // A line in the menu of each project row, which runs a command and shows what is left to do.
    yield return PluginUi.Button(PluginButtonPlace.ProjectMenu, "release", icon: "icons/release.svg", label: "Release checklist") with
    {
        Command = "release.open",
        GetState = button => new PluginButtonState { Badge = StepsLeft(button.ProjectId) },
    };
}
```

| Place | Where | What it is asked about |
|---|---|---|
| `TitleBar` | The top right of the title bar, before the space switch, the zoom and the theme | The shown space and the selected project and session. At most 2 for a plugin |
| `Rail` | The navigation rail at the left, after Issues and before Settings | The same. At most 1 for a plugin; the rail draws two buttons, and folds the rest into a menu |
| `ProjectMenu` | A line in the menu of a project row of the Explorer | That project, whatever is selected. At most 6 |
| `SessionMenu` | A line in the menu of a session row | That session and its project. At most 6 |

**What a button does.** `PluginButtonContribution` has an `Id` (1 to 64 letters, digits, `-`, `_` or `.`, unique in the plugin; the choice of a person to hide a button is kept under it), an `Icon`, a `Label` (the tooltip, the accessible name and the text of a menu line), an `Order` among the buttons of plugins at the same place, and exactly one of `Command` and `Canvas`. `Command` names a command of the same plugin, as `PluginStatusItem.Command` does; it runs for the context of the button: the project and the session of the row for a menu, the selected ones for the title bar and the rail, and `Services.Workspace.SelectedSpaceId` is the space the window showed. `Canvas` names a canvas of the same plugin; the window opens it with the context of the button (a project canvas needs a project, a session canvas a session; without one the button is disabled), and no handler runs. A button that names neither or both, an identifier used twice, or more buttons than a place allows are left out when the plugin starts, each with a warning that `alta plugin status <id>` shows.

**State.** `GetState` returns a `PluginButtonState`: a `Badge` (a number, `PluginButtonBadge.Dot`, or `PluginButtonBadge.Busy` for a small ring while the plugin reads or computes), a `Tone` (`PluginStatusTone`, which colors the icon and the badge), `Hidden`, `Disabled` and a `Tooltip` that replaces the label. The callback is synchronous and called each time the window reads the buttons, so it follows the rule of status items: read a field, never a file or the network. The window reads them when it starts, when the context changes (a project or a session is selected, a space is shown, a menu opens), when a command of the plugin ends, and when the plugin calls `Services.Ui.InvalidateButtons()`; a burst of calls is one read. Nothing reads them on a timer.

**Icons.** The `Icon` is one of three things. The name of any icon of the Lucide library (`chart-column`, `list-checks`, `bell`): the window ships the icons it draws itself, and loads the rest, all in one file, the first time a plugin names one. The name of a brand logo (`github`, `anthropic`), looked up after the library. Or a file of the plugin package, such as `icons/statistics.svg`: the host reads it from the folder of the package, accepts only an SVG file of at most 32 KiB, rebuilds it with a short list of shapes (no script, style, link or image), and the window draws it as a mask in the color of the text, so it follows the theme. An icon that is not found draws a neutral plugin icon. Canvases use the same icons for their tabs.

**The person keeps the window.** A right click on a button offers to hide it, and Settings > Plugins lists the buttons of each plugin with a switch. The choice is a view state of the window, kept under the plugin and the button, not under a space. In a narrow window the buttons of plugins fold into one menu before anything of the application moves.

The `canvas-checklist` sample of the `codealta-plugin-runtime` skill has a button that opens its canvas, a button that runs a command with a badge, and a line in the menu of each project.

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

`Services.Database` (`IPluginDatabase`) gives a plugin tables of its own in the SQLite database of the application (`<state root>/data/alta.sqlite3`, see `catalog-and-config.md`, "The application database"): the database of the instance the plugin runs in, so the developer instance has separate ones. Both applications and every built-in or source plugin get it; a host that has no database (a catalog-only tool, a test) gives a service whose `HasDatabase` is `false` and whose operations throw `InvalidOperationException`. `NoopPluginServices` is such a host, and so is an `IPluginServices` that was written before the member existed: `Database` has a default that gives `NoopPluginDatabase.Instance`, as `Canvases` gives `NoopPluginCanvasService.Instance`.

| Member | Behavior |
| --- | --- |
| `TablePrefix` | The prefix of every table, index, view and trigger of the plugin, derived from its runtime key: `builtin:statistics` is `statistics_`. A source plugin gets its letters and digits (at most sixteen) followed by eight hexadecimal digits of the hash of the key, for example `notesdb1f2e3d4c_`. A prefix is lowercase, has one underscore (its last character) so that no prefix starts another one, and the same key gives the same prefix after every restart. A built-in id that the application keeps for itself (`session`, `app`, `plugin`, `alta`, `sqlite`) takes the hashed form. |
| `MigrateAsync(version, migrate)` | The host records the version of the tables of the plugin. When it is lower than `version`, `migrate(connection, fromVersion, toVersion, token)` runs once, in a write transaction, and the version is recorded in the same transaction. Write the steps as `if (fromVersion < 1) { create }`, `if (fromVersion < 2) { alter }`. The host compares the schema before and after the steps: an object that does not start with `TablePrefix` that was created, changed or dropped (a table of another plugin, one of the application), or an index or a trigger that is on a table that does not start with it, whatever its own name, rolls the migration back with `InvalidOperationException`. A plugin is trusted code, so this is a convention checked at migration time, not a wall. A recorded version above `version` (the data was written by a newer build) is an error too. A plugin migrates once, when it is activated; when the file of the database is replaced while it runs (it was damaged, see `catalog-and-config.md`), the host runs the last migration again before the next read or write of the plugin, so its tables exist again, empty or as the restored copy had them. The steps therefore depend on nothing but their arguments. |
| `ReadAsync(read)` | Runs on a connection of its own that cannot write; it never waits for a writer, however long the writer takes. Inside a write of the plugin it sees what is committed, not what that write has done so far, and it does not run the migration again: when the file was replaced under the write, the read fails, the write is rolled back with it, and the host migrates and runs the write once more. |
| `WriteAsync(write)` | One transaction (do not begin or end one inside it, and do not start another write or a migration inside it: it would wait for itself, so it is refused with `InvalidOperationException`), in turn with every other writer of the application. The refusal follows the flow of the write, not the `await`: work the write starts without awaiting it (a `Task.Run`, a fire-and-forget call) is refused too when it reaches the database before the write ended, so start it after the write. Commands need no `Transaction` object. Keep a write short: the others wait for it, and the host logs a write that holds the queue for more than two seconds. A plugin that reads a long history commits every few megabytes, never once for the whole. A write that is still queued when the plugin is deactivated or the token is canceled never runs. |

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

It contributes transient per-turn/session statistics projections (the cards of the timeline, without writing plugin messages into canonical session history) and the `alta statistics` command root. In CodeAlta Desktop it also **keeps the statistics of every session** in the application database (`statistics_*` tables, `doc/catalog-and-config.md`): `DesktopPlugins.StatisticsDefinition` builds it with the journals of the session store (`StatisticsPlugin.CreateForDesktop(ISessionJournalCatalog)`), and once it is activated on a host that has a database it runs a tracked job that reads the history the user chose to read, and a flow that catches a session up a second after an agent event signals it (`OnAgentEventAsync` only enqueues the session id). In CodeAlta Desktop the plugin is also a **canvas** (`statistics`, scope Application, icon `chart-column`, module `PluginScript.App("statistics")`), a **button** of the title bar (`PluginUi.Button(PluginButtonPlace.TitleBar, "statistics", ...)` whose state is a ring while the history is read and a dot while the first choice waits, and `InvalidateButtons()` is called only when that changes), a line in the menu of a project (`ProjectMenu`, which opens the canvas with the key `project:<id>`), a command `statistics` (`/statistics`) with the binding `Ctrl+G C`, and the calls of the canvas (`canvas.Rpc`, names `statistics.*`, results written by `StatisticsJson`). They exist only where the plugin reads the sessions (CodeAlta Desktop with a database): CodeAlta TUI and a disabled plugin declare none. The plugin exposes the running engine as `StatisticsPlugin.Statistics` (`IStatisticsService`: the status of the reading, its events, its controls and `StatisticsQueries`); it is null in CodeAlta TUI, which runs no job, and when the plugin is disabled, where the tables stay as they are. The store, the job, the flow, the questions and the commands are in `doc/statistics.md`; the reader and the facts in `doc/statistics-facts.md`.

The MCP plugin is packaged as `CodeAlta.Plugin.Mcp`, is enabled by default, and contributes the `alta mcp` command root plus compact dynamic developer prompt guidance for active/inactive configured MCP servers. It reads fixed MCP JSON server files (`~/.alta/mcp.json` and project `.alta/mcp.json`) and TOML policy under `[plugins.mcp]`; connection fields stay in JSON, while enablement, `disabled_tools`, prompt caps, timeouts, and direct-exposure policy stay in TOML. The plugin also provides reusable `McpManagementService`/runtime services consumed by session-activated MCP agent tools, the TUI MCP Servers dialog, and the status indicator. Dynamic MCP `AgentToolDefinition` exposure is progressive: `alta mcp activate <id>*` marks servers active for the session, and tools from active servers are registered on agent runs where policy allows them. Automatic refresh on `tool-list-changed` remains follow-up work unless direct-tool freshness requires it. See [MCP support](mcp.md).

**MCP presentation migration:** the parameterless `McpPlugin` now supplies backend contributions and portable status, without native revision state or the interactive management command. The TUI explicitly composes the existing command, status button, dialog, bindable rows and icons from `CodeAlta.Tui/Plugins/Mcp/`. Its presentation borrows the plugin's existing management and activation owners; it does not create another backend. This built-in wiring is internal, using friendship to the `altatui` assembly, not a supported public activation-state API or a backend-to-TUI project reference. Friendship is assembly-wide, not member-scoped.

Contribution enumeration does not read configuration or evaluate status content. Native decoration retains the exact portable callback and metadata, adding deferred visual rendering. Status snapshot timing, independent markup/tone refreshes and the existing revision dispatch/exception behavior are preserved; the extraction adds no unsubscribe/disposal policy. Backend commands, prompt discovery and explicit management/runtime operations are unchanged. This separation does not qualify native MCP execution, authentication, real activation/loading or desktop MCP management.

The parameterless `StatisticsPlugin` now emits neutral transient events with portable Markdown summaries/details and the existing opaque payload, without terminal UI dependencies. The TUI explicitly decorates each existing cache candidate with deferred card/detail factories. Terminal controls and ANSI summary styling live in the TUI; calculations, portable formatting, event identifiers, fingerprint/cache keys and live-tool commands remain in the backend. This internal built-in composition is not a public statistics-model API or a new renderer dispatch mechanism.

The presentation borrows callbacks over the same already-built turn. Portable summary/details/payload remain eager; native summary/table formatting and controls remain deferred until rendering. Sequential cache reuse returns the decorated event; concurrent `GetOrAdd` candidates can each decorate, so this is not an exactly-once guarantee. Decorator exceptions/cancellation propagate without fallback. This presentation change does not qualify native rendering or establish real builtin loading/activation; the desktop shows the cards through its own page (`doc/desktop.md`, "Turn statistics").

## Troubleshooting

- **Missing SDK:** source plugins require the .NET 10 SDK selected by the generated plugin-root `global.json`. If `dotnet build plugin.cs` is treated as a project build, install the required SDK or start with safe mode.
- **Build failures:** the terminal shows concise live build progress in the console; the desktop names the plugin on its start-up screen, raises a notice and shows the errors of the compiler under the plugin in Settings > Plugins. Both write detailed diagnostics plus stdout/stderr tails to the log. In a session, `alta plugin status <id>` returns the same errors with their file, line and column.
- **Dependency load failures:** CodeAlta assemblies and shared authoring dependencies resolve from the host load context. Plugin-owned package dependencies must be copied by the SDK so the plugin load context can resolve them from the plugin output folder.
- **Broken plugin:** start with `CODEALTA_DISABLE_PLUGINS=1` (both applications), or `--no-plugins` / `--plugin-safe-mode` (terminal), then disable or edit the plugin package.
- **Unload delays:** ensure the plugin cancels tracked work and does not keep static references or unmanaged resources alive.
