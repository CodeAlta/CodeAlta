namespace CodeAlta.Agent.Runtime;

public sealed partial class FileSystemAgentSessionStore
{
    /// <summary>Lists every journal directly from disk for destructive preflight, without the projection cache or silent omissions.</summary>
    /// <remarks>Rejects linked entries, missing summaries and duplicate identities. This is not an atomic snapshot against external writers.</remarks>
    /// <param name="cancellationToken">Cancels enumeration and journal reads.</param>
    /// <returns>Persisted session metadata from each journal.</returns>
    /// <exception cref="IOException">A journal cannot be read or identified, a linked entry exists, or identities are duplicated.</exception>
    /// <exception cref="System.Text.Json.JsonException">A journal contains malformed JSON.</exception>
    /// <exception cref="UnauthorizedAccessException">A directory or journal cannot be accessed.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the read.</exception>
    public async IAsyncEnumerable<AgentSessionMetadata> ListSessionsForDeletionAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        FileAttributes rootAttributes;
        try { rootAttributes = File.GetAttributes(_layout.SessionsRootPath); }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException) { yield break; }
        if (!rootAttributes.HasFlag(FileAttributes.Directory)) throw new IOException("The session root is not a directory.");
        var directories = new Stack<string>();
        directories.Push(_layout.SessionsRootPath);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("A session directory is linked.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("A session entry is linked.");
                if (attributes.HasFlag(FileAttributes.Directory)) { directories.Push(path); continue; }
                if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                var projection = await ProjectSessionFileAsync(path, includeHistory: false, cancellationToken).ConfigureAwait(false);
                if (projection.Summary is not { SessionId.Length: > 0 } summary || !ids.Add(summary.SessionId))
                    throw new IOException("A session journal has missing or ambiguous identity.");
                if (string.IsNullOrWhiteSpace(summary.WorkingDirectory)) throw new IOException("A session journal has no workspace scope.");
                _sessionFiles[summary.SessionId] = path;
                yield return ToMetadata(summary, projection.State, projection.ViewState, projection.SpaceId);
            }
        }
    }
}
