using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The journals of the file-system session store, listed from the folder layout: <c>sessions/yyyy/MM/dd/&lt;session id&gt;.jsonl</c>.
/// </summary>
/// <remarks>Thread-safe. It remembers where it found each file, which is the only state it keeps.</remarks>
public sealed class FileSystemSessionJournalCatalog : ISessionJournalCatalog
{
    private const int ReadBufferBytes = 1;

    private readonly AgentRuntimePathLayout _layout;
    private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a catalog over the sessions folder of a layout.</summary>
    /// <param name="layout">The layout of the store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is null.</exception>
    public FileSystemSessionJournalCatalog(AgentRuntimePathLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SessionJournalFile> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var files = await Task.Run(() => ReadFolder(cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    /// <inheritdoc />
    public ValueTask<SessionJournalFile?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        var path = FindPath(sessionId);
        return ValueTask.FromResult(path is null ? null : Describe(sessionId, path));
    }

    /// <inheritdoc />
    public ValueTask<Stream?> OpenAsync(string sessionId, long offset = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        cancellationToken.ThrowIfCancellationRequested();

        var path = FindPath(sessionId);
        if (path is null)
        {
            return ValueTask.FromResult<Stream?>(null);
        }

        try
        {
            // The session keeps writing (it holds the file with FileShare.Read) and may replace the file: a reader asks for
            // sharing that allows both, and takes no lock of its own. The reader has its own buffer, so the stream has none.
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ReadBufferBytes, FileOptions.SequentialScan);
            try
            {
                stream.Position = offset;
                return ValueTask.FromResult<Stream?>(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _paths.TryRemove(sessionId, out _);
            return ValueTask.FromResult<Stream?>(null);
        }
    }

    private List<SessionJournalFile> ReadFolder(CancellationToken cancellationToken)
    {
        var root = _layout.SessionsRootPath;
        var files = new List<SessionJournalFile>();
        if (!Directory.Exists(root))
        {
            return files;
        }

        var traces = Path.TrimEndingDirectorySeparator(_layout.SessionTracesRootPath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.StartsWith(traces, comparison))
            {
                continue;
            }

            var sessionId = Path.GetFileNameWithoutExtension(path);
            if (Describe(sessionId, path) is { } file)
            {
                _paths[sessionId] = path;
                files.Add(file);
            }
        }

        files.Sort(static (left, right) =>
        {
            var order = right.LastWriteUtc.CompareTo(left.LastWriteUtc);
            return order != 0 ? order : string.CompareOrdinal(right.SessionId, left.SessionId);
        });
        return files;
    }

    private string? FindPath(string sessionId)
    {
        if (_paths.TryGetValue(sessionId, out var known) && File.Exists(known))
        {
            return known;
        }

        var root = _layout.SessionsRootPath;
        if (!Directory.Exists(root))
        {
            return null;
        }

        var traces = Path.TrimEndingDirectorySeparator(_layout.SessionTracesRootPath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        var name = sessionId.Trim();
        foreach (var path in Directory.EnumerateFiles(root, name + ".jsonl", options))
        {
            if (!path.StartsWith(traces, comparison))
            {
                _paths[sessionId] = path;
                return path;
            }
        }

        _paths.TryRemove(sessionId, out _);
        return null;
    }

    private static SessionJournalFile? Describe(string sessionId, string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new SessionJournalFile(sessionId, path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
