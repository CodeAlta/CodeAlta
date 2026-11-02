using System.Buffers;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;

namespace CodeAlta.Plugin.Mobile;

/// <summary>What a provider of the connection says of itself, as the tab and the settings page show it.</summary>
/// <param name="State">
/// <c>pending</c> while it publishes, <c>serving</c> once phones reach the view at <paramref name="Url"/>, <c>stopped</c>, or a state of
/// the provider that says what keeps phones out (Tailscale: <c>not-installed</c>, <c>serve-disabled</c>…; Dev Tunnels:
/// <c>not-signed-in</c>…). The pages find the words and the buttons of each state of each provider.
/// </param>
/// <param name="Url">The address phones open, while the view is served.</param>
/// <param name="Detail">A line for people: the words of the provider, or of the tool it uses.</param>
/// <param name="FixUrl">The page that fixes the state, when there is one.</param>
internal sealed record MobileProviderStatus(string State, string? Url, string Detail, string? FixUrl = null)
{
    /// <summary>A provider that publishes the view, with what it does now.</summary>
    internal static MobileProviderStatus Pending(string detail) => new("pending", null, detail);

    /// <summary>A provider that serves nothing: the plugin is off.</summary>
    internal static MobileProviderStatus Stopped { get; } = new("stopped", null, "The mobile view is not served.");
}

/// <summary>How the server of the view listens for a provider.</summary>
/// <param name="Port">The port of this computer.</param>
/// <param name="Tls">
/// What answers the TLS handshakes when the server serves HTTPS itself, on every address of this computer; null to serve HTTP on the
/// loopback address only, for a provider that publishes it (Tailscale Serve, a Dev Tunnel).
/// </param>
internal sealed record MobileListen(int Port, IMobileServerTls? Tls);

/// <summary>
/// The certificates of a server that serves HTTPS itself: the one of its name, and, while a check of Let's Encrypt runs, the one that
/// answers the challenge <c>tls-alpn-01</c> to the handshakes that ask for <c>acme-tls/1</c>.
/// </summary>
internal interface IMobileServerTls
{
    /// <summary>The certificate a handshake gets.</summary>
    /// <param name="serverName">The name the client asked for (SNI), or null.</param>
    /// <param name="acme">Whether the client asked for <c>acme-tls/1</c>: Let's Encrypt checks this computer.</param>
    /// <returns>The certificate, with its chain; null when there is none for that handshake (it fails).</returns>
    (X509Certificate2 Certificate, X509Certificate2Collection Chain)? Certificate(string? serverName, bool acme);
}

/// <summary>What the plugin gives a provider.</summary>
internal interface IMobileProviderHost
{
    /// <summary>Gets the settings of now.</summary>
    MobileSettings Settings { get; }

    /// <summary>Gets the folder the provider keeps its files in (a certificate, an account), readable by the user only.</summary>
    string Folder { get; }

    /// <summary>Changes and saves the settings: what the provider keeps between starts (a tunnel it made).</summary>
    Task SaveAsync(Func<MobileSettings, MobileSettings> change);

    /// <summary>Says a new status of the provider, outside of a publication: a host that stopped, a sign-in that runs.</summary>
    Task ReportAsync(MobileProviderStatus status);

    /// <summary>Publishes the view again, as Try again does: once a sign-in ended, for example.</summary>
    Task RepublishAsync();

    /// <summary>Runs work in the background of the plugin, stopped with it.</summary>
    void Run(string name, string description, Func<CancellationToken, ValueTask> work);

    /// <summary>Writes a line to the log of the plugin.</summary>
    void Log(string line);
}

/// <summary>
/// A way phones reach the view: Tailscale Serve, a Dev Tunnel, the Internet. The plugin knows only the provider of the settings: it
/// starts the server as the provider says, asks it to publish the view, and shows what it says.
/// </summary>
internal interface IMobileProvider : IAsyncDisposable
{
    /// <summary>Gets the id of the provider, as the settings keep it: <c>tailscale</c>, <c>devtunnel</c>, <c>internet</c>.</summary>
    string Id { get; }

