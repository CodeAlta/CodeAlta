using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>Keeps the landing cards of a plugin that are valid and within the limit of a plugin.</summary>
internal static class PluginLandingCardValidation
{
    /// <summary>
    /// Passes the cards that are valid: one per identifier, and no more than <see cref="PluginLandingCardLimits.Cards"/>. The others are
    /// left out and each one adds a warning.
    /// </summary>
    /// <param name="descriptor">The plugin that returned the cards.</param>
    /// <param name="cards">What the plugin returned.</param>
    /// <param name="diagnostics">Receives a warning for each card that is left out.</param>
    /// <returns>The cards to register, in their order.</returns>
    internal static IReadOnlyList<PluginLandingCardContribution> Filter(PluginDescriptor descriptor, IEnumerable<PluginLandingCardContribution> cards, List<PluginRuntimeDiagnostic> diagnostics)
    {
        var kept = new List<PluginLandingCardContribution>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (card is null) continue;
            var problem = card.Validate();
            if (problem is null && !ids.Add(card.Id)) problem = "repeats the identifier of another card of the plugin";
            if (problem is null && kept.Count >= PluginLandingCardLimits.Cards) problem = $"is one too many: a plugin pins at most {PluginLandingCardLimits.Cards} cards";
            if (problem is null)
            {
                kept.Add(card);
                continue;
            }

            diagnostics.Add(new PluginRuntimeDiagnostic
            {
                Severity = PluginDiagnosticSeverity.Warning,
                Source = PluginRuntimeDiagnosticSource.Contribution,
                Message = $"The landing card '{card.Id}' {problem}, so it is left out.",
                RuntimeKey = descriptor.RuntimeKey,
                Metadata = new Dictionary<string, string> { ["Point"] = PluginPoint.LandingCard.ToString(), ["NaturalName"] = card.Id },
            });
        }

        return kept;
    }
}
