using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

public sealed partial class FileSystemAgentSessionStore
{
    /// <summary>Gets the folder of the attachments of a session whose journal is not written yet.</summary>
    /// <param name="sessionId">The identifier of the new session.</param>
    /// <param name="createdAt">Its creation time, which decides where its journal is.</param>
    /// <returns>The folder; it may not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    internal string GetNewSessionAttachmentDirectory(string sessionId, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Path.ChangeExtension(_layout.GetSessionFilePath(sessionId, createdAt), ".attachments");
    }

    /// <summary>
    /// Writes the journal of a new session that starts with records made elsewhere: a conversation imported from another
    /// program, its turns written as a session writes its own.
    /// </summary>
    /// <param name="summary">The summary of the new session: its identifier and its creation time decide where its journal is.</param>
    /// <param name="state">The state of the new session after the records.</param>
    /// <param name="header">The first line of the new journal; null for none.</param>
    /// <param name="events">The records of the turns, in order, written after the header.</param>
    /// <param name="additionalSnapshots">Records written after the summary and the state.</param>
    /// <param name="cancellationToken">Cancels the write; nothing of the new session remains then.</param>
    /// <remarks>
    /// The journal is written beside its final place and renamed, which is the commit point: a session that is listed has
    /// its whole journal. The attachments its records name are saved by the caller before, in
    /// <see cref="GetNewSessionAttachmentDirectory"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">The summary and the state name different sessions.</exception>
    /// <exception cref="InvalidOperationException">The new session exists.</exception>
    /// <exception cref="IOException">Writing fails.</exception>
    /// <exception cref="OperationCanceledException">Canceled before the commit point.</exception>
    internal async Task CommitImportAsync(AgentSessionSummary summary, AgentSessionState state, string? header, IReadOnlyList<AgentEvent> events,
        IReadOnlyList<string> additionalSnapshots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(additionalSnapshots);
        if (!string.Equals(summary.SessionId, state.SessionId, StringComparison.Ordinal))
            throw new ArgumentException("The summary and the state name different sessions.", nameof(summary));
        if (await TryGetSessionFilePathAsync(summary.SessionId, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException($"The session '{summary.SessionId}' exists already.");
        var targetPath = _layout.GetSessionFilePath(summary.SessionId, summary.CreatedAt);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"Path '{targetPath}' did not resolve to a parent directory.");
        Directory.CreateDirectory(targetDirectory);
        var temporary = targetPath + ".import-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true))
            {
                var lines = new List<string>(events.Count + additionalSnapshots.Count + 3);
                if (header is not null) lines.Add(header);
                lines.AddRange(events.Select(static @event => @event.ToJson()));
                lines.Add(new AgentRawEvent(summary.ProviderId, summary.SessionId, summary.UpdatedAt, SessionSummaryEventType,
                    JsonSerializer.SerializeToElement(summary, AgentJsonSerializerContext.Default.AgentSessionSummary)).ToJson());
                lines.Add(new AgentRawEvent(summary.ProviderId, summary.SessionId, state.UpdatedAt, SessionStateEventType,
                    JsonSerializer.SerializeToElement(state, AgentJsonSerializerContext.Default.AgentSessionState)).ToJson());
                lines.AddRange(additionalSnapshots);
                foreach (var line in lines)
                {
                    await output.WriteAsync(Utf8WithoutBom.GetBytes(line + "\n"), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _journalFile.WithPathLockAsync(targetPath, () =>
            {
                // Nothing fallible follows the rename: the new session exists from here on.
                File.Move(temporary, targetPath, overwrite: false);
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }

        _sessionFiles[summary.SessionId] = targetPath;
        await UpsertCacheFromFileAsync(targetPath, CancellationToken.None).ConfigureAwait(false);
    }
}
