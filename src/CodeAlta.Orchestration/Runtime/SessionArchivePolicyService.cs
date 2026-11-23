using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>One session as the archive policies see it.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ProjectId">Its project; null for a chat.</param>
/// <param name="ParentSessionId">The session it is under; null for a session at the top.</param>
/// <param name="Labels">The identifiers of its labels.</param>
/// <param name="AutomationId">The automation whose run started it; null for none.</param>
/// <param name="LastActiveAt">When it was last active.</param>
/// <param name="Archived">Whether it is archived already.</param>
public sealed record SessionArchiveCandidate(string SessionId, string? ProjectId, string? ParentSessionId, IReadOnlyList<string> Labels,
    string? AutomationId, DateTimeOffset LastActiveAt, bool Archived);

/// <summary>An archive policy with the sessions it applies to: those of one project, or those of every project and the chats.</summary>
/// <param name="Policy">The policy.</param>
/// <param name="ProjectId">The project whose configuration has it; null for the configuration of the user.</param>
public sealed record ScopedSessionArchivePolicy(SessionArchivePolicy Policy, string? ProjectId);

/// <summary>What one pass of the archive policies did.</summary>
/// <param name="Status"><c>completed</c>; <c>skipped</c> with no policy or while a window or the bookmarks are not ready; <c>failed</c>.</param>
/// <param name="Archived">The sessions archived, each with the sessions below it.</param>
/// <param name="Kept">How many sessions the policies would archive that were kept: shown in a window, bookmarked, at work, or with a session below them that is.</param>
public sealed record SessionArchivePolicyPass(string Status, IReadOnlyList<string> Archived, int Kept);

/// <summary>
/// Applies the archive policies of the configuration (<see cref="SessionArchivePolicy"/>): the sessions of some labels, or
/// the runs of some automations, are archived once inactive for some days or beyond the most recent ones. A session is
/// archived, never deleted, with the sessions below it, which follow it; a session below another one is never archived
/// alone. A session shown in a window, bookmarked, at work, waiting for an answer or with queued prompts is kept, and so is
/// a session with such a session below it.
/// </summary>
public sealed class SessionArchivePolicyService
{
    private readonly SessionRuntimeService _runtime;
    private readonly CodeAltaConfigStore _config;
    private readonly SpaceCatalog _spaces;
    private readonly SessionRetentionProtection _protection;
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? _automationNames;
    private readonly ISessionArchive? _archive;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="SessionArchivePolicyService"/> class.</summary>
    /// <param name="runtime">The runtime that lists and archives the sessions.</param>
    /// <param name="config">The configuration of the user and of the projects.</param>
    /// <param name="spaces">The projects, and the labels whose sub-labels a policy covers.</param>
    /// <param name="protection">What keeps a session shown in a window, bookmarked or claimed by work.</param>
    /// <param name="automationNames">The names of the automations by their id, for a policy that names an automation; null when the host has none.</param>
    /// <param name="archive">What archives a session for the owner of the host, with its refusals (a send under way, a question of an agent); null for the runtime alone.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public SessionArchivePolicyService(SessionRuntimeService runtime, CodeAltaConfigStore config, SpaceCatalog spaces, SessionRetentionProtection protection,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? automationNames = null, ISessionArchive? archive = null)
    {
        _archive = archive;
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(spaces);
        ArgumentNullException.ThrowIfNull(protection);
        (_runtime, _config, _spaces, _protection, _automationNames) = (runtime, config, spaces, protection, automationNames);
    }

    /// <summary>
    /// Applies the policies once: reads them, lists the sessions, and archives those they say. Two passes never run at once:
    /// a pass asked while one runs waits for it.
    /// </summary>
    /// <param name="now">The instant the ages are measured from.</param>
    /// <param name="cancellationToken">Stops the pass between two sessions.</param>
    /// <returns>What the pass did.</returns>
    /// <exception cref="OperationCanceledException">The pass was canceled.</exception>
    public async Task<SessionArchivePolicyPass> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var policies = await LoadPoliciesAsync(cancellationToken).ConfigureAwait(false);
            if (policies.Count == 0) return new("skipped", [], 0);
            // Until the windows and the bookmarks said what they hold, every session is kept.
            if (!_protection.IsReady) return new("skipped", [], 0);
            var labels = await _spaces.Labels.LoadAsync(cancellationToken).ConfigureAwait(false);
            var automations = policies.Any(static scoped => scoped.Policy.Automations.Count > 0) && _automationNames is not null
                ? await _automationNames(cancellationToken).ConfigureAwait(false)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            var candidates = new List<SessionArchiveCandidate>();
            var namesLabels = policies.Any(static scoped => scoped.Policy.Labels.Count > 0);
            await foreach (var session in _runtime.ListRecoverableSessionsAsync(cancellationToken).ConfigureAwait(false))
            {
                var archived = session.Status == SessionViewStatus.Archived || session.CachedViewState?.Archived == true;
                IReadOnlyList<string> sessionLabels = archived || !namesLabels ? [] : await _runtime.GetSessionLabelsAsync(session, cancellationToken).ConfigureAwait(false);
                candidates.Add(new(session.SessionId,
                    session.Kind == SessionViewKind.ProjectSession ? session.ProjectRef : null,
                    session.ParentSessionId,
                    sessionLabels,
                    session.CreatedBy is { Kind: AltaActorProvenance.AutomationKind, AutomationId: { } automation } ? automation : null,
                    session.LastActiveAt == default ? session.UpdatedAt : session.LastActiveAt,
                    archived));
            }

