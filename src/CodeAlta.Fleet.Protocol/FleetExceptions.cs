namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// An error of the fleet protocol: the error a peer answered, or the error a handler throws to answer with.
/// </summary>
public class FleetException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="FleetException"/> class.</summary>
    /// <param name="error">The error.</param>
    /// <param name="innerException">The exception that caused it, if any.</param>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public FleetException(FleetError error, Exception? innerException = null)
        : base(Describe(error), innerException)
    {
        Error = error;
    }

    /// <summary>Initializes a new instance of the <see cref="FleetException"/> class.</summary>
    /// <param name="code">The code of the error (<see cref="FleetErrorCodes"/>).</param>
    /// <param name="hint">What the receiver should do (<see cref="FleetErrorHints"/>), or <see langword="null"/>.</param>
    /// <param name="message">A description without sensitive data, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null or empty.</exception>
    public FleetException(string code, string? hint = null, string? message = null)
        : this(new FleetError(RequireCode(code), hint, message))
    {
    }

    /// <summary>Gets the error.</summary>
    public FleetError Error { get; }

    private static string RequireCode(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        return code;
    }

    private static string Describe(FleetError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var text = error.Message ?? error.Hint;
        return text is null ? error.Code : $"{error.Code}: {text}";
    }
}

/// <summary>The refusal of a handshake, by the peer or by this side.</summary>
public sealed class FleetHandshakeException : FleetException
{
    /// <summary>Initializes a new instance of the <see cref="FleetHandshakeException"/> class.</summary>
    /// <param name="error">Why the handshake failed.</param>
    /// <param name="innerException">The exception that caused it, if any.</param>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public FleetHandshakeException(FleetError error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}

/// <summary>
/// The failure of an operation because its connection closed: a pending request, or a request or event sent after
/// the close. <see cref="FleetException.Error"/> carries the close code.
/// </summary>
public sealed class FleetConnectionClosedException : FleetException
{
    /// <summary>Initializes a new instance of the <see cref="FleetConnectionClosedException"/> class.</summary>
    /// <param name="closeInfo">How the connection closed.</param>
    /// <param name="innerException">The exception that closed it, if any.</param>
    /// <exception cref="ArgumentNullException"><paramref name="closeInfo"/> is <see langword="null"/>.</exception>
    public FleetConnectionClosedException(FleetConnectionCloseInfo closeInfo, Exception? innerException = null)
        : base(ToError(closeInfo), innerException ?? closeInfo.Exception)
    {
        CloseInfo = closeInfo;
    }

    /// <summary>Gets how the connection closed.</summary>
    public FleetConnectionCloseInfo CloseInfo { get; }

    private static FleetError ToError(FleetConnectionCloseInfo closeInfo)
    {
        ArgumentNullException.ThrowIfNull(closeInfo);
        return new FleetError(closeInfo.Code, FleetErrorHints.Reconnect, closeInfo.Message);
    }
}

/// <summary>How a <see cref="FleetConnection"/> closed.</summary>
/// <param name="Code">
/// <see cref="FleetErrorCodes.ConnectionClosed"/> for a clean close, otherwise the failure
/// (<see cref="FleetErrorCodes.PeerTimeout"/>, <see cref="FleetErrorCodes.ProtocolError"/>,
/// <see cref="FleetErrorCodes.FrameTooLarge"/>, <see cref="FleetErrorCodes.TransportError"/>).
/// </param>
/// <param name="Message">A description, or <see langword="null"/>.</param>
/// <param name="ClosedLocally">Whether this side closed it.</param>
/// <param name="Exception">The exception that closed it, if any.</param>
public sealed record FleetConnectionCloseInfo(string Code, string? Message, bool ClosedLocally, Exception? Exception = null)
{
    /// <summary>Gets whether the connection closed cleanly, by either side.</summary>
    public bool IsClean => Code == FleetErrorCodes.ConnectionClosed;
}
