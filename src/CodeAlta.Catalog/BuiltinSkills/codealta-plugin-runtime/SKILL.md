---
name: codealta-plugin-runtime
description: Use this skill when authoring, testing, enabling, disabling, or troubleshooting CodeAlta source plugins.
---

# CodeAlta plugin runtime

CodeAlta source plugins live under either the user root `~/.alta/plugins/<package-id>/plugin.cs` or a project root `<project>/.alta/plugins/<package-id>/plugin.cs`. Source plugins are trusted code: discovered plugins are enabled by default, so copying one into a plugin root allows .NET SDK, NuGet/MSBuild, and plugin initialization logic to run in the CodeAlta process unless disabled by configuration.

Generated root files are CodeAlta-owned and marker-protected: `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, and `global.json`. They select the .NET 10 SDK for native file-based builds, set file-based plugins up as `net10.0` libraries with `EnableDynamicLoading=true`, reference host CodeAlta assemblies from the running executable folder with `Private=false`, pin shared authoring package versions directly on generated shared `PackageReference` items while excluding runtime/native assets, disable central package management so `#:package Package@Version` directives work, and expose a deterministic `CodeAltaPluginTargetPath=` message for runtime output discovery.

The runtime builds enabled source packages by running `dotnet build plugin.cs` from each plugin package directory. CodeAlta does not pass forwarded MSBuild switches such as `/logger:` or `/nr:false` because current .NET 10 file-based builds treat those as project-build mode and try to parse `plugin.cs` as XML. CodeAlta lets the .NET SDK choose the file-based build output/cache location; its own manifests live under CodeAlta-owned cache state (`~/.alta/cache/plugins/build/`) and record generated-file hashes, source inputs, CodeAlta build identity, SDK selection, output assembly, target framework, and diagnostic summaries so up-to-date plugins can load with only a concise startup summary. CodeAlta does not generate replacement `.csproj` fallback projects for source plugins.

Dynamic plugins load in a collectible `AssemblyLoadContext`. CodeAlta public assemblies and shared authoring dependencies resolve from the default ALC; plugin-private managed and unmanaged dependencies resolve from the plugin output folder through `AssemblyDependencyResolver`. Unload is cooperative: the runtime removes contributions, cancels plugin lifetime work, disposes the plugin, calls `Unload()`, and reports diagnostics if references or background tasks keep the ALC alive.

Use `/plugins`, `/plugin`, or the command palette to inspect descriptors, source paths, README files, state, diagnostics, contribution summaries, source-change notifications, and enable/disable/rebuild/reload/clean actions. Interactive startup shows source-plugin build/activation progress in a transient `Terminal.Live` region using the built-in `Spinner` control while work is running and colored state icons per package; `--plugins-wait-for-enter` pauses the live region after source plugin startup finishes, shows the concise build/activation timing summary there, and continues after Enter before discarding the region. Failed source-plugin builds are still printed with source paths and full per-plugin diagnostics plus captured stdout/stderr tails are written to `~/.alta/logs/codealta.log`. Use `--no-plugins`, `--plugin-safe-mode`, or `CODEALTA_DISABLE_PLUGINS=1` when a source plugin breaks startup. Use `--plugins-status` for a headless config/discovery summary.

## Region content and terminal authoring

Use `PluginContentContribution` / `PluginUi.Content` with a required `CreateContent` callback returning portable `PluginRenderResult` Markdown or text for the existing three UI regions. Native visual authoring now lives in the optional `CodeAlta.Plugins.Tui` assembly/namespace: migrate `PluginUi.Visual` to `PluginTui.Visual` and supply a portable callback alongside the native visual/factory. Native renderer contributions use `PluginTerminalRendererContribution` / `PluginTui.Renderer` with both portable and terminal callbacks; shared `PluginRenderResult.Visual` / `FromVisual` are removed. Agent-tool renderer callbacks remain portable.

The TUI explicitly enables call-scoped `SupportsTerminalVisuals`; other callers default to portable selection, and headless/noninteractive UI bypasses remain. This capability is not a security permission or dialog/prompt-editor capability. Direct visuals precede factories. Selected native null results mean absence, and native errors never cause fallback. Portable callbacks must be nonnull; they may return null for intentional absence. Explain terminal-only actions in fallback text rather than imply an unsupported action succeeded.

Do not add a TUI executable reference or modify generated root files. Abstractions no longer exposes terminal types or references terminal packages, but builtin implementations still combine backend and terminal code. This does not establish backend-only builtin loading or desktop parity. Source samples remain trusted executable code; do not copy them into a live plugin root merely to inspect them.

## Source-plugin authoring profiles

Reusable APIs default to `PluginAuthoringProfile.Neutral`. Rich source-plugin hosts must explicitly choose Terminal using `PluginRuntimeManagerOptions.AuthoringProfile` or `CodeAltaHostOptions.PluginAuthoringProfile`; direct generation uses `PluginRootBuildFileOptions.AuthoringProfile`, and loaders/contexts expose explicit-profile overloads. Keep generation and loading profiles consistent. Both TUI startup paths select Terminal even for CLI/noninteractive usage. Interactivity and stale DLL presence do not select a profile; borrowed prestarted runtimes are not reprofiled.

Neutral omits host-injected terminal packages and optional Plugins.Tui. Terminal includes the existing five terminal authoring packages and optional assembly with `Private=false`. Generation-option reference lists are now nullable/additive, and loader shared-name lists are additive: mandatory profile identities cannot be removed, and Neutral rejects explicit terminal entries. Legacy `DefaultSharedPackageNames`/`DefaultHostSharedAssemblyNames` remain Terminal compatibility catalogs, not effective Neutral defaults.

