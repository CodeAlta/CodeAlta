using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The approval a machine asks for a run that uses an environment (<c>doc/fleet/secrets.md</c>, environments protect
/// themselves), authenticated by the machine: the nonce it chooses is a random part and its signature of what it asks (the
/// organization, the environment and the version of its rules, the machine, the run and its automation). The server keeps and
/// relays the nonce as it does any; an approver's device checks it against its anchored device list before it signs anything,
/// so that a request the server made up, or changed (another run, another automation), is never approved. The approval is
/// bound to that nonce, so the machine takes only the approval of what it asked.
/// </summary>
public static class FleetApprovalRequests
{
    private const string Domain = "codealta-approval-request/1";
    private const int RandomLength = 16;

    /// <summary>Makes the nonce of a request of this machine: a random part and its signature of the request.</summary>
    /// <param name="machine">The key of this machine, which asks.</param>
    /// <param name="organizationId">The organization.</param>
    /// <param name="environmentId">The environment.</param>
    /// <param name="environmentVersion">The version of its rules the machine verified.</param>
    /// <param name="runId">The run.</param>
    /// <param name="automationId">Its automation; null for a session.</param>
    /// <returns>The nonce, at most 128 characters.</returns>
    /// <exception cref="ArgumentNullException">An argument but <paramref name="automationId"/> is null.</exception>
    public static string Nonce(DeviceIdentity machine, string organizationId, string environmentId, long environmentVersion, string runId, string? automationId)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(environmentId);
        ArgumentNullException.ThrowIfNull(runId);
        var random = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomLength));
        var signature = machine.SignData(Signed(organizationId, environmentId, environmentVersion, machine.DeviceId, runId, automationId, random));
        return random + "." + Base64Url.EncodeToString(signature);
    }

    /// <summary>
    /// Tells whether an approval asked is what its machine asked: its nonce carries the signature of that machine, a device of
    /// the list that the list did not remove, of this organization, environment and version, run and automation.
    /// </summary>
    /// <param name="asked">The approval as the server lists it.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <param name="reason">Why it is not, or null.</param>
    /// <returns>Whether it is.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool IsAskedBy(FleetApprovalDto asked, FleetDeviceListState list, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(list);
        if (list.OrganizationId is not { } organization)
        {
            reason = "the device list names no organization";
            return false;
        }

        if (!list.Members.TryGetValue(asked.MachineId, out var machine) || list.Removed.Contains(asked.MachineId) || machine.Kind == FleetDeviceKinds.Recovery)
        {
            reason = "the machine is not a device of the list";
            return false;
        }

        var dot = asked.Nonce.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || !machine.Key.Verify(Signed(organization, asked.EnvironmentId, asked.EnvironmentVersion, asked.MachineId, asked.RunId, asked.AutomationId, asked.Nonce[..dot]),
                asked.Nonce[(dot + 1)..]))
        {
            reason = "the machine did not sign this request: the server made it up or changed it";
            return false;
        }

        reason = null;
        return true;
    }

    private static byte[] Signed(string organizationId, string environmentId, long environmentVersion, string machineId, string runId, string? automationId, string random)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{Domain}\n{organizationId}\n{environmentId}\n{environmentVersion}\n{machineId}\n{runId}\n{automationId}\n{random}"));
}
