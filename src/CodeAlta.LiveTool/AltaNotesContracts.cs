namespace CodeAlta.LiveTool;

/// <summary>
/// Exposes the single in-process sticky Markdown notes document used by the <c>alta notes</c> live-tool commands.
/// </summary>
public interface IAltaNotesService
{
    /// <summary>Occurs after the active notes Markdown changes.</summary>
    event EventHandler<AltaNotesChangedEventArgs>? Changed;

    /// <summary>Gets the current sticky notes Markdown.</summary>
    /// <returns>The current Markdown text, or an empty string when no notes are set.</returns>
    string GetMarkdown();

    /// <summary>Replaces the current sticky notes Markdown.</summary>
    /// <param name="markdown">The replacement Markdown text.</param>
    /// <param name="caller">The caller replacing the notes.</param>
    /// <param name="cancellationToken">A token that can cancel the operation before the notes are changed.</param>
    /// <returns>A completed task after the notes are replaced.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="markdown"/> or <paramref name="caller"/> is <see langword="null"/>.</exception>
    ValueTask SetMarkdownAsync(string markdown, AltaCallerIdentity caller, CancellationToken cancellationToken = default);

    /// <summary>Clears the current sticky notes Markdown.</summary>
    /// <param name="caller">The caller clearing the notes.</param>
    /// <param name="cancellationToken">A token that can cancel the operation before the notes are cleared.</param>
    /// <returns>A completed task after the notes are cleared.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="caller"/> is <see langword="null"/>.</exception>
    ValueTask ClearAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes a sticky notes Markdown change.
/// </summary>
public sealed class AltaNotesChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="AltaNotesChangedEventArgs"/> class.</summary>
    /// <param name="markdown">The current Markdown after the change.</param>
    /// <param name="caller">The caller that requested the change.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="markdown"/> or <paramref name="caller"/> is <see langword="null"/>.</exception>
    public AltaNotesChangedEventArgs(string markdown, AltaCallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(caller);

        Markdown = markdown;
        Caller = caller;
    }

    /// <summary>Gets the current Markdown after the change.</summary>
    public string Markdown { get; }

    /// <summary>Gets the caller that requested the change.</summary>
    public AltaCallerIdentity Caller { get; }
}

/// <summary>
/// In-memory implementation of <see cref="IAltaNotesService"/> for one active sticky notes document.
/// </summary>
public sealed class AltaNotesService : IAltaNotesService
{
    private readonly object _gate = new();
    private string _markdown = string.Empty;

    /// <inheritdoc />
    public event EventHandler<AltaNotesChangedEventArgs>? Changed;

    /// <inheritdoc />
    public string GetMarkdown()
    {
        lock (_gate)
        {
            return _markdown;
        }
    }

    /// <inheritdoc />
    public ValueTask SetMarkdownAsync(string markdown, AltaCallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _markdown = markdown;
        }

        Changed?.Invoke(this, new AltaNotesChangedEventArgs(markdown, caller));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _markdown = string.Empty;
        }

        Changed?.Invoke(this, new AltaNotesChangedEventArgs(string.Empty, caller));
        return ValueTask.CompletedTask;
    }
}
