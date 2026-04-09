using System.Text;

namespace CodeAlta.Agent.LocalRuntime.Compaction;

/// <summary>
/// Represents a persisted local compaction checkpoint.
/// </summary>
public sealed record LocalAgentCompactionCheckpoint
{
    /// <summary>
    /// Gets or initializes the checkpoint schema version.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Gets or initializes the checkpoint content identifier.
    /// </summary>
    public required string ContentId { get; init; }

    /// <summary>
    /// Gets or initializes the compaction trigger.
    /// </summary>
    public required string Trigger { get; init; }

    /// <summary>
    /// Gets or initializes the summary text.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Gets or initializes the first kept event offset when known.
    /// </summary>
    public long? FirstKeptEventOffset { get; init; }

    /// <summary>
    /// Gets or initializes the anchor content identifier when known.
    /// </summary>
    public string? AnchorContentId { get; init; }

    /// <summary>
    /// Gets or initializes the token count before compaction.
    /// </summary>
    public long TokensBefore { get; init; }

    /// <summary>
    /// Gets or initializes the token count after compaction when known.
    /// </summary>
    public long? TokensAfter { get; init; }

    /// <summary>
    /// Gets or initializes the summarized message count.
    /// </summary>
    public int SummarizedMessageCount { get; init; }

    /// <summary>
    /// Gets or initializes the verbatim kept suffix captured at compaction time.
    /// </summary>
    public IReadOnlyList<LocalAgentConversationMessage> KeptMessages { get; init; } = [];

    /// <summary>
    /// Gets or initializes the best-effort tracked read files.
    /// </summary>
    public IReadOnlyList<string> ReadFiles { get; init; } = [];

    /// <summary>
    /// Gets or initializes the best-effort tracked modified files.
    /// </summary>
    public IReadOnlyList<string> ModifiedFiles { get; init; } = [];

    /// <summary>
    /// Creates the synthetic replay message for the checkpoint.
    /// </summary>
    /// <returns>The replay message.</returns>
    public LocalAgentConversationMessage CreateMessage()
        => new(
            LocalAgentConversationRole.User,
            [new LocalAgentMessagePart.Text(WrapSummary(Summary))]);

    /// <summary>
    /// Wraps summary text in the canonical checkpoint envelope.
    /// </summary>
    /// <param name="summary">The summary text.</param>
    /// <returns>The wrapped text.</returns>
    public static string WrapSummary(string summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<codealta-compaction-checkpoint version="1">""");
        builder.AppendLine(summary.Trim());
        builder.Append("""</codealta-compaction-checkpoint>""");
        return builder.ToString();
    }

    /// <summary>
    /// Attempts to extract a wrapped summary from a replay message.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    /// <returns>The unwrapped summary when present; otherwise <see langword="null" />.</returns>
    public static string? TryExtractSummary(LocalAgentConversationMessage message)
    {
        if (message.Role is not LocalAgentConversationRole.User)
        {
            return null;
        }

        var text = message.Parts.OfType<LocalAgentMessagePart.Text>().Select(static part => part.Value).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        const string prefix = """<codealta-compaction-checkpoint version="1">""";
        const string suffix = "</codealta-compaction-checkpoint>";
        var start = text.IndexOf(prefix, StringComparison.Ordinal);
        var end = text.LastIndexOf(suffix, StringComparison.Ordinal);
        if (start < 0 || end < 0 || end <= start)
        {
            return null;
        }

        start += prefix.Length;
        var summary = text[start..end].Trim();
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }
}
