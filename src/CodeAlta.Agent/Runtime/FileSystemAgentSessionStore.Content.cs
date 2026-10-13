using System.Text;

namespace CodeAlta.Agent.Runtime;

public sealed partial class FileSystemAgentSessionStore
{
    /// <summary>
    /// Finds a completed message of the user or of the assistant in the journal of a session by what names it: its
    /// run, its kind and its content identifier. A byte offset does not name a record: it moves when the first line
    /// of the journal is replaced.
    /// </summary>
    /// <param name="sessionId">An existing session identifier, never a path.</param>
    /// <param name="runId">The run of the message; null for a message that names no run, which is looked for in the whole journal.</param>
    /// <param name="kind"><see cref="AgentContentKind.User"/> or <see cref="AgentContentKind.Assistant"/>.</param>
    /// <param name="contentId">The content identifier of the message; null for the last message of the kind that the run wrote.</param>
    /// <param name="cancellationToken">Cancels lookup, gate admission or reading.</param>
    /// <returns>The record with the offset the journal holds it at now; null when the journal does not hold it.</returns>
    /// <remarks>
    /// The journal is read once by the two ends of each record to find the turn of the run, then the records of that
    /// turn alone are looked at (the whole journal for a run that is no turn), and only the messages of the kind that
    /// name the run are parsed. The journal gate orders the read after admitted writes and is not held while reading.
    /// </remarks>
    /// <exception cref="ArgumentException">The session identifier is blank, or neither a run nor a content identifier is given.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a message of the user or of the assistant.</exception>
    /// <exception cref="AgentSessionHistoryException">The session is missing, its journal is outside the sessions root or is not UTF-8.</exception>
    /// <exception cref="IOException">Journal access fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Journal access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<AgentSessionHistoryEntry?> FindContentRecordAsync(string sessionId, string? runId, AgentContentKind kind,
        string? contentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var recordKind = kind switch
        {
            AgentContentKind.User => AgentJournalRecordKind.UserContent,
            AgentContentKind.Assistant => AgentJournalRecordKind.AssistantContent,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only a message of the user or of the assistant is found this way."),
        };
        if (runId is null && contentId is null)
        {
            throw new ArgumentException("A run or a content identifier is required.", nameof(contentId));
        }

        var scan = await ScanTurnsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var start = 0L;
        var end = scan.Result.End.Offset;
        if (runId is not null)
        {
            // A run that is no turn (what a provider does between two turns, under a name of its own) has its
            // records anywhere in the journal: they are looked for in all of it, as those of no run are.
            foreach (var candidate in scan.Result.Turns)
            {
                if (string.Equals(candidate.RunId, runId, StringComparison.Ordinal))
                {
                    (start, end) = (candidate.StartOffset, candidate.EndOffset);
                    break;
                }
            }
        }

        await using var stream = await OpenReadStreamAsync(scan.Path, cancellationToken).ConfigureAwait(false);
        var offsets = await AgentJournalContentReader.FindAsync(stream, start, end, recordKind,
            runId is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(runId), cancellationToken).ConfigureAwait(false);
        // The last message of the run is looked for from the end.
        if (contentId is null)
        {
            offsets.Reverse();
        }

        foreach (var offset in offsets)
        {
            AgentEvent? record;
            try
            {
                record = await AgentJournalHistoryReader.ReadRecordAsync(stream, offset, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentSessionHistoryException)
            {
                // A record that cannot be read is not the one that is looked for.
                continue;
            }

            if (record is AgentContentCompletedEvent message && message.Kind == kind
                && string.Equals(message.RunId?.Value, runId, StringComparison.Ordinal)
                && (contentId is null || string.Equals(message.ContentId, contentId, StringComparison.Ordinal)))
            {
                return new(offset, message);
            }
        }

        return null;
    }
}
