using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Frontend.Commands;

namespace CodeAlta.Frontend.Help;

internal static class ShellHelpContentBuilder
{
    public static string BuildMarkdown(IReadOnlyList<ShellCommand> commands, string? filterText = null)
    {
        var sections = BuildSections(commands, filterText);
        var builder = new StringBuilder();

        builder.Append("# ").AppendLine(SR.T("Shell Commands"));
        builder.AppendLine();
        builder.AppendLine(SR.T("Use `?`, `/`, or the shortcuts below to discover available shell actions."));
        builder.AppendLine();

        if (sections.Count == 0)
        {
            builder.Append('_').Append(SR.T("No commands matched that filter.")).AppendLine("_");
            return builder.ToString();
        }

        foreach (var section in sections)
        {
            builder.Append("## ")
                .AppendLine(EscapeMarkdownText(section.Title));
            builder.AppendLine();

            foreach (var entry in section.Entries)
            {
                builder.Append("- **")
                    .Append(EscapeMarkdownText(entry.Label))
                    .Append("** — ")
                    .Append(EscapeMarkdownText(entry.Description));

                if (entry.Bindings.Count > 0)
                {
                    builder.Append(" (")
                        .Append(string.Join(" · ", entry.Bindings.Select(FormatInlineCode)));
                    builder.Append(')');
                }

                builder.AppendLine();
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    public static IReadOnlyList<ShellHelpSection> BuildSections(IReadOnlyList<ShellCommand> commands, string? filterText = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return commands
            .Where(static command => command.ShowInHelp)
            .Where(command => MatchesFilter(command, filterText))
            .GroupBy(static command => command.HelpCategory)
            .OrderBy(static group => group.Key)
            .Select(group => new ShellHelpSection(
                GetCategoryTitle(group.Key),
                group
                    .OrderBy(static command => command.Label, StringComparer.Ordinal)
                    .Select(BuildEntry)
                    .ToArray()))
            .Where(static section => section.Entries.Count > 0)
            .ToArray();
    }

    private static ShellHelpEntry BuildEntry(ShellCommand command)
        => new(command.Label, command.Description, BuildBindings(command));

    private static IReadOnlyList<string> BuildBindings(ShellCommand command)
    {
        var bindings = new List<string>();
        if (command.Gesture is not null)
        {
            bindings.Add(command.Gesture.ToString() ?? string.Empty);
        }

        if (command.Sequence is not null)
        {
            bindings.Add(command.Sequence.ToString() ?? string.Empty);
        }

        bindings.AddRange(command.AdditionalHelpBindings.Where(static binding => !string.IsNullOrWhiteSpace(binding)));
        return bindings;
    }

    private static bool MatchesFilter(ShellCommand command, string? filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return true;
        }

        return command.Label.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
               command.Description.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(command.Name) && command.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(command.SearchText) && command.SearchText.Contains(filterText, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetCategoryTitle(ShellCommandHelpCategory category)
        => category switch
        {
            ShellCommandHelpCategory.General => SR.T("General"),
            ShellCommandHelpCategory.Prompt => SR.T("Prompt"),
            ShellCommandHelpCategory.Session => SR.T("Session"),
            ShellCommandHelpCategory.Navigation => SR.T("Navigation"),
            ShellCommandHelpCategory.Inspection => SR.T("Inspection"),
            _ => category.ToString(),
        };

    private static string FormatInlineCode(string value)
        => string.IsNullOrEmpty(value)
            ? "``"
            : $"`{value.Replace("`", "\\`", StringComparison.Ordinal)}`";

    private static string EscapeMarkdownText(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}

internal sealed record ShellHelpSection(string Title, IReadOnlyList<ShellHelpEntry> Entries);

internal sealed record ShellHelpEntry(string Label, string Description, IReadOnlyList<string> Bindings);
