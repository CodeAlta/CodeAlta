using System.Globalization;
using System.Text;

namespace CodeAlta.Catalog.Reviews;

/// <summary>Writes the prompt an agent receives for a round: the verdict, then every comment with its lines.</summary>
internal static class ReviewPromptComposer
{
    /// <summary>The longest prompt, in UTF-16 units: snippets are shortened, then comments listed by id only.</summary>
    internal const int MaximumLength = 32 * 1024;

    // The lines of a snippet kept when the prompt would be too long.
    private const int ShortSnippetLines = 6;

    /// <summary>A comment to write, with the lines it is about.</summary>
    /// <param name="Thread">The thread.</param>
    /// <param name="Snippet">The lines it is about, as they are now (its anchor when it is outdated).</param>
    /// <param name="Added">For a line of the new side of a diff: whether it is not in the old side, shown with <c>+</c>.</param>
    /// <param name="Earlier">Whether an earlier round sent it already.</param>
    internal sealed record Item(ReviewThread Thread, IReadOnlyList<string> Snippet, IReadOnlyList<bool>? Added, bool Earlier);

    /// <summary>Writes the prompt of a round.</summary>
    internal static string Compose(ReviewRound round, int number, IReadOnlyList<Item> items)
    {
        var text = Write(round, number, items, snippetLines: int.MaxValue, budget: int.MaxValue);
        if (text.Length <= MaximumLength)
        {
            return text;
        }

        text = Write(round, number, items, ShortSnippetLines, int.MaxValue);
        return text.Length <= MaximumLength ? text : Write(round, number, items, ShortSnippetLines, MaximumLength - 2048);
    }

    private static string Write(ReviewRound round, int number, IReadOnlyList<Item> items, int snippetLines, int budget)
    {
        var diff = ReviewTargets.IsDiff(round.Target);
        var builder = new StringBuilder();
        var what = round.Target switch
        {
            ReviewTargets.Changes => "your changes",
            ReviewTargets.Branch => "the changes of your branch",
            _ => "`" + Code(round.Path ?? "") + "`",
        };
        var verdict = round.Verdict switch
        {
            ReviewVerdicts.Approved => "approved",
            ReviewVerdicts.ChangesRequested => "changes requested",
            _ => "comments",
        };
        builder.Append("# Review of ").Append(what).Append(": ").AppendLine(verdict).AppendLine();
        var current = items.Count(static item => !item.Earlier);
        builder.Append("Round ").Append(number.ToString(CultureInfo.InvariantCulture)).Append(" · ")
            .Append(Count(current, "comment"));
        if (items.Count > current)
        {
            builder.Append(", ").Append(Count(items.Count - current, "still open from earlier rounds", plural: false));
        }

        if (round.Snapshot is { Length: >= 7 } snapshot)
        {
            builder.Append(" · reviewed snapshot `").Append(snapshot[..7]).Append('`');
        }

        builder.AppendLine().AppendLine();
        if (!string.IsNullOrWhiteSpace(round.Overall))
        {
            builder.AppendLine(round.Overall.Trim()).AppendLine();
        }

        var skipped = new List<string>();
        var newItems = items.Where(static item => !item.Earlier).ToArray();
        var earlierItems = items.Where(static item => item.Earlier).ToArray();
        WriteGroup(builder, newItems, diff, snippetLines, budget, skipped, heading: null);
        if (earlierItems.Length > 0)
        {
            WriteGroup(builder, earlierItems, diff, snippetLines, budget, skipped, heading: "Still open from earlier rounds");
        }

        if (skipped.Count > 0)
        {
            builder.Append("Also open, too long to list here: ").Append(string.Join(", ", skipped))
                .AppendLine(". Read them with `alta review list`.").AppendLine();
        }

        builder.AppendLine("---");
        builder.AppendLine(Footer(round, diff, items.Count > 0));
        return builder.ToString();
    }

    private static void WriteGroup(StringBuilder builder, IReadOnlyList<Item> items, bool diff, int snippetLines, int budget, List<string> skipped, string? heading)
    {
        if (heading is not null)
        {
            builder.Append("## ").AppendLine(heading).AppendLine();
        }

        foreach (var file in items.GroupBy(static item => item.Thread.Path).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            var start = builder.Length;
            builder.Append(heading is null ? "## " : "### ").AppendLine(file.Key).AppendLine();
            var wrote = false;
            foreach (var item in file.OrderBy(static item => item.Thread.StartLine).ThenBy(static item => item.Thread.Id, StringComparer.Ordinal))
            {
                var before = builder.Length;
                WriteItem(builder, item, diff, snippetLines, heading is null ? "### " : "#### ");
                if (builder.Length > budget)
                {
                    builder.Length = before;
                    skipped.Add(item.Thread.Id);
                    continue;
                }

                wrote = true;
            }

            if (!wrote)
            {
                builder.Length = start;
            }
        }
    }

