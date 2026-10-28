using System.Text.RegularExpressions;

namespace CodeAlta.Orchestration.Runtime.SystemPrompts;

/// <summary>
/// The text of a prompt file that depends on a tool of the session: between
/// <c>&lt;!-- if-tool: shell_command --&gt;</c> and <c>&lt;!-- end-if-tool --&gt;</c>, with an optional
/// <c>&lt;!-- else --&gt;</c> for what is said without it. A session without the tool is not told about it.
/// </summary>
public static partial class PromptToolConditions
{
    /// <summary>The name of the built-in shell tool.</summary>
    public const string ShellCommand = "shell_command";

    /// <summary>The name of the tool of Code Mode that runs a script, which a session has only in Code Mode.</summary>
    public const string Exec = "exec";

    /// <summary>Keeps the text of the tools the session has and the other text of those it does without.</summary>
    /// <param name="text">The text of a prompt file.</param>
    /// <param name="disabledTools">The tools the session does without; <see langword="null"/> for none.</param>
    /// <returns>The text without its markers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string Apply(string text, IReadOnlySet<string>? disabledTools)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("<!-- if-tool:", StringComparison.Ordinal)) return text;
        return Condition().Replace(text, match =>
        {
            var off = disabledTools?.Contains(match.Groups["tool"].Value) == true;
            return off ? match.Groups["else"].Value : match.Groups["then"].Value;
        });
    }

    [GeneratedRegex(@"<!-- if-tool: (?<tool>[A-Za-z0-9_.:\-]+) -->(?<then>.*?)(?:<!-- else -->(?<else>.*?))?<!-- end-if-tool -->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Condition();
}
