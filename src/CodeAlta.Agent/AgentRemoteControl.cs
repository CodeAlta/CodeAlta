namespace CodeAlta.Agent;

/// <summary>Where the remote control of a session stands.</summary>
public enum AgentRemoteControlStatus
{
    /// <summary>The session is not controlled remotely.</summary>
    Off,

    /// <summary>The provider is connecting the session, or connecting it again.</summary>
    Connecting,

    /// <summary>The session can be followed and driven from its link.</summary>
    Connected,

    /// <summary>The provider could not connect the session; <see cref="AgentRemoteControl.Error"/> says why.</summary>
    Failed,
}

/// <summary>
/// The remote control of a session: whether the session can be followed and driven from elsewhere (for Claude
/// Code, from claude.ai and the Claude app), and the link that opens it there.
/// </summary>
/// <param name="Status">Where the remote control stands.</param>
/// <param name="SessionUrl">The link that opens the session remotely; <see langword="null"/> before the provider gives it.</param>
/// <param name="Error">Why the provider could not connect the session; <see langword="null"/> unless <paramref name="Status"/> is <see cref="AgentRemoteControlStatus.Failed"/>.</param>
public sealed record AgentRemoteControl(AgentRemoteControlStatus Status, string? SessionUrl = null, string? Error = null)
{
    /// <summary>Gets the state of a session that is not controlled remotely.</summary>
    public static AgentRemoteControl Off { get; } = new(AgentRemoteControlStatus.Off);
}

/// <summary>
/// The remote control of a session changed. Like the background tasks, it is the state of a provider that runs
/// now: it is given to those who listen to the session and is never recorded.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="RemoteControl">The remote control of the session now.</param>
public sealed record AgentRemoteControlEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRemoteControl RemoteControl)
    : AgentEvent(ProviderId, SessionId, Timestamp, null);

/// <summary>
/// Optional capability of a session whose provider can let it be followed and driven from elsewhere. A prompt
/// sent from there starts a turn the provider runs by itself, shown as a run of the session.
/// </summary>
public interface IAgentRemoteControlProvider
{
    /// <summary>Gets whether the provider of the session has remote control at all.</summary>
    bool SupportsRemoteControl { get; }

    /// <summary>Gets the remote control of the session now, as the last <see cref="AgentRemoteControlEvent"/> told it.</summary>
    AgentRemoteControl RemoteControl { get; }

    /// <summary>
    /// Turns the remote control of the session on or off. Turning it on starts what the provider needs (its
    /// process) when nothing runs, without a turn; the provider then keeps it running while the remote control is
    /// on, and connects it again with the same link when it has to start it again.
    /// </summary>
    /// <param name="enabled">Whether the session is to be controlled remotely.</param>
    /// <param name="name">The name the session is shown under remotely; <see langword="null"/> for the provider's.</param>
    /// <param name="cancellationToken">Cancels the request, not a connection that was already made.</param>
    /// <returns>The remote control of the session after the request.</returns>
    /// <exception cref="NotSupportedException">The provider has no remote control.</exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    Task<AgentRemoteControl> SetRemoteControlAsync(bool enabled, string? name, CancellationToken cancellationToken = default);
}
