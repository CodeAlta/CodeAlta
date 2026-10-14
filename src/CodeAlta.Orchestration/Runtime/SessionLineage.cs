using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>A session below another one: started by it, or by one of the sessions it started.</summary>
/// <param name="Session">The stored session.</param>
/// <param name="ParentSessionId">The session it is shown under: the root, or another descendant.</param>
/// <param name="Depth">1 for a child of the root, 2 for a grandchild, and so on.</param>
public sealed record SessionDescendant(AgentSessionMetadata Session, string ParentSessionId, int Depth);

/// <summary>
/// The sessions that come from a session, wherever they are: in another project, in the chats, in another space.
/// A session is a child of the one its journal names as its parent, as the session that created it, or as the
/// parent its view keeps: the links a deletion refuses to break.
/// </summary>
public static class SessionLineage
{
    /// <summary>Tells whether <paramref name="session"/> names <paramref name="parentSessionId"/> as its parent.</summary>
    /// <param name="session">A stored session.</param>
    /// <param name="parentSessionId">The session that could be its parent.</param>
    /// <returns><see langword="true"/> when one of its links names that session.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="parentSessionId"/> is null.</exception>
    public static bool IsChildOf(AgentSessionMetadata session, string parentSessionId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(parentSessionId);
        return !string.Equals(session.SessionId, parentSessionId, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(session.ParentSessionId, parentSessionId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(session.CreatedBySessionId, parentSessionId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(session.ViewState?.ParentSessionId, parentSessionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lists the descendants of a session, the tree read from the top: each child follows its parent, the children of a
    /// session from the oldest. A session reached through two of its links is listed once, under the nearest one, and a
    /// cycle of links ends where it comes back.
    /// </summary>
    /// <param name="sessionId">The session whose descendants are listed; it is not listed itself.</param>
    /// <param name="sessions">Every stored session.</param>
    /// <returns>The descendants; empty when the session has none.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    public static IReadOnlyList<SessionDescendant> Descendants(string sessionId, IEnumerable<AgentSessionMetadata> sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(sessions);
        var all = sessions.ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sessionId };
        var children = new Dictionary<string, List<AgentSessionMetadata>>(StringComparer.OrdinalIgnoreCase);
        // Level by level, so a session is placed under the nearest of the sessions it names.
        var level = new List<string> { sessionId };
        while (level.Count > 0)
        {
            var next = new List<string>();
            foreach (var parent in level)
            {
                foreach (var candidate in all)
                {
                    if (seen.Contains(candidate.SessionId) || !IsChildOf(candidate, parent)) continue;
                    seen.Add(candidate.SessionId);
                    if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
                    list.Add(candidate);
                    next.Add(candidate.SessionId);
                }
            }

            level = next;
        }

        var result = new List<SessionDescendant>();
        void Add(string parent, int depth)
        {
            if (!children.TryGetValue(parent, out var list)) return;
            foreach (var child in list.OrderBy(static child => child.CreatedAt).ThenBy(static child => child.SessionId, StringComparer.Ordinal))
            {
                result.Add(new SessionDescendant(child, parent, depth));
                Add(child.SessionId, depth + 1);
            }
        }

        Add(sessionId, 1);
        return result;
    }
}
