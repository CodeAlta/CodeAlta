using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpYaml;

namespace CodeAlta.Catalog.WorkItems;

/// <summary>The tags of tasks: the <c>tags</c> line of a task's front matter, and the tags file of a project.</summary>
public static partial class WorkItemFiles
{
    /// <summary>The tags file of a project, under its folder.</summary>
    public const string TagsFile = ".alta/tags.yaml";

    /// <summary>The most characters of a tag name.</summary>
    public const int MaximumTagName = 32;

    /// <summary>The most characters of the description of a tag.</summary>
    public const int MaximumTagDescription = 200;

    /// <summary>The most tags of one task.</summary>
    public const int MaximumTaskTags = 10;

    /// <summary>The most tags of a project, listed or found on its tasks.</summary>
    public const int MaximumTags = 200;

    /// <summary>Gets the tags file of a project.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>The full path of the file, which may not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="projectPath"/> is blank.</exception>
    public static string TagsPath(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        return Path.Combine(projectPath, ".alta", "tags.yaml");
    }

    /// <summary>Whether a text is a tag name: lower case ASCII letters and digits, in words joined by single hyphens.</summary>
    /// <param name="name">The text.</param>
    /// <returns>True for a name such as <c>flaky-tests</c> of at most <see cref="MaximumTagName"/> characters.</returns>
    public static bool IsTagName(ReadOnlySpan<char> name)
    {
        if (name.Length is 0 or > MaximumTagName || name[0] == '-' || name[^1] == '-')
        {
            return false;
        }

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (character == '-' ? name[index - 1] == '-' : !char.IsAsciiDigit(character) && !char.IsAsciiLetterLower(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads a tag name as a person may type it: trimmed, in lower case, without a leading <c>#</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The tag name; null when the text is not one.</returns>
    public static string? TagNameOf(string? text)
    {
        var name = text?.Trim().TrimStart('#').ToLowerInvariant();
        return name is not null && IsTagName(name) ? name : null;
    }

    /// <summary>Reads the tags of a task from the value of its <c>tags</c> line: <c>[a, b]</c> or <c>a, b</c>.</summary>
    /// <param name="value">The value; null when the task has no such line.</param>
    /// <returns>The valid tag names, each once, in their order, at most <see cref="MaximumTaskTags"/>.</returns>
    public static IReadOnlyList<string> ParseTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var list = value.Trim();
        if (list.StartsWith('[') && list.EndsWith(']'))
        {
            list = list[1..^1];
        }

        var tags = new List<string>();
        foreach (var item in list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (TagNameOf(item.Trim('"', '\'')) is { } name && !tags.Contains(name) && tags.Count < MaximumTaskTags)
            {
                tags.Add(name);
            }
        }

        return tags;
    }

    /// <summary>
    /// Gives the file of a task other tags, changing nothing else: its <c>tags</c> line, written as a flow list, or
    /// removed when there are none. A file without a front matter gets one.
    /// </summary>
    /// <param name="text">The text of the file.</param>
    /// <param name="tags">The tag names.</param>
    /// <returns>The new text.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A tag is not a tag name, or there are more than <see cref="MaximumTaskTags"/>.</exception>
    public static string WithTags(string text, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = TagsLine(tags);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.ReplaceLineEndings("\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0] == "---" && lines.IndexOf("---", 1) is var end and > 0)
        {
            var at = lines.FindIndex(1, end - 1, static current => current.TrimStart().StartsWith("tags:", StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                if (line is not null)
                {
                    lines.Insert(end, line);
                }

                return string.Join(newline, lines);
            }

            // A block list goes on in the lines that start with "- " under the key.
            var count = 1;
            while (at + count < end && lines[at + count].TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                count++;
            }

            lines.RemoveRange(at, count);
            if (line is not null)
            {
                lines.Insert(at, line);
            }

            return string.Join(newline, lines);
        }

        return line is null ? text : "---" + newline + line + newline + "---" + newline + newline + string.Join(newline, lines);
    }

    // The tags line of a front matter; null for no tags.
    private static string? TagsLine(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count > MaximumTaskTags)
        {
            throw new ArgumentException($"A task has at most {MaximumTaskTags} tags.", nameof(tags));
        }

        foreach (var tag in tags)
        {
            if (!IsTagName(tag))
            {
                throw new ArgumentException($"'{tag}' is not a tag name: lower case letters, digits and single hyphens, at most {MaximumTagName} characters.", nameof(tags));
            }
        }

        var distinct = tags.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 0 ? null : "tags: [" + string.Join(", ", distinct) + "]";
    }

    /// <summary>Reads the tags file of a project.</summary>
    /// <param name="text">The text of the file.</param>
    /// <param name="tags">The tags it lists with their descriptions, in its order; a name that is not a tag name is left out.</param>
    /// <param name="declined">The names the user declined.</param>
    /// <returns>False when the text is not a tags file, whose tags are then not known.</returns>
    public static bool TryParseTagsFile(string text, out IReadOnlyList<KeyValuePair<string, string?>> tags, out IReadOnlyList<string> declined)
    {
        ArgumentNullException.ThrowIfNull(text);
        tags = [];
        declined = [];
        TagsDocument? document;
        try
        {
            document = string.IsNullOrWhiteSpace(text) ? new TagsDocument() : YamlSerializer.Deserialize<TagsDocument>(text);
        }
        catch (Exception exception) when (exception is YamlException or InvalidOperationException or FormatException or JsonException)
        {
            return false;
        }

        var listed = new List<KeyValuePair<string, string?>>();
        foreach (var (key, description) in document?.Tags ?? [])
        {
            if (TagNameOf(key) is { } name && listed.TrueForAll(tag => tag.Key != name) && listed.Count < MaximumTags)
            {
                listed.Add(new(name, Clean(description) is { } line ? Bound(line.ReplaceLineEndings(" "), MaximumTagDescription) : null));
            }
        }

        tags = listed;
        declined = [.. (document?.Declined ?? []).Select(TagNameOf).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaximumTags)];
        return true;
    }

    /// <summary>Writes the text of the tags file of a project.</summary>
    /// <param name="tags">The tags with their descriptions, in their order.</param>
    /// <param name="declined">The names the user declined.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string SerializeTagsFile(IReadOnlyList<KeyValuePair<string, string?>> tags, IReadOnlyList<string> declined)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(declined);
        var text = new StringBuilder();
        text.Append("# The tags of the tasks of this project (.alta/tasks): a name, then what the tag is for, in a line.\n");
        text.Append("tags:");
        if (tags.Count == 0)
        {
            text.Append(" {}");
        }

        text.Append('\n');
        foreach (var (name, description) in tags)
        {
            text.Append("  ").Append(name).Append(':');
            if (Clean(description) is { } line)
            {
                text.Append(' ').Append(Quote(line));
            }

            text.Append('\n');
        }

        if (declined.Count > 0)
        {
            text.Append("# The names agents proposed and the user declined: no agent proposes them again.\n");
            text.Append("declined: [").Append(string.Join(", ", declined)).Append("]\n");
        }

        return text.ToString();
    }

    private sealed class TagsDocument
    {
        [JsonPropertyName("tags")]
        public Dictionary<string, string?>? Tags { get; set; }

        [JsonPropertyName("declined")]
        public List<string>? Declined { get; set; }
    }
}
