using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;

namespace CodeAlta.Plugin.Statistics;

internal static class StatisticsTerminalContributions
{
    internal static PluginDerivedSessionEvent DecorateProjection(PluginDerivedSessionEvent projection, StatisticsPresentation turn)
        => new PluginTerminalDerivedSessionEvent
        {
            EventId = projection.EventId,
            Timestamp = projection.Timestamp,
            Markdown = projection.Markdown,
            RenderTarget = projection.RenderTarget,
            Payload = projection.Payload,
            DynamicContent = projection.DynamicContent,
            Remove = projection.Remove,
            DetailSections = projection.DetailSections.Select(section => new PluginTerminalDerivedSessionEventDetailSection
            {
                Header = section.Header,
                Markdown = section.Markdown,
                VisualFactory = _ => StatisticsVisualRenderer.RenderTurnDetails(turn),
            }).ToArray(),
            VisualFactory = _ => StatisticsVisualRenderer.RenderTurnCard(turn),
        };
}
