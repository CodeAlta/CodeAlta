namespace CodeAlta.Acp;

/// <summary>
/// The base of an ACP agent: what ACP requires is abstract, and the other requests are answered with
/// <see cref="AcpErrorCodes.MethodNotFound"/>, so that an agent implements only what it supports.
/// </summary>
public abstract class AcpAgentBase : IAcpAgent
{
    /// <inheritdoc />
    public abstract Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken);

    /// <inheritdoc />
    /// <exception cref="AcpJsonRpcException">Thrown with <see cref="AcpErrorCodes.MethodNotFound"/> unless overridden.</exception>
    public virtual Task<AuthenticateResponse> AuthenticateAsync(AuthenticateRequest request, CancellationToken cancellationToken)
        => NotSupported<AuthenticateResponse>("authenticate");

    /// <inheritdoc />
    public abstract Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken);

    /// <inheritdoc />
    /// <exception cref="AcpJsonRpcException">Thrown with <see cref="AcpErrorCodes.MethodNotFound"/> unless overridden.</exception>
    public virtual Task<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
        => NotSupported<LoadSessionResponse>("session/load");

    /// <inheritdoc />
    /// <exception cref="AcpJsonRpcException">Thrown with <see cref="AcpErrorCodes.MethodNotFound"/> unless overridden.</exception>
    public virtual Task<ResumeSessionResponse> ResumeSessionAsync(ResumeSessionRequest request, CancellationToken cancellationToken)
        => NotSupported<ResumeSessionResponse>("session/resume");

    /// <inheritdoc />
    /// <exception cref="AcpJsonRpcException">Thrown with <see cref="AcpErrorCodes.MethodNotFound"/> unless overridden.</exception>
    public virtual Task<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
        => NotSupported<ListSessionsResponse>("session/list");

    /// <inheritdoc />
    /// <exception cref="AcpJsonRpcException">Thrown with <see cref="AcpErrorCodes.MethodNotFound"/> unless overridden.</exception>
    public virtual Task<ForkSessionResponse> ForkSessionAsync(ForkSessionRequest request, CancellationToken cancellationToken)
        => NotSupported<ForkSessionResponse>("session/fork");

    /// <inheritdoc />
    /// <exception cref="AcpJsonRpcException">Thrown with <see cref="AcpErrorCodes.MethodNotFound"/> unless overridden.</exception>
    public virtual Task<CloseSessionResponse> CloseSessionAsync(CloseSessionRequest request, CancellationToken cancellationToken)
        => NotSupported<CloseSessionResponse>("session/close");

    /// <inheritdoc />
    /// <exception cref="AcpJsonRpcException">Thrown with <see cref="AcpErrorCodes.MethodNotFound"/> unless overridden.</exception>
    public virtual Task<SetSessionModeResponse> SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken)
        => NotSupported<SetSessionModeResponse>("session/set_mode");

    /// <inheritdoc />
    /// <exception cref="AcpJsonRpcException">Thrown with <see cref="AcpErrorCodes.MethodNotFound"/> unless overridden.</exception>
    public virtual Task<SetSessionConfigOptionResponse> SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
        => NotSupported<SetSessionConfigOptionResponse>("session/set_config_option");

    /// <inheritdoc />
    public abstract Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract Task CancelAsync(CancelNotification notification, CancellationToken cancellationToken);

    private static Task<T> NotSupported<T>(string method)
        => Task.FromException<T>(new AcpJsonRpcException(AcpErrorCodes.MethodNotFound, $"The agent does not support '{method}'."));
}
