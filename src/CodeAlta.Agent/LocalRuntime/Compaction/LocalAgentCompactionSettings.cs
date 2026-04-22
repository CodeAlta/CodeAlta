namespace CodeAlta.Agent.LocalRuntime.Compaction;

/// <summary>
/// Normalized local-runtime compaction settings.
/// </summary>
public sealed record LocalAgentCompactionSettings(
    bool Enabled,
    double TriggerThreshold,
    double TargetThreshold,
    int ReservedOutputTokens,
    int ReservedOverheadTokens,
    bool KeepLastUserMessage,
    bool AllowSplitTurn)
{
    /// <summary>
    /// The default enabled value.
    /// </summary>
    public const bool DefaultEnabled = true;

    /// <summary>
    /// The default trigger threshold.
    /// </summary>
    public const double DefaultTriggerThreshold = 0.85;

    /// <summary>
    /// The default target threshold.
    /// </summary>
    public const double DefaultTargetThreshold = 0.50;

    /// <summary>
    /// The default reserved output-token budget.
    /// </summary>
    public const int DefaultReservedOutputTokens = 4096;

    /// <summary>
    /// The default reserved overhead-token budget.
    /// </summary>
    public const int DefaultReservedOverheadTokens = 2048;

    /// <summary>
    /// The default keep-last-user-message value.
    /// </summary>
    public const bool DefaultKeepLastUserMessage = true;

    /// <summary>
    /// The default allow-split-turn value.
    /// </summary>
    public const bool DefaultAllowSplitTurn = true;

    /// <summary>
    /// The default ideal post-compaction context ratio.
    /// </summary>
    public const double DefaultTargetContextRatioIdeal = 0.03;

    /// <summary>
    /// The default maximum post-compaction context ratio.
    /// </summary>
    public const double DefaultTargetContextRatioMax = 0.10;

    /// <summary>
    /// The default recent-suffix token target.
    /// </summary>
    public const int DefaultRecentSuffixTargetTokens = 20_000;

    /// <summary>
    /// The default summary output-token budget.
    /// </summary>
    public const int DefaultSummaryOutputTokens = 1_024;

    /// <summary>
    /// The default summary input-token budget.
    /// </summary>
    public const int DefaultSummaryInputTokens = 24_000;

    /// <summary>
    /// The default per-item tool-result character cap.
    /// </summary>
    public const int DefaultToolResultCharsPerItem = 1_200;

    /// <summary>
    /// The default total tool-result character cap.
    /// </summary>
    public const int DefaultToolResultCharsTotal = 6_000;

    /// <summary>
    /// The default per-item reasoning character cap.
    /// </summary>
    public const int DefaultReasoningCharsPerItem = 600;

    /// <summary>
    /// The default total reasoning character cap.
    /// </summary>
    public const int DefaultReasoningCharsTotal = 3_000;

    /// <summary>
    /// The default reasoning retention mode.
    /// </summary>
    public const LocalAgentCompactionReasoningMode DefaultReasoningMode = LocalAgentCompactionReasoningMode.Adaptive;

    /// <summary>
    /// The default maximum chunk-pass count.
    /// </summary>
    public const int DefaultMaxChunkPasses = 4;

    /// <summary>
    /// The default oversized-anchor reduction value.
    /// </summary>
    public const bool DefaultAllowOversizedAnchorReduction = true;

    /// <summary>
    /// The default prefer-recent-messages value.
    /// </summary>
    public const bool DefaultPreferRecentMessages = true;

    /// <summary>
    /// The default prefer-recent-tool-outputs value.
    /// </summary>
    public const bool DefaultPreferRecentToolOutputs = true;

    /// <summary>
    /// The default drop-messages-only-when-summary-input-exceeds-budget value.
    /// </summary>
    public const bool DefaultDropMessagesOnlyWhenSummaryInputExceedsBudget = true;

    /// <summary>
    /// Gets or initializes the ideal post-compaction context ratio.
    /// </summary>
    public double TargetContextRatioIdeal { get; init; } = DefaultTargetContextRatioIdeal;

    /// <summary>
    /// Gets or initializes the preferred maximum post-compaction context ratio.
    /// </summary>
    public double TargetContextRatioMax { get; init; } = DefaultTargetContextRatioMax;

    /// <summary>
    /// Gets or initializes the preferred token budget for the retained recent suffix.
    /// </summary>
    public int RecentSuffixTargetTokens { get; init; } = DefaultRecentSuffixTargetTokens;

    /// <summary>
    /// Gets or initializes the maximum output tokens allowed for the summarizer call.
    /// </summary>
    public int SummaryOutputTokens { get; init; } = DefaultSummaryOutputTokens;

    /// <summary>
    /// Gets or initializes the maximum input tokens allowed for a single summarizer request.
    /// </summary>
    public int SummaryInputTokens { get; init; } = DefaultSummaryInputTokens;

    /// <summary>
    /// Gets or initializes the per-item tool-result excerpt cap.
    /// </summary>
    public int ToolResultCharsPerItem { get; init; } = DefaultToolResultCharsPerItem;

    /// <summary>
    /// Gets or initializes the total tool-result excerpt cap.
    /// </summary>
    public int ToolResultCharsTotal { get; init; } = DefaultToolResultCharsTotal;

    /// <summary>
    /// Gets or initializes the per-item reasoning excerpt cap.
    /// </summary>
    public int ReasoningCharsPerItem { get; init; } = DefaultReasoningCharsPerItem;

    /// <summary>
    /// Gets or initializes the total reasoning excerpt cap.
    /// </summary>
    public int ReasoningCharsTotal { get; init; } = DefaultReasoningCharsTotal;

    /// <summary>
    /// Gets or initializes how reasoning should be retained during compaction.
    /// </summary>
    public LocalAgentCompactionReasoningMode ReasoningMode { get; init; } = DefaultReasoningMode;

    /// <summary>
    /// Gets or initializes the maximum recursive chunking passes.
    /// </summary>
    public int MaxChunkPasses { get; init; } = DefaultMaxChunkPasses;

    /// <summary>
    /// Gets or initializes whether the latest oversized anchor may be reduced.
    /// </summary>
    public bool AllowOversizedAnchorReduction { get; init; } = DefaultAllowOversizedAnchorReduction;

    /// <summary>
    /// Gets or initializes whether recency should be favored when trimming messages.
    /// </summary>
    public bool PreferRecentMessages { get; init; } = DefaultPreferRecentMessages;

    /// <summary>
    /// Gets or initializes whether recent tool outputs should be favored when trimming excerpts.
    /// </summary>
    public bool PreferRecentToolOutputs { get; init; } = DefaultPreferRecentToolOutputs;

    /// <summary>
    /// Gets or initializes whether message dropping is allowed only after the summary-input budget is exceeded.
    /// </summary>
    public bool DropMessagesOnlyWhenSummaryInputExceedsBudget { get; init; } = DefaultDropMessagesOnlyWhenSummaryInputExceedsBudget;

    /// <summary>
    /// Gets the runtime defaults.
    /// </summary>
    public static LocalAgentCompactionSettings Default { get; } = new(
        Enabled: DefaultEnabled,
        TriggerThreshold: DefaultTriggerThreshold,
        TargetThreshold: DefaultTargetThreshold,
        ReservedOutputTokens: DefaultReservedOutputTokens,
        ReservedOverheadTokens: DefaultReservedOverheadTokens,
        KeepLastUserMessage: DefaultKeepLastUserMessage,
        AllowSplitTurn: DefaultAllowSplitTurn)
    {
        TargetContextRatioIdeal = DefaultTargetContextRatioIdeal,
        TargetContextRatioMax = DefaultTargetContextRatioMax,
        RecentSuffixTargetTokens = DefaultRecentSuffixTargetTokens,
        SummaryOutputTokens = DefaultSummaryOutputTokens,
        SummaryInputTokens = DefaultSummaryInputTokens,
        ToolResultCharsPerItem = DefaultToolResultCharsPerItem,
        ToolResultCharsTotal = DefaultToolResultCharsTotal,
        ReasoningCharsPerItem = DefaultReasoningCharsPerItem,
        ReasoningCharsTotal = DefaultReasoningCharsTotal,
        ReasoningMode = DefaultReasoningMode,
        MaxChunkPasses = DefaultMaxChunkPasses,
        AllowOversizedAnchorReduction = DefaultAllowOversizedAnchorReduction,
        PreferRecentMessages = DefaultPreferRecentMessages,
        PreferRecentToolOutputs = DefaultPreferRecentToolOutputs,
        DropMessagesOnlyWhenSummaryInputExceedsBudget = DefaultDropMessagesOnlyWhenSummaryInputExceedsBudget,
    };
}