            var expired = SelectExpired(candidates, policies, id => LabelCatalog.Subtree(id, labels),
                // An automation that is gone is still named by its id, which its runs keep.
                reference => automations.ContainsKey(reference) ? reference
                    : automations.FirstOrDefault(pair => string.Equals(pair.Value, reference, StringComparison.OrdinalIgnoreCase)).Key ?? reference,
                now);
            var below = candidates.Where(static candidate => candidate.ParentSessionId is not null)
                .ToLookup(static candidate => candidate.ParentSessionId!, StringComparer.OrdinalIgnoreCase);
            var archivedNow = new List<string>();
            var kept = 0;
            foreach (var sessionId in expired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Family(sessionId, below).Any(_protection.IsSessionKept))
                {
                    kept++;
                    continue;
                }

                // A session that cannot be archived now is kept, and the pass goes on with the others.
                try
                {
                    var result = _archive is not null
                        ? await _archive.SetSessionArchivedAsync(sessionId, archived: true, withChildren: true).ConfigureAwait(false)
                        : await _runtime.SetSessionArchivedAsync(sessionId, archived: true, withChildren: true, cancellationToken).ConfigureAwait(false);
                    if (result.Status == "ok") archivedNow.Add(sessionId);
                    else kept++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    kept++;
                }
            }

            return new("completed", archivedNow, kept);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new("failed", [], 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tells which sessions the policies archive: of the sessions at the top that are not archived, those a policy applies
    /// to (one of their labels, a label above one of them, or the automation that started them) that are inactive for longer
    /// than its age and beyond the most recent ones it keeps, counted in each project (the chats together). With both an age
    /// and a number to keep, a session is archived only when it is both old and beyond the most recent ones.
    /// </summary>
    /// <param name="sessions">The sessions.</param>
    /// <param name="policies">The policies, each with the sessions it applies to.</param>
    /// <param name="labelSubtree">The identifiers of a label and of the labels under it.</param>
    /// <param name="automationId">The identifier of the automation a policy names by its id or its name; null for none.</param>
    /// <param name="now">The instant the ages are measured from.</param>
    /// <returns>The sessions to archive, the least recently active first, each once.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> SelectExpired(IReadOnlyList<SessionArchiveCandidate> sessions, IReadOnlyList<ScopedSessionArchivePolicy> policies,
        Func<string, IReadOnlyList<string>> labelSubtree, Func<string, string?> automationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(labelSubtree);
        ArgumentNullException.ThrowIfNull(automationId);
        var known = sessions.Select(static session => session.SessionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A session below another one follows it: only the sessions at the top are archived, with those below them.
        var top = sessions.Where(session => !session.Archived && (session.ParentSessionId is null || !known.Contains(session.ParentSessionId))).ToArray();
        var expired = new Dictionary<string, SessionArchiveCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var scoped in policies)
        {
            var policy = scoped.Policy;
            var labels = policy.Labels.SelectMany(labelSubtree).ToHashSet(StringComparer.Ordinal);
            var automations = policy.Automations.Select(automationId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matched = top.Where(session => (scoped.ProjectId is null || string.Equals(session.ProjectId, scoped.ProjectId, StringComparison.OrdinalIgnoreCase))
                && (session.Labels.Any(labels.Contains) || session.AutomationId is { } automation && automations.Contains(automation)));
            foreach (var project in matched.GroupBy(static session => session.ProjectId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                var place = 0;
                foreach (var session in project.OrderByDescending(static session => session.LastActiveAt).ThenBy(static session => session.SessionId, StringComparer.Ordinal))
                {
                    var beyond = policy.KeepLast is not { } keep || place >= keep;
                    var old = policy.OlderThan is not { } age || now - session.LastActiveAt > age;
                    place++;
                    if (beyond && old) expired.TryAdd(session.SessionId, session);
                }
            }
        }

        return [.. expired.Values.OrderBy(static session => session.LastActiveAt).Select(static session => session.SessionId)];
    }

    // The policies of the user, then those of each project that is not archived.
    private async Task<IReadOnlyList<ScopedSessionArchivePolicy>> LoadPoliciesAsync(CancellationToken cancellationToken)
    {
        var policies = _config.LoadGlobalSessionArchivePolicies().Select(static policy => new ScopedSessionArchivePolicy(policy, null)).ToList();
        foreach (var project in await _spaces.Projects.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (project.Archived || string.IsNullOrWhiteSpace(project.ProjectPath)) continue;
            policies.AddRange(_config.LoadProjectSessionArchivePolicies(project.ProjectPath).Select(policy => new ScopedSessionArchivePolicy(policy, project.Id)));
        }

        return policies;
    }

    // A session and every session below it.
    private static IEnumerable<string> Family(string sessionId, ILookup<string, SessionArchiveCandidate> below)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sessionId };
        var pending = new Queue<string>([sessionId]);
        while (pending.TryDequeue(out var current))
        {
            yield return current;
            foreach (var child in below[current])
            {
                if (seen.Add(child.SessionId)) pending.Enqueue(child.SessionId);
            }
        }
    }
}
