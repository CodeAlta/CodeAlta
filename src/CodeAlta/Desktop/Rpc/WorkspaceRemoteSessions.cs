using CodeAlta.Desktop.Fleet;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    /// <summary>The prefix of the identifier and the path of the project that stands for a machine in the Explorer.</summary>
    internal const string MachineScopePrefix = "machine:";

    // The text bound of the titles of the list, as for the sessions of this process.
    private const int RemoteTitleLength = 256;

    /// <summary>
    /// Adds the sessions the paired machines last listed to the snapshot of this process. A remote session goes under the
    /// project of this catalog it belongs to (the same project id, or the only project of that name), or else under a
    /// project that stands for its machine (<see cref="MachineScopePrefix"/>); each one names its machine. A session this
    /// process lists itself keeps its row.
    /// </summary>
    /// <param name="local">The snapshot of this process.</param>
    /// <param name="remotes">The hosts of the paired machines.</param>
    /// <returns>The snapshot with the remote sessions; <paramref name="local"/> when no machine lists any.</returns>
    internal static WorkspaceSnapshot WithRemoteSessions(WorkspaceSnapshot local, IReadOnlyList<RemoteSessionHost> remotes)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remotes);
        if (remotes.All(static host => host.Listing.Sessions.Count == 0)) return local;
        var projects = local.Projects.ToList();
        var sessions = local.Sessions.ToList();
        var known = sessions.Select(static session => session.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localProjects = local.Projects.Where(static project => !project.Archived).ToArray();
        foreach (var host in remotes)
        {
            var listing = host.Listing;
            if (listing.Sessions.Count == 0) continue;
            var machineId = host.MachineId!;
            var machineName = Bounded(listing.Info?.Name ?? host.Machine.Name);
            var remoteProjects = (listing.Info?.Projects ?? []).ToDictionary(static project => project.Id, StringComparer.Ordinal);
            var scope = MachineScopePrefix + machineId;
            var grouped = false;
            foreach (var session in listing.Sessions)
            {
                if (session.State == "archived" || !known.Add(session.Id)) continue;
                var remoteProject = session.ProjectId is { } id && remoteProjects.TryGetValue(id, out var found) ? found : null;
                var match = localProjects.FirstOrDefault(project => project.Id == session.ProjectId)
                    ?? (remoteProject is null ? null : Single(localProjects.Where(project => string.Equals(project.Name, remoteProject.Name, StringComparison.OrdinalIgnoreCase))));
                if (match is null) grouped = true;
                var title = Bounded(string.IsNullOrWhiteSpace(session.Title) ? "Session" : session.Title);
                sessions.Add(new WorkspaceSession(session.Id, title, match?.Path ?? scope, session.ProviderKey, session.UpdatedAt, title, session.Title.Length > RemoteTitleLength,
                    session.ParentSessionId, "project", match?.Id ?? scope, null, session.CreatedAt)
                {
                    MachineId = machineId,
                    MachineName = machineName,
                    RemoteState = session.State,
                    RemoteProject = remoteProject is null ? null : Bounded(remoteProject.Name),
                });
            }

            if (grouped) projects.Add(new WorkspaceProject(scope, machineName, scope, false) { MachineId = machineId });
        }

        return local with { Projects = [.. projects], Sessions = [.. sessions] };

        static WorkspaceProject? Single(IEnumerable<WorkspaceProject> candidates)
        {
            using var items = candidates.GetEnumerator();
            if (!items.MoveNext()) return null;
            var first = items.Current;
            return items.MoveNext() ? null : first;
        }
    }

    private static string Bounded(string value)
    {
        if (value.Length <= RemoteTitleLength) return value;
        var length = char.IsHighSurrogate(value[RemoteTitleLength - 1]) ? RemoteTitleLength - 1 : RemoteTitleLength;
        return value[..length];
    }
}
