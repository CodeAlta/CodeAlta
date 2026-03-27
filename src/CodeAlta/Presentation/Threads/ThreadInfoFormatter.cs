using System.Globalization;
using System.Text;

namespace CodeAlta.Presentation.Threads;

internal static class ThreadInfoFormatter
{
    public static string BuildBodyMarkdown(ThreadInfoReport? report, bool isLoading, string? errorMessage)
    {
        if (isLoading)
        {
            return "Loading thread information from the active backend and history.";
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            return $"Failed to load thread information: {errorMessage}";
        }

        return report is null
            ? "No thread is currently selected."
            : BuildMarkdown(report, includeTitle: false);
    }

    public static string BuildMarkdown(ThreadInfoReport report, bool includeTitle = true)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        if (includeTitle)
        {
            builder.Append("# ")
                .Append(report.ThreadTitle)
                .AppendLine(" thread info");
        }

        AppendOverview(builder, report);
        AppendTiming(builder, report);
        AppendConversation(builder, report);
        AppendStorage(builder, report.StorageLocation);
        AppendBackendFacts(builder, report.BackendFacts);

        return builder.ToString().TrimEnd();
    }

    public static string BuildSubtitle(ThreadInfoReport? report, bool isLoading)
    {
        if (report is not null)
        {
            return $"{report.BackendName} · {report.ThreadTitle}";
        }

        return isLoading
            ? "Fetching current thread details."
            : "Current selected thread.";
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

    private static void AppendOverview(StringBuilder builder, ThreadInfoReport report)
    {
        StartSection(builder, "Overview");
        builder.Append("- Backend: ").AppendLine(report.BackendName);
        builder.Append("- Session ID: `").Append(report.BackendSessionId).AppendLine("`");
        builder.Append("- Working directory: `").Append(report.WorkingDirectory).AppendLine("`");
        builder.Append("- Model: ").AppendLine(report.ModelName ?? "(default model)");
        builder.Append("- Reasoning: ").AppendLine(report.ReasoningEffort?.ToString() ?? "(default)");
    }

    private static void AppendTiming(StringBuilder builder, ThreadInfoReport report)
    {
        StartSection(builder, "Timing");
        builder.Append("- Backend created: ").AppendLine(FormatTimestamp(report.CreatedAt));
        builder.Append("- Conversation started: ").AppendLine(FormatTimestamp(report.StartedAt));
        builder.Append("- Last backend update: ").AppendLine(FormatTimestamp(report.LastUpdatedAt));
        builder.Append("- Elapsed: ").AppendLine(FormatElapsed(report.Elapsed));
    }

    private static void AppendConversation(StringBuilder builder, ThreadInfoReport report)
    {
        StartSection(builder, "Conversation");
        builder.Append("- User prompts: ").AppendLine(FormatCount(report.UserMessageCount));
        builder.Append("- Assistant messages: ").AppendLine(FormatCount(report.AssistantMessageCount));
        builder.Append("- Total messages: ").AppendLine(
            FormatCount(report.UserMessageCount is { } userCount && report.AssistantMessageCount is { } assistantCount
                ? userCount + assistantCount
                : null));
    }

    private static void AppendStorage(StringBuilder builder, ThreadInfoStorageLocation? storageLocation)
    {
        StartSection(builder, "Storage");
        if (storageLocation is null)
        {
            builder.AppendLine("- Backend path: Not exposed by the backend.");
            return;
        }

        builder.Append("- Backend path: `").Append(storageLocation.Path).AppendLine("`");
        builder.Append("- Path kind: ").AppendLine(storageLocation.Kind switch
        {
            ThreadInfoStorageKind.File => "File",
            ThreadInfoStorageKind.Directory => "Directory",
            ThreadInfoStorageKind.MissingPath => "Missing on disk",
            _ => "Unknown",
        });

        if (storageLocation.SizeBytes is { } sizeBytes)
        {
            builder.Append("- File size: ").AppendLine(FormatFileSize(sizeBytes));
        }
    }

    private static void AppendBackendFacts(StringBuilder builder, IReadOnlyList<ThreadInfoFact> backendFacts)
    {
        if (backendFacts.Count == 0)
        {
            return;
        }

        StartSection(builder, "Backend-specific details");
        foreach (var fact in backendFacts)
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
