using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>Keeps the canvases of a plugin whose placement the window can serve.</summary>
internal static class PluginCanvasValidation
{
    /// <summary>
    /// Passes the canvases that can be shown where they say: a settings page is of scope <see cref="PluginCanvasScope.Application"/>, and a plugin
    /// has at most one. The others are left out and each one adds a warning.
    /// </summary>
    /// <param name="descriptor">The plugin that returned the canvases.</param>
    /// <param name="canvases">What the plugin returned.</param>
    /// <param name="diagnostics">Receives a warning for each canvas that is left out.</param>
    /// <returns>The canvases to register, in their order.</returns>
    internal static IReadOnlyList<PluginCanvasContribution> Filter(PluginDescriptor descriptor, IEnumerable<PluginCanvasContribution> canvases, List<PluginRuntimeDiagnostic> diagnostics)
    {
        var kept = new List<PluginCanvasContribution>();
        string? settings = null;
        foreach (var canvas in canvases)
        {
            if (canvas is null) continue;
            string? problem = canvas.Placement switch
            {
                PluginCanvasPlacement.Tab => null,
                PluginCanvasPlacement.Settings when canvas.Scope != PluginCanvasScope.Application => "is a settings page of scope " + canvas.Scope + ": a settings page is of scope Application",
                PluginCanvasPlacement.Settings when settings is not null => $"is a second settings page: the plugin has one already, '{settings}'",
                PluginCanvasPlacement.Settings => null,
                _ => "has a placement that the application does not know",
            };
            if (problem is null)
            {
                if (canvas.Placement == PluginCanvasPlacement.Settings) settings = canvas.Id;
                kept.Add(canvas);
                continue;
            }

            diagnostics.Add(new PluginRuntimeDiagnostic
            {
                Severity = PluginDiagnosticSeverity.Warning,
                Source = PluginRuntimeDiagnosticSource.Contribution,
                Message = $"The canvas '{canvas.Id}' {problem}, so it is left out.",
                RuntimeKey = descriptor.RuntimeKey,
                Metadata = new Dictionary<string, string> { ["Point"] = PluginPoint.Canvas.ToString(), ["NaturalName"] = canvas.Id ?? string.Empty },
            });
        }

        return kept;
    }
}
