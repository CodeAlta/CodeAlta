using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>Checks the types of credential providers a plugin returns before they are registered.</summary>
internal static class PluginCredentialProviderValidation
{
    /// <summary>
    /// Passes the types that are valid, one per type, of a plugin of the user or a built-in plugin. A plugin of a project brings
    /// none: a cloned repository never takes part in reading secrets. The others are left out with a warning each.
    /// </summary>
    /// <param name="descriptor">The plugin.</param>
    /// <param name="scope">Where it was loaded from.</param>
    /// <param name="providers">What it returned.</param>
    /// <param name="diagnostics">Receives a warning for each type left out.</param>
    /// <returns>The types to register.</returns>
    internal static IReadOnlyList<PluginCredentialProviderContribution> Filter(PluginDescriptor descriptor, PluginScope scope,
        IEnumerable<PluginCredentialProviderContribution> providers, List<PluginRuntimeDiagnostic> diagnostics)
    {
        var kept = new List<PluginCredentialProviderContribution>();
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (provider is null) continue;
            var problem = scope == PluginScope.Project ? "comes from a plugin of a project, which never reads secrets" : provider.Validate();
            if (problem is null && !types.Add(provider.Type)) problem = "repeats the type of another credential provider of the plugin";
            if (problem is null)
            {
                kept.Add(provider);
                continue;
            }

            diagnostics.Add(new PluginRuntimeDiagnostic
            {
                Severity = PluginDiagnosticSeverity.Warning,
                Source = PluginRuntimeDiagnosticSource.Contribution,
                Message = $"The credential provider type '{provider.Type}' {problem}, so it is left out.",
                RuntimeKey = descriptor.RuntimeKey,
                Metadata = new Dictionary<string, string> { ["Point"] = PluginPoint.CredentialProvider.ToString(), ["NaturalName"] = provider.Type },
            });
        }

        return kept;
    }
}
