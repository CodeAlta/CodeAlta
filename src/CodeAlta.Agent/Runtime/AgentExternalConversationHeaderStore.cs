namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Keeps what a provider read of the files of conversations another program started (Claude Code: the transcripts of
/// the sessions started in VS Code or a terminal), so that a listing after a restart of CodeAlta reads again only the
/// files that changed since: one row per file, with the length and the last write time it had when it was read.
/// </summary>
/// <remarks>
/// <para>
/// The store does not understand what it keeps: the provider serializes and parses the header, and owns its format
/// version. A provider that changes what it reads of a file, or the rules that read it, raises its version, and the
/// rows of an older one are read again from their files.
/// </para>
/// <para>
/// The rows are derived from the files: a store that cannot be used now (locked, damaged) loses nothing that a later
/// listing cannot read again, and says so instead of throwing. Each provider writes and removes only its own rows.
/// </para>
/// </remarks>
public interface IAgentExternalConversationHeaderStore
{
    /// <summary>Reads every row of a provider.</summary>
    /// <param name="provider">The name the provider keeps its rows under, such as <c>claude-code</c>.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The rows, in no order; null when the store cannot be read now, which the provider takes as none.</returns>
    /// <exception cref="ArgumentException"><paramref name="provider"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    Task<IReadOnlyList<AgentExternalConversationHeaderRecord>?> ReadAsync(string provider, CancellationToken cancellationToken = default);

    /// <summary>Writes the rows a listing read and removes those of the files it no longer found, in one transaction.</summary>
    /// <param name="provider">The name the provider keeps its rows under.</param>
    /// <param name="upserts">The rows to add or replace, by path.</param>
    /// <param name="removedPaths">The paths whose rows are removed.</param>
    /// <param name="cancellationToken">Stops the write before it starts.</param>
    /// <returns>Whether it was written; false when the store cannot be written now, which a later listing tries again.</returns>
    /// <exception cref="ArgumentException"><paramref name="provider"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="upserts"/> or <paramref name="removedPaths"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The write was stopped.</exception>
    Task<bool> WriteAsync(
        string provider,
        IReadOnlyCollection<AgentExternalConversationHeaderRecord> upserts,
        IReadOnlyCollection<string> removedPaths,
        CancellationToken cancellationToken = default);
}

/// <summary>What a provider read of the file of a conversation another program started.</summary>
/// <param name="Path">The full path of the file, as the provider found it.</param>
/// <param name="Length">The length of the file when it was read, in bytes.</param>
/// <param name="LastWriteUtcTicks">The last write time of the file when it was read, in UTC ticks.</param>
/// <param name="FormatVersion">The version of the format of <paramref name="HeaderJson"/>, owned by the provider.</param>
/// <param name="Importable">Whether the provider lists the conversation for an import.</param>
/// <param name="HeaderJson">What the provider read, as it serialized it; null when the file holds no conversation.</param>
public sealed record AgentExternalConversationHeaderRecord(
    string Path,
    long Length,
    long LastWriteUtcTicks,
    int FormatVersion,
    bool Importable,
    string? HeaderJson);
