namespace CodeAlta.Plugin.Statistics;

/// <summary>Deferred portable formatters over one already-built turn, borrowed by host presentation.</summary>
internal sealed record StatisticsPresentation(
    string Title,
    Func<string> RenderTurnSummarySuffix,
    Func<string> RenderMetricTable,
    Func<string> RenderUsageTable,
    Func<string> RenderToolBucketTable);
