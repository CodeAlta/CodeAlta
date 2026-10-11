using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Journal;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.History;

internal sealed partial class StatisticsEngine : ISessionStatisticsRetirementArchive
{
    private async Task ReplaceArchivedProjectionAsync(string sessionId, CancellationToken token)
    {
        lock (_gate)
        {
            if (_choice?.Kind != HistoryChoiceKind.All || _floorQuarter != int.MinValue || _stopped)
            {
                throw new InvalidDataException("Saved Statistics remain effective until full surviving-journal history is selected and verified.");
            }
        }

        await using var source = await StatisticsArchiveSource.OpenAsync(_journals, sessionId, token).ConfigureAwait(false);
        if (source is null)
        {
            return;
        }

        using var reader = new SessionCatchUp(new JournalScanOptions { RetirementSessionId = sessionId });
        var read = reader.CatchUp(sessionId, source.Stream, null, long.MaxValue, token);
        token.ThrowIfCancellationRequested();
        if (!IsCompleteSource(read, source.Stream.Length))
        {
            throw new InvalidDataException("The returning journal is incomplete or unverified; saved Statistics remain effective.");
        }

        var identity = await source.VerifyAsync(read.Scan.SourceSha256, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The returning journal changed; saved Statistics remain effective.");
        var applied = await _store.ApplyAsync(new ApplyRequest { SessionId = sessionId, Batch = read.Batch, Cursor = read.Cursor, Replace = true, VerifiedArchiveReplacement = true,
            FileLength = identity.Length, FileStampTicks = identity.LastWriteUtc.UtcTicks }, token).ConfigureAwait(false);
        RaiseDataChanged(applied.ChangedDays, [sessionId]);
    }

    private static bool IsCompleteSource(CatchUpResult read, long length)
        => read.ReachedEnd && !read.Scan.Canceled && read.Scan.PendingBytes == 0 && read.Scan.EndOffset == length && read.Scan.SourceSha256 is not null
            && read.Scan.MalformedLines == 0 && read.Scan.UnknownLines == 0 && read.Scan.OversizeRecords == 0 && read.Cursor.State.OpenRuns.Count == 0 && read.Batch.Session is not null;

    // Saves what the tables already hold, without opening the journal: retention must never read old sessions. Only a session
    // that Statistics has read to the end of the file as it is now (same length and write time) is saved; any other is
    // deleted without a saved copy, which loses its numbers only after a reset of Statistics.
    public async ValueTask<SessionStatisticsRetirementResult> PrepareAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_store.HasArchive || Volatile.Read(ref _disposed) != 0)
        {
            return new(null, "The Statistics archive participant is unavailable or disabled.");
        }

        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _stepGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return new(null, "Statistics was disabled during retirement preparation.");
                }

                var row = await _store.GetJournalAsync(sessionId, cancellationToken).ConfigureAwait(false);
                var file = await _journals.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (row is null || file is null || row.Deleted || row.FactsVersion != SessionFactsState.CurrentVersion || row.FloorQuarter != int.MinValue
                    || row.Offset != file.Length || row.FileLength != file.Length || row.FileStampTicks != file.LastWriteUtc.UtcTicks)
                {
                    return new(null, "Statistics has not read the whole session; it is deleted without saved Statistics.");
                }

                // The file is only described, never read: its length and write time are what Statistics has read.
                var source = new SessionStatisticsSource(sessionId, file.Path, file.Length, file.LastWriteUtc, Sha256: null);
                var entry = await _store.SaveArchiveAsync(sessionId, source, cancellationToken).ConfigureAwait(false);
                return new(new SessionStatisticsRetirementReceipt(source, entry.Generation, entry.PayloadSha256), null);
            }
            finally
            {
                _stepGate.Release();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(null, "Saving Statistics failed; the session is deleted without saved Statistics: " + exception.Message);
        }
    }

    public async ValueTask<bool> ReconcileDeletedAsync(SessionStatisticsRetirementReceipt receipt, bool artifactsRemaining, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!_store.HasArchive || Volatile.Read(ref _disposed) != 0) return false;
        await _stepGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || await _journals.GetAsync(receipt.Source.SessionId, cancellationToken).ConfigureAwait(false) is not null
                || !await _store.VerifyArchiveAsync(receipt, cancellationToken).ConfigureAwait(false)) return false;
            var reconciled = await _store.ReconcileDeletedArchiveAsync(receipt, artifactsRemaining, cancellationToken).ConfigureAwait(false);
            if (reconciled)
            {
                Publish();
                DataChanged?.Invoke(new StatisticsDataChange(Interlocked.Increment(ref _revision), 10101, 99991231, [receipt.Source.SessionId]));
            }

            return reconciled;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _stepGate.Release();
        }
    }

    private async ValueTask ReconcileArchivesAsync(CancellationToken token)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var file in _journals.ListAsync(token).ConfigureAwait(false))
        {
            present.Add(file.SessionId);
        }

        var rows = await _store.ListJournalsAsync(token).ConfigureAwait(false);
        await _store.MarkDeletedAsync(rows.Keys.Where(id => !present.Contains(id)).ToArray(), token).ConfigureAwait(false);
        await _store.RestoreArchivesAsync(token, present).ConfigureAwait(false);
    }
}
