namespace CodeAlta.Fleet.Identity;

/// <summary>What a paired device may do on this instance.</summary>
[Flags]
public enum DevicePermissions
{
    /// <summary>Nothing beyond being known.</summary>
    None = 0,

    /// <summary>See the sessions of this instance.</summary>
    View = 1,

    /// <summary>Send prompts to sessions, answer their questions and permission requests.</summary>
    Drive = 2,

    /// <summary>Start sessions on this instance.</summary>
    Start = 4,

    /// <summary>Move sessions to this instance.</summary>
    Move = 8,
}

/// <summary>A device paired with this instance.</summary>
/// <param name="DeviceId">The device id, derived from its key (<see cref="DeviceIdentity.DeviceIdOf"/>).</param>
/// <param name="Name">The name the device gave itself at pairing.</param>
/// <param name="Fingerprint">The fingerprint of its device key.</param>
/// <param name="Permissions">What it may do here.</param>
/// <param name="PairedAt">When it was paired.</param>
/// <param name="RevokedAt">When it was revoked, or <see langword="null"/> while it is trusted.</param>
public sealed record TrustedDevice(
    string DeviceId,
    string Name,
    DeviceFingerprint Fingerprint,
    DevicePermissions Permissions,
    DateTimeOffset PairedAt,
    DateTimeOffset? RevokedAt)
{
    /// <summary>Gets whether the device was revoked: its key is refused.</summary>
    public bool IsRevoked => RevokedAt is not null;
}

/// <summary>Says which device keys a connection accepts.</summary>
public interface IDeviceTrustPolicy
{
    /// <summary>Finds the trusted device of a key.</summary>
    /// <param name="fingerprint">The fingerprint of the key.</param>
    /// <returns>The device, or <see langword="null"/> when the key is unknown or revoked.</returns>
    TrustedDevice? FindTrusted(DeviceFingerprint fingerprint);

    /// <summary>Gets whether a key was revoked: it is refused even where unknown keys are accepted, as in a pairing.</summary>
    /// <param name="fingerprint">The fingerprint of the key.</param>
    /// <returns>Whether the key is revoked.</returns>
    bool IsRevoked(DeviceFingerprint fingerprint);
}
