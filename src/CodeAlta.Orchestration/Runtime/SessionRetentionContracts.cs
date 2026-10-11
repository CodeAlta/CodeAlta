namespace CodeAlta.Orchestration.Runtime;

/// <summary>One startup pass's replayable immutable outcome.</summary>
/// <param name="State">not_started, running, completed, partial, skipped or failed.</param>
/// <param name="Examined">Candidates considered.</param>
/// <param name="Deleted">Confirmed removed journals.</param>
/// <param name="Skipped">Candidates retained.</param>
/// <param name="Reason">Bounded status code; never journal content or private paths.</param>
public sealed record SessionRetentionStatus(string State, int Examined, int Deleted, int Skipped, string? Reason)
{
    /// <summary>Gets the number of candidates the pass found, known once it has listed them; zero before.</summary>
    public int Total { get; init; }

    /// <summary>Gets the bytes of the journals and artifacts the pass removed.</summary>
    public long FreedBytes { get; init; }

    /// <summary>Gets the number of files of the sessions folder older than the cutoff that the pass removed besides the journals of the sessions it retired: orphan files and what removed sessions left.</summary>
    public int FilesDeleted { get; init; }
}
