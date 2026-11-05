using System.Net;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// A refusal of CodeAlta Server's REST API: the HTTP status and the stable code and hint of its problem details, so that
/// a client reacts (wait for an approval, ask for a new token) instead of retrying blindly.
/// </summary>
public sealed class FleetServerException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="FleetServerException"/> class.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="code">The stable code, such as <c>enrollment.token_used</c>; <c>http_error</c> when the server gave none.</param>
    /// <param name="hint">What to do, or <see langword="null"/>.</param>
    /// <param name="message">The description the server gave, or one of the client.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null or empty.</exception>
    public FleetServerException(HttpStatusCode status, string code, string? hint, string message) : base(message)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Status = status;
        Code = code;
        Hint = hint;
    }

    /// <summary>The code of a failure the server did not describe.</summary>
    public const string HttpErrorCode = "http_error";

    /// <summary>Gets the HTTP status.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Gets the stable code.</summary>
    public string Code { get; }

    /// <summary>Gets what to do, or <see langword="null"/>.</summary>
    public string? Hint { get; }
}
