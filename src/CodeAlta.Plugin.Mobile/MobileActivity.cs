using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// The work between two messages, summed up on one line the way the desktop timeline folds it
/// (<c>frontend/src/timelineActivity/activityFold.ts</c>, <c>activitySummary</c> and <c>TimelineActivity</c>): the first
/// line of the newest thought, else "Calling tools…"/"Called tools" with the names of the first steps, then how many
/// thoughts and tool calls, what failed and for how long.
/// </summary>
internal sealed partial class MobileActivity
{
    private const int LatestNames = 3;
    private readonly HashSet<string> _calls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly List<string> _names = [];

    internal string? Headline { get; private set; }
    internal int Thoughts { get; private set; }
    internal int Tools => _calls.Count;
    internal int Succeeded { get; private set; }
    internal int Failed { get; private set; }
    internal int Running => _running.Count;
    internal DateTimeOffset StartedAt { get; private set; }
    internal DateTimeOffset EndedAt { get; private set; }
    internal bool IsEmpty => Thoughts == 0 && Tools == 0;

    internal void Reset()
    {
        _calls.Clear(); _running.Clear(); _names.Clear();
        Headline = null; Thoughts = Succeeded = Failed = 0; StartedAt = EndedAt = default;
    }

    private void Touch(DateTimeOffset at)
    {
        if (StartedAt == default) StartedAt = at;
        EndedAt = at;
    }

    internal void Thought(string? markdown, DateTimeOffset at)
    {
        Touch(at);
        Thoughts++;
        if (ThoughtHeadline(markdown) is { } line) Headline = line;
    }

    internal void ToolStarted(string id, string title, string? detail, DateTimeOffset at)
    {
        Touch(at);
        _running.Add(id);
        if (!_calls.Add(id)) return;
        var name = StepName(title, detail);
        if (_names.Count < LatestNames && !_names.Contains(name)) _names.Add(name);
    }

    internal void ToolEnded(string id, bool failed, DateTimeOffset at)
    {
        Touch(at);
        _calls.Add(id);
        _running.Remove(id);
        if (failed) Failed++; else Succeeded++;
    }

    internal void Ended() => _running.Clear();

    /// <summary>The line and its facts, as the phone renders them.</summary>
    internal JsonObject ToJson(bool live) => new()
    {
        ["state"] = live ? "running" : Failed > 0 ? "failed" : "done",
        ["headline"] = Headline ?? (Tools > 0 ? (live ? "Calling tools…" : "Called tools") : (live ? "Working…" : "Worked")),
        ["names"] = Headline is null && _names.Count > 0 ? string.Join(", ", _names) : null,
        ["thoughts"] = Thoughts, ["tools"] = Tools, ["succeeded"] = Succeeded, ["failed"] = Failed, ["running"] = Running,
        ["startedAt"] = StartedAt == default ? null : StartedAt.ToString("O"),
        ["endedAt"] = EndedAt == default ? null : EndedAt.ToString("O"),
    };

    /// <summary>A line of Markdown without its heading, its bullet and its marks.</summary>
    internal static string PlainLine(string line)
        => Whitespace().Replace(Marks().Replace(Bullet().Replace(Heading().Replace(line, ""), ""), ""), " ").Trim();

    /// <summary>Port of <c>thoughtHeadline</c>: a bold title is the line, else the last sentence of the last prose paragraph.</summary>
    internal static string? ThoughtHeadline(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var text = markdown.Replace("\r\n", "\n").Trim();
        var title = BoldTitle().Match(text);
        if (title.Success) { var plain = PlainLine(title.Groups[1].Value); return plain.Length > 0 ? plain : null; }
        var paragraphs = ParagraphBreak().Split(Fences().Replace(text, ""))
            .Select(static p => PlainLine(string.Join(" ", p.Split('\n').Select(PlainLine))))
            .Where(static p => p.Length > 0).ToList();
        if (paragraphs.Count == 0) return null;
        var last = paragraphs[^1];
        var sentences = SentenceBreak().Split(last).Where(static s => s.Length > 0).ToList();
        var line = sentences.Count > 0 ? sentences[^1] : last;
        if (line.Length < 16 && sentences.Count > 1) line = $"{sentences[^2]} {line}";
        return line;
    }

    /// <summary>Port of <c>stepName</c>: the tool, with the program a command runs or the file it works on.</summary>
    internal static string StepName(string title, string? detail)
    {
        detail = detail?.Trim();
        if (string.IsNullOrEmpty(detail)) return title;
        var words = detail.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var at = string.Equals(words[0], title, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (at >= words.Length) return title;
        if (CallOperator().IsMatch(words[at]) && at + 1 < words.Length) at++;
        if (Variable().IsMatch(words[at])) return title;
        var first = words[at].TrimStart('(', '&', '"', '\'').TrimEnd('"', '\'');
        if (first.Length == 0) first = words[at];
        var word = first.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? first;
        return word.Length > 32 ? title : $"{title} {word}";
    }

    [GeneratedRegex(@"^#+\s*")] private static partial Regex Heading();
    [GeneratedRegex(@"^[-*>]\s+")] private static partial Regex Bullet();
    [GeneratedRegex(@"\*\*|__|`")] private static partial Regex Marks();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"^\*\*([^*\n]+)\*\*")] private static partial Regex BoldTitle();
    [GeneratedRegex(@"```[\s\S]*?(```|$)")] private static partial Regex Fences();
    [GeneratedRegex(@"\n\s*\n")] private static partial Regex ParagraphBreak();
    [GeneratedRegex(@"(?<=[.!?…])\s+(?=\S)")] private static partial Regex SentenceBreak();
    [GeneratedRegex(@"^[(&]+$")] private static partial Regex CallOperator();
    [GeneratedRegex(@"^[(&]*[$@]")] private static partial Regex Variable();
}
