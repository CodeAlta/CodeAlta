# CodeAlta Plugin Runtime Specification

Status: **Draft**
Last updated: **2026-05-04**
Audience: implementers of the future `CodeAlta.Plugins` runtime, CodeAlta startup/configuration code, plugin management UI, plugin test infrastructure, and plugin authors who need to understand runtime behavior.

Primary inputs:

- `doc/specs/plugins_abstraction_specs.md`
- .NET SDK file-based program support, especially `dotnet build plugin.cs` from the .NET 10 `dotnet run file.cs` feature family
- `../XenoAtom/XenoAtom.MsBuildPipeLogger/doc/readme.md`
- `../dotnet-releaser/src/dotnet-releaser/ReleaserApp.Projects.cs` and `../dotnet-releaser/src/dotnet-releaser/Runners/MSBuildProgram.cs` for structured MSBuild pipe-logger target output collection patterns
- `../XenoAtom/XenoAtom.Terminal.UI/samples/ControlsDemo/Demos/ToastDemo.cs` and `../XenoAtom/XenoAtom.Terminal.UI/samples/ControlsDemo/ControlsDemoApp.cs` for toast notifications and `ToastHost` wiring
- `src/global.json` and `src/Directory.Packages.props`
- current CodeAlta skill, config, catalog, TUI, provider, and startup behavior

## 1. Goal

Define the first CodeAlta plugin runtime: discovery, enablement, build, load, activation, contribution registration, diagnostics, unload, and reload for CodeAlta plugins that use `CodeAlta.Plugins.Abstractions`.

The runtime should make the common development loop feel like scripting while still producing normal .NET assemblies:

1. a plugin author creates a folder under a plugin root;
2. the folder usually contains a single `plugin.cs` file;
3. CodeAlta generates the shared MSBuild files needed by all plugins in that root;
4. CodeAlta runs `dotnet build plugin.cs` using .NET 10 file-based program support;
5. CodeAlta loads the resulting assembly in a plugin-specific `AssemblyLoadContext`;
6. CodeAlta discovers `PluginBase` types, activates them, registers returned contributions, and owns cleanup.

The runtime must be reusable outside initial startup because the plugin management UI will need the same build/load/unload/reload operations.

## 2. Non-goals for this document

This specification does **not** define:

- a plugin marketplace, package signing, update protocol, or remote install flow;
- a sandbox or permission model;
- a stable binary plugin packaging format beyond source-folder/file-based plugins and built-in plugins;
- a full provider/model settings UI for plugin-contributed providers;
- final product copy for trust prompts or plugin marketplace pages;
- the exact implementation classes and namespaces, although candidate runtime records and services are described.

The runtime must still be compatible with future marketplace, packaging, signing, and management work.

## 3. Key v1 decisions

### 3.1 Build source plugins with .NET 10 file-based programs

Dynamic source plugins are built by invoking the .NET CLI on a source file:

```text
dotnet build plugin.cs
```

This uses the same .NET 10 file-based program infrastructure documented for `dotnet run file.cs`, but CodeAlta uses `dotnet build` because it needs the compiled plugin assembly rather than executing the file as an app.

The generated `Directory.Build.props` must force plugin source files to compile as libraries:

```xml
<OutputType>Library</OutputType>
<TargetFramework>net10.0</TargetFramework>
```

The runtime should not invoke Roslyn directly for v1. The .NET SDK owns file-based program behavior, package restore, source generators, analyzers, implicit project construction, and the grow-up path to ordinary projects.

### 3.2 Use generated root-level build files

For each plugin root, CodeAlta generates shared build files:

- `Directory.Build.props`
- `Directory.Build.targets`
- `Directory.Packages.props`
- `global.json`

These files are generated before dynamic plugin discovery/build. They provide the common plugin TFM, library output type, host assembly references, shared package versions, central-package-management settings, and SDK selection.

For project-local plugin roots, CodeAlta should generate these files only when the project plugin root already exists or when a user action creates it. Starting CodeAlta in a repository should not create `<project>/.alta/plugins/` as an incidental side effect unless plugin management/scaffolding requested it.

### 3.3 Use host-local assembly references for CodeAlta assemblies

Plugins must share CodeAlta public type identities with the host. For CodeAlta assemblies, the generated build targets use assembly references to files colocated with the running CodeAlta executable, not `PackageReference`.

Example intent:

```xml
<Reference Include="CodeAlta.Agent">
  <HintPath>$(CodeAltaExeFolder)\CodeAlta.Agent.dll</HintPath>
  <Private>false</Private>
</Reference>
```

`<Private>false</Private>` prevents host-owned assemblies from being copied into plugin outputs. The plugin `AssemblyLoadContext` resolves these assemblies from the default host context.

### 3.4 Use package references for external authoring packages

External packages used by plugin authors should be referenced as `PackageReference` items so plugins can benefit from analyzers, source generators, and build assets. For example, `XenoAtom.Terminal.UI` uses package-provided build-time assets that should flow through NuGet rather than raw assembly references.

For host-shared external packages included by CodeAlta's generated targets, use compile/build assets but exclude runtime and native assets:

```xml
<PackageReference Include="XenoAtom.Terminal.UI">
  <ExcludeAssets>runtime;native</ExcludeAssets>
</PackageReference>
```

This avoids copying another copy of host-shared UI assemblies and native runtime libraries to the plugin output while still allowing compile-time use and package build assets.

A plugin's own direct package references are different. If a plugin adds its own package through `#:package` or another future authoring path, that package should normally keep runtime assets so `EnableDynamicLoading=true` can copy private dependencies to the plugin output for the plugin ALC to load.

