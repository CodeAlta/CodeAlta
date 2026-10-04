using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.LiveTool;

/// <summary>
/// The plugin catalog of the <c>alta</c> commands over a host's plugin runtime: its active plugins and the
/// <c>alta</c> commands they contribute, as they are when asked.
/// </summary>
/// <param name="runtime">The host's plugin runtime.</param>
public sealed class RuntimeAltaPluginCatalog(PluginRuntimeManager runtime) : IAltaPluginCatalog
{
    /// <inheritdoc />
    public IReadOnlyList<AltaPluginSummary> ListPlugins()
        => runtime.ActivePlugins
            .Select(CreateSummary)
            .OrderBy(static plugin => plugin.RuntimeKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <inheritdoc />
    public AltaPluginSummary? GetPlugin(string runtimeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeKey);
        return ListPlugins().FirstOrDefault(plugin => string.Equals(plugin.RuntimeKey, runtimeKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public IReadOnlyList<AltaCommandPolicy> ListCommandPolicies()
        => ListCommandContributions()
            .Select(static registration => new AltaCommandPolicy
            {
                Path = registration.Command.Path,
                RequiresInProcessRuntime = registration.Command.Policy.RequiresInProcessRuntime,
                IsMutating = registration.Command.Policy.IsMutating,
                IsDisruptive = registration.Command.Policy.IsDisruptive,
                SupportsCatalogOnlyContext = registration.Command.Policy.SupportsCatalogOnlyContext,
            })
            .ToArray();

    /// <inheritdoc />
    public IReadOnlyList<AltaPluginCommandContribution> ListCommandContributions()
    {
        var activePlugins = runtime.ActivePlugins.ToDictionary(plugin => plugin.Descriptor.RuntimeKey, StringComparer.OrdinalIgnoreCase);
        return runtime.Registry.GetSnapshot()
            .Where(static registration => registration.Contribution is PluginAltaCommandContribution)
            .Select(registration => CreateCommandContribution(registration, activePlugins))
            .Where(static registration => registration is not null)
            .Select(static registration => registration!)
            .OrderBy(static registration => registration.Command.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static registration => registration.Plugin.RuntimeKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private AltaPluginSummary CreateSummary(ActivePluginInstance plugin)
    {
        var descriptor = plugin.Descriptor;
        var packageId = plugin.SourcePackage?.PackageId;
        var diagnostics = runtime.Diagnostics
            .Where(diagnostic => string.Equals(diagnostic.RuntimeKey, descriptor.RuntimeKey, StringComparison.OrdinalIgnoreCase) ||
                                 (!string.IsNullOrWhiteSpace(packageId) && string.Equals(diagnostic.PackageId, packageId, StringComparison.OrdinalIgnoreCase)))
            .Select(FormatDiagnostic)
            .ToArray();
        return new AltaPluginSummary
        {
            RuntimeKey = descriptor.RuntimeKey,
            DisplayName = descriptor.DisplayName ?? descriptor.TypeName,
            Version = descriptor.Version,
            Scope = plugin.SourcePackage?.Root.Scope.ToString().ToLowerInvariant() ?? "builtin",
            State = plugin.State.ToString().ToLowerInvariant(),
            Diagnostics = diagnostics,
        };
    }

    private static string FormatDiagnostic(PluginRuntimeDiagnostic diagnostic)
        => $"{diagnostic.Severity}/{diagnostic.Source}: {diagnostic.Message}";

    private static AltaPluginCommandContribution? CreateCommandContribution(
        PluginContributionRegistration registration,
        IReadOnlyDictionary<string, ActivePluginInstance> activePlugins)
    {
        if (registration.Contribution is not PluginAltaCommandContribution command ||
            !activePlugins.TryGetValue(registration.Handle.PluginRuntimeKey, out var plugin))
        {
            return null;
        }

        return new AltaPluginCommandContribution
        {
            Plugin = plugin.Descriptor,
            Command = command,
            Services = plugin.RuntimeContext.Services,
            Scope = registration.Scope,
            ScopeProjectId = registration.ScopeProjectId,
            ScopeProjectPath = registration.ScopeProjectPath,
        };
    }
}
