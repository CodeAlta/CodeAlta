namespace CodeAlta.Acp;

/// <summary>
/// The client of an ACP agent served by an <see cref="AcpAgentServer"/>: what the agent tells it and asks it.
/// </summary>
public interface IAcpClientConnection
{
    /// <summary>Gets the capabilities the client gave in <c>initialize</c>, or <see langword="null"/> before it.</summary>
    ClientCapabilities? ClientCapabilities { get; }

    /// <summary>Gets the name and version the client gave in <c>initialize</c>, or <see langword="null"/>.</summary>
    Implementation? ClientInfo { get; }

    /// <summary>
    /// Sends a <c>session/update</c> notification. Updates are written in the order they are awaited, and an update
    /// awaited before a request returns is written before its response.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="update">The update, made with <see cref="AcpSessionUpdates"/>.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that ends when the update is written.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId"/> is empty or <paramref name="update"/> is undefined.</exception>
    /// <exception cref="AcpConnectionClosedException">Thrown when the connection has ended.</exception>
    Task SessionUpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a request to the client, such as <c>session/request_permission</c>, <c>fs/read_text_file</c> or
    /// <c>terminal/create</c>, and waits for its result.
    /// </summary>
    /// <typeparam name="TParams">A type of the parameters known to the ACP serializer.</typeparam>
    /// <typeparam name="TResult">A type of the result known to the ACP serializer.</typeparam>
    /// <param name="method">The method.</param>
    /// <param name="parameters">The parameters.</param>
    /// <param name="cancellationToken">A token to cancel the request; the client is then sent <c>$/cancel_request</c>.</param>
    /// <returns>The result.</returns>
    /// <exception cref="AcpJsonRpcException">Thrown when the client answers with an error.</exception>
    /// <exception cref="AcpConnectionClosedException">Thrown when the connection has ended.</exception>
    Task<TResult> SendRequestAsync<TParams, TResult>(string method, TParams parameters, CancellationToken cancellationToken = default);

    /// <summary>Sends a notification to the client.</summary>
    /// <typeparam name="TParams">A type of the parameters known to the ACP serializer.</typeparam>
    /// <param name="method">The method.</param>
    /// <param name="parameters">The parameters.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that ends when the notification is written.</returns>
    /// <exception cref="AcpConnectionClosedException">Thrown when the connection has ended.</exception>
    Task SendNotificationAsync<TParams>(string method, TParams parameters, CancellationToken cancellationToken = default);
}
