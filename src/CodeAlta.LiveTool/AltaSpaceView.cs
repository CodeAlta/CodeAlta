namespace CodeAlta.LiveTool;

/// <summary>
/// The window of a host that shows one space at a time. A host without such a window registers no such
/// service: <c>alta space switch</c> is then not part of its commands, and every command reads the whole
/// catalog as its current space.
/// </summary>
public interface IAltaSpaceView
{
    /// <summary>
    /// Gets the identifier of the space the window shows; <see langword="null"/> while no window said what
    /// it shows.
    /// </summary>
    string? ShownSpaceId { get; }

    /// <summary>Asks the window to show a space. It changes what the window shows and no setting of the user.</summary>
    /// <param name="spaceId">The identifier of the space.</param>
    /// <returns>True when a window received the request; false when none is there to show it.</returns>
    /// <exception cref="ArgumentException"><paramref name="spaceId"/> is null or blank.</exception>
    bool Show(string spaceId);

    /// <summary>
    /// Tells the window that the spaces, or the projects of one, were changed by a command: it reads them again.
    /// </summary>
    void NotifyChanged();
}
