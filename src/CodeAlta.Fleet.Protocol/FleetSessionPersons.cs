using System.Text;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// The person who started a session on a machine of a team or of the organization, as the machine signs it
/// (<c>doc/fleet/access-control.md</c>, access and keys): the server relays the assertion with the facts of the session and with
/// the keys of a grant of its content, and a device that opens such a grant verifies it with the key of the machine in the device
/// list it verified. A server, or another person of the team, cannot make a session someone else's.
/// </summary>
public static class FleetSessionPersons
{
    private const string Domain = "codealta-session-person/1";

    /// <summary>Gets the bytes a machine signs to say who started one of its sessions.</summary>
    /// <param name="organizationId">The organization of the server.</param>
    /// <param name="machineId">The machine of the session.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="personId">The person who started it there.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static byte[] GetSignedBytes(string organizationId, string machineId, string sessionId, string personId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(machineId);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(personId);
        return Encoding.UTF8.GetBytes($"{Domain}\n{organizationId}\n{machineId}\n{sessionId}\n{personId}");
    }

    /// <summary>Gets whether a machine signed that a person started one of its sessions.</summary>
    /// <param name="machineKey">The key of the machine, from the device list the verifier holds.</param>
    /// <param name="organizationId">The organization of the server.</param>
    /// <param name="machineId">The machine of the session.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="personId">The person.</param>
    /// <param name="signature">The signature, base64url; null for none.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="machineKey"/> is null.</exception>
    public static bool Verify(FleetDevicePublicKey machineKey, string organizationId, string machineId, string sessionId, string? personId, string? signature)
    {
        ArgumentNullException.ThrowIfNull(machineKey);
        return personId is not null && !string.IsNullOrEmpty(signature) && machineKey.Verify(GetSignedBytes(organizationId, machineId, sessionId, personId), signature);
    }
}
