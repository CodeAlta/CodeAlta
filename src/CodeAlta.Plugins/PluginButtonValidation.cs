using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>Keeps the buttons of a plugin that are valid and within the limits of their place.</summary>
internal static class PluginButtonValidation
{
    /// <summary>
    /// Passes every UI contribution that is not a button, and the buttons that are valid: one per identifier, and no more at a
    /// place than <see cref="PluginButtonLimits.For"/> allows. The others are left out and each one adds a warning.
    /// </summary>
    /// <param name="descriptor">The plugin that returned the contributions.</param>
    /// <param name="contributions">What the plugin returned.</param>
    /// <param name="diagnostics">Receives a warning for each button that is left out.</param>
    /// <returns>The contributions to register, in their order.</returns>
    internal static IReadOnlyList<PluginUiContribution> Filter(PluginDescriptor descriptor, IEnumerable<PluginUiContribution> contributions, List<PluginRuntimeDiagnostic> diagnostics)
    {
        var kept = new List<PluginUiContribution>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<PluginButtonPlace, int>();
        foreach (var contribution in contributions)
        {
            if (contribution is not PluginButtonContribution button)
            {
                kept.Add(contribution);
                continue;
            }

            var problem = button.Validate();
            if (problem is null && !ids.Add(button.Id)) problem = "repeats the identifier of another button of the plugin";
            if (problem is null)
            {
                counts.TryGetValue(button.Place, out var count);
                if (count >= PluginButtonLimits.For(button.Place)) problem = $"is one too many for {button.Place}: a plugin has at most {PluginButtonLimits.For(button.Place)} there";
                else counts[button.Place] = count + 1;
            }

            if (problem is null)
            {
                kept.Add(button);
                continue;
            }

            diagnostics.Add(new PluginRuntimeDiagnostic
            {
                Severity = PluginDiagnosticSeverity.Warning,
                Source = PluginRuntimeDiagnosticSource.Contribution,
                Message = $"The button '{button.Id}' ({button.Place}) {problem}, so it is left out.",
                RuntimeKey = descriptor.RuntimeKey,
                Metadata = new Dictionary<string, string> { ["Point"] = PluginPoint.Ui.ToString(), ["NaturalName"] = button.Id },
            });
        }

        return kept;
    }
}
