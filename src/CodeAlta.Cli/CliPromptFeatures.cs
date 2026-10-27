using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Cli;

/// <summary>
/// The parts of the agent prompt (<see cref="PromptFeatures"/>) the sessions of <c>altacli</c> leave out: the defaults of
/// <c>run</c> and <c>acp</c>, changed by <c>--enable</c> and <c>--disable</c>.
/// </summary>
internal static class CliPromptFeatures
{
    /// <summary>The name that stands for every feature.</summary>
    public const string All = "all";

    /// <summary>
    /// What <c>altacli run</c> leaves out: a run is one request read by a program, so nobody reads its notes, its proposed
    /// tasks or a review request (its commands are not offered), nor switches it to another mode, and the request stays
    /// in the context when it is compacted, which the scratchpad was for.
    /// </summary>
    public static IReadOnlyList<string> RunDefaults { get; } =
        [PromptFeatures.Scratchpad, PromptFeatures.Notes, PromptFeatures.Tasks, PromptFeatures.Review, PromptFeatures.ModeHandoff];

    /// <summary>
    /// What <c>altacli acp</c> leaves out: a user works in the editor, over several prompts, as in CodeAlta Desktop, so only
    /// the review by the user, whose commands altacli does not offer.
    /// </summary>
    public static IReadOnlyList<string> AcpDefaults { get; } = [PromptFeatures.Review];

    /// <summary>Works out the features the sessions leave out.</summary>
    /// <param name="defaults">The features the command leaves out by default.</param>
    /// <param name="ephemeral">Whether the state root is deleted at the end (<c>--ephemeral</c>): no session is listed, so none is titled.</param>
    /// <param name="enable">The values of <c>--enable</c>: names separated by commas, or <c>all</c>.</param>
    /// <param name="disable">The values of <c>--disable</c>.</param>
    /// <param name="disabled">The features left out, in the order of <see cref="PromptFeatures.All"/>.</param>
    /// <param name="error">Why the values are not valid; null when they are.</param>
    /// <returns>True when the values are valid.</returns>
    public static bool TryResolve(IReadOnlyList<string> defaults, bool ephemeral, IEnumerable<string> enable, IEnumerable<string> disable,
        out IReadOnlyList<string> disabled, out string? error)
    {
        disabled = [];
        if (!TryRead("--enable", enable, out var enabledNames, out var enableAll, out error)) return false;
        if (!TryRead("--disable", disable, out var disabledNames, out var disableAll, out error)) return false;
        if (enableAll && disableAll)
        {
            error = "--enable all and --disable all cannot be given together.";
            return false;
        }

        if (enabledNames.Intersect(disabledNames, StringComparer.Ordinal).FirstOrDefault() is { } both)
        {
            error = $"'{both}' is given to both --enable and --disable.";
            return false;
        }

        // `all` first, then the names: a name says more than `all`.
        var off = new HashSet<string>(defaults, StringComparer.Ordinal);
        if (ephemeral) off.Add(PromptFeatures.SessionTitle);
        if (enableAll) off.Clear();
        if (disableAll) off.UnionWith(PromptFeatures.All);
        off.UnionWith(disabledNames);
        off.ExceptWith(enabledNames);
        disabled = [.. PromptFeatures.All.Where(off.Contains)];
        return true;
    }

    private static bool TryRead(string option, IEnumerable<string> values, out HashSet<string> names, out bool all, out string? error)
    {
        names = new HashSet<string>(StringComparer.Ordinal);
        all = false;
        error = null;
        foreach (var name in values.SelectMany(static value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var normalized = name.ToLowerInvariant();
            if (normalized == All)
            {
                all = true;
            }
            else if (PromptFeatures.IsKnown(normalized))
            {
                names.Add(normalized);
            }
            else
            {
                error = $"{option} {name}: not a part of the agent prompt; use {string.Join(", ", PromptFeatures.All)} or all"
                    + (option == "--disable" ? " (tools are turned off with --disable-tools)." : ".");
                return false;
            }
        }

        return true;
    }
}
