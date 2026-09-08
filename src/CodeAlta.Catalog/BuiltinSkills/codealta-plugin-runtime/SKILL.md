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

Generated targets conditionally reference the optional assembly with MSBuild `Exists` and `Private=false`; do not add a TUI executable reference or modify generated root files. Other abstraction contracts and builtin implementations still have terminal dependencies: this slice does not establish backend-only loading or desktop parity. Source samples remain trusted executable code; do not copy them into a live plugin root merely to inspect them.

## Typed command shortcuts

Author `PluginCommandContribution.KeyBinding` with `new PluginKeyBinding(...)` containing one to four neutral `PluginKeyGesture` strokes. A stroke is a `PluginKey` named key or a character/`System.Text.Rune`, with explicit `PluginKeyModifiers` flags. For Ctrl+G then Ctrl+Y, use `new PluginKeyBinding(new PluginKeyGesture('G', PluginKeyModifiers.Ctrl), new PluginKeyGesture('Y', PluginKeyModifiers.Ctrl))`. Do not use the removed `DisplayText`/terminal `Gesture`/terminal `Sequence` initializer API or raw control-character constants. Unbound commands use null. Definitions validate keys/modifiers/scalars/length, reject default strokes, normalize only letters invariantly and copy their inputs.

The TUI's optional mapper preserves modifier bits and maps Ctrl letters to terminal encoding. Unsupported scalars leave the entire binding unbound without removing the command or changing its visibility. Structural conflict keys use stroke kinds/identities/modifiers/order rather than display text, and warnings do not resolve collisions. Native dispatch/timing remains unchanged; Meta is retained for matching but omitted by the existing native hint formatter. Do not claim desktop shortcut routing, IME/physical-key semantics, or cross-terminal delivery from this contract migration.

## Samples

Copy one of the `samples/*` folders to `~/.alta/plugins/<sample-name>/` or `<project>/.alta/plugins/<sample-name>/`; it will be discovered, built, and loaded on the next startup unless disabled in TOML:

```toml
[plugins.hello-command]
enabled = false
```

The sample folders are intentionally small and are used by integration tests as real plugin inputs rather than unverified snippets.
