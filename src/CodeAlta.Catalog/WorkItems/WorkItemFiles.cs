using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAlta.Catalog.WorkItems;

/// <summary>
/// Reads and writes the files of tasks (<c>.alta/tasks/*.md</c>) and plans (<c>.alta/plans/*.md</c>).
/// </summary>
public static partial class WorkItemFiles
{
    /// <summary>The folder of the tasks, under the folder of a project.</summary>
    public const string TasksFolder = ".alta/tasks";

    /// <summary>The folder of the plans, under the folder of a project.</summary>
    public const string PlansFolder = ".alta/plans";

    /// <summary>The most characters of the body of a task.</summary>
    public const int MaximumTaskBody = 16 * 1024;

    /// <summary>The most characters of a title.</summary>
    public const int MaximumTitle = 160;

    /// <summary>The most characters of a summary.</summary>
    public const int MaximumSummary = 600;

    // A plan can be very long: what describes it is at its top.
    private const int PlanHeadLength = 16 * 1024;
    private const int MaximumTaskFile = 256 * 1024;
    private const int MaximumSlug = 60;

    /// <summary>The kinds a task can have, the default one last.</summary>
    public static IReadOnlyList<string> TaskKinds { get; } = ["gap", "problem", "improvement"];

    /// <summary>Gets the folder of the tasks of a project.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>The full path of the folder, which may not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="projectPath"/> is blank.</exception>
    public static string TasksDirectory(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        return Path.Combine(projectPath, ".alta", "tasks");
    }

    /// <summary>Gets the folder of the plans of a project.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>The full path of the folder, which may not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="projectPath"/> is blank.</exception>
    public static string PlansDirectory(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        return Path.Combine(projectPath, ".alta", "plans");
    }

    /// <summary>Whether a text can be the id of an item: the name of a file, without a folder and without its extension.</summary>
    /// <param name="id">The text.</param>
    /// <returns>True for letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, starting with a letter or a digit, up to 160 characters.</returns>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 160 || !char.IsAsciiLetterOrDigit(id[0]) || id.EndsWith('.'))
        {
            return false;
        }

        foreach (var character in id)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Names a new file after the day and a title, as plans are: <c>yyyy-MM-dd-the-title</c>.</summary>
    /// <param name="day">The day.</param>
    /// <param name="title">The title.</param>
    /// <returns>An id that <see cref="IsValidId"/> accepts.</returns>
    public static string NewId(DateOnly day, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var slug = new StringBuilder(MaximumSlug);
        foreach (var character in title.Normalize(NormalizationForm.FormD))
        {
            if (slug.Length >= MaximumSlug)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(char.ToLowerInvariant(character));
            }
            else if (slug.Length > 0 && slug[^1] != '-' && CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                slug.Append('-');
            }
        }

