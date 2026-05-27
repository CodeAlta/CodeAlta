namespace CodeAlta.Agent.Runtime.Compaction;

/// <summary>
/// Normalized agent-runtime compaction settings.
/// </summary>
/// <param name="Enabled">Whether automatic threshold compaction is enabled.</param>
/// <param name="Ratio">The active-context/input-limit ratio that triggers automatic compaction.</param>
/// <param name="KeepLastUserMessage">Whether the latest user message should be preserved as an anchor.</param>
/// <param name="AllowSplitTurn">Whether compaction may split a large turn while preserving continuation state.</param>
public sealed record AgentCompactionSettings(
    bool Enabled,
    double Ratio,
    bool KeepLastUserMessage,
    bool AllowSplitTurn)
{
    /// <summary>
    /// The default enabled value.
    /// </summary>
    public const bool DefaultEnabled = true;

    /// <summary>
    /// The default compaction trigger ratio.
    /// </summary>
    public const double DefaultRatio = 0.95;

    /// <summary>
    /// The default keep-last-user-message value.
    /// </summary>
    public const bool DefaultKeepLastUserMessage = true;

    /// <summary>
    /// The default allow-split-turn value.
    /// </summary>
    public const bool DefaultAllowSplitTurn = true;

    /// <summary>
    /// The default maximum summarizer output budget as a ratio of the input-context limit.
    /// </summary>
    public const double DefaultSummaryOutputRatio = 0.10;

    /// <summary>
    /// The default preferred post-compaction active-context budget as a ratio of the input-context limit.
    /// </summary>
    public const double DefaultPostCompactionTargetRatio = 0.10;

    /// <summary>
    /// The default share of the preferred post-compaction target offered to summary generation.
    /// </summary>
    public const double DefaultSummaryShareOfTarget = 0.40;

    /// <summary>
    /// The default share of the summary target available for model-visible file context.
    /// </summary>
    public const double DefaultFileContextShareOfSummaryTarget = 0.15;

    /// <summary>
    /// The largest configurable summarizer output budget ratio.
    /// </summary>
    public const double MaxSummaryOutputRatio = 0.50;

    /// <summary>
    /// The largest configurable preferred post-compaction target ratio.
    /// </summary>
    public const double MaxPostCompactionTargetRatio = 1.00;

    /// <summary>
    /// The largest configurable summary share of the post-compaction target.
    /// </summary>
    public const double MaxSummaryShareOfTarget = 1.00;

    /// <summary>
    /// The largest configurable file-context share of the summary target.
    /// </summary>
    public const double MaxFileContextShareOfSummaryTarget = 1.00;

    /// <summary>
    /// Gets or initializes the maximum summarizer output budget as a ratio of the input-context limit.
    /// </summary>
    public double SummaryOutputRatio { get; init; } = DefaultSummaryOutputRatio;

    /// <summary>
    /// Gets or initializes the preferred post-compaction active-context target as a ratio of the input-context limit.
    /// </summary>
    public double PostCompactionTargetRatio { get; init; } = DefaultPostCompactionTargetRatio;

    /// <summary>
    /// Gets or initializes the desired summary output share of the preferred post-compaction target.
    /// </summary>
    public double SummaryShareOfTarget { get; init; } = DefaultSummaryShareOfTarget;

    /// <summary>
    /// Gets or initializes the desired model-visible file-context share of the summary target.
    /// </summary>
    public double FileContextShareOfSummaryTarget { get; init; } = DefaultFileContextShareOfSummaryTarget;

    /// <summary>
    /// Gets the runtime defaults.
    /// </summary>
    public static AgentCompactionSettings Default { get; } = new(
        Enabled: DefaultEnabled,
        Ratio: DefaultRatio,
        KeepLastUserMessage: DefaultKeepLastUserMessage,
        AllowSplitTurn: DefaultAllowSplitTurn);
}
