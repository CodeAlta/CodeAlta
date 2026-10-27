using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace CodeAlta.Orchestration.Runtime.SystemPrompts;

/// <summary>
/// The parts of the workflow of an agent that a host can leave out of its prompt: a host without a user who reads the
/// sticky notes, the follow-up tasks or the title of a session (a headless run) does not ask the agent for them.
/// </summary>
public static class PromptFeatures
{
    /// <summary>The scratchpad file where the agent copies a long request, and its line in the Runtime Context.</summary>
    public const string Scratchpad = "scratchpad";

    /// <summary>The sticky notes of the session (<c>alta notes</c>).</summary>
    public const string Notes = "notes";

    /// <summary>The follow-up tasks the agent proposes (<c>alta task create</c>).</summary>
    public const string Tasks = "tasks";

    /// <summary>The review of the changes by the user before commit (<c>alta review</c>).</summary>
    public const string Review = "review";

    /// <summary>
    /// The sub-agents, child sessions the agent creates to work in parallel. It is not a part a host turns off: it follows
    /// the option of the session (<see cref="CodeAlta.Agent.AgentSessionOptions.SubSessions"/>), so it is not in <see cref="All"/>.
    /// </summary>
    public const string SubAgents = "sub-agents";

    /// <summary>The switch to another agent prompt (Plan mode), and the list of the agent prompts.</summary>
    public const string ModeHandoff = "mode-handoff";

    /// <summary>The title the model gives the session.</summary>
    public const string SessionTitle = "session-title";

    /// <summary>Every feature a host turns off, in the order the help lists them; <see cref="SubAgents"/> follows the session.</summary>
    public static IReadOnlyList<string> All { get; } = [Scratchpad, Notes, Tasks, Review, ModeHandoff, SessionTitle];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Tells whether a name is the name of a feature (lower case, as <see cref="All"/> lists them).</summary>
    /// <param name="name">The name.</param>
    /// <returns>True for a feature.</returns>
    public static bool IsKnown(string? name) => name is not null && Known.Contains(name);
}

/// <summary>
/// The text of a prompt file that depends on a feature of the workflow (<see cref="PromptFeatures"/>): between
/// <c>&lt;!-- if-feature: notes --&gt;</c> and <c>&lt;!-- end-if-feature --&gt;</c>, with an optional
/// <c>&lt;!-- else --&gt;</c> for what is said without it. A marker alone on its line takes its line with it, so a
/// prompt with every feature on reads as if the markers were not there. Blocks may nest.
/// </summary>
/// <remarks>Apply <see cref="PromptToolConditions"/> first: an <c>&lt;!-- else --&gt;</c> of a tool is then gone.</remarks>
public static partial class PromptFeatureConditions
{
    /// <summary>Keeps the text of the features the host keeps and the other text of those it turns off.</summary>
    /// <param name="text">The text of a prompt file.</param>
    /// <param name="disabledFeatures">The features the host turns off; <see langword="null"/> for none.</param>
    /// <returns>The text without its markers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string Apply(string text, IReadOnlySet<string>? disabledFeatures)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("<!-- if-feature:", StringComparison.Ordinal)) return text;
        // A marker alone on its line goes with its line break; the innermost blocks are then replaced first, until none
        // is left: a block holds no other one when it is replaced.
        text = MarkerLine().Replace(text, "$1");
        while (text.Contains("<!-- if-feature:", StringComparison.Ordinal))
        {
            var replaced = Condition().Replace(text, match =>
            {
                var off = disabledFeatures?.Contains(match.Groups["feature"].Value) == true;
                return off ? match.Groups["else"].Value : match.Groups["then"].Value;
            });
            if (string.Equals(replaced, text, StringComparison.Ordinal)) break;
            text = replaced;
        }

        return text;
    }

    [GeneratedRegex(@"<!-- if-feature: (?<feature>[a-z0-9\-]+) -->(?<then>(?:(?!<!-- if-feature:).)*?)(?:<!-- else -->(?<else>(?:(?!<!-- if-feature:).)*?))?<!-- end-if-feature -->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Condition();

    [GeneratedRegex(@"^[ \t]*(<!-- (?:if-feature: [a-z0-9\-]+|else|end-if-feature) -->)[ \t]*\r?\n", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerLine();
}
