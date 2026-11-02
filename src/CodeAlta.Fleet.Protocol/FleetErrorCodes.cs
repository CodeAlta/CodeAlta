using System.Text.Json;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// The stable codes of <see cref="FleetError"/>. Services add their own, prefixed by their group
/// (such as <c>session.stale_run</c>).
/// </summary>
public static class FleetErrorCodes
{
    /// <summary>A bad or expired credential; hint <see cref="FleetErrorHints.Reauthenticate"/>.</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>The device waits for its approval; hint <see cref="FleetErrorHints.WaitForApproval"/>.</summary>
    public const string PendingApproval = "pending_approval";

    /// <summary>Authenticated, but not allowed.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The device was revoked.</summary>
    public const string Revoked = "revoked";

    /// <summary>No protocol version in common; hint <see cref="FleetErrorHints.Update"/>.</summary>
    public const string UpdateRequired = "update_required";

    /// <summary>The server is overloaded; <see cref="FleetError.RetryAfterSeconds"/> says when to retry.</summary>
    public const string ServerBusy = "server_busy";

    /// <summary>No handler for the method of a request.</summary>
    public const string MethodNotFound = "method_not_found";

    /// <summary>A request that lacks its id or its method.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>The parameters of a request could not be read.</summary>
    public const string InvalidParams = "invalid_params";

    /// <summary>The handler failed for a reason it does not share.</summary>
    public const string InternalError = "internal_error";

    /// <summary>The operation did not end in time.</summary>
    public const string Timeout = "timeout";

    /// <summary>The operation was cancelled.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The receipt of an idempotency key was evicted: the command never runs again.</summary>
    public const string Expired = "expired";

    /// <summary>The connection closed normally; hint <see cref="FleetErrorHints.Reconnect"/>.</summary>
    public const string ConnectionClosed = "connection_closed";

    /// <summary>Nothing came from the peer within the dead-peer timeout.</summary>
    public const string PeerTimeout = "peer_timeout";

    /// <summary>A frame that breaks the protocol (not JSON, a binary frame, a handshake frame out of place).</summary>
    public const string ProtocolError = "protocol_error";

    /// <summary>A frame larger than the policy allows.</summary>
    public const string FrameTooLarge = "frame_too_large";

    /// <summary>The transport failed (a reset socket, an aborted peer).</summary>
    public const string TransportError = "transport_error";
}

/// <summary>The hints of <see cref="FleetError"/>: what the receiver of an error should do.</summary>
public static class FleetErrorHints
{
    /// <summary>Sign in again, then reconnect.</summary>
    public const string Reauthenticate = "reauthenticate";

    /// <summary>Wait for <c>device.approved</c>.</summary>
    public const string WaitForApproval = "wait_approval";

    /// <summary>Update the application.</summary>
    public const string Update = "update";

    /// <summary>Retry, after <see cref="FleetError.RetryAfterSeconds"/> when given.</summary>
    public const string Retry = "retry";

    /// <summary>Read the current state again before retrying.</summary>
    public const string Refresh = "refresh";

    /// <summary>Open a new connection and resume from the cursors.</summary>
    public const string Reconnect = "reconnect";
}

/// <summary>Maps exceptions to the errors of the protocol.</summary>
public static class FleetErrors
{
    /// <summary>
    /// Gets the error a handler's exception is answered with. A <see cref="FleetException"/> gives its own error;
    /// other exceptions give a code without their message, which may hold sensitive data.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The error to send to the peer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static FleetError FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            FleetException fleet => fleet.Error,
            TimeoutException => new FleetError(FleetErrorCodes.Timeout, FleetErrorHints.Retry),
            OperationCanceledException => new FleetError(FleetErrorCodes.Cancelled),
            JsonException => new FleetError(FleetErrorCodes.InvalidParams),
            _ => new FleetError(FleetErrorCodes.InternalError),
        };
    }
}
