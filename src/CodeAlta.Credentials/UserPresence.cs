namespace CodeAlta.Credentials;

/// <summary>Whether the system can verify the user here.</summary>
/// <param name="Available">Whether it can.</param>
/// <param name="Reason">Why not, in a sentence, when it cannot; null otherwise.</param>
public sealed record UserPresenceAvailability(bool Available, string? Reason)
{
    /// <summary>Gets the availability of a host that cannot ask anybody.</summary>
    public static UserPresenceAvailability None { get; } = new(false, "Nobody can be asked here.");
}

/// <summary>What a verification of the user ended with.</summary>
public enum UserPresenceResult
{
    /// <summary>The system verified the user.</summary>
    Verified,

    /// <summary>The user canceled, or failed.</summary>
    Canceled,

    /// <summary>The system cannot verify the user now.</summary>
    Unavailable,
}

/// <summary>
/// Asks the system to verify the user before a secret that asks before use is given: Windows Hello on Windows 11, Touch ID or
/// the password of the account on macOS. It is a check CodeAlta makes, not an encryption.
/// </summary>
public interface IUserPresenceVerifier
{
    /// <summary>Says whether the system can verify the user here.</summary>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The availability.</returns>
    ValueTask<UserPresenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>Asks the system to verify the user, with a message that names the secret and what will use it.</summary>
    /// <param name="message">The message of the prompt.</param>
    /// <param name="cancellationToken">A token to cancel the verification.</param>
    /// <returns>What it ended with.</returns>
    ValueTask<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken);
}