    private static void WriteItem(StringBuilder builder, Item item, bool diff, int snippetLines, string level)
    {
        var thread = item.Thread;
        builder.Append(level).Append(thread.Id).Append(" · ");
        if (thread.WholeFile)
        {
            builder.Append("whole file");
        }
        else if (thread.StartLine == thread.EndLine)
        {
            builder.Append("line ").Append(thread.StartLine.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            builder.Append("lines ").Append(thread.StartLine.ToString(CultureInfo.InvariantCulture)).Append('–').Append(thread.EndLine.ToString(CultureInfo.InvariantCulture));
        }

        if (diff && !thread.WholeFile)
        {
            builder.Append(" (").Append(thread.Side).Append(')');
        }

        if (thread.Suggestion is not null)
        {
            builder.Append(" · suggestion");
        }

        if (thread.Outdated)
        {
            builder.Append(" · outdated: these lines changed since the comment");
        }

        builder.AppendLine();
        var snippet = item.Snippet;
        if (snippet.Count > 0)
        {
            var shown = Math.Min(snippet.Count, snippetLines);
            if (diff)
            {
                var fence = Fence(snippet);
                builder.Append(fence).AppendLine("diff");
                for (var index = 0; index < shown; index++)
                {
                    var mark = thread.Side == ReviewSides.Old ? '-' : item.Added is { } added && index < added.Count && added[index] ? '+' : ' ';
                    builder.Append(mark).AppendLine(snippet[index]);
                }

                if (shown < snippet.Count)
                {
                    builder.Append(' ').Append("… ").Append((snippet.Count - shown).ToString(CultureInfo.InvariantCulture)).AppendLine(" more lines");
                }

                builder.AppendLine(fence);
            }
            else
            {
                for (var index = 0; index < shown; index++)
                {
                    builder.Append("> ").AppendLine(snippet[index]);
                }

                if (shown < snippet.Count)
                {
                    builder.Append("> … ").Append((snippet.Count - shown).ToString(CultureInfo.InvariantCulture)).AppendLine(" more lines");
                }

                builder.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(thread.Body))
        {
            builder.AppendLine(thread.Body.ReplaceLineEndings("\n").Trim());
        }

        if (thread.Suggestion is { } suggestion)
        {
            if (suggestion.Length == 0)
            {
                builder.AppendLine("Suggestion: delete these lines.");
            }
            else
            {
                var lines = ReviewAnchoring.SplitLines(suggestion);
                var fence = Fence(lines);
                builder.Append("Suggestion, to replace these lines:").AppendLine();
                builder.Append(fence).AppendLine("suggestion");
                foreach (var line in lines)
                {
                    builder.AppendLine(line);
                }

                builder.AppendLine(fence);
            }
        }

        foreach (var reply in thread.Replies)
        {
            builder.Append("> ").Append(reply.Author == ReviewAuthors.Agent ? "Your earlier reply: " : "User's reply: ")
                .AppendLine(reply.Body.ReplaceLineEndings(" ").Trim());
        }

        builder.AppendLine();
    }

    private static string Footer(ReviewRound round, bool diff, bool comments)
    {
        const string Reply = "answer them all in one call: `alta review reply --stdin` with `[{\"id\":\"c1\",\"reply\":\"…\"}]`";
        return round.Verdict switch
        {
            ReviewVerdicts.Approved when diff => (comments ? "The comments above are notes, not blockers: " + Reply + ". " : string.Empty)
                + "Commit the approved changes now, and only them: a change made after this approval needs a new review (`alta review request "
                + round.Target + "`) before it is committed.",
            ReviewVerdicts.Approved => (comments ? "Fold what the comments ask into the file, then " + Reply + ". " : string.Empty)
                + "The user approved `" + Code(round.Path ?? "") + "`: go on with it.",
            ReviewVerdicts.ChangesRequested when diff => "Address each comment, then " + Reply
                + ". When you are done and the changes are verified, request the next review with `alta review request " + round.Target
                + "` and end your turn. Do not commit before a review approves the changes.",
            ReviewVerdicts.ChangesRequested => "Revise the file for each comment, then " + Reply
                + ". When you are done, request the next review with `alta review request file " + Code(round.Path ?? "") + "` and end your turn.",
            _ => comments ? "Answer each comment, then " + Reply + ". There is no verdict yet." : "There is no verdict yet.",
        };
    }

    // A fence longer than any run of backticks in the lines it holds.
    private static string Fence(IEnumerable<string> lines)
    {
        var longest = 0;
        foreach (var line in lines)
        {
            var run = 0;
            foreach (var character in line)
            {
                run = character == '`' ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
        }

        return new string('`', Math.Max(3, longest + 1));
    }

    private static string Count(int count, string what, bool plural = true)
        => count.ToString(CultureInfo.InvariantCulture) + " " + what + (plural && count != 1 ? "s" : string.Empty);

    private static string Code(string text) => text.Replace("`", "ˋ", StringComparison.Ordinal);
}
