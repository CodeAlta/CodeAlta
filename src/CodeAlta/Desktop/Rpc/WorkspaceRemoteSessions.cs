using System.Collections.Concurrent;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    /// <summary>The prefix of the identifier and the path of the project that stands for a machine in the Explorer.</summary>
    internal const string MachineScopePrefix = "machine:";

    // The text bound of the titles of the list, as for the sessions of this process.
    private const int RemoteTitleLength = 256;

    /// <summary>Adds the sessions the paired machines last listed to the snapshot of this process, knowing no repository of its projects and no move.</summary>
    /// <param name="local">The snapshot of this process.</param>
    /// <param name="remotes">The hosts of the paired machines.</param>
    /// <returns>The snapshot with the rows of the machines and their sessions; <paramref name="local"/> when no machine is paired.</returns>
    internal static WorkspaceSnapshot WithRemoteSessions(WorkspaceSnapshot local, IReadOnlyList<RemoteSessionHost> remotes)
        => WithRemoteSessions(local, remotes, static _ => null);

    /// <summary>
    /// Adds the sessions the paired machines last listed to the snapshot of this process. A remote session goes under the
    /// project of this catalog it belongs to (<see cref="MatchProject"/>), or else under a project that stands for its
    /// machine (<see cref="MachineScopePrefix"/>); each one names its machine. A session this process lists itself keeps
    /// its row, unless this process gave it to the machine that lists it (a move). The project of a machine is listed for
    /// every paired machine, with its state, sessions or none: its row starts a session there.
    /// </summary>
    /// <param name="local">The snapshot of this process.</param>
    /// <param name="remotes">The hosts of the paired machines.</param>
    /// <param name="localLocation">The repository of a project of this catalog; null when it has none or it is not known.</param>
    /// <param name="movedAway">Whether this process gave a session to another machine; null for none.</param>
    /// <returns>The snapshot with the rows of the machines and their sessions; <paramref name="local"/> when no machine is paired.</returns>
    internal static WorkspaceSnapshot WithRemoteSessions(WorkspaceSnapshot local, IReadOnlyList<RemoteSessionHost> remotes,
        Func<WorkspaceProject, RepositoryLocation?> localLocation, Func<string, bool>? movedAway = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remotes);
        ArgumentNullException.ThrowIfNull(localLocation);
        if (remotes.Count == 0) return local;
        var projects = local.Projects.ToList();
        var sessions = local.Sessions.ToList();
        var known = sessions.Select(static session => session.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localProjects = local.Projects.Where(static project => !project.Archived).ToArray();
        foreach (var host in remotes)
        {
            var listing = host.Listing;
            var machineId = host.MachineId!;
            var machineName = Bounded(listing.Info?.Name ?? host.Machine.Name);
            var remoteProjects = (listing.Info?.Projects ?? []).ToDictionary(static project => project.Id, StringComparer.Ordinal);
            var providers = listing.Info?.Providers ?? [];
            var scope = MachineScopePrefix + machineId;
            var channel = host.ChannelPath switch
            {
                CodeAlta.Fleet.Server.FleetChannelPath.Relayed => "relayed",
                CodeAlta.Fleet.Server.FleetChannelPath.Direct => "direct",
                _ => null,
            };
            foreach (var session in listing.Sessions)
            {
                if (session.State == "archived") continue;
                if (!known.Add(session.Id))
                {
                    // The read-only copy of a session moved to that machine gives its row to the live one.
                    if (movedAway is null || !movedAway(session.Id) || sessions.FindIndex(value => value.MachineId is null
                            && string.Equals(value.Id, session.Id, StringComparison.OrdinalIgnoreCase)) is not (>= 0 and var index)) continue;
                    sessions.RemoveAt(index);
                }

                var remoteProject = session.ProjectId is { } id && remoteProjects.TryGetValue(id, out var found) ? found : null;
                var match = MatchProject(session.ProjectId, remoteProject, localProjects, localLocation);
                var title = Bounded(string.IsNullOrWhiteSpace(session.Title) ? "Session" : session.Title);
                sessions.Add(new WorkspaceSession(session.Id, title, match?.Path ?? scope, session.ProviderKey, session.UpdatedAt, title, session.Title.Length > RemoteTitleLength,
                    session.ParentSessionId, "project", match?.Id ?? scope, null, session.CreatedAt)
                {
                    MachineId = machineId,
                    MachineName = machineName,
                    MachineChannel = channel,
                    RemoteState = session.State,
                    RemoteQueuedPosition = session.QueuedPosition,
                    RemoteProject = remoteProject is null ? null : Bounded(remoteProject.Name),
                    MachineProviderName = (session.ProviderKey is { } key ? providers.FirstOrDefault(provider => string.Equals(provider.Key, key, StringComparison.Ordinal))
                        : providers.FirstOrDefault(static provider => provider.IsDefault)) is { } named ? Bounded(named.Name) : null,
                });
            }

            // Listed while the machine is paired, whatever it lists: a machine with no session yet is where one starts.
            projects.Add(new WorkspaceProject(scope, machineName, scope, false) { MachineId = machineId, MachineState = RemoteSessionHost.Describe(host.State) });
        }

        return local with { Projects = [.. projects], Sessions = [.. sessions] };
    }

    /// <summary>
    /// The project of this catalog a session of a paired machine belongs to. A project of the machine in a repository with
    /// a remote is the project of this catalog in the same folder of the same repository (its repository key and its path
    /// in the checkout), wherever each machine keeps it, and no other: the one with the same id, else the only one of the
    /// same name, else the first, when this catalog has several checkouts of it. Only a project of the machine with no
    /// repository key falls back to the same id, else the only project of the same name.
    /// </summary>
    /// <param name="sessionProjectId">The project the machine lists the session in; null for a session of no project.</param>
    /// <param name="remoteProject">That project as the machine describes it; null when it does not.</param>
    /// <param name="localProjects">The projects of this catalog that are not archived.</param>
    /// <param name="localLocation">The repository of a project of this catalog; null when it has none or it is not known.</param>
    /// <returns>The project; null when none is the same: the session goes under its machine.</returns>
    internal static WorkspaceProject? MatchProject(string? sessionProjectId, FleetProjectInfo? remoteProject, IReadOnlyList<WorkspaceProject> localProjects,
        Func<WorkspaceProject, RepositoryLocation?> localLocation)
    {
        ArgumentNullException.ThrowIfNull(localProjects);
        ArgumentNullException.ThrowIfNull(localLocation);
        if (remoteProject?.RepositoryKey is { Length: > 0 } key)
        {
            var remote = new RepositoryLocation(key, remoteProject.RepositoryPath ?? string.Empty);
            var same = localProjects.Where(project => project.MachineId is null && RepositoryKey.SameLocation(localLocation(project), remote)).ToArray();
            return same.Length <= 1 ? same.FirstOrDefault()
                : same.FirstOrDefault(project => project.Id == remoteProject.Id) ?? Single(SameName(same, remoteProject.Name)) ?? same[0];
        }

        return localProjects.FirstOrDefault(project => project.MachineId is null && project.Id == sessionProjectId)
            ?? (remoteProject is null ? null : Single(SameName(localProjects.Where(static project => project.MachineId is null), remoteProject.Name)));

        static IEnumerable<WorkspaceProject> SameName(IEnumerable<WorkspaceProject> candidates, string name)
            => candidates.Where(project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase));

        static WorkspaceProject? Single(IEnumerable<WorkspaceProject> candidates)
        {
            using var items = candidates.GetEnumerator();
            if (!items.MoveNext()) return null;
            var first = items.Current;
            return items.MoveNext() ? null : first;
        }
    }

    /// <summary>
    /// Reads the repositories of the projects of this catalog, when a paired machine lists a session in a project with a
    /// repository key: only then are they compared (<see cref="MatchProject"/>). A few folders at a time, each kept a
    /// while by <paramref name="locations"/>.
    /// </summary>
    /// <param name="local">The snapshot of this process.</param>
    /// <param name="remotes">The hosts of the paired machines.</param>
    /// <param name="locations">The repositories of folders, read once in a while.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The repository of a project of this catalog; null when it has none, or nothing needs it.</returns>
    internal static async Task<Func<WorkspaceProject, RepositoryLocation?>> ReadLocalLocationsAsync(WorkspaceSnapshot local, IReadOnlyList<RemoteSessionHost> remotes,
        RepositoryLocationCache locations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remotes);
        ArgumentNullException.ThrowIfNull(locations);
        var keyed = remotes.Any(static host => host.Listing is { Sessions.Count: > 0, Info.Projects: { } projects } && projects.Any(static project => project.RepositoryKey is not null));
        if (!keyed) return static _ => null;
        var found = new ConcurrentDictionary<string, RepositoryLocation?>(StringComparer.Ordinal);
        var projects = local.Projects.Where(static project => !project.Archived && project.MachineId is null && !string.IsNullOrWhiteSpace(project.Path)).ToArray();
        await Parallel.ForEachAsync(projects, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (project, token) => found[project.Id] = await locations.GetAsync(project.Path, token).ConfigureAwait(false)).ConfigureAwait(false);
        return project => found.TryGetValue(project.Id, out var location) ? location : null;
    }

    private static string Bounded(string value)
    {
        if (value.Length <= RemoteTitleLength) return value;
        var length = char.IsHighSurrogate(value[RemoteTitleLength - 1]) ? RemoteTitleLength - 1 : RemoteTitleLength;
        return value[..length];
    }
}
