using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using XenoAtom.Logging;

namespace CodeAlta.Cli;

/// <summary>
/// The marker of a session a visible run created (<see cref="SessionMarkerStore"/>, writer <c>altacli</c>): CodeAlta
/// Desktop and TUI list the sessions the markers name, since the run writes no application database of theirs. The marker
/// is written once the journal exists: at the opening when it already does, else at the first event of the session, and at
/// the end of the run at the latest.
/// </summary>
internal sealed class CliSessionMarkers
{
    private readonly SessionMarkerStore _store;
    private readonly FileSystemSessionJournalCatalog _journals;
    private readonly string _sessionId;
    private int _written;

    public CliSessionMarkers(CatalogOptions catalog, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _store = new SessionMarkerStore(catalog);
        _journals = new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(catalog.StateRoot));
        _sessionId = sessionId;
    }

    /// <summary>Gets whether the marker was written.</summary>
    public bool Written => Volatile.Read(ref _written) != 0;

    /// <summary>Writes the marker when the journal exists and it was not written yet.</summary>
    public async ValueTask TryWriteAsync(CancellationToken cancellationToken)
    {
        if (Written) return;
        try
        {
            if (await _journals.GetAsync(_sessionId, cancellationToken).ConfigureAwait(false) is not { } journal) return;
            if (Interlocked.Exchange(ref _written, 1) != 0) return;
            var relative = Path.GetRelativePath(_store.SessionsDirectory, journal.Path).Replace(Path.DirectorySeparatorChar, '/');
            _store.Write(new SessionMarker(_sessionId, relative, DateTimeOffset.UtcNow, SessionLeaseOwner.CliKind, Environment.ProcessId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The session stays in the journals: a later start of Desktop lists it when it rebuilds its database.
            LogManager.GetLogger("CodeAlta.Cli").Warn($"The marker of session {_sessionId} was not written: {exception.Message}");
        }
    }
}
