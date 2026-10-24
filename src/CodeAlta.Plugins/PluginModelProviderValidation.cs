using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>Checks the model provider types a plugin contributes before they are registered.</summary>
internal static class PluginModelProviderValidation
{
    /// <summary>
    /// Passes the provider types that are valid, one per type. The others are left out and each one adds a warning.
    /// </summary>
    /// <param name="descriptor">The plugin that returned the types.</param>
    /// <param name="providers">What the plugin returned.</param>
    /// <param name="diagnostics">Receives a warning for each type that is left out.</param>
    /// <returns>The types to register, in their order.</returns>
    internal static IReadOnlyList<PluginModelProviderContribution> Filter(
        PluginDescriptor descriptor,
        IEnumerable<PluginModelProviderContribution> providers,
        List<PluginRuntimeDiagnostic> diagnostics)
    {
        var kept = new List<PluginModelProviderContribution>();
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (provider is null) continue;
            var problem = provider.CheckValid();
            if (problem is null && !types.Add(provider.ProviderType)) problem = "repeats the type of another provider type of the plugin";
            if (problem is null)
            {
                kept.Add(provider);
                continue;
            }

            diagnostics.Add(new PluginRuntimeDiagnostic
            {
                Severity = PluginDiagnosticSeverity.Warning,
                Source = PluginRuntimeDiagnosticSource.Contribution,
                Message = $"The model provider type '{provider.ProviderType}' {problem}, so it is left out.",
                RuntimeKey = descriptor.RuntimeKey,
                Metadata = new Dictionary<string, string> { ["Point"] = PluginPoint.ModelProvider.ToString(), ["NaturalName"] = provider.ProviderType },
            });
        }

        return kept;
    }
}
