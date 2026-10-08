using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Describes plugin enablement resolved from global/project config and safe mode.
/// </summary>
public sealed record PluginEnablementResult
{
    /// <summary>Gets the plugin id that was resolved.</summary>
    public required string PluginId { get; init; }

    /// <summary>Gets the plugin scope.</summary>
    public required PluginScope Scope { get; init; }

    /// <summary>Gets a value indicating whether the plugin is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the reason for the enablement result.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Describes the validated plugin configuration documents used for enablement.
/// </summary>
public sealed record PluginRuntimeConfigLoadResult
{
    /// <summary>Gets a value indicating whether plugin enablement can be resolved safely.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets the global configuration document when loading succeeded.</summary>
    public CodeAltaConfigDocument? GlobalConfig { get; init; }

    /// <summary>Gets the project configuration document when loading succeeded and available.</summary>
    public CodeAltaConfigDocument? ProjectConfig { get; init; }

    /// <summary>Gets diagnostics raised while loading configuration.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Resolves plugin enablement from CodeAlta TOML configuration documents.
/// </summary>
public sealed class PluginRuntimeConfigResolver
{
    /// <summary>
    /// Loads global/project configuration only after validation so plugin enablement is not guessed from invalid TOML.
    /// </summary>
    /// <param name="store">The CodeAlta configuration store.</param>
    /// <param name="globalConfigPath">The global config path to validate before loading.</param>
    /// <param name="projectConfigPath">The project config path to validate before loading, when available.</param>
    /// <param name="projectRoot">The project root used to load project config, when available.</param>
    /// <returns>The load result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="store"/> is <see langword="null"/>.</exception>
    public static PluginRuntimeConfigLoadResult LoadValidatedConfig(
        CodeAltaConfigStore store,
        string globalConfigPath,
        string? projectConfigPath = null,
        string? projectRoot = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        if (!ValidateConfigFile(globalConfigPath, "global", diagnostics))
        {
            return new PluginRuntimeConfigLoadResult { Diagnostics = diagnostics };
        }

        if (!string.IsNullOrWhiteSpace(projectConfigPath) &&
            File.Exists(projectConfigPath) &&
            !ValidateConfigFile(projectConfigPath, "project", diagnostics))
        {
            return new PluginRuntimeConfigLoadResult { Diagnostics = diagnostics };
        }

        try
        {
            return new PluginRuntimeConfigLoadResult
            {
                Succeeded = true,
                GlobalConfig = store.LoadGlobal(),
                ProjectConfig = string.IsNullOrWhiteSpace(projectRoot) ? null : store.LoadProject(projectRoot),
                Diagnostics = diagnostics,
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Also a file that stopped being readable between its validation and this load.
            diagnostics.Add(PluginRuntimeDiagnostic.Error(
                PluginRuntimeDiagnosticSource.Config,
                "Plugin enablement was not resolved because CodeAlta configuration could not be loaded after validation.",
                path: globalConfigPath,
                exception: ex));
            return new PluginRuntimeConfigLoadResult { Diagnostics = diagnostics };
        }
    }

    /// <summary>
    /// Resolves enablement for a dynamic source plugin.
    /// </summary>
    /// <param name="package">The source plugin package.</param>
    /// <param name="globalConfig">The global config document.</param>
    /// <param name="projectConfig">The project config document, when available.</param>
    /// <param name="safeMode">Whether plugins are disabled by host safe mode.</param>
    /// <returns>The enablement result.</returns>
    public PluginEnablementResult ResolveSourcePlugin(
        SourcePluginPackage package,
        CodeAltaConfigDocument globalConfig,
        CodeAltaConfigDocument? projectConfig = null,
        bool safeMode = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(globalConfig);
        if (safeMode)
        {
            return Disabled(package.PackageId, package.Root.Scope, "Plugin safe mode is active.");
        }

        var scopeConfig = package.Root.Scope == PluginScope.Project ? projectConfig : null;
        if (TryGetEnabled(scopeConfig, package.PackageId, out var projectEnabled))
        {
            return FromConfig(package.PackageId, package.Root.Scope, projectEnabled, "Project plugin configuration.");
        }

        if (TryGetEnabled(globalConfig, package.PackageId, out var globalEnabled))
        {
            return FromConfig(package.PackageId, package.Root.Scope, globalEnabled, "Global plugin configuration.");
        }

        return new PluginEnablementResult
        {
            PluginId = package.PackageId,
            Scope = package.Root.Scope,
            Enabled = true,
            Reason = "Source plugin default enablement.",
        };
    }

    /// <summary>
    /// Resolves enablement for a built-in plugin.
    /// </summary>
    /// <param name="definition">The built-in plugin definition.</param>
    /// <param name="globalConfig">The global config document.</param>
    /// <param name="safeMode">Whether plugins are disabled by host safe mode.</param>
    /// <returns>The enablement result.</returns>
    public PluginEnablementResult ResolveBuiltInPlugin(
        BuiltInPluginDefinition definition,
        CodeAltaConfigDocument globalConfig,
        bool safeMode = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(globalConfig);
        if (safeMode)
        {
            return Disabled(definition.Id, PluginScope.Global, "Plugin safe mode is active.");
        }

        if (TryGetEnabled(globalConfig, definition.Id, out var configuredEnabled))
        {
            return FromConfig(definition.Id, PluginScope.Global, configuredEnabled, "Global plugin configuration.");
        }

        return new PluginEnablementResult
        {
            PluginId = definition.Id,
            Scope = PluginScope.Global,
            Enabled = definition.EnabledByDefault,
            Reason = definition.EnabledByDefault ? "Built-in plugin default enablement." : "Built-in plugin default disabled state.",
        };
    }

    /// <summary>
    /// Reads plugin safe mode from raw startup arguments and environment variables.
    /// </summary>
    /// <param name="args">The raw startup arguments.</param>
    /// <returns><see langword="true"/> when dynamic plugin build/load should be bypassed.</returns>
    public static bool IsSafeModeEnabled(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(static arg => string.Equals(arg, "--no-plugins", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--plugin-safe-mode", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(Environment.GetEnvironmentVariable("CODEALTA_DISABLE_PLUGINS"), "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("CODEALTA_DISABLE_PLUGINS"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetEnabled(CodeAltaConfigDocument? document, string pluginId, out bool enabled)
    {
        enabled = false;
        if (document?.Plugins is null)
        {
            return false;
        }

        if (document.Plugins.TryGetValue(pluginId, out var settings) && settings.Enabled is bool configured)
        {
            enabled = configured;
            return true;
        }

        return false;
    }

    private static PluginEnablementResult FromConfig(string pluginId, PluginScope scope, bool enabled, string reason)
        => new()
        {
            PluginId = pluginId,
            Scope = scope,
            Enabled = enabled,
            Reason = enabled ? reason : reason + " Disabled by configuration.",
        };

    private static PluginEnablementResult Disabled(string pluginId, PluginScope scope, string reason)
        => new()
        {
            PluginId = pluginId,
            Scope = scope,
            Enabled = false,
            Reason = reason,
        };

    private static bool ValidateConfigFile(string path, string scope, List<PluginRuntimeDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return true;
        }

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another program is writing the file, or it cannot be opened: enablement is not guessed.
            diagnostics.Add(PluginRuntimeDiagnostic.Error(
                PluginRuntimeDiagnosticSource.Config,
                $"Plugin enablement was not resolved because the {scope} CodeAlta config could not be read: {exception.Message}",
                path: path,
                exception: exception));
            return false;
        }

        var validation = CodeAltaConfigStore.ValidateGlobalConfigContent(content, path);
        if (validation.IsValid)
        {
            return true;
        }

        diagnostics.Add(new PluginRuntimeDiagnostic
        {
            Severity = PluginDiagnosticSeverity.Error,
            Source = PluginRuntimeDiagnosticSource.Config,
            Message = $"Plugin enablement was not resolved because the {scope} CodeAlta config is invalid. Use the normal config recovery flow before building or loading plugins.",
            Path = path,
            Metadata = new Dictionary<string, string>
            {
                ["Line"] = validation.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["Column"] = validation.Column?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["ValidationMessage"] = validation.Message ?? string.Empty,
            },
        });
        return false;
    }
}
