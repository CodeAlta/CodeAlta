using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    // The patterns over tool names each session of this runtime does without, beside the settings ([tools] disabled,
    // its space and its agent prompt). Held by the runtime only: a session resumed by another process has its usual tools.
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _toolPatterns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Turns off, for one session of this runtime, the tools that patterns name (<see cref="AgentToolPatterns"/>): built-in
    /// tools, <c>alta</c>, the tools of MCP servers and plugins, and the web tools providers run themselves
    /// (<see cref="AgentToolPatterns.WebAlias"/>). A tool they name is never offered to the model. The built-in tools they
    /// name are also turned off as the settings turn tools off: in the instructions, the write guard and the provider.
    /// </summary>
    /// <param name="sessionId">The session; one not created yet takes them when it is created with this identifier.</param>
    /// <param name="patterns">The patterns; null or empty gives the session its usual tools again.</param>
    /// <remarks>
    /// They are kept by this runtime only, never in the journal. They apply from the next attachment of the session: an
    /// idle session is attached again at its next prompt, and a session another session of the runtime creates takes the
    /// patterns of its parent, or of its creator, so that it is never given more tools.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank, or a pattern is not valid (<see cref="AgentToolPatterns.Validate"/>).</exception>
    public void SetDisabledToolPatterns(string sessionId, IReadOnlyList<string>? patterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (patterns is not { Count: > 0 })
        {
            _toolPatterns.TryRemove(sessionId, out _);
            return;
        }

        foreach (var pattern in patterns)
        {
            if (AgentToolPatterns.Validate(pattern) is { } error) throw new ArgumentException(error, nameof(patterns));
        }

        _toolPatterns[sessionId] = [.. patterns.Select(static pattern => pattern.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Gets the patterns over tool names a session of this runtime does without (<see cref="SetDisabledToolPatterns"/>).</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The patterns; empty for none.</returns>
    public IReadOnlyList<string> GetDisabledToolPatterns(string? sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && _toolPatterns.TryGetValue(sessionId, out var patterns) ? patterns : [];

    // A session created by another one does without what its parent, or its creator, does without; a session given
    // patterns of its own before it was created keeps them.
    private void InheritToolPatterns(string sessionId, string? parentSessionId, string? creatorSessionId)
    {
        if (_toolPatterns.ContainsKey(sessionId)) return;
        var source = InheritedToolPatterns(parentSessionId, creatorSessionId);
        if (source.Count > 0) _toolPatterns.TryAdd(sessionId, source);
    }

    // A fork does without what its source does without, and what a session created by its parent or creator would.
    private void InheritForkToolPatterns(string forkSessionId, string sourceSessionId, string? parentSessionId, string? creatorSessionId)
    {
        var patterns = GetDisabledToolPatterns(sourceSessionId).Union(InheritedToolPatterns(parentSessionId, creatorSessionId), StringComparer.OrdinalIgnoreCase).ToArray();
        if (patterns.Length > 0) _toolPatterns.TryAdd(forkSessionId, patterns);
    }

    private IReadOnlyList<string> InheritedToolPatterns(string? parentSessionId, string? creatorSessionId)
        => GetDisabledToolPatterns(parentSessionId) is { Count: > 0 } fromParent ? fromParent : GetDisabledToolPatterns(creatorSessionId);

    // The built-in tools a session about to be created does without by the patterns it will inherit.
    private IReadOnlySet<string> InheritedPatternTools(SessionViewDescriptor session)
        => InheritedToolPatterns(session.ParentSessionId, session.CreatedBy?.SourceSessionId) is { Count: > 0 } patterns
            ? AgentToolPatterns.Expand(AgentToolPatterns.KnownToolNames, patterns)
            : new HashSet<string>();
}
