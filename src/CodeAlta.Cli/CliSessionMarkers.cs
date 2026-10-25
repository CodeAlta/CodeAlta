using System.Collections.Concurrent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Cli;

/// <summary>
/// The markers of the sessions a visible run writes (<see cref="SessionMarkerStore"/>, writer <c>altacli</c>): CodeAlta
/// Desktop and TUI list and refresh the sessions the markers name, since the run writes no application database of theirs.
/// A session opened by the run, new or resumed, and every session it starts get one, each once its journal exists: at the
/// opening when it already does, else at the first event of the session, and at the end of the run at the latest. Without
/// a marker, a session written while Desktop is closed, or one that ends between two of its looks at the leases, would
/// not be found.
/// </summary>
internal sealed class CliSessionMarkers
{
    private readonly SessionMarkerStore _store;
    private readonly FileSystemSessionJournalCatalog _journals;
    private readonly SessionRuntimeService _runtime;
    private readonly string _rootSessionId;
    private readonly ConcurrentDictionary<string, byte> _written = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the markers of the tree of a session.</summary>
    /// <param name="catalog">The roots of the run.</param>
    /// <param name="runtime">The runtime, which knows the sessions the root started.</param>
    /// <param name="rootSessionId">The session the run opened.</param>
    public CliSessionMarkers(CatalogOptions catalog, SessionRuntimeService runtime, string rootSessionId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(runtime);
        _store = new SessionMarkerStore(catalog);
        _journals = new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(catalog.StateRoot));
        _runtime = runtime;
        _rootSessionId = rootSessionId;
    }

    /// <summary>Gets whether the marker of a session was written.</summary>
    public bool IsWritten(string sessionId) => _written.ContainsKey(sessionId);

    /// <summary>Writes the marker of a session of the tree when its journal exists and it was not written yet.</summary>
    public async ValueTask TryWriteAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (IsWritten(sessionId)) return;
        try
        {
            if (await _journals.GetAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } journal) return;
            if (!_written.TryAdd(sessionId, 0)) return;
            var relative = Path.GetRelativePath(_store.SessionsDirectory, journal.Path).Replace(Path.DirectorySeparatorChar, '/');
            _store.Write(new SessionMarker(sessionId, relative, DateTimeOffset.UtcNow, SessionLeaseOwner.CliKind, Environment.ProcessId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The session stays in the journals: a later start of Desktop lists it when it rebuilds its database.
            LogManager.GetLogger("CodeAlta.Cli").Warn($"The marker of session {sessionId} was not written: {exception.Message}");
        }
    }

    /// <summary>Writes the markers of the root and of every session it started that have none yet.</summary>
    public async ValueTask TryWriteTreeAsync(CancellationToken cancellationToken)
    {
        foreach (var sessionId in _runtime.GetSessionTree(_rootSessionId)) await TryWriteAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }
}
