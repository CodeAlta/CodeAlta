using CodeAlta.Frontend.Commands;

namespace CodeAlta.Frontend.Help;

internal static class ShellHelpContentBuilder
{
    public static IReadOnlyList<ShellHelpSection> BuildSections(string? filterText = null)
    {
        var commands = ShellCommandCatalog.Commands
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

        return commands;
    }

    private static ShellHelpEntry BuildEntry(ShellCommandMetadata command)
    {
        var bindings = new List<string>();
        if (command.Gesture is { } gesture)
        {
            bindings.Add(FormatGesture(gesture));
        }

        if (command.Sequence is { } sequence)
        {
            bindings.Add(FormatSequence(sequence));
        }

        foreach (var alias in command.TextCommandAliases)
        {
            bindings.Add($"/{alias}");
        }

        if (command.Id == "CodeAlta.Shell.Help")
        {
            bindings.Add("?");
        }

        if (command.Id == "CodeAlta.Shell.CommandPalette")
        {
            bindings.Add("/");
        }

        return new ShellHelpEntry(command.Label, command.Description, bindings);
    }

    private static bool MatchesFilter(ShellCommandMetadata command, string? filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return true;
        }

        return command.Label.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
               command.Description.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
               command.Aliases.Any(alias => alias.Contains(filterText, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetCategoryTitle(ShellCommandHelpCategory category)
    {
        return category switch
        {
            ShellCommandHelpCategory.General => "General",
            ShellCommandHelpCategory.Prompt => "Prompt",
            ShellCommandHelpCategory.Thread => "Thread",
            ShellCommandHelpCategory.Navigation => "Navigation",
            ShellCommandHelpCategory.Inspection => "Inspection",
            _ => category.ToString()
        };
    }

    private static string FormatGesture(object gesture)
        => gesture.ToString() ?? string.Empty;

    private static string FormatSequence(object sequence)
        => sequence.ToString() ?? string.Empty;
}

internal sealed record ShellHelpSection(string Title, IReadOnlyList<ShellHelpEntry> Entries);

internal sealed record ShellHelpEntry(string Label, string Description, IReadOnlyList<string> Bindings);
