using System.Globalization;
using System.Text;
using CodeAlta.Agent;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace CodeAlta.Desktop.Automations;

/// <summary>
/// The automations of one configuration file: each is a table <c>[automations.&lt;guid&gt;]</c>. Reading is on its
/// own, apart from the settings of the application, so that an automation written wrong is reported and nothing
/// else; writing replaces the text of one table and leaves every other character of the file as it is.
/// </summary>
internal static class AutomationConfig
{
    /// <summary>The name of the table that holds the automations.</summary>
    internal const string TableName = "automations";

    /// <summary>The largest configuration file read for its automations, in UTF-16 units.</summary>
    internal const int MaximumContentLength = 1024 * 1024;

    /// <summary>The most automations read from one file.</summary>
    internal const int MaximumAutomations = 64;

    /// <summary>Reads the automations of a configuration file.</summary>
    /// <param name="content">The text of the file.</param>
    /// <param name="source">The file, for what is reported.</param>
    /// <returns>The automations that can run, and what is wrong with the others.</returns>
    internal static (IReadOnlyList<AutomationDefinition> Definitions, IReadOnlyList<AutomationFault> Faults) Read(string content, AutomationSource source)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(source);
        if (content.Length > MaximumContentLength)
            return ([], [new(source, string.Empty, "The configuration file is too large to read its automations.")]);
        if (!content.Contains(TableName, StringComparison.Ordinal)) return ([], []);
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(content) ?? [];
        }
        catch (TomlException exception)
        {
            return ([], [new(source, string.Empty, "The configuration file cannot be read: " + FirstLine(exception.Message))]);
        }

        if (!root.TryGetValue(TableName, out var value)) return ([], []);
        if (value is not TomlTable tables) return ([], [new(source, string.Empty, "automations is not a table of automations.")]);
        var definitions = new List<AutomationDefinition>();
        var faults = new List<AutomationFault>();
        foreach (var (key, entry) in tables)
        {
            if (definitions.Count + faults.Count == MaximumAutomations)
            {
                faults.Add(new(source, string.Empty, $"Only the first {MaximumAutomations} automations of a file are read."));
                break;
            }

            if (!Guid.TryParseExact(key, "D", out var id)) faults.Add(new(source, key, "The key of an automation is a GUID, such as " + AutomationDefinition.NewId() + "."));
            else if (entry is not TomlTable table) faults.Add(new(source, key, "An automation is a table."));
            else if (definitions.Any(definition => definition.Id == id.ToString("D"))) faults.Add(new(source, key, "Another automation of this file has the same identifier."));
            else if (TryRead(id.ToString("D"), table, source, out var definition, out var problem)) definitions.Add(definition!);
            else faults.Add(new(source, key, problem!));
        }

        return (definitions, faults);
    }

    /// <summary>Checks a definition the way reading it back would, before it is written.</summary>
    /// <returns>What is wrong with it, or null.</returns>
    internal static string? Validate(AutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!AutomationDefinition.IsId(definition.Id)) return "The identifier of an automation is a GUID.";
        if (string.IsNullOrWhiteSpace(definition.Name)) return "An automation has a name.";
        if (definition.Name.Length > AutomationDefinition.MaximumNameLength || definition.Name.Any(char.IsControl)) return $"The name of an automation is one line of at most {AutomationDefinition.MaximumNameLength} characters.";
        if (string.IsNullOrWhiteSpace(definition.Prompt)) return "An automation has a prompt.";
        if (definition.Prompt.Length > AutomationDefinition.MaximumPromptLength) return $"The prompt of an automation has at most {AutomationDefinition.MaximumPromptLength} characters.";
        if (definition.Prompt.Any(static character => char.IsControl(character) && character is not ('\n' or '\t')) || !IsWellFormed(definition.Prompt))
            return "The prompt of an automation is plain text.";
        if (definition.Project is { } project && (project.Length is 0 or > 4096 || project.Any(char.IsControl))) return "The project of an automation is the path of its folder.";
        if (definition.Agent is { } agent && (agent.Length is 0 or > 256 || agent.Any(char.IsControl))) return "The agent prompt of an automation is a name.";
        if (definition.Triggers.Count > AutomationDefinition.MaximumTriggers) return $"An automation has at most {AutomationDefinition.MaximumTriggers} triggers.";
        foreach (var trigger in definition.Triggers)
        {
            if (trigger.IsCommand)
            {
                if (string.IsNullOrWhiteSpace(trigger.Command)) return "A command trigger has a command.";
                if (trigger.Command.Length > AutomationTrigger.MaximumCommandLength || trigger.Command.Any(char.IsControl) || !IsWellFormed(trigger.Command))
                    return $"The command of a trigger is one line of at most {AutomationTrigger.MaximumCommandLength} characters.";
                if (trigger.Folder is { } folder && (string.IsNullOrWhiteSpace(folder) || folder.Length > AutomationTrigger.MaximumFolderLength || folder.Any(char.IsControl) || !IsWellFormed(folder)))
                    return "The folder of a command trigger is a path.";
            }
            else if (trigger.IsSchedule)
            {
                if (!AutomationSchedule.TryCreate(trigger, out _, out var error)) return error;
                if (trigger.Expression is { Length: > AutomationTrigger.MaximumExpressionLength }) return "The cron expression is too long.";
            }
            else if (trigger.IsTracker)
            {
                if (trigger.Event is not ("created" or "updated")) return "A Jira trigger starts when an issue is created or updated.";
            }
            else if (trigger.Event is not ("opened" or "updated") || trigger is { Kind: AutomationTriggerKind.Issue, Event: "updated" })
            {
                return trigger.Kind == AutomationTriggerKind.Issue ? "An issue trigger starts when an issue is opened." : "A pull request trigger starts when one is opened or updated.";
            }
        }

        return definition.Triggers.Select(static trigger => trigger.Key).Distinct(StringComparer.Ordinal).Count() == definition.Triggers.Count
            ? null : "Two triggers of the automation are the same.";
    }

    /// <summary>Writes an automation in the text of a configuration file: over its table when it has one, at the end otherwise.</summary>
    /// <param name="content">The text of the file; empty for a file that does not exist yet.</param>
    /// <param name="definition">The automation, already valid.</param>
    /// <returns>The new text of the file.</returns>
    /// <exception cref="InvalidDataException">The file cannot be parsed, or the automation is not written as a table of its own.</exception>
    internal static string Write(string content, AutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(definition);
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = Format(definition, newline);
        var ranges = Locate(content, definition.Id);
        if (ranges.Count == 0)
        {
            var separator = content.Length == 0 ? string.Empty : content.EndsWith('\n') ? newline : newline + newline;
            return content + separator + text;
        }

        // The table takes the place of the first of its parts; a trigger written as a table of its own goes with it.
        var builder = new StringBuilder(content);
        for (var index = ranges.Count - 1; index > 0; index--) RemoveWithBlankLine(builder, ranges[index]);
        builder.Remove(ranges[0].Start, ranges[0].Length).Insert(ranges[0].Start, text);
        return builder.ToString();
    }

    /// <summary>Removes an automation from the text of a configuration file.</summary>
    /// <returns>The new text, or the same text when the file has no such automation.</returns>
    /// <exception cref="InvalidDataException">The file cannot be parsed, or the automation is not written as a table of its own.</exception>
    internal static string Remove(string content, string id)
    {
        ArgumentNullException.ThrowIfNull(content);
        var ranges = Locate(content, id);
        if (ranges.Count == 0) return content;
        var builder = new StringBuilder(content);
        for (var index = ranges.Count - 1; index >= 0; index--) RemoveWithBlankLine(builder, ranges[index]);
        return builder.ToString();
    }

    // The blank line that set a table apart goes with it.
    private static void RemoveWithBlankLine(StringBuilder builder, (int Start, int Length) range)
    {
        var (start, length) = range;
        if (start >= 2 && builder[start - 1] == '\n' && (builder[start - 2] == '\n' || start >= 3 && builder[start - 2] == '\r' && builder[start - 3] == '\n'))
        {
            var blank = builder[start - 2] == '\r' ? 2 : 1;
            (start, length) = (start - blank, length + blank);
        }

        builder.Remove(start, length);
    }

    // The text of each table of the automation, in the order of the file: from the start of the line of its header
    // to the end of the line of its last value.
    private static List<(int Start, int Length)> Locate(string content, string id)
    {
        if (content.Length == 0) return [];
        DocumentSyntax document;
        try
        {
            document = SyntaxParser.ParseStrict(content);
        }
        catch (TomlException exception)
        {
            throw new InvalidDataException("The configuration file cannot be read: " + FirstLine(exception.Message), exception);
        }

        var ranges = new List<(int Start, int Length)>();
        foreach (var table in document.Tables)
        {
            var name = Parts(table.Name);
            if (name.Count < 2 || name[0] != TableName || !string.Equals(name[1], id, StringComparison.OrdinalIgnoreCase)) continue;
            var start = table.Span.Start.Offset;
            while (start > 0 && content[start - 1] is ' ' or '\t') start--;
            var end = table.Span.End.Offset + 1;
            while (end < content.Length && content[end - 1] != '\n') end++;
            ranges.Add((start, Math.Min(end, content.Length) - start));
        }

        if (ranges.Count > 0) return ranges;
        // Written another way (a dotted key, an inline table): replacing text would not replace it.
        var (definitions, faults) = Read(content, new AutomationSource(string.Empty, null, null));
        return definitions.Any(definition => definition.Id == id) || faults.Any(fault => string.Equals(fault.Key, id, StringComparison.OrdinalIgnoreCase))
            ? throw new InvalidDataException("This automation is not written as a table of its own. Edit it in the configuration file.")
            : ranges;
    }

    private static List<string> Parts(KeySyntax? key)
    {
        var parts = new List<string>();
        if (key?.Key is null) return parts;
        parts.Add(Part(key.Key));
        foreach (var dotted in key.DotKeys) parts.Add(Part(dotted.Key));
        return parts;

        static string Part(BareKeyOrStringValueSyntax? part) => part switch
        {
            BareKeySyntax bare => bare.Key?.Text ?? string.Empty,
            StringValueSyntax text => text.Value ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static bool TryRead(string id, TomlTable table, AutomationSource source, out AutomationDefinition? definition, out string? problem)
    {
        definition = null;
        if (!TryText(table, "name", out var name, out problem) || !TryText(table, "prompt", out var prompt, out problem)
            || !TryText(table, "project", out var project, out problem) || !TryText(table, "model", out var model, out problem)
            || !TryText(table, "agent", out var agent, out problem) || !TryFlag(table, "enabled", true, out var enabled, out problem)
            || !TryFlag(table, "catch_up", false, out var catchUp, out problem)) return false;
        if (!AutomationModelRef.TryParse(model, out var reference, out problem)) return false;
        var triggers = new List<AutomationTrigger>();
        if (table.TryGetValue("triggers", out var value))
        {
            var items = value switch
            {
                TomlTableArray array => array.Cast<object>(),
                TomlArray array => array.Cast<object>(),
                TomlTable single => [single],
                _ => null,
            };
            if (items is null)
            {
                problem = "triggers is a list of tables.";
                return false;
            }

            foreach (var item in items)
            {
                if (item is not TomlTable trigger || !TryReadTrigger(trigger, out var read, out problem))
                {
                    problem ??= "A trigger is a table with a type.";
                    return false;
                }

                triggers.Add(read!);
            }
        }

        definition = new AutomationDefinition(id, (name ?? string.Empty).Trim())
        {
            Enabled = enabled,
            Prompt = (prompt ?? string.Empty).ReplaceLineEndings("\n").Trim(),
            // The folder a project automation runs in is the one of its file.
            Project = source.IsGlobal ? Blank(project) : null,
            Model = reference,
            Agent = Blank(agent),
            CatchUp = catchUp,
            Triggers = triggers,
        };
        problem = Validate(definition);
        return problem is null;
    }

    private static bool TryReadTrigger(TomlTable table, out AutomationTrigger? trigger, out string? problem)
    {
        trigger = null;
        if (!TryText(table, "type", out var type, out problem)) return false;
        if (!AutomationTrigger.TryParseKind(type, out var kind))
        {
            problem = $"'{type}' is not a trigger: hourly, daily, weekly, cron, issue, pull_request, jira or command.";
            return false;
        }

        if (!TryNumber(table, "minute", 0, out var minute, out problem) || !TryNumber(table, "every", 1, out var every, out problem)
            || !TryTexts(table, "at", out var at, out problem) || !TryTexts(table, "days", out var days, out problem)
            || !TryText(table, "expression", out var expression, out problem) || !TryText(table, "event", out var @event, out problem)
            || !TryText(table, "authors", out var authors, out problem) || !TryText(table, "command", out var command, out problem)
            || !TryText(table, "cwd", out var folder, out problem)) return false;
        var times = new List<AutomationTime>();
        foreach (var text in at)
        {
            if (!AutomationTime.TryParse(text, out var time))
            {
                problem = $"'{text}' is not a time of day, such as 09:00.";
                return false;
            }

            if (!times.Contains(time)) times.Add(time);
        }

        var weekDays = new List<DayOfWeek>();
        foreach (var text in days)
        {
            var name = text.Trim().ToLowerInvariant();
            var day = Array.FindIndex(AutomationTrigger.DayNames, candidate => name == candidate || name.Length > 3 && ((DayOfWeek)Array.IndexOf(AutomationTrigger.DayNames, candidate)).ToString().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (day < 0)
            {
                problem = $"'{text}' is not a day of the week, such as mon.";
                return false;
            }

            if (!weekDays.Contains((DayOfWeek)day)) weekDays.Add((DayOfWeek)day);
        }

        if (authors is not null && authors.Trim().ToLowerInvariant() is not ("trusted" or "anyone"))
        {
            problem = "authors is trusted or anyone.";
            return false;
        }

        times.Sort();
        weekDays.Sort();
        trigger = new AutomationTrigger(kind)
        {
            Minute = minute,
            Every = every,
            At = times,
            Days = weekDays,
            Expression = kind == AutomationTriggerKind.Cron ? string.Join(' ', (expression ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null,
            // An issue of Jira is created where one of a repository is opened: both words are read for it.
            Event = (@event ?? (kind == AutomationTriggerKind.Jira ? "created" : "opened")).Trim().ToLowerInvariant() is var read && kind == AutomationTriggerKind.Jira && read == "opened" ? "created" : read,
            Authors = authors?.Trim().ToLowerInvariant() == "anyone" ? AutomationAuthors.Anyone : AutomationAuthors.Trusted,
            Command = kind == AutomationTriggerKind.Command ? (command ?? string.Empty).Trim() : null,
            Folder = kind == AutomationTriggerKind.Command ? Blank(folder) : null,
        };
        return true;
    }

    private static bool TryText(TomlTable table, string key, out string? text, out string? problem)
    {
        (text, problem) = (null, null);
        if (!table.TryGetValue(key, out var value)) return true;
        if (value is not string read)
        {
            problem = key + " is a text.";
            return false;
        }

        text = read;
        return true;
    }

    private static bool TryTexts(TomlTable table, string key, out IReadOnlyList<string> texts, out string? problem)
    {
        (texts, problem) = ([], null);
        if (!table.TryGetValue(key, out var value)) return true;
        if (value is string single) texts = [single];
        else if (value is TomlArray array && array.All(static item => item is string)) texts = [.. array.Cast<string>()];
        else problem = key + " is a text or a list of texts.";
        return problem is null;
    }

    private static bool TryFlag(TomlTable table, string key, bool fallback, out bool flag, out string? problem)
    {
        (flag, problem) = (fallback, null);
        if (!table.TryGetValue(key, out var value)) return true;
        if (value is not bool read)
        {
            problem = key + " is true or false.";
            return false;
        }

        flag = read;
        return true;
    }

    private static bool TryNumber(TomlTable table, string key, int fallback, out int number, out string? problem)
    {
        (number, problem) = (fallback, null);
        if (!table.TryGetValue(key, out var value)) return true;
        if (value is not long read || read is < 0 or > 1440)
        {
            problem = key + " is a whole number.";
            return false;
        }

        number = (int)read;
        return true;
    }

    private static string Format(AutomationDefinition definition, string newline)
    {
        var builder = new StringBuilder();
        builder.Append('[').Append(TableName).Append('.').Append(definition.Id).Append(']').Append(newline);
        builder.Append("name = ").Append(Quote(definition.Name)).Append(newline);
        builder.Append("enabled = ").Append(definition.Enabled ? "true" : "false").Append(newline);
        // A folder of Windows is written with the slashes every system reads, which need no escape.
        if (definition.Project is { } project) builder.Append("project = ").Append(Quote(OperatingSystem.IsWindows() ? project.Replace('\\', '/') : project)).Append(newline);
        if (definition.Model.Format() is { } model) builder.Append("model = ").Append(Quote(model)).Append(newline);
        if (definition.Agent is { } agent) builder.Append("agent = ").Append(Quote(agent)).Append(newline);
        if (definition.CatchUp) builder.Append("catch_up = true").Append(newline);
        if (definition.Triggers.Count > 0)
        {
            builder.Append("triggers = [").Append(newline);
            foreach (var trigger in definition.Triggers) builder.Append("  ").Append(Format(trigger)).Append(',').Append(newline);
            builder.Append(']').Append(newline);
        }

        // Without quotes of its own, a prompt is written as it is typed: no character of it needs an escape.
        var prompt = definition.Prompt.ReplaceLineEndings("\n").Trim();
        var literal = !prompt.Contains("'''", StringComparison.Ordinal) && !prompt.Any(static character => char.IsControl(character) && character is not ('\n' or '\t'));
        builder.Append("prompt = ").Append(literal ? "'''" : "\"\"\"").Append(newline);
        builder.Append((literal ? prompt : Escape(prompt, multiline: true)).Replace("\n", newline, StringComparison.Ordinal)).Append(newline);
        builder.Append(literal ? "'''" : "\"\"\"").Append(newline);
        return builder.ToString();
    }

    private static string Format(AutomationTrigger trigger)
    {
        var builder = new StringBuilder("{ type = ").Append(Quote(trigger.KindName));
        switch (trigger.Kind)
        {
            case AutomationTriggerKind.Hourly:
                builder.Append(", minute = ").Append(trigger.Minute.ToString(CultureInfo.InvariantCulture));
                if (trigger.Every != 1) builder.Append(", every = ").Append(trigger.Every.ToString(CultureInfo.InvariantCulture));
                break;
            case AutomationTriggerKind.Daily:
                builder.Append(", at = ").Append(List(trigger.At.Select(static time => time.ToString())));
                break;
            case AutomationTriggerKind.Weekly:
                builder.Append(", days = ").Append(List(trigger.Days.Select(static day => AutomationTrigger.DayNames[(int)day])));
                builder.Append(", at = ").Append(List(trigger.At.Select(static time => time.ToString())));
                break;
            case AutomationTriggerKind.Cron:
                builder.Append(", expression = ").Append(Quote(trigger.Expression ?? string.Empty));
                break;
            case AutomationTriggerKind.Command:
                builder.Append(", command = ").Append(Quote(trigger.Command ?? string.Empty));
                if (trigger.Folder is { } folder) builder.Append(", cwd = ").Append(Quote(folder));
                break;
            default:
                builder.Append(", event = ").Append(Quote(trigger.Event));
                if (trigger.Authors == AutomationAuthors.Anyone) builder.Append(", authors = \"anyone\"");
                break;
        }

        return builder.Append(" }").ToString();

        static string List(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Quote)) + "]";
    }

    private static string Quote(string text) => "\"" + Escape(text, multiline: false) + "\"";

    private static string Escape(string text, bool multiline)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var character in text)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n' when multiline: builder.Append('\n'); break;
                case '\t' when multiline: builder.Append('\t'); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                case '\r': builder.Append("\\r"); break;
                default:
                    if (char.IsControl(character)) builder.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static bool IsWellFormed(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (!char.IsHighSurrogate(text[index]) || ++index == text.Length || !char.IsLowSurrogate(text[index])) return false;
        }

        return true;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}
