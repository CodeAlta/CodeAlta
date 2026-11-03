using System.Buffers.Text;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>Signs the requests agents make of other machines, with the key of the device of their person (<see cref="FleetAgentRequest"/>).</summary>
public static class FleetAgentSigning
{
    /// <summary>Signs a request with the key of this device, which it names as its signer, at the time given, with a new nonce.</summary>
    /// <param name="identity">The key of the device: the one its channels to other machines authenticate.</param>
    /// <param name="request">The request; its signer, key and nonce are set here.</param>
    /// <returns>The signed request.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The request does not hold.</exception>
    public static FleetSignedAgentRequest Sign(DeviceIdentity identity, FleetAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(request);
        var named = request with
        {
            SignerDeviceId = identity.DeviceId,
            SignerKey = Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo()),
            Nonce = FleetAgentRequestDocuments.CreateNonce(),
        };
        var text = FleetAgentRequestDocuments.Serialize(named);
        if (!FleetAgentRequestDocuments.TryRead(text, out _, out var reason)) throw new ArgumentException($"The agent request does not hold: {reason}.", nameof(request));
        return new FleetSignedAgentRequest(text, Base64Url.EncodeToString(identity.SignData(FleetAgentRequestDocuments.GetSignedBytes(text))));
    }
}
