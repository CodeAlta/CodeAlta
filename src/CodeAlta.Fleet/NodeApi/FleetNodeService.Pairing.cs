using System.Buffers.Text;
using System.Security.Cryptography;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

public sealed partial class FleetNodeService
{
    private async Task<FleetPairRedeemResult> RedeemAsync(FleetNodeCall call, FleetPairRedeemParams parameters, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        if (call.Device is { } known)
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
            (TrustedDevice? Device, PairingRedeemResult Result) redeemed;
            try
            {
                redeemed = Pairing.Redeem(secret, parameters.Name ?? "", call.Caller.Fingerprint);
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

            LogInfo($"Device {device.Name} ({device.DeviceId}) paired with the node.");
            return new FleetPairRedeemResult(_options.Name, _options.Identity.DeviceId, Describe(device.Permissions));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private Task<FleetPairingOffer> CreatePairingOfferAsync(FleetNodeCall call, FleetPairingCreateParams parameters, CancellationToken cancellationToken)
    {
        var addresses = _options.PairingAddresses();
        if (addresses.Count == 0) throw new FleetException(FleetNodeErrorCodes.Unavailable, null, "The node does not listen: there is no address to pair with.");
        var lifetime = parameters.LifetimeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : PairingOfferService.DefaultLifetime;
        if (lifetime < TimeSpan.FromSeconds(10) || lifetime > PairingOfferService.MaxLifetime)
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "A pairing code lives from 10 seconds to 24 hours.");
        var (offer, _) = Pairing.CreateOffer(_options.Name, [.. addresses.Take(PairingPayload.MaxAddresses)], lifetime);
        LogInfo($"A pairing code was made, valid until {offer.ExpiresAt:O}.");
        return Task.FromResult(new FleetPairingOffer(offer.ToUri(), offer.ExpiresAt, offer.Addresses));
    }

    private async Task<FleetPairingOutcome> WaitPairingAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
    {
        var outcome = Pairing.CurrentOutcome() ?? throw new FleetException(FleetNodeErrorCodes.PairingRefused, null, "No pairing code is open.");
        return await outcome.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
