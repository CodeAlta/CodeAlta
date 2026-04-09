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
    /// Gets the runtime defaults.
    /// </summary>
    public static LocalAgentCompactionSettings Default { get; } = new(
        Enabled: true,
        TriggerThreshold: 0.80,
        TargetThreshold: 0.50,
        ReservedOutputTokens: 4096,
        ReservedOverheadTokens: 2048,
        KeepLastUserMessage: true,
        AllowSplitTurn: true);
}

