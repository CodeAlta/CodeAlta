namespace CodeAlta.Agent.Claude;

/// <summary>
/// Where the last read of each transcript stopped, kept by the turn executor for its sessions: a read of the turns
/// made outside CodeAlta then reads only what was written since, also for a session that is attached and records no
/// state of its own after the read, and the next answer of a session records that position with its state.
/// </summary>
/// <remarks>
/// A position is only a shortcut: the reader checks that the file still starts with the same line and did not shrink,
/// and reads the whole file when it cannot be sure, so a position of an older read is never wrong, only slower.
/// </remarks>
internal sealed class ClaudeCodeTranscriptPositions
{
    // Conversations of the CLI followed by one executor are few: past this many, the oldest are forgotten.
    private const int MaxConversations = 256;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ClaudeCodeTranscriptPosition> _latest = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>Records where a read of the transcript of a conversation stopped.</summary>
    /// <param name="cliSessionId">The identifier of the conversation of the CLI.</param>
    /// <param name="position">Where the read stopped, or null: nothing is recorded then.</param>
    public void Note(string cliSessionId, ClaudeCodeTranscriptPosition? position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliSessionId);
        if (position is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_latest.ContainsKey(cliSessionId))
            {
                _order.Enqueue(cliSessionId);
                while (_order.Count > MaxConversations && _order.TryDequeue(out var oldest))
                {
                    _latest.Remove(oldest);
                }
            }

            _latest[cliSessionId] = position;
        }
    }

    /// <summary>Returns the position a read of a conversation starts from: the latest of the one recorded and the one known here.</summary>
    /// <param name="cliSessionId">The identifier of the conversation of the CLI.</param>
    /// <param name="recorded">The position the state of the session holds, or null.</param>
    /// <returns>The position to read from, or null to read the whole file.</returns>
    public ClaudeCodeTranscriptPosition? Choose(string cliSessionId, ClaudeCodeTranscriptPosition? recorded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliSessionId);
        ClaudeCodeTranscriptPosition? known;
        lock (_gate)
        {
            _latest.TryGetValue(cliSessionId, out known);
        }

        // The one known here is from the latest read of the file by this process, even of a file written again.
        return known is not null && (recorded is null || known.Offset >= recorded.Offset ||
                                     !string.Equals(known.FirstLineHash, recorded.FirstLineHash, StringComparison.Ordinal))
            ? known
            : recorded;
    }
}