        var name = slug.ToString().Trim('-');
        return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + (name.Length == 0 ? "task" : name);
    }

    /// <summary>Reads a task from the text of its file.</summary>
    /// <param name="path">The full path of the file, which names the task.</param>
    /// <param name="text">The text of the file.</param>
    /// <returns>The task. A file without a front matter is a pending task whose title is its first heading.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WorkTask ParseTask(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);
        var (values, body) = PromptFileFormat.SplitFrontmatter(text);
        var id = Path.GetFileNameWithoutExtension(path);
        body = body.Trim();
        var kind = Clean(values.GetValueOrDefault("kind"))?.ToLowerInvariant();
        return new WorkTask(
            id,
            Clean(values.GetValueOrDefault("title")) ?? FirstHeading(body) ?? id,
            Clean(values.GetValueOrDefault("summary")),
            kind is not null && TaskKinds.Contains(kind) ? kind : TaskKinds[^1],
            ParseTaskStatus(values.GetValueOrDefault("status")),
            ParseDay(values.GetValueOrDefault("created")) ?? DayOfName(id),
            path,
            body);
    }

    /// <summary>Writes the text of the file of a task.</summary>
    /// <param name="title">The title.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="status">The status.</param>
    /// <param name="created">The day it was written, as <c>yyyy-MM-dd</c>.</param>
    /// <param name="summary">The summary, if any.</param>
    /// <param name="body">The Markdown body.</param>
    /// <returns>The text: a front matter, then the body.</returns>
    public static string SerializeTask(string title, string kind, WorkTaskStatus status, string created, string? summary, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(created);
        ArgumentNullException.ThrowIfNull(body);
        var text = new StringBuilder(body.Length + 256);
        text.Append("---\n");
        text.Append("title: ").Append(Quote(title)).Append('\n');
        text.Append("kind: ").Append(kind).Append('\n');
        text.Append("status: ").Append(NameOf(status)).Append('\n');
        text.Append("created: ").Append(created).Append('\n');
        if (Clean(summary) is { } line)
        {
            text.Append("summary: ").Append(Quote(line)).Append('\n');
        }

        text.Append("---\n\n").Append(body.ReplaceLineEndings("\n").Trim()).Append('\n');
        return text.ToString();
    }

    /// <summary>Reads what describes a plan from the text of its file, or from the start of it.</summary>
    /// <param name="path">The full path of the file, which names the plan.</param>
    /// <param name="text">The text of the file; its first thousands of characters are enough.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WorkPlan ParsePlan(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);
        var id = Path.GetFileNameWithoutExtension(path);
        var (values, body) = PromptFileFormat.SplitFrontmatter(text);
        var hasFrontMatter = !ReferenceEquals(body, text) || values.Count > 0;
        // A plan written before the front matter says the same things in a list under its title.
        var rawStatus = Clean(values.GetValueOrDefault("status")) ?? LegacyField(body, "Status");
        var status = ParsePlanStatus(rawStatus);
        return new WorkPlan(
            id,
            Clean(values.GetValueOrDefault("title")) ?? FirstHeading(body) ?? id,
            Clean(values.GetValueOrDefault("summary")) ?? Bound(LegacyField(body, "Task"), MaximumSummary),
            status,
            SaysMore(rawStatus, status) ? Bound(rawStatus, MaximumSummary) : null,
            ParseDay(values.GetValueOrDefault("created")) ?? ParseDay(LegacyField(body, "Created")) ?? DayOfName(id),
            path,
            hasFrontMatter);
    }

    /// <summary>Removes the front matter of a file, which a reader of the document does not need.</summary>
    /// <param name="text">The text of the file.</param>
    /// <returns>The Markdown under the front matter; the whole text when there is none.</returns>
    public static string Body(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PromptFileFormat.SplitFrontmatter(text).Body.TrimStart('\n', '\r');
    }

    /// <summary>
    /// Gives a file another status, changing nothing else: the <c>status</c> of its front matter, or the
    /// <c>- Status:</c> line of a plan written before the front matter. A file that has neither gets a front matter.
    /// </summary>
    /// <param name="text">The text of the file.</param>
    /// <param name="status">The name of the status.</param>
    /// <returns>The new text.</returns>
    /// <exception cref="ArgumentException"><paramref name="status"/> is blank.</exception>
    public static string WithStatus(string text, string status)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length > 0 && lines[0] == "---" && Array.IndexOf(lines, "---", 1) is var end and > 0)
        {
            for (var index = 1; index < end; index++)
            {
                if (lines[index].TrimStart().StartsWith("status:", StringComparison.OrdinalIgnoreCase))
                {
                    lines[index] = "status: " + status;
                    return string.Join(newline, lines);
                }
            }

            return string.Join(newline, lines[..end].Append("status: " + status).Concat(lines[end..]));
        }

        for (var index = 0; index < lines.Length && index < 40; index++)
        {
            var match = LegacyFieldLine().Match(lines[index]);
            if (match.Success && string.Equals(match.Groups["name"].Value, "Status", StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = match.Groups["lead"].Value + char.ToUpperInvariant(status[0]) + status[1..].Replace('-', ' ');
                return string.Join(newline, lines);
            }
        }

        return "---" + newline + "status: " + status + newline + "---" + newline + newline + string.Join(newline, lines);
    }

    /// <summary>Gets the name of the status of a task in its file.</summary>
    /// <param name="status">The status.</param>
    /// <returns><c>pending</c>, <c>later</c>, <c>done</c> or <c>dismissed</c>.</returns>
    public static string NameOf(WorkTaskStatus status)
        => status switch
        {
            WorkTaskStatus.Later => "later",
            WorkTaskStatus.Done => "done",
            WorkTaskStatus.Dismissed => "dismissed",
            _ => "pending",
        };

    /// <summary>Gets the name of the status of a plan in its file.</summary>
    /// <param name="status">The status.</param>
    /// <returns><c>draft</c>, <c>approved</c>, <c>in-progress</c>, <c>done</c> or <c>blocked</c>.</returns>
    public static string NameOf(WorkPlanStatus status)
        => status switch
        {
            WorkPlanStatus.Approved => "approved",
            WorkPlanStatus.InProgress => "in-progress",
            WorkPlanStatus.Done => "done",
            WorkPlanStatus.Blocked => "blocked",
            _ => "draft",
        };

    /// <summary>Reads the status of a task by its name; what is not known is a pending task.</summary>
    /// <param name="name">The name as written.</param>
    /// <returns>The status.</returns>
    public static WorkTaskStatus ParseTaskStatus(string? name)
        => Normalized(name) switch
        {
            "later" or "parked" => WorkTaskStatus.Later,
            "done" or "completed" or "complete" => WorkTaskStatus.Done,
            "dismissed" => WorkTaskStatus.Dismissed,
            _ => WorkTaskStatus.Pending,
        };

    /// <summary>
    /// Reads the status of a plan from what its file says, which for a plan written by hand or by an older
    /// prompt is a sentence: its first words decide.
    /// </summary>
    /// <param name="text">The status as written.</param>
    /// <returns>The status; a draft when nothing is recognized.</returns>
    public static WorkPlanStatus ParsePlanStatus(string? text)
    {
        var value = Normalized(text);
        if (value.Length == 0)
        {
            return WorkPlanStatus.Draft;
        }

        if (StartsWithAny(value, "done", "complete", "implemented", "finished", "shipped", "merged"))
        {
            return WorkPlanStatus.Done;
        }

        if (StartsWithAny(value, "blocked", "stopped", "on hold"))
        {
            return WorkPlanStatus.Blocked;
        }

        if (StartsWithAny(value, "in progress", "inprogress", "executing", "started", "ongoing", "partial") || value.Contains("in progress", StringComparison.Ordinal)
            || value.Contains("resumed", StringComparison.Ordinal))
        {
            return WorkPlanStatus.InProgress;
        }

        if (StartsWithAny(value, "approved", "ready", "accepted"))
        {
            return WorkPlanStatus.Approved;
        }

        return WorkPlanStatus.Draft;
    }

    /// <summary>Reads the name of the status of a plan, exactly.</summary>
    /// <param name="name">A name of <see cref="NameOf(WorkPlanStatus)"/>.</param>
    /// <returns>The status, or null when the name is not one.</returns>
    public static WorkPlanStatus? PlanStatusOf(string? name)
        => name?.Trim().ToLowerInvariant() switch
        {
            "draft" => WorkPlanStatus.Draft,
            "approved" or "ready" => WorkPlanStatus.Approved,
            "in-progress" or "in_progress" or "inprogress" => WorkPlanStatus.InProgress,
            "done" => WorkPlanStatus.Done,
            "blocked" => WorkPlanStatus.Blocked,
            _ => null,
        };

    internal static string? ReadHead(string path, bool plan)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!plan && stream.Length > MaximumTaskFile)
            {
                return null;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (!plan)
            {
                return reader.ReadToEnd();
            }

            var buffer = new char[PlanHeadLength];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // One word is another name of the status ("Completed"); a sentence says something the status does not.
    private static bool SaysMore(string? rawStatus, WorkPlanStatus status)
        => rawStatus is not null && rawStatus.Trim().Contains(' ') && !string.Equals(Normalized(rawStatus), Normalized(NameOf(status)), StringComparison.Ordinal);

    private static string Normalized(string? value)
        => value is null ? string.Empty : value.Trim().TrimStart('*', '_', '`').ToLowerInvariant().Replace('-', ' ').Replace('_', ' ');

    private static bool StartsWithAny(string value, params ReadOnlySpan<string> starts)
    {
        foreach (var start in starts)
        {
            if (value.StartsWith(start, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var line = value.ReplaceLineEndings(" ").Trim();
        return line.Length == 0 ? null : line;
    }

    private static string? Bound(string? value, int maximum)
        => value is null || value.Length <= maximum ? value : value[..(maximum - 1)].TrimEnd() + "…";

    private static string Quote(string value) => "\"" + JsonEncodedText.Encode(value.ReplaceLineEndings(" ").Trim(), System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";

    private static string? FirstHeading(string body)
    {
        foreach (var raw in body.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                return Clean(line[2..].ToString().Trim('#', ' '));
            }

            if (!line.IsEmpty && !line.StartsWith("<!--", StringComparison.Ordinal))
            {
                break;
            }
        }

        return null;
    }

    // "- Status: Approved" or "- **Status**: Approved", near the top of the file.
    private static string? LegacyField(string body, string name)
    {
        var seen = 0;
        foreach (var raw in body.AsSpan().EnumerateLines())
        {
            if (++seen > 40)
            {
                break;
            }

            var match = LegacyFieldLine().Match(raw.ToString());
            if (match.Success && string.Equals(match.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                return Clean(match.Groups["value"].Value.Trim('`'));
            }
        }

        return null;
    }

    private static string? ParseDay(string? value)
        => Clean(value) is { } text && text.Length >= 10
            && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    private static string? DayOfName(string id) => ParseDay(id);

    [GeneratedRegex(@"^(?<lead>\s*[-*]\s*\**(?<name>Status|Created|Task)\**\s*:\**\s*)(?<value>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyFieldLine();
}
