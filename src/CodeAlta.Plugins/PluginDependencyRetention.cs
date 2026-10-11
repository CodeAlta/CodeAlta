using CodeAlta.Agent;

namespace CodeAlta.Plugins;

/// <summary>Recognizes failed dependency releases that prohibit retiring plugin work.</summary>
public static class PluginDependencyRetention
{
    /// <summary>Inspects the complete exception graph without flattening or replacing its evidence.</summary>
    /// <param name="failure">The failure returned by the original operation.</param>
    /// <returns>Whether any nested failure requires dependencies to remain owned.</returns>
    /// <exception cref="ArgumentNullException">The failure is null.</exception>
    public static bool Contains(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(failure);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is AgentDependencyRetentionException or PluginEventDependencyException) return true;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return false;
    }
}
