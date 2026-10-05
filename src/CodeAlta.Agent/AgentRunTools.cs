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
    private List<AgentToolDefinition>? _added;

    /// <summary>Creates the tools of a run that has none registered yet.</summary>
    public AgentRunTools()
    {
    }

    internal AgentRunTools(IEnumerable<string> registeredNames) => _names.UnionWith(registeredNames);

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
                (_added ??= []).Add(tool);
                added.Add(tool.Spec.Name);
            }
        }

        return added;
    }

    /// <summary>Whether a tool of this name is registered for the run.</summary>
    /// <exception cref="ArgumentException"><paramref name="toolName"/> is blank.</exception>
    public bool Contains(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        lock (_gate) return _names.Contains(AgentToolBridge.GetRegisteredToolName(toolName));
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
