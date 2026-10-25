using System.Collections.Frozen;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Agent;

/// <summary>
/// Patterns over the names of the tools a session is offered, which turn tools off: the built-in tools, the
/// <c>alta</c> tool, the tools of MCP servers (<c>mcp__server__tool</c>), the tools of plugins, and the tools a provider
/// runs itself (<see cref="WebSearchToolName"/>, <see cref="WebFetchToolName"/>). A pattern is a name, where <c>*</c>
/// stands for any characters, compared without regard to case; <see cref="WebAlias"/> names every tool that reaches the
/// web.
/// </summary>
public static class AgentToolPatterns
{
    /// <summary>The pattern that names every tool that reaches the web: <c>webget</c> and the web tools providers run.</summary>
    public const string WebAlias = "web";

    /// <summary>The name a session's turn requests give the web search a provider runs itself (OpenAI, Claude Code).</summary>
    public const string WebSearchToolName = "web_search";

    /// <summary>The name a session's turn requests give the web fetch a provider runs itself (Claude Code).</summary>
    public const string WebFetchToolName = "web_fetch";

    /// <summary>The longest pattern.</summary>
    public const int MaximumPatternLength = 256;

    private static readonly FrozenSet<string> WebTools = FrozenSet.Create(StringComparer.Ordinal, "webget", WebSearchToolName, WebFetchToolName);

    /// <summary>
    /// Gets the names the disabled set of a session can name besides its own tools: the built-in tools and the tools
    /// providers run themselves. The patterns of a session are expanded over them into the tools its instructions,
    /// its write guard and its provider do without.
    /// </summary>
    public static IReadOnlyList<string> KnownToolNames { get; } = [.. AgentBuiltInToolFactory.BuiltInToolNames, WebSearchToolName, WebFetchToolName];

    /// <summary>Says what is wrong with a pattern.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The reason it cannot be used, or <see langword="null"/> for a valid pattern.</returns>
    public static string? Validate(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "A tool pattern is not empty.";
        if (pattern.Length > MaximumPatternLength) return $"A tool pattern has at most {MaximumPatternLength} characters.";
        foreach (var ch in pattern)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '*'))
                return $"'{pattern}' is not a tool pattern: use the letters, digits, '_' and '-' of tool names, and '*'.";
        }

        return null;
    }

    /// <summary>Gets whether a tool is turned off by one of the patterns.</summary>
    /// <param name="toolName">The name of the tool.</param>
    /// <param name="patterns">The patterns; null or empty turns nothing off.</param>
    /// <returns><see langword="true"/> when a pattern names the tool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="toolName"/> is null.</exception>
    public static bool IsDisabled(string toolName, IReadOnlyList<string>? patterns)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        if (patterns is not { Count: > 0 }) return false;
        foreach (var pattern in patterns)
        {
            if (string.Equals(pattern, WebAlias, StringComparison.OrdinalIgnoreCase) ? WebTools.Contains(toolName) : Matches(toolName, pattern)) return true;
        }

        return false;
    }

    /// <summary>Lists the names the patterns turn off among some names.</summary>
    /// <param name="names">The names, such as <see cref="KnownToolNames"/>.</param>
    /// <param name="patterns">The patterns; null or empty turns nothing off.</param>
    /// <returns>The names turned off, each once.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> is null.</exception>
    public static IReadOnlySet<string> Expand(IEnumerable<string> names, IReadOnlyList<string>? patterns)
    {
        ArgumentNullException.ThrowIfNull(names);
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        if (patterns is not { Count: > 0 }) return disabled;
        foreach (var name in names)
        {
            if (IsDisabled(name, patterns)) disabled.Add(name);
        }

        return disabled;
    }

    /// <summary>Gets whether two lists of patterns turn off the same tools, written the same, whatever their order.</summary>
    /// <param name="first">The first list; null for none.</param>
    /// <param name="second">The second list; null for none.</param>
    /// <returns><see langword="true"/> when they hold the same patterns.</returns>
    public static bool SameAs(IReadOnlyList<string>? first, IReadOnlyList<string>? second)
        => new HashSet<string>(first ?? [], StringComparer.OrdinalIgnoreCase).SetEquals(second ?? []);

    // A glob where '*' matches any run of characters, without regard to case.
    private static bool Matches(string name, string pattern)
    {
        var n = 0;
        var p = 0;
        var star = -1;
        var resume = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n]))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
