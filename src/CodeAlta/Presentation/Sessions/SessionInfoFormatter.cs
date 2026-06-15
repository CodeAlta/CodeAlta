using System.Globalization;
using System.Text;
using CodeAlta.Catalog;

namespace CodeAlta.Presentation.Sessions;

internal static class SessionInfoFormatter
{
    public static string BuildBodyMarkdown(SessionInfoReport? report, bool isLoading, string? errorMessage)
    {
        if (isLoading)
        {
            return SR.T("Loading session information from the active provider and history.");
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            return SR.T("Failed to load session information: {0}", errorMessage);
        }

        return report is null
            ? SR.T("No session is currently selected.")
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
                .AppendLine(SR.T(" session info"));
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
            ? SR.T("Fetching current session details.")
            : SR.T("Current selected session.");
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
        StartSection(builder, SR.T("Overview"));
        AppendMarkdownLabel(builder, SR.T("Provider"), report.ProviderName);
        builder.Append("- ").Append(SR.T("Session ID")).Append(": `").Append(report.SessionId).AppendLine("`");
        builder.Append("- ").Append(SR.T("Working directory")).Append(": `").Append(report.WorkingDirectory).AppendLine("`");
        AppendMarkdownLabel(builder, SR.T("Model"), report.ModelName ?? SR.T("(default model)"));
        AppendMarkdownLabel(builder, SR.T("Reasoning"), report.ReasoningEffort?.ToString() ?? SR.T("(default)"));
    }

    private static void AppendTiming(StringBuilder builder, SessionInfoReport report)
    {
        StartSection(builder, SR.T("Timing"));
        AppendMarkdownLabel(builder, SR.T("Provider session created"), FormatTimestamp(report.CreatedAt));
        AppendMarkdownLabel(builder, SR.T("Conversation started"), FormatTimestamp(report.StartedAt));
        AppendMarkdownLabel(builder, SR.T("Last provider update"), FormatTimestamp(report.LastUpdatedAt));
        AppendMarkdownLabel(builder, SR.T("Elapsed"), FormatElapsed(report.Elapsed));
    }

    private static void AppendConversation(StringBuilder builder, SessionInfoReport report)
    {
        StartSection(builder, SR.T("Conversation"));
        AppendMarkdownLabel(builder, SR.T("User prompts"), FormatCount(report.UserMessageCount));
        AppendMarkdownLabel(builder, SR.T("Assistant messages"), FormatCount(report.AssistantMessageCount));
        AppendMarkdownLabel(builder, SR.T("Total messages"),
            FormatCount(report.UserMessageCount is { } userCount && report.AssistantMessageCount is { } assistantCount
                ? userCount + assistantCount
                : null));
    }

    private static void AppendStorage(StringBuilder builder, SessionInfoStorageLocation? storageLocation)
    {
        StartSection(builder, SR.T("Storage"));
        if (storageLocation is null)
        {
            AppendMarkdownLabel(builder, SR.T("Session path"), SR.T("Not exposed by the provider."));
            return;
        }

        builder.Append("- ").Append(SR.T("Session path")).Append(": `").Append(storageLocation.Path).AppendLine("`");
        AppendMarkdownLabel(builder, SR.T("Path kind"), storageLocation.Kind switch
        {
            SessionInfoStorageKind.File => SR.T("File"),
            SessionInfoStorageKind.Directory => SR.T("Directory"),
            SessionInfoStorageKind.MissingPath => SR.T("Missing on disk"),
            _ => SR.T("Unknown"),
        });

        if (storageLocation.SizeBytes is { } sizeBytes)
        {
            AppendMarkdownLabel(builder, SR.T("File size"), FormatFileSize(sizeBytes));
        }
    }

    private static void AppendLoadedSkills(StringBuilder builder, IReadOnlyList<CodeAlta.Agent.Runtime.AgentLoadedSkillState> loadedSkills)
    {
        if (loadedSkills.Count == 0)
        {
            return;
        }

        StartSection(builder, SR.T("Loaded skills"));
        foreach (var skill in loadedSkills)
        {
            builder.Append("- `")
                .Append(skill.Name)
                .Append("`");
            if (!string.IsNullOrWhiteSpace(skill.SourceKind))
            {
                builder.Append(" · ").Append(skill.SourceKind);
            }

            builder.Append(" · ").Append(SR.T("activated")).Append(' ')
                .AppendLine(FormatTimestamp(skill.ActivatedAt));
            builder.Append("  - ").Append(SR.T("Path")).Append(": `").Append(skill.SkillFilePath).AppendLine("`");
            builder.Append("  - ").Append(SR.T("Mode")).Append(": ").AppendLine(skill.ActivationMode);
            builder.Append("  - ").Append(SR.T("Status")).Append(": ").AppendLine(skill.IsAvailable ? SR.T("Available") : SR.T("Missing ({0})", skill.MissingReason));
            if (skill.RestoredFromHistory)
            {
                builder.Append("  - ").Append(SR.T("Restore source")).Append(": ").AppendLine(SR.T("Session history"));
            }
        }
    }

    private static void AppendProviderFacts(StringBuilder builder, IReadOnlyList<SessionInfoFact> providerFacts)
    {
        if (providerFacts.Count == 0)
        {
            return;
        }

        StartSection(builder, SR.T("Provider-specific details"));
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
        => count?.ToString(CultureInfo.InvariantCulture) ?? SR.T("Unavailable");

    private static void AppendMarkdownLabel(StringBuilder builder, string label, string value)
        => builder.Append("- ").Append(label).Append(": ").AppendLine(value);
}
