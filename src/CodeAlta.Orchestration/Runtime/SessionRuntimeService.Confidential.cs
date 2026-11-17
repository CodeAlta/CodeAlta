using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.Plugins;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    /// <summary>What a confidential session that cannot pseudonymize what it sends tells the user: nothing was sent.</summary>
    internal const string PseudonymizerNotReadyMessage =
        "This project is pseudonymized and the Pseudonymizer plugin is off: turn it on in Settings > Plugins, or open Pseudonymization from the menu of the project. Nothing was sent.";

    /// <summary>What a session whose project configuration cannot be read tells the user: nothing was sent.</summary>
    internal const string ProjectConfigurationUnreadableMessage =
        "The configuration of the project of this session (.alta/config.toml) cannot be read, so CodeAlta cannot tell whether the project is pseudonymized: correct the file. Nothing was sent.";

    /// <summary>What a confidential session that cannot record that it is tells the user: nothing was sent.</summary>
    internal const string ConfidentialNotRecordedMessage =
        "This session is confidential and CodeAlta could not record it in the files of the session, so nothing was sent: send again.";

    /// <summary>
    /// Gets or sets what gives the boundary the requests of a confidential session pass through before its provider (the one of
    /// the plugins, <see cref="PluginOrchestrationBridge.GetProviderBoundary"/>), asked at each request. <see langword="null"/>,
    /// the default, gives none: a confidential session then sends nothing.
    /// </summary>
    public Func<SessionToolHookScope, IAgentProviderBoundary?>? ProviderBoundaryOf { get; set; }

    /// <summary>
    /// Tells whether a session is confidential: it recorded it once, its project is secure (<c>[privacy] pseudonymize</c>
    /// of the project), or a session of its lineage is confidential.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>
    /// Whether what it sends must be pseudonymized. A lineage that cannot be read is taken as confidential, and so is a
    /// project whose configuration cannot be read.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public async Task<bool> IsConfidentialAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return (await ReadConfidentialLineageAsync(session, cancellationToken).ConfigureAwait(false)).IsConfidential(_configStore) != false;
    }

    // The lineage of a session as its confidentiality reads it: whether one of its sessions recorded it, and the folders of
    // their projects, whose setting may change while it runs. What cannot be read is taken as recorded: the session then
    // sends nothing it cannot pseudonymize.
    private async Task<ConfidentialLineage> ReadConfidentialLineageAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { session.SessionId };
        var projects = new List<string?>();
        var pending = new Queue<SessionViewDescriptor>([session]);
        while (pending.TryDequeue(out var current))
        {
            if (seen.Count > SessionToolGuards.MaximumLineageDepth) return ConfidentialLineage.Unreadable;
            if ((await ReadLatestLocalStateAsync(current.SessionId, current.CreatedAt, cancellationToken).ConfigureAwait(false))?.Confidential == true)
                return new ConfidentialLineage(Recorded: true, projects);
            try
            {
                projects.Add((await ResolveProjectAsync(current, cancellationToken).ConfigureAwait(false))?.ProjectPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return ConfidentialLineage.Unreadable;
            }

            // Where it comes from: its parent, the session that created it and the one it was forked from, each of which may
            // be confidential. One that no longer exists ends its branch of the lineage.
            foreach (var origin in (string?[])[current.ParentSessionId, current.CreatedBy?.SourceSessionId, current.ForkedFrom?.SessionId])
            {
                if (NormalizeOptionalText(origin) is not { } id || !seen.Add(id)) continue;
                if (await TryGetRecoverableSessionAsync(id, cancellationToken).ConfigureAwait(false) is { } found) pending.Enqueue(found);
            }
        }

        return new ConfidentialLineage(Recorded: false, projects);
    }

    // What the session asks at each request whether it is confidential, and for its boundary then. It is built even while
    // the session is not confidential: the setting of a project of its lineage may change while it runs.
    private async ValueTask<IAgentProviderBoundarySource> CreateProviderBoundarySourceAsync(SessionViewDescriptor session, SessionToolHookScope scope,
        CancellationToken cancellationToken)
        => new SessionBoundarySource(this, session, scope, await ReadConfidentialLineageAsync(session, cancellationToken).ConfigureAwait(false));

    // A session that became confidential records it: it stays so once the setting that made it so is turned off, since its
    // history would then go in clear. The record is written by the actor of the session, before the first request that goes
    // through the boundary: false when it could not be written, and that request is then not sent.
    private async Task<bool> RecordConfidentialAsync(SessionViewDescriptor session)
    {
        try
        {
            return await GetActorForWork(session.SessionId).QueryAsync(async actorCancellationToken =>
            {
                var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                if (localState?.Confidential == true) return true;
                if (localState is null)
                {
                    localState = new SessionViewLocalState();
                    CopySessionMetadata(session, localState);
                }

                localState.Confidential = true;
                await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    // Whether a session of the lineage recorded that it is confidential, and the folders of the projects of the lineage.
    private sealed record ConfidentialLineage(bool Recorded, IReadOnlyList<string?> ProjectPaths)
    {
        public static ConfidentialLineage Unreadable { get; } = new(Recorded: true, []);

        // Null when no session recorded it, no project asks for it and the configuration of a project cannot be read: the
        // session may be confidential, and is not made so for good by a file that cannot be read for a moment.
        public bool? IsConfidential(CodeAltaConfigStore config)
        {
            if (Recorded) return true;
            bool? confidential = false;
            foreach (var path in ProjectPaths)
            {
                switch (config.ReadProjectPseudonymized(path))
                {
                    case true: return true;
                    case null: confidential = null; break;
                }
            }

            return confidential;
        }
    }

    private sealed class SessionBoundarySource(SessionRuntimeService owner, SessionViewDescriptor session, SessionToolHookScope scope, ConfidentialLineage lineage)
        : IAgentProviderBoundarySource
    {
        // Once confidential, always: the answer is not read again. Until then, a request reads again only the setting of the
        // projects of the lineage: the records of the lineage were read when the session was attached, and only this
        // process writes them.
        private volatile bool _confidential = lineage.IsConfidential(owner._configStore) == true;
        private volatile bool _recorded;

        public bool IsConfidential => _confidential;

        public async ValueTask<IAgentProviderBoundary?> GetBoundaryAsync(AgentTurnRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!_confidential)
            {
                switch (lineage.IsConfidential(owner._configStore))
                {
                    case false: return null;
                    case null: throw new AgentProviderBoundaryException(ProjectConfigurationUnreadableMessage);
                }

                _confidential = true;
            }

            // Recorded before the first request that goes through the boundary: a session that sent pseudonyms is never read as
            // one that sends as it is. A record that could not be written is tried again at the next request.
            if (!_recorded)
            {
                if (!await owner.RecordConfidentialAsync(session).ConfigureAwait(false)) throw new AgentProviderBoundaryException(ConfidentialNotRecordedMessage);
                _recorded = true;
            }

            return owner.ProviderBoundaryOf?.Invoke(scope) ?? throw new AgentProviderBoundaryException(PseudonymizerNotReadyMessage);
        }
    }
}
