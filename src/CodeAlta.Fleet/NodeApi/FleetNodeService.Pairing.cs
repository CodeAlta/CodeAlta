using System.Buffers.Text;
using System.Security.Cryptography;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

public sealed partial class FleetNodeService
{
    private async Task<FleetPairRedeemResult> RedeemAsync(FleetNodeCall call, FleetPairRedeemParams parameters, CancellationToken cancellationToken)
    {
        if (call.Device is { } known && !parameters.BothWays)
        {
            // Already paired: redeeming again changes nothing, and spends no offer.
            return new FleetPairRedeemResult(_options.Name, _options.Identity.DeviceId, Describe(known.Permissions));
        }

        byte[] secret;
        try
        {
            secret = Base64Url.DecodeFromChars(parameters.Secret ?? "");
        }
        catch (FormatException)
        {
            throw new FleetException(FleetNodeErrorCodes.PairingRefused, null, "The pairing secret is not valid.");
        }

        try
        {
            // A device paired already that asks for both ways spends the offer as a new one does, and keeps what it may do here.
            FleetPairingRedemption redeemed;
            try
            {
                redeemed = Pairing.Begin(secret, parameters.Name ?? "", call.Caller.Fingerprint, call.Device);
            }
            catch (ArgumentException exception)
            {
                throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message);
            }

            if (redeemed.Device is not { } device)
            {
                var why = redeemed.Result switch
                {
                    PairingRedeemResult.Expired => "The pairing code has expired.",
                    PairingRedeemResult.TooManyAttempts => "The pairing code was tried too many times: make a new one.",
                    PairingRedeemResult.NoOffer => "The node has no open pairing code.",
                    _ => "The pairing code is not the one the node offers.",
                };
                LogInfo($"A pairing attempt from {call.Caller.DeviceId} was refused: {redeemed.Result}.");
                throw new FleetException(FleetNodeErrorCodes.PairingRefused, null, why);
            }

            var bothWays = false;
            try
            {
                bothWays = parameters.BothWays && redeemed.OfferedBothWays && await PairBackAsync(device, parameters, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                redeemed.Complete(bothWays);
            }

            LogInfo($"Device {device.Name} ({device.DeviceId}) paired with the node{(bothWays ? " both ways" : "")}.");
            return new FleetPairRedeemResult(_options.Name, _options.Identity.DeviceId, Describe(device.Permissions)) { BothWays = bothWays };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    // The device trusts this node already, over the channel pinned to the key of the link: the node records it as a machine
    // it reaches, with what it says of itself. A failure leaves the pairing one way, which the device is told.
    private async Task<bool> PairBackAsync(TrustedDevice device, FleetPairRedeemParams parameters, CancellationToken cancellationToken)
    {
        if (_options.PairBack is not { } pairBack) return false;
        IReadOnlyList<string> addresses = [.. (parameters.Addresses ?? []).Where(PairingPayload.IsValidAddress).Distinct(StringComparer.OrdinalIgnoreCase).Take(PairingPayload.MaxAddresses)];
        var permissions = DevicePermissionNames.TryParse(parameters.Permissions ?? [], out var granted, out _) ? DevicePermissionNames.Format(granted) : [];
        try
        {
            return await pairBack(new FleetPairBackRequest(device.Name, device.Fingerprint, addresses, permissions), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or TimeoutException or ArgumentException)
        {
            LogWarn($"Device {device.Name} ({device.DeviceId}) paired one way only: it could not be recorded as a machine here ({exception.Message}).");
            return false;
        }
    }

    private Task<FleetPairingOffer> CreatePairingOfferAsync(FleetNodeCall call, FleetPairingCreateParams parameters, CancellationToken cancellationToken)
    {
        var addresses = _options.PairingAddresses();
        if (addresses.Count == 0) throw new FleetException(FleetNodeErrorCodes.Unavailable, null, "The node does not listen: there is no address to pair with.");
        var lifetime = parameters.LifetimeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : PairingOfferService.DefaultLifetime;
        if (lifetime < TimeSpan.FromSeconds(10) || lifetime > PairingOfferService.MaxLifetime)
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "A pairing code lives from 10 seconds to 24 hours.");
        var (offer, _) = Pairing.CreateOffer(_options.Name, [.. addresses.Take(PairingPayload.MaxAddresses)], lifetime, parameters.Manage);
        LogInfo($"A pairing code{(parameters.Manage ? " for a device that manages the node" : "")} was made, valid until {offer.ExpiresAt:O}.");
        return Task.FromResult(new FleetPairingOffer(offer.ToUri(), offer.ExpiresAt, offer.Addresses));
    }

    private async Task<FleetPairingOutcome> WaitPairingAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
    {
        var outcome = Pairing.CurrentOutcome() ?? throw new FleetException(FleetNodeErrorCodes.PairingRefused, null, "No pairing code is open.");
        return await outcome.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
