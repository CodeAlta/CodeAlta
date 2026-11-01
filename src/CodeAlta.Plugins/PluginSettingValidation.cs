using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>Keeps the settings of a plugin that are valid and within the limit of a plugin.</summary>
internal static class PluginSettingValidation
{
    /// <summary>
    /// Passes the settings that are valid: one per key, and no more than <see cref="PluginSettingLimits.Settings"/>. The others
    /// are left out and each one adds a warning. A setting of each user in a plugin of a project is kept for that project.
    /// </summary>
    /// <param name="descriptor">The plugin that returned the settings.</param>
    /// <param name="scope">Where the plugin comes from: the settings of a plugin of a project are all kept by that project.</param>
    /// <param name="settings">What the plugin returned.</param>
    /// <param name="diagnostics">Receives a warning for each setting that is left out.</param>
    /// <returns>The settings to register, in their order.</returns>
    internal static IReadOnlyList<PluginSettingContribution> Filter(PluginDescriptor descriptor, PluginScope scope, IEnumerable<PluginSettingContribution> settings,
        List<PluginRuntimeDiagnostic> diagnostics)
    {
        var kept = new List<PluginSettingContribution>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in settings)
        {
            if (setting is null) continue;
            var problem = setting.Validate();
            if (problem is null && !keys.Add(setting.Key!)) problem = "repeats the key of another setting of the plugin";
            if (problem is null && kept.Count >= PluginSettingLimits.Settings) problem = $"is one too many: a plugin declares at most {PluginSettingLimits.Settings} settings";
            if (problem is null)
            {
                // A plugin a repository brings never writes the configuration of the user.
                kept.Add(scope == PluginScope.Project && setting.Scope != PluginSettingScope.Project ? setting with { Scope = PluginSettingScope.Project } : setting);
                continue;
            }

            diagnostics.Add(new PluginRuntimeDiagnostic
            {
                Severity = PluginDiagnosticSeverity.Warning,
                Source = PluginRuntimeDiagnosticSource.Contribution,
                Message = $"The setting '{setting.Key}' {problem}, so it is left out.",
                RuntimeKey = descriptor.RuntimeKey,
                Metadata = new Dictionary<string, string> { ["Point"] = PluginPoint.Settings.ToString(), ["NaturalName"] = setting.Key ?? string.Empty },
            });
        }

        return kept;
    }
}
