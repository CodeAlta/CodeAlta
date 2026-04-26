using XenoAtom.Ansi;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Frontend.Commands;

internal enum ShellCommandScope
{
    AnyShell,
    DraftOrThread,
    ThreadOnly,
}

internal enum ShellCommandAvailability
{
    Always,
    PromptEnabled,
    CanSend,
    CanSteer,
    CanDelegate,
    CanAbort,
    CanClearQueue,
    CanCompact,
    CanCloseTab,
    CanShowThreadInfo,
}

internal enum ShellCommandHelpCategory
{
    General,
    Prompt,
    Thread,
    Navigation,
    Inspection,
}

internal sealed record ShellCommandMetadata(
    string Id,
    string Label,
    string Description,
    ShellCommandHelpCategory HelpCategory,
    ShellCommandScope Scope,
    ShellCommandAvailability Availability,
    KeyGesture? Gesture = null,
    KeySequence? Sequence = null,
    string? CommandName = null,
    IReadOnlyList<string>? Aliases = null,
    bool ShowInCommandBar = true,
    bool ShowInCommandPalette = true,
    bool SupportsTextCommand = true,
    bool ShowInHelp = true)
{
    public string CommandName { get; } = ResolveCommandName(CommandName, Label);
    internal string SlashCommandText { get; } = $"/{ResolveCommandName(CommandName, Label)}";
    internal string DisplayLabelMarkup { get; } = AnsiMarkup.Escape(Label);
    internal string DescriptionMarkup { get; } = $"[dim]{AnsiMarkup.Escape(Description)}[/]";

    public IReadOnlyList<string> Aliases { get; } = BuildAliases(
        ResolveCommandName(CommandName, Label),
        Aliases);
    public IReadOnlyList<string> TextCommandAliases { get; } = SupportsTextCommand
        ? BuildAliases(
            ResolveCommandName(CommandName, Label),
            Aliases)
        : [];

    internal string CommandSearchText { get; } = BuildCommandSearchText(
        Label,
        ResolveCommandName(CommandName, Label),
        BuildAliases(
            ResolveCommandName(CommandName, Label),
            Aliases),
        SupportsTextCommand);

    private static string ResolveCommandName(string? commandName, string label)
    {
        if (!string.IsNullOrWhiteSpace(commandName))
        {
            return commandName;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        Span<char> buffer = stackalloc char[label.Length];
        var index = 0;
        var pendingUnderscore = false;
        foreach (var character in label)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingUnderscore && index > 0)
                {
                    buffer[index++] = '_';
                }

                buffer[index++] = char.ToLowerInvariant(character);
                pendingUnderscore = false;
                continue;
            }

            pendingUnderscore = index > 0;
        }

        return index == 0 ? "command" : new string(buffer[..index]);
    }

    private static IReadOnlyList<string> BuildAliases(string commandName, IReadOnlyList<string>? aliases)
    {
        var allAliases = new List<string>(1 + (aliases?.Count ?? 0))
        {
            commandName,
        };

        if (aliases is not null)
        {
            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias) ||
                    allAliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                allAliases.Add(alias);
            }
        }

        return allAliases;
    }

    private static string BuildCommandSearchText(
        string label,
        string commandName,
        IReadOnlyList<string> aliases,
        bool supportsTextCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(aliases);

        var searchTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            label,
            commandName,
        };

        if (supportsTextCommand)
        {
            searchTerms.Add($"/{commandName}");
        }

        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            searchTerms.Add(alias);
            if (supportsTextCommand)
            {
                searchTerms.Add($"/{alias}");
            }
        }

        return string.Join(' ', searchTerms);
    }
}
