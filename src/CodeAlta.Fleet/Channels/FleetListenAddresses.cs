using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Channels;

/// <summary>
/// The addresses a pairing link names for a listener of direct channels: those another computer can reach, this machine's
/// name first, then loopback for the owner's local control. A node (<c>altanode</c>) and a desktop that other computers
/// pair with describe their listener the same way.
/// </summary>
public static class FleetListenAddresses
{
    /// <summary>
    /// Describes where a listener is reached: loopback when it listens there; otherwise this machine's name and its addresses
    /// on the networks that are up, then loopback.
    /// </summary>
    /// <param name="endPoint">The end point the listener is bound to.</param>
    /// <returns>The addresses as <c>host:port</c>, valid for a pairing link, at most <see cref="PairingPayload.MaxAddresses"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endPoint"/> is null.</exception>
    public static IReadOnlyList<string> Describe(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        var port = endPoint.Port;
        var bound = endPoint.Address.IsIPv4MappedToIPv6 ? endPoint.Address.MapToIPv4() : endPoint.Address;
        if (IPAddress.IsLoopback(bound)) return [FleetDirectConnector.Format(bound, port)];
        var addresses = new List<string>();
        if (bound.Equals(IPAddress.Any) || bound.Equals(IPAddress.IPv6Any))
        {
            try
            {
                foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                    {
                        var address = unicast.Address;
                        if (address.AddressFamily == AddressFamily.InterNetwork
                            || address.AddressFamily == AddressFamily.InterNetworkV6 && bound.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal)
                            addresses.Add(FleetDirectConnector.Format(address, port));
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // Without the list of networks, the name of the machine stays.
            }

            var host = Dns.GetHostName();
            if (PairingPayload.IsValidAddress($"{host}:{port}")) addresses.Insert(0, $"{host}:{port}");
            addresses.Add(FleetDirectConnector.Format(IPAddress.Loopback, port));
        }
        else
        {
            addresses.Add(FleetDirectConnector.Format(bound, port));
        }

        return [.. addresses.Distinct(StringComparer.OrdinalIgnoreCase).Where(PairingPayload.IsValidAddress).Take(PairingPayload.MaxAddresses)];
    }
}
