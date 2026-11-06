using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>Keeps the automation triggers of a plugin that are valid.</summary>
internal static class PluginAutomationTriggerValidation
{
    /// <summary>The most triggers a plugin gives.</summary>
    internal const int MaximumTriggers = 16;

    /// <summary>
    /// Passes the triggers that are valid: one per type, and no more than <see cref="MaximumTriggers"/>. The others are left
    /// out and each one adds a warning.
    /// </summary>
    /// <param name="descriptor">The plugin that returned the triggers.</param>
    /// <param name="triggers">What the plugin returned.</param>
    /// <param name="diagnostics">Receives a warning for each trigger that is left out.</param>
    /// <returns>The triggers to register, in their order.</returns>
    internal static IReadOnlyList<PluginAutomationTriggerContribution> Filter(PluginDescriptor descriptor, IEnumerable<PluginAutomationTriggerContribution> triggers,
        List<PluginRuntimeDiagnostic> diagnostics)
    {
        var kept = new List<PluginAutomationTriggerContribution>();
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trigger in triggers)
        {
            if (trigger is null) continue;
            var problem = trigger.Validate();
            // A trigger that is valid has a type (Validate refuses a null one).
            if (problem is null && !types.Add(trigger.Type!)) problem = "repeats the type of another trigger of the plugin";
            if (problem is null && kept.Count >= MaximumTriggers) problem = $"is one too many: a plugin gives at most {MaximumTriggers} triggers";
            if (problem is null)
            {
                kept.Add(trigger);
                continue;
            }

            diagnostics.Add(new PluginRuntimeDiagnostic
            {
                Severity = PluginDiagnosticSeverity.Warning,
                Source = PluginRuntimeDiagnosticSource.Contribution,
                Message = $"The automation trigger '{trigger.Type}' {problem}, so it is left out.",
                RuntimeKey = descriptor.RuntimeKey,
                Metadata = new Dictionary<string, string> { ["Point"] = PluginPoint.AutomationTrigger.ToString(), ["NaturalName"] = trigger.Type ?? string.Empty },
            });
        }

        return kept;
    }

    /// <summary>The most scripts a plugin gives.</summary>
    internal const int MaximumScripts = 32;

    /// <summary>Passes the scripts that are valid: one per name, and no more than <see cref="MaximumScripts"/>; the others add a warning.</summary>
    /// <param name="descriptor">The plugin that returned the scripts.</param>
    /// <param name="scripts">What the plugin returned.</param>
    /// <param name="diagnostics">Receives a warning for each script that is left out.</param>
    /// <returns>The scripts to register, in their order.</returns>
    internal static IReadOnlyList<PluginScriptContribution> FilterScripts(PluginDescriptor descriptor, IEnumerable<PluginScriptContribution> scripts, List<PluginRuntimeDiagnostic> diagnostics)
    {
        var kept = new List<PluginScriptContribution>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var script in scripts)
        {
            if (script is null) continue;
            var problem = script.Validate();
            // A script that is valid has a name (Validate refuses a null one).
            if (problem is null && !names.Add(script.Name!)) problem = "repeats the name of another script of the plugin";
            if (problem is null && kept.Count >= MaximumScripts) problem = $"is one too many: a plugin gives at most {MaximumScripts} scripts";
            if (problem is null)
            {
                kept.Add(script);
                continue;
            }

            diagnostics.Add(new PluginRuntimeDiagnostic
            {
                Severity = PluginDiagnosticSeverity.Warning,
                Source = PluginRuntimeDiagnosticSource.Contribution,
                Message = $"The script '{script.Name}' {problem}, so it is left out.",
                RuntimeKey = descriptor.RuntimeKey,
                Metadata = new Dictionary<string, string> { ["Point"] = PluginPoint.Script.ToString(), ["NaturalName"] = script.Name ?? string.Empty },
            });
        }

        return kept;
    }
}