### 3.5 Enable dynamic loading

The generated props must set:

```xml
<EnableDynamicLoading>true</EnableDynamicLoading>
```

This is required so NuGet packages referenced directly by a plugin can copy runtime dependencies to the plugin build output. The plugin ALC then resolves those private dependencies from the output directory.

Host-shared CodeAlta assemblies and host-shared public authoring packages should still be excluded from copy-local through `<Private>false</Private>` or `<ExcludeAssets>runtime;native</ExcludeAssets>`.

### 3.6 One load context per dynamic plugin load unit

A dynamic source plugin load unit is a plugin package folder compiled to one assembly. Each load unit gets its own collectible `AssemblyLoadContext`.

A single assembly may contain multiple `PluginBase` types. In v1, those plugin instances share the same load context because they come from the same assembly and dependency closure. The runtime still tracks each `PluginBase` instance separately for descriptors, contributions, diagnostics, and enablement metadata where applicable.

### 3.7 Built-in plugins use the same contribution runtime without dynamic loading

CodeAlta may ship built-in plugins along with the executable. Built-in plugins:

- are registered by host code or host assemblies already loaded in the default context;
- do not use `dotnet build plugin.cs`;
- do not use a collectible plugin ALC;
- can be enabled/disabled through the same configuration and management UI model;
- cannot be dynamically reloaded without restarting CodeAlta.

### 3.8 Load plugins early, but config and safe mode are earlier

Enabled plugins must be built and loaded early enough to contribute command-line nodes, startup hooks, skill/resource roots, provider/backend registrations, and other bootstrap behavior before normal command-line parsing and app startup complete.

A small host-owned pre-startup phase still runs before plugins:

1. resolve CodeAlta home and candidate current project;
2. honor emergency plugin-disable switches from raw args/environment;
3. load/validate enough configuration to know which plugins are enabled;
4. generate plugin build support files;
5. build/load enabled plugins;
6. collect plugin command-line/startup contributions;
7. build the final command-line parser and parse arguments.

Plugin-contributed command-line options cannot be required to disable plugins, because disabling plugins must remain possible when a plugin is broken.

### 3.9 Builds are parallel and cache-aware

Initial startup should build enabled source plugins in parallel, bounded by a runtime-controlled degree of parallelism. Fast-path checks should skip builds when the generated build files, `global.json`, plugin source inputs, SDK selection, CodeAlta build identity, and previous output assembly are still up to date.

The build/load service must also be callable by the plugin management UI for rebuild, reload, clean, enable, and disable operations.

### 3.10 Plugins are trusted code

A collectible `AssemblyLoadContext` is an unload and dependency boundary, not a security sandbox. Building a plugin can also execute MSBuild/NuGet build logic from enabled plugin sources and packages. CodeAlta treats source plugins copied into a plugin root as user-selected trusted code and enables them by default unless disabled by configuration or host safe mode.

## 4. Directory model

### 4.1 Plugin roots

CodeAlta supports two dynamic source plugin scopes:

```text
~/.alta/plugins/                 # global user plugins
<project>/.alta/plugins/         # project-specific plugins
```

The runtime assigns `PluginScope.Global` or `PluginScope.Project` from the root. Plugin source code does not choose its own scope.

### 4.2 Root layout

Each plugin root contains generated build files and one subfolder per source plugin package:

```text
~/.alta/plugins/
  Directory.Build.props          # generated by CodeAlta
  Directory.Build.targets        # generated by CodeAlta
  Directory.Packages.props       # generated by CodeAlta
  global.json                    # generated by CodeAlta
  HelloWorld/
    plugin.cs
    readme.md                    # optional
    skills/                      # optional plugin resources
  GoodbyeWorld/
    plugin.cs
```

Project-specific plugins use the same layout under `<project>/.alta/plugins/`.

### 4.3 Source plugin package layout

The v1 source plugin package convention is:

```text
<plugin-root>/<package-id>/
  plugin.cs
```

Rules:

- `<package-id>` is the folder name and the initial configuration key.
- `plugin.cs` is the v1 entry-point source file for `dotnet build`.
- Additional C# files may be included through .NET file-based directives such as `#:include` when needed.
- Optional resources (`skills/`, `prompts/`, `templates/`, `themes/`, `assets/`) are loaded only when exposed through plugin resource contributions or future conventions.
- Optional `readme.md` documents the package and appears in management UI when present.

A plugin package should not include its own `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, or `global.json` in v1. Those files can shadow CodeAlta's generated root files and break host assembly identity. The runtime should diagnose these as unsupported for source-folder plugins until an advanced project-based plugin format exists.

### 4.4 Configuration key versus runtime key

The folder name is the source plugin package id and should be used for pre-build configuration because it is known without compiling source code.

After build/load, each discovered `PluginBase` type receives a runtime descriptor key using the abstraction rules, for example package id plus assembly/type metadata. A package can contain multiple plugin classes. V1 enablement is package-level for source plugins; future config may add per-plugin-type enablement if needed.

## 5. Generated build files

### 5.1 Ownership and generation rules

Generated XML files should include a stable header comment, for example:

```xml
<!-- This file is generated by CodeAlta. Do not edit directly. -->
```

Generated JSON files should remain valid JSON and use a marker property instead of comments.

Generation rules:

- generate atomically by writing a temporary file and replacing the destination;
- overwrite only files that are recognized as CodeAlta-generated, or report a diagnostic if a user-owned file already exists;
- use deterministic content so timestamp changes reflect actual content changes;
- include enough paths/properties to build plugins from the installed app and from local development builds;
- protect concurrent CodeAlta processes with a per-root file lock where practical.

### 5.2 `Directory.Build.props`

Recommended generated shape:

```xml
<Project>
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>preview</LangVersion>
    <IsPackable>false</IsPackable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <CodeAltaExeFolder Condition="'$(CodeAltaExeFolder)' == ''">C:\Path\To\CodeAlta</CodeAltaExeFolder>
    <CodeAltaPluginRoot>$(MSBuildThisFileDirectory)</CodeAltaPluginRoot>
  </PropertyGroup>
