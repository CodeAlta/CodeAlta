using System.Text.Json;
using System.Text;

namespace CodeAlta.Agent.LocalRuntime.Compaction;

internal static class LocalAgentCompactionSummarizer
{
    public static LocalAgentCompactionResult Summarize(
        LocalAgentCompactionPreparation preparation,
        IReadOnlyList<AgentEvent> history,
        string? latestUserRequest,
        long? tokensAfter)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(history);

        var fileActivity = ExtractFileActivity(history);
        var summary = BuildStructuredSummary(preparation, latestUserRequest, fileActivity);
        return new LocalAgentCompactionResult(
            Summary: summary,
            AnchorContentId: preparation.AnchorContentId,
            IsSplitTurn: preparation.IsSplitTurn,
            TokensBefore: preparation.TokensBefore.Tokens,
            TokensAfter: tokensAfter,
            MessagesSummarized: preparation.MessagesToSummarize.Count,
            ReadFiles: fileActivity.ReadFiles,
            ModifiedFiles: fileActivity.ModifiedFiles);
    }

    private static string BuildStructuredSummary(
        LocalAgentCompactionPreparation preparation,
        string? latestUserRequest,
        FileActivity fileActivity)
    {
        var serializedMessages = LocalAgentCompactionSerializer.SerializeForSummary(preparation.MessagesToSummarize);
        var builder = new StringBuilder();
        builder.AppendLine("## Objective");
        builder.AppendLine(ExtractObjective(preparation.PreviousSummary, latestUserRequest));
        builder.AppendLine();
        builder.AppendLine("## Active User Request");
        builder.AppendLine(string.IsNullOrWhiteSpace(latestUserRequest) ? "(none recorded)" : latestUserRequest.Trim());
        builder.AppendLine();
        builder.AppendLine("## Constraints");
        builder.AppendLine(ExtractSection(preparation.PreviousSummary, "## Constraints") ?? "- Preserve existing behavior unless the specs require a change.");
        builder.AppendLine();
        builder.AppendLine("## Progress");
        builder.AppendLine("### Done");
        builder.AppendLine(BuildProgressList(preparation.MessagesToSummarize, successOnly: true));
        builder.AppendLine("### In Progress");
        builder.AppendLine(BuildInProgressList(preparation.MessagesToKeep));
        builder.AppendLine("### Blocked");
        builder.AppendLine(BuildBlockedList(preparation.MessagesToSummarize));
        builder.AppendLine();
        builder.AppendLine("## Decisions");
        builder.AppendLine(ExtractSection(preparation.PreviousSummary, "## Decisions") ?? "- None recorded.");
        builder.AppendLine();
        builder.AppendLine("## Next Steps");
        builder.AppendLine(BuildNextSteps(latestUserRequest, preparation.MessagesToKeep));
        builder.AppendLine();
        builder.AppendLine("## Critical Context");
        builder.AppendLine(string.IsNullOrWhiteSpace(serializedMessages) ? "(none)" : serializedMessages);
        builder.AppendLine();
        builder.AppendLine("## Relevant Files");
        builder.AppendLine(BuildRelevantFiles(fileActivity));
        return builder.ToString().Trim();
    }

    private static string ExtractObjective(string? previousSummary, string? latestUserRequest)
        => ExtractSection(previousSummary, "## Objective")
           ?? (string.IsNullOrWhiteSpace(latestUserRequest) ? "Continue the current coding task." : latestUserRequest.Trim());

    private static string BuildProgressList(
        IReadOnlyList<LocalAgentConversationMessage> messages,
        bool successOnly)
    {
        var entries = new List<string>();
        foreach (var message in messages)
        {
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case LocalAgentMessagePart.Text text when !string.IsNullOrWhiteSpace(text.Value):
                        entries.Add("- " + Condense(text.Value));
                        break;
                    case LocalAgentMessagePart.ToolResult toolResult when toolResult.Result.Success == successOnly:
                        entries.Add("- " + Condense(RenderToolResult(toolResult.Result)));
                        break;
                }
            }
        }

        return entries.Count == 0 ? "- None recorded." : string.Join(Environment.NewLine, entries.Distinct(StringComparer.Ordinal));
    }

    private static string BuildInProgressList(IReadOnlyList<LocalAgentConversationMessage> keptMessages)
    {
        var latestAssistantText = keptMessages
            .Reverse()
            .SelectMany(static message => message.Parts.OfType<LocalAgentMessagePart.Text>())
            .Select(static part => part.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        return string.IsNullOrWhiteSpace(latestAssistantText)
            ? "- Continue from the preserved recent suffix."
            : "- " + Condense(latestAssistantText);
    }

    private static string BuildBlockedList(IReadOnlyList<LocalAgentConversationMessage> messages)
    {
        var blocked = messages
            .SelectMany(static message => message.Parts.OfType<LocalAgentMessagePart.ToolResult>())
            .Where(static result => !result.Result.Success)
            .Select(static result => "- " + Condense(RenderToolResult(result.Result)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return blocked.Length == 0 ? "- None recorded." : string.Join(Environment.NewLine, blocked);
    }

    private static string BuildNextSteps(string? latestUserRequest, IReadOnlyList<LocalAgentConversationMessage> keptMessages)
    {
        if (!string.IsNullOrWhiteSpace(latestUserRequest))
        {
            return "- Continue executing the latest user request verbatim.\n- Use the preserved recent suffix for immediate context.";
        }

        return keptMessages.Count == 0
            ? "- Continue the active task from the saved checkpoint."
            : "- Continue from the most recent preserved messages.";
    }

    private static string BuildRelevantFiles(FileActivity fileActivity)
    {
        if (fileActivity.ReadFiles.Count == 0 && fileActivity.ModifiedFiles.Count == 0)
        {
            return "- None tracked.";
        }

        var builder = new StringBuilder();
        if (fileActivity.ReadFiles.Count > 0)
        {
            builder.AppendLine("### Read");
            foreach (var path in fileActivity.ReadFiles)
            {
                builder.AppendLine("- " + path);
            }
        }

        if (fileActivity.ModifiedFiles.Count > 0)
        {
            builder.AppendLine("### Modified");
            foreach (var path in fileActivity.ModifiedFiles)
            {
                builder.AppendLine("- " + path);
            }
        }

        return builder.ToString().Trim();
    }

    private static FileActivity ExtractFileActivity(IReadOnlyList<AgentEvent> history)
    {
        var readFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var modifiedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var activity in history.OfType<AgentActivityEvent>())
        {
            if (activity.Kind is not AgentActivityKind.ToolCall || activity.Details is not { } details)
            {
                continue;
            }

            AddPaths(details, "readFiles", readFiles);
            AddPaths(details, "modifiedFiles", modifiedFiles);
        }

        return new FileActivity([.. readFiles], [.. modifiedFiles]);
    }

    private static void AddPaths(JsonElement details, string propertyName, ISet<string> target)
    {
        if (!details.TryGetProperty(propertyName, out var property) || property.ValueKind is not JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in property.EnumerateArray())
        {
            var path = item.GetString();
            if (!string.IsNullOrWhiteSpace(path))
            {
                target.Add(path);
            }
        }
    }

    private static string? ExtractSection(string? markdown, string heading)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var start = markdown.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += heading.Length;
        var nextHeading = markdown.IndexOf("\n## ", start, StringComparison.Ordinal);
        var section = nextHeading < 0 ? markdown[start..] : markdown[start..nextHeading];
        var trimmed = section.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string Condense(string value)
    {
        var condensed = string.Join(" ", value.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return condensed.Length <= 280 ? condensed : condensed[..277] + "...";
    }

    private static string RenderToolResult(AgentToolResult result)
    {
        var segments = result.Items.Select(static item => item switch
        {
            AgentToolResultItem.Text text => text.Value,
            AgentToolResultItem.ImageUrl imageUrl => imageUrl.Url,
            _ => string.Empty,
        });
        var rendered = string.Join(Environment.NewLine, segments.Where(static value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(rendered) ? (result.Error ?? "(no output)") : rendered;
    }

    private sealed record FileActivity(IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles);
}
