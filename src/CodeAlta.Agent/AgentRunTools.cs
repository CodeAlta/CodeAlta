using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Agent;

/// <summary>
/// The tools of one running turn, open to additions: a tool call can register tools, and the model sees them
/// from its next request of the same run.
/// </summary>
/// <remarks>
/// The run owns it. Tools added here last until the run ends: the host prepares the session's own tools
/// again for the next run. It is how a tool that turns something on (an MCP server) makes it usable at once,
/// without ending the turn.
/// </remarks>
public sealed class AgentRunTools
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentToolSpec> _deferred = new(StringComparer.Ordinal);
    private List<AgentToolDefinition>? _added;
    private List<string>? _loaded;

    /// <summary>Creates the tools of a run that has none registered yet.</summary>
    public AgentRunTools()
    {
    }

    internal AgentRunTools(IEnumerable<string> registeredNames) => _names.UnionWith(registeredNames);

    internal AgentRunTools(IEnumerable<string> registeredNames, IEnumerable<AgentToolSpec> deferredTools)
        : this(registeredNames)
    {
        foreach (var spec in deferredTools) _deferred[AgentToolBridge.GetRegisteredToolName(spec.Name)] = spec;
    }

    /// <summary>Registers tools for the rest of the run.</summary>
    /// <param name="tools">The tools to add.</param>
    /// <returns>The names of the tools that were added. A tool whose name is already registered is left as it is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tools"/> or one of its items is null.</exception>
    public IReadOnlyList<string> Add(IReadOnlyList<AgentToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var added = new List<string>(tools.Count);
        lock (_gate)
        {
            foreach (var tool in tools)
            {
                ArgumentNullException.ThrowIfNull(tool);
                // The name the model is given, which is the one a second registration would collide on.
                if (!_names.Add(AgentToolBridge.GetRegisteredToolName(tool.Spec.Name))) continue;
                if (tool.Spec.Deferred) _deferred[AgentToolBridge.GetRegisteredToolName(tool.Spec.Name)] = tool.Spec;
                (_added ??= []).Add(tool);
                added.Add(tool.Spec.Name);
            }
        }

        return added;
    }

    /// <summary>
    /// Registers tools for the rest of the run, each in place of the registered tool of its name: a plugin that
    /// was built again gives the run the new version of its tools.
    /// </summary>
    /// <param name="tools">The tools to register.</param>
    /// <returns>The names of the tools.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tools"/> or one of its items is null.</exception>
    public IReadOnlyList<string> Set(IReadOnlyList<AgentToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var names = new List<string>(tools.Count);
        lock (_gate)
        {
            foreach (var tool in tools)
            {
                ArgumentNullException.ThrowIfNull(tool);
                var name = AgentToolBridge.GetRegisteredToolName(tool.Spec.Name);
                _names.Add(name);
                if (tool.Spec.Deferred) _deferred[name] = tool.Spec;
                else _deferred.Remove(name);
                // The last one registered for a name is the one the run takes.
                (_added ??= []).RemoveAll(pending => string.Equals(AgentToolBridge.GetRegisteredToolName(pending.Spec.Name), name, StringComparison.Ordinal));
                _added.Add(tool);
                names.Add(tool.Spec.Name);
            }
        }

        return names;
    }

    /// <summary>Whether a tool of this name is registered for the run.</summary>
    /// <exception cref="ArgumentException"><paramref name="toolName"/> is blank.</exception>
    public bool Contains(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        lock (_gate) return _names.Contains(AgentToolBridge.GetRegisteredToolName(toolName));
    }

    /// <summary>Whether a tool of this name is registered for the run as a deferred tool (<see cref="AgentToolSpec.Deferred"/>).</summary>
    /// <exception cref="ArgumentException"><paramref name="toolName"/> is blank.</exception>
    public bool IsDeferred(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        lock (_gate) return _deferred.ContainsKey(AgentToolBridge.GetRegisteredToolName(toolName));
    }

    /// <summary>Gets the deferred tools of the run (<see cref="AgentToolSpec.Deferred"/>), in no order.</summary>
    public IReadOnlyList<AgentToolSpec> GetDeferredTools()
    {
        lock (_gate) return [.. _deferred.Values];
    }

    /// <summary>
    /// Records that the session loaded tools, by the references of what they come from (<c>mcp:server/tool</c>,
    /// <c>mcp:server</c>): the session journals them and keeps them in its state, and the plugins are given them
    /// at each later run, a resumed session included, to register those tools again.
    /// </summary>
    /// <remarks>It does not register a tool: <see cref="Add"/> does that for the rest of this run.</remarks>
    /// <param name="references">The references, in the form of the plugin that registers their tools.</param>
    /// <exception cref="ArgumentNullException"><paramref name="references"/> is null.</exception>
    public void KeepLoaded(IEnumerable<string> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        lock (_gate)
        {
            foreach (var reference in references)
            {
                if (string.IsNullOrWhiteSpace(reference)) continue;
                var trimmed = reference.Trim();
                if (_loaded?.Contains(trimmed, StringComparer.Ordinal) != true) (_loaded ??= []).Add(trimmed);
            }
        }
    }

    // The session takes the references kept since a tool call, to journal them.
    internal IReadOnlyList<string>? TakeLoaded()
    {
        lock (_gate)
        {
            var loaded = _loaded;
            _loaded = null;
            return loaded;
        }
    }

    // A tool added since the last model request, which a script may call at once: an MCP server it activated.
    internal AgentToolDefinition? FindPending(string toolName)
    {
        var name = AgentToolBridge.GetRegisteredToolName(toolName);
        lock (_gate)
        {
            return _added?.LastOrDefault(tool => string.Equals(AgentToolBridge.GetRegisteredToolName(tool.Spec.Name), name, StringComparison.Ordinal));
        }
    }

    // The run takes what was added since its last model request.
    internal IReadOnlyList<AgentToolDefinition>? Take()
    {
        lock (_gate)
        {
            var added = _added;
            _added = null;
            return added;
        }
    }
}
