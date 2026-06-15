using System.Globalization;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Styling;
using XenoAtom.Ansi;

namespace CodeAlta.Presentation.Formatting;

internal static class FileChangeSummaryFormatter
{
    public static string BuildFileNameMarkup(FileChangeEntryState entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var fileName = Path.GetFileName(entry.FilePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = entry.FilePath;
        }

        return new StringBuilder()
            .Append("[bold]")
            .Append(AnsiMarkup.Escape(fileName))
            .Append("[/]")
            .ToString();
    }

    public static string BuildDirectoryMarkup(FileChangeEntryState entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var directory = Path.GetDirectoryName(entry.FilePath)?.Replace('\\', '/');
        return string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : $"[dim]{AnsiMarkup.Escape(directory)}[/]";
    }

    public static string BuildCountsMarkup(FileChangeEntryState entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var builder = new StringBuilder();
        DiffDisplayFormatter.AppendChangeCountsMarkup(builder, entry.Additions, entry.Deletions);
        return builder.ToString();
    }

    public static string BuildGroupSummaryMarkup(FileChangeGroupState group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var builder = new StringBuilder()
            .Append('[')
            .Append(UiPalette.MutedMarkup)
            .Append(']')
            .Append(group.Files.Count.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(SR.T("file(s)"))
            .Append(" · ");
        DiffDisplayFormatter.AppendChangeCountsMarkup(builder, group.TotalAdditions, group.TotalDeletions);
        builder.Append("[/]");
        return builder.ToString();
    }

    public static string BuildDetailMarkdown(FileChangeEntryState entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var builder = new StringBuilder()
            .Append("- ").Append(SR.T("File")).Append(": `").Append(entry.FilePath).AppendLine("`")
            .Append("- ").Append(SR.T("Operation")).Append(": ").AppendLine(GetOperationLabel(entry.Operation))
            .Append("- ").Append(SR.T("First Seen")).Append(": `").Append(FormatTimestamp(entry.FirstSeenAt)).AppendLine("`")
            .Append("- ").Append(SR.T("Last Updated")).Append(": `").Append(FormatTimestamp(entry.LastUpdatedAt)).AppendLine("`")
            .Append("- ").Append(SR.T("Additions")).Append(": `").Append(entry.Additions.ToString(CultureInfo.InvariantCulture)).AppendLine("`")
            .Append("- ").Append(SR.T("Deletions")).Append(": `").Append(entry.Deletions.ToString(CultureInfo.InvariantCulture)).AppendLine("`");

        if (string.IsNullOrWhiteSpace(entry.DiffText))
        {
            builder.AppendLine().Append('_').Append(SR.T("Diff unavailable for this file.")).Append('_');
        }

        return builder.ToString();
    }

    public static string BuildStatsMarkup(FileChangeEntryState entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var builder = new StringBuilder("[dim]");
        DiffDisplayFormatter.AppendChangeCountsMarkup(builder, entry.Additions, entry.Deletions);
        builder.Append(" · ").Append(GetOperationLabel(entry.Operation)).Append("[/]");
        return builder.ToString();
    }

    public static string GetDiffLineMarkup(string line)
        => DiffDisplayFormatter.GetDiffLineMarkup(line);

    private static string GetOperationLabel(FileChangeOperation operation)
    {
        return operation switch
        {
            FileChangeOperation.Created => SR.T("Created"),
            FileChangeOperation.Deleted => SR.T("Deleted"),
            FileChangeOperation.Modified => SR.T("Modified"),
            _ => SR.T("Changed"),
        };
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
}
