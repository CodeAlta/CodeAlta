namespace CodeAlta.Catalog;

/// <summary>
/// The assignments of the global configuration, read again only when the file changes: what is asked for often (the
/// buttons and the regions of a pane, every run) does not parse the configuration each time.
/// </summary>
/// <remarks>
/// Thread-safe. A file that cannot be read or parsed gives the assignments it gave last, or none: the enablement read
/// from the same file reports its problem.
/// </remarks>
public sealed class CachedSpaceAssignments
{
    private readonly object _gate = new();
    private readonly CodeAltaConfigStore _store;
    private (DateTime WrittenAt, long Length) _stamp;
    private SpaceAssignments _current = SpaceAssignments.Empty;

    /// <summary>Initializes a new instance of the <see cref="CachedSpaceAssignments"/> class.</summary>
    /// <param name="store">The store of the global configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public CachedSpaceAssignments(CodeAltaConfigStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Gets the assignments as the file says them now.</summary>
    /// <returns>The assignments; empty when the file does not exist.</returns>
    public SpaceAssignments Current()
    {
        var file = new FileInfo(_store.ConfigPath);
        var stamp = file.Exists ? (file.LastWriteTimeUtc, file.Length) : (DateTime.MinValue, -1L);
        lock (_gate)
        {
            if (stamp == _stamp) return _current;
            try
            {
                _current = _store.LoadGlobalSpaceAssignments();
                _stamp = stamp;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
            }

            return _current;
        }
    }
}