</Project>
```

Notes:

- `CodeAltaExeFolder` is the folder containing the running CodeAlta assemblies, normally `AppContext.BaseDirectory` normalized for MSBuild.
- `OutputType=Library` is required because file-based programs default toward app/executable behavior.
- `LangVersion=preview` is appropriate while targeting .NET 10 preview-era SDK features; this can be revisited after .NET 10 stabilizes.
- The generated file should mirror project-wide defaults that matter for plugin compilation, but it should avoid importing app-specific build targets that are irrelevant to plugin authors.

### 5.3 `Directory.Build.targets`

`Directory.Build.targets` provides host assembly references, shared external package references, and a deterministic `CodeAltaPluginTargetPath=` message emitted after successful builds. Current .NET 10 file-based builds do not accept forwarded MSBuild logger switches without falling back to project-build mode, so output assembly discovery is limited to parsing that generated target message from captured stdout.

Recommended host references:

```xml
<Project>
  <ItemGroup>
    <Reference Include="CodeAlta.Plugins.Abstractions">
      <HintPath>$(CodeAltaExeFolder)\CodeAlta.Plugins.Abstractions.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="CodeAlta.Agent">
      <HintPath>$(CodeAltaExeFolder)\CodeAlta.Agent.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="CodeAlta.Catalog">
      <HintPath>$(CodeAltaExeFolder)\CodeAlta.Catalog.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

The exact CodeAlta assembly list should match the public abstraction signatures. It should start with:

- `CodeAlta.Plugins.Abstractions.dll`
- `CodeAlta.Agent.dll`
- `CodeAlta.Catalog.dll`

and can add other stable public CodeAlta assemblies when plugin abstractions expose their types.

Recommended shared package references:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.5.1">
    <ExcludeAssets>runtime;native</ExcludeAssets>
  </PackageReference>
  <PackageReference Include="XenoAtom.CommandLine" Version="2.0.3">
    <ExcludeAssets>runtime;native</ExcludeAssets>
  </PackageReference>
  <PackageReference Include="XenoAtom.Logging" Version="1.1.3">
    <ExcludeAssets>runtime;native</ExcludeAssets>
  </PackageReference>
  <PackageReference Include="XenoAtom.Terminal.UI" Version="3.2.2">
    <ExcludeAssets>runtime;native</ExcludeAssets>
  </PackageReference>
  <PackageReference Include="XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp" Version="3.2.2">
    <ExcludeAssets>runtime;native</ExcludeAssets>
  </PackageReference>
  <PackageReference Include="XenoAtom.Terminal.UI.Extensions.Markdown" Version="3.2.2">
    <ExcludeAssets>runtime;native</ExcludeAssets>
  </PackageReference>
  <PackageReference Include="XenoAtom.Terminal.UI.Extensions.Screenshot" Version="3.2.2">
    <ExcludeAssets>runtime;native</ExcludeAssets>
  </PackageReference>
  <PackageReference Include="XenoAtom.Terminal.UI.Graphics" Version="3.2.2">
    <ExcludeAssets>runtime;native</ExcludeAssets>
  </PackageReference>
</ItemGroup>
```

The list should stay aligned with `CodeAlta.Plugins.Abstractions` public dependencies. Transitive native runtime packages are not referenced directly; generated shared package references use `<ExcludeAssets>runtime;native</ExcludeAssets>` to keep those assets out of plugin outputs. Shared package versions are written directly on generated `PackageReference` items so package-level `#:package Package@Version` directives remain compatible with NuGet's central package management rules.

The build service may parse only the generated `CodeAltaPluginTargetPath=` message for output discovery. It should not infer diagnostics or output paths from general console formatting.

### 5.4 `Directory.Packages.props`

