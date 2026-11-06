using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Client;

/// <summary>
/// How a device that pairs with a node from its link lets that node reach it too (<see cref="PairingPayload.BothWays"/>): where
/// the node reaches it, and its trust of the node, given before the secret is redeemed so that the first connection of the
/// node is accepted, and taken back when the node refuses the secret or pairs one way only.
/// </summary>
/// <param name="Addresses">Where the node reaches this device, as <c>host:port</c>; empty when it does not listen.</param>
/// <param name="Trust">Trusts the node, by its name and the key the channel is pinned to, and gives what it may do here and how to take it back.</param>
public sealed record FleetPairBackOffer(IReadOnlyList<string> Addresses, Func<string, DeviceFingerprint, FleetPairBackTrust> Trust);

/// <summary>The trust a device gave the node it pairs with both ways.</summary>
/// <param name="Permissions">What the node may do on this device (<c>view</c>, <c>drive</c>, <c>start</c>, <c>move</c>).</param>
/// <param name="Withdraw">Takes the trust back, leaving the device as it was before; called once at most.</param>
public sealed record FleetPairBackTrust(IReadOnlyList<string> Permissions, Action Withdraw);

/// <summary>A pairing made from a link.</summary>
/// <param name="Machine">The machine this device paired with.</param>
/// <param name="BothWays">Whether that machine reaches this device too: it recorded it as one of its machines.</param>
public sealed record FleetPairingResult(PairedMachine Machine, bool BothWays);
