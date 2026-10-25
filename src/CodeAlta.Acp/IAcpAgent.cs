namespace CodeAlta.Acp;

/// <summary>
/// An ACP agent served by an <see cref="AcpAgentServer"/>: one method per request of the client.
/// </summary>
/// <remarks>
/// <para>
/// Each request runs in a task of its own, so that a long <c>session/prompt</c> does not hold the others. The token of a
/// request is cancelled when the client sends <c>$/cancel_request</c> for it or when the connection ends.
/// </para>
/// <para>
/// A method answers the request with its result. To answer with an error, it throws an
/// <see cref="AcpJsonRpcException"/> (<see cref="AcpJsonRpcException.WithData"/> for an error with <c>data</c>), such as
/// <see cref="AcpErrorCodes.AuthRequired"/> when the client must authenticate first; any other exception is answered
/// with <see cref="AcpErrorCodes.InternalError"/>. <see cref="AcpAgentBase"/> answers
/// <see cref="AcpErrorCodes.MethodNotFound"/> for what an agent does not support.
/// </para>
/// </remarks>
public interface IAcpAgent
{
    /// <summary>Answers <c>initialize</c>: the protocol version and the capabilities of the agent.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token cancelled when the request is cancelled or the connection ends.</param>
    /// <returns>The response.</returns>
    Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken);

    /// <summary>Answers <c>authenticate</c>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token cancelled when the request is cancelled or the connection ends.</param>
    /// <returns>The response.</returns>
    Task<AuthenticateResponse> AuthenticateAsync(AuthenticateRequest request, CancellationToken cancellationToken);

    /// <summary>Answers <c>session/new</c>: creates a session.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token cancelled when the request is cancelled or the connection ends.</param>
    /// <returns>The response.</returns>
    Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Answers <c>session/load</c>: loads a session and replays its history as <c>session/update</c> notifications,
    /// awaited before the method returns.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token cancelled when the request is cancelled or the connection ends.</param>
    /// <returns>The response.</returns>
    Task<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken);

    /// <summary>Answers <c>session/resume</c>: takes a session back without replaying its history.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token cancelled when the request is cancelled or the connection ends.</param>
    /// <returns>The response.</returns>
    Task<ResumeSessionResponse> ResumeSessionAsync(ResumeSessionRequest request, CancellationToken cancellationToken);

    /// <summary>Answers <c>session/list</c>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token cancelled when the request is cancelled or the connection ends.</param>
    /// <returns>The response.</returns>
    Task<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken);

    /// <summary>Answers <c>session/fork</c>: creates a session that starts with the history of another.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token cancelled when the request is cancelled or the connection ends.</param>
    /// <returns>The response.</returns>
    Task<ForkSessionResponse> ForkSessionAsync(ForkSessionRequest request, CancellationToken cancellationToken);

    /// <summary>Answers <c>session/close</c>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token cancelled when the request is cancelled or the connection ends.</param>
    /// <returns>The response.</returns>
    Task<CloseSessionResponse> CloseSessionAsync(CloseSessionRequest request, CancellationToken cancellationToken);

    /// <summary>Answers <c>session/set_mode</c>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token cancelled when the request is cancelled or the connection ends.</param>
    /// <returns>The response.</returns>
    Task<SetSessionModeResponse> SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken);

    /// <summary>Answers <c>session/set_config_option</c> with the configuration options of the session as they are then.</summary>
    /// <param name="request">The request; its properties read its session, option and value.</param>
    /// <param name="cancellationToken">A token cancelled when the request is cancelled or the connection ends.</param>
    /// <returns>The response.</returns>
    Task<SetSessionConfigOptionResponse> SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Answers <c>session/prompt</c>: runs a turn. The <c>session/update</c> notifications of the turn are awaited
    /// before the method returns, so that the client has them all before the response.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token cancelled when the request is cancelled or the connection ends.</param>
    /// <returns>The response, with <see cref="StopReason.Cancelled"/> for a turn that <c>session/cancel</c> stopped.</returns>
    Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Handles the notification <c>session/cancel</c>: stops the turn of the session, whose <c>session/prompt</c> then
    /// answers with <see cref="StopReason.Cancelled"/>.
    /// </summary>
    /// <param name="notification">The notification.</param>
    /// <param name="cancellationToken">A token cancelled when the connection ends.</param>
    /// <returns>A task that ends when the notification is handled.</returns>
    Task CancelAsync(CancelNotification notification, CancellationToken cancellationToken);
}