The generated plugin root `Directory.Packages.props` explicitly disables central package management for source-plugin builds so file-level `#:package Package@Version` directives work normally:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
</Project>
```

CodeAlta still reads its source package-version block; those versions are written directly onto generated shared `PackageReference` items in `Directory.Build.targets`; see section 6.

### 5.5 `global.json`

The generated plugin root `global.json` should mirror `src/global.json` so plugins build with the same SDK family as CodeAlta, plus a valid-JSON `_codealta` marker property used for generated-file ownership checks. Current generated shape:

```json
{
    "sdk": {
        "version": "10.0.100",
        "rollForward": "latestMinor",
        "allowPrerelease": false
    },
    "_codealta": "This file is generated by CodeAlta. Do not edit directly."
}
```

The build process should run with `WorkingDirectory` set to the plugin package directory and pass the entry file as `plugin.cs`. The plugin-root `global.json` remains effective through normal SDK ancestor lookup, while file-based build semantics match running `dotnet build plugin.cs` manually from the package folder. CodeAlta should rely on .NET 10's native file-based build pipeline and let the SDK choose its own output/intermediate/cache paths; after a successful build, CodeAlta records the resolved output assembly path in its plugin build manifest for fast subsequent loads. CodeAlta should not create replacement `.csproj` files or generated entry-source shims for source plugin builds.

If the required SDK is not available, the build fails with a plugin diagnostic. The runtime should not silently fall back to a different target framework or older SDK.

## 6. Package version propagation from CodeAlta source

`src/Directory.Packages.props` should eventually contain a marker-delimited block for package versions that must be copied into plugin roots:

```xml
<!-- CodeAltaPluginPackageVersions:Start -->
<PackageVersion Include="Microsoft.Extensions.AI.Abstractions" Version="10.5.0" />
<PackageVersion Include="XenoAtom.CommandLine" Version="2.0.3" />
<PackageVersion Include="XenoAtom.Logging" Version="1.1.3" />
<PackageVersion Include="XenoAtom.Terminal.UI" Version="3.1.0" />
<!-- CodeAltaPluginPackageVersions:End -->
```

Build/release packaging can copy this block as content into CodeAlta. At runtime, CodeAlta uses it to add explicit versions to generated shared `PackageReference` items in plugin `Directory.Build.targets` files.

Guidelines for this block:

- include only external package versions intended for plugin source compilation;
- do not include CodeAlta assemblies that should be referenced from `$(CodeAltaExeFolder)`;
- include packages needed for public abstraction signatures and common plugin authoring;
- include source-generator/analyzer packages when plugin authors need those build assets;
- keep this block synchronized with `CodeAlta.Plugins.Abstractions` dependencies.

The runtime implementation itself will also need normal package versions for MSBuild-related packages in `src/Directory.Packages.props`, but those packages are runtime implementation dependencies and do not necessarily belong in the plugin-copied package-version block.

## 7. Configuration and enablement

### 7.1 TOML model

Plugin enablement is read from `~/.alta/config.toml` before plugin build/load:

```toml
[plugins.HelloWorld]
enabled = true

[plugins.GoodbyeWorld]
enabled = false
```

Recommended v1 semantics:

- table key = source plugin package id for source plugins;
- table key = stable built-in plugin id for built-in plugins;
- `enabled = true` means CodeAlta may build and load the plugin;
- `enabled = false` means CodeAlta should not build, load, or activate it during startup;
- missing source plugin entries default to enabled because placing a folder under a plugin root is treated as user trust/selection;
- missing built-in entries use built-in defaults chosen by CodeAlta;
- unknown plugin config entries are preserved and shown as unresolved/unknown in diagnostics or management UI.

Project-specific plugin roots may use project-local config from `<project>/.alta/config.toml` when that project root is known. Project config should affect only project-scoped plugins for that project unless future settings define broader override behavior.

### 7.2 Pre-parse safe mode

Because plugins load before normal command-line parsing, CodeAlta must provide host-owned ways to skip dynamic plugins without relying on plugin-contributed CLI options.

Recommended emergency controls:

- a raw-argument pre-scan for a built-in option such as `--no-plugins` or `--plugin-safe-mode`;
- an environment variable such as `CODEALTA_DISABLE_PLUGINS=1`;
- config-level `enabled = false` entries;
- management UI disable actions after startup.

Safe mode should skip dynamic source plugin build/load. Built-in plugins may either be skipped or limited to essential host-owned ones, but the behavior must be explicit in diagnostics.

### 7.3 Invalid config

If `~/.alta/config.toml` is invalid, plugin build/load should not proceed with a guessed enablement state. CodeAlta should use its existing config recovery path first. After config is valid or the user exits recovery, plugin startup can continue or stop safely.

## 8. Startup pipeline

Recommended startup sequence:

1. `Program.Main` initializes plugin runtime services before source plugin builds run.
2. CodeAlta resolves global paths (`~/.alta/`, `~/.alta/config.toml`, `~/.alta/plugins/`) and probes the current project root without plugin-contributed command-line behavior.
3. CodeAlta checks raw safe-mode switches/environment.
4. CodeAlta loads and validates global config, then project config if a project root is known and valid.
5. CodeAlta discovers built-in plugin descriptors.
6. CodeAlta discovers source plugin package folders by looking for `<plugin-root>/<package-id>/plugin.cs`.
7. CodeAlta applies enablement. Disabled source plugins are not built on normal startup.
8. CodeAlta generates/updates plugin root build files for roots that exist and may build plugins.
9. CodeAlta fast-path checks enabled source plugins.
10. CodeAlta builds stale enabled source plugins in parallel with terminal feedback.
11. CodeAlta loads successful source plugin assemblies in collectible ALCs.
12. CodeAlta scans for valid `PluginBase` types, creates descriptors, and orders activations.
13. CodeAlta activates plugins enough to collect bootstrap and command-line contributions.
14. CodeAlta composes the final command-line model and parses the user's arguments.
15. CodeAlta continues full app startup and materializes runtime-phase contributions.
16. CodeAlta starts file-system watchers for discovered plugin roots and reports future source changes as pending reload/rebuild work.

If a plugin fails to build or load, CodeAlta should continue startup without that plugin unless the failing plugin was explicitly required by a future dependency policy. The failure must be attributed and visible.

## 9. MSBuild and pipe logger integration

### 9.1 Runtime package references

The plugin runtime may reference MSBuild-related packages for future structured build integration, but native file-based builds must not pass forwarded MSBuild logger switches until the .NET SDK accepts that combination without treating `plugin.cs` as an XML project file.

`Microsoft.Build*.dll` should not be copied from NuGet to the CodeAlta output. `MSBuildLocator.RegisterDefaults()` aligns CodeAlta's process with the MSBuild assemblies installed with the selected .NET SDK when MSBuild APIs are used.

### 9.2 Registering MSBuild early

`MSBuildLocator.RegisterDefaults()` must run as early as possible, before code touches any `Microsoft.Build` type. A safe shape is:

```csharp
using Microsoft.Build.Locator;

