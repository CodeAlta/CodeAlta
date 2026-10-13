using CodeAlta.Catalog.Bookmarks;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop;

/// <summary>
/// Ties the bookmarks to the life of the sessions of the host: a session that has a bookmark, of its own or of one
/// of its messages, is not removed by automatic retention, and the bookmarks of a session that is deleted on
/// purpose go with it.
/// </summary>
/// <remarks>
/// Retention deletes nothing until the bookmarks were read once: the owner is unknown before, and stays so when
/// they cannot be read.
/// </remarks>
internal sealed class DesktopBookmarks : IDisposable
{
    /// <summary>The name under which the bookmarked sessions are published to the retention of sessions.</summary>
    internal const string RetentionOwner = "bookmarks";

    private readonly BookmarkService _bookmarks;
    private readonly SessionRetentionProtection _protection;
    private readonly SessionRuntimeService? _runtime;
    private int _disposed;

    internal DesktopBookmarks(BookmarkService bookmarks, SessionRetentionProtection protection, SessionRuntimeService? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        ArgumentNullException.ThrowIfNull(protection);
        (_bookmarks, _protection, _runtime) = (bookmarks, protection, runtime);
        // Unknown until the bookmarks were read: an owner that is not ready protects every session.
        _protection.MarkUnknown(RetentionOwner, "bookmarks_unread");
        _bookmarks.Changed += Publish;
        if (_runtime is not null) _runtime.SessionDeleted += _bookmarks.ForgetSession;
        Loaded = LoadAsync();
    }

    /// <summary>Gets the first read of the bookmarks, after which the bookmarked sessions are published.</summary>
    internal Task Loaded { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _bookmarks.Changed -= Publish;
        if (_runtime is not null) _runtime.SessionDeleted -= _bookmarks.ForgetSession;
    }

    private async Task LoadAsync()
    {
        try
        {
            await _bookmarks.ListAsync().ConfigureAwait(false);
            Publish();
        }
        catch (Exception)
        {
            // Bookmarks that cannot be read protect every session, until a change of the bookmarks publishes them.
            _protection.MarkUnknown(RetentionOwner, "bookmarks_unreadable");
        }
    }

    // Runs where the bookmarks changed, before the request that changed them returns: a session whose bookmark was
    // just added is published while that request still claims it.
    private void Publish()
    {
        if (Volatile.Read(ref _disposed) != 0 || _bookmarks.BookmarkedSessionIds is not { } sessions) return;
        try
        {
            // Refused when a session of the list is already being retired: what was published before stays.
            _protection.Publish(RetentionOwner, sessions.Where(static id => id.Length > 0 && id == id.Trim()), ready: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // What was published before stays.
        }
    }
}
