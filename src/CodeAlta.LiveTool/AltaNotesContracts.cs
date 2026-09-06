namespace CodeAlta.LiveTool;

/// <summary>
/// Exposes session-scoped sticky Markdown notes used by the <c>alta notes</c> live-tool commands.
/// </summary>
public interface IAltaNotesService
{
    /// <summary>Occurs after a session's notes are acknowledged. Durable adapters deliver notifications in journal-write order; handlers must not block or reenter notes storage.</summary>
    event EventHandler<AltaNotesChangedEventArgs>? Changed;

    /// <summary>Captures explicit caller identity or the injected current-session fallback before awaiting input or storage.</summary>
    /// <param name="caller">The immutable caller identity.</param>
    /// <returns>A caller with a fixed source session identifier. Explicit identifiers never fall back.</returns>
    /// <exception cref="ArgumentNullException">The caller is null.</exception>
    /// <exception cref="AltaNotesSessionRequiredException">No session identifier is available.</exception>
    AltaCallerIdentity CaptureCaller(AltaCallerIdentity caller);

    /// <summary>Gets the current sticky notes Markdown for the caller's session.</summary>
    /// <param name="caller">The caller whose current session should be used.</param>
    /// <param name="cancellationToken">Cancels lookup or reading.</param>
    /// <returns>The current Markdown text, or an empty string when no notes are set.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="caller"/> is <see langword="null"/>.</exception>
    /// <exception cref="AltaNotesSessionRequiredException">Thrown when no current session can be resolved.</exception>
    /// <exception cref="OperationCanceledException">Lookup or reading was canceled.</exception>
    /// <exception cref="IOException">Durable notes could not be read.</exception>
    ValueTask<string> GetMarkdownAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default);

    /// <summary>Replaces the current session's sticky notes Markdown.</summary>
    /// <param name="markdown">The replacement Markdown text.</param>
    /// <param name="caller">The caller replacing the notes.</param>
    /// <param name="cancellationToken">A token that can cancel the operation before the notes are changed.</param>
    /// <returns>A completed task after the notes are replaced.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="markdown"/> or <paramref name="caller"/> is <see langword="null"/>.</exception>
    /// <exception cref="AltaNotesSessionRequiredException">Thrown when no current session can be resolved.</exception>
    /// <exception cref="OperationCanceledException">Canceled before write admission.</exception>
    /// <exception cref="IOException">Durable notes could not be written; partial I/O is not claimed to be rolled back.</exception>
    /// <exception cref="CodeAlta.Agent.Runtime.AgentNotesCommittedException">The journal committed but subsequent feedback failed.</exception>
    ValueTask SetMarkdownAsync(string markdown, AltaCallerIdentity caller, CancellationToken cancellationToken = default);

    /// <summary>Clears the current session's sticky notes Markdown.</summary>
    /// <param name="caller">The caller clearing the notes.</param>
    /// <param name="cancellationToken">A token that can cancel the operation before the notes are cleared.</param>
    /// <returns>A completed task after the notes are cleared.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="caller"/> is <see langword="null"/>.</exception>
    /// <exception cref="AltaNotesSessionRequiredException">Thrown when no current session can be resolved.</exception>
    /// <exception cref="OperationCanceledException">Canceled before write admission.</exception>
    /// <exception cref="IOException">Durable notes could not be written; partial I/O is not claimed to be rolled back.</exception>
    /// <exception cref="CodeAlta.Agent.Runtime.AgentNotesCommittedException">The journal committed but subsequent feedback failed.</exception>
    ValueTask ClearAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes a session-scoped sticky notes Markdown change.
/// </summary>
public sealed class AltaNotesChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="AltaNotesChangedEventArgs"/> class.</summary>
    /// <param name="sessionId">The session identifier whose notes changed.</param>
    /// <param name="markdown">The current Markdown after the change.</param>
    /// <param name="caller">The caller that requested the change.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="markdown"/> or <paramref name="caller"/> is <see langword="null"/>.</exception>
    public AltaNotesChangedEventArgs(string sessionId, string markdown, AltaCallerIdentity caller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(caller);

        SessionId = sessionId;
        Markdown = markdown;
        Caller = caller;
    }

    /// <summary>Gets the session identifier whose notes changed.</summary>
    public string SessionId { get; }

    /// <summary>Gets the current Markdown after the change.</summary>
    public string Markdown { get; }

    /// <summary>Gets the caller that requested the change.</summary>
    public AltaCallerIdentity Caller { get; }
}

/// <summary>
/// Thrown when an <c>alta notes</c> operation cannot resolve a current session.
/// </summary>
public sealed class AltaNotesSessionRequiredException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="AltaNotesSessionRequiredException"/> class.</summary>
    public AltaNotesSessionRequiredException()
        : base("A current session is required for alta notes.")
    {
    }
}

/// <summary>
/// In-memory implementation of <see cref="IAltaNotesService"/> for session-scoped sticky notes documents.
/// </summary>
public sealed class AltaNotesService : IAltaNotesService
{
    private readonly Func<string?> _currentSessionIdProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _markdownBySessionId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="AltaNotesService"/> class.</summary>
    /// <param name="currentSessionIdProvider">Optional provider for host/current-session callers.</param>
    public AltaNotesService(Func<string?>? currentSessionIdProvider = null)
    {
        _currentSessionIdProvider = currentSessionIdProvider ?? (() => null);
    }

    /// <inheritdoc />
    public event EventHandler<AltaNotesChangedEventArgs>? Changed;

    /// <inheritdoc />
    public AltaCallerIdentity CaptureCaller(AltaCallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller with { SourceSessionId = ResolveSessionId(caller) };
    }

    /// <inheritdoc />
    public ValueTask<string> GetMarkdownAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();
        var sessionId = ResolveSessionId(caller);
        lock (_gate)
        {
            return ValueTask.FromResult(_markdownBySessionId.TryGetValue(sessionId, out var markdown) ? markdown : string.Empty);
        }
    }

    /// <inheritdoc />
    public ValueTask SetMarkdownAsync(string markdown, AltaCallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = ResolveSessionId(caller);
        lock (_gate)
        {
            _markdownBySessionId[sessionId] = markdown;
        }

        Changed?.Invoke(this, new AltaNotesChangedEventArgs(sessionId, markdown, caller));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = ResolveSessionId(caller);
        lock (_gate)
        {
            _markdownBySessionId[sessionId] = string.Empty;
        }

        Changed?.Invoke(this, new AltaNotesChangedEventArgs(sessionId, string.Empty, caller));
        return ValueTask.CompletedTask;
    }

    private string ResolveSessionId(AltaCallerIdentity caller)
    {
        if (!string.IsNullOrWhiteSpace(caller.SourceSessionId))
        {
            return caller.SourceSessionId;
        }

        var currentSessionId = _currentSessionIdProvider();
        return string.IsNullOrWhiteSpace(currentSessionId)
            ? throw new AltaNotesSessionRequiredException()
            : currentSessionId;
    }
}
