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
    // Whether the open offer makes the device that redeems it a manager of the node (altanode pair --manage).
    private bool _offerManages;
    // Whether the open offer said that the node reaches the device that redeems it too (PairingPayload.BothWays).
    private bool _offerBothWays;

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
        => CreateOffer(nodeName, addresses, lifetime, manages: false);

    /// <summary>
    /// Opens an offer, replacing the one that was open (whose waiter is told <c>replaced</c>); an offer that
    /// <paramref name="manages"/> gives the device that redeems it <see cref="DevicePermissions.Manage"/> too.
    /// </summary>
    /// <param name="nodeName">The name of the node in the offer.</param>
    /// <param name="addresses">The addresses the client connects to.</param>
    /// <param name="lifetime">How long the offer stays open.</param>
    /// <param name="manages">Whether the device that redeems it manages the node, as its owner asked on its machine.</param>
    /// <returns>The offer and the task of its outcome.</returns>
    /// <exception cref="ArgumentException">The name or the addresses are not valid for a pairing payload.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is out of the bounds of <see cref="PairingOfferService"/>.</exception>
    public (PairingPayload Offer, Task<FleetPairingOutcome> Outcome) CreateOffer(string nodeName, IReadOnlyList<string> addresses, TimeSpan lifetime, bool manages)
        => CreateOffer(nodeName, addresses, lifetime, manages, bothWays: false);

    /// <summary>
    /// Opens an offer, replacing the one that was open (whose waiter is told <c>replaced</c>); an offer made
    /// <paramref name="bothWays"/> says that the node reaches the device that redeems it too, when that device asks
    /// (<see cref="PairingPayload.BothWays"/>).
    /// </summary>
    /// <param name="nodeName">The name of the node in the offer.</param>
    /// <param name="addresses">The addresses the client connects to.</param>
    /// <param name="lifetime">How long the offer stays open.</param>
    /// <param name="manages">Whether the device that redeems it manages the node, as its owner asked on its machine.</param>
    /// <param name="bothWays">Whether the node offers to reach the device that redeems it too.</param>
    /// <returns>The offer and the task of its outcome.</returns>
    /// <exception cref="ArgumentException">The name or the addresses are not valid for a pairing payload.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is out of the bounds of <see cref="PairingOfferService"/>.</exception>
    public (PairingPayload Offer, Task<FleetPairingOutcome> Outcome) CreateOffer(string nodeName, IReadOnlyList<string> addresses, TimeSpan lifetime, bool manages,
        bool bothWays)
    {
        var outcome = new TaskCompletionSource<FleetPairingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiry = new CancellationTokenSource(lifetime, _time);
        PairingPayload offer;
        TaskCompletionSource<FleetPairingOutcome>? replaced;
        CancellationTokenSource? previousExpiry;
        // The offer and what it grants change together: a redemption never sees one with the grant of the other.
        lock (_gate)
        {
            try
            {
                offer = _offers.CreateOffer(nodeName, addresses, lifetime, bothWays);
            }
            catch
            {
                expiry.Dispose();
                throw;
            }

            (replaced, previousExpiry) = (_outcome, _expiry);
            (_outcome, _expiry, _offerManages, _offerBothWays) = (outcome, expiry, manages, bothWays);
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

    /// <summary>
    /// Redeems the open offer for a device that proved its key over TLS, and trusts that device with
    /// <see cref="DefaultPermissions"/>, and <see cref="DevicePermissions.Manage"/> only when the offer was made for a manager
    /// (<c>altanode pair --manage</c>, which the owner asks for) or the device managed the node already: the first device of a
    /// node gets it no more than the next ones, so a link seen by someone else never hands over the node's devices,
    /// secrets and updates.
    /// </summary>
    /// <param name="secret">The secret the client gave.</param>
    /// <param name="deviceName">The name the client gives itself.</param>
    /// <param name="fingerprint">The fingerprint of the client's key, from TLS.</param>
    /// <returns>The trusted device, or null with why the offer refused.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceName"/> is not a valid device name.</exception>
    public (TrustedDevice? Device, PairingRedeemResult Result) Redeem(ReadOnlySpan<byte> secret, string deviceName, DeviceFingerprint fingerprint)
    {
        var redemption = Begin(secret, deviceName, fingerprint, known: null);
        redemption.Complete(bothWays: false);
        return (redemption.Device, redemption.Result);
    }

    /// <summary>
    /// Redeems the open offer as <see cref="Redeem"/> does, and holds its outcome until <see cref="FleetPairingRedemption.Complete"/>
    /// says whether the node reaches the device too: the device asked to pair both ways. A device the node trusts already
    /// (<paramref name="known"/>) keeps its trust as it is; only the offer is spent.
    /// </summary>
    /// <param name="secret">The secret the client gave.</param>
    /// <param name="deviceName">The name the client gives itself.</param>
    /// <param name="fingerprint">The fingerprint of the client's key, from TLS.</param>
    /// <param name="known">The device the node trusts with that key already; null for a device it does not.</param>
    /// <returns>The redemption, whose outcome waits for <see cref="FleetPairingRedemption.Complete"/> once it is accepted.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceName"/> is not a valid device name.</exception>
    public FleetPairingRedemption Begin(ReadOnlySpan<byte> secret, string deviceName, DeviceFingerprint fingerprint, TrustedDevice? known)
    {
        var name = TrustedDeviceStore.ValidateName(deviceName, nameof(deviceName));
        PairingRedeemResult result;
        bool manages, bothWays;
        lock (_gate)
        {
            result = _offers.Redeem(secret);
            (manages, bothWays) = (_offerManages, _offerBothWays);
        }

        if (result != PairingRedeemResult.Accepted) return new FleetPairingRedemption(null, result, false, null);
        TrustedDevice device;
        if (known is not null) device = known;
        else
        {
            // A device paired again keeps the manage permission it had; otherwise only an offer made to manage gives it.
            var previous = _trusted.List(includeRevoked: true).FirstOrDefault(device => device.Fingerprint == fingerprint && !device.IsRevoked);
            if (previous?.Permissions.HasFlag(DevicePermissions.Manage) == true) manages = true;
            device = _trusted.Add(name, fingerprint, manages ? DefaultPermissions | DevicePermissions.Manage : DefaultPermissions);
        }

        TaskCompletionSource<FleetPairingOutcome>? outcome;
        lock (_gate)
        {
            outcome = _outcome;
            _outcome = null;
            _expiry?.Dispose();
            _expiry = null;
        }

        return new FleetPairingRedemption(device, result, bothWays, outcome);
    }
}

/// <summary>An offer redeemed by <see cref="FleetPairingCoordinator.Begin"/>: the device it trusts, and its outcome, held until completed.</summary>
public sealed class FleetPairingRedemption
{
    private TaskCompletionSource<FleetPairingOutcome>? _outcome;

    internal FleetPairingRedemption(TrustedDevice? device, PairingRedeemResult result, bool offeredBothWays, TaskCompletionSource<FleetPairingOutcome>? outcome)
    {
        Device = device;
        Result = result;
        OfferedBothWays = offeredBothWays;
        _outcome = outcome;
    }

    /// <summary>Gets the device the node trusts now; null when the offer refused the secret.</summary>
    public TrustedDevice? Device { get; }

    /// <summary>Gets whether the offer accepted the secret, or why it refused it.</summary>
    public PairingRedeemResult Result { get; }

    /// <summary>Gets whether the offer said that the node reaches the device that redeems it too (<see cref="PairingPayload.BothWays"/>).</summary>
    public bool OfferedBothWays { get; }

    /// <summary>Tells whoever created the offer that it was redeemed, and whether the node reaches the device too; once.</summary>
    /// <param name="bothWays">Whether the node recorded the device as a machine it reaches.</param>
    public void Complete(bool bothWays)
    {
        if (Interlocked.Exchange(ref _outcome, null) is not { } outcome || Device is not { } device) return;
        outcome.TrySetResult(new FleetPairingOutcome("redeemed", device.Name, device.DeviceId, bothWays));
    }
}
