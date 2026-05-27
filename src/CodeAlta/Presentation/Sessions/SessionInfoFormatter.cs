using System.Globalization;
using System.Text;

namespace CodeAlta.Presentation.Sessions;

internal static class SessionInfoFormatter
{
    public static string BuildBodyMarkdown(SessionInfoReport? report, bool isLoading, string? errorMessage)
    {
        if (isLoading)
        {
            return "Loading session information from the active provider and history.";
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            return $"Failed to load session information: {errorMessage}";
        }

        return report is null
            ? "No session is currently selected."
            : BuildMarkdown(report, includeTitle: false);
    }

    public static string BuildMarkdown(SessionInfoReport report, bool includeTitle = true)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        if (includeTitle)
        {
            builder.Append("# ")
                .Append(report.SessionTitle)
                .AppendLine(" session info");
        }

        AppendOverview(builder, report);
        AppendTiming(builder, report);
        AppendConversation(builder, report);
        AppendLoadedSkills(builder, report.LoadedSkills);
        AppendStorage(builder, report.StorageLocation);
        AppendProviderFacts(builder, report.ProviderFacts);

        return builder.ToString().TrimEnd();
    }

    public static string BuildSubtitle(SessionInfoReport? report, bool isLoading)
    {
        if (report is not null)
        {
            return $"{report.ProviderName} · {report.SessionTitle}";
        }

        return isLoading
            ? "Fetching current session details."
            : "Current selected session.";
    }

    public static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalHours >= 1)
        {
            return FormattableString.Invariant($"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s");
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return FormattableString.Invariant($"{elapsed.Minutes}m {elapsed.Seconds}s");
        }

        return FormattableString.Invariant($"{Math.Max(0, (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero))}s");
    }

    public static string FormatFileSize(long sizeBytes)
    {
        const double kib = 1024d;
        const double mib = kib * 1024d;

        if (sizeBytes >= mib)
        {
            return FormattableString.Invariant($"{sizeBytes / mib:0.0} MiB");
        }

        if (sizeBytes >= kib)
        {
            return FormattableString.Invariant($"{sizeBytes / kib:0.0} KiB");
        }

        return FormattableString.Invariant($"{sizeBytes} B");
    }

    private static void AppendOverview(StringBuilder builder, SessionInfoReport report)
    {
        StartSection(builder, "Overview");
        builder.Append("- Provider: ").AppendLine(report.ProviderName);
        builder.Append("- Session ID: `").Append(report.SessionId).AppendLine("`");
        builder.Append("- Working directory: `").Append(report.WorkingDirectory).AppendLine("`");
        builder.Append("- Model: ").AppendLine(report.ModelName ?? "(default model)");
        builder.Append("- Reasoning: ").AppendLine(report.ReasoningEffort?.ToString() ?? "(default)");
    }

    private static void AppendTiming(StringBuilder builder, SessionInfoReport report)
    {
        StartSection(builder, "Timing");
        builder.Append("- Provider session created: ").AppendLine(FormatTimestamp(report.CreatedAt));
        builder.Append("- Conversation started: ").AppendLine(FormatTimestamp(report.StartedAt));
        builder.Append("- Last provider update: ").AppendLine(FormatTimestamp(report.LastUpdatedAt));
        builder.Append("- Elapsed: ").AppendLine(FormatElapsed(report.Elapsed));
    }

    private static void AppendConversation(StringBuilder builder, SessionInfoReport report)
    {
        StartSection(builder, "Conversation");
        builder.Append("- User prompts: ").AppendLine(FormatCount(report.UserMessageCount));
        builder.Append("- Assistant messages: ").AppendLine(FormatCount(report.AssistantMessageCount));
        builder.Append("- Total messages: ").AppendLine(
            FormatCount(report.UserMessageCount is { } userCount && report.AssistantMessageCount is { } assistantCount
                ? userCount + assistantCount
                : null));
    }

    private static void AppendStorage(StringBuilder builder, SessionInfoStorageLocation? storageLocation)
    {
        StartSection(builder, "Storage");
        if (storageLocation is null)
        {
            builder.AppendLine("- Session path: Not exposed by the provider.");
            return;
        }

        builder.Append("- Session path: `").Append(storageLocation.Path).AppendLine("`");
        builder.Append("- Path kind: ").AppendLine(storageLocation.Kind switch
        {
            SessionInfoStorageKind.File => "File",
            SessionInfoStorageKind.Directory => "Directory",
            SessionInfoStorageKind.MissingPath => "Missing on disk",
            _ => "Unknown",
        });

        if (storageLocation.SizeBytes is { } sizeBytes)
        {
            builder.Append("- File size: ").AppendLine(FormatFileSize(sizeBytes));
        }
    }

    private static void AppendLoadedSkills(StringBuilder builder, IReadOnlyList<CodeAlta.Agent.Runtime.AgentLoadedSkillState> loadedSkills)
    {
        if (loadedSkills.Count == 0)
        {
            return;
        }

        StartSection(builder, "Loaded skills");
        foreach (var skill in loadedSkills)
        {
            builder.Append("- `")
                .Append(skill.Name)
                .Append("`");
            if (!string.IsNullOrWhiteSpace(skill.SourceKind))
            {
                builder.Append(" · ").Append(skill.SourceKind);
            }

            builder.Append(" · activated ")
                .AppendLine(FormatTimestamp(skill.ActivatedAt));
            builder.Append("  - Path: `").Append(skill.SkillFilePath).AppendLine("`");
            builder.Append("  - Mode: ").AppendLine(skill.ActivationMode);
            builder.Append("  - Status: ").AppendLine(skill.IsAvailable ? "Available" : $"Missing ({skill.MissingReason})");
            if (skill.RestoredFromHistory)
            {
                builder.AppendLine("  - Restore source: Session history");
            }
        }
    }

    private static void AppendProviderFacts(StringBuilder builder, IReadOnlyList<SessionInfoFact> providerFacts)
    {
        if (providerFacts.Count == 0)
        {
            return;
        }

        StartSection(builder, "Provider-specific details");
        foreach (var fact in providerFacts)
        {
            builder.Append("- ")
                .Append(fact.Label)
                .Append(": ")
                .AppendLine(fact.Value);
        }
    }

    private static void StartSection(StringBuilder builder, string title)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append("## ")
            .AppendLine(title);
    }

    private static string FormatCount(int? count)
        => count?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable";
}
