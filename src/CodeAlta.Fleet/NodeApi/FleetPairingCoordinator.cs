using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// The pairing of a node: one open offer at a time (<see cref="PairingOfferService"/>), its redemption over a session
/// channel, and the device it adds to <see cref="TrustedDeviceStore"/>. Whoever created the offer waits for its outcome.
/// </summary>
public sealed class FleetPairingCoordinator
{
    /// <summary>What a device paired by redeeming an offer may do: view, drive and start sessions; never move them here.</summary>
    public const DevicePermissions DefaultPermissions = DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start;

    private readonly Lock _gate = new();
    private readonly PairingOfferService _offers;
    private readonly TrustedDeviceStore _trusted;
    private readonly TimeProvider _time;
    private TaskCompletionSource<FleetPairingOutcome>? _outcome;
    private CancellationTokenSource? _expiry;

    /// <summary>Initializes a new instance of the <see cref="FleetPairingCoordinator"/> class.</summary>
    /// <param name="identity">The key of the node, whose fingerprint the offers carry.</param>
    /// <param name="trusted">The devices the node trusts: a redeemed offer adds one.</param>
    /// <param name="time">The clock of the offers.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FleetPairingCoordinator(DeviceIdentity identity, TrustedDeviceStore trusted, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(time);
        _offers = new PairingOfferService(identity, time);
        _trusted = trusted;
        _time = time;
    }

    /// <summary>Gets whether an offer is open: unpaired clients may then open a channel to redeem it.</summary>
    public bool HasOpenOffer => _offers.HasOpenOffer;

    /// <summary>Opens an offer, replacing the one that was open (whose waiter is told <c>replaced</c>).</summary>
    /// <param name="nodeName">The name of the node in the offer.</param>
    /// <param name="addresses">The addresses the client connects to.</param>
    /// <param name="lifetime">How long the offer stays open.</param>
    /// <returns>The offer and the task of its outcome.</returns>
    /// <exception cref="ArgumentException">The name or the addresses are not valid for a pairing payload.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is out of the bounds of <see cref="PairingOfferService"/>.</exception>
    public (PairingPayload Offer, Task<FleetPairingOutcome> Outcome) CreateOffer(string nodeName, IReadOnlyList<string> addresses, TimeSpan lifetime)
    {
        var offer = _offers.CreateOffer(nodeName, addresses, lifetime);
        var outcome = new TaskCompletionSource<FleetPairingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiry = new CancellationTokenSource(lifetime, _time);
        TaskCompletionSource<FleetPairingOutcome>? replaced;
        CancellationTokenSource? previousExpiry;
        lock (_gate)
        {
            (replaced, previousExpiry) = (_outcome, _expiry);
            (_outcome, _expiry) = (outcome, expiry);
        }

        replaced?.TrySetResult(new FleetPairingOutcome("replaced"));
        previousExpiry?.Dispose();
        expiry.Token.Register(static state => ((TaskCompletionSource<FleetPairingOutcome>)state!).TrySetResult(new FleetPairingOutcome("expired")), outcome);
        return (offer, outcome.Task);
    }

    /// <summary>Gets the outcome of the offer that is open, or null when none is.</summary>
    /// <returns>The task of the outcome.</returns>
    public Task<FleetPairingOutcome>? CurrentOutcome()
    {
        lock (_gate)
        {
            return _outcome?.Task;
        }
    }

    /// <summary>Redeems the open offer for a device that proved its key over TLS, and trusts that device.</summary>
    /// <param name="secret">The secret the client gave.</param>
    /// <param name="deviceName">The name the client gives itself.</param>
    /// <param name="fingerprint">The fingerprint of the client's key, from TLS.</param>
    /// <returns>The trusted device, or null with why the offer refused.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceName"/> is not a valid device name.</exception>
    public (TrustedDevice? Device, PairingRedeemResult Result) Redeem(ReadOnlySpan<byte> secret, string deviceName, DeviceFingerprint fingerprint)
    {
        var name = TrustedDeviceStore.ValidateName(deviceName, nameof(deviceName));
        var result = _offers.Redeem(secret);
        if (result != PairingRedeemResult.Accepted) return (null, result);
        var device = _trusted.Add(name, fingerprint, DefaultPermissions);
        TaskCompletionSource<FleetPairingOutcome>? outcome;
        lock (_gate)
        {
            outcome = _outcome;
            _outcome = null;
            _expiry?.Dispose();
            _expiry = null;
        }

        outcome?.TrySetResult(new FleetPairingOutcome("redeemed", device.Name, device.DeviceId));
        return (device, result);
    }
}
