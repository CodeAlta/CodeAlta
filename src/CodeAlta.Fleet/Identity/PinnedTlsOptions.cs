using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace CodeAlta.Fleet.Identity;

/// <summary>How a device accepts a connection as the TLS server.</summary>
public sealed class PinnedTlsServerOptions
{
    /// <summary>Gets the certificate of this device (<see cref="DeviceIdentity.CreateTlsCertificate"/>).</summary>
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>Gets the devices whose keys are accepted.</summary>
    public required IDeviceTrustPolicy TrustPolicy { get; init; }

    /// <summary>
    /// Gets whether a client whose key is unknown is accepted once it proved the key, for a pairing that checks its
    /// secret next. A revoked key is refused all the same. Default false.
    /// </summary>
    public bool AllowUnpairedClients { get; init; }

    /// <summary>Gets whether the transport stays open when the connection is disposed. Default false.</summary>
    public bool LeaveInnerStreamOpen { get; init; }
}

/// <summary>How a device connects as the TLS client.</summary>
public sealed class PinnedTlsClientOptions
{
    /// <summary>Gets the device that connects; it proves its key to the server.</summary>
    public required DeviceIdentity Identity { get; init; }

    /// <summary>Gets the fingerprint of the server's key, pinned from pairing or from the device list.</summary>
    public required DeviceFingerprint ServerFingerprint { get; init; }

    /// <summary>
    /// Gets the devices this one trusts, or <see langword="null"/> to rely on the pin alone (a pairing, whose pin comes
    /// from the pairing payload). When set, the server must be a trusted device that is not revoked.
    /// </summary>
    public IDeviceTrustPolicy? TrustPolicy { get; init; }

    /// <summary>Gets whether the transport stays open when the connection is disposed. Default false.</summary>
    public bool LeaveInnerStreamOpen { get; init; }
}

/// <summary>A TLS connection between two devices, with the key of the other side checked.</summary>
public sealed class PinnedTlsConnection : IDisposable, IAsyncDisposable
{
    internal PinnedTlsConnection(SslStream stream, DeviceFingerprint remoteFingerprint, TrustedDevice? remoteDevice)
    {
        Stream = stream;
        RemoteFingerprint = remoteFingerprint;
        RemoteDeviceId = DeviceIdentity.DeviceIdOf(remoteFingerprint);
        RemoteDevice = remoteDevice;
    }

    /// <summary>Gets the encrypted stream, for what the connection carries next.</summary>
    public SslStream Stream { get; }

    /// <summary>Gets the fingerprint of the other device's key.</summary>
    public DeviceFingerprint RemoteFingerprint { get; }

    /// <summary>Gets the device id of the other device.</summary>
    public string RemoteDeviceId { get; }

    /// <summary>
    /// Gets the other device as the trust policy knows it, or <see langword="null"/> when it is not paired yet (a client
    /// accepted for a pairing, or a server reached by its pin alone).
    /// </summary>
    public TrustedDevice? RemoteDevice { get; }

    /// <summary>Gets the TLS version of the connection.</summary>
    public SslProtocols Protocol => Stream.SslProtocol;

    /// <inheritdoc />
    public void Dispose() => Stream.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
