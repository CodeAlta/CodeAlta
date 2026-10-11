namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The working memory of one retention pass: the bytes it has freed, and whether Statistics stopped answering. Owned by
/// that pass and dropped with it.
/// </summary>
internal sealed class SessionRetentionPass
{
    private long _freedBytes;
    private int _statisticsUnavailable;

    /// <summary>Gets the bytes of the journals and artifacts this pass removed.</summary>
    internal long FreedBytes => Interlocked.Read(ref _freedBytes);

    /// <summary>Gets whether Statistics did not answer in time: the rest of the pass deletes without asking it again.</summary>
    internal bool StatisticsUnavailable => Volatile.Read(ref _statisticsUnavailable) != 0;

    internal void AddFreedBytes(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _freedBytes, bytes);
    }

    internal void MarkStatisticsUnavailable() => Volatile.Write(ref _statisticsUnavailable, 1);
}
