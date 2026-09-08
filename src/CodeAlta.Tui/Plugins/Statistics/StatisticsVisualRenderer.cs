using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Plugin.Statistics;

internal static class StatisticsVisualRenderer
{
    public static Visual RenderTurnCard(StatisticsPresentation turn)
        => new Collapsible(
            CreateHeaderMarkup(RenderTurnSummaryMarkup(turn)),
            RenderTurnDetails(turn))
        {
            IsExpanded = false,
        };

    public static Visual RenderTurnDetails(StatisticsPresentation turn)
    {
        var tables = new List<Visual>
        {
            CreateTable(turn.RenderMetricTable()),
        };

        var usageTable = turn.RenderUsageTable();
        if (!string.IsNullOrWhiteSpace(usageTable))
        {
            tables.Add(CreateTable(usageTable));
        }

        var toolBucketTable = turn.RenderToolBucketTable();
        if (!string.IsNullOrWhiteSpace(toolBucketTable))
        {
            tables.Add(CreateTable(toolBucketTable));
        }

        return new WrapHStack(tables.ToArray())
            .Spacing(1)
            .RunSpacing(1)
            .MeasureMode(WrapMeasureMode.ConstrainToRun)
            .HorizontalAlignment(Align.Stretch);
    }

    private static string RenderTurnSummaryMarkup(StatisticsPresentation turn)
        => $"[bold]{turn.Title}[/]{AnsiMarkup.Escape(turn.RenderTurnSummarySuffix())}";

    private static Markup CreateHeaderMarkup(string markup)
        => new(markup)
        {
            Wrap = false,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Start,
        };

    private static Visual CreateTable(string markdown)
        => new MarkdownControl(markdown.Trim())
        {
            HorizontalAlignment = Align.Start,
            VerticalAlignment = Align.Start,
            Options = MarkdownRenderOptions.Default with
            {
                TableStyle = TableStyle.Minimal,
                WrapCodeBlocks = true,
                MaxCodeBlockHeight = 14,
            },
        };
}
