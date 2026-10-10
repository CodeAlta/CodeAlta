using CodeAlta.Plugin.Statistics.Journal;

namespace CodeAlta.Plugin.Statistics.Facts;

/// <summary>
/// Where the reading of a journal stopped, as the store keeps it: the offset to resume from, the mark of the first line the
/// file had, and the state the facts need. Saved in the same transaction as the facts the reading added.
/// </summary>
/// <param name="Offset">The byte after the last complete line that was read.</param>
/// <param name="FirstLine">The mark of the first line of the file; null while the file had no complete first line.</param>
/// <param name="State">The state of the facts of the session.</param>
internal sealed record JournalCursor(long Offset, JournalFingerprint? FirstLine, SessionFactsState State)
{
    /// <summary>Gets the cursor of a journal that was never read.</summary>
    public static JournalCursor Start => new(0, null, new SessionFactsState());
}

/// <summary>The result of catching a session up.</summary>
internal sealed class CatchUpResult
{
    /// <summary>Gets or sets the facts the reading added, with the runs that changed and the session.</summary>
    public required FactBatch Batch { get; init; }

    /// <summary>Gets or sets the cursor to save with the facts and to resume from.</summary>
    public required JournalCursor Cursor { get; init; }

    /// <summary>Gets or sets what the reader did.</summary>
    public required JournalScanResult Scan { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the file was not the one the cursor belonged to (a header prepended to an old
    /// file, a file replaced when the provider changed while idle, a file shorter than the offset, a state of another version of
    /// the facts): the batch is then the facts of the whole session, and the store must remove the rows it has for the session
    /// before it saves them.
    /// </summary>
    public bool Restarted { get; init; }

    /// <summary>Gets or sets the counters of what the reducer met; for the tests and the harness.</summary>
    public required ReducerDiagnostics Diagnostics { get; init; }

    /// <summary>Gets a value indicating whether the journal was read to its last complete line.</summary>
    public bool ReachedEnd => Scan.ReachedEnd;
}

/// <summary>
/// The one operation of the history and of the flow: catch a session up from where its reading stopped. It reads the journal
/// from the offset of the cursor to its last complete line (or to a limit of bytes), turns the records into facts, and returns
/// them with the new cursor, to be saved together.
/// </summary>
/// <remarks>Not thread-safe; use one per thread of reading.</remarks>
internal sealed class SessionCatchUp : IDisposable
{
    private readonly JournalScanner _scanner;

    /// <summary>Initializes a catch-up operation.</summary>
    /// <param name="options">The limits of the reader; null for the defaults.</param>
    public SessionCatchUp(JournalScanOptions? options = null)
    {
        _scanner = new JournalScanner(options);
    }

    /// <summary>Catches a session up.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="stream">A readable, seekable stream of its journal, opened with sharing flags that let the session write.</param>
    /// <param name="cursor">Where the reading stopped; null to read from the start.</param>
    /// <param name="maxBytes">The reading stops at the first line end after this many bytes; <see cref="long.MaxValue"/> for no limit.</param>
    /// <param name="cancellationToken">A token that stops the reading at a line end; the cursor of the result is then valid.</param>
    /// <returns>The facts and the cursor.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public CatchUpResult CatchUp(string sessionId, Stream stream, JournalCursor? cursor, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(stream);

        var restarted = false;
        SessionFactsState? state = null;
        var offset = 0L;
        JournalFingerprint? firstLine = null;
        if (cursor is not null)
        {
            if (cursor.State.Version == SessionFactsState.CurrentVersion)
            {
                // The state of the cursor is the saved one: the reading works on a copy of it.
                state = cursor.State.Clone();
                offset = cursor.Offset;
                firstLine = cursor.FirstLine;
            }
            else
            {
                restarted = true;
            }
        }

        var reducer = new SessionFactsReducer(sessionId, state);
        var scan = _scanner.Scan(stream, offset, firstLine, reducer, maxBytes, cancellationToken);
        if (scan.RewriteDetected)
        {
            // Not the file the cursor was made for: its facts are of another file, so the session is read again from the start.
            restarted = true;
            reducer = new SessionFactsReducer(sessionId);
            scan = _scanner.Scan(stream, 0, null, reducer, maxBytes, cancellationToken);
        }

        var batch = reducer.TakeBatch();
        return new CatchUpResult
        {
            Batch = batch,
            Cursor = new JournalCursor(scan.EndOffset, scan.FirstLine, reducer.State),
            Scan = scan,
            Diagnostics = reducer.Diagnostics,
            Restarted = restarted,
        };
    }

    /// <inheritdoc />
    public void Dispose() => _scanner.Dispose();
}
