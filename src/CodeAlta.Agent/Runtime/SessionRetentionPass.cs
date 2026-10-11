namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The working memory of one retention pass: the family edges it has read from each journal, and the bytes it has
/// freed. Owned by that pass and dropped with it. A journal is read again when its size or times change: journals
/// are appended to, so an unchanged journal holds no edge the pass has not seen.
/// </summary>
internal sealed class SessionRetentionPass
{
    private readonly Dictionary<string, (RetentionJournalStamp Stamp, RetentionJournalFamily Family)> _families = new(StringComparer.OrdinalIgnoreCase);
    private long _freedBytes;

    /// <summary>Gets the bytes of the journals and artifacts this pass removed.</summary>
    internal long FreedBytes => Interlocked.Read(ref _freedBytes);

    internal void AddFreedBytes(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _freedBytes, bytes);
    }

    internal RetentionJournalFamily? TryGetFamily(string path, RetentionJournalStamp stamp)
    {
        lock (_families)
            return _families.TryGetValue(path, out var entry) && entry.Stamp == stamp ? entry.Family : null;
    }

    internal void SetFamily(string path, RetentionJournalStamp stamp, RetentionJournalFamily family)
    {
        lock (_families) _families[path] = (stamp, family);
    }
}

/// <summary>What identifies one generation of a journal file between two reads of the same pass.</summary>
internal readonly record struct RetentionJournalStamp(long Length, DateTime LastWriteUtc, DateTime CreationUtc);

/// <summary>The session a journal belongs to and every session its lineage edges protect, itself included when it has one.</summary>
internal sealed record RetentionJournalFamily(string SessionId, IReadOnlyList<string> Protected);
