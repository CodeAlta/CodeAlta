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
}
