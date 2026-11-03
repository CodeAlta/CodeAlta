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

    /// <summary>Give automations to this instance, which runs them by their triggers while the device is away.</summary>
    Automate = 16,

    /// <summary>
    /// Manage this instance as its owner does on its machine: what each paired device may do, revocations, the projects
    /// of its catalog, its status, its log and its updates. Given to the device that pairs first with a node that never
    /// had one, to the device a pairing link made for a manager (<c>altanode pair --manage</c>) adds, or by the owner.
    /// </summary>
    Manage = 32,
}

/// <summary>The names of <see cref="DevicePermissions"/> as people and the node API write them: <c>view</c>, <c>drive</c>, <c>start</c>, <c>move</c>, <c>automate</c>, <c>manage</c>.</summary>
public static class DevicePermissionNames
{
    /// <summary>Gets the names of the permissions, in their order.</summary>
    public static IReadOnlyList<string> All { get; } = ["view", "drive", "start", "move", "automate", "manage"];

    /// <summary>Names the permissions of a set, in their order.</summary>
    /// <param name="permissions">The set.</param>
    /// <returns>The names; empty for <see cref="DevicePermissions.None"/>.</returns>
    public static IReadOnlyList<string> Format(DevicePermissions permissions)
    {
        var names = new List<string>(6);
        if (permissions.HasFlag(DevicePermissions.View)) names.Add("view");
        if (permissions.HasFlag(DevicePermissions.Drive)) names.Add("drive");
        if (permissions.HasFlag(DevicePermissions.Start)) names.Add("start");
        if (permissions.HasFlag(DevicePermissions.Move)) names.Add("move");
        if (permissions.HasFlag(DevicePermissions.Automate)) names.Add("automate");
        if (permissions.HasFlag(DevicePermissions.Manage)) names.Add("manage");
        return names;
    }

    /// <summary>Reads names of permissions, separated by commas or given one by one, ignoring case.</summary>
    /// <param name="names">The names; each may hold several separated by commas.</param>
    /// <param name="permissions">The permissions they name.</param>
    /// <param name="unknown">The first name that is no permission, or null.</param>
    /// <returns>Whether every name is a permission.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> is null.</exception>
    public static bool TryParse(IEnumerable<string> names, out DevicePermissions permissions, out string? unknown)
    {
        ArgumentNullException.ThrowIfNull(names);
        permissions = DevicePermissions.None;
        unknown = null;
        foreach (var part in names.SelectMany(static name => (name ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            switch (part.ToLowerInvariant())
            {
                case "view": permissions |= DevicePermissions.View; break;
                case "drive": permissions |= DevicePermissions.Drive; break;
                case "start": permissions |= DevicePermissions.Start; break;
                case "move": permissions |= DevicePermissions.Move; break;
                case "automate": permissions |= DevicePermissions.Automate; break;
                case "manage": permissions |= DevicePermissions.Manage; break;
                default:
                    unknown = part;
                    permissions = DevicePermissions.None;
                    return false;
            }
        }

        return true;
    }
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