Generated profile/policy/API stamps participate in the existing manifest hashes. Failed-generation roots are excluded from build scheduling and cached loading while successful roots continue. The normal loader checks reserved main identity before loading; Neutral also traverses reachable managed metadata, refusing terminal names or uninspectable nonplatform dependencies instead of invoking executable resolver callbacks. Terminal identities remain host-shared, never private fallback. This is compatibility admission, not a sandbox or a guarantee that arbitrary trusted source cannot request terminal packages during restore. Real metadata/CLR loading, rich source compilation, packaged contents and builtin separation require separate qualification; no desktop plugin startup or sidecar discovery is implied.

## Built-in GitHub composition

`CodeAlta.Plugin.GitHub` contains the backend; its parameterless constructor supplies no prompt UI. TUI composition injects the existing neutral prompt contribution into that same instance and owns the picker/attachment/parser/binding-accessor code. A custom head may supply `Func<GitHubPlugin, IEnumerable<PluginPromptEditorContribution>>`; enumeration invokes it with the backend, while attachment construction remains deferred. Do not duplicate backend initialization/authentication, transfer backend disposal to attachments, or infer desktop picker support from this extraction.

Registered built-in factories now create the actual activated instance. Supply a matching concrete plugin type and explicit `PluginType` where known; legacy metadata resolution may otherwise invoke the factory separately. Supplied activation factories do not retry or fall back after null/throw/cancellation. Direct factory exceptions are not wrapped like reflection constructor failures; existing runtime failure/cancellation/cleanup policy remains. Source-plugin reflection activation is unchanged. MCP/Statistics separation and real activation/native qualification remain separate work.

## Dialogs and prompt-editor anchors

Native custom requests use `PluginTui.CustomDialog` / `PluginTerminalDialogRequest.Content`, replacing `PluginUi.CustomDialog` / the removed neutral request `Content`. `PluginDialogLayout` also moved to `CodeAlta.Plugins.Tui`. Neutral requests retain text, buttons, selection and metadata. Generic dialog operations currently have only no-op services: `HasInteractiveUi` is false, non-result completion does not prove presentation, and result operations return null when unsupported. Do not invent a generic dialog backend; MCP/GitHub still use their native paths.

`IPluginPromptEditorHost` is neutral; its old `Visual` property moved to optional `IPluginTerminalPromptEditorHost`. Use `PluginTui.PromptEditor(name, attach, placeholderText, order)` to defer a terminal callback until the host implements that interface. Unsupported hosts receive null without invoking the callback; terminal null/exception does not trigger fallback. Returned attachments remain host-owned, not factory-owned. No desktop picker or lifetime guarantee is added. Do not advertise a picker from metadata alone after attachment is declined.

## Typed command shortcuts

Author `PluginCommandContribution.KeyBinding` with `new PluginKeyBinding(...)` containing one to four neutral `PluginKeyGesture` strokes. A stroke is a `PluginKey` named key or a character/`System.Text.Rune`, with explicit `PluginKeyModifiers` flags. For Ctrl+G then Ctrl+Y, use `new PluginKeyBinding(new PluginKeyGesture('G', PluginKeyModifiers.Ctrl), new PluginKeyGesture('Y', PluginKeyModifiers.Ctrl))`. Do not use the removed `DisplayText`/terminal `Gesture`/terminal `Sequence` initializer API or raw control-character constants. Unbound commands use null. Definitions validate keys/modifiers/scalars/length, reject default strokes, normalize only letters invariantly and copy their inputs.

The TUI's optional mapper preserves modifier bits and maps Ctrl letters to terminal encoding. Unsupported scalars leave the entire binding unbound without removing the command or changing its visibility. Structural conflict keys use stroke kinds/identities/modifiers/order rather than display text, and warnings do not resolve collisions. Native dispatch/timing remains unchanged; Meta is retained for matching but omitted by the existing native hint formatter. Do not claim desktop shortcut routing, IME/physical-key semantics, or cross-terminal delivery from this contract migration.

## Session-event presentation

Keep canonical projection calculations in one `GetSessionEventProjections()` handler.
Neutral `PluginDerivedSessionEvent`, `PluginDerivedSessionEventDetailSection` and
`PluginDynamicDerivedSessionEventContent` carry Markdown/details/data/notifications, not
terminal factories. Native event/detail/dynamic variants are
`PluginTerminalDerivedSessionEvent`, `PluginTerminalDerivedSessionEventDetailSection` and
`PluginTerminalDynamicDerivedSessionEventContent` in optional `CodeAlta.Plugins.Tui`.
`PluginSessionEventVisualFactory` and `PluginSessionEventVisualContext` moved there too.
Migrate old native initializers/overrides and retain useful Markdown/header fallbacks.

Native factories are borrowed and invoked later; failure does not invoke a second fallback.
Initial upsert selects dynamic native content before static; dynamic refresh uses only the
dynamic factory, so null clears a previous native factory. Payload remains opaque in-process
data, not an RPC schema. Do not claim desktop transient projections, backend-only builtin
loading or unload/resource-lifetime safety from this contract extraction.

## Samples

Copy one of the `samples/*` folders to `~/.alta/plugins/<sample-name>/` or `<project>/.alta/plugins/<sample-name>/`; it will be discovered, built, and loaded on the next startup unless disabled in TOML:

```toml
[plugins.hello-command]
enabled = false
```

The sample folders are intentionally small and are used by integration tests as real plugin inputs rather than unverified snippets.
