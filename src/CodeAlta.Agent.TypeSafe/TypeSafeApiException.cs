using System.Net;

namespace CodeAlta.Agent.TypeSafe;

/// <summary>
/// A request the TypeSafe API refused or could not answer.
/// </summary>
public sealed class TypeSafeApiException : Exception
{
    /// <summary>
    /// Creates the exception of one failed request.
    /// </summary>
    /// <param name="statusCode">The HTTP status of the answer, or <see langword="null"/> when no answer came.</param>
    /// <param name="message">What failed, with the detail the API gave.</param>
    /// <param name="innerException">The exception of the transport, if any.</param>
    public TypeSafeApiException(HttpStatusCode? statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status of the answer, or <see langword="null"/> when no answer came (network failure, timeout).</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Whether the key was missing, invalid or refused (401 or 403).</summary>
    public bool IsAuthenticationFailure => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>Whether the request itself was refused (400, 413, 415 or 422): sending it again fails again.</summary>
    public bool IsInvalidRequest => StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge
        or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity;

    /// <summary>Whether the service was busy (429 rate limit or 529 overloaded) after the retries.</summary>
    public bool IsBusy => StatusCode is HttpStatusCode.TooManyRequests || (int?)StatusCode == 529;
}