static int Main(string[] args)
{
    MSBuildLocator.RegisterDefaults();
    return RunAfterMSBuildRegistration(args);
}
```

Avoid static fields, static constructors, or top-level code that references `Microsoft.Build` types before registration. Code that creates `AnonymousPipeLoggerServer`, reads `BuildEventArgs`, or handles MSBuild event args should run only after registration.

### 9.3 Build process command

For each stale plugin, the build service starts a `dotnet` process:

```text
dotnet build plugin.cs --nologo -v:minimal
```

Implementation notes:

- use `ProcessStartInfo.ArgumentList`, not shell-quoted command strings;
- set `WorkingDirectory` to the plugin package directory so the command is the same `dotnet build plugin.cs` shape authors run manually; the generated plugin-root `global.json` is found via SDK ancestor lookup;
- do not pass forwarded MSBuild switches such as `/logger:` or `/nr:false`; current .NET 10 SDK file-based builds treat those as project-build mode and try to parse `plugin.cs` as XML;
- pass cancellation when CodeAlta exits or the management operation is cancelled;
- let .NET's native file-based build pipeline choose its output/intermediate/cache paths; CodeAlta stores only its own manifest state under `~/.alta/cache/plugins/build/`;
- capture stdout/stderr for diagnostics and parse only the generated `CodeAltaPluginTargetPath=` message for output assembly discovery.

If the selected SDK does not accept `dotnet build plugin.cs` as a file-based build and instead reports that the `.cs` file could not be loaded as a project file, the runtime should surface an actionable build diagnostic that identifies the SDK/file-build support issue and points users to safe mode. CodeAlta should not retry through a generated `.csproj`; the plugin root `global.json` is responsible for selecting a .NET 10 SDK with native file-based build support. Captured stdout/stderr are retained for troubleshooting, and stdout parsing is limited to the deterministic `CodeAltaPluginTargetPath=` message emitted by CodeAlta's generated target for output assembly discovery.

### 9.4 Capturing the output assembly

The .NET 10 SDK currently falls back to project-build mode when file-based builds receive forwarded MSBuild logger switches such as `/logger:`. The plugin build service therefore records synthetic build-start/build-finish runtime diagnostics, process exit code, stdout/stderr tails, and the generated `CodeAltaPluginTargetPath=` message.

The output assembly path should come from the `CodeAltaPluginTargetPath=` message emitted by CodeAlta's generated target. If multiple target-path messages are present, the runtime should choose the managed plugin assembly for the requested target framework, verify that it exists, and diagnose ambiguity rather than guessing silently. Do not rely on generic `-> path\Plugin.dll` console lines or parse compiler diagnostics from stdout/stderr.

### 9.5 Build result model

Recommended result shape:

```csharp
public sealed record PluginBuildResult
{
    public required string PackageId { get; init; }
    public required PluginScope Scope { get; init; }
    public required string EntryFilePath { get; init; }
    public string? OutputAssemblyPath { get; init; }
    public string? TargetFramework { get; init; }
    public IReadOnlyList<PluginBuildTargetOutput> TargetOutputs { get; init; } = [];
    public bool Succeeded { get; init; }
    public int ExitCode { get; init; }
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<PluginBuildDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record PluginBuildTargetOutput
{
    public required string TargetName { get; init; }
    public required string ItemSpec { get; init; }
    public string? FullPath { get; init; }
    public string? TargetFramework { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
```

Diagnostics should include source file, line/column when MSBuild supplies them, severity, code, message, and the plugin package id.

## 10. Fast-path build skipping

The runtime should maintain a build manifest for each source plugin package. The manifest can live in a CodeAlta-owned cache directory; its exact path is implementation-specific but should not be confused with user-authored plugin files.

Recommended manifest fields:

- package id and scope;
- plugin root and entry file path;
- output assembly path;
- target framework;
- last successful build time;
- CodeAlta executable/version/build identity;
- SDK version and `global.json` content hash;
- generated `Directory.Build.props`, `Directory.Build.targets`, and `Directory.Packages.props` content hashes;
- input source file paths and last-write times/hashes from the previous build;
- direct package directives observed in file-based sources when practical;
- diagnostic summary from the last build.

Minimum fast-path condition:

1. a previous successful build manifest exists;
2. the output assembly path exists;
3. `plugin.cs` is not newer than the output assembly;
4. generated `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, and `global.json` are not newer than the output assembly;
5. the manifest matches the current CodeAlta build identity and SDK selection;
6. any previously known included source files are not newer than the output assembly.

If any condition fails, rebuild. If the .NET SDK's own file-based program cache also skips work, that is an extra optimization; CodeAlta should still own the plugin-level decision and manifest because it needs the output assembly path and diagnostics.

Management UI should expose explicit rebuild/clean actions that bypass the fast path.

## 11. Parallel build scheduling

Build enabled stale plugins in parallel, but with a bounded degree of parallelism.

Recommended behavior:

- default max parallel builds to a small value based on CPU count, for example `min(Environment.ProcessorCount, 4)`;
- allow future configuration or an internal setting for CI/dev scenarios;
- use one pipe logger server and diagnostic collector per plugin build;
- avoid sharing mutable MSBuild state between build tasks;
- serialize generated build file writes per plugin root;
- use per-plugin build locks where practical to avoid two CodeAlta processes rebuilding the same plugin cache concurrently;
- do not let one plugin build failure cancel unrelated plugin builds unless the whole startup is cancelled.

NuGet restore can use the user's global packages cache and may perform network I/O. Disabled plugins should not restore packages during normal startup.

## 12. Terminal feedback

On first startup with stale enabled plugins, CodeAlta should provide visible progress before normal command-line parsing completes.

Recommended interactive behavior:

- use a transient `Terminal.Live` region for per-plugin build and activation status when builds run;
- use the built-in `Spinner` control instead of hand-rolled spinner frames;
- stop or hide the spinner once plugin startup has completed so finished output does not look busy;
- return `TerminalLoopResult.Stop` when plugin startup completes so the live region is discarded before command output or fullscreen TUI startup;
- support `--plugins-wait-for-enter` to keep the transient live region running after source plugin startup completes until Enter is pressed, then discard it with `TerminalLoopResult.Stop`;
- show package id and current state (`queued`, `building`, `built`, `failed`, `up-to-date`) with state icons and minimal success/warning/error color styling;
- include the concise `CodeAlta plugins: ...` build/activation timing summary in the paused live region when `--plugins-wait-for-enter` is used;
- summarize warnings/errors after the discarded live region;
- avoid dumping full MSBuild logs unless a plugin fails or verbose diagnostics are enabled.

Recommended headless/non-interactive behavior:

- write concise text diagnostics to the normal logger/output path;
- avoid terminal control sequences;
- continue startup if plugins are skipped by fast path.

Fast-path loads should produce only a minimal one-line summary after transient live startup progress (or inside the paused live region when `--plugins-wait-for-enter` is used), such as `CodeAlta plugins: 7 source plugin packages checked (0 built, 7 up-to-date); 7 source plugins activated in 120ms.` Startup should not feel slow or noisy when plugins are already up to date.

## 13. File change watching and reload notifications

CodeAlta should monitor plugin root folders with `FileSystemWatcher` so the UI can report changed plugins without polling every few seconds.

### 13.1 Watched roots

The runtime should create watchers for plugin roots that exist and are in scope for the current process:

- `~/.alta/plugins/` for global source plugins;
- `<project>/.alta/plugins/` for the selected/current project when project plugins are in scope.

Recommended watcher settings:

- `IncludeSubdirectories = true` so changes inside plugin package folders are detected;
- watch `FileName`, `DirectoryName`, `LastWrite`, `Size`, and `CreationTime` where supported;
- listen to `Created`, `Changed`, `Deleted`, `Renamed`, and `Error`;
- debounce and coalesce events per plugin package, for example with a short 250-1000 ms quiet period;
- ignore known SDK build output folders if any are placed under the plugin root;
- suppress self-generated events while CodeAlta writes `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, or `global.json`, then explicitly update the affected state.

`FileSystemWatcher` can drop events under load. On watcher `Error` or overflow, the runtime should mark the root as needing a rescan and show an attributed diagnostic rather than reverting to continuous polling.

### 13.2 Change model

File changes should not automatically rebuild or reload plugins. The watcher marks plugin packages as changed and lets the user choose when to rebuild/reload.

Recommended states:

```csharp
public enum PluginSourceChangeState
{
    Unchanged,
    Added,
    Changed,
    Deleted,
    BuildFilesChanged,
    UnknownRescanRequired
}
```

Behavior:

- an active plugin continues running its currently loaded assembly until the user reloads, disables, or exits CodeAlta;
- a changed enabled plugin is marked `Changed`/`BuildFilesChanged` and shown as needing rebuild/reload;
- a newly added package is shown as discovered but not built/loaded unless enablement policy allows it;
- a deleted package remains active only for the current activation, if already loaded, and should be shown as source missing; explicit reload should unload it and fail/disable cleanly;
- root-level generated build file changes should mark enabled source plugins stale because the next build may produce different output.

The fast-path build manifest remains the authoritative check before actual build/load. The watcher is an early notification and UI state mechanism, not the only correctness check.

### 13.3 User notification surfaces

The UI should make pending plugin changes visible without interrupting the user's current work.

Recommended surfaces:

- plugin management dialog: list changed/added/deleted/stale plugins with actions to rebuild, reload, disable, or ignore;
- toast notification: when the interactive TUI has a `ToastHost`, show a coalesced toast such as `3 plugins changed` with an action to open plugin management;
- compact prompt/footer status: show a small bottom-bar indicator such as `Plugins: 3 changed` while changes are pending;
- diagnostics/log viewer: record watcher failures and source-change events with plugin root/package attribution.

Toast support requires the CodeAlta root visual to be wrapped with a `ToastHost`, similar to the XenoAtom Terminal UI controls demo. Plugin change toasts should use `ToastService.Show(...)`, avoid stealing focus, coalesce repeated changes, and be disabled/no-op in headless mode.

### 13.4 Lifetime

Watchers are runtime-owned resources:

- global plugin root watcher lives for the CodeAlta process lifetime;
- project plugin root watcher is created/disposed as the current project scope is opened/closed or as project plugin management requires;
- watchers must be disposed during app shutdown;
- reload/rebuild operations should not race watcher callbacks; use the same plugin runtime state lock or serialized operation queue used by plugin management actions.

## 14. Assembly load context design

### 14.1 Load unit

Each successful dynamic source plugin package creates a load unit:

```csharp
public sealed record PluginLoadUnit
{
    public required string PackageId { get; init; }
    public required PluginScope Scope { get; init; }
    public required string PackageDirectory { get; init; }
    public required string AssemblyPath { get; init; }
    public required AssemblyLoadContext LoadContext { get; init; }
}
```

The load unit owns the plugin assembly and private dependencies. Plugin instances, contribution handles, task tracking, and diagnostics refer back to the load unit and activation generation.

### 14.2 Dependency resolution

Use `AssemblyDependencyResolver` with the plugin output assembly path for private dependencies. Override `Load` and `LoadUnmanagedDll` in the collectible ALC.

Resolution policy:

1. If the requested assembly is host-shared, return the assembly from `AssemblyLoadContext.Default`.
2. Otherwise, use `AssemblyDependencyResolver.ResolveAssemblyToPath` and load from the plugin output/dependency directory.
3. If no path is resolved, return `null` and let normal resolution fail with attributed diagnostics.
4. For unmanaged libraries, use `ResolveUnmanagedDllToPath` and `LoadUnmanagedDllFromPath`.

The host-shared set should include assemblies whose types appear in plugin abstraction signatures:

- `CodeAlta.Plugins.Abstractions`
- `CodeAlta.Agent`
- `CodeAlta.Catalog`
- `Microsoft.Extensions.AI.Abstractions`
- `XenoAtom.CommandLine`
- `XenoAtom.Logging`
- `XenoAtom.Terminal.UI`
- `XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp`
- `XenoAtom.Terminal.UI.Extensions.Markdown`
- `XenoAtom.Terminal.UI.Extensions.Screenshot`
- `XenoAtom.Terminal.UI.Graphics`

This set must stay synchronized with `CodeAlta.Plugins.Abstractions` public dependencies and generated build references.

### 14.3 Collectibility and unload

The plugin ALC must be collectible:

```csharp
new AssemblyLoadContext(name, isCollectible: true)
```

Unload sequence:

1. stop dispatching callbacks to the plugin activation;
2. cancel plugin lifetime tokens and tracked plugin tasks;
3. remove runtime-owned contributions;
4. call `OnDeactivatingAsync` and `DisposeAsync` with bounded timeouts;
5. release host references to plugin instances, delegates, visuals, and descriptors that contain plugin type instances;
6. call `AssemblyLoadContext.Unload()`;
7. force one or more GC cycles in diagnostic/test paths to confirm collectibility;
8. report diagnostics if the ALC remains alive.

The runtime cannot guarantee unload if plugin code keeps static references, starts untracked background work, pins native resources, or leaks plugin objects into host static state. The management UI should show unload failures separately from build/load failures.

## 15. Discovery and activation

After loading a dynamic assembly, the runtime scans for plugin types using the abstraction rules:

- derives from `PluginBase`;
- public;
- concrete;
- non-generic;
- public parameterless constructor.

Activation should follow the lifecycle defined in `plugins_abstraction_specs.md`:

1. create descriptor from package id, assembly metadata, attributes, and sidecar docs;
2. instantiate plugin type;
3. attach `PluginRuntimeContext`, logger, services, scope, package directory, and lifetime token;
4. call `InitializeAsync`;
5. collect contributions;
6. register runtime-owned handles;
7. call `OnActivatedAsync` when appropriate;
8. mark active.

If one plugin type in an assembly fails, runtime policy must decide whether to fail the whole package load unit or only that plugin type. Recommended v1 policy: fail the affected plugin type, keep other types only if contribution registration can remain isolated and unload semantics are not compromised; otherwise fail the package and unload the ALC.

## 16. Contribution registration and ordering

The runtime owns registration and removal for all contributions returned by plugins.

Registration inputs:

- built-in plugin descriptors;
- global source plugin descriptors;
- project source plugin descriptors;
- plugin dependency metadata when available;
- contribution `Order`, natural names, and plugin point-specific precedence.

Recommended default activation ordering:

1. built-in plugins;
2. global source plugins by package id;
3. project source plugins by project id/path and package id;
4. dependency ordering within each group when dependency metadata exists;
5. stable runtime-key tie-breakers.

Conflict resolution remains plugin-point-specific as described by the abstraction spec. Plugins may override built-in commands, tools, key bindings, UI regions, prompt parts, and backend names where the runtime supports replacement, but all overrides must be visible in diagnostics.

Project-scoped plugin contributions should be registered with applicability predicates so they affect only the matching project context.

## 17. Built-in plugins

Built-in plugins are runtime descriptors backed by host-owned code. They can implement the same `PluginBase` abstraction or be adapted to the same internal contribution model.

Built-in plugin requirements:

- stable id for config, diagnostics, and management UI;
- descriptor metadata and optional documentation;
- enable/disable state in `[plugins.<id>]` config;
- contribution handles and diagnostics like dynamic plugins;
- no dynamic build, no ALC, no unload/reload beyond enable/disable of contributions;
- reload action should explain that restart is required if code changes.

Built-ins are useful for features that should be optional and diagnosable but are shipped with CodeAlta.

## 18. Runtime services and reusable manager

The runtime should be organized around reusable services rather than startup-only code.

Candidate service boundaries:

```csharp
public interface IPluginRuntime
{
    ValueTask<PluginStartupResult> StartAsync(PluginStartupOptions options, CancellationToken cancellationToken);
    ValueTask<PluginReloadResult> ReloadAsync(string packageId, PluginScope scope, CancellationToken cancellationToken);
    ValueTask<PluginDisableResult> DisableAsync(string packageId, PluginScope scope, CancellationToken cancellationToken);
    IReadOnlyList<PluginRuntimeDescriptor> Plugins { get; }
}

public interface IPluginBuildService
{
    ValueTask<PluginBuildResult> BuildAsync(PluginBuildRequest request, CancellationToken cancellationToken);
    ValueTask<PluginBuildCheckResult> CheckUpToDateAsync(PluginBuildRequest request, CancellationToken cancellationToken);
}

public interface IPluginLoadService
{
    ValueTask<PluginLoadResult> LoadAsync(PluginBuildResult build, CancellationToken cancellationToken);
    ValueTask<PluginUnloadResult> UnloadAsync(PluginLoadUnit loadUnit, CancellationToken cancellationToken);
}

public interface IPluginChangeMonitor : IAsyncDisposable
{
    IReadOnlyList<PluginSourceChange> PendingChanges { get; }
    event EventHandler<PluginSourceChangeBatchEventArgs>? Changed;

    ValueTask StartAsync(IReadOnlyList<PluginRootDescriptor> roots, CancellationToken cancellationToken);
    ValueTask MarkCleanAsync(string packageId, PluginScope scope, CancellationToken cancellationToken);
}
```

The exact API can differ, but the implementation should support:

- startup load;
- management UI build/reload/disable;
- test harnesses;
- `FileSystemWatcher`-based source change monitoring and pending reload notifications;
- future plugin install/update flows.

## 19. Diagnostics

Diagnostics must be plugin-attributed and separated from normal thread conversation history.

Diagnostic categories:

- root generation diagnostics;
- config enablement diagnostics;
- discovery diagnostics;
- build warnings/errors;
- load/dependency resolution failures;
- plugin type discovery failures;
- initialization/activation failures;
- contribution registration conflicts;
- callback failures;
- source change and watcher diagnostics;
- deactivation/unload failures.

Each diagnostic should include, when known:

- plugin scope;
- package id;
- runtime plugin key/type;
- root/package/source path;
- build command or operation kind;
- severity;
- exception or MSBuild diagnostic details;
- suggested next action.

Plugin management UI should show last build time, build output assembly, enabled state, active state, load context state, warnings/errors, and contribution summary.

## 20. Security and trust

The runtime must be explicit that plugins are trusted code.

Important implications:

- building an enabled plugin may restore packages and run package/MSBuild build logic;
- loading a plugin runs .NET code in the CodeAlta process;
- plugin callbacks can read/write files, spawn processes, use the network, and inspect host-exposed data;
- `AssemblyLoadContext` does not restrict permissions;
- disabling/unloading is for behavior management and versioning, not containment.

Recommended safety posture:

- build/load discovered source plugin folders by default because the plugin root is treated as a user-selected trust boundary;
- show source path and README before enable/disable/rebuild/reload actions in management UI;
- provide safe mode that skips dynamic plugins;
- keep build diagnostics visible before activation;
- make built-in overrides and plugin overrides inspectable;
- never persist plugin runtime failures into normal conversation history unless a plugin intentionally emits user-visible content.

## 21. Built-in CodeAlta skill and sample plugins

CodeAlta should ship a built-in CodeAlta skill that documents CodeAlta itself, including:

- CodeAlta home and data directory organization;
- plugin root layout;
- generated build files;
- `dotnet build plugin.cs` authoring;
- `CodeAlta.Plugins.Abstractions` contribution points;
- dynamic loading and unload limitations;
- troubleshooting build/load diagnostics;
- sample plugin walkthroughs.

The skill should include a `samples/plugins/` folder with real sample plugins. Samples should double as integration test inputs for the plugin runtime.

Recommended initial samples:

- `hello-command`: one-file command and notification plugin;
- `prompt-guidance`: system/developer prompt contribution plugin;
- `ui-status`: terminal UI status/visual contribution plugin;
- `skill-root`: plugin-contributed skill root;
- `package-reference`: plugin with a direct `#:package` dependency to verify `EnableDynamicLoading` and private dependency resolution;
- `background-task`: plugin using `IPluginTaskService` and unload cancellation;
- `backend-provider`: minimal backend/provider contribution using `CodeAlta.Agent` contracts;
- `multi-plugin-assembly`: one `plugin.cs` assembly with multiple `PluginBase` classes.

Integration tests should copy these samples into temporary plugin roots, generate build files, run the build service, load the result, assert expected contributions, unload the ALC, and verify diagnostics. Tests that require the .NET 10 SDK or network package restore should be categorized so failures are actionable.

## 22. Suggested implementation slices

A practical first implementation order:

1. Add runtime package references and call `MSBuildLocator.RegisterDefaults()` at the start of `Program.Main`.
2. Implement generated plugin root files for a temporary/test root.
3. Implement `IPluginBuildService` using native `dotnet build plugin.cs` from each plugin package directory.
4. Implement structured `Build` target output collection and build manifest fast path.
5. Implement collectible ALC loading with host-shared assembly resolution.
6. Discover `PluginBase` types and create descriptors/contexts.
7. Activate a narrow contribution subset first, for example commands/resources/diagnostics.
8. Add config enablement for `[plugins.<package-id>]`.
9. Add startup integration before command-line parsing.
10. Add terminal progress for stale builds.
11. Add plugin management UI operations backed by the same services.
12. Add `FileSystemWatcher`-based change monitoring, changed-plugin dialog state, toast notifications, and prompt/footer status.
13. Add built-in CodeAlta skill and sample plugin integration tests.

## 23. Open questions

- Should source plugin package enablement remain package-level only, or should v1 also support per-`PluginBase` type enablement inside one assembly?
- Should CodeAlta add a stricter opt-in mode for environments that do not want plugin roots to act as a user-selected trust boundary?
- Where should build manifests live for project-scoped plugins so they are durable but do not create noisy project files?
- Should plugin roots support an explicit `plugin.toml` manifest before a broader package format exists?
- How much of `Directory.Packages.props` should be copied into plugin roots versus generated from a smaller curated dependency list?
- Which built-in plugins should ship first, and what are their default enablement states?
- What debounce interval and notification coalescing policy should plugin root watchers use by default?
- Should the compact prompt/footer plugin-change indicator be global, project-scoped, or both?
