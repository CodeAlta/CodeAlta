using System.Security.Authentication;

namespace CodeAlta.Fleet.Identity;

/// <summary>Why a device connection was refused.</summary>
public enum DeviceAuthenticationFailure
{
    /// <summary>The server presented another key than the pinned one.</summary>
    ServerKeyMismatch,

    /// <summary>The server's key is not a trusted device.</summary>
    ServerNotTrusted,

    /// <summary>The server's key was revoked.</summary>
    ServerRevoked,

    /// <summary>The client's proof of its device key is invalid.</summary>
    InvalidProof,

    /// <summary>The client's key is not a trusted device.</summary>
    ClientNotTrusted,

    /// <summary>The client's key was revoked.</summary>
    ClientRevoked,

    /// <summary>The other side sent what the exchange does not expect, or closed the connection during it.</summary>
    ProtocolError,
}

/// <summary>A device connection was refused: a key did not match its pin, was not trusted, or did not prove itself.</summary>
public sealed class DeviceAuthenticationException : AuthenticationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">Why the connection was refused.</param>
    /// <param name="message">The message.</param>
    public DeviceAuthenticationException(DeviceAuthenticationFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="failure">Why the connection was refused.</param>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused it.</param>
    public DeviceAuthenticationException(DeviceAuthenticationFailure failure, string message, Exception? innerException)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Gets why the connection was refused.</summary>
    public DeviceAuthenticationFailure Failure { get; }
}
