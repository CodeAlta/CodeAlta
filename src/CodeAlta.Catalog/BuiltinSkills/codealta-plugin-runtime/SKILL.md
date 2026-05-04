---
name: codealta-plugin-runtime
description: Use this skill when authoring, testing, enabling, disabling, or troubleshooting CodeAlta source plugins.
---

# CodeAlta plugin runtime

CodeAlta source plugins live under either the user root `~/.alta/plugins/<package-id>/plugin.cs` or a project root `<project>/.alta/plugins/<package-id>/plugin.cs`. Source plugins are trusted code: discovered plugins are enabled by default, so copying one into a plugin root allows .NET SDK, NuGet/MSBuild, and plugin initialization logic to run in the CodeAlta process unless disabled by configuration.

Generated root files are CodeAlta-owned and marker-protected: `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, and `global.json`. They select the .NET 10 SDK for native file-based builds, set file-based plugins up as `net10.0` libraries with `EnableDynamicLoading=true`, reference host CodeAlta assemblies from the running executable folder with `Private=false`, pin shared authoring package versions directly on generated shared `PackageReference` items, disable central package management so `#:package Package@Version` directives work, and expose a deterministic `CodeAltaPluginTargetPath=` message for runtime output discovery.

The runtime builds enabled source packages by running `dotnet build plugin.cs` from each plugin package directory. CodeAlta does not pass forwarded MSBuild switches such as `/logger:` or `/nr:false` because current .NET 10 file-based builds treat those as project-build mode and try to parse `plugin.cs` as XML. CodeAlta lets the .NET SDK choose the file-based build output/cache location; its own manifests live under CodeAlta-owned cache state (`~/.alta/cache/plugins/build/`) and record generated-file hashes, source inputs, CodeAlta build identity, SDK selection, output assembly, target framework, and diagnostic summaries so up-to-date plugins can load with only a concise startup summary. CodeAlta does not generate replacement `.csproj` fallback projects for source plugins.

Dynamic plugins load in a collectible `AssemblyLoadContext`. CodeAlta public assemblies and shared authoring dependencies resolve from the default ALC; plugin-private managed and unmanaged dependencies resolve from the plugin output folder through `AssemblyDependencyResolver`. Unload is cooperative: the runtime removes contributions, cancels plugin lifetime work, disposes the plugin, calls `Unload()`, and reports diagnostics if references or background tasks keep the ALC alive.

Use `/plugins`, `/plugin`, or the command palette to inspect descriptors, source paths, README files, state, diagnostics, contribution summaries, source-change notifications, and enable/disable/rebuild/reload/clean actions. Interactive startup shows source-plugin builds in a transient `Terminal.Live` region using the built-in `Spinner` control and discards it before command output or fullscreen TUI startup; `--plugins-wait-for-enter` pauses the live region after source plugin builds finish until Enter is pressed. Failed source-plugin builds are still printed with source paths and full per-plugin diagnostics plus captured stdout/stderr tails are written to `~/.alta/logs/codealta.log`. Use `--no-plugins`, `--plugin-safe-mode`, or `CODEALTA_DISABLE_PLUGINS=1` when a source plugin breaks startup. Use `--plugins-status` for a headless config/discovery summary.

## Samples

Copy one of the `samples/*` folders to `~/.alta/plugins/<sample-name>/` or `<project>/.alta/plugins/<sample-name>/`; it will be discovered, built, and loaded on the next startup unless disabled in TOML:

```toml
[plugins.hello-command]
enabled = false
```

The sample folders are intentionally small and are used by integration tests as real plugin inputs rather than unverified snippets.