    /// <summary>How the server listens for the settings.</summary>
    MobileListen Listen(MobileSettings settings);

    /// <summary>Why settings cannot work with this provider, or null: checked before they are saved.</summary>
    string? Refusal(MobileSettings settings);

    /// <summary>Publishes the view the server serves; called with the server listening as <see cref="Listen"/> says.</summary>
    /// <returns>What the provider does now.</returns>
    Task<MobileProviderStatus> PublishAsync(CancellationToken cancellationToken);

    /// <summary>Takes off what <see cref="PublishAsync"/> published, before the settings change or the plugin stops.</summary>
    Task UnpublishAsync(CancellationToken cancellationToken);

    /// <summary>Runs an action the tab offers for a state of the provider (<c>signin-github</c>…).</summary>
    /// <returns>Whether the provider knows the action.</returns>
    Task<bool> ActAsync(string action, CancellationToken cancellationToken);
}

/// <summary>Helpers of the TLS of a server that serves HTTPS itself.</summary>
internal static class MobileTls
{
    /// <summary>The ALPN protocol of the challenge <c>tls-alpn-01</c> of ACME (RFC 8737).</summary>
    internal const string AcmeProtocol = "acme-tls/1";

    /// <summary>The key, in the items of a connection, of whether its ClientHello asked for <see cref="AcmeProtocol"/>.</summary>
    internal const string AcmeItem = "codealta.mobile.acme";

    /// <summary>Notes in the connection whether its ClientHello asks for <see cref="AcmeProtocol"/>.</summary>
    internal static void Observe(ConnectionContext connection, ReadOnlySequence<byte> clientHello)
    {
        if (AsksAcme(clientHello.IsSingleSegment ? clientHello.FirstSpan : clientHello.ToArray())) connection.Items[AcmeItem] = true;
    }

    /// <summary>Whether a connection asked for <see cref="AcmeProtocol"/>.</summary>
    internal static bool IsAcme(ConnectionContext? connection) => connection?.Items.TryGetValue(AcmeItem, out var value) == true && value is true;

    /// <summary>
    /// Whether a TLS ClientHello (the record with its header) lists <see cref="AcmeProtocol"/> in its ALPN extension. A ClientHello that
    /// cannot be read asks for nothing.
    /// </summary>
    internal static bool AsksAcme(ReadOnlySpan<byte> record)
    {
        try
        {
            // Record: type 22, version (2), length (2); handshake: type 1, length (3), version (2), random (32).
            if (record.Length < 43 || record[0] != 22 || record[5] != 1) return false;
            var at = 43;
            at += 1 + record[at];                                    // session id
            at += 2 + (record[at] << 8 | record[at + 1]);           // cipher suites
            at += 1 + record[at];                                    // compression methods
            var end = at + 2 + (record[at] << 8 | record[at + 1]);  // extensions
            at += 2;
            while (at + 4 <= end && at + 4 <= record.Length)
            {
                var type = record[at] << 8 | record[at + 1];
                var length = record[at + 2] << 8 | record[at + 3];
                at += 4;
                if (type == 16)
                {
                    // ALPN: a list of length-prefixed names.
                    var listEnd = at + 2 + (record[at] << 8 | record[at + 1]);
                    for (var name = at + 2; name < listEnd && name < record.Length;)
                    {
                        var size = record[name];
                        if (name + 1 + size <= record.Length && record.Slice(name + 1, size).SequenceEqual("acme-tls/1"u8)) return true;
                        name += 1 + size;
                    }

                    return false;
                }

                at += length;
            }
        }
        catch (IndexOutOfRangeException)
        {
            // A ClientHello cut or malformed: it asks for nothing.
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        return false;
    }
}
