using System.Collections.Concurrent;

namespace CodeAlta.Catalog.SessionMoves;

/// <summary>
/// The repository locations of folders (<see cref="RepositoryKey.ReadLocationAsync"/>), read once and kept for a while:
/// a list that names the repository of each project asks git only for the folders it did not see recently.
/// </summary>
/// <remarks>
/// A folder read by several callers at once is read once. A read is not canceled by its caller (it is bounded by the
/// timeout of git), so a canceled wait never leaves a folder without its location. A read that fails gives no location,
/// kept as long as a location is.
/// </remarks>
public sealed class RepositoryLocationCache
{
    private const int MaximumFolders = 1024;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;
    private readonly Func<string, CancellationToken, Task<RepositoryLocation?>> _read;

    /// <summary>Creates a cache that reads with git and keeps a location five minutes.</summary>
    public RepositoryLocationCache() : this(TimeSpan.FromMinutes(5), TimeProvider.System, RepositoryKey.ReadLocationAsync) { }

    /// <summary>Creates a cache.</summary>
    /// <param name="lifetime">How long a location is kept.</param>
    /// <param name="time">The clock.</param>
    /// <param name="read">Reads the location of a folder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> or <paramref name="read"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is not positive.</exception>
    public RepositoryLocationCache(TimeSpan lifetime, TimeProvider time, Func<string, CancellationToken, Task<RepositoryLocation?>> read)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(read);
        _lifetime = lifetime;
        _time = time;
        _read = read;
    }

    /// <summary>Gives the location of a folder, read now when it was not read recently.</summary>
    /// <param name="folder">The folder of a project.</param>
    /// <param name="cancellationToken">Cancels the wait, not the read.</param>
    /// <returns>The location; null when the folder is in no repository with a remote.</returns>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public Task<RepositoryLocation?> GetAsync(string folder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var now = _time.GetUtcNow();
        if (_entries.TryGetValue(folder, out var known) && now - known.ReadAt < _lifetime) return known.Read.WaitAsync(cancellationToken);
        if (_entries.Count >= MaximumFolders) _entries.Clear();
        var entry = new Entry(ReadAsync(folder), now);
        var kept = known is null ? _entries.GetOrAdd(folder, entry) : _entries.TryUpdate(folder, entry, known) ? entry : _entries.GetOrAdd(folder, entry);
        return kept.Read.WaitAsync(cancellationToken);
    }

    private async Task<RepositoryLocation?> ReadAsync(string folder)
    {
        await Task.Yield();
        try
        {
            return await _read(folder, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
        {
            // No git, a folder that went away, a git that did not answer: no location, read again later.
            return null;
        }
    }

    private sealed record Entry(Task<RepositoryLocation?> Read, DateTimeOffset ReadAt);
}
